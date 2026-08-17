using Vintagestory.API.Client;
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
    public sealed class ColorGradeSubsystem : IVisualSubsystem
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

        private VintageVisualsModSystem _mod;

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

        public void Apply()
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
            }

            program.Stop();

            _mod.Mod.Logger.Notification("[VintageVisuals] colorgrade: uniforms uploaded, active=" + active +
                " (exposure=" + config.Exposure.ToString("0.##") +
                " contrast=" + config.Contrast.ToString("0.##") +
                " saturation=" + config.Saturation.ToString("0.##") +
                " temperature=" + config.Temperature.ToString("0.##") +
                " tonemap=" + config.TonemapStrength.ToString("0.##") + ")");
        }

        public void Dispose()
        {
            _mod = null;
        }
    }
}
