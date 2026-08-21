using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageVisuals.Weather
{
    /// <summary>
    /// Reads the game's own cloud placement, so shadows land under the clouds
    /// that cast them.
    ///
    /// Three attempts at a noise field of our own all failed the same way, and
    /// the last one failed for a reason no amount of tuning fixes: an invented
    /// field cannot line up with clouds it knows nothing about. Cloud shape in
    /// BOTH renderers comes from a per-tile array the CPU builds and uploads as
    /// mapData1/mapData2 (see cloudmap.fsh); nothing a shader can reach decides
    /// where a cloud is. So the array is where this has to read from.
    ///
    /// That array lives in VintagestoryLib, which is explicitly not a stable
    /// API, so every step here is discovered by SHAPE rather than by name where
    /// that is possible - an array field whose elements are called something
    /// like CloudTile, a numeric member on that type that looks like thickness
    /// - and every failure is reported once with enough detail to fix it in one
    /// round rather than guessed at again. If any step fails, Available stays
    /// false and the shader falls back to the noise field, which is worse but
    /// is not nothing.
    /// </summary>
    public sealed class CloudTileReader
    {
        /// <summary>
        /// Tiles across the window handed to the shader.
        ///
        /// 16 tiles of 50 blocks is 800 blocks, a little more than the default
        /// view distance. It is deliberately small: the window ships as a
        /// uniform ARRAY rather than a texture, which costs 64 vec4s and needs
        /// no sampler at all. Adding a second sampler to chunkopaque.fsh is the
        /// change that has twice cost this project the whole world render, and
        /// it is not worth it for 256 numbers.
        /// </summary>
        public const int Window = 16;

        /// <summary>Blocks per cloud tile. Hard-coded in the game's own shaders as cloudTileSize.</summary>
        public const float TileSize = 50f;

        private readonly ICoreClientAPI _capi;
        private readonly ILogger _logger;

        private readonly float[] _density = new float[Window * Window];

        // Raw members are kept separately because they have to be normalised
        // separately before vanilla's formula can be applied to them. See
        // ReadWindow.
        private readonly float[] _thickness = new float[Window * Window];
        private readonly float[] _opaqueness = new float[Window * Window];

        private readonly Vec2f _origin = new Vec2f();

        private object _renderer;
        private FieldInfo _tilesField;
        private MemberInfo _thicknessMember;
        private MemberInfo _opaqueMember;
        private int _side;

        private bool _searched;
        private bool _reported;
        private float _thicknessPeak = 1f;
        private float _opaquenessPeak = 1f;

        public CloudTileReader(ICoreClientAPI capi, ILogger logger)
        {
            _capi = capi;
            _logger = logger;
        }

        /// <summary>True when Density and Origin describe the game's real clouds.</summary>
        public bool Available { get; private set; }

        /// <summary>
        /// Window * Window tile densities, 0 clear to 1 solid, row-major with x
        /// fastest. Packed four to a vec4 by the binder.
        /// </summary>
        public float[] Density { get { return _density; } }

        /// <summary>
        /// CAMERA-RELATIVE XZ of the corner of tile [0,0].
        ///
        /// Not world XZ, and the difference is the whole reason cloud shadows
        /// never appeared. The shader has only camera-relative coordinates and
        /// a camera position wrapped to 4096 blocks, because a float32 cannot
        /// resolve a Vintage Story world coordinate - so a corner in true world
        /// coordinates could not be compared against anything the shader had.
        /// Handing it over camera-relative keeps both sides small and removes
        /// the wrap from the question entirely.
        /// </summary>
        public Vec2f Origin { get { return _origin; } }

        /// <summary>
        /// Re-reads the window. Cheap enough for a few times a second: 256
        /// reflected member reads, no allocation beyond the boxing the reflection
        /// itself forces.
        /// </summary>
        public void Update()
        {
            if (!_searched)
            {
                _searched = true;
                Discover();
            }

            if (_renderer == null || _tilesField == null || _thicknessMember == null)
            {
                Available = false;
                return;
            }

            try
            {
                Array tiles = _tilesField.GetValue(_renderer) as Array;
                if (tiles == null || tiles.Length < Window * Window)
                {
                    Available = false;
                    return;
                }

                int side = (int)Math.Round(Math.Sqrt(tiles.Length));
                if (side * side != tiles.Length || side < Window)
                {
                    ReportOnce("the cloud tile array is " + tiles.Length + " entries, which is not a square " +
                               "grid of at least " + Window + " a side, so the window cannot be placed in it");
                    Available = false;
                    return;
                }

                _side = side;
                ReadWindow(tiles);
                Available = true;
                ReportFieldOnce();
            }
            catch (Exception ex)
            {
                ReportOnce("reading the cloud tile array threw: " + ex.Message);
                Available = false;
            }
        }

        /// <summary>
        /// Copies the centre of the game's grid into the window.
        ///
        /// The game's grid follows the camera - cloudmap.fsh places tile (i, j)
        /// at mapOffset + (i - width/2) * 50, and mapOffset tracks the player -
        /// so the centre of the array is where the player is, and no origin
        /// field has to be found. The window's world corner follows from that.
        /// </summary>
        private void ReadWindow(Array tiles)
        {
            int first = (_side - Window) / 2;

            float thickestSeen = 0f;
            float mostOpaqueSeen = 0f;

            for (int z = 0; z < Window; z++)
            {
                for (int x = 0; x < Window; x++)
                {
                    object tile = tiles.GetValue((first + z) * _side + first + x);
                    int i = z * Window + x;

                    float thickness = tile == null ? 0f : Math.Max(0f, Numeric(_thicknessMember, tile));
                    float opaqueness = tile == null || _opaqueMember == null
                        ? 1f
                        : Math.Max(0f, Numeric(_opaqueMember, tile));

                    _thickness[i] = thickness;
                    _opaqueness[i] = opaqueness;

                    if (thickness > thickestSeen) thickestSeen = thickness;
                    if (opaqueness > mostOpaqueSeen) mostOpaqueSeen = opaqueness;
                }
            }

            // The tile fields are whatever scale the game happens to use, and
            // guessing a divisor would be one more thing to be wrong about. A
            // decaying peak normalises without needing to know: it settles on
            // the busiest sky seen recently and lets an emptying sky fade
            // rather than rescaling itself brighter.
            //
            // Per member, not on the product, and that is the whole reason this
            // was restructured. Vanilla's opacity is
            //
            //     min(1, cloudOpaqueness * min(1, 10 * selfThickness))
            //
            // and the inner min SATURATES: a tile of thickness 0.1 or more is a
            // full cloud, which is why the game's sky is mostly solid tiles with
            // sharp edges. That threshold is only meaningful against a 0..1
            // thickness, so it cannot be applied before normalising, and the
            // normaliser cannot run on a product that has already been through
            // it. Applied in the wrong order it is either a no-op or it turns
            // the whole field binary.
            _thicknessPeak = Math.Max(thickestSeen, _thicknessPeak * 0.995f);
            _opaquenessPeak = Math.Max(mostOpaqueSeen, _opaquenessPeak * 0.995f);

            if (_thicknessPeak < 1e-4f) _thicknessPeak = 1e-4f;
            if (_opaquenessPeak < 1e-4f) _opaquenessPeak = 1e-4f;

            // A sky with nothing in it is zero everywhere, said explicitly. The
            // decaying peak would otherwise divide the dregs of an empty sky up
            // into shadows that are not there - the floor above is small enough
            // that a thickness of 1e-5 would read as a tenth of a cloud.
            bool clear = thickestSeen < 1e-4f;

            for (int i = 0; i < _density.Length; i++)
            {
                if (clear)
                {
                    _density[i] = 0f;
                    continue;
                }

                float covered = Math.Min(1f, 10f * (_thickness[i] / _thicknessPeak));
                float opaque = GameMath.Clamp(_opaqueness[i] / _opaquenessPeak, 0f, 1f);

                _density[i] = GameMath.Clamp(opaque * covered, 0f, 1f);
            }

            IClientPlayer player = _capi.World?.Player;
            if (player?.Entity == null) return;

            // Snapped to the tile grid, because the game's own grid is: a
            // window that slid smoothly over a grid that jumps would make the
            // shadows crawl by up to a tile every time the player crossed one.
            //
            // Expressed relative to the CAMERA, and computed so that the
            // difference is taken in double before it ever becomes a float. The
            // snap offset is the fractional part of the player's position
            // within its tile, which lives in [-50, 0]; adding the half-window
            // puts the corner around -400. Both are numbers a float32 has
            // precision to spare on, at any distance from the world origin.
            //
            // The previous version subtracted nothing and handed over
            // floor(px/50)*50 - 400 as an absolute world coordinate, which the
            // shader then compared against a wrapped position. See Origin.
            double px = player.Entity.Pos.X;
            double pz = player.Entity.Pos.Z;

            double snapX = Math.Floor(px / TileSize) * TileSize - px;
            double snapZ = Math.Floor(pz / TileSize) * TileSize - pz;

            _origin.X = (float)(snapX - Window / 2 * TileSize);
            _origin.Y = (float)(snapZ - Window / 2 * TileSize);
        }

        private static float Numeric(MemberInfo member, object target)
        {
            object value = member is FieldInfo field
                ? field.GetValue(target)
                : ((PropertyInfo)member).GetValue(target);

            return value == null ? 0f : Convert.ToSingle(value);
        }

        // -------------------------------------------------------------------
        // Discovery
        // -------------------------------------------------------------------

        private void Discover()
        {
            _renderer = FindCloudRenderer();
            if (_renderer == null)
            {
                ReportOnce("no CloudRenderer could be found on the client, so cloud shadows fall back to " +
                           "the mod's own noise field and will not line up with the sky");
                return;
            }

            Type type = _renderer.GetType();

            _tilesField = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(f => f.FieldType.IsArray &&
                                     f.FieldType.GetElementType() != null &&
                                     f.FieldType.GetElementType().Name.IndexOf("CloudTile",
                                         StringComparison.OrdinalIgnoreCase) >= 0);

            if (_tilesField == null)
            {
                ReportOnce("found " + type.FullName + " but no array of cloud tiles on it. Array fields: " +
                           Describe(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                        .Where(f => f.FieldType.IsArray)
                                        .Select(f => f.Name + ":" + f.FieldType.Name)));
                return;
            }

            Type tile = _tilesField.FieldType.GetElementType();

            _thicknessMember = PickMember(tile, "SelfThickness", "Thickness", "MaxThickness");
            _opaqueMember = PickMember(tile, "CloudOpaqueness", "Opaqueness", "Opacity");

            if (_thicknessMember == null)
            {
                ReportOnce("found " + tile.Name + " but nothing on it that looks like thickness. Members: " +
                           Describe(NumericMembers(tile).Select(m => m.Name)));
                return;
            }

            _logger.Notification("[VintageVisuals] weather: reading cloud placement from " + type.Name + "." +
                _tilesField.Name + " (" + tile.Name + "." + _thicknessMember.Name +
                (_opaqueMember == null ? "" : " x " + _opaqueMember.Name) + "). Cloud shadows will follow " +
                "the game's own clouds.");

            ReportRegistrationCandidates(type);
        }

        /// <summary>
        /// Walks the client for a cloud renderer.
        ///
        /// By type name rather than by field name, and through collections as
        /// well as fields, because the one thing that can be relied on across
        /// versions is roughly what the class is called - not where it is
        /// stored or whether it is held directly at all.
        /// </summary>
        /// <summary>
        /// Logs everything on the cloud renderer that could say WHERE its tile
        /// array is.
        ///
        /// This is the one question about cloud shadows that has never been
        /// answered, and it cannot be answered from outside a running game. The
        /// window's corner is currently assumed to be the player's position
        /// snapped down to the 50-block tile grid. That assumption is not
        /// obviously right and is certainly incomplete: the game's clouds drift
        /// with the wind, which means the CPU is moving the tile data or its
        /// offsets over time, and a corner derived purely from the player's
        /// position knows nothing about that drift.
        ///
        /// clouds.vsh places instance i at a CAMERA-RELATIVE position it is
        /// handed directly (vertexPosition + 32767 * cloudTileOffset), so the
        /// renderer already knows the answer exactly. It is only a question of
        /// which member holds it.
        ///
        /// So rather than guess again, this prints the candidates with their
        /// live values: anything vector-shaped, and any number whose name reads
        /// like a position, an offset or a counter. One run of the game with
        /// this in the log is worth more than another round of tuning.
        /// </summary>
        private void ReportRegistrationCandidates(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var found = new List<string>();

            foreach (FieldInfo field in type.GetFields(flags))
            {
                string name = field.Name;
                Type held = field.FieldType;

                bool vectorish = held.Name.StartsWith("Vec", StringComparison.Ordinal);
                bool namedLikePlacement =
                    name.IndexOf("offset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("origin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("pos", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("wind", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("counter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("tile", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("size", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("quantity", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!vectorish && !(namedLikePlacement && IsNumeric(held))) continue;

                string value;
                try { value = Convert.ToString(field.GetValue(_renderer)); }
                catch (Exception) { continue; }

                found.Add(name + "(" + held.Name + ")=" + value);
            }

            if (found.Count == 0)
            {
                ReportOnce("nothing on " + type.Name + " looks like it says where the tile array sits, so " +
                           "the window's corner stays a guess - the player's position snapped to the tile grid");
                return;
            }

            _logger.Notification("[VintageVisuals] weather: cloud renderer placement candidates - " +
                Describe(found) + ". The window corner is currently ASSUMED to be the player snapped down to " +
                "the 50-block grid, which cannot account for the clouds drifting on the wind. Use cloud " +
                "diagnostic view 2 to see whether that assumption holds.");
        }

        private object FindCloudRenderer()
        {
            object client = _capi.World;
            if (client == null) return null;

            foreach (FieldInfo field in client.GetType()
                         .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object value;
                try { value = field.GetValue(client); }
                catch (Exception) { continue; }

                if (value == null) continue;

                if (IsCloudRenderer(value)) return value;

                if (value is IEnumerable items && !(value is string))
                {
                    try
                    {
                        foreach (object item in items)
                        {
                            if (item != null && IsCloudRenderer(item)) return item;
                        }
                    }
                    catch (Exception)
                    {
                        // Some collections here are live and throw if enumerated
                        // off their own thread. Not finding the renderer in one
                        // of them is not a reason to stop looking in the rest.
                    }
                }
            }

            return null;
        }

        private static bool IsCloudRenderer(object value)
        {
            return value.GetType().Name.IndexOf("CloudRenderer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static MemberInfo PickMember(Type type, params string[] names)
        {
            MemberInfo[] members = NumericMembers(type).ToArray();

            foreach (string name in names)
            {
                MemberInfo exact = members.FirstOrDefault(
                    m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }

            // Nothing matched exactly, so fall back to anything containing the
            // first name - a version that renamed SelfThickness to
            // CloudSelfThickness should still work.
            return members.FirstOrDefault(
                m => m.Name.IndexOf(names[0], StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static System.Collections.Generic.IEnumerable<MemberInfo> NumericMembers(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (IsNumeric(field.FieldType)) yield return field;
            }

            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (property.CanRead && property.GetIndexParameters().Length == 0 &&
                    IsNumeric(property.PropertyType))
                {
                    yield return property;
                }
            }
        }

        private static bool IsNumeric(Type type)
        {
            return type == typeof(float) || type == typeof(double) || type == typeof(int) ||
                   type == typeof(short) || type == typeof(ushort) || type == typeof(byte) ||
                   type == typeof(sbyte) || type == typeof(long) || type == typeof(uint);
        }

        private static string Describe(IEnumerable<string> names)
        {
            var text = new StringBuilder();
            int count = 0;

            foreach (string name in names)
            {
                if (count++ > 0) text.Append(", ");
                if (count > 24) { text.Append("..."); break; }
                text.Append(name);
            }

            return count == 0 ? "(none)" : text.ToString();
        }

        private bool _reportedField;

        /// <summary>
        /// Says what the cloud field actually looks like, once.
        ///
        /// Cloud shadows have now failed three times, and every round was spent
        /// guessing from a screenshot whether the field was empty, saturated or
        /// simply somewhere else. Four numbers in the log answer that without a
        /// screenshot: a mean near zero means the sky is being read as clear, a
        /// mean near one means everything saturated, and a plausible sky is
        /// broken cloud - roughly a third to two thirds covered, with a spread
        /// between them.
        ///
        /// Once, and only after the first successful read, because this is
        /// per-frame code.
        /// </summary>
        private void ReportFieldOnce()
        {
            if (_reportedField) return;
            _reportedField = true;

            float sum = 0f;
            float high = 0f;
            int covered = 0;

            for (int i = 0; i < _density.Length; i++)
            {
                sum += _density[i];
                if (_density[i] > high) high = _density[i];
                if (_density[i] > 0.5f) covered++;
            }

            _logger.Notification("[VintageVisuals] weather: cloud field read - mean " +
                (sum / _density.Length).ToString("0.###") + ", peak " + high.ToString("0.###") +
                ", " + (covered * 100 / _density.Length) + "% of tiles more than half covered" +
                " (raw peaks: thickness " + _thicknessPeak.ToString("0.####") +
                ", opaqueness " + _opaquenessPeak.ToString("0.####") + "). A plausible broken sky is " +
                "a mean around a third with tiles at both ends; a mean at either extreme means the " +
                "field is not being read the way this assumes.");
        }

        private void ReportOnce(string message)
        {
            if (_reported) return;

            _reported = true;
            _logger.Warning("[VintageVisuals] weather: " + message);
        }
    }
}
