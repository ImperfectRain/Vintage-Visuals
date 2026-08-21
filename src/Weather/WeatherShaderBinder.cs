using System;
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
        /// <summary>
        /// Runs at EnumRenderStage.Before, like ColorGrade.
        ///
        /// It used to run at Opaque with order 0.05, which is a stage this mod
        /// has only ever confirmed working at 0.35 (PseudoPBR). If anything
        /// holds a program that early in the stage, Use() would throw - so the
        /// binder skips the frame, forever, without a word. Cloud shadows
        /// failing to appear three times with nothing in the log is exactly the
        /// shape that bug would have, and Before is the one stage this project
        /// has documented as guaranteed quiet. See _consecutiveSkips for the
        /// change that makes this class of failure impossible to miss again.
        /// </summary>
        private const double UploadOrder = 0.1;

        public const string RainUniform = "vv_weatherRain";
        public const string FogStrengthUniform = "vv_weatherFogStrength";
        public const string FogTintUniform = "vv_weatherFogTint";

        public const string CloudShadowUniform = "vv_cloudShadowStrength";
        public const string CloudCoverUniform = "vv_cloudCover";
        public const string CloudScaleUniform = "vv_cloudScale";
        public const string CloudDriftUniform = "vv_cloudDrift";
        public const string CloudHeightUniform = "vv_cloudHeight";
        public const string CloudOriginUniform = "vv_cloudOrigin";
        public const string CloudDebugUniform = "vv_cloudDebug";
        public const string CloudTilesUniform = "vv_cloudTiles";
        public const string CloudMapCornerUniform = "vv_cloudMapCorner";
        public const string CloudMapValidUniform = "vv_cloudMapValid";

        /// <summary>vec4s in the cloud tile window: four tiles packed per vec4.</summary>
        private const int CloudVectors = CloudTileReader.Window * CloudTileReader.Window / 4;

        /// <summary>Reused so a per-frame upload allocates nothing.</summary>
        private readonly float[] _cloudPacked = new float[CloudVectors * 4];

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

        /// <summary>
        /// Frames skipped in a row because a program was already bound.
        ///
        /// A binder that silently returns is indistinguishable from a binder
        /// that is working, and this mod has now lost three rounds of debugging
        /// to effects that were simply never uploaded. After a few seconds of
        /// it, say so.
        /// </summary>
        private int _consecutiveSkips;
        private bool _reportedSkipping;

        /// <summary>What was last uploaded, so the log fires on a change instead of every frame.</summary>
        private float _loggedStrength = -1f;
        private float _loggedCover = -1f;

        public WeatherShaderBinder(ICoreClientAPI capi, WeatherSubsystem weather)
        {
            _capi = capi;
            _weather = weather;
        }

        public double RenderOrder { get { return UploadOrder; } }

        public int RenderRange { get { return 0; } }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Before) return;

            // Use() throws if a program is already bound and the client does
            // not recover. See ColorGrade for the full story.
            if (_capi.Render.CurrentActiveShader != null)
            {
                _consecutiveSkips++;

                if (_consecutiveSkips > 300 && !_reportedSkipping)
                {
                    _reportedSkipping = true;
                    _capi.Logger.Warning("[VintageVisuals] weather: a shader program has been bound on every " +
                        "frame for several seconds, so no weather uniform has been uploaded at all. Rain fog " +
                        "and cloud shadows are inactive, and nothing else will say so.");
                }

                return;
            }

            _consecutiveSkips = 0;

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
                // Scaled by what arbitration allowed, not by the config value.
                // Three subsystems used to wash out the same rainy afternoon
                // independently; now they share one allowance.
                program.Uniform(FogStrengthUniform, config.FogStrength * _weather.Grants.RainFog);
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
                float strength = enabled
                    ? config.CloudShadowStrength * DayLight() * _weather.Grants.CloudShadow
                    : 0f;

                program.Uniform(CloudShadowUniform, strength);
                program.Uniform(CloudCoverUniform, _weather.CloudCover);
                program.Uniform(CloudScaleUniform, config.CloudScale);
                program.Uniform(CloudHeightUniform, config.CloudHeight);
                program.Uniform(CloudDriftUniform, _weather.CloudDrift);

                // Camera world position, so cloud shadows stay put on the
                // ground instead of sliding with the player. The chunk shaders
                // only have camera-relative positions.
                program.Uniform(CloudOriginUniform, _weather.CameraOrigin);
                program.Uniform(CloudDebugUniform, config.CloudDebugView);

                UploadCloudMap(program, config);

                ReportShadowState(id, strength);
            }

            program.Stop();
            return true;
        }

        /// <summary>
        /// Hands over the game's own cloud placement, or says it is unavailable.
        ///
        /// vv_cloudMapValid is uploaded either way and its zero is the fallback,
        /// so a program that never received this - unpatched, unbound, rolled
        /// back - reads exactly what a failed tile read produces rather than
        /// sampling an array of zeros as though it meant a cloudless sky.
        /// </summary>
        private void UploadCloudMap(IShaderProgram program, WeatherConfig config)
        {
            if (!program.HasUniform(CloudMapValidUniform)) return;

            CloudTileReader clouds = _weather.Clouds;
            bool valid = config.CloudsFromGame && clouds != null && clouds.Available;

            program.Uniform(CloudMapValidUniform, valid ? 1f : 0f);
            if (!valid) return;

            float[] density = clouds.Density;
            for (int i = 0; i < _cloudPacked.Length && i < density.Length; i++)
            {
                _cloudPacked[i] = density[i];
            }

            // The array name without a subscript. GL accepts the first element's
            // location for the whole array, and which spelling a given driver
            // reports through introspection is not something to depend on.
            program.Uniforms4(CloudTilesUniform, CloudVectors, _cloudPacked);
            program.Uniform(CloudMapCornerUniform, clouds.Origin);
        }

        /// <summary>
        /// Says what the cloud shadows are actually being driven with, once and
        /// again whenever it meaningfully changes.
        ///
        /// Every term here has been suspected of being the zero that made the
        /// effect invisible, and each round was spent guessing which. One log
        /// line ends that: if the shadows are absent and this says the strength
        /// is 0.3 with cover 0.4, the fault is in the GLSL, and if it never
        /// appears at all the fault is that this code does not run.
        /// </summary>
        private void ReportShadowState(EnumShaderProgram id, float strength)
        {
            if (Math.Abs(strength - _loggedStrength) < 0.02f &&
                Math.Abs(_weather.CloudCover - _loggedCover) < 0.05f) return;

            _loggedStrength = strength;
            _loggedCover = _weather.CloudCover;

            Vec3f origin = _weather.CameraOrigin;

            _capi.Logger.Notification("[VintageVisuals] weather: cloud shadows on " + id +
                " - strength " + strength.ToString("0.###") +
                " (config " + _weather.Config.CloudShadowStrength.ToString("0.##") +
                " x daylight " + _weather.DayLight.ToString("0.##") + ")" +
                ", cover " + _weather.CloudCover.ToString("0.##") +
                ", scale " + _weather.Config.CloudScale.ToString("0") +
                ", deck " + _weather.Config.CloudHeight.ToString("0") +
                ", drift " + _weather.CloudDrift.X.ToString("0.##") + "/" + _weather.CloudDrift.Y.ToString("0.##") +
                ", origin " + origin.X.ToString("0") + "/" + origin.Y.ToString("0") + "/" + origin.Z.ToString("0") +
                ", source " + (_weather.Clouds != null && _weather.Clouds.Available && _weather.Config.CloudsFromGame
                    ? "the game's own cloud tiles"
                    : "the mod's noise field (will NOT line up with the sky)") +
                (_weather.Config.CloudDebugView > 0.5f
                    ? " [DEBUG VIEW " + (int)(_weather.Config.CloudDebugView + 0.5f) + "]"
                    : ""));
        }

        /// <summary>
        /// How much sun there is to be blocked, 0..1.
        ///
        /// Squared toward the end of the day rather than used raw: the calendar
        /// still reports a good fraction of full daylight while the sun is on
        /// the horizon, and a cloud shadow at that point falls across ground the
        /// sun is barely reaching anyway.
        ///
        /// Read from the shared environment state rather than from the calendar
        /// directly, so this subsystem and colour grading cannot end up
        /// disagreeing about what time it is.
        /// </summary>
        private float DayLight()
        {
            float light = GameMath.Clamp(_weather.DayLight, 0f, 1f);
            return light * light;
        }

        public void Dispose()
        {
        }
    }
}
