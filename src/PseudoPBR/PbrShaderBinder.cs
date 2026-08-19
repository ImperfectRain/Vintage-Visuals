using Vintagestory.API.Client;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Owns everything this subsystem does on the render thread: uploading the
    /// material atlas, keeping it bound into the vanilla chunkopaque program,
    /// and keeping its uniforms current.
    ///
    /// It draws nothing. It exists because all of that is GL state work with a
    /// right moment and a wrong moment, and the wrong moment has already cost
    /// this subsystem two rounds of debugging. Texture unit bindings are global
    /// state this mod does not own, and creating a texture binds it as a side
    /// effect — so both the upload and the re-bind happen here, once per frame,
    /// immediately before terrain draws.
    ///
    /// Every path that declines to do something says so exactly once. A
    /// renderer that silently returns is indistinguishable from a renderer that
    /// is working, and this subsystem has now twice been debugged from a
    /// screenshot because the log had nothing to say.
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

        // One-shot latches. Each clears when its condition clears, so a problem
        // that returns after a shader reload is reported again rather than
        // staying silent.
        private bool _reportedNoPixels;
        private bool _reportedNoTexture;
        private bool _reportedMissingUniform;
        private bool _reportedActive;

        public PbrShaderBinder(ICoreClientAPI capi, MaterialAtlasTexture atlas)
        {
            _capi = capi;
            _atlas = atlas;
        }

        public double RenderOrder { get { return BindBeforeTerrainOpaque; } }

        public int RenderRange { get { return 0; } }

        /// <summary>
        /// Sets what the next frame will do. Called from config changes, so it
        /// deliberately does no GL work of its own — the config path is not
        /// guaranteed to be a thread with a GL context.
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

            if (!_enabled)
            {
                ReleaseWhileDisabled();
                return;
            }

            if (!_atlas.HasPixels)
            {
                if (!_reportedNoPixels)
                {
                    _reportedNoPixels = true;
                    _capi.Logger.Warning("[VintageVisuals] pseudopbr: enabled, but no material atlas has been " +
                        "derived yet, so there is nothing to sample. Check PseudoPBR.BuildMaterialAtlas and the " +
                        "earlier pseudopbr lines in this log.");
                }
                return;
            }

            _reportedNoPixels = false;

            // Upload happens here, not at asset-load time. Creating a texture
            // binds it to the active unit as a side effect, and doing that at
            // an arbitrary moment during startup means clobbering whatever the
            // game had bound there.
            if (!_atlas.EnsureUploaded(_capi, _capi.Logger))
            {
                if (!_reportedNoTexture)
                {
                    _reportedNoTexture = true;
                    _capi.Logger.Warning("[VintageVisuals] pseudopbr: the material atlas is not on the GPU, so " +
                        "surface relief is inactive. The upload failure is logged above.");
                }
                return;
            }

            _reportedNoTexture = false;

            IShaderProgram program = _capi.Shader.GetProgram((int)EnumShaderProgram.Chunkopaque);

            // HasUniform is the ground truth for "did our GLSL reach the GPU",
            // exactly as in ColorGrade: the uniform exists only if the injection
            // survived compilation. Same lesson too — GetProgramByName does not
            // find vanilla passes, they are addressed by EnumShaderProgram id.
            if (program == null || !program.HasUniform(EnabledUniform))
            {
                if (!_reportedMissingUniform)
                {
                    _reportedMissingUniform = true;
                    _capi.Logger.Warning("[VintageVisuals] pseudopbr: chunkopaque " +
                        (program == null ? "is not loaded" : "has no " + EnabledUniform + " uniform") +
                        ", so the GLSL injection did not reach the compiled program. Surface relief and the " +
                        "debug views are inactive — look for a pseudopbr patch failure above, and set " +
                        "EnableShaderDebugDump to inspect the merged source.");
                }
                return;
            }

            _reportedMissingUniform = false;

            program.Use();
            program.BindTexture2D(SamplerUniform, _atlas.TextureId, MaterialAtlasTexture.TextureUnit);
            program.Uniform(EnabledUniform, 1f);
            program.Uniform(NormalStrengthUniform, _normalStrength);
            program.Uniform(SpecularStrengthUniform, _specularStrength);
            program.Uniform(DebugViewUniform, _debugView);
            program.Stop();

            _programThinksEnabled = true;

            if (!_reportedActive)
            {
                _reportedActive = true;
                _capi.Logger.Notification("[VintageVisuals] pseudopbr: surface relief active — atlas texture " +
                    _atlas.TextureId + " bound at unit " + MaterialAtlasTexture.TextureUnit + ", uniforms uploading.");
            }
        }

        /// <summary>
        /// Gives back everything this renderer holds while it is switched off.
        ///
        /// Not just "stop varying the uniform". The texture is released too,
        /// because while it exists it stays bound to its unit, and a config flag
        /// that leaves shared GL state occupied is not an off switch — the
        /// player has no way to rule this subsystem out.
        /// </summary>
        private void ReleaseWhileDisabled()
        {
            _reportedActive = false;

            if (_programThinksEnabled)
            {
                IShaderProgram program = _capi.Shader.GetProgram((int)EnumShaderProgram.Chunkopaque);
                if (program != null && program.HasUniform(EnabledUniform))
                {
                    program.Use();
                    program.Uniform(EnabledUniform, 0f);
                    program.Stop();
                }

                _programThinksEnabled = false;
            }

            if (_atlas.IsUploaded)
            {
                _atlas.Release();
                _capi.Logger.Notification("[VintageVisuals] pseudopbr: disabled — material atlas released from " +
                    "the GPU. This subsystem now holds no shader patch, no texture and no texture unit.");
            }
        }

        /// <summary>
        /// Required by IRenderer. The atlas is owned by the subsystem, not by
        /// this, so there is deliberately nothing to release here — the game
        /// calls this on renderer unregistration, which happens on world leave,
        /// while the atlas outlives that.
        /// </summary>
        public void Dispose()
        {
        }
    }
}
