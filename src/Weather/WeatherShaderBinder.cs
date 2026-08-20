using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VintageVisuals.Common;

namespace VintageVisuals.Weather
{
    /// <summary>
    /// Uploads the weather uniforms into the vanilla programs the weather
    /// patches touch.
    ///
    /// Separate from PbrShaderBinder rather than folded into it. The two are
    /// different patch groups and either has to be able to roll back without
    /// the other noticing, which a shared binder would quietly prevent. Every
    /// uniform here goes to every program in its own list, and a program the
    /// group did not reach falls out on HasUniform.
    /// </summary>
    public sealed class WeatherShaderBinder : IRenderer
    {
        /// <summary>Before the sky (0.1) and before terrain (0.37), so every consumer sees this frame's values.</summary>
        private const double UploadBeforeEverything = 0.05;

        public const string RainUniform = "vv_weatherRain";
        public const string FogStrengthUniform = "vv_weatherFogStrength";
        public const string FogTintUniform = "vv_weatherFogTint";

        public const string CloudShadowUniform = "vv_cloudShadowStrength";
        public const string CloudCoverUniform = "vv_cloudCover";
        public const string CloudScaleUniform = "vv_cloudScale";
        public const string CloudDriftUniform = "vv_cloudDrift";
        public const string CloudHeightUniform = "vv_cloudHeight";
        public const string CloudOriginUniform = "vv_cloudOrigin";

        /// <summary>
        /// Every program a weather patch reaches: the two terrain shaders, and
        /// nothing else.
        ///
        /// Neither the sky nor either cloud renderer is patched, deliberately.
        /// Fogging the sky dome flattens cloud against sky into a uniform haze,
        /// and the clouds' own shape is not something a cloud shader decides -
        /// see weather.glsl and src/Weather/README.md.
        /// </summary>
        private static readonly EnumShaderProgram[] PatchedPrograms =
        {
            EnumShaderProgram.Chunkopaque,
            EnumShaderProgram.Chunktopsoil,
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
                        ", so the weather GLSL did not reach any compiled program. Rain fog and cloud " +
                        "shadows are inactive - look for a weather patch failure above.");
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
        /// HasUniform per name rather than per program, so a group that rolled
        /// back simply stops matching instead of needing to be tracked.
        /// </summary>
        private bool Upload(EnumShaderProgram id, WeatherConfig config)
        {
            IShaderProgram program = _capi.Shader.GetProgram((int)id);
            if (program == null) return false;

            bool fog = program.HasUniform(RainUniform);
            bool shadows = program.HasUniform(CloudShadowUniform);

            if (!fog && !shadows) return false;

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

            if (shadows)
            {
                // Scaled by daylight here rather than in the shader. A cloud
                // shadow is the sun being blocked, so at night there is nothing
                // to block and the ground should be exactly as vanilla left it.
                // Folding it into the strength keeps that as one uniform whose
                // zero already means "vanilla", instead of a second one that
                // has to be uploaded for the first to behave.
                float strength = enabled ? config.CloudShadowStrength * DayLight() : 0f;

                program.Uniform(CloudShadowUniform, strength);
                program.Uniform(CloudCoverUniform, _weather.CloudCover);
                program.Uniform(CloudScaleUniform, config.CloudScale);
                program.Uniform(CloudHeightUniform, config.CloudHeight);
                program.Uniform(CloudDriftUniform, _weather.CloudDrift);

                // Camera world position, so cloud shadows stay put on the
                // ground instead of sliding with the player. The chunk shaders
                // only have camera-relative positions.
                program.Uniform(CloudOriginUniform, _weather.CameraOrigin);
            }

            program.Stop();
            return true;
        }

        /// <summary>
        /// How much sun there is to be blocked, 0..1.
        ///
        /// Squared toward the end of the day rather than used raw: the calendar
        /// still reports a good fraction of full daylight while the sun is on
        /// the horizon, and a cloud shadow at that point falls across ground the
        /// sun is barely reaching anyway.
        /// </summary>
        private float DayLight()
        {
            var calendar = _capi.World?.Calendar;
            if (calendar == null) return 1f;

            float light = GameMath.Clamp(calendar.DayLightStrength, 0f, 1f);
            return light * light;
        }

        public void Dispose()
        {
        }
    }
}
