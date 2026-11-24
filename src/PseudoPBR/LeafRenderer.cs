using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// v15. Falling leaves with their real shapes: small flat meshes, each a
    /// leaf cut from its tree's own texture (<see cref="LeafSprites"/>),
    /// tinted with the colour the game's climate and season maps give that
    /// tree where it stands, tumbling in 3D down and along the wind.
    ///
    /// The game's particles cannot do this: they are solid quads and cubes of
    /// one colour. So this draws its own, through the game's OWN standard
    /// shader (<see cref="IRenderAPI.PreparedStandardShader"/>), the program
    /// block entities are drawn with: its lighting, fog, alpha test and
    /// render-target outputs are the game's, and this mod ships no GLSL for it.
    /// One RenderMesh per leaf, at most <see cref="MaxLeaves"/>.
    ///
    /// Textures: one small sprite sheet per leaf texture seen (4x4 leaves,
    /// 48x48 for a 64x64 texture), cut on the CPU from the PNG and uploaded at
    /// EnumRenderStage.Before with no shader bound (CLAUDE.md: textures are
    /// created only there). Freed on Dispose. Meshes: 16 quads, one per sheet
    /// cell, shared by every sheet.
    ///
    /// If anything here fails - a texture that will not load, a sheet with no
    /// leaf in it, a render exception - that texture (or the whole renderer)
    /// is marked unusable, the spawner falls back to the coloured particles,
    /// and the log says so once. Cost not measured.
    /// </summary>
    public sealed class LeafRenderer : IRenderer
    {
        public const int MaxLeaves = 160;

        /// <summary>A leaf's drawn width in blocks; the cut leaf fills about two thirds of it.</summary>
        public const float MinSize = 0.4f;
        public const float MaxSize = 0.6f;
        public const float NeedleSize = 0.35f;

        /// <summary>How long a leaf lies on the ground before it shrinks away, and the most any leaf lives.</summary>
        public const float RestSeconds = 8f;
        public const float MaxLife = 40f;
        public const float FadeSeconds = 1.2f;

        /// <summary>Beyond this from the player a leaf is dropped.</summary>
        public const float KeepRange = 48f;

        private readonly ICoreClientAPI _capi;
        private readonly Func<WindSample> _wind;
        private readonly List<Leaf> _leaves = new List<Leaf>();
        private readonly Dictionary<string, Sheet> _sheets = new Dictionary<string, Sheet>();
        private readonly Random _random = new Random();
        private readonly Matrixf _matrix = new Matrixf();
        private readonly BlockPos _pos = new BlockPos(0);
        private MeshRef[] _meshes;
        private bool _failed;
        private bool _reported;

        public LeafRenderer(ICoreClientAPI capi, Func<WindSample> wind)
        {
            _capi = capi;
            _wind = wind;
        }

        /// <summary>The game's wind for the leaves: direction (unit, XZ), speed 0..1, gust slider and breeze clock.</summary>
        public struct WindSample
        {
            public float DirX, DirZ, Speed, Gusts, Breeze;
        }

        private sealed class Sheet
        {
            public LoadedTexture Texture;
            public int Sprites;
            public bool Usable;
        }

        private sealed class Leaf
        {
            public LeafFlight.State Flight;
            public Sheet Sheet;
            public int Variant;
            public Vec4f Tint;
            public Vec4f Light = new Vec4f(1, 1, 1, 1);
            public float Size;
            public float Age;
            public float Rest;
            public float WindAge;
            public float LocalWind;
        }

        public double RenderOrder { get { return 0.5; } }

        public int RenderRange { get { return 0; } }

        public int Count { get { return _leaves.Count; } }

        public bool Usable { get { return !_failed; } }

        // --- Spawning (called from ForestLifeSpawner at EnumRenderStage.Before) ---

        /// <summary>
        /// Tries to add one textured leaf from the leaf block at <paramref name="pos"/>.
        /// False when this block's texture cannot give one, so the caller can
        /// fall back to a coloured particle.
        /// </summary>
        public bool TrySpawn(Block block, BlockPos pos, double x, double y, double z,
                             float vx, float vy, float vz, bool evergreen)
        {
            if (_failed || block == null) return false;
            if (_leaves.Count >= MaxLeaves) return true;

            Sheet sheet = SheetFor(block, out float meanGrey);
            if (sheet == null || !sheet.Usable) return false;

            var leaf = new Leaf
            {
                Sheet = sheet,
                Variant = _random.Next(sheet.Sprites),
                Tint = TintAt(block, pos, meanGrey),
                Size = evergreen ? NeedleSize : MinSize + (float)_random.NextDouble() * (MaxSize - MinSize),
                WindAge = (float)_random.NextDouble() * 0.1f,
            };
            leaf.Flight = LeafFlight.Start(x, y, z, vx, vy, vz, _random);
            _leaves.Add(leaf);
            return true;
        }

        private readonly int[] _samples = new int[8];

        /// <summary>
        /// The tint: the game's own tinted samples of this block at this
        /// position (Block.GetRandomColor, the call vanilla's breaking
        /// particles make) over the texture's own mean grey.
        /// </summary>
        private Vec4f TintAt(Block block, BlockPos pos, float meanGrey)
        {
            for (int i = 0; i < _samples.Length; i++)
                _samples[i] = block.GetRandomColor(_capi, pos, BlockFacing.UP, i * 3);
            LeafSprites.Tint(_samples, _samples.Length, meanGrey, out float r, out float g, out float b);
            return new Vec4f(r, g, b, 1f);
        }

        private readonly Dictionary<string, float> _meanGrey = new Dictionary<string, float>();

        private Sheet SheetFor(Block block, out float meanGrey)
        {
            meanGrey = 0f;
            AssetLocation png = TexturePng(block);
            if (png == null) return null;
            string key = png.ToString();
            if (_sheets.TryGetValue(key, out Sheet known))
            {
                _meanGrey.TryGetValue(key, out meanGrey);
                return known;
            }

            var sheet = new Sheet();
            _sheets[key] = sheet;
            try
            {
                // Textures are created only with no shader bound (CLAUDE.md).
                if (_capi.Render.CurrentActiveShader != null) { _sheets.Remove(key); return null; }

                IAsset asset = _capi.Assets.TryGet(png);
                IBitmap bitmap = asset?.ToBitmap(_capi);
                if (bitmap == null) { Report("could not load " + key); return sheet; }
                int w = bitmap.Width, h = bitmap.Height;
                int[] pixels = bitmap.Pixels;
                meanGrey = LeafSprites.MeanGrey(pixels);
                _meanGrey[key] = meanGrey;

                int[] cut = LeafSprites.BuildSheet(pixels, w, h, key.GetHashCode(), out int cell, out int sprites);
                if (cut == null || meanGrey <= 1f) { Report("no leaf shape found in " + key); return sheet; }

                int size = LeafSprites.Side * cell;
                sheet.Texture = new LoadedTexture(_capi, 0, size, size);
                _capi.Render.LoadOrUpdateTextureFromRgba(cut, false, 0, ref sheet.Texture);
                sheet.Sprites = sprites;
                sheet.Usable = sheet.Texture.TextureId != 0;
                if (!_reported)
                {
                    _reported = true;
                    _capi.Logger.Notification("[VintageVisuals] pseudopbr: textured falling leaves on; first leaf sheet cut from " +
                        key + " (" + sprites + " leaves, " + cell + " px each, mean grey " + meanGrey.ToString("0") + ").");
                }
            }
            catch (Exception e)
            {
                sheet.Usable = false;
                Report("sheet for " + key + " failed: " + e.Message);
            }
            return sheet;
        }

        /// <summary>The PNG behind the block's leaf texture: its first baked variant, overlays stripped.</summary>
        public static AssetLocation TexturePng(Block block)
        {
            if (block?.Textures == null || block.Textures.Count == 0) return null;
            CompositeTexture tex = null;
            foreach (string face in new[] { "all", "up", "north" })
                if (block.Textures.TryGetValue(face, out tex) && tex != null) break;
            if (tex == null) foreach (var kv in block.Textures) { tex = kv.Value; break; }
            BakedCompositeTexture baked = tex?.Baked;
            AssetLocation name = baked?.BakedVariants != null && baked.BakedVariants.Length > 0
                ? baked.BakedVariants[0].BakedName
                : baked?.BakedName ?? tex?.Base;
            if (name == null) return null;
            string path = name.Path;
            int plus = path.IndexOf("++", StringComparison.Ordinal);
            if (plus >= 0) path = path.Substring(0, plus);
            if (!path.StartsWith("textures/", StringComparison.Ordinal)) path = "textures/" + path;
            if (!path.EndsWith(".png", StringComparison.Ordinal)) path += ".png";
            return new AssetLocation(name.Domain, path);
        }

        private void Report(string why)
        {
            _capi.Logger.Warning("[VintageVisuals] pseudopbr: textured leaves: " + why + "; that tree's leaves fall as coloured particles.");
        }

        // --- Simulation and drawing ---------------------------------------

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (_failed || _leaves.Count == 0) return;
            if (stage != EnumRenderStage.Opaque) return;
            EntityPlayer player = _capi.World?.Player?.Entity;
            if (player == null) return;

            dt = Math.Min(dt, 0.1f);
            Step(dt, player);
            if (_leaves.Count == 0) return;

            try
            {
                Draw(player);
            }
            catch (Exception e)
            {
                _failed = true;
                _capi.Logger.Warning("[VintageVisuals] pseudopbr: textured leaves failed while drawing and are off for this session, " +
                    "falling back to coloured particles. " + e.Message);
            }
        }

        private void Step(float dt, EntityPlayer player)
        {
            IBlockAccessor blocks = _capi.World.BlockAccessor;
            WindSample wind = _wind();
            for (int i = _leaves.Count - 1; i >= 0; i--)
            {
                Leaf leaf = _leaves[i];
                leaf.Age += dt;

                double dx = leaf.Flight.X - player.Pos.X, dz = leaf.Flight.Z - player.Pos.Z;
                bool gone = leaf.Age > MaxLife || dx * dx + dz * dz > KeepRange * KeepRange || leaf.Rest > RestSeconds + FadeSeconds;
                if (gone) { _leaves.RemoveAt(i); continue; }

                if (leaf.Flight.Landed) { leaf.Rest += dt; LeafFlight.Settle(ref leaf.Flight, dt); continue; }

                // The wind where the leaf is, refreshed ten times a second: the
                // game's, plus the gust the player sees crossing there.
                leaf.WindAge -= dt;
                if (leaf.WindAge <= 0f)
                {
                    leaf.WindAge = 0.1f;
                    float gust = GustField.At(Wrap(leaf.Flight.X), Wrap(leaf.Flight.Z), wind.Gusts, wind.Speed, wind.Breeze, wind.DirX, wind.DirZ);
                    leaf.LocalWind = GameMath.Clamp(wind.Speed + ForestLifeSpawner.LeafGustLift * gust, 0f, 1f);
                    _pos.Set((int)Math.Floor(leaf.Flight.X), (int)Math.Floor(leaf.Flight.Y), (int)Math.Floor(leaf.Flight.Z));
                    _pos.dimension = player.Pos.Dimension;
                    leaf.Light = blocks.GetLightRGBs(_pos) ?? leaf.Light;
                }

                LeafFlight.Step(ref leaf.Flight, dt, wind.DirX, wind.DirZ, leaf.LocalWind);

                // Ground and water: the block the leaf is in, as the game has it.
                _pos.Set((int)Math.Floor(leaf.Flight.X), (int)Math.Floor(leaf.Flight.Y), (int)Math.Floor(leaf.Flight.Z));
                _pos.dimension = player.Pos.Dimension;
                Block fluid = blocks.GetBlock(_pos, BlockLayersAccess.Fluid);
                if (fluid != null && fluid.IsLiquid())
                {
                    LeafFlight.Land(ref leaf.Flight, _pos.Y + 0.9, true);
                    continue;
                }
                Block solid = blocks.GetBlock(_pos);
                Cuboidf[] boxes = solid?.CollisionBoxes;
                if (boxes != null && boxes.Length > 0 && solid.BlockMaterial != EnumBlockMaterial.Leaves)
                {
                    float top = 0f;
                    foreach (Cuboidf box in boxes) top = Math.Max(top, box.Y2);
                    if (leaf.Flight.Y < _pos.Y + top) LeafFlight.Land(ref leaf.Flight, _pos.Y + top + 0.02, false);
                }
            }
        }

        private void Draw(EntityPlayer player)
        {
            IRenderAPI rpi = _capi.Render;
            if (rpi.CurrentActiveShader != null) return;
            EnsureMeshes();

            Vec3d cam = player.CameraPos;
            rpi.GlDisableCullFace();
            IStandardShaderProgram prog = rpi.PreparedStandardShader((int)cam.X, (int)cam.Y, (int)cam.Z);
            try
            {
                prog.ViewMatrix = rpi.CameraMatrixOriginf;
                prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;
                prog.AlphaTest = 0.5f;
                prog.NormalShaded = 0;
                prog.ExtraGlow = 0;
                prog.DontWarpVertices = 1;
                int bound = -1;
                foreach (Leaf leaf in _leaves)
                {
                    int tex = leaf.Sheet.Texture.TextureId;
                    if (tex != bound) { prog.Tex2D = tex; bound = tex; }
                    float s = leaf.Size * LeafFlight.Shrink(leaf.Rest - RestSeconds, FadeSeconds);
                    if (leaf.Age > MaxLife - FadeSeconds) s *= Math.Max(0f, (MaxLife - leaf.Age) / FadeSeconds);
                    if (s <= 0.001f) continue;
                    _matrix.Identity()
                        .Translate((float)(leaf.Flight.X - cam.X), (float)(leaf.Flight.Y - cam.Y), (float)(leaf.Flight.Z - cam.Z))
                        .RotateY(leaf.Flight.Yaw)
                        .RotateX(leaf.Flight.Pitch)
                        .RotateZ(leaf.Flight.Roll)
                        .Scale(s, s, s);
                    prog.ModelMatrix = _matrix.Values;
                    prog.RgbaTint = leaf.Tint;
                    prog.RgbaLightIn = leaf.Light;
                    rpi.RenderMesh(_meshes[leaf.Variant]);
                }
            }
            finally
            {
                prog.Stop();
                rpi.GlEnableCullFace();
            }
        }

        /// <summary>One unit quad per sheet cell, centred, in the XY plane, with the game's block vertex layout (xyz, uv, rgba, flags).</summary>
        private void EnsureMeshes()
        {
            if (_meshes != null) return;
            int n = LeafSprites.Side * LeafSprites.Side;
            _meshes = new MeshRef[n];
            for (int i = 0; i < n; i++)
            {
                float u0 = (i % LeafSprites.Side) / (float)LeafSprites.Side;
                float v0 = (i / LeafSprites.Side) / (float)LeafSprites.Side;
                float d = 1f / LeafSprites.Side;
                var mesh = new MeshData(4, 6, false, true, true, true);
                int white = ColorUtil.WhiteArgb;
                mesh.AddVertexWithFlags(-0.5f, -0.5f, 0f, u0, v0 + d, white, 0);
                mesh.AddVertexWithFlags(0.5f, -0.5f, 0f, u0 + d, v0 + d, white, 0);
                mesh.AddVertexWithFlags(0.5f, 0.5f, 0f, u0 + d, v0, white, 0);
                mesh.AddVertexWithFlags(-0.5f, 0.5f, 0f, u0, v0, white, 0);
                mesh.AddIndices(0, 1, 2, 0, 2, 3);
                _meshes[i] = _capi.Render.UploadMesh(mesh);
            }
        }

        private static float Wrap(double v)
        {
            double p = Common.Scene.EnvironmentState.CameraPeriod;
            return (float)(v - p * Math.Floor(v / p));
        }

        public void Dispose()
        {
            if (_meshes != null) foreach (MeshRef m in _meshes) m?.Dispose();
            _meshes = null;
            foreach (Sheet sheet in _sheets.Values) sheet.Texture?.Dispose();
            _sheets.Clear();
            _leaves.Clear();
        }
    }

    /// <summary>
    /// v15. How a leaf moves, with no world in it: the caller supplies the
    /// wind and finds the ground. A leaf falls slowly and swings from side to
    /// side as it falls, relaxes toward the wind's speed downwind, and tumbles
    /// faster the harder it is blown. Pure, so tools/smoketest can fly one.
    /// </summary>
    public static class LeafFlight
    {
        /// <summary>The fall speed a leaf settles to in still air, blocks a second, and how much it swings about it.</summary>
        public const float FallSpeed = 0.7f;
        public const float FallSwing = 0.35f;

        /// <summary>Side-to-side swing as it falls, blocks a second.</summary>
        public const float Sway = 0.6f;

        /// <summary>The air speed downwind at wind 0 and at a full gale, blocks a second.</summary>
        public const float StillDrift = 0.3f;
        public const float GaleDrift = 5f;

        /// <summary>How fast a leaf takes up the air's speed, per second.</summary>
        public const float Drag = 2.5f;

        public struct State
        {
            public double X, Y, Z;
            public float VX, VY, VZ;
            public float Yaw, Pitch, Roll;
            public float Spin, Phase, PhaseRate;
            public bool Landed, Floating;
        }

        public static State Start(double x, double y, double z, float vx, float vy, float vz, Random rnd)
        {
            return new State
            {
                X = x, Y = y, Z = z, VX = vx, VY = vy, VZ = vz,
                Yaw = (float)(rnd.NextDouble() * Math.PI * 2),
                Pitch = (float)(rnd.NextDouble() - 0.5),
                Roll = (float)(rnd.NextDouble() * Math.PI * 2),
                Spin = (float)((rnd.NextDouble() < 0.5 ? -1 : 1) * (1.5 + rnd.NextDouble() * 2.5)),
                Phase = (float)(rnd.NextDouble() * Math.PI * 2),
                PhaseRate = (float)(4.0 + rnd.NextDouble() * 3.0),
            };
        }

        /// <summary>The air's speed downwind for a local wind 0..1: rising with the square, as the shedding does.</summary>
        public static float Drift(float localWind)
        {
            float w = Math.Max(0f, Math.Min(1f, localWind));
            return StillDrift + (GaleDrift - StillDrift) * w * w;
        }

        public static void Step(ref State s, float dt, float dirX, float dirZ, float localWind)
        {
            s.Phase += s.PhaseRate * dt;
            float sin = (float)Math.Sin(s.Phase);
            float drift = Drift(localWind);

            // Air velocity: downwind, plus a swing across the wind as the leaf
            // rocks, plus a fall that pauses on each swing.
            float ax = dirX * drift + -dirZ * Sway * sin;
            float az = dirZ * drift + dirX * Sway * sin;
            float ay = -(FallSpeed + FallSwing * (float)Math.Cos(2 * s.Phase));

            float k = Math.Min(1f, Drag * dt);
            s.VX += (ax - s.VX) * k;
            s.VY += (ay - s.VY) * k;
            s.VZ += (az - s.VZ) * k;
            s.X += s.VX * dt;
            s.Y += s.VY * dt;
            s.Z += s.VZ * dt;

            // Tumbling: spin about its own axis, faster in a stronger wind,
            // and a rocking pitch with the swing.
            s.Roll += s.Spin * (0.5f + 1.5f * localWind) * dt;
            s.Pitch = 0.7f * sin;
            s.Yaw += 0.3f * s.Spin * dt;
        }

        /// <summary>Puts the leaf down: on the ground it stops; on water it floats.</summary>
        public static void Land(ref State s, double y, bool water)
        {
            s.Landed = true;
            s.Floating = water;
            s.Y = y;
            s.VX = s.VY = s.VZ = 0f;
        }

        /// <summary>A landed leaf eases to lying flat.</summary>
        public static void Settle(ref State s, float dt)
        {
            float k = Math.Min(1f, 6f * dt);
            s.Pitch += ((float)(Math.PI / 2) - s.Pitch) * k;
        }

        /// <summary>1 until the fade starts, then down to 0 over <paramref name="fade"/> seconds.</summary>
        public static float Shrink(float sinceFadeStart, float fade)
        {
            if (sinceFadeStart <= 0f) return 1f;
            return Math.Max(0f, 1f - sinceFadeStart / fade);
        }
    }
}
