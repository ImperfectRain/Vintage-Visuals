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
        /// How far out creatures count toward proximity.
        ///
        /// Roughly the distance at which something becomes the player's problem
        /// and comfortably inside the range at which heavy weather starts
        /// hiding things.
        /// </summary>
        private const float ProximityRangeBlocks = 24f;

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
        private float _proximity;
        private float _autumn;
        private float _winter;
        private readonly Vec3f _camera = new Vec3f();

        private bool _reportedFirstSample;

        /// <summary>
        /// Advanced in double and handed over wrapped, for the same reason the
        /// camera position is.
        /// </summary>
        private double _rippleClock;
        private double _breezeClock;

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

        /// <summary>
        /// A much slower clock, wrapped to 0..1, for things that move at the
        /// speed leaves do rather than the speed raindrops do.
        ///
        /// Separate because the ripple clock turns over every 1.5 seconds, and
        /// reusing it for canopy dapple made every sunfleck complete a full
        /// cycle in that time - which read, accurately, as "a fast paced
        /// rotating shine". Leaves shift on the order of tens of seconds.
        ///
        /// Wrapped for the same reason everything here is wrapped: an unbounded
        /// float32 clock stops resolving phases at all past about 1e7, and
        /// anything reading it must be periodic in it so the wrap is invisible.
        /// </summary>
        public float BreezeClock { get; private set; }

        /// <summary>Seconds for one full ripple lifetime at the slowest per-cell rate.</summary>
        private const double RippleSeconds = 1.5;

        /// <summary>Seconds for one turn of the breeze clock.</summary>
        private const double BreezeSeconds = 26.0;

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

        /// <summary>
        /// What each subsystem wants this tick, pushed in from config before
        /// arbitration runs. Config-scaled values, so they stay out of
        /// EnvironmentState by the same rule everything else does.
        /// </summary>
        public SceneDemand Demand { get; set; } = new SceneDemand(0f, 0f, 0f);

        /// <summary>What the scene needs, rebuilt every tick from the state above.</summary>
        public SceneIntent Intent { get; private set; } = new SceneIntent();

        /// <summary>
        /// What each subsystem is allowed to take, after arbitration.
        ///
        /// Read this rather than the config value: the whole point is that three
        /// subsystems all removing light on the same rainy afternoon get shares
        /// of one allowance rather than a free hand each.
        /// </summary>
        public SceneGrants Grants { get; private set; } = SceneGrants.Full;

        /// <summary>The claim record behind Grants, for the log and for reports.</summary>
        public VisualBudget Budget { get; private set; }

        public double RenderOrder { get { return BeforeEverything; } }

        public int RenderRange { get { return 0; } }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Before) return;

            // EVERY FRAME, ahead of the tick gate, and this is not an
            // optimisation detail - it is what keeps every world-anchored field
            // anchored.
            //
            // A shader only ever sees camera-relative coordinates, so anything
            // that must stay put on the ground reconstructs a world position as
            // cameraRelativePos + CameraPosition. Sampling the camera on the
            // 0.1s tick left that second term stale while the first changed
            // every frame, so the sum drifted with the player and snapped back
            // ten times a second. Rain ripples showed it first and worst -
            // they are the finest field this mod draws, and they visibly swam
            // across the ground as you walked.
            //
            // Two doubles and a modulo. EnvironmentState is a readonly struct,
            // so rebuilding it costs a stack copy and no allocation.
            SampleCamera();
            Current = BuildState();

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
                SampleProximity();

                IClientPlayer seasonPlayer = _capi.World?.Player;
                if (seasonPlayer?.Entity != null) SampleSeason(seasonPlayer.Entity.Pos.AsBlockPos);
            }

            SampleObserver();
            SampleSky();

            _rippleClock = (_rippleClock + deltaSeconds / RippleSeconds) % 1.0;
            RippleClock = (float)_rippleClock;

            // Advanced by the WIND, not by the wall clock. Leaves are still in
            // still air and thrash in a gust, and the flecks they let through do
            // the same - so the rate is the world's own wind speed rather than a
            // constant nobody can tie to anything on screen. A floor, because
            // dead calm that never moves at all reads as a frozen texture.
            double gust = 0.35 + 1.65 * GameMath.Clamp(_windSpeed, 0f, 1f);
            _breezeClock = (_breezeClock + deltaSeconds * gust / BreezeSeconds) % 1.0;
            BreezeClock = (float)_breezeClock;

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

            EnvironmentState next = BuildState();
            Current = next;

            // Intent and arbitration run here, once, at one cadence. Letting
            // each subsystem claim from its own renderer looks tidier and does
            // not work - they tick at different rates and stages, so whoever ran
            // first would exhaust the budget and the rest would collapse to
            // nothing on most frames.
            Intent = SceneIntentBuilder.Build(next);

            Grants = SceneArbiter.Arbitrate(Intent, next, Demand, out VisualBudget budget);
            Budget = budget;

            ReportArbitration();
        }

        private string _reportedIntent;

        /// <summary>
        /// Says what the scene asked for and what it was allowed, when that
        /// meaningfully changes.
        ///
        /// Cheap insurance against the failure this whole layer exists to stop:
        /// three subsystems quietly agreeing to remove all the colour from a
        /// rainy afternoon, each of them individually reasonable, with nothing
        /// anywhere saying so.
        /// </summary>
        private void ReportArbitration()
        {
            string summary = Intent.Describe();
            if (summary == _reportedIntent) return;

            _reportedIntent = summary;
            _logger.Debug("[VintageVisuals] scene: " + summary + " | " + Budget.Describe());
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

        /// <summary>
        /// How much company the player has, 0..1.
        ///
        /// Sampled on the slow tick with the climate: an entity query is the
        /// second most expensive thing this file does and creatures do not
        /// cross twenty blocks in under a second.
        ///
        /// Counts living non-player creatures rather than deciding which are
        /// hostile. Vintage Story has no authoritative "is this dangerous"
        /// flag, and a hand-maintained list of creature codes would be wrong for
        /// every mod that adds one - which is exactly the kind of invented
        /// answer this project avoids. The cost of being imprecise is a little
        /// less fog around a deer.
        /// </summary>
        private void SampleProximity()
        {
            try
            {
                IClientPlayer player = _capi.World?.Player;
                if (player?.Entity == null) return;

                Entity[] nearby = _capi.World.GetEntitiesAround(
                    player.Entity.Pos.XYZ, ProximityRangeBlocks, ProximityRangeBlocks,
                    e => e != null && e.Alive && !(e is EntityPlayer) && e is EntityAgent);

                int count = nearby == null ? 0 : nearby.Length;

                // Saturates fast. One creature nearby is most of the signal;
                // the difference between three and eight does not change what
                // the player needs to be able to see.
                _proximity = GameMath.Clamp(count / 2f, 0f, 1f);
            }
            catch (Exception)
            {
                // Held at the last reading. An entity query that starts throwing
                // is a reason to stop tracking, not a reason to claim the player
                // is alone.
            }
        }

        /// <summary>
        /// How far into autumn and winter the world is, from the game's answer.
        ///
        /// GetSeason is per-position and hemisphere-aware, so it is asked at the
        /// player rather than assumed from the day of the year - southern
        /// hemisphere autumn is northern hemisphere spring, and a mod that got
        /// that backwards would be wrong for half of every world.
        ///
        /// Blended with GetSeasonRel rather than used as a hard flag, because a
        /// material response that switched on the instant a season ticked over
        /// would be a visible pop on a day nothing else changed. The discrete
        /// season decides WHICH lane; the relative position decides how far in.
        /// </summary>
        private EnumSeason? _reportedSeason;

        private void SampleSeason(BlockPos pos)
        {
            try
            {
                var calendar = _capi.World?.Calendar;
                if (calendar == null) return;

                EnumSeason season = calendar.GetSeason(pos);
                float rel = GameMath.Clamp(calendar.GetSeasonRel(pos), 0f, 1f);

                // Rel runs across the whole year, so the position WITHIN the
                // current season is what a fade wants. Quarters, because there
                // are four seasons and the game divides the year evenly.
                float within = GameMath.Clamp((rel * 4f) % 1f, 0f, 1f);

                // Rises through the first half of its season and falls through
                // the second, so neighbouring seasons hand over rather than
                // switching.
                float depth = 1f - Math.Abs(within - 0.5f) * 2f;

                _autumn = season == EnumSeason.Fall ? depth : 0f;
                _winter = season == EnumSeason.Winter ? depth : 0f;

                // The one assumption here that cannot be checked without the
                // game running: that the season the calendar names and the
                // quarter GetSeasonRel falls in are the same quarter. If the
                // year does not start where this expects, `within` is measured
                // from the wrong boundary and `depth` peaks at a season change
                // rather than mid-season - inverted, not merely offset.
                //
                // Logged on every season change rather than asserted, because
                // being wrong about it is not a crash and the log is where a
                // player can see it. Expect depth near 0 at a handover.
                if (season != _reportedSeason)
                {
                    _reportedSeason = season;
                    _capi.Logger.Notification("[VintageVisuals] scene: season " + season +
                        ", year " + rel.ToString("0.###") + ", " + within.ToString("0.###") +
                        " through it, depth " + depth.ToString("0.###") +
                        ". Depth should be near zero right now and rise to one at midseason; if it " +
                        "is near one at a handover, the year does not start where this assumes.");
                }
            }
            catch (Exception)
            {
                // Held at the last reading, as everywhere else here.
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

        /// <summary>
        /// The current state as one value, from whatever the samplers last put
        /// in the fields behind it.
        ///
        /// Called every frame as well as every tick, because the camera moves
        /// every frame and everything else is happy to be a tenth of a second
        /// stale.
        /// </summary>
        private EnvironmentState BuildState()
        {
            return new EnvironmentState(
                _dayLight, _moonLight, _cloudCover,
                _wind, _windSpeed,
                _precipitation, _rain.Current, _snow.Current, _wetness.Current,
                _temperature, _humidity,
                _skyExposure, _depth, _underwater,
                _camera, _proximity, _autumn, _winter);
        }

        /// <summary>
        /// Where the camera is, wrapped, this frame.
        ///
        /// Split out of SampleObserver so it can run every frame while the
        /// light-level lookup beside it - a chunk query, and the expensive half
        /// - stays on the tick. See OnRenderFrame for why the split matters.
        /// </summary>
        private void SampleCamera()
        {
            try
            {
                EntityPos position = _capi.World?.Player?.Entity?.Pos;
                if (position == null) return;

                // Wrapped in double, before the value ever becomes a float.
                // See EnvironmentState.CameraPosition for why this is not
                // tidiness.
                _camera.Set((float)Wrap(position.X), (float)position.Y, (float)Wrap(position.Z));
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
            BreezeClock = 0f;
            Current = EnvironmentState.Clear;
        }
    }
}
