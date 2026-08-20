using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using VintageVisuals.Common;

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
        public const string DayLightUniform = "vv_pbrDayLight";
        public const string RoughnessBiasUniform = "vv_pbrRoughnessBias";
        public const string MetalResponseUniform = "vv_pbrMetalResponse";
        public const string AmbientUniform = "vv_pbrAmbient";
        public const string SpecularAaUniform = "vv_pbrSpecularAA";
        public const string DetailDistanceUniform = "vv_pbrDetailDistance";
        public const string BlockLightUniform = "vv_pbrBlockLight";
        public const string BlockLightDirUniform = "vv_pbrBlockLightDir";
        public const string WetnessUniform = "vv_weatherWetness";
        public const string RainCoverUniform = "vv_weatherRainCover";
        public const string RipplesUniform = "vv_weatherRipples";
        public const string OvercastUniform = "vv_weatherOvercast";
        public const string OriginUniform = "vv_pbrOrigin";

        /// <summary>
        /// Every vanilla program this subsystem patches. Grass and soil tops go
        /// through their own program, so uniforms have to reach both or the
        /// forest floor is the one surface in the world the material system
        /// does not touch.
        /// </summary>
        private static readonly EnumShaderProgram[] PatchedPrograms =
        {
            EnumShaderProgram.Chunkopaque,
            EnumShaderProgram.Chunktopsoil,
        };

        private readonly ICoreClientAPI _capi;
        private readonly MaterialAtlasSet _atlas;
        private readonly Func<Dictionary<int, int>> _buildPageMap;

        private bool _enabled;
        private PseudoPbrConfig _look = new PseudoPbrConfig();
        private WeatherInputs _weather = WeatherInputs.None;

        /// <summary>
        /// Set while a program still believes the effect is on, so switching
        /// off pushes vv_pbrEnabled=0 exactly once and then stops touching the
        /// programs at all.
        /// </summary>
        private bool _programsThinkEnabled;

        // One-shot latches. Each clears when its condition clears, so a problem
        // that returns after a shader reload is reported again rather than
        // staying silent.
        private bool _reportedNoPixels;
        private bool _reportedNoTexture;
        private bool _reportedMissingUniform;
        private bool _reportedActive;
        private bool _reportedBusy;

        public PbrShaderBinder(ICoreClientAPI capi, MaterialAtlasSet atlas,
                               Func<Dictionary<int, int>> buildPageMap)
        {
            _capi = capi;
            _atlas = atlas;
            _buildPageMap = buildPageMap;
        }

        public double RenderOrder { get { return BindBeforeTerrainOpaque; } }

        public int RenderRange { get { return 0; } }

        /// <summary>
        /// Sets what the next frame will do. Called from config changes, so it
        /// deliberately does no GL work of its own - the config path is not
        /// guaranteed to be a thread with a GL context.
        /// </summary>
        public void SetState(bool enabled, PseudoPbrConfig look, WeatherInputs weather)
        {
            _enabled = enabled;
            _look = look;
            _weather = weather;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Opaque) return;

            // IShaderProgram.Use() THROWS if a program is already bound -
            // "Already a different shader (gui) in use!" - and the client does
            // not recover: OpenGL goes to InvalidOperation and the world stops
            // drawing correctly. Nothing should hold a program this early in
            // the opaque stage, but the cost of being wrong is the whole frame,
            // so skip and try again next frame instead.
            if (_capi.Render.CurrentActiveShader != null)
            {
                if (!_reportedBusy)
                {
                    _reportedBusy = true;
                    _capi.Logger.Warning("[VintageVisuals] pseudopbr: another shader program was bound at the " +
                        "start of the opaque stage, so the material uniforms were not uploaded this frame.");
                }
                return;
            }

            _reportedBusy = false;

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

            // Republished every frame rather than cached. The game recreates
            // its atlas textures on reload and their ids change with them, and
            // a stale map is a page rendering with another page's material
            // data - silent and wrong, the worst of the two failure modes.
            TerrainTextureBindInterceptor.SetPages(_buildPageMap());

            // Vanilla's own dayLight uniform is declared in chunkopaque.fsh and
            // not in chunktopsoil.fsh, so the shared snippet reads ours instead
            // and this is where it comes from.
            float daylight = _capi.World?.Calendar == null ? 1f : _capi.World.Calendar.DayLightStrength;

            int uploaded = 0;
            foreach (EnumShaderProgram id in PatchedPrograms)
            {
                if (Upload(id, daylight)) uploaded++;
            }

            if (uploaded == 0)
            {
                if (!_reportedMissingUniform)
                {
                    _reportedMissingUniform = true;
                    _capi.Logger.Warning("[VintageVisuals] pseudopbr: no patched chunk program exposes " +
                        EnabledUniform + ", so the GLSL injection did not reach any compiled program. Surface " +
                        "relief and the debug views are inactive - look for a pseudopbr patch failure above, " +
                        "and set EnableShaderDebugDump to inspect the merged source.");
                }
                return;
            }

            _reportedMissingUniform = false;
            _programsThinkEnabled = true;

            if (!_reportedActive)
            {
                _reportedActive = true;
                _capi.Logger.Notification("[VintageVisuals] pseudopbr: surface relief active - " +
                    _atlas.PageCount + " atlas page(s) on the GPU at unit " +
                    MaterialAtlasTexture.TextureUnit + ", uniforms uploading to " + uploaded +
                    " of " + PatchedPrograms.Length + " patched program(s).");
            }
        }

        /// <summary>
        /// Pushes every uniform to one program. Returns false when the program
        /// is not loaded or was never patched.
        ///
        /// One place, one list. An earlier version had the uniform writes
        /// scattered through the frame path and five of them were silently
        /// never added, which meant the sliders that drove them did nothing and
        /// - because vv_pbrDayLight gates it - the whole specular term read as
        /// night. Uniform uploads all live here so that "did I wire this up"
        /// is answerable by looking at one method.
        /// </summary>
        private bool Upload(EnumShaderProgram id, float daylight)
        {
            IShaderProgram program = _capi.Shader.GetProgram((int)id);

            // HasUniform is the ground truth for "did our GLSL reach the GPU",
            // exactly as in ColorGrade: the uniform exists only if the injection
            // survived compilation. Same lesson too - GetProgramByName does not
            // find vanilla passes, they are addressed by EnumShaderProgram id.
            if (program == null || !program.HasUniform(EnabledUniform)) return false;

            program.Use();

            // Page 0 as the default. The bind hook swaps in the right page as
            // vanilla selects it, but on a single-page atlas there is nothing
            // to swap and this is the whole binding.
            program.BindTexture2D(SamplerUniform, _atlas.TextureIdFor(0), MaterialAtlasTexture.TextureUnit);

            program.Uniform(EnabledUniform, 1f);
            program.Uniform(NormalStrengthUniform, _look.NormalStrength);
            program.Uniform(SpecularStrengthUniform, _look.SpecularStrength);
            program.Uniform(DebugViewUniform, _look.DebugView);
            program.Uniform(DayLightUniform, daylight);
            program.Uniform(RoughnessBiasUniform, _look.RoughnessBias);
            program.Uniform(MetalResponseUniform, _look.MetalResponse);
            program.Uniform(AmbientUniform, _look.AmbientSpecular);
            program.Uniform(SpecularAaUniform, _look.SpecularAntiAliasing);
            program.Uniform(DetailDistanceUniform, _look.DetailDistance);
            program.Uniform(BlockLightUniform, _look.BlockLightSpecular);
            program.Uniform(BlockLightDirUniform, _look.BlockLightDirectionality);
            program.Uniform(WetnessUniform, _weather.Wetness);
            program.Uniform(RainCoverUniform, _weather.RainCover);
            program.Uniform(RipplesUniform, _weather.Ripples);
            program.Uniform(OvercastUniform, _weather.Overcast);
            program.Uniform(OriginUniform, _weather.Origin);

            program.Stop();
            return true;
        }

        /// <summary>
        /// Gives back everything this renderer holds while it is switched off.
        ///
        /// Not just "stop varying the uniform". The texture is released too,
        /// because while it exists it stays bound to its unit, and a config flag
        /// that leaves shared GL state occupied is not an off switch - the
        /// player has no way to rule this subsystem out.
        /// </summary>
        private void ReleaseWhileDisabled()
        {
            _reportedActive = false;

            TerrainTextureBindInterceptor.SetPages(null);

            if (_programsThinkEnabled)
            {
                foreach (EnumShaderProgram id in PatchedPrograms)
                {
                    IShaderProgram program = _capi.Shader.GetProgram((int)id);
                    if (program == null || !program.HasUniform(EnabledUniform)) continue;

                    program.Use();
                    program.Uniform(EnabledUniform, 0f);
                    program.Stop();
                }

                _programsThinkEnabled = false;
            }

            if (_atlas.AnyUploaded)
            {
                _atlas.Release();
                _capi.Logger.Notification("[VintageVisuals] pseudopbr: disabled - material atlas released from " +
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
