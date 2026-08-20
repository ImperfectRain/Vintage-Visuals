using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using VintageVisuals.Weather;

namespace VintageVisuals.Common.Scene
{
    /// <summary>
    /// The one place the mod asks the game what is going on.
    ///
    /// Runs before every subsystem, publishes an <see cref="EnvironmentState"/>,
    /// and is the only file outside a subsystem that touches the world API. Two
    /// things follow from that and both are the point:
    ///
    ///  - When the API moves under us, it moves under one file.
    ///  - Two subsystems cannot disagree about the weather, because there is
    ///    only one answer to disagree with.
    ///
    /// It runs whether or not any subsystem is enabled. State is what the world
    /// is doing, not what the player asked for, and a tracker that switched
    /// itself off with Weather would leave colour grading unable to tell it was
    /// raining.
    ///
    /// Every lookup is guarded and every failure keeps the LAST GOOD value
    /// rather than resetting. A climate query that starts throwing is a reason
    /// to stop tracking, not a reason to claim the player has been teleported
    /// to a temperate plain.
    /// </summary>
    public sealed class EnvironmentTracker : IRenderer
    {
        /// <summary>
        /// Before everything. ColorGrade and Weather both tick at 0.0, and both
        /// have to see this frame's state rather than last frame's.
        /// </summary>
        private const double BeforeEverything = -1.0;

        /// <summary>
        /// Seconds between climate lookups.
        ///
        /// The one expensive query here, and biomes are not crossed in under a
        /// second. Everything else is a field read or a light lookup and is
        /// sampled every tick alongside it.
        /// </summary>
        private const float ClimateIntervalSeconds = 1.0f;

        /// <summary>Ticks per second. Fast enough that easing is smooth, slow enough to cost nothing.</summary>
        private const float TickSeconds = 0.1f;

        /// <summary>
        /// Blocks below sea level at which "underground" is at full strength.
        /// A cellar barely registers; a real cave system is well into it.
        /// </summary>
        private const float FullDepthBlocks = 45f;

        /// <summary>
        /// BlendedCloudDensity is the game's density parameter, not a fraction
        /// of sky covered, and it sits low - so read raw as coverage it reports
        /// nearly clear under a sky full of cloud. Gained once, here, so there
        /// is one copy of this number in the codebase rather than one per
        /// consumer.
        /// </summary>
        private const float CloudCoverGain = 2.0f;

        private readonly ICoreClientAPI _capi;
        private readonly ILogger _logger;

        private readonly WetnessTracker _wetness = new WetnessTracker();
        private readonly WetnessTracker _rain = new WetnessTracker();
        private readonly WetnessTracker _snow = new WetnessTracker();

        private float _sinceTick;
        private float _sinceClimate = ClimateIntervalSeconds;

        private float _dayLight = 1f;
        private float _moonLight;
        private float _cloudCover;
        private readonly Vec2f _wind = new Vec2f(1f, 0f);
        private float _windSpeed;
        private float _precipitation;
        private float _temperature = EnvironmentState.TemperateCelsius;
        private float _humidity = 0.5f;
        private float _skyExposure = 1f;
        private float _depth;
        private float _underwater;
        private readonly Vec3f _camera = new Vec3f();

        private bool _reportedFirstSample;

        /// <summary>
        /// Advanced in double and handed over wrapped, for the same reason the
        /// camera position is.
        /// </summary>
        private double _rippleClock;

        public EnvironmentTracker(ICoreClientAPI capi, ILogger logger)
        {
            _capi = capi;
            _logger = logger;
        }

        /// <summary>
        /// Animation clock for the rain ripples, already wrapped to 0..1.
        ///
        /// Wrapped here rather than in the shader because a shader can only
        /// wrap what it can still resolve. Vanilla's windWaveCounter was the
        /// obvious clock and is the same trap as the world coordinate: it
        /// accumulates without bound, and past about ten million a float32
        /// cannot separate two phases at all - so every drop in the world lands
        /// on the same frame. Which is what happened.
        /// </summary>
        public float RippleClock { get; private set; }

        /// <summary>Seconds for one full ripple lifetime at the slowest per-cell rate.</summary>
        private const double RippleSeconds = 1.5;

        /// <summary>
        /// The world as of the last tick. Never null, never uninitialised: it
        /// reads as a clear temperate noon until the first sample lands.
        /// </summary>
        public EnvironmentState Current { get; private set; } = EnvironmentState.Clear;

        /// <summary>
        /// How long a wet surface takes to dry, in seconds.
        ///
        /// Reaches in from Weather's config because drying is the one part of
        /// the model that is a matter of taste rather than of physics. Set by
        /// the mod system each tick rather than read from here, so this class
        /// stays free of the config graph.
        /// </summary>
        public float DryingSeconds { get; set; } = 60f;

        public double RenderOrder { get { return BeforeEverything; } }

        public int RenderRange { get { return 0; } }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Before) return;

            _sinceTick += deltaTime;
            if (_sinceTick < TickSeconds) return;

            Tick(_sinceTick);
            _sinceTick = 0f;
        }

        /// <summary>
        /// Advances the world state by one tick. Public so tools/smoketest can
        /// drive the easing without a client.
        /// </summary>
        public void Tick(float deltaSeconds)
        {
            _sinceClimate += deltaSeconds;
            if (_sinceClimate >= ClimateIntervalSeconds)
            {
                _sinceClimate = 0f;
                SampleClimate();
            }

            SampleObserver();
            SampleSky();

            _rippleClock = (_rippleClock + deltaSeconds / RippleSeconds) % 1.0;
            RippleClock = (float)_rippleClock;

            // Rain and snow are the same precipitation seen through the
            // thermometer. Below freezing it falls as snow, and a snowstorm
            // making the ground look rained on would be the wrong effect at
            // exactly the moment the player is most likely to be looking at it.
            float wetTarget = WetnessTracker.TargetFor(_precipitation, _temperature);
            float snowTarget = WetnessTracker.SnowTargetFor(_precipitation, _temperature);

            // Rain settles in seconds either way; only the surface stays wet
            // afterwards. Two trackers rather than one because fog and ripples
            // belong to the rain that is falling and wetness belongs to the
            // rain that fell, and driving both off one number leaves the air
            // thick with fog for a minute after the sky cleared.
            _rain.Step(wetTarget, deltaSeconds, WetnessTracker.WettingSeconds);
            _snow.Step(snowTarget, deltaSeconds, WetnessTracker.WettingSeconds);
            _wetness.Step(wetTarget, deltaSeconds, DryingSeconds);

            Current = new EnvironmentState(
                _dayLight, _moonLight, _cloudCover,
                _wind, _windSpeed,
                _precipitation, _rain.Current, _snow.Current, _wetness.Current,
                _temperature, _humidity,
                _skyExposure, _depth, _underwater,
                _camera);
        }

        private void SampleClimate()
        {
            try
            {
                IClientPlayer player = _capi.World?.Player;
                if (player?.Entity == null) return;

                BlockPos pos = player.Entity.Pos.AsBlockPos;

                ClimateCondition climate = _capi.World.BlockAccessor.GetClimateAt(pos);
                if (climate != null)
                {
                    _precipitation = GameMath.Clamp(climate.Rainfall, 0f, 1f);
                    _temperature = climate.Temperature;
                    _humidity = GameMath.Clamp(climate.WorldgenRainfall, 0f, 1f);
                }

                Vec3d wind = _capi.World.BlockAccessor.GetWindSpeedAt(pos);
                if (wind != null)
                {
                    float x = (float)wind.X;
                    float z = (float)wind.Z;
                    _windSpeed = MathF.Sqrt(x * x + z * z);

                    // Below a breath of wind the direction is numerical noise,
                    // and anything driven by it jitters in place. Hold the last
                    // heading instead.
                    if (_windSpeed >= 0.02f)
                    {
                        _wind.X = x / _windSpeed;
                        _wind.Y = z / _windSpeed;
                    }
                }

                if (!_reportedFirstSample)
                {
                    _reportedFirstSample = true;
                    _logger.Notification("[VintageVisuals] environment: first sample - daylight " +
                        _dayLight.ToString("0.##") + ", cloud cover " + _cloudCover.ToString("0.##") +
                        ", precipitation " + _precipitation.ToString("0.##") +
                        ", " + _temperature.ToString("0.#") + "C, humidity " + _humidity.ToString("0.##") +
                        ", sky exposure " + _skyExposure.ToString("0.##"));
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("[VintageVisuals] environment: climate lookup failed, holding the last " +
                                "reading: " + ex.Message);
            }
        }

        private void SampleSky()
        {
            try
            {
                var calendar = _capi.World?.Calendar;
                if (calendar != null)
                {
                    _dayLight = GameMath.Clamp(calendar.DayLightStrength, 0f, 1f);

                    // Scaled by how dark it is, because the moon contributes
                    // nothing to a scene the sun is already lighting.
                    _moonLight = GameMath.Clamp(calendar.MoonPhaseBrightness, 0f, 1f) * (1f - _dayLight);
                }

                if (_capi.Ambient != null)
                {
                    _cloudCover = GameMath.Clamp(_capi.Ambient.BlendedCloudDensity * CloudCoverGain, 0f, 1f);
                }
            }
            catch (Exception)
            {
                // Held at the last reading, as everywhere else here.
            }
        }

        private void SampleObserver()
        {
            try
            {
                IClientPlayer player = _capi.World?.Player;
                if (player?.Entity == null) return;

                EntityPos position = player.Entity.Pos;

                // Wrapped in double, before the value ever becomes a float.
                // See EnvironmentState.CameraPosition for why this is not
                // tidiness.
                _camera.Set((float)Wrap(position.X), (float)position.Y, (float)Wrap(position.Z));

                BlockPos pos = position.AsBlockPos;

                // OnlySunLight, not MaxTimeOfDayLight. The two ask different
                // questions: eye adaptation wants to know how bright it is here
                // right now and would happily count a torch, while this wants
                // to know whether the player is indoors - and a torch is
                // evidence of nothing.
                int sun = _capi.World.BlockAccessor.GetLightLevel(pos, EnumLightLevelType.OnlySunLight);
                _skyExposure = GameMath.Clamp(sun / (float)Math.Max(1, _capi.World.SunBrightness), 0f, 1f);

                _depth = GameMath.Clamp((_capi.World.SeaLevel - pos.Y) / FullDepthBlocks, 0f, 1f);

                _underwater = player.Entity.Swimming ? 1f : 0f;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Positive modulo, so the wrap behaves the same either side of the world origin.</summary>
        private static double Wrap(double v)
        {
            double period = EnvironmentState.CameraPeriod;
            double wrapped = v % period;
            return wrapped < 0 ? wrapped + period : wrapped;
        }

        public void Dispose()
        {
            _wetness.Reset();
            _rain.Reset();
            _snow.Reset();
            _rippleClock = 0;
            RippleClock = 0f;
            Current = EnvironmentState.Clear;
        }
    }
}
