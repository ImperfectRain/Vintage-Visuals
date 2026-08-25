using System;
using System.Collections.Generic;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VintageVisuals.Common;
using VintageVisuals.Reflections;

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
        public const string SecondSamplerUniform = "vv_materialTex2";

        /// <summary>
        /// 1 only when the second atlas is genuinely on the GPU.
        ///
        /// Its zero is the fallback, and zero is also what an unset uniform
        /// reads - so a program that never received this, or one whose group
        /// rolled back, reads exactly what a failed build produces rather than
        /// sampling an uninitialised texture unit and calling the result
        /// metalness.
        /// </summary>
        public const string SecondValidUniform = "vv_material2Valid";
        public const string EnabledUniform = "vv_pbrEnabled";
        public const string CompareWipeUniform = "vv_compareWipe";
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
        public const string SpecOcclusionUniform = "vv_pbrSpecOcclusion";
        public const string EnergyCompensationUniform = "vv_pbrEnergyCompensation";
        public const string GrainUniform = "vv_pbrGrain";
        public const string DappleUniform = "vv_pbrDapple";
        public const string CanopyRadiusUniform = "vv_pbrCanopyRadius";
        public const string PixelReflectUniform = "vv_pbrPixelReflect";

        // The render-stage bridge. See SceneCaptureRenderer.
        public const string ReflectSceneUniform = "vv_reflectScene";
        public const string ReflectViewProjUniform = "vv_reflectViewProj";
        public const string ReflectCameraDeltaUniform = "vv_reflectCameraDelta";
        public const string ReflectValidUniform = "vv_reflectValid";
        public const string ReflectFarUniform = "vv_reflectFar";
        public const string ReflectFrameSizeUniform = "vv_reflectFrameSize";
        public const string ReflectWorldUniform = "vv_reflectWorld";
        public const string ReflectWorldValidUniform = "vv_reflectWorldValid";
        public const string ReflectWorldOriginUniform = "vv_reflectWorldOrigin";
        public const string ReflectWorldSizeUniform = "vv_reflectWorldSize";
        public const string ReflectWorldSliceSizeUniform = "vv_reflectWorldSliceSize";
        public const string ReflectWorldAtlasGridUniform = "vv_reflectWorldAtlasGrid";
        public const string ReflectWorldAtlasSizeUniform = "vv_reflectWorldAtlasSize";
        public const string CanopyContextUniform = "vv_canopyContext";
        public const string CanopyContextValidUniform = "vv_canopyContextValid";
        public const string ShaftUniform = "vv_pbrShafts";

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
        public const string ArtificialLightUniform = "vv_sceneArtificialLight";
        public const string RestraintUniform = "vv_sceneRestraint";
        public const string ReadabilityUniform = "vv_sceneReadability";
        public const string ClockUniform = "vv_sceneClock";
        public const string BreezeUniform = "vv_sceneBreeze";
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
        private readonly MaterialAtlasSet _atlas2;
        private readonly Func<Dictionary<int, int>> _buildPageMap;
        private readonly Func<Dictionary<int, int>> _buildSecondPageMap;

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
        private bool _reportedNoSecond;
        private bool _secondAtlasReady;
        private bool _reportedMissingUniform;
        private bool _reportedActive;
        private bool _reportedBusy;
        private bool _reportedEntities;
        private bool _reportedParticles;

        /// <summary>
        /// The scene capture, when the reflection subsystem has one.
        ///
        /// Settable rather than constructor-injected because the capture is
        /// optional and can fail at any point - a driver refusing the
        /// framebuffer, a shader that will not compile - and the terrain path
        /// must keep working exactly as before when it does. Null here uploads
        /// a validity of 0, which every consumer reads as "use the fallback".
        /// </summary>
        public SceneCaptureRenderer SceneCapture { get; set; }

        /// <summary>Debug-only nearby block volume for the isolated world-DDA proof.</summary>
        public WorldReflectionVolume WorldVolume { get; set; }

        public PbrShaderBinder(ICoreClientAPI capi, MaterialAtlasSet atlas, MaterialAtlasSet atlas2,
                               Func<Dictionary<int, int>> buildPageMap,
                               Func<Dictionary<int, int>> buildSecondPageMap,
                               Func<SceneInputs> readScene)
        {
            _capi = capi;
            _atlas = atlas;
            _atlas2 = atlas2;
            _buildPageMap = buildPageMap;
            _buildSecondPageMap = buildSecondPageMap;
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
        public void SetState(bool enabled, PseudoPbrConfig look, float compareWipe)
        {
            _enabled = enabled;
            _look = look;
            _compareWipe = compareWipe;
        }

        /// <summary>
        /// Where the comparison wipe falls, as a fraction of frame width. 0 off.
        ///
        /// Held here rather than read from the root config because this binder
        /// deliberately sees only its own section - the one exception being a
        /// setting that is not a look at all but a diagnostic over the whole
        /// frame.
        /// </summary>
        private float _compareWipe;

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
            // The second atlas is a strict addition, so its upload is attempted
            // separately and its failure is not allowed to cost the first. A
            // missing second page means metalness, height, AO and the emission
            // mask fall back to neutral; a missing FIRST page means there is no
            // material system at all, and conflating the two would let a new
            // feature take out an established one.
            bool second = _atlas2 != null && _atlas2.HasPixels &&
                          _atlas2.EnsureUploaded(_capi, _capi.Logger);

            if (!second && _atlas2 != null && _atlas2.HasPixels && !_reportedNoSecond)
            {
                _reportedNoSecond = true;
                _capi.Logger.Warning("[VintageVisuals] pseudopbr: the second material atlas is not on the GPU. " +
                    "Metalness, height, baked AO and the emission mask fall back to neutral; everything else " +
                    "is unaffected.");
            }

            _secondAtlasReady = second;

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
            TerrainTextureBindInterceptor.SetPages(_buildPageMap(),
                _secondAtlasReady && _buildSecondPageMap != null ? _buildSecondPageMap() : null);

            int uploaded = 0;
            foreach (EnumShaderProgram id in PatchedPrograms)
            {
                if (Upload(id)) uploaded++;
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
        private bool Upload(EnumShaderProgram id)
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

            // Same story for the second page: bound to its own explicit unit,
            // and only when it is genuinely there. vv_material2Valid is
            // uploaded either way, and its zero is what makes every consumer
            // fall back rather than sample an unbound unit.
            if (_secondAtlasReady && program.HasUniform(SecondSamplerUniform))
            {
                program.BindTexture2D(SecondSamplerUniform, _atlas2.TextureIdFor(0),
                                      MaterialAtlasTexture.SecondTextureUnit);
            }

            SetIfPresent(program, SecondValidUniform, _secondAtlasReady ? 1f : 0f);

            BindSceneCapture(program);
            BindWorldReflectionVolume(program);

            SetIfPresent(program, EnabledUniform, 1f);

            // In frame PIXELS rather than a fraction, so the shader compares
            // against gl_FragCoord.x directly instead of every fragment
            // recomputing the width.
            SetIfPresent(program, CompareWipeUniform, _compareWipe * _capi.Render.FrameWidth);
            SetIfPresent(program, NormalStrengthUniform, _look.NormalStrength);
            SetIfPresent(program, SpecularStrengthUniform, _look.SpecularStrength);
            SetIfPresent(program, DebugViewUniform, _look.DebugView);
            SetIfPresent(program, RoughnessBiasUniform, _look.RoughnessBias);
            SetIfPresent(program, MetalResponseUniform, _look.MetalResponse);
            SetIfPresent(program, AmbientUniform, _look.AmbientSpecular);
            SetIfPresent(program, SpecularAaUniform, _look.SpecularAntiAliasing);
            SetIfPresent(program, DetailDistanceUniform, _look.DetailDistance);
            SetIfPresent(program, FoliageUniform, _look.FoliageTranslucency);
            SetIfPresent(program, CavityUniform, _look.CavityStrength);
            SetIfPresent(program, SpecOcclusionUniform, _look.SpecularOcclusion);
            SetIfPresent(program, EnergyCompensationUniform, _look.EnergyCompensation);
            SetIfPresent(program, GrainUniform, _look.GrainAnisotropy);
            SetIfPresent(program, DappleUniform, _look.SunDapple);
            SetIfPresent(program, CanopyRadiusUniform, _look.CanopyRadius);
            SetIfPresent(program, PixelReflectUniform, _look.PixelReflection);
            SetIfPresent(program, ShaftUniform, _look.SunShafts);
            SetIfPresent(program, BlockLightUniform, _look.BlockLightSpecular);
            SetIfPresent(program, BlockLightDirUniform, _look.BlockLightDirectionality);
            SetIfPresent(program, RainCoverUniform, _weather.RainCover);
            SetIfPresent(program, RipplesUniform, _weather.Ripples);
            SetIfPresent(program, RippleTimeUniform, _weather.RippleTime);
            SetIfPresent(program, OriginUniform, _weather.Origin);
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

                SetIfPresent(program, ParticleUniform, _look.ParticleLighting ? 1f : 0f);
                SetIfPresent(program, ParticleSpecularUniform, _look.ParticleSpecular);
                SetIfPresent(program, RoughnessBiasUniform, _look.RoughnessBias);
                SetIfPresent(program, AmbientUniform, _look.AmbientSpecular);
                SetIfPresent(program, SpecularAaUniform, _look.SpecularAntiAliasing);
                SetIfPresent(program, MetalResponseUniform, _look.MetalResponse);
                SetIfPresent(program, EnergyCompensationUniform, _look.EnergyCompensation);
                SetIfPresent(program, GrainUniform, _look.GrainAnisotropy);
                SetIfPresent(program, BlockLightUniform, _look.BlockLightSpecular);
                SetIfPresent(program, BlockLightDirUniform, _look.BlockLightDirectionality);
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
        /// The same values into every shaded program, from one place, so there
        /// is no way for two of them to be told different things.
        ///
        /// EVERY uniform scene.glsl declares goes through here, and nowhere
        /// else. Three of them used to be uploaded ad hoc by the terrain and
        /// entity paths instead, which meant the particle programs - which
        /// inject the same snippet - never received them at all. An unset
        /// vv_sceneDayLight reads as zero, zero multiplies the visibility term,
        /// and particle specular was silently dead.
        ///
        /// There is deliberately NO sentinel guard on this method. It had one -
        /// an early return unless the program had vv_sceneRestraint - and that
        /// guard was itself the bug it was meant to prevent. Only pseudopbr.glsl
        /// calls vvSceneVisibilityDampen(), so only the terrain programs READ
        /// vv_sceneRestraint; the compiler eliminates it everywhere else,
        /// HasUniform reports it absent, and the whole method returned before
        /// uploading anything to entities or particles. A sentinel is a claim
        /// that one uniform's fate predicts the rest, and in a snippet shared by
        /// five programs that shade differently, it does not.
        /// </summary>
        private void UploadScene(IShaderProgram program)
        {
            SetIfPresent(program, DayLightUniform, _weather.DayLight);
            SetIfPresent(program, WetnessUniform, _weather.Wetness);
            SetIfPresent(program, OvercastUniform, _weather.Overcast);
            SetIfPresent(program, ArtificialLightUniform, _weather.ArtificialLight);
            SetIfPresent(program, RestraintUniform, _weather.Restraint);
            SetIfPresent(program, ReadabilityUniform, _weather.Readability);
            SetIfPresent(program, ClockUniform, _weather.RippleTime);
            SetIfPresent(program, BreezeUniform, _weather.Breeze);
            SetIfPresent(program, FrostUniform, _weather.Frost);
            SetIfPresent(program, SnowUniform, _weather.Snow);

            SetIfPresent(program, EmissiveUniform, _look.EmissiveStrength);
            SetIfPresent(program, EmissiveTemperatureUniform, _look.EmissiveTemperature);
            SetIfPresent(program, EmissiveFlickerUniform, _look.EmissiveFlicker);
            SetIfPresent(program, EmissiveBloomUniform, _look.EmissiveBloom);
        }

        /// <summary>
        /// Uploads a uniform only if the linked program actually has it.
        ///
        /// A shared snippet is injected into programs that read different parts
        /// of it, and GLSL removes what a program does not read - so "declared
        /// in the snippet" and "present in this program" are different
        /// questions. Asking per name is a dictionary lookup; assuming from one
        /// name cost the particle path every scene value it needed.
        /// </summary>
        /// <summary>
        /// Hands the terrain program last frame's scene, and the transform it
        /// was drawn with.
        ///
        /// EVERY PATH UPLOADS A VALIDITY. A missing capture, a failed one, a
        /// program compiled before the feature existed - all of them end with
        /// vv_reflectValid at 0, and 0 means the shader uses the analytic
        /// fallback. An unset GLSL uniform also reads as 0, so the failure mode
        /// of forgetting to call this at all is the safe one, which is the only
        /// arrangement worth having for something that binds a texture unit.
        /// </summary>
        private void BindSceneCapture(IShaderProgram program)
        {
            if (!program.HasUniform(ReflectValidUniform)) return;

            SceneCaptureRenderer capture = SceneCapture;

            if (capture == null || !capture.HasCapture || capture.TextureId == 0)
            {
                TerrainTextureBindInterceptor.SetSceneCapture(0);
                program.Uniform(ReflectValidUniform, 0f);
                return;
            }

            // The per-draw rebind is what actually reaches the chunk draws; this
            // bind only makes the sampler uniform point at the right unit.
            TerrainTextureBindInterceptor.SetSceneCapture(capture.TextureId);

            program.BindTexture2D(ReflectSceneUniform, capture.TextureId,
                                  SceneCaptureRenderer.TextureUnit);

            SetIfPresent(program, ReflectViewProjUniform, capture.CaptureViewProjection);

            // CURRENT MINUS CAPTURE. The direction of this subtraction is the
            // whole correctness of the reprojection, and it shipped backwards.
            //
            // The shader has a point as `cameraRelative = world - currentOrigin`
            // and must hand the captured matrix `world - captureOrigin`. Those
            // differ by exactly (currentOrigin - captureOrigin):
            //
            //   world - captureOrigin
            //     = (world - currentOrigin) + (currentOrigin - captureOrigin)
            //
            // so the shader adds THIS. Supplying capture-minus-current instead
            // moves every reflected point the wrong way by twice the camera's
            // travel, which reads as the reflection sliding across surfaces as
            // the player walks - the exact failure the correction exists to
            // prevent, and indistinguishable from having no correction at all
            // except that it is worse.
            //
            // ORIGIN IS THE PLAYER, not the camera. CameraMatrixOriginf is
            // documented as "player camera matrix with PLAYER positioned at
            // 0,0,0", and chunkopaque.vsh builds its worldPos the same way -
            // `xyz + origin`, where origin is chunk-relative-to-player. Both
            // ends already agree on the player as the origin, and the camera's
            // own offset is baked into the matrix. Subtracting CameraOffset here
            // would introduce an error rather than remove one.
            EntityPos now = _capi.World?.Player?.Entity?.Pos;
            Vec3d then = capture.CapturePosition;

            Vec3f delta = now == null
                ? new Vec3f()
                : new Vec3f((float)(now.X - then.X), (float)(now.Y - then.Y), (float)(now.Z - then.Z));

            SetIfPresent(program, ReflectCameraDeltaUniform, delta);
            SetIfPresent(program, ReflectFarUniform, _capi.Render.ShaderUniforms.ZFar);
            SetIfPresent(program, ReflectFrameSizeUniform,
                         new Vec2f(_capi.Render.FrameWidth, _capi.Render.FrameHeight));

            program.Uniform(ReflectValidUniform, 1f);
        }

        /// <summary>
        /// Uploads the local world-space reflection volume for reflective
        /// gameplay and the world-volume debug views.
        /// </summary>
        private void BindWorldReflectionVolume(IShaderProgram program)
        {
            if (!program.HasUniform(ReflectWorldValidUniform)) return;

            bool activeDebugView = _look.DebugView >= 53f && _look.DebugView <= 58f;
            bool activeReflection = _look.PixelReflection > 0.001f;
            bool activeCanopy = _look.SunDapple > 0.001f || _look.SunShafts > 0.001f ||
                                (_look.DebugView >= 59f && _look.DebugView <= 62f);
            EntityPos now = _capi.World?.Player?.Entity?.Pos;
            WorldReflectionVolume volume = WorldVolume;

            if ((!activeDebugView && !activeReflection && !activeCanopy) || volume == null || now == null ||
                !volume.EnsureUploaded(_capi, _capi.Logger, now))
            {
                TerrainTextureBindInterceptor.SetWorldReflection(0);
                TerrainTextureBindInterceptor.SetCanopyContext(0);
                program.Uniform(ReflectWorldValidUniform, 0f);
                SetIfPresent(program, CanopyContextValidUniform, 0f);
                return;
            }

            if (activeDebugView || activeReflection)
            {
                TerrainTextureBindInterceptor.SetWorldReflection(volume.TextureId);
                program.BindTexture2D(ReflectWorldUniform, volume.TextureId, WorldReflectionVolume.TextureUnit);

                SetIfPresent(program, ReflectWorldOriginUniform, volume.OriginRelativeToPlayer(now));
                SetIfPresent(program, ReflectWorldSizeUniform, volume.Size);
                SetIfPresent(program, ReflectWorldSliceSizeUniform, volume.SliceSize);
                SetIfPresent(program, ReflectWorldAtlasGridUniform, volume.AtlasGrid);
                SetIfPresent(program, ReflectWorldAtlasSizeUniform, volume.AtlasSize);
                program.Uniform(ReflectWorldValidUniform, 1f);
            }
            else
            {
                TerrainTextureBindInterceptor.SetWorldReflection(0);
                program.Uniform(ReflectWorldValidUniform, 0f);
            }

            if (program.HasUniform(CanopyContextUniform) && volume.CanopyTextureId != 0)
            {
                TerrainTextureBindInterceptor.SetCanopyContext(volume.CanopyTextureId);
                program.BindTexture2D(CanopyContextUniform, volume.CanopyTextureId,
                                      WorldReflectionVolume.CanopyTextureUnit);
                SetIfPresent(program, CanopyContextValidUniform, 1f);
            }
            else
            {
                TerrainTextureBindInterceptor.SetCanopyContext(0);
                SetIfPresent(program, CanopyContextValidUniform, 0f);
            }
        }

        private static void SetIfPresent(IShaderProgram program, string name, float value)
        {
            if (program.HasUniform(name)) program.Uniform(name, value);
        }

        /// <summary>Same rule for the vec3 uploads.</summary>
        private static void SetIfPresent(IShaderProgram program, string name, Vec2f value)
        {
            if (program.HasUniform(name)) program.Uniform(name, value);
        }

        /// <summary>A 4x4 matrix, for the captured view-projection.</summary>
        private static void SetIfPresent(IShaderProgram program, string name, float[] matrix)
        {
            if (matrix != null && matrix.Length == 16 && program.HasUniform(name))
            {
                program.UniformMatrix(name, matrix);
            }
        }

        private static void SetIfPresent(IShaderProgram program, string name, Vec3f value)
        {
            if (program.HasUniform(name)) program.Uniform(name, value);
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

                SetIfPresent(program, EntityEnabledUniform, _look.EntityLighting ? 1f : 0f);
                SetIfPresent(program, EntityRoughnessUniform, _look.EntityRoughness);
                SetIfPresent(program, EntitySpecularUniform, _look.EntitySpecular);
                SetIfPresent(program, EntityDebugUniform, _look.EntityDebugView);
                SetIfPresent(program, RoughnessBiasUniform, _look.RoughnessBias);
                SetIfPresent(program, MetalResponseUniform, _look.MetalResponse);
                SetIfPresent(program, EnergyCompensationUniform, _look.EnergyCompensation);
                SetIfPresent(program, GrainUniform, _look.GrainAnisotropy);
                SetIfPresent(program, AmbientUniform, _look.AmbientSpecular);
                SetIfPresent(program, SpecularAaUniform, _look.SpecularAntiAliasing);
                SetIfPresent(program, BlockLightUniform, _look.BlockLightSpecular);
                SetIfPresent(program, BlockLightDirUniform, _look.BlockLightDirectionality);
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
            TerrainTextureBindInterceptor.SetSceneCapture(0);
            TerrainTextureBindInterceptor.SetWorldReflection(0);
            TerrainTextureBindInterceptor.SetCanopyContext(0);

            if (_programsThinkEnabled)
            {
                foreach (EnumShaderProgram id in PatchedPrograms)
                {
                    IShaderProgram program = _capi.Shader.GetProgram((int)id);
                    if (program == null || !program.HasUniform(EnabledUniform)) continue;

                    program.Use();
                    SetIfPresent(program, EnabledUniform, 0f);
                    program.Stop();
                }

                _programsThinkEnabled = false;
            }

            if (_atlas2 != null && _atlas2.AnyUploaded)
            {
                _atlas2.Release();
                _secondAtlasReady = false;
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
