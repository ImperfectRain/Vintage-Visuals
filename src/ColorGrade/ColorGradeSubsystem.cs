using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VintageVisuals.Common;

namespace VintageVisuals.ColorGrade
{
    /// <summary>
    /// Uploads the color grading uniforms into the vanilla "final" shader
    /// program.
    ///
    /// No per-frame work: GL uniform values are per-program state that persists
    /// until the program is relinked, so pushing them once whenever they change
    /// is enough. That avoids putting this mod in the render hot path at all,
    /// which is the main reason it does not hook the render loop.
    /// </summary>
    public sealed class ColorGradeSubsystem : IVisualSubsystem, IRenderer
    {
        /// <summary>Matches the shaderpatches group name and the config section name.</summary>
        public const string GroupName = "colorgrade";

        /// <summary>Vanilla program that composes the final image. Patched by colorgrade.yaml.</summary>
        private const string TargetProgramName = "final";

        /// <summary>
        /// The master uniform. Its presence is also how we detect that the GLSL
        /// injection actually reached the compiled program.
        /// </summary>
        private const string EnabledUniform = "vv_enabled";

        /// <summary>Eye-adaptation multiplier, updated on a timer rather than on config change.</summary>
        private const string AdaptationUniform = "vv_adaptation";

        private VintageVisualsModSystem _mod;

        private readonly AdaptiveExposure _adaptation = new AdaptiveExposure();
        private float _lastUploadedAdaptation = -1f;

        /// <summary>
        /// Set by Apply(), consumed by the next frame.
        ///
        /// Apply() runs from config changes, and a config change can arrive
        /// from anywhere — including ConfigLib's GUI, mid-frame, with the gui
        /// shader bound. Calling IShaderProgram.Use() then throws
        /// "Already a different shader (gui) in use!" and leaves OpenGL in a
        /// state the client never recovers from: the log fills with
        /// InvalidOperation and the world stops drawing correctly. So config
        /// changes only set a flag, and every GL call this subsystem makes
        /// happens on the render thread at a stage where nothing else holds a
        /// program.
        /// </summary>
        private bool _uniformsDirty = true;

        /// <summary>Seconds since adaptation was last stepped, to keep the old 10Hz cost profile.</summary>
        private float _sinceAdaptationStep;

        /// <summary>
        /// 100ms. Adaptation is measured in seconds, so ticking faster buys
        /// nothing and puts a block lookup plus a uniform upload on a hotter
        /// path than it needs to be.
        /// </summary>
        private const int AdaptationTickMs = 100;

        // Apply() runs on every config change and shader reload; these keep a
        // recurring problem to one log line instead of one per reload. They are
        // cleared whenever the condition clears, so a problem that comes back
        // after being fixed is reported again rather than staying silent.
        private bool _warnedMissingProgram;
        private bool _warnedMissingUniform;

        public string Name => GroupName;

        public void Initialize(VintageVisualsModSystem mod)
        {
            _mod = mod;

            // EnumRenderStage.Before is the one point in the frame the game has
            // not yet bound a program for anything. Uniform values are
            // per-program state that survives until the program is relinked, so
            // this does no per-frame work beyond an early return once the
            // config has settled and adaptation has converged.
            mod.Capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "vintagevisuals-colorgrade");
        }

        /// <summary>0 = drawn first. Nothing else should hold a shader program at this point.</summary>
        public double RenderOrder { get { return 0.0; } }

        public int RenderRange { get { return 0; } }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Before) return;
            if (_mod == null || _mod.Capi == null) return;

            // Belt and braces. Use() throws rather than queues if a program is
            // already bound, and that throw is not recoverable in practice —
            // it has already cost this project a session of broken rendering.
            // If anything is bound at this stage, skip the frame; the dirty
            // flag survives and the next frame tries again.
            if (_mod.Capi.Render.CurrentActiveShader != null) return;

            _sinceAdaptationStep += deltaTime;
            if (_sinceAdaptationStep >= AdaptationTickMs / 1000f)
            {
                StepAdaptation(_sinceAdaptationStep);
                _sinceAdaptationStep = 0f;
            }

            if (_uniformsDirty) UploadUniforms();
        }

        /// <summary>
        /// Advances eye adaptation and pushes it when it has actually moved.
        ///
        /// This is the one recurring cost the subsystem has. It is kept to a
        /// single block-light lookup, and the uniform is only uploaded when the
        /// value changed by enough to see — once adaptation settles, this
        /// becomes a lookup and an early return.
        /// </summary>
        private void StepAdaptation(float deltaSeconds)
        {
            AdaptiveExposureConfig config = _mod.ConfigManager.Config.AdaptiveExposure;

            float target;
            if (!config.Enabled || !_mod.ConfigManager.Config.ColorGrade.Enabled)
            {
                // Ease back to neutral rather than snapping, so toggling the
                // feature off mid-session is not a visible jolt.
                target = 1.0f;
            }
            else
            {
                target = AdaptiveExposure.TargetFor(SampleNormalisedLight(), config.DarkGain, config.BrightGain);
            }

            float adaptation = _adaptation.Step(target, deltaSeconds, config.BrightenSeconds, config.DarkenSeconds);

            if (Math.Abs(adaptation - _lastUploadedAdaptation) < 1e-3f) return;

            IShaderProgram program = ResolveFinalProgram();
            if (program == null || !program.HasUniform(AdaptationUniform)) return;

            // Safe here: the caller has already established that this is the
            // render thread at a stage with no program bound.
            program.Use();
            program.Uniform(AdaptationUniform, adaptation);
            program.Stop();

            _lastUploadedAdaptation = adaptation;
        }

        /// <summary>
        /// Light level where the player's head is, normalised to 0..1.
        ///
        /// MaxTimeOfDayLight rather than raw sunlight: it combines block light
        /// with sunlight scaled by time of day, which is what "how bright is it
        /// here, right now" actually means. Raw sunlight would report a bright
        /// surface at midnight.
        /// </summary>
        private float SampleNormalisedLight()
        {
            IClientPlayer player = _mod.Capi.World?.Player;
            if (player?.Entity == null) return 1.0f;

            BlockPos pos = player.Entity.Pos.AsBlockPos;
            int level = _mod.Capi.World.BlockAccessor.GetLightLevel(pos, EnumLightLevelType.MaxTimeOfDayLight);
            return AdaptiveExposure.NormaliseLightLevel(level);
        }

        /// <summary>
        /// Finds the vanilla "final" program.
        ///
        /// <see cref="IShaderAPI.GetProgramByName"/> does NOT find it — that
        /// lookup covers name-registered programs, which in practice means ones
        /// mods registered, and the vanilla passes are addressed by their
        /// <see cref="EnumShaderProgram"/> id instead. Calling only the by-name
        /// overload is what produced "no shader program named 'final'" in the
        /// log while the shader itself was patched perfectly well.
        ///
        /// The by-name call is kept as a fallback purely in case a future
        /// version starts registering the vanilla passes by name too.
        /// </summary>
        private IShaderProgram ResolveFinalProgram()
        {
            return _mod.Capi.Shader.GetProgram((int)EnumShaderProgram.Final)
                   ?? _mod.Capi.Shader.GetProgramByName(TargetProgramName);
        }

        /// <summary>
        /// Records that the config changed. Deliberately does NO GL work — see
        /// <see cref="_uniformsDirty"/> for why that distinction is not a
        /// stylistic one.
        /// </summary>
        public void Apply()
        {
            _uniformsDirty = true;
        }

        private void UploadUniforms()
        {
            if (_mod == null || _mod.Capi == null) return;

            IShaderProgram program = ResolveFinalProgram();

            if (program == null)
            {
                if (!_warnedMissingProgram)
                {
                    _warnedMissingProgram = true;
                    _mod.Mod.Logger.Warning("[VintageVisuals] colorgrade: the vanilla '" + TargetProgramName +
                        "' shader program is not loaded yet. Color grading is inactive for now; this resolves " +
                        "itself if it was simply called too early.");
                }
                return;
            }

            _warnedMissingProgram = false;

            // HasUniform is the ground truth for "did our GLSL reach the GPU":
            // the uniform exists only if the injection survived compilation.
            if (!program.HasUniform(EnabledUniform))
            {
                if (!_warnedMissingUniform)
                {
                    _warnedMissingUniform = true;
                    _mod.Mod.Logger.Warning("[VintageVisuals] colorgrade: '" + TargetProgramName +
                        "' has no " + EnabledUniform + " uniform, so the GLSL injection did not reach the " +
                        "compiled program. Color grading is inactive — check the log above for a patch failure, " +
                        "and set EnableShaderDebugDump to inspect the merged source.");
                }
                return;
            }

            _warnedMissingUniform = false;

            ColorGradeConfig config = _mod.ConfigManager.Config.ColorGrade;

            // Activation deliberately does NOT consult ShaderPatchingAvailable
            // or IsGroupHealthy. Those track what this mod *believes* it did;
            // HasUniform above is direct evidence of what actually reached the
            // compiled program, and it is strictly stronger. Requiring both
            // meant that any reset of our own bookkeeping — a shader reload
            // rebuilds the patch groups — could veto a shader that was in fact
            // correctly patched, which is a false negative with no upside.
            bool active = config.Enabled;

            program.Use();
            program.Uniform(EnabledUniform, active ? 1f : 0f);

            if (active)
            {
                program.Uniform("vv_exposure", config.Exposure);
                program.Uniform("vv_contrast", config.Contrast);
                program.Uniform("vv_saturation", config.Saturation);
                program.Uniform("vv_temperature", config.Temperature);
                program.Uniform("vv_tonemapStrength", config.TonemapStrength);
                program.Uniform(AdaptationUniform, _adaptation.Current);
                _lastUploadedAdaptation = _adaptation.Current;
            }

            program.Stop();

            _uniformsDirty = false;

            _mod.Mod.Logger.Notification("[VintageVisuals] colorgrade: uniforms uploaded, active=" + active +
                " (exposure=" + config.Exposure.ToString("0.##") +
                " contrast=" + config.Contrast.ToString("0.##") +
                " saturation=" + config.Saturation.ToString("0.##") +
                " temperature=" + config.Temperature.ToString("0.##") +
                " tonemap=" + config.TonemapStrength.ToString("0.##") + ")");
        }

        public void Dispose()
        {
            if (_mod?.Capi != null)
            {
                _mod.Capi.Event.UnregisterRenderer(this, EnumRenderStage.Before);
            }

            _adaptation.Reset();
            _mod = null;
        }

        /// <summary>
        /// Required by IRenderer. Disposal is driven by the mod system through
        /// IVisualSubsystem.Dispose above, which is also what unregisters this.
        /// </summary>
        void IDisposable.Dispose()
        {
            Dispose();
        }
    }
}
