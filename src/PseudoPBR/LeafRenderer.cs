using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VintageVisuals.Common.Diagnostics;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// v15. Falling leaves with their real shapes, and the litter they leave.
    ///
    /// Each leaf is a flat two-sided quad cut from its tree's own texture
    /// (<see cref="LeafSprites"/>) and tinted with the colour the game's climate
    /// and season maps give that tree. They are drawn through the game's OWN
    /// standard shader (<see cref="IRenderAPI.PreparedStandardShader"/>), so the
    /// game's lighting, fog, alpha test and outputs apply and this mod ships no
    /// GLSL for them.
    ///
    /// BATCHED (second cut): the first cut drew one mesh per leaf, which capped
    /// it at about 160. Now every leaf of one tree texture goes into one
    /// dynamic mesh whose corners are transformed on the CPU each frame, and
    /// lying leaves go into a second mesh per texture that is rebuilt at most
    /// twice a second, only when the litter changed. A frame draws two meshes
    /// per tree texture in view, however many leaves there are. Per-leaf tint
    /// and light ride the vertex colour (the shader multiplies it in; the tint
    /// uniform carries a fixed x2.5 so a tint above 1 survives the byte).
    ///
    /// Litter: a landed leaf stays, for a spread of lifetimes (most a few
    /// minutes, some much longer, shorter in rain, LeafFlight.LitterLife),
    /// thickening a little where several land on one block, until the cap; a
    /// gust can lift one again; one whose ground is dug away falls. Floating
    /// leaves ride the water's own current (IBlockFlowing.GetPushVector, the
    /// push the game gives swimmers) or, on still water, the wind. Litter is
    /// cosmetic and session-only: it is not saved, and it is dropped beyond
    /// <see cref="KeepRange"/>.
    ///
    /// Textures: one small sprite sheet per leaf texture seen (48x48 for a 64x64
    /// texture), created at EnumRenderStage.Before with no shader bound, freed
    /// on Dispose. Meshes: two per sheet, starting at <see cref="FirstBatch"/>
    /// quads and doubling as needed up to the caps (D78; before, each was
    /// allocated at the cap and uploaded whole every frame). Leaves landing
    /// beyond <see cref="LeafBudget.LitterReach"/> fade instead of lying.
    /// Cost per flying leaf per frame: two block reads (fluid, then solid),
    /// a light read and a gust sample every 0.1 s, and one quad. Not
    /// measured; <c>.vvbench leaves</c> measures it in game.
    /// </summary>
    public sealed class LeafRenderer : IRenderer
    {
        /// <summary>Leaves in the air (and on water, and fading) at once, and lying leaves.</summary>
        public const int MaxFlying = LeafBudget.MaxCap;
        public const int MaxLitter = 1500;

        /// <summary>A leaf's drawn width in blocks; the cut leaf fills about two thirds of it.</summary>
        public const float MinSize = 0.4f;
        public const float MaxSize = 0.6f;
        public const float NeedleSize = 0.35f;

        public const float MaxAirborne = 40f;
        public const float FadeSeconds = 1.5f;

        /// <summary>How thick a pile on one block may get: each leaf lies this much above the last, up to the cap.</summary>
        public const float PileStep = 0.004f;
        public const int PileCap = 24;

        /// <summary>Litter is rebuilt at most this often.</summary>
        public const float LitterRebuildSeconds = 0.5f;

        /// <summary>Lying leaves checked per frame for decay, wind and ground.</summary>
        public const int LitterChecksPerFrame = 40;

        /// <summary>Local wind above which a gust may lift a lying leaf, and the chance per check at full wind.</summary>
        public const float LiftWind = 0.7f;
        public const float LiftChance = 0.02f;

        /// <summary>Beyond this from the player a leaf, flying or lying, is dropped.</summary>
        public const float KeepRange = 64f;

        /// <summary>
        /// D78. A batch starts this many quads big and doubles as its leaves
        /// need, up to its cap. Every frame uploads the whole flying batch, so
        /// a batch sized to the cap (800) would upload 3,200 vertices a frame
        /// per tree texture for a handful of leaves.
        /// </summary>
        public const int FirstBatch = 64;

        /// <summary>The fixed tint scale: vertex colour carries tint / TintScale.</summary>
        public const float TintScale = 2.5f;

        private readonly ICoreClientAPI _capi;
        private readonly Func<WindSample> _wind;
        private readonly List<Leaf> _flying = new List<Leaf>();
        private readonly List<Leaf> _litter = new List<Leaf>();
        private readonly Dictionary<long, int> _pile = new Dictionary<long, int>();
        private readonly Dictionary<string, Sheet> _sheets = new Dictionary<string, Sheet>();
        private readonly List<Sheet> _sheetList = new List<Sheet>();
        private readonly Random _random = new Random();
        private readonly Matrixf _matrix = new Matrixf();
        private readonly BlockPos _pos = new BlockPos(0);
        private readonly float[] _corners = new float[12];
        private bool _failed;
        private bool _reported;
        private bool _litterDirty;
        private float _sinceRebuild;
        private int _litterCursor;
        private double _originX, _originY, _originZ;

        public LeafRenderer(ICoreClientAPI capi, Func<WindSample> wind)
        {
            _capi = capi;
            _wind = wind;
        }

        /// <summary>The game's wind and rain for the leaves.</summary>
        public struct WindSample
        {
            public float DirX, DirZ, Speed, Gusts, Breeze, Rain;
        }

        private sealed class Sheet
        {
            public LoadedTexture Texture;
            public int Sprites;
            public bool Usable;
            public float MeanGrey;
            public Batch Flying;
            public Batch Litter;
            public int Count;
        }

        private sealed class Batch
        {
            public MeshData Data;
            public MeshRef Mesh;
            public int Capacity;
            public int Used;
            public int LastUsed;
        }

        private sealed class Leaf
        {
            public LeafFlight.State Flight;
            public Sheet Sheet;
            public int Variant;
            public float TintR, TintG, TintB;
            public float Shade = 1f;
            public float Size;
            public float Age;
            public float Life;
            public float Fade = -1f;
            public float WindAge;
            public float LocalWind;
            public long PileKey;
        }

        public double RenderOrder { get { return 0.5; } }

        public int RenderRange { get { return 0; } }

        /// <summary>Leaves in the air or on water: what the spawner's cap counts.</summary>
        public int Count { get { return _flying.Count; } }

        public int LitterCount { get { return _litter.Count; } }

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
            if (_flying.Count >= MaxFlying) return true;

            Sheet sheet = SheetFor(block);
            if (sheet == null || !sheet.Usable) return false;

            for (int i = 0; i < _samples.Length; i++)
                _samples[i] = block.GetRandomColor(_capi, pos, BlockFacing.UP, i * 3);
            LeafSprites.Tint(_samples, _samples.Length, sheet.MeanGrey, out float r, out float g, out float b);

            var leaf = new Leaf
            {
                Sheet = sheet,
                Variant = _random.Next(sheet.Sprites),
                TintR = r, TintG = g, TintB = b,
                Size = evergreen ? NeedleSize : MinSize + (float)_random.NextDouble() * (MaxSize - MinSize),
                WindAge = (float)_random.NextDouble() * 0.1f,
            };
            leaf.Flight = LeafFlight.Start(x, y, z, vx, vy, vz, _random);
            _flying.Add(leaf);
            return true;
        }

        private readonly int[] _samples = new int[8];

        private Sheet SheetFor(Block block)
        {
            AssetLocation png = TexturePng(block);
            if (png == null) return null;
            string key = png.ToString();
            if (_sheets.TryGetValue(key, out Sheet known)) return known;

            // Textures are created only with no shader bound (CLAUDE.md).
            if (_capi.Render.CurrentActiveShader != null) return null;

            var sheet = new Sheet();
            _sheets[key] = sheet;
            try
            {
                IAsset asset = _capi.Assets.TryGet(png);
                IBitmap bitmap = asset?.ToBitmap(_capi);
                if (bitmap == null) { Report("could not load " + key); return sheet; }
                int w = bitmap.Width, h = bitmap.Height;
                int[] pixels = bitmap.Pixels;
                sheet.MeanGrey = LeafSprites.MeanGrey(pixels);

                int[] cut = LeafSprites.BuildSheet(pixels, w, h, key.GetHashCode(), out int cell, out int sprites);
                if (cut == null || sheet.MeanGrey <= 1f) { Report("no leaf shape found in " + key); return sheet; }

                int size = LeafSprites.Side * cell;
                sheet.Texture = new LoadedTexture(_capi, 0, size, size);
                _capi.Render.LoadOrUpdateTextureFromRgba(cut, false, 0, ref sheet.Texture);
                sheet.Sprites = sprites;
                sheet.Flying = NewBatch(FirstBatch);
                sheet.Litter = NewBatch(FirstBatch);
                sheet.Usable = sheet.Texture.TextureId != 0;
                if (sheet.Usable) _sheetList.Add(sheet);
                if (!_reported)
                {
                    _reported = true;
                    _capi.Logger.Notification("[VintageVisuals] pseudopbr: textured falling leaves on (batched); first leaf sheet cut from " +
                        key + " (" + sprites + " leaves, " + cell + " px each, mean grey " + sheet.MeanGrey.ToString("0") + ").");
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

        // --- Batches -------------------------------------------------------

        /// <summary>
        /// A mesh of <paramref name="capacity"/> quads in the game's block vertex
        /// layout (xyz, uv, rgba, flags), uploaded empty. Unused quads are left
        /// at zero size, so they cost a degenerate triangle and draw nothing.
        /// </summary>
        private Batch NewBatch(int capacity)
        {
            var data = new MeshData(capacity * 4, capacity * 6, false, true, true, true);
            data.SetVerticesCount(capacity * 4);
            data.SetIndicesCount(capacity * 6);
            for (int q = 0; q < capacity; q++)
            {
                int v = q * 4, i = q * 6;
                data.Indices[i] = v; data.Indices[i + 1] = v + 1; data.Indices[i + 2] = v + 2;
                data.Indices[i + 3] = v; data.Indices[i + 4] = v + 2; data.Indices[i + 5] = v + 3;
            }
            return new Batch { Data = data, Capacity = capacity, Mesh = _capi.Render.UploadMesh(data) };
        }

        /// <summary>Grows a batch to hold <paramref name="needed"/> quads, doubling, at most <paramref name="cap"/>.</summary>
        private void Reserve(Batch b, int needed, int cap)
        {
            int size = GrownCapacity(b.Capacity, needed, cap);
            if (size == b.Capacity) return;
            b.Mesh?.Dispose();
            Batch grown = NewBatch(size);
            b.Data = grown.Data; b.Mesh = grown.Mesh; b.Capacity = grown.Capacity;
            b.LastUsed = 0;
        }

        /// <summary>The next batch size for a count: the current one, or doubled until it fits, at most the cap. Pure.</summary>
        public static int GrownCapacity(int capacity, int needed, int cap)
        {
            if (needed <= capacity || capacity >= cap) return capacity;
            int size = capacity;
            while (size < needed && size < cap) size *= 2;
            return Math.Min(size, cap);
        }

        private void BeginBatch(Batch b) { b.Used = 0; }

        private void AddQuad(Batch b, Leaf leaf, double ox, double oy, double oz, float scale)
        {
            if (b.Used >= b.Capacity) return;
            float size = leaf.Size * scale;
            LeafFlight.Corners(leaf.Flight.Yaw, leaf.Flight.Pitch, leaf.Flight.Roll, size, _corners);
            float cx = (float)(leaf.Flight.X - ox), cy = (float)(leaf.Flight.Y - oy), cz = (float)(leaf.Flight.Z - oz);

            int side = LeafSprites.Side;
            float d = 1f / side;
            float u0 = (leaf.Variant % side) * d, v0 = (leaf.Variant / side) * d;

            byte r = ToByte(leaf.TintR * leaf.Shade / TintScale);
            byte g = ToByte(leaf.TintG * leaf.Shade / TintScale);
            byte bl = ToByte(leaf.TintB * leaf.Shade / TintScale);

            MeshData m = b.Data;
            int v = b.Used * 4;
            for (int k = 0; k < 4; k++)
            {
                int xi = (v + k) * 3, ui = (v + k) * 2, ci = (v + k) * 4;
                m.xyz[xi] = cx + _corners[k * 3];
                m.xyz[xi + 1] = cy + _corners[k * 3 + 1];
                m.xyz[xi + 2] = cz + _corners[k * 3 + 2];
                m.Uv[ui] = (k == 0 || k == 3) ? u0 : u0 + d;
                m.Uv[ui + 1] = (k < 2) ? v0 + d : v0;
                m.Rgba[ci] = r; m.Rgba[ci + 1] = g; m.Rgba[ci + 2] = bl; m.Rgba[ci + 3] = 255;
                m.Flags[v + k] = 0;
            }
            b.Used++;
        }

        /// <summary>Zeroes the quads used last time but not this time, then uploads.</summary>
        private void EndBatch(Batch b)
        {
            for (int q = b.Used; q < b.LastUsed; q++)
            {
                int xi = q * 12;
                Array.Clear(b.Data.xyz, xi, 12);
            }
            b.LastUsed = b.Used;
            _capi.Render.UpdateMesh(b.Mesh, b.Data);
        }

        private static byte ToByte(float v)
        {
            int i = (int)(v * 255f + 0.5f);
            return (byte)(i < 0 ? 0 : (i > 255 ? 255 : i));
        }

        // --- Simulation ----------------------------------------------------

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (_failed || stage != EnumRenderStage.Opaque) return;
            if (LeafBench.Mode == LeafBench.Off && (_flying.Count > 0 || _litter.Count > 0))
            {
                // The bench's no-leaves run: none flying, none lying, nothing drawn.
                _flying.Clear(); _litter.Clear(); _pile.Clear();
                foreach (Sheet s in _sheetList) { BeginBatch(s.Flying); EndBatch(s.Flying); BeginBatch(s.Litter); EndBatch(s.Litter); }
            }
            LeafBench.Flying = _flying.Count;
            LeafBench.Litter = _litter.Count;
            if (_flying.Count == 0 && _litter.Count == 0) return;
            EntityPlayer player = _capi.World?.Player?.Entity;
            if (player == null) return;

            dt = Math.Min(dt, 0.1f);
            try
            {
                WindSample wind = _wind();
                StepFlying(dt, player, wind);
                StepLitter(dt, player, wind);
                Draw(player, dt);
            }
            catch (Exception e)
            {
                _failed = true;
                _capi.Logger.Warning("[VintageVisuals] pseudopbr: textured leaves failed and are off for this session, " +
                    "falling back to coloured particles. " + e.Message);
            }
        }

        private float Brightness(Vec4f light)
        {
            if (light == null) return 1f;
            return Math.Max(light.W, Math.Max(light.X, Math.Max(light.Y, light.Z)));
        }

        private float _cameraBrightness = 1f;

        private void SetPos(double x, double y, double z, int dim)
        {
            _pos.Set((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
            _pos.dimension = dim;
        }

        private void StepFlying(float dt, EntityPlayer player, WindSample wind)
        {
            IBlockAccessor blocks = _capi.World.BlockAccessor;
            int dim = player.Pos.Dimension;
            for (int i = _flying.Count - 1; i >= 0; i--)
            {
                Leaf leaf = _flying[i];
                leaf.Age += dt;
                double dx = leaf.Flight.X - player.Pos.X, dz = leaf.Flight.Z - player.Pos.Z;
                if (dx * dx + dz * dz > KeepRange * KeepRange) { _flying.RemoveAt(i); continue; }

                // Fading out: the last moments of a leaf that has decayed or sunk.
                if (leaf.Fade >= 0f)
                {
                    leaf.Fade += dt;
                    if (leaf.Fade > FadeSeconds) { _flying.RemoveAt(i); continue; }
                    LeafFlight.Settle(ref leaf.Flight, dt);
                    continue;
                }

                leaf.WindAge -= dt;
                if (leaf.WindAge <= 0f)
                {
                    leaf.WindAge = 0.1f;
                    float gust = GustField.At(Wrap(leaf.Flight.X), Wrap(leaf.Flight.Z), wind.Gusts, wind.Speed, wind.Breeze, wind.DirX, wind.DirZ);
                    leaf.LocalWind = GameMath.Clamp(wind.Speed + ForestLifeSpawner.LeafGustLift * gust, 0f, 1f);
                    SetPos(leaf.Flight.X, leaf.Flight.Y, leaf.Flight.Z, dim);
                    leaf.Shade = GameMath.Clamp(Brightness(blocks.GetLightRGBs(_pos)) / Math.Max(0.05f, _cameraBrightness), 0.25f, 1.5f);
                }

                if (leaf.Flight.Floating)
                {
                    StepFloating(leaf, dt, blocks, dim, wind);
                    continue;
                }

                LeafFlight.Step(ref leaf.Flight, dt, wind.DirX, wind.DirZ, leaf.LocalWind);
                if (leaf.Age > MaxAirborne) { leaf.Fade = 0f; continue; }

                // Ground and water: the block the leaf is in, as the game has it.
                SetPos(leaf.Flight.X, leaf.Flight.Y, leaf.Flight.Z, dim);
                Block fluid = blocks.GetBlock(_pos, BlockLayersAccess.Fluid);
                if (fluid != null && fluid.IsLiquid())
                {
                    LeafFlight.Land(ref leaf.Flight, _pos.Y + SurfaceOf(fluid), true);
                    leaf.Life = LeafFlight.LitterLife(_random.NextDouble(), wind.Rain, true);
                    leaf.Age = 0f;
                    continue;
                }
                Block solid = blocks.GetBlock(_pos);
                float top = TopOf(solid);
                if (top >= 0f && leaf.Flight.Y < _pos.Y + top)
                {
                    long key = PileKey(_pos.X, _pos.Y, _pos.Z);
                    _pile.TryGetValue(key, out int n);
                    // D78: far from the player a leaf lands and fades rather than
                    // lying, so the litter cap stays where litter is seen.
                    bool farLanding = dx * dx + dz * dz > LeafBudget.LitterReach * LeafBudget.LitterReach;
                    if (_litter.Count >= MaxLitter || n >= PileCap || farLanding)
                    {
                        // No room: it lands, lies a moment, and goes.
                        LeafFlight.Land(ref leaf.Flight, _pos.Y + top + 0.01 + PileStep * Math.Min(n, PileCap), false);
                        leaf.Fade = 0f;
                        continue;
                    }
                    LeafFlight.Land(ref leaf.Flight, _pos.Y + top + 0.01 + PileStep * n, false);
                    _pile[key] = n + 1;
                    leaf.PileKey = key;
                    leaf.Life = LeafFlight.LitterLife(_random.NextDouble(), wind.Rain, false);
                    leaf.Age = 0f;
                    leaf.Flight.Pitch = (float)(Math.PI / 2 + (_random.NextDouble() - 0.5) * 0.3);
                    _flying.RemoveAt(i);
                    _litter.Add(leaf);
                    _litterDirty = true;
                }
            }
        }

        private void StepFloating(Leaf leaf, float dt, IBlockAccessor blocks, int dim, WindSample wind)
        {
            if (leaf.Age > leaf.Life) { leaf.Fade = 0f; return; }
            SetPos(leaf.Flight.X, leaf.Flight.Y - 0.5, leaf.Flight.Z, dim);
            Block fluid = blocks.GetBlock(_pos, BlockLayersAccess.Fluid);
            float cx = 0f, cz = 0f;
            if (fluid is IBlockFlowing flowing)
            {
                FastVec3f push = flowing.GetPushVector(_pos);
                cx = push.X; cz = push.Z;
            }
            else if (fluid?.PushVector != null)
            {
                cx = (float)fluid.PushVector.X; cz = (float)fluid.PushVector.Z;
            }
            double px = leaf.Flight.X, pz = leaf.Flight.Z;
            LeafFlight.Float(ref leaf.Flight, dt, cx, cz, wind.DirX, wind.DirZ, leaf.LocalWind);

            // Stranded: the water ends here. It stops at the bank and goes in time.
            SetPos(leaf.Flight.X, leaf.Flight.Y - 0.5, leaf.Flight.Z, dim);
            Block next = blocks.GetBlock(_pos, BlockLayersAccess.Fluid);
            if (next == null || !next.IsLiquid())
            {
                leaf.Flight.X = px; leaf.Flight.Z = pz;
                leaf.Flight.VX = leaf.Flight.VZ = 0f;
            }
            else
            {
                leaf.Flight.Y = _pos.Y + SurfaceOf(next);
            }
        }

        private void StepLitter(float dt, EntityPlayer player, WindSample wind)
        {
            if (_litter.Count == 0) return;
            IBlockAccessor blocks = _capi.World.BlockAccessor;
            int dim = player.Pos.Dimension;
            float checkDt = dt * Math.Max(1f, _litter.Count / (float)LitterChecksPerFrame);
            int checks = Math.Min(LitterChecksPerFrame, _litter.Count);
            for (int c = 0; c < checks; c++)
            {
                if (_litter.Count == 0) break;
                if (_litterCursor >= _litter.Count) _litterCursor = 0;
                int i = _litterCursor++;
                Leaf leaf = _litter[i];
                leaf.Age += checkDt;

                double dx = leaf.Flight.X - player.Pos.X, dz = leaf.Flight.Z - player.Pos.Z;
                bool far = dx * dx + dz * dz > KeepRange * KeepRange;

                // Decayed: it fades in the flying batch for its last moments.
                bool decayed = leaf.Age > leaf.Life;

                // Ground gone (dug away, or the block changed): it falls again.
                SetPos(leaf.Flight.X, leaf.Flight.Y - 0.05, leaf.Flight.Z, dim);
                float top = TopOf(blocks.GetBlock(_pos));
                bool unsupported = top < 0f;

                // A gust may lift it.
                bool lifted = false;
                if (!decayed && !unsupported && wind.Speed > 0f)
                {
                    float gust = GustField.At(Wrap(leaf.Flight.X), Wrap(leaf.Flight.Z), wind.Gusts, wind.Speed, wind.Breeze, wind.DirX, wind.DirZ);
                    float local = GameMath.Clamp(wind.Speed + ForestLifeSpawner.LeafGustLift * gust, 0f, 1f);
                    if (local > LiftWind && _random.NextDouble() < LiftChance * (local - LiftWind) / (1f - LiftWind) * (checkDt * 10f))
                        lifted = true;
                }

                if (!(far || decayed || unsupported || lifted)) continue;

                _litter.RemoveAt(i);
                _litterCursor = i;
                _litterDirty = true;
                if (_pile.TryGetValue(leaf.PileKey, out int n))
                {
                    if (n <= 1) _pile.Remove(leaf.PileKey); else _pile[leaf.PileKey] = n - 1;
                }
                if (far || _flying.Count >= MaxFlying) continue;

                if (decayed) leaf.Fade = 0f;
                else
                {
                    leaf.Flight.Landed = false;
                    leaf.Flight.VY = lifted ? 1.2f : 0f;
                    leaf.Age = 0f;
                }
                _flying.Add(leaf);
            }
        }

        /// <summary>The top of what a leaf can lie on in this block, 0..1, or -1 if it falls through (air, leaves, plants).</summary>
        private static float TopOf(Block block)
        {
            Cuboidf[] boxes = block?.CollisionBoxes;
            if (boxes == null || boxes.Length == 0) return -1f;
            if (block.BlockMaterial == EnumBlockMaterial.Leaves) return -1f;
            float top = 0f;
            foreach (Cuboidf box in boxes) top = Math.Max(top, box.Y2);
            return top;
        }

        private static float SurfaceOf(Block fluid)
        {
            return Math.Max(0.1f, Math.Min(1f, (fluid.LiquidLevel + 1) / 8f)) - 0.08f;
        }

        private static long PileKey(int x, int y, int z)
        {
            return ((long)(x & 0x3FFFFFF) << 38) | ((long)(z & 0x3FFFFFF) << 12) | (long)(y & 0xFFF);
        }

        // --- Drawing -------------------------------------------------------

        private void Draw(EntityPlayer player, float dt)
        {
            IRenderAPI rpi = _capi.Render;
            if (rpi.CurrentActiveShader != null) return;
            Vec3d cam = player.CameraPos;

            SetPos(cam.X, cam.Y, cam.Z, player.Pos.Dimension);
            _cameraBrightness = Brightness(_capi.World.BlockAccessor.GetLightRGBs(_pos));

            // Flying (and floating, and fading) leaves: every frame, camera-relative.
            foreach (Sheet s in _sheetList) s.Count = 0;
            foreach (Leaf leaf in _flying) leaf.Sheet.Count++;
            foreach (Sheet s in _sheetList) Reserve(s.Flying, s.Count, MaxFlying);
            foreach (Sheet s in _sheetList) BeginBatch(s.Flying);
            foreach (Leaf leaf in _flying)
            {
                float scale = leaf.Fade >= 0f ? LeafFlight.Shrink(leaf.Fade, FadeSeconds) : 1f;
                if (scale > 0.001f) AddQuad(leaf.Sheet.Flying, leaf, cam.X, cam.Y, cam.Z, scale);
            }
            foreach (Sheet s in _sheetList) if (s.Flying.Used > 0 || s.Flying.LastUsed > 0) EndBatch(s.Flying);

            // Litter: rebuilt only when it changed, relative to a held origin.
            _sinceRebuild += dt;
            double ox = cam.X - _originX, oz = cam.Z - _originZ;
            if ((_litterDirty && _sinceRebuild >= LitterRebuildSeconds) || ox * ox + oz * oz > 256 * 256)
            {
                _sinceRebuild = 0f;
                _litterDirty = false;
                _originX = Math.Floor(cam.X); _originY = Math.Floor(cam.Y); _originZ = Math.Floor(cam.Z);
                foreach (Sheet s in _sheetList) s.Count = 0;
                foreach (Leaf leaf in _litter) leaf.Sheet.Count++;
                foreach (Sheet s in _sheetList) Reserve(s.Litter, s.Count, MaxLitter);
                foreach (Sheet s in _sheetList) BeginBatch(s.Litter);
                foreach (Leaf leaf in _litter) AddQuad(leaf.Sheet.Litter, leaf, _originX, _originY, _originZ, 1f);
                foreach (Sheet s in _sheetList) EndBatch(s.Litter);
            }

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
                prog.RgbaTint = new Vec4f(TintScale, TintScale, TintScale, 1f);
                foreach (Sheet s in _sheetList)
                {
                    prog.Tex2D = s.Texture.TextureId;
                    if (s.Flying.LastUsed > 0)
                    {
                        prog.ModelMatrix = _matrix.Identity().Values;
                        rpi.RenderMesh(s.Flying.Mesh);
                    }
                    if (s.Litter.LastUsed > 0)
                    {
                        prog.ModelMatrix = _matrix.Identity()
                            .Translate((float)(_originX - cam.X), (float)(_originY - cam.Y), (float)(_originZ - cam.Z)).Values;
                        rpi.RenderMesh(s.Litter.Mesh);
                    }
                }
            }
            finally
            {
                prog.Stop();
                rpi.GlEnableCullFace();
            }
        }

        private static float Wrap(double v)
        {
            double p = Common.Scene.EnvironmentState.CameraPeriod;
            return (float)(v - p * Math.Floor(v / p));
        }

        public void Dispose()
        {
            foreach (Sheet sheet in _sheets.Values)
            {
                sheet.Texture?.Dispose();
                sheet.Flying?.Mesh?.Dispose();
                sheet.Litter?.Mesh?.Dispose();
            }
            _sheets.Clear();
            _sheetList.Clear();
            _flying.Clear();
            _litter.Clear();
            _pile.Clear();
        }
    }

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

        /// <summary>Speed a floating leaf takes from a current, blocks a second, and from the wind on still water at a gale.</summary>
        public const float CurrentSpeed = 0.9f;
        public const float SurfaceWind = 0.6f;

        /// <summary>
        /// A floating leaf: carried by the water's current where it has one
        /// (the game's own push vector for that water block, any length; zero
        /// is still water), and on still water nudged by the wind; turning
        /// slowly, lying flat. The caller keeps it on the surface and strands
        /// it at the bank.
        /// </summary>
        public static void Float(ref State s, float dt, float currentX, float currentZ,
                                 float dirX, float dirZ, float localWind)
        {
            float len = (float)Math.Sqrt(currentX * currentX + currentZ * currentZ);
            float tx, tz;
            if (len > 1e-6f)
            {
                tx = currentX / len * CurrentSpeed;
                tz = currentZ / len * CurrentSpeed;
            }
            else
            {
                float w = Math.Max(0f, Math.Min(1f, localWind));
                tx = dirX * SurfaceWind * w * w;
                tz = dirZ * SurfaceWind * w * w;
            }
            float k = Math.Min(1f, 1.5f * dt);
            s.VX += (tx - s.VX) * k;
            s.VZ += (tz - s.VZ) * k;
            s.VY = 0f;
            s.X += s.VX * dt;
            s.Z += s.VZ * dt;
            s.Roll += 0.2f * s.Spin * dt;
            Settle(ref s, dt);
        }

        /// <summary>
        /// How long a leaf lies before it is gone, seconds: a spread, not a
        /// timer - most go in a few minutes, a few stay much longer - and
        /// shorter in rain, which beats them down and rots them. Pure.
        /// </summary>
        public static float LitterLife(double draw, float rain, bool water)
        {
            double u = Math.Max(1e-4, Math.Min(1.0, draw));
            double mean = (water ? 90.0 : 240.0) / (1.0 + 2.0 * Math.Max(0f, Math.Min(1f, rain)));
            double life = -Math.Log(u) * mean;
            return (float)Math.Max(20.0, Math.Min(900.0, life));
        }

        /// <summary>
        /// The four corners of a leaf of width <paramref name="size"/> turned by
        /// yaw, pitch and roll (Y, then X, then Z, as the single-mesh draw
        /// did), relative to its centre, into <paramref name="out12"/>. Pure.
        /// </summary>
        public static void Corners(float yaw, float pitch, float roll, float size, float[] out12)
        {
            float cy = (float)Math.Cos(yaw), sy = (float)Math.Sin(yaw);
            float cp = (float)Math.Cos(pitch), sp = (float)Math.Sin(pitch);
            float cr = (float)Math.Cos(roll), sr = (float)Math.Sin(roll);
            float h = 0.5f * size;
            for (int i = 0; i < 4; i++)
            {
                float x = (i == 0 || i == 3) ? -h : h;
                float y = (i < 2) ? -h : h;
                // Z roll
                float x1 = x * cr - y * sr, y1 = x * sr + y * cr, z1 = 0f;
                // X pitch
                float y2 = y1 * cp - z1 * sp, z2 = y1 * sp + z1 * cp, x2 = x1;
                // Y yaw
                float x3 = x2 * cy + z2 * sy, z3 = -x2 * sy + z2 * cy;
                out12[i * 3] = x3;
                out12[i * 3 + 1] = y2;
                out12[i * 3 + 2] = z3;
            }
        }
    }
}
