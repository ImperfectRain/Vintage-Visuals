using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VintageVisuals.Common;
using VintageVisuals.Common.Scene;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// v14. Drips from wet leaves, pollen over vegetation, and fireflies, as
    /// the game's own particles. One spawner for the three because they share
    /// everything except their gates: a tick, a reach around the player, a
    /// look at the ground the game says is there, and a cap on how many live.
    ///
    /// Every gate is a fact the tracker already holds - the wetness rain left
    /// on the leaves, the temperature, the climate's rainfall, the season, the
    /// time of day, the wind - and where they spawn is the game's own blocks:
    /// leaves from the leaf map (<see cref="CanopyMap"/>), plants (block
    /// material Plant) at the top of the rain map. Nothing here invents a place for
    /// them. The game has no pollen or fireflies of its own (DATA GAP: neither
    /// is in its assets, so how many and how they move is this mod's choice;
    /// where and when is the world's).
    ///
    /// No GL: IClientWorldAccessor.SpawnParticles, safe at
    /// EnumRenderStage.Before. Cost: at most <see cref="TriesPerTick"/>
    /// block or leaf-map lookups per kind every <see cref="TickSeconds"/>,
    /// and a hard cap alive per kind. Not measured.
    /// </summary>
    public sealed class ForestLifeSpawner : IRenderer
    {
        public const float TickSeconds = 0.2f;
        public const int TriesPerTick = 8;

        public const float DripReach = 8f;
        public const int DripMaxAlive = 30;
        public const float DripLife = 1.6f;
        /// <summary>Leaf wetness below which the leaves have stopped dripping.</summary>
        public const float DripWetness = 0.15f;

        public const float PollenReach = 14f;
        public const int PollenMaxAlive = 30;
        public const float PollenLife = 8f;
        public const float PollenMinCelsius = 12f;

        public const float FireflyNear = 4f;
        public const float FireflyReach = 20f;
        public const int FireflyMaxAlive = 16;
        /// <summary>One flash: a firefly is drawn as a short glow that fades, so many of them read as blinking.</summary>
        public const float FireflyLife = 1.3f;
        public const float FireflyMinCelsius = 14f;

        private readonly ICoreClientAPI _capi;
        private readonly Func<EnvironmentTracker> _tracker;
        private readonly Func<PseudoPbrConfig> _config;
        private readonly Random _random = new Random();
        private readonly SimpleParticleProperties _drip;
        private readonly SimpleParticleProperties _pollen;
        private readonly SimpleParticleProperties _firefly;

        private float _sinceTick;
        private float _drips;
        private float _pollens;
        private float _fireflies;
        private bool _reported;

        public ForestLifeSpawner(ICoreClientAPI capi, Func<EnvironmentTracker> tracker, Func<PseudoPbrConfig> config)
        {
            _capi = capi;
            _tracker = tracker;
            _config = config;

            _drip = new SimpleParticleProperties(
                1, 0, ColorUtil.ToRgba(150, 205, 220, 235),
                new Vec3d(), new Vec3d(),
                new Vec3f(0f, -0.6f, 0f), new Vec3f(0f, -0.3f, 0f),
                DripLife, 1f, 0.07f, 0.1f, EnumParticleModel.Quad);
            _drip.WithTerrainCollision = true;
            _drip.ShouldDieInLiquid = true;

            _pollen = new SimpleParticleProperties(
                1, 0, ColorUtil.ToRgba(120, 255, 242, 190),
                new Vec3d(), new Vec3d(),
                new Vec3f(), new Vec3f(),
                PollenLife, 0f, 0.04f, 0.07f, EnumParticleModel.Quad);
            _pollen.WithTerrainCollision = false;
            _pollen.ShouldDieInLiquid = true;
            // Faintly self-lit, so a speck reads against shade as it does in a beam.
            _pollen.VertexFlags = 48;
            _pollen.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -80f);

            _firefly = new SimpleParticleProperties(
                1, 0, ColorUtil.ToRgba(230, 200, 255, 110),
                new Vec3d(), new Vec3d(),
                new Vec3f(-0.12f, -0.04f, -0.12f), new Vec3f(0.24f, 0.08f, 0.24f),
                FireflyLife, 0f, 0.09f, 0.13f, EnumParticleModel.Quad);
            _firefly.WithTerrainCollision = false;
            _firefly.ShouldDieInLiquid = true;
            // Full glow: a firefly is a light, not a lit speck.
            _firefly.VertexFlags = 255;
            _firefly.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -230f);
        }

        public double RenderOrder { get { return 0.96; } }

        public int RenderRange { get { return 0; } }

        public void Dispose() { }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Before) return;

            _drips = Age(_drips, deltaTime, DripLife);
            _pollens = Age(_pollens, deltaTime, PollenLife);
            _fireflies = Age(_fireflies, deltaTime, FireflyLife);

            _sinceTick += deltaTime;
            if (_sinceTick < TickSeconds) return;
            _sinceTick = 0f;

            PseudoPbrConfig config = _config();
            if (config == null || !config.Enabled) return;
            EnvironmentTracker tracker = _tracker();
            EntityPlayer player = _capi.World?.Player?.Entity;
            if (tracker == null || player == null) return;
            EnvironmentState world = tracker.Current;

            float drips = GameMath.Clamp(config.LeafDrips, 0f, 1f);
            if (drips > 0.001f) tracker.Canopy.Request(_capi.ElapsedMilliseconds);

            int spawned = 0;
            spawned += SpawnDrips(drips * DripRate(world), tracker.Canopy, player);
            spawned += SpawnPollen(GameMath.Clamp(config.Pollen, 0f, 1f) * PollenRate(world), world, player);
            spawned += SpawnFireflies(GameMath.Clamp(config.Fireflies, 0f, 1f) * FireflyRate(world), player);

            if (spawned > 0 && !_reported)
            {
                _reported = true;
                _capi.Logger.Notification("[VintageVisuals] pseudopbr: forest particles spawning (drips, pollen or fireflies).");
            }
        }

        // --- Gates: pure, so tools/smoketest can drive them ------------------

        /// <summary>How hard the leaves are dripping, 0..1: wet leaves above freezing.</summary>
        public static float DripRate(EnvironmentState world)
        {
            float wet = GameMath.Clamp((world.Wetness - DripWetness) / (1f - DripWetness), 0f, 1f);
            float thawed = GameMath.Clamp((world.Temperature + 1f) / 3f, 0f, 1f);
            return wet * thawed;
        }

        /// <summary>The share of the year's growing season left, 0 midautumn and midwinter to 1.</summary>
        public static float GrowingSeason(EnvironmentState world)
        {
            return 1f - GameMath.Clamp(Math.Max(world.Autumn, world.Winter), 0f, 1f);
        }

        /// <summary>Pollen, 0..1: a warm, dry day in the growing season, outdoors.</summary>
        public static float PollenRate(EnvironmentState world)
        {
            float day = GameMath.Clamp((world.DayLight - 0.45f) / 0.25f, 0f, 1f);
            float warm = GameMath.Clamp((world.Temperature - PollenMinCelsius) / 6f, 0f, 1f);
            float dry = 1f - GameMath.Clamp(world.Rain * 10f, 0f, 1f);
            float damp = 1f - GameMath.Clamp(world.Wetness * 2f, 0f, 1f);
            float outside = GameMath.Clamp((world.SkyExposure - 0.3f) / 0.4f, 0f, 1f);
            return day * warm * dry * damp * outside * GrowingSeason(world);
        }

        /// <summary>Fireflies, 0..1: dusk or a warm night in the growing season, more in humid places, none in rain or a strong wind.</summary>
        public static float FireflyRate(EnvironmentState world)
        {
            float dusk = 1f - GameMath.Clamp((world.DayLight - 0.15f) / 0.2f, 0f, 1f);
            float warm = GameMath.Clamp((world.Temperature - FireflyMinCelsius) / 6f, 0f, 1f);
            float dry = 1f - GameMath.Clamp(world.Rain * 10f, 0f, 1f);
            float calm = 1f - GameMath.Clamp((world.WindSpeed - 0.3f) / 0.4f, 0f, 1f);
            float humid = 0.4f + 0.6f * GameMath.Clamp(world.Humidity, 0f, 1f);
            float outside = GameMath.Clamp((world.SkyExposure - 0.3f) / 0.4f, 0f, 1f);
            return dusk * warm * dry * calm * humid * outside * GrowingSeason(world);
        }

        // --- Spawning --------------------------------------------------------

        private int SpawnDrips(float rate, CanopyMap map, EntityPlayer player)
        {
            if (rate < 0.001f || map == null || !map.Valid) return 0;
            int cap = (int)(DripMaxAlive * rate);
            int spawned = 0;
            for (int t = 0; t < TriesPerTick && _drips < cap; t++)
            {
                double x = player.Pos.X + (_random.NextDouble() * 2.0 - 1.0) * DripReach;
                double z = player.Pos.Z + (_random.NextDouble() * 2.0 - 1.0) * DripReach;
                if (!map.TryCell((int)Math.Floor(x), (int)Math.Floor(z), out int density, out int lowest)) continue;
                // A real crown overhead, not a twig, and above the player's feet.
                if (density < 2 || lowest <= player.Pos.Y + 1.0 || lowest > player.Pos.Y + 24.0) continue;
                // Thicker crowns hold more water and drip more often.
                if (_random.NextDouble() > density / 7.0) continue;
                _drip.MinPos.Set(x, lowest - 0.05, z);
                _capi.World.SpawnParticles(_drip);
                _drips += 1f;
                spawned++;
            }
            return spawned;
        }

        private int SpawnPollen(float rate, EnvironmentState world, EntityPlayer player)
        {
            if (rate < 0.001f) return 0;
            int cap = (int)(PollenMaxAlive * rate);
            if (_pollens >= cap) return 0;
            Vec2f dir = world.WindDirection ?? new Vec2f(1f, 0f);
            float drift = 0.08f + 0.35f * GameMath.Clamp(world.WindSpeed, 0f, 1f);
            _pollen.MinVelocity.Set(dir.X * drift - 0.03f, -0.01f, dir.Y * drift - 0.03f);
            _pollen.AddVelocity.Set(0.06f, 0.03f, 0.06f);
            int spawned = 0;
            for (int t = 0; t < TriesPerTick && _pollens < cap; t++)
            {
                if (!TryVegetation(player, 2f, PollenReach, out double x, out double groundY, out double z)) continue;
                _pollen.MinPos.Set(x, groundY + 0.4 + _random.NextDouble() * 2.2, z);
                _capi.World.SpawnParticles(_pollen);
                _pollens += 1f;
                spawned++;
            }
            return spawned;
        }

        private int SpawnFireflies(float rate, EntityPlayer player)
        {
            if (rate < 0.001f) return 0;
            int cap = (int)(FireflyMaxAlive * rate);
            int spawned = 0;
            for (int t = 0; t < TriesPerTick && _fireflies < cap; t++)
            {
                if (!TryVegetation(player, FireflyNear, FireflyReach, out double x, out double groundY, out double z)) continue;
                _firefly.MinPos.Set(x, groundY + 0.3 + _random.NextDouble() * 1.6, z);
                _capi.World.SpawnParticles(_firefly);
                _fireflies += 1f;
                spawned++;
            }
            return spawned;
        }

        /// <summary>
        /// A random point between <paramref name="near"/> and
        /// <paramref name="reach"/> blocks from the player whose top block is
        /// a plant (grass, flowers, crops), by the game's own block material.
        /// </summary>
        private bool TryVegetation(EntityPlayer player, float near, float reach,
                                   out double x, out double groundY, out double z)
        {
            double angle = _random.NextDouble() * Math.PI * 2.0;
            double r = near + _random.NextDouble() * (reach - near);
            x = player.Pos.X + Math.Cos(angle) * r;
            z = player.Pos.Z + Math.Sin(angle) * r;
            groundY = 0;
            IBlockAccessor blocks = _capi.World?.BlockAccessor;
            if (blocks == null) return false;
            int bx = (int)Math.Floor(x);
            int bz = (int)Math.Floor(z);
            int top = blocks.GetRainMapHeightAt(bx, bz);
            if (top <= 0 || Math.Abs(top - player.Pos.Y) > 12) return false;
            var pos = new BlockPos(bx, top, bz, player.Pos.Dimension);
            // The rain map ends on the topmost blocking block; a grass tuft or
            // flower sits in the block above it.
            for (int dy = 1; dy >= 0; dy--)
            {
                pos.Y = top + dy;
                Block block = blocks.GetBlock(pos);
                if (block == null) continue;
                if (block.BlockMaterial == EnumBlockMaterial.Plant)
                {
                    groundY = top + dy;
                    return true;
                }
            }
            return false;
        }

        private static float Age(float alive, float deltaTime, float life)
        {
            return Math.Max(0f, alive - deltaTime * (alive / life));
        }
    }
}
