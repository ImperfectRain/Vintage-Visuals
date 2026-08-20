using Vintagestory.API.Client;
using VintageVisuals.Common;

namespace VintageVisuals.Weather
{
    /// <summary>
    /// Uploads the weather uniforms into the vanilla programs the weather
    /// patches touch.
    ///
    /// Separate from PbrShaderBinder rather than folded into it. The two reach
    /// different programs - weather also patches the sky and the volumetric
    /// clouds, neither of which knows anything about materials - and a single
    /// binder for both would have to keep straight which uniforms exist in
    /// which shader. Every uniform here goes to every program in its own list,
    /// and programs that were not patched fall out on HasUniform.
    /// </summary>
    public sealed class WeatherShaderBinder : IRenderer
    {
        /// <summary>Before the sky (0.1) and before terrain (0.37), so every consumer sees this frame's values.</summary>
        private const double UploadBeforeEverything = 0.05;

        public const string RainUniform = "vv_weatherRain";
        public const string CoverUniform = "vv_weatherCover";
        public const string FogStrengthUniform = "vv_weatherFogStrength";
        public const string FogTintUniform = "vv_weatherFogTint";

        public const string CloudShadowUniform = "vv_cloudShadowStrength";
        public const string CloudCoverUniform = "vv_cloudCover";
        public const string CloudScaleUniform = "vv_cloudScale";
        public const string CloudDriftUniform = "vv_cloudDrift";
        public const string CloudOriginUniform = "vv_cloudOrigin";

        public const string CloudDetailUniform = "vv_cloudDetail";

        /// <summary>
        /// Every program a weather patch reaches. Chunkopaque and Chunktopsoil
        /// take fog and cloud shadows, Cloudvolumetric takes cloud shaping -
        /// but nothing here needs to know that, because each program only
        /// accepts the uniforms it actually declares.
        ///
        /// The sky is deliberately absent. Fogging the sky dome flattens cloud
        /// against sky into a uniform haze; see weather.glsl.
        /// </summary>
        private static readonly EnumShaderProgram[] PatchedPrograms =
        {
            EnumShaderProgram.Chunkopaque,
            EnumShaderProgram.Chunktopsoil,
            EnumShaderProgram.Cloudvolumetric,
        };

        private readonly ICoreClientAPI _capi;
        private readonly WeatherSubsystem _weather;

        private bool _reportedMissing;
        private bool _reportedActive;

        public WeatherShaderBinder(ICoreClientAPI capi, WeatherSubsystem weather)
        {
            _capi = capi;
            _weather = weather;
        }

        public double RenderOrder { get { return UploadBeforeEverything; } }

        public int RenderRange { get { return 0; } }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Opaque) return;

            // Use() throws if a program is already bound and the client does
            // not recover. See ColorGrade for the full story.
            if (_capi.Render.CurrentActiveShader != null) return;

            WeatherConfig config = _weather.Config;

            int uploaded = 0;
            foreach (EnumShaderProgram id in PatchedPrograms)
            {
                if (Upload(id, config)) uploaded++;
            }

            if (uploaded == 0)
            {
                if (!_reportedMissing)
                {
                    _reportedMissing = true;
                    _capi.Logger.Warning("[VintageVisuals] weather: no patched program exposes " + RainUniform +
                        " or " + CloudDetailUniform + ", so the weather GLSL did not reach any compiled " +
                        "program. Fog, cloud shadows and cloud shaping are inactive - look for a weather, " +
                        "weathersky or cloudshape patch failure above.");
                }
                return;
            }

            _reportedMissing = false;

            if (!_reportedActive)
            {
                _reportedActive = true;
                _capi.Logger.Notification("[VintageVisuals] weather: active, uploading to " + uploaded +
                                          " of " + PatchedPrograms.Length + " patched program(s).");
            }
        }

        /// <summary>
        /// Pushes whichever of the weather uniforms this program declares.
        ///
        /// HasUniform per name rather than per program, so the same code serves
        /// the terrain shaders, the sky and the cloud raymarcher without a
        /// table saying which is which - and a group that rolled back simply
        /// stops matching instead of needing to be tracked.
        /// </summary>
        private bool Upload(EnumShaderProgram id, WeatherConfig config)
        {
            IShaderProgram program = _capi.Shader.GetProgram((int)id);
            if (program == null) return false;

            bool fog = program.HasUniform(RainUniform);
            bool clouds = program.HasUniform(CloudDetailUniform);
            bool shadows = program.HasUniform(CloudShadowUniform);

            if (!fog && !clouds && !shadows) return false;

            bool enabled = config.Enabled;

            program.Use();

            if (fog)
            {
                // Rain drives fog directly rather than through wetness: fog
                // thickens while it is raining, not while the ground is still
                // drying an hour later.
                program.Uniform(RainUniform, enabled ? _weather.Rain : 0f);
                program.Uniform(FogStrengthUniform, config.FogStrength);
                program.Uniform(FogTintUniform, config.FogTint);
            }

            if (program.HasUniform(CoverUniform))
            {
                program.Uniform(CoverUniform, config.RainCoverThreshold);
            }

            if (shadows)
            {
                program.Uniform(CloudShadowUniform, enabled ? config.CloudShadowStrength : 0f);
                program.Uniform(CloudCoverUniform, _weather.CloudCover);
                program.Uniform(CloudScaleUniform, config.CloudScale);
                program.Uniform(CloudDriftUniform, _weather.CloudDrift);

                // Camera world position, so cloud shadows stay put on the
                // ground instead of sliding with the player. The chunk shaders
                // only have camera-relative positions.
                program.Uniform(CloudOriginUniform, _weather.CameraOrigin);
            }

            if (clouds)
            {
                program.Uniform(CloudDetailUniform, enabled ? config.CloudDetail : 0f);
            }

            program.Stop();
            return true;
        }

        public void Dispose()
        {
        }
    }
}
