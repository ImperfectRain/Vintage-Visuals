using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VintageVisuals.Common;
using VintageVisuals.Common.Scene;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Dust motes in the sun shafts under a canopy, as the game's own
    /// particles.
    ///
    /// A mote is only visible where a beam of sun crosses it, so this spawns
    /// them ONLY inside a shaft. The shaft comes from game data, never from an
    /// invented field: a point below the crown whose ray toward the sun crosses
    /// a thin cell of the leaf map (<see cref="CanopyMap"/>) beside thicker
    /// ones. Clear sky and daylight come from <see cref="EnvironmentState"/>,
    /// the sun direction from <see cref="AtmosphereState"/>, so nothing here
    /// asks the world anything the tracker has not already asked.
    ///
    /// No GL at all: particles go through IClientWorldAccessor.SpawnParticles,
    /// so this is safe at EnumRenderStage.Before and from any config change.
    ///
    /// Cost: up to <see cref="TriesPerTick"/> leaf-map lookups and at most a
    /// few particles every <see cref="TickSeconds"/>, with a hard cap on how
    /// many are alive. Not measured.
    /// </summary>
    public sealed class DustMoteSpawner : IRenderer
    {
        /// <summary>Seconds between spawn rounds.</summary>
        public const float TickSeconds = 0.2f;

        /// <summary>Random points tested per round at full slider.</summary>
        public const int TriesPerTick = 24;

        /// <summary>Horizontal reach around the player, in blocks.</summary>
        public const float Reach = 12f;

        /// <summary>Most motes alive at once at full slider: a hint of dust, never a haze.</summary>
        public const int MaxAlive = 40;

        /// <summary>Seconds a mote lives.</summary>
        public const float LifeSeconds = 4.5f;

        /// <summary>Cloud cover above which there is no beam to light a mote.</summary>
        public const float CloudLimit = 0.55f;

        /// <summary>The most opaque a mote may be, out of 255: specks, not snow.</summary>
        public const int MaxAlpha = 110;

        private readonly ICoreClientAPI _capi;
        private readonly Func<EnvironmentTracker> _tracker;
        private readonly Func<PseudoPbrConfig> _config;
        private readonly Random _random = new Random();
        private readonly SimpleParticleProperties _props;

        private float _sinceTick;
        private float _alive;
        private bool _reported;

        public DustMoteSpawner(ICoreClientAPI capi, Func<EnvironmentTracker> tracker, Func<PseudoPbrConfig> config)
        {
            _capi = capi;
            _tracker = tracker;
            _config = config;

            _props = new SimpleParticleProperties(
                1, 1, ColorUtil.ToRgba(MaxAlpha, 255, 236, 190),
                new Vec3d(), new Vec3d(),
                new Vec3f(-0.03f, -0.015f, -0.03f), new Vec3f(0.06f, 0.03f, 0.06f),
                LifeSeconds, 0f, 0.06f, 0.11f, EnumParticleModel.Quad);
            _props.WithTerrainCollision = false;
            _props.ShouldDieInLiquid = true;
            _props.GravityEffect = 0f;
            // Lit as if in sun: they are only ever spawned in a beam.
            _props.VertexFlags = 96;
            _props.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -MaxAlpha * 0.8f);
        }

        public double RenderOrder { get { return 0.95; } }

        public int RenderRange { get { return 0; } }

        public void Dispose() { }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Before) return;

            // Motes age out on their own; this only tracks roughly how many
            // the cap still allows.
            _alive = Math.Max(0f, _alive - deltaTime * (_alive / LifeSeconds));

            _sinceTick += deltaTime;
            if (_sinceTick < TickSeconds) return;
            _sinceTick = 0f;

            PseudoPbrConfig config = _config();
            if (config == null || !config.Enabled) return;
            float strength = GameMath.Clamp(config.DustMotes, 0f, 1f);
            if (strength < 0.001f) return;

            EnvironmentTracker tracker = _tracker();
            if (tracker == null) return;
            EnvironmentState world = tracker.Current;
            AtmosphereState air = tracker.Atmosphere;
            CanopyMap map = tracker.Canopy;
            if (map == null || !map.Valid) return;

            // A beam needs the sun: up, and not behind cloud.
            if (world.DayLight < 0.25f || world.CloudCover > CloudLimit) return;
            Vec3f sun = air.SunDirection;
            if (sun == null || sun.Y < 0.12f) return;

            EntityPlayer player = _capi.World?.Player?.Entity;
            if (player == null) return;

            // Dust shows where a beam shows: the same humid-air weighting the
            // shafts use in pbrterrainmaterial.glsl (vvTerrainShaftLevel), so
            // motes and beams thicken together after rain and at a low sun.
            int cap = (int)(MaxAlive * strength * BeamHumidity(world.Wetness, sun.Y));
            int tries = Math.Max(1, (int)(TriesPerTick * strength));
            int spawned = 0;

            for (int t = 0; t < tries && _alive < cap; t++)
            {
                double x = player.Pos.X + (_random.NextDouble() * 2.0 - 1.0) * Reach;
                double z = player.Pos.Z + (_random.NextDouble() * 2.0 - 1.0) * Reach;
                double y = player.Pos.Y + _random.NextDouble() * 5.0 - 0.5;

                if (!InShaft(map, x, y, z, sun)) continue;

                _props.MinPos.Set(x, y, z);
                _capi.World.SpawnParticles(_props);
                _alive += 1f;
                spawned++;
            }

            if (spawned > 0 && !_reported)
            {
                _reported = true;
                _capi.Logger.Notification("[VintageVisuals] pseudopbr: dust motes spawning in sun shafts under the canopy.");
            }
        }

        /// <summary>
        /// How strongly humid air carries a beam, 0.35 dry at noon to 1: a
        /// mirror of the `humid` term in vvTerrainShaftLevel (wet ground, or a
        /// sun low between 0.55 and 0.15 of elevation). Fog is the shader's
        /// third input and is not known here.
        /// </summary>
        public static float BeamHumidity(float wetness, float sunY)
        {
            float t = GameMath.Clamp((sunY - 0.15f) / 0.4f, 0f, 1f);
            float lowSun = 1f - t * t * (3f - 2f * t);
            return 0.35f + 0.65f * GameMath.Clamp(Math.Max(GameMath.Clamp(wetness, 0f, 1f), lowSun), 0f, 1f);
        }

        /// <summary>
        /// Whether a point is inside a beam of sun under a canopy: below the
        /// crown it sits under, with its ray toward the sun crossing the crown
        /// at a thin cell that has thicker cells beside it - a gap, not open
        /// sky and not a solid roof of leaves.
        /// </summary>
        public static bool InShaft(CanopyMap map, double x, double y, double z, Vec3f sun)
        {
            if (!map.TryCell((int)Math.Floor(x), (int)Math.Floor(z), out int hereDensity, out int hereLowest)) return false;
            if (hereDensity <= 0 || y > hereLowest - 0.5) return false;

            // Up the ray to the underside of the crown overhead.
            double rise = hereLowest + 1.0 - y;
            double run = rise / Math.Max(0.12, sun.Y);
            if (run > 24.0) return false;
            double cx = x + sun.X * run;
            double cz = z + sun.Z * run;
            if (!map.TryCell((int)Math.Floor(cx), (int)Math.Floor(cz), out int gapDensity, out _)) return false;

            int around = 0;
            for (int d = 0; d < 4; d++)
            {
                int ox = d == 0 ? 2 : d == 1 ? -2 : 0;
                int oz = d == 2 ? 2 : d == 3 ? -2 : 0;
                if (map.TryCell((int)Math.Floor(cx) + ox, (int)Math.Floor(cz) + oz, out int n, out _) && n >= 4) around++;
            }
            return gapDensity <= 2 && around >= 2;
        }
    }
}
