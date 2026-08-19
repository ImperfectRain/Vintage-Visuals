using Vintagestory.API.Client;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Keeps the material atlas bound into the vanilla chunkopaque program and
    /// its uniforms current.
    ///
    /// This is a renderer that draws nothing. It exists because texture unit
    /// bindings are global GL state that this mod does not own: anything can
    /// rebind unit 6 between frames, and the failure would be silent and ugly
    /// (surface relief sampled out of whatever texture happened to be there).
    /// Re-binding once per frame, immediately before terrain draws, costs a
    /// handful of GL calls and removes the question entirely.
    /// </summary>
    public sealed class PbrShaderBinder : IRenderer
    {
        /// <summary>
        /// Terrain opaque renders at 0.37 (see <see cref="IRenderer.RenderOrder"/>'s
        /// own documentation), so binding at 0.35 lands in the gap right before
        /// it — after the sky, before any chunk geometry.
        /// </summary>
        private const double BindBeforeTerrainOpaque = 0.35;

        public const string SamplerUniform = "vv_materialTex";
        public const string EnabledUniform = "vv_pbrEnabled";
        public const string NormalStrengthUniform = "vv_pbrNormalStrength";
        public const string SpecularStrengthUniform = "vv_pbrSpecularStrength";
        public const string DebugViewUniform = "vv_pbrDebugView";

        private readonly ICoreClientAPI _capi;
        private readonly MaterialAtlasTexture _atlas;

        private bool _enabled;
        private float _normalStrength = 1f;
        private float _specularStrength = 1f;
        private float _debugView;

        /// <summary>
        /// Set while the program still believes the effect is on, so switching
        /// off pushes vv_pbrEnabled=0 exactly once and then stops touching the
        /// program at all.
        /// </summary>
        private bool _programThinksEnabled;

        /// <summary>
        /// Latched so a missing uniform is reported once rather than at 60Hz.
        /// Cleared when the condition clears, so a problem that returns after
        /// a shader reload is reported again instead of staying silent.
        /// </summary>
        private bool _warnedMissingUniform;

        public PbrShaderBinder(ICoreClientAPI capi, MaterialAtlasTexture atlas)
        {
            _capi = capi;
            _atlas = atlas;
        }

        public double RenderOrder { get { return BindBeforeTerrainOpaque; } }

        public int RenderRange { get { return 0; } }

        /// <summary>
        /// Sets what the next frame will upload. Called from config changes, so
        /// it deliberately does no GL work of its own — the config thread is not
        /// necessarily a thread with a GL context.
        /// </summary>
        public void SetState(bool enabled, float normalStrength, float specularStrength, float debugView)
        {
            _enabled = enabled;
            _normalStrength = normalStrength;
            _specularStrength = specularStrength;
            _debugView = debugView;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Opaque) return;
            if (!_atlas.IsUploaded) return;

            // Nothing to do while switched off — and specifically, no texture
            // binding. An earlier version bound every frame regardless and only
            // varied the uniform, which meant PseudoPBR.Enabled=false still
            // occupied a texture unit. That is not an off switch: when binding
            // to the wrong unit was corrupting the frame, the config flag that
            // should have rescued the player did nothing and only a restart
            // helped. Whatever else this renderer does, "off" has to mean it
            // touches no shared GL state.
            if (!_enabled && !_programThinksEnabled) return;

            IShaderProgram program = _capi.Shader.GetProgram((int)EnumShaderProgram.Chunkopaque);

            // HasUniform is the ground truth for "did our GLSL reach the GPU",
            // exactly as in ColorGrade: the uniform exists only if the injection
            // survived compilation. Same lesson too — GetProgramByName does not
            // find vanilla passes, they are addressed by EnumShaderProgram id.
            if (program == null || !program.HasUniform(EnabledUniform))
            {
                if (!_warnedMissingUniform)
                {
                    _warnedMissingUniform = true;
                    _capi.Logger.Warning("[VintageVisuals] pseudopbr: chunkopaque has no " + EnabledUniform +
                        " uniform, so the GLSL injection did not reach the compiled program. Surface relief is " +
                        "inactive — check the log for a pseudopbr patch failure, and set EnableShaderDebugDump " +
                        "to inspect the merged source.");
                }
                return;
            }

            _warnedMissingUniform = false;

            if (!_enabled)
            {
                // One last visit to clear the flag the program is still holding,
                // then leave it alone until the effect is switched back on.
                program.Use();
                program.Uniform(EnabledUniform, 0f);
                program.Stop();
                _programThinksEnabled = false;
                return;
            }

            program.Use();
            program.BindTexture2D(SamplerUniform, _atlas.TextureId, MaterialAtlasTexture.TextureUnit);
            program.Uniform(EnabledUniform, 1f);
            program.Uniform(NormalStrengthUniform, _normalStrength);
            program.Uniform(SpecularStrengthUniform, _specularStrength);
            program.Uniform(DebugViewUniform, _debugView);
            program.Stop();
            _programThinksEnabled = true;
        }

        /// <summary>
        /// Required by IRenderer. The atlas texture is owned by the subsystem,
        /// not by this, so there is deliberately nothing to release here — the
        /// game calls this on renderer unregistration, which happens on world
        /// leave, while the texture outlives that.
        /// </summary>
        public void Dispose()
        {
        }
    }
}
