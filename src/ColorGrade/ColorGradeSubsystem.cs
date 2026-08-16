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
        // recurring problem to one log line instead of one per reload.
        private bool _warnedMissingProgram;
        private bool _warnedMissingUniform;

        public string Name => GroupName;

        public void Initialize(VintageVisualsModSystem mod)
        {
            _mod = mod;
        }

        public void Apply()
        {
            if (_mod == null || _mod.Capi == null) return;

            IShaderProgram program = _mod.Capi.Shader.GetProgramByName(TargetProgramName);

            if (program == null)
            {
                if (!_warnedMissingProgram)
                {
                    _warnedMissingProgram = true;
                    _mod.Mod.Logger.Warning("[VintageVisuals] colorgrade: no shader program named '" +
                        TargetProgramName + "'. Color grading is inactive.");
                }
                return;
            }

            _warnedMissingProgram = false;

            // HasUniform is the ground truth for "did the patch land": the
            // uniform only exists if our GLSL was injected and survived
            // compilation. Checking the patcher's own bookkeeping alone would
            // not catch a shader that failed to compile downstream.
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

            // Every one of these must hold for the effect to be legitimate:
            // the player enabled it, the hook installed, and the patch applied.
            bool active = config.Enabled
                          && _mod.ShaderPatchingAvailable
                          && _mod.ShaderPatcher.IsGroupHealthy(GroupName);

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

            _mod.Mod.Logger.VerboseDebug("[VintageVisuals] colorgrade: uniforms uploaded, active=" + active);
        }

        public void Dispose()
        {
            _mod = null;
        }
    }
}
