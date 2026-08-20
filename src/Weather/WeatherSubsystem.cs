using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VintageVisuals.Common;

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

        /// <summary>
        /// The volumetric clouds are a separate program sharing nothing with
        /// the terrain patches, so they get their own group: a reworded line in
        /// one should not switch off the other.
        /// </summary>
        public const string CloudGroupName = "cloudshape";

        /// <summary>
        /// Seconds between climate samples.
        ///
        /// Climate lookups are not free and weather does not change in a
        /// hurry - the easing between samples is what the player sees, not the
        /// sampling itself.
        /// </summary>
        private const float SampleIntervalSeconds = 1.0f;

        private VintageVisualsModSystem _mod;
        private readonly WetnessTracker _wetness = new WetnessTracker();

        private readonly WetnessTracker _rain = new WetnessTracker();

        private WeatherShaderBinder _binder;
        private float _sinceSample;
        private float _rainfall;
        private float _temperature = 20f;
        private float _cloudCover = 0.35f;
        private float _drift;
        private Vec3f _cameraOrigin = new Vec3f();

        /// <summary>0 dry, 1 as wet as it gets. Read by PseudoPBR each frame.</summary>
        public float Wetness
        {
            get { return _wetness.Current; }
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
            get { return _rain.Current; }
        }

        /// <summary>0 clear, 1 overcast. Sets how much of the sky casts shadow.</summary>
        public float CloudCover
        {
            get { return _cloudCover; }
        }

        /// <summary>Accumulated cloud drift, so shadows move without the shader knowing the time.</summary>
        public float CloudDrift
        {
            get { return _drift; }
        }

        /// <summary>
        /// Camera world position. The chunk shaders only have camera-relative
        /// coordinates, so without this cloud shadows slide along the ground
        /// with the player instead of staying where they fell.
        /// </summary>
        public Vec3f CameraOrigin
        {
            get { return _cameraOrigin; }
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

            _binder = new WeatherShaderBinder(mod.Capi, this);
            mod.Capi.Event.RegisterRenderer(_binder, EnumRenderStage.Opaque, "vintagevisuals-weather-uniforms");
        }

        public double RenderOrder { get { return 0.0; } }

        public int RenderRange { get { return 0; } }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Before) return;
            if (_mod == null || _mod.Capi == null) return;

            WeatherConfig config = _mod.ConfigManager.Config.Weather;

            if (!config.Enabled)
            {
                // Ease back to dry rather than snapping, so switching the
                // subsystem off mid-storm is not a visible jolt.
                _wetness.Step(0f, deltaTime, config.DryingSeconds);
                return;
            }

            _sinceSample += deltaTime;
            if (_sinceSample >= SampleIntervalSeconds)
            {
                _sinceSample = 0f;
                SampleClimate();
            }

            float target = WetnessTracker.TargetFor(_rainfall, _temperature);

            _wetness.Step(target, deltaTime, config.DryingSeconds);

            // Rain itself settles in seconds either way. Only the surface stays
            // wet afterwards.
            _rain.Step(target, deltaTime, WetnessTracker.WettingSeconds);

            // Cells per minute, kept as a plain accumulator so the shader never
            // needs a clock and the speed can change without the clouds jumping.
            _drift += deltaTime * config.CloudDriftSpeed / 60f;

            IClientPlayer trackedPlayer = _mod.Capi.World?.Player;
            if (trackedPlayer?.Entity != null)
            {
                _cameraOrigin.Set((float)trackedPlayer.Entity.Pos.X,
                                  (float)trackedPlayer.Entity.Pos.Y,
                                  (float)trackedPlayer.Entity.Pos.Z);
            }
        }

        /// <summary>
        /// Reads rainfall and temperature where the player is standing.
        ///
        /// Temperature matters as much as rainfall: below freezing the same
        /// precipitation falls as snow, and a snowstorm making the ground look
        /// rained on would be the wrong effect at exactly the moment the player
        /// is most likely to be looking at the sky.
        /// </summary>
        private void SampleClimate()
        {
            try
            {
                IClientPlayer player = _mod.Capi.World?.Player;
                if (player?.Entity == null) return;

                BlockPos pos = player.Entity.Pos.AsBlockPos;
                ClimateCondition climate = _mod.Capi.World.BlockAccessor.GetClimateAt(pos);
                if (climate == null) return;

                _rainfall = climate.Rainfall;
                _temperature = climate.Temperature;

                // RainCloudOverlay is the game's own cloud cover, so shadows on
                // the ground agree with the sky the player is looking at rather
                // than being an unrelated noise field that happens to move.
                _cloudCover = GameMath.Clamp(climate.RainCloudOverlay, 0f, 1f);
            }
            catch (Exception ex)
            {
                // A climate lookup that throws must cost the wetness effect,
                // not the frame. Sampling stops updating and the surface dries
                // out, which is the safe direction to fail in.
                _mod.Mod.Logger.Warning("[VintageVisuals] weather: climate lookup failed, so wetness will " +
                                        "stop tracking the weather: " + ex.Message);
                _rainfall = 0f;
            }
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
                    _mod.Capi.Event.UnregisterRenderer(_binder, EnumRenderStage.Opaque);
                }
            }

            _binder = null;

            _wetness.Reset();
            _rain.Reset();
            _mod = null;
        }

        void IDisposable.Dispose()
        {
            Dispose();
        }
    }
}
