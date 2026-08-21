using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VintageVisuals.Common;
using VintageVisuals.Common.Scene;

namespace VintageVisuals.Weather
{
    /// <summary>
    /// Phase 2: what the weather does to how the world looks.
    ///
    /// Owns no shader patch of its own. Rain changes how surfaces respond to
    /// light, and the material system already models that response - so wetness
    /// is published as one number and the PseudoPBR shader consumes it, rather
    /// than duplicating a lighting path to say the same thing twice.
    ///
    /// That is also why this reads as a weather effect at all. A wet surface is
    /// not "the same surface with rain drawn on it": it is smoother, more
    /// reflective and darker, and those are exactly the three inputs the
    /// microfacet model already takes.
    /// </summary>
    public sealed class WeatherSubsystem : IVisualSubsystem, IRenderer
    {
        public const string GroupName = "weather";

        private VintageVisualsModSystem _mod;

        private WeatherShaderBinder _binder;

        /// <summary>
        /// The game's own cloud placement. Owned here rather than by the shared
        /// environment tracker because it is not a fact about the world so much
        /// as a read of another renderer's internals - reflection into
        /// VintagestoryLib, which is exactly the kind of thing that should sit
        /// inside the subsystem that needs it and degrade there.
        /// </summary>
        private CloudTileReader _clouds;

        /// <summary>
        /// Accumulated cloud drift in cloud cells.
        ///
        /// The one piece of weather state this subsystem still owns, because it
        /// is not a fact about the world: it is the world's wind integrated at
        /// a speed the player chose. Everything factual - how hard it is
        /// raining, which way the wind blows, how wet things are - comes from
        /// the shared environment state.
        /// </summary>
        private readonly Vec2f _drift = new Vec2f();

        /// <summary>Seconds between reads of the game's cloud tiles.</summary>
        private const float CloudReadSeconds = 0.25f;

        private float _sinceCloudRead = CloudReadSeconds;

        private EnvironmentState World
        {
            get { return _mod == null || _mod.Environment == null ? EnvironmentState.Clear : _mod.Environment.Current; }
        }

        /// <summary>0 dry, 1 as wet as it gets. Read by PseudoPBR each frame.</summary>
        public float Wetness
        {
            get { return World.Wetness; }
        }

        /// <summary>
        /// How hard it is raining, 0..1, eased far faster than wetness.
        ///
        /// A separate value on purpose. Fog belongs to the rain that is falling
        /// now; wetness belongs to the rain that fell. Driving both from one
        /// number would leave the air thick with fog for a minute after the sky
        /// cleared, which is the wrong half of the effect to linger.
        /// </summary>
        public float Rain
        {
            get { return World.Rain; }
        }

        /// <summary>
        /// 0 clear, 1 overcast. Sets how much of the sky casts shadow.
        ///
        /// This is the ambient manager's own blended cloud density - the same
        /// number the game hands its cloud renderers - rather than the climate
        /// map's RainCloudOverlay, which is only the storm component and reads
        /// as clear on a normally cloudy day.
        /// </summary>
        public float CloudCover
        {
            get { return World.CloudCover; }
        }

        /// <summary>
        /// Accumulated cloud drift in cloud cells, so shadows move without the
        /// shader knowing the time.
        ///
        /// A vector rather than a scalar because it follows the world's actual
        /// wind. Vanilla's clouds are pushed by that same wind, so shadows
        /// crossing the ground the other way is a tell that they are not being
        /// cast by anything.
        /// </summary>
        public Vec2f CloudDrift
        {
            get { return _drift; }
        }

        /// <summary>The game's own cloud tiles, or an unavailable reader when they could not be found.</summary>
        public CloudTileReader Clouds
        {
            get { return _clouds; }
        }

        /// <summary>What arbitration allowed this subsystem to take. See SceneGrants.</summary>
        public SceneGrants Grants
        {
            get { return _mod == null || _mod.Environment == null ? SceneGrants.Full : _mod.Environment.Grants; }
        }

        /// <summary>0 at midnight, 1 at noon. From the shared state, so nothing here reads the clock twice.</summary>
        public float DayLight
        {
            get { return World.DayLight; }
        }

        /// <summary>
        /// Camera world position. The chunk shaders only have camera-relative
        /// coordinates, so without this cloud shadows slide along the ground
        /// with the player instead of staying where they fell.
        /// </summary>
        public Vec3f CameraOrigin
        {
            get { return World.CameraPosition; }
        }

        public WeatherConfig Config
        {
            get { return _mod == null ? new WeatherConfig() : _mod.ConfigManager.Config.Weather; }
        }

        public string Name => GroupName;

        public void Initialize(VintageVisualsModSystem mod)
        {
            _mod = mod;

            // Before, like ColorGrade: no GL work happens here, but the tick
            // has to be frame-paced rather than on a game timer so the easing
            // is smooth, and this is the stage that is guaranteed quiet.
            mod.Capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "vintagevisuals-weather");

            _clouds = new CloudTileReader(mod.Capi, mod.Mod.Logger);
            _binder = new WeatherShaderBinder(mod.Capi, this);
            mod.Capi.Event.RegisterRenderer(_binder, EnumRenderStage.Before, "vintagevisuals-weather-uniforms");
        }

        public double RenderOrder { get { return 0.0; } }

        public int RenderRange { get { return 0; } }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Before) return;
            if (_mod == null || _mod.Capi == null) return;

            WeatherConfig config = _mod.ConfigManager.Config.Weather;

            // Drying is a matter of taste rather than of physics, so the number
            // lives in this subsystem's config - but the wetness it governs is
            // shared state, so the tracker is told rather than asked.
            if (_mod.Environment != null)
            {
                _mod.Environment.DryingSeconds = config.DryingSeconds;

                // What this subsystem wants, before arbitration. The tracker
                // decides how much of it is actually available once colour
                // grading and the overcast term have had their say.
                PseudoPbrConfig pbr = _mod.ConfigManager.Config.PseudoPBR;
                _mod.Environment.Demand = new SceneDemand(
                    config.Enabled ? config.FogStrength : 0f,
                    config.Enabled ? config.CloudShadowStrength : 0f,
                    config.Enabled && pbr.Enabled ? config.OvercastStrength : 0f);
            }

            if (!config.Enabled) return;

            // Cells per minute along the world's own wind, kept as a plain
            // accumulator so the shader never needs a clock and the speed can
            // change without the shadows jumping - a new rate bends the path
            // from here on rather than teleporting the pattern.
            // A few times a second is plenty: the cloud tiles are rebuilt as
            // the player crosses one, which is 50 blocks.
            _sinceCloudRead += deltaTime;
            if (_sinceCloudRead >= CloudReadSeconds && config.CloudShadowStrength > 0.001f)
            {
                _sinceCloudRead = 0f;
                if (config.CloudsFromGame) _clouds.Update();
            }

            Vec2f wind = World.WindDirection;
            float step = deltaTime * config.CloudDriftSpeed / 60f;
            _drift.X += wind.X * step;
            _drift.Y += wind.Y * step;
        }

        /// <summary>
        /// Nothing to push. Wetness is pulled by PseudoPBR when it uploads its
        /// own uniforms, because it is an input to that shader rather than a
        /// subsystem of its own with something to draw.
        /// </summary>
        public void Apply()
        {
        }

        public void Dispose()
        {
            if (_mod?.Capi != null)
            {
                _mod.Capi.Event.UnregisterRenderer(this, EnumRenderStage.Before);

                if (_binder != null)
                {
                    _mod.Capi.Event.UnregisterRenderer(_binder, EnumRenderStage.Before);
                }
            }

            _binder = null;
            _clouds = null;
            _drift.X = 0f;
            _drift.Y = 0f;
            _mod = null;
        }

        void IDisposable.Dispose()
        {
            Dispose();
        }
    }
}
