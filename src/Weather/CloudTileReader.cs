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
        private readonly Vec2f _origin = new Vec2f();

        private object _renderer;
        private FieldInfo _tilesField;
        private MemberInfo _thicknessMember;
        private MemberInfo _opaqueMember;
        private int _side;

        private bool _searched;
        private bool _reported;
        private float _peak = 1f;

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

        /// <summary>World XZ of the corner of tile [0,0].</summary>
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

            float peak = 0f;
            for (int z = 0; z < Window; z++)
            {
                for (int x = 0; x < Window; x++)
                {
                    object tile = tiles.GetValue((first + z) * _side + first + x);

                    float value = tile == null ? 0f : DensityOf(tile);
                    _density[z * Window + x] = value;
                    if (value > peak) peak = value;
                }
            }

            // The tile fields are whatever integer scale the game happens to
            // use, and guessing a divisor would be one more thing to be wrong
            // about. A decaying peak normalises without needing to know: it
            // settles on the busiest sky seen recently and lets an emptying sky
            // fade rather than rescaling itself brighter.
            _peak = Math.Max(peak, _peak * 0.995f);
            if (_peak < 1e-4f) _peak = 1e-4f;

            for (int i = 0; i < _density.Length; i++)
            {
                _density[i] = GameMath.Clamp(_density[i] / _peak, 0f, 1f);
            }

            IClientPlayer player = _capi.World?.Player;
            if (player?.Entity == null) return;

            // Snapped to the tile grid, because the game's own grid is: a
            // window that slid smoothly over a grid that jumps would make the
            // shadows crawl by up to a tile every time the player crossed one.
            double px = player.Entity.Pos.X;
            double pz = player.Entity.Pos.Z;

            _origin.X = (float)(Math.Floor(px / TileSize) * TileSize - Window / 2 * TileSize);
            _origin.Y = (float)(Math.Floor(pz / TileSize) * TileSize - Window / 2 * TileSize);
        }

        /// <summary>
        /// How much of the sun this tile blocks.
        ///
        /// Mirrors cloudmap.fsh's own opacity term, which is
        /// <c>cloudOpaqueness * min(1, 10 * selfThickness)</c>, when both
        /// members can be found. Thickness alone is the fallback: it is the one
        /// that decides whether there is a cloud here at all.
        /// </summary>
        private float DensityOf(object tile)
        {
            float thickness = Numeric(_thicknessMember, tile);
            if (_opaqueMember == null) return Math.Max(0f, thickness);

            return Math.Max(0f, thickness) * Math.Max(0f, Numeric(_opaqueMember, tile));
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
        }

        /// <summary>
        /// Walks the client for a cloud renderer.
        ///
        /// By type name rather than by field name, and through collections as
        /// well as fields, because the one thing that can be relied on across
        /// versions is roughly what the class is called - not where it is
        /// stored or whether it is held directly at all.
        /// </summary>
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

        private void ReportOnce(string message)
        {
            if (_reported) return;

            _reported = true;
            _logger.Warning("[VintageVisuals] weather: " + message);
        }
    }
}
