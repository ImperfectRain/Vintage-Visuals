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

            ReportIfInert(mod);
        }

        /// <summary>
        /// Says so when the subsystem is switched on and every one of its
        /// effects is at zero.
        ///
        /// THIS SHIPPED FOR MONTHS AND NOTHING SAID A WORD. Atmosphere defaulted
        /// to Enabled = true with all eleven strengths at 0.0, so it registered
        /// two renderers, held a slot in the game's ambient stack, uploaded
        /// twenty-two uniforms and changed nothing whatsoever. The log said the
        /// patch group applied, which was true, and the player saw no
        /// atmosphere, which was also true, and there was no line anywhere
        /// connecting the two.
        ///
        /// "Off means off" is a rule this project already enforces in several
        /// places. This is its converse and it had no guard at all: a subsystem
        /// reporting itself ON while contributing nothing is a worse lie than
        /// one reporting itself off, because it invites the player to blame the
        /// implementation for a number.
        /// </summary>
        private static void ReportIfInert(VintageVisualsModSystem mod)
        {
            AtmosphereConfig config = mod.ConfigManager == null ? null : mod.ConfigManager.Config.Atmosphere;
            if (config == null || !config.Enabled) return;

            if (config.WantsShader || config.HeightHaze > 0f) return;

            mod.Capi.Logger.Warning(
                "[VintageVisuals] atmosphere: enabled, and every one of its effects is at zero - " +
                "it will have NO visible effect at all. This is a configuration state, not a " +
                "failure: nothing is broken and no patch has failed. Raise any of the strengths " +
                "in the config or the F7 panel, or switch the subsystem off so it stops " +
                "registering renderers it has no work for.");
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
