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
        public const string DayLightUniform = "vv_sceneDayLight";
        public const string RoughnessBiasUniform = "vv_pbrRoughnessBias";
        public const string MetalResponseUniform = "vv_pbrMetalResponse";
        public const string AmbientUniform = "vv_pbrAmbient";
        public const string SpecularAaUniform = "vv_pbrSpecularAA";
        public const string DetailDistanceUniform = "vv_pbrDetailDistance";
        public const string BlockLightUniform = "vv_pbrBlockLight";
        public const string BlockLightDirUniform = "vv_pbrBlockLightDir";
        public const string WetnessUniform = "vv_sceneWetness";
        public const string RainCoverUniform = "vv_weatherRainCover";
        public const string RipplesUniform = "vv_weatherRipples";
        public const string RippleTimeUniform = "vv_weatherRippleTime";
        public const string OvercastUniform = "vv_sceneOvercast";
        public const string OriginUniform = "vv_pbrOrigin";
        public const string FoliageUniform = "vv_pbrFoliage";
        public const string CavityUniform = "vv_pbrCavity";

        // Entity programme only. Named apart from the terrain controls because
        // they describe a different material: entities have no derived atlas, so
        // there is one roughness for all of them rather than one per texel.
        public const string EntityEnabledUniform = "vv_pbrEntity";
        public const string EntityRoughnessUniform = "vv_pbrEntityRoughness";
        public const string EntitySpecularUniform = "vv_pbrEntitySpecular";
        public const string EntityDebugUniform = "vv_pbrEntityDebug";

        // The shared scene vocabulary. Same names in every shaded program, so a
        // creature and the ground it stands on cannot disagree about the
        // weather - see assets/.../shadersnippets/scene.glsl.
        public const string EnclosureUniform = "vv_sceneEnclosure";
        public const string ArtificialLightUniform = "vv_sceneArtificialLight";
        public const string RestraintUniform = "vv_sceneRestraint";
        public const string ReadabilityUniform = "vv_sceneReadability";
        public const string ClockUniform = "vv_sceneClock";
        public const string AutumnUniform = "vv_sceneAutumn";
        public const string WinterUniform = "vv_sceneWinter";
        public const string FrostUniform = "vv_sceneFrost";
        public const string SnowUniform = "vv_sceneSnow";

        public const string ParticleUniform = "vv_pbrParticle";
        public const string ParticleSpecularUniform = "vv_pbrParticleSpecular";

        public const string EmissiveUniform = "vv_emissive";
        public const string EmissiveTemperatureUniform = "vv_emissiveTemperature";
        public const string EmissiveFlickerUniform = "vv_emissiveFlicker";
        public const string EmissiveBloomUniform = "vv_emissiveBloom";

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

        /// <summary>
        /// Programs that shade something other than terrain with the same lobe.
        ///
        /// Kept separate from PatchedPrograms because the preconditions differ:
        /// the terrain path needs a derived material atlas and refuses to run
        /// without one, and this one deliberately does not have an atlas at all.
        /// Folding them into one list would mean a missing atlas silently taking
        /// entity lighting down with it.
        /// </summary>
        private static readonly EnumShaderProgram[] EntityPrograms =
        {
            EnumShaderProgram.Entityanimated,
        };

        /// <summary>
        /// The small moving things. Separate again from the entity list: they
        /// take their own restraint - a highlight that reads as detail on a wall
        /// reads as twinkling noise on a cloud of dust - and one of the two has
        /// no normal at all, so it gets emission and nothing else.
        /// </summary>
        private static readonly EnumShaderProgram[] ParticlePrograms =
        {
            EnumShaderProgram.Particlescube,
            EnumShaderProgram.Particlesquad,
        };

        private readonly ICoreClientAPI _capi;
        private readonly MaterialAtlasSet _atlas;
        private readonly Func<Dictionary<int, int>> _buildPageMap;

        private bool _enabled;
        private PseudoPbrConfig _look = new PseudoPbrConfig();
        private SceneInputs _weather = SceneInputs.None;
        private readonly Func<SceneInputs> _readScene;

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
        private bool _reportedEntities;
        private bool _reportedParticles;

        public PbrShaderBinder(ICoreClientAPI capi, MaterialAtlasSet atlas,
                               Func<Dictionary<int, int>> buildPageMap,
                               Func<SceneInputs> readScene)
        {
            _capi = capi;
            _atlas = atlas;
            _buildPageMap = buildPageMap;
            _readScene = readScene;
        }

        public double RenderOrder { get { return BindBeforeTerrainOpaque; } }

        public int RenderRange { get { return 0; } }

        /// <summary>
        /// Sets what the next frame will do. Called from config changes, so it
        /// deliberately does no GL work of its own - the config path is not
        /// guaranteed to be a thread with a GL context.
        ///
        /// Config only. What the WORLD is doing is pulled per frame instead -
        /// see _readScene. Pushing that here as well is how the rain ripples
        /// ended up frozen between slider movements: Apply() runs on config
        /// change, and a config change is not a clock.
        /// </summary>
        public void SetState(bool enabled, PseudoPbrConfig look)
        {
            _enabled = enabled;
            _look = look;
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

            // Pulled every frame, not pushed on config change. Wetness, the
            // ripple clock and the camera origin all move continuously, and a
            // config change is not a clock.
            if (_readScene != null) _weather = _readScene();

            // Entities first, and deliberately BEFORE every terrain
            // precondition below. They share the lobe but not the material: the
            // terrain path refuses to run without a derived atlas, and letting
            // that refusal take entity lighting with it would mean a missing
            // atlas silently un-lighting every mob in the world.
            UploadEntities();
            UploadParticles();

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

            int uploaded = 0;
            foreach (EnumShaderProgram id in PatchedPrograms)
            {
                if (Upload(id, _weather.DayLight)) uploaded++;
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
            program.Uniform(FoliageUniform, _look.FoliageTranslucency);
            program.Uniform(CavityUniform, _look.CavityStrength);
            program.Uniform(BlockLightUniform, _look.BlockLightSpecular);
            program.Uniform(BlockLightDirUniform, _look.BlockLightDirectionality);
            program.Uniform(WetnessUniform, _weather.Wetness);
            program.Uniform(RainCoverUniform, _weather.RainCover);
            program.Uniform(RipplesUniform, _weather.Ripples);
            program.Uniform(RippleTimeUniform, _weather.RippleTime);
            program.Uniform(OvercastUniform, _weather.Overcast);
            program.Uniform(OriginUniform, _weather.Origin);
            UploadScene(program);

            program.Stop();
            return true;
        }

        /// <summary>
        /// Pushes the particle response.
        ///
        /// Before the terrain preconditions for the same reason entities are:
        /// particles need no material atlas, and a missing one must not quietly
        /// un-light every spark in the world.
        /// </summary>
        private void UploadParticles()
        {
            foreach (EnumShaderProgram id in ParticlePrograms)
            {
                IShaderProgram program = _capi.Shader.GetProgram((int)id);
                if (program == null || !program.HasUniform(ParticleUniform)) continue;

                program.Use();

                program.Uniform(ParticleUniform, _look.ParticleLighting ? 1f : 0f);
                program.Uniform(ParticleSpecularUniform, _look.ParticleSpecular);
                program.Uniform(RoughnessBiasUniform, _look.RoughnessBias);
                program.Uniform(AmbientUniform, _look.AmbientSpecular);
                program.Uniform(SpecularAaUniform, _look.SpecularAntiAliasing);
                program.Uniform(MetalResponseUniform, _look.MetalResponse);
                program.Uniform(BlockLightUniform, _look.BlockLightSpecular);
                program.Uniform(BlockLightDirUniform, _look.BlockLightDirectionality);
                UploadScene(program);

                program.Stop();

                if (!_reportedParticles)
                {
                    _reportedParticles = true;
                    _capi.Logger.Notification("[VintageVisuals] pseudopbr: particle lighting active on " + id +
                        " - falling leaves, dust, sparks and smoke now share the world's lighting.");
                }
            }
        }

        /// <summary>
        /// Pushes the shared scene vocabulary.
        ///
        /// The same five values into every shaded program, from one place, so
        /// there is no way for two of them to be told different things.
        /// </summary>
        private void UploadScene(IShaderProgram program)
        {
            if (!program.HasUniform(RestraintUniform)) return;

            program.Uniform(EnclosureUniform, _weather.Enclosure);
            program.Uniform(ArtificialLightUniform, _weather.ArtificialLight);
            program.Uniform(RestraintUniform, _weather.Restraint);
            program.Uniform(ReadabilityUniform, _weather.Readability);
            program.Uniform(ClockUniform, _weather.RippleTime);
            program.Uniform(AutumnUniform, _weather.Autumn);
            program.Uniform(WinterUniform, _weather.Winter);
            program.Uniform(FrostUniform, _weather.Frost);
            program.Uniform(SnowUniform, _weather.Snow);

            program.Uniform(EmissiveUniform, _look.EmissiveStrength);
            program.Uniform(EmissiveTemperatureUniform, _look.EmissiveTemperature);
            program.Uniform(EmissiveFlickerUniform, _look.EmissiveFlicker);
            program.Uniform(EmissiveBloomUniform, _look.EmissiveBloom);
        }

        /// <summary>
        /// Pushes the entity material response.
        ///
        /// Separate from Upload because almost nothing is shared: no sampler to
        /// bind, no atlas to require, no page map, and a different set of
        /// uniforms describing a single default material rather than a derived
        /// per-texel one.
        /// </summary>
        private void UploadEntities()
        {
            foreach (EnumShaderProgram id in EntityPrograms)
            {
                IShaderProgram program = _capi.Shader.GetProgram((int)id);
                if (program == null || !program.HasUniform(EntityEnabledUniform)) continue;

                program.Use();

                program.Uniform(EntityEnabledUniform, _look.EntityLighting ? 1f : 0f);
                program.Uniform(EntityRoughnessUniform, _look.EntityRoughness);
                program.Uniform(EntitySpecularUniform, _look.EntitySpecular);
                program.Uniform(EntityDebugUniform, _look.EntityDebugView);
                program.Uniform(RoughnessBiasUniform, _look.RoughnessBias);
                program.Uniform(MetalResponseUniform, _look.MetalResponse);
                program.Uniform(AmbientUniform, _look.AmbientSpecular);
                program.Uniform(SpecularAaUniform, _look.SpecularAntiAliasing);
                program.Uniform(BlockLightUniform, _look.BlockLightSpecular);
                program.Uniform(BlockLightDirUniform, _look.BlockLightDirectionality);
                program.Uniform(DayLightUniform, _weather.DayLight);
                program.Uniform(WetnessUniform, _weather.Wetness);
                program.Uniform(OvercastUniform, _weather.Overcast);
                UploadScene(program);

                program.Stop();

                if (!_reportedEntities)
                {
                    _reportedEntities = true;
                    _capi.Logger.Notification("[VintageVisuals] pseudopbr: entity lighting active on " + id +
                        " - mobs, animals and players now use the same microfacet lobe as the terrain.");
                }
            }
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
