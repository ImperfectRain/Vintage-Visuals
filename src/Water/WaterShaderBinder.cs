using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VintageVisuals.Common;
using VintageVisuals.Common.Diagnostics;
using VintageVisuals.Common.Scene;

namespace VintageVisuals.Water
{
    /// <summary>
    /// Uploads the water uniforms to chunkliquid once a frame, at
    /// <see cref="EnumRenderStage.Before"/> (the stage documented as having no
    /// program bound), after the environment tracker has sampled the world.
    ///
    /// Every uniform's zero is vanilla (water.glsl), so a skipped frame or a
    /// rolled-back group is safe. HasUniform on the master is the ground truth
    /// for "did the patch reach the GPU", as in every other binder.
    /// </summary>
    public sealed class WaterShaderBinder : IRenderer
    {
        public const string MasterUniform = "vv_water";
        public const string OriginUniform = "vv_waterOrigin";
        public const string EyeUniform = "vv_waterEye";
        public const string SunDirUniform = "vv_waterSunDir";
        public const string SunColorUniform = "vv_waterSunColor";
        public const string MoonDirUniform = "vv_waterMoonDir";
        public const string SkyUniform = "vv_waterSky";
        public const string SoftUniform = "vv_waterSoft";
        public const string SoftTapsUniform = "vv_waterSoftTaps";
        public const string DepthTintUniform = "vv_waterDepthTint";
        public const string ClarityUniform = "vv_waterClarity";
        public const string RipplesUniform = "vv_waterRipples";
        public const string RippleTimeUniform = "vv_waterRippleTime";
        public const string RippleEpochUniform = "vv_waterRippleEpoch";
        public const string RippleLayersUniform = "vv_waterRippleLayers";
        public const string OwnDropsUniform = "vv_waterOwnDrops";
        public const string GlintsUniform = "vv_waterGlints";
        public const string WavesUniform = "vv_waterWaves";
        public const string CausticsUniform = "vv_waterCaustics";
        public const string CausticWavesUniform = "vv_waterCausticWaves";
        public const string ShoreUniform = "vv_waterShore";
        public const string DebugUniform = "vv_waterDebug";

        // waterscene group (D82). Present only while that group is applied.
        public const string SceneOnUniform = "vv_waterSceneOn";
        public const string SceneStepsUniform = "vv_waterSceneSteps";
        public const string SceneMixUniform = "vv_waterSceneMix";
        public const string RefractionUniform = "vv_waterRefraction";


        /// <summary>After the environment tracker (-1) and the atmosphere (-1) have sampled.</summary>
        private const double UploadOrder = 0.2;

        private readonly ICoreClientAPI _capi;
        private readonly VintageVisualsModSystem _mod;
        private readonly Vec3f _v3 = new Vec3f();

        private int _consecutiveSkips;
        private bool _reportedSkipping;
        private bool _reportedActive;
        private bool _reportedMissing;

        public WaterShaderBinder(ICoreClientAPI capi, VintageVisualsModSystem mod)
        {
            _capi = capi;
            _mod = mod;
        }

        public double RenderOrder { get { return UploadOrder; } }
        public int RenderRange { get { return 0; } }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Before) return;

            if (_capi.Render.CurrentActiveShader != null)
            {
                _consecutiveSkips++;
                if (_consecutiveSkips > 300 && !_reportedSkipping)
                {
                    _reportedSkipping = true;
                    _capi.Logger.Warning("[VintageVisuals] water: a shader program has been bound on every " +
                        "frame for several seconds, so no water uniform has been uploaded. The water draws " +
                        "as vanilla, and nothing else will say so.");
                }
                return;
            }
            _consecutiveSkips = 0;

            IShaderProgram program = _capi.Shader.GetProgram((int)EnumShaderProgram.Chunkliquid);
            if (program == null || !program.HasUniform(MasterUniform))
            {
                if (!_reportedMissing && _mod.ConfigManager.Config.Water.Enabled && _reportedActive)
                {
                    _reportedMissing = true;
                    _capi.Logger.Warning("[VintageVisuals] water: chunkliquid no longer exposes " + MasterUniform +
                        " - the water patch group did not reach the compiled program after a reload.");
                }
                return;
            }
            _reportedMissing = false;

            WaterConfig config = _mod.ConfigManager.Config.Water;
            EnvironmentTracker tracker = _mod.Environment;
            EnvironmentState world = tracker != null ? tracker.Current : EnvironmentState.Clear;
            AtmosphereState air = tracker != null ? tracker.Atmosphere : AtmosphereState.Clear;

            program.Use();

            program.Uniform(MasterUniform, config.Enabled ? 1f : 0f);
            program.Uniform(OriginUniform, world.CameraPosition);
            program.Uniform(EyeUniform, EyeFromViewMatrix(_capi.Render.CameraMatrixOriginf));

            SetDir(program, SunDirUniform, air.SunDirection, 0f, 1f, 0f);
            SetDir(program, MoonDirUniform, air.MoonDirection, 0f, -1f, 0f);
            Vec3f sun = air.SunColor;
            _v3.Set(sun == null ? 1f : sun.X, sun == null ? 1f : sun.Y, sun == null ? 1f : sun.Z);
            program.Uniform(SunColorUniform, _v3);

            program.Uniform(SkyUniform, config.SkyReflection);
            program.Uniform(SoftUniform, config.ReflectionSoftness);
            program.Uniform(SoftTapsUniform, QualityTier.WaterSoftTaps);
            program.Uniform(WavesUniform, config.Waves);
            program.Uniform(DepthTintUniform, config.DepthTint);
            program.Uniform(ClarityUniform, config.ShallowClarity);

            // Driven by the rain FALLING, like the terrain ripples: they stop
            // the moment the shower does.
            program.Uniform(RipplesUniform, Clamp01(world.Rain) * config.RainRipples);
            program.Uniform(RippleTimeUniform, tracker != null ? tracker.RippleClock : 0f);
            program.Uniform(RippleEpochUniform, tracker != null ? tracker.RippleEpoch : 0f);
            program.Uniform(RippleLayersUniform, QualityTier.WaterRippleLayers);
            // One drop system: vanilla's fixed-grid droplet rings step aside while ours are on.
            program.Uniform(OwnDropsUniform, config.RainRipples > 0.001f ? 1f : 0f);
            program.Uniform(GlintsUniform, config.Glints);
            program.Uniform(CausticsUniform, config.Caustics);
            program.Uniform(CausticWavesUniform, QualityTier.WaterCausticWaves);
            program.Uniform(ShoreUniform, config.Shore);
            program.Uniform(DebugUniform, config.DebugView);
            UploadScene(program, config);

            // The scene vocabulary (scene.glsl) goes through the ONE shared
            // upload path, so the water reads daylight, overcast, wind and
            // moonlight exactly as the terrain does. Last frame's values: the
            // terrain binder refreshes them in the opaque stage. Without
            // PseudoPBR they stay zero, which only switches the glints off.
            if (_mod.Pbr != null && _mod.Pbr.Binder != null) _mod.Pbr.Binder.UploadSceneTo(program);

            program.Stop();

            if (!_reportedActive)
            {
                _reportedActive = true;
                _capi.Logger.Notification("[VintageVisuals] water: surface active on chunkliquid (quality " +
                                          QualityTier.Name(QualityTier.Current) + ").");
            }
        }

        /// <summary>
        /// The waterscene group's inputs, and the texture the bind hook puts
        /// on its unit when vanilla binds the opaque depth. Guarded on
        /// HasUniform: with the group rolled back or switched off there is no
        /// sampler, the hook is told 0 and binds nothing.
        ///
        /// vv_waterSceneOn is 1 only with a colour texture to bind. The
        /// sampler unit is assigned here too (an unbound sampler2D reads unit
        /// 0, the block atlas), so the program never samples a wrong texture
        /// even on the frame before the hook first fires.
        /// </summary>
        private void UploadScene(IShaderProgram program, WaterConfig config)
        {
            if (!program.HasUniform(WaterSceneBindInterceptor.SceneSampler))
            {
                WaterSceneBindInterceptor.SetSceneTexture(0);
                return;
            }

            int colour = PrimaryColourTexture();
            WaterSceneBindInterceptor.SetSceneTexture(colour);
            bool on = colour != 0 && config.SceneReflections;

            program.Uniform(WaterSceneBindInterceptor.SceneSampler, WaterSceneBindInterceptor.TextureUnit);
            program.Uniform(SceneOnUniform, on ? 1f : 0f);
            program.Uniform(SceneStepsUniform, QualityTier.WaterMarchSteps);
            program.Uniform(SceneMixUniform, on ? 1f : 0f);
            program.Uniform(RefractionUniform, on ? config.Refraction : 0f);
        }

        /// <summary>Primary's colour attachment 0: the finished opaque frame. 0 if absent.</summary>
        private int PrimaryColourTexture()
        {
            var buffers = _capi.Render.FrameBuffers;
            int index = (int)EnumFrameBuffer.Primary;
            if (buffers == null || index < 0 || index >= buffers.Count) return 0;
            FrameBufferRef primary = buffers[index];
            if (primary == null || primary.Disposed || primary.ColorTextureIds == null ||
                primary.ColorTextureIds.Length == 0) return 0;
            return primary.ColorTextureIds[0];
        }

        private void SetDir(IShaderProgram program, string name, Vec3f dir, float fx, float fy, float fz)
        {
            if (dir == null || dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z < 1e-8f) _v3.Set(fx, fy, fz);
            else
            {
                float inv = 1f / dir.Length();
                _v3.Set(dir.X * inv, dir.Y * inv, dir.Z * inv);
            }
            program.Uniform(name, _v3);
        }

        private readonly Vec3f _eye = new Vec3f();

        /// <summary>
        /// Where the eye is in the space chunkliquid's fWorldPos is measured
        /// in. That space has the PLAYER at 0,0,0 (CameraMatrixOriginf is "the
        /// player camera matrix with the player at the origin", and the chunk
        /// vertex shader builds worldPos the same way), so the eye height and
        /// any third-person offset live only in the matrix. The eye is the
        /// point the view matrix maps to the origin: -R^T t, column-major.
        /// Anything implausible uploads zero, the previous behaviour.
        /// </summary>
        internal Vec3f EyeFromViewMatrix(float[] m)
        {
            EyeFromView(m, _eye);
            return _eye;
        }

        /// <summary>Pure form of <see cref="EyeFromViewMatrix"/>, for tools/smoketest.</summary>
        public static void EyeFromView(float[] m, Vec3f into)
            => VintageVisuals.Common.Scene.ViewEye.FromView(m, into);

        private static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

        public void Dispose() { }
    }
}
