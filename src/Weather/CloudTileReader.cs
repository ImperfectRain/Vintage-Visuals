using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using HarmonyLib;

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
        /// 24 tiles of 50 blocks is 1200 blocks. It was 16, and 800 blocks was
        /// not enough: a shadow is thrown climb/tan(elevation) blocks along the
        /// sun's azimuth, which at a 20-degree morning sun is 400 - the whole
        /// half-width, straight into the edge fade. The window has to hold the
        /// throw or shadows sit too close under their clouds all morning. The
        /// default
        /// view distance. It is deliberately small: the window ships as a
        /// uniform ARRAY rather than a texture, which costs 144 vec4s and needs
        /// no sampler at all. Adding a second sampler to chunkopaque.fsh is the
        /// change that has twice cost this project the whole world render, and
        /// it is not worth it for 256 numbers.
        /// </summary>
        public const int Window = 24;

        /// <summary>Blocks per cloud tile. Hard-coded in the game's own shaders as cloudTileSize.</summary>
        public const float TileSize = 50f;

        private readonly ICoreClientAPI _capi;
        private readonly ILogger _logger;

        private readonly float[] _density = new float[Window * Window];

        /// <summary>
        /// What the last read of the game's tiles actually said. _density eases
        /// toward this rather than being assigned it - see Ease.
        /// </summary>
        private readonly float[] _target = new float[Window * Window];

        private bool _seeded;

        // Raw members are kept separately because they have to be normalised
        // separately before vanilla's formula can be applied to them. See
        // ReadWindow.
        private readonly float[] _thickness = new float[Window * Window];
        private readonly float[] _opaqueness = new float[Window * Window];

        private readonly Vec2f _origin = new Vec2f();

        /// <summary>
        /// Optical depth of a fully thick, fully opaque cloud tile.
        ///
        /// 2.0 puts a solid cloud at 86% occlusion and a tenth-thickness wisp
        /// at 18%, which is the spread cloud shadows are actually made of.
        /// </summary>
        private const float FullOpticalDepth = 2.0f;

        /// <summary>Time constant for easing the field toward the last reading.</summary>
        private const float EaseSeconds = 0.33f;

        private object _renderer;
        private MemberInfo _deckMember;
        private FieldInfo _tilesField;
        private MemberInfo _thicknessMember;
        private MemberInfo _opaqueMember;
        private int _side;

        /// <summary>
        /// The live cloud renderer, captured by a Harmony postfix on its own
        /// per-frame method.
        ///
        /// Static because a Harmony patch has nowhere else to put it. There is
        /// only ever one client and one cloud renderer, so this is not the
        /// compromise it looks like.
        /// </summary>
        private static object _captured;

        private bool _searched;
        private bool _installedCapture;
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
        /// The height the game actually draws its clouds at, or 0 if unknown.
        ///
        /// Worth having rather than guessing: this decides how far a shadow is
        /// thrown sideways from the cloud casting it, so a wrong value
        /// mis-places every shadow except at noon. The config slider defaulted
        /// to 160 and the renderer reports 256.5, which at a 30-degree sun is
        /// about a hundred blocks of error.
        /// </summary>
        public float DeckHeight { get; private set; }

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
        /// Advances the window. Call EVERY frame; pass readTiles only as often
        /// as the tile data is worth re-reading.
        ///
        /// The split is the whole point and its absence was a visible bug. The
        /// tile read is reflective and was throttled to 4 Hz, which is ample for
        /// data that changes as slowly as a cloud - but the window's CORNER was
        /// being recomputed inside that same throttle, and the corner is how the
        /// shadows stay attached to the world rather than to the camera. Between
        /// reads it was stale, so the whole field slid along with the player and
        /// then snapped back four times a second. Walking, that is a metre-long
        /// jerk four times a second; flying, it is the field visibly stepping
        /// from tile to tile.
        ///
        /// The corner costs two divisions. It belongs on every frame.
        /// </summary>
        public void Update(float deltaSeconds, bool readTiles)
        {
            if (!_searched)
            {
                _searched = true;
                InstallCapture();
            }

            // The Harmony postfix cannot fire before the cloud renderer's first
            // frame, so discovery is retried rather than done once. Everything
            // it works out is cached the moment it succeeds.
            if (_renderer == null && _captured != null)
            {
                _renderer = _captured;
                Discover();
            }

            if (_renderer == null || _tilesField == null || _thicknessMember == null)
            {
                Available = false;
                return;
            }

            UpdateOrigin();
            Ease(deltaSeconds);

            if (!readTiles) return;

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

            for (int i = 0; i < _target.Length; i++)
            {
                if (clear)
                {
                    _target[i] = 0f;
                    continue;
                }

                float thickness = GameMath.Clamp(_thickness[i] / _thicknessPeak, 0f, 1f);
                float opaque = GameMath.Clamp(_opaqueness[i] / _opaquenessPeak, 0f, 1f);

                // Beer-Lambert, NOT vanilla's draw alpha, and this is the fix
                // for a sky that came out obsessively overcast.
                //
                // clouds.vsh writes min(1, cloudOpaqueness * min(1, 10 *
                // selfThickness)) into the cloud's alpha, and copying that here
                // was the obvious thing to do - it is the game's own answer, and
                // this project's whole rule is to use the game's own answer.
                // But it answers a different question. That expression saturates
                // deliberately: it exists to make a cloud look SOLID from
                // underneath, and at a tenth of full thickness it is already
                // fully opaque. Measured against a real sky it put 64% of tiles
                // past half coverage with a mean of 0.65, so nearly the whole
                // world sat in shadow all day and the shadows had no edges left
                // to read as shadows.
                //
                // How much sunlight a cloud stops is optical depth, not draw
                // opacity: transmission falls off exponentially with how much
                // water the light passes through. A wisp takes a little light, a
                // thick cloud takes most but never all of it, and everything
                // between is a gradient - which is exactly the variation that
                // makes a cloud shadow legible as it crosses a field.
                float depth = FullOpticalDepth * thickness * opaque;

                _target[i] = GameMath.Clamp(1f - (float)Math.Exp(-depth), 0f, 1f);
            }

            SampleDeckHeight();

        }

        /// <summary>
        /// Where the window's corner sits relative to the camera, this frame.
        ///
        /// Cheap on purpose - two divisions and no reflection - because it has
        /// to run every frame. See Update for what happened when it did not.
        /// </summary>
        private void UpdateOrigin()
        {
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
            // puts the corner around -600. Both are numbers a float32 has
            // precision to spare on, at any distance from the world origin.
            double px = player.Entity.Pos.X;
            double pz = player.Entity.Pos.Z;

            double snapX = Math.Floor(px / TileSize) * TileSize - px;
            double snapZ = Math.Floor(pz / TileSize) * TileSize - pz;

            _origin.X = (float)(snapX - Window / 2 * TileSize);
            _origin.Y = (float)(snapZ - Window / 2 * TileSize);
        }

        /// <summary>
        /// Eases the field toward the last reading.
        ///
        /// Two discontinuities to absorb, both of them steps rather than noise.
        /// The tiles are re-read a few times a second, so without easing the
        /// field changes in visible increments. And when the player crosses a
        /// tile boundary the window's crop shifts by one index while its corner
        /// jumps back by a tile; the two are meant to cancel exactly, and any
        /// residue between the game's schedule and ours lands as a step.
        ///
        /// A third of a second of easing turns both into a crossfade. Cloud
        /// shadows move slowly enough that nothing is lost to it - the game
        /// blends its own cloud tiles far slower than this.
        /// </summary>
        private void Ease(float deltaSeconds)
        {
            if (!_seeded)
            {
                Array.Copy(_target, _density, _density.Length);
                _seeded = true;
                return;
            }

            // Exponential, on a time constant, so the speed does not change with
            // frame rate - the same rule the wetness trackers follow.
            float k = 1f - (float)Math.Exp(-deltaSeconds / EaseSeconds);

            for (int i = 0; i < _density.Length; i++)
            {
                _density[i] += (_target[i] - _density[i]) * k;
            }
        }

        /// <summary>
        /// Reads the altitude the game draws its clouds at, from the renderer.
        ///
        /// Cheap enough to redo per read, and it moves: the deck is not a
        /// constant. Left at 0 when it cannot be found, which the binder reads
        /// as "fall back to the config slider" - the zero case is the harmless
        /// one, as it has to be.
        /// </summary>
        private void SampleDeckHeight()
        {
            if (_deckMember == null) return;

            try
            {
                object value = ((FieldInfo)_deckMember).GetValue(_renderer);
                if (value == null) return;

                FieldInfo y = value.GetType().GetField("Y") ?? value.GetType().GetField("y");
                if (y == null) return;

                float height = Convert.ToSingle(y.GetValue(value));

                // A deck below the ground or above the sky is a misread field,
                // not a cloud height. Refuse it rather than throwing every
                // shadow to the horizon.
                if (height > 32f && height < 2048f) DeckHeight = height;
            }
            catch (Exception)
            {
                // Held at the last reading, as everywhere else here.
            }
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

            // The renderer's own offset carries the cloud altitude in Y. Found
            // by shape - a vec3 named like an offset - because the name is the
            // only part of it that is even roughly stable.
            _deckMember = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(f => f.FieldType.Name.StartsWith("Vec3", StringComparison.Ordinal) &&
                                     (f.Name.IndexOf("offset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      f.Name.IndexOf("pos", StringComparison.OrdinalIgnoreCase) >= 0));

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
        /// like a position, an offset or a counter.
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

        /// <summary>
        /// Gets hold of the live cloud renderer by patching it, not by hunting
        /// for it.
        ///
        /// Two searches have now failed at this. The first looked at
        /// ClientMain's own fields and one level into any collection among
        /// them, which misses entirely because the client groups its renderers
        /// BY RENDER STAGE - the field holds an array of lists, and the walk was
        /// asking whether a List&lt;IRenderer&gt; is a CloudRenderer. The second
        /// walked the graph breadth-first and ran out of its 40,000 node budget
        /// without arriving: a client's object graph is mostly chunks, meshes
        /// and entities, and BFS spends itself on them long before it reaches
        /// anything that owns a renderer. Raising the budget is guessing at how
        /// much of the wrong thing to enumerate.
        ///
        /// So this stops searching. The type is found by name across the loaded
        /// assemblies - deterministic, and the one thing that stays roughly
        /// stable across versions - and a Harmony postfix on its own per-frame
        /// method hands over the instance the first time it draws. No traversal,
        /// no budget, no assumption about where the client keeps it.
        ///
        /// Guarded and loud on every branch, per the rule for everything this
        /// mod touches in VintagestoryLib: a version that renames the class
        /// costs the cloud shadows and says so, and takes nothing else with it.
        /// </summary>
        private void InstallCapture()
        {
            if (_installedCapture) return;
            _installedCapture = true;

            Type type = FindCloudRendererType();
            if (type == null) return;

            MethodInfo target = AccessTools.Method(type, "OnRenderFrame")
                                ?? AccessTools.Method(type, "OnRenderFrame3D");

            if (target == null)
            {
                ReportOnce("found " + type.FullName + " but no OnRenderFrame to hook, so its tile array " +
                           "cannot be reached. Methods: " +
                           Describe(type.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                                                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                                        .Select(m => m.Name).Distinct()));
                return;
            }

            try
            {
                new Harmony(HarmonyId).Patch(target,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CloudTileReader), nameof(CapturePostfix))));

                _logger.Notification("[VintageVisuals] weather: hooked " + type.FullName + "." + target.Name +
                    " to capture the cloud renderer. Cloud placement will be read from the game's own tiles " +
                    "as soon as it draws its first frame.");
            }
            catch (Exception ex)
            {
                ReportOnce("could not hook " + type.FullName + "." + target.Name + ": " + ex.Message +
                           ". Cloud shadows have nothing to follow");
            }
        }

        /// <summary>Harmony id for the capture hook, kept apart from the other two.</summary>
        private const string HarmonyId = "vintagevisuals.cloudcapture";

        /// <summary>
        /// Stores the renderer the first time it draws, then does nothing.
        ///
        /// Deliberately incapable of throwing into the render loop: a postfix
        /// that fails takes the frame with it, and this one exists only to
        /// assign a reference.
        /// </summary>
        public static void CapturePostfix(object __instance)
        {
            if (_captured == null) _captured = __instance;
        }

        /// <summary>
        /// The cloud renderer's TYPE, by name, across everything loaded.
        ///
        /// Deterministic where a graph walk is not, and it costs one pass over
        /// the assembly list at startup. Logs every candidate it saw, because
        /// "the class is called something else now" and "the class is gone" need
        /// different answers and look identical from here.
        /// </summary>
        private Type FindCloudRendererType()
        {
            var candidates = new List<Type>();

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch (Exception) { continue; }

                foreach (Type type in types)
                {
                    if (type == null || type.IsAbstract || type.IsInterface) continue;
                    if (type.Name.IndexOf("CloudRenderer", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    candidates.Add(type);
                }
            }

            if (candidates.Count == 0)
            {
                ReportOnce("no type named anything like CloudRenderer exists in any loaded assembly, so " +
                           "cloud shadows have nothing to follow. The class has been renamed or removed");
                return null;
            }

            // The one that actually owns tiles, when more than one matches - a
            // renderer with no tile array is some other kind of cloud renderer.
            Type best = candidates.FirstOrDefault(HasTileArray) ?? candidates[0];

            _logger.Notification("[VintageVisuals] weather: cloud renderer type " + best.FullName +
                (candidates.Count > 1
                    ? " (chosen from " + Describe(candidates.Select(c => c.FullName)) + ")"
                    : ""));

            return best;
        }

        private static bool HasTileArray(Type type)
        {
            return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                       .Any(f => f.FieldType.IsArray &&
                                 f.FieldType.GetElementType() != null &&
                                 f.FieldType.GetElementType().Name.IndexOf("CloudTile",
                                     StringComparison.OrdinalIgnoreCase) >= 0);
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
        private float _lastReportedMean = -1f;

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
            float mean = 0f;
            for (int k = 0; k < _density.Length; k++) mean += _density[k];
            mean /= _density.Length;

            // Re-reported when the sky meaningfully changes, not once. The field
            // is now something to be TUNED - the occlusion curve is a judgement
            // about how much light a cloud stops - and a number logged once at
            // world load says nothing about whether a change helped.
            if (_reportedField && Math.Abs(mean - _lastReportedMean) < 0.08f) return;

            _reportedField = true;
            _lastReportedMean = mean;

            float high = 0f;
            int covered = 0;

            for (int i = 0; i < _density.Length; i++)
            {
                if (_density[i] > high) high = _density[i];
                if (_density[i] > 0.5f) covered++;
            }

            _logger.Notification("[VintageVisuals] weather: cloud field read - mean " +
                mean.ToString("0.###") + ", peak " + high.ToString("0.###") +
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
