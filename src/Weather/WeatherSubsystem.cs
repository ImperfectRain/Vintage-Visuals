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
        /// Seconds between climate samples.
        ///
        /// Climate lookups are not free and weather does not change in a
        /// hurry - the easing between samples is what the player sees, not the
        /// sampling itself.
        /// </summary>
        private const float SampleIntervalSeconds = 1.0f;

        private VintageVisualsModSystem _mod;
        private readonly WetnessTracker _wetness = new WetnessTracker();

        private float _sinceSample;
        private float _rainfall;
        private float _temperature = 20f;

        /// <summary>0 dry, 1 as wet as it gets. Read by PseudoPBR each frame.</summary>
        public float Wetness
        {
            get { return _wetness.Current; }
        }

        public string Name => GroupName;

        public void Initialize(VintageVisualsModSystem mod)
        {
            _mod = mod;

            // Before, like ColorGrade: no GL work happens here, but the tick
            // has to be frame-paced rather than on a game timer so the easing
            // is smooth, and this is the stage that is guaranteed quiet.
            mod.Capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "vintagevisuals-weather");
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

            _wetness.Step(WetnessTracker.TargetFor(_rainfall, _temperature), deltaTime, config.DryingSeconds);
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
            }

            _wetness.Reset();
            _mod = null;
        }

        void IDisposable.Dispose()
        {
            Dispose();
        }
    }
}
