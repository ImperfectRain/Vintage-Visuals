using System;
using Vintagestory.API.Client;
using VintageVisuals.Common;
using VintageVisuals.Common.Scene;

namespace VintageVisuals.Atmosphere
{
    /// <summary>
    /// The air between the camera and everything else.
    ///
    /// Present on a clear day at noon; weather modifies it. That ordering is
    /// the point of the subsystem existing at all. Before it, fog was something
    /// rain switched on inside two terrain shaders, so a valley filling with
    /// haze left the animals standing in it with crisp edges, and a future
    /// weather type would have had to add its own special case rather than
    /// inherit the rendering.
    ///
    /// Owns NO shader patch group at present. Everything it currently does is
    /// expressed through Vintage Story's own ambient stack, which reaches every
    /// shading program the game has - including the sky, the water and the
    /// entities, none of which this mod patches. Where vanilla already computes
    /// the thing, driving vanilla is not a shortcut; it is the only way to get
    /// an answer that is consistent across the frame.
    /// </summary>
    public sealed class AtmosphereSubsystem : IVisualSubsystem, IRenderer
    {
        public const string GroupName = "atmosphere";

        /// <summary>
        /// Before everything, alongside the environment tracker.
        ///
        /// The ambient stack is read by the game during its own frame setup, so
        /// a modifier written later than this would take effect one frame late.
        /// Nothing here touches GL, which is what makes this stage safe - see
        /// the note in CLAUDE.md about Use() off the render thread.
        /// </summary>
        private const double BeforeEverything = -1.0;

        private VintageVisualsModSystem _mod;
        private AmbientBridge _ambient;
        private AtmosphereShaderBinder _binder;

        private bool _registered;

        public string Name { get { return GroupName; } }

        /// <summary>The ambient bridge, so a test and the scene report can see what was written.</summary>
        public AmbientBridge Ambient { get { return _ambient; } }

        public void Initialize(VintageVisualsModSystem mod)
        {
            _mod = mod;
            if (mod == null || mod.Capi == null) return;

            _ambient = new AmbientBridge(mod.Capi, mod.Capi.Logger);

            // Registered unconditionally, like the environment tracker and for
            // the same reason: the renderer is what turns the feature OFF as
            // well as on. A subsystem that only registered while enabled would
            // leave its modifier sitting in the game's ambient stack the moment
            // the player unticked it.
            mod.Capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "vintagevisuals-atmosphere");

            // The uniform upload is a separate renderer from this one on
            // purpose. This one must run whether or not the patch group applied
            // - it is what switches height haze off as well as on - and the
            // binder must not, because a program that never got the uniforms
            // has nothing to say and should stay quiet rather than log.
            _binder = new AtmosphereShaderBinder(mod.Capi, mod);
            mod.Capi.Event.RegisterRenderer(_binder, EnumRenderStage.Before, "vintagevisuals-atmosphere-uniforms");

            _registered = true;
        }

        public void Apply()
        {
            // Nothing to push. There is no shader program to bind and no
            // uniform to upload: the config is read on the render tick, and
            // the one thing this subsystem writes is written there.
        }

        public double RenderOrder { get { return BeforeEverything; } }

        public int RenderRange { get { return 0; } }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Before) return;
            if (_mod == null || _mod.Capi == null || _ambient == null) return;

            try
            {
                AtmosphereConfig config = _mod.ConfigManager.Config.Atmosphere;
                EnvironmentTracker tracker = _mod.Environment;

                float wanted = config.Enabled ? config.HeightHaze : 0f;

                // Stated every frame even when zero. The arbiter has to know
                // the claim went to nothing, or an atmosphere that was switched
                // off would keep holding the haze allowance it was granted on
                // the frame before it was.
                if (tracker != null) tracker.DemandFromAtmosphere(wanted);

                if (wanted <= 0f)
                {
                    _ambient.SetHeightHaze(0f, 0f);
                    return;
                }

                // The GRANT, not the config. Two subsystems put air between the
                // camera and the world and only one budget pays for it.
                float granted = tracker == null ? 1f : tracker.Grants.HeightHaze;
                float pressure = tracker == null
                    ? 0f
                    : SceneArbiter.HazePressure(tracker.Current);

                float seaLevel = _mod.Capi.World == null ? 0f : _mod.Capi.World.SeaLevel;

                _ambient.SetHeightHaze(wanted * granted * pressure, seaLevel);
            }
            catch (Exception e)
            {
                _mod.Capi.Logger.Warning("[VintageVisuals] atmosphere: " + e.Message +
                    ". Height haze is off for this frame; the rest of the frame is unaffected.");
            }
        }

        public void Dispose()
        {
            // The modifier comes OUT of the game's stack. Leaving a zeroed
            // entry behind would be invisible right up until someone wondered
            // why an uninstalled mod was still in the ambient list.
            if (_ambient != null) _ambient.Remove();

            if (_registered && _mod != null && _mod.Capi != null)
            {
                _mod.Capi.Event.UnregisterRenderer(this, EnumRenderStage.Before);
                if (_binder != null) _mod.Capi.Event.UnregisterRenderer(_binder, EnumRenderStage.Before);
            }

            _binder = null;

            _registered = false;
            _ambient = null;
            _mod = null;
        }
    }
}
