using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VintageVisuals.Common;
using VintageVisuals.Common.Scene;

namespace VintageVisuals.Atmosphere
{
    /// <summary>
    /// Uploads the atmosphere uniforms into every program the atmosphere patch
    /// group reached.
    ///
    /// Its own binder rather than a branch of the weather one. The two are
    /// separate patch groups, either has to be able to roll back without the
    /// other noticing, and a shared binder would quietly prevent that.
    ///
    /// Four programs, and the list matters: the point of this group is that
    /// terrain, ground cover, entities and particles all get the SAME answer.
    /// Every uniform goes to every program in the list, and a program the group
    /// did not reach falls out on HasUniform rather than needing to be tracked.
    /// </summary>
    public sealed class AtmosphereShaderBinder : IRenderer
    {
        /// <summary>
        /// EnumRenderStage.Before, like every other binder in this mod.
        ///
        /// The one stage this project has documented as guaranteed quiet. A
        /// binder registered somewhere a program is always bound skips every
        /// frame forever and says nothing, which has cost three rounds of
        /// debugging here already - see _consecutiveSkips.
        /// </summary>
        private const double UploadOrder = 0.1;

        // The eleven features, one uniform each. No separate enabled flag:
        // zero is already the vanilla value and is what an unset uniform reads
        // as, so a second flag would be a second way to say the same thing.
        public const string AerialUniform = "vv_atmosAerial";
        public const string HorizonUniform = "vv_atmosHorizon";
        public const string SunScatterUniform = "vv_atmosSunScatter";
        public const string HeightUniform = "vv_atmosHeight";
        public const string WeatherUniform = "vv_atmosWeather";
        public const string CloudUniform = "vv_atmosCloud";
        public const string CloudEdgeUniform = "vv_atmosCloudEdge";
        public const string GodrayUniform = "vv_atmosGodray";
        public const string PrecipUniform = "vv_atmosPrecip";
        public const string MoonUniform = "vv_atmosMoon";
        public const string DappleUniform = "vv_atmosDapple";
        public const string WeatherTintUniform = "vv_atmosWeatherTint";

        // Normalised world state. Every one read from the game, none simulated.
        public const string SunDirUniform = "vv_atmosSunDir";
        public const string SunColorUniform = "vv_atmosSunColor";
        public const string SunElevationUniform = "vv_atmosSunElevation";
        public const string MoonDirUniform = "vv_atmosMoonDir";
        public const string MoonLightUniform = "vv_atmosMoonLight";
        public const string RainUniform = "vv_atmosRain";
        public const string SnowUniform = "vv_atmosSnow";
        public const string OvercastUniform = "vv_atmosOvercast";
        public const string BrokenCloudUniform = "vv_atmosBrokenCloud";
        public const string DensityUniform = "vv_atmosDensity";
        public const string AltitudeUniform = "vv_atmosAltitude";
        public const string DebugUniform = "vv_atmosDebug";

        /// <summary>
        /// Every program the atmosphere group patches.
        ///
        /// Water and the sprite particle shader are absent deliberately, and
        /// named in atmosphere.yaml so they read as excluded rather than
        /// forgotten.
        /// </summary>
        private static readonly EnumShaderProgram[] PatchedPrograms =
        {
            EnumShaderProgram.Chunkopaque,
            EnumShaderProgram.Chunktopsoil,
            EnumShaderProgram.Entityanimated,
            EnumShaderProgram.Particlescube,
        };

        private readonly ICoreClientAPI _capi;
        private readonly VintageVisualsModSystem _mod;

        private bool _reportedMissing;
        private bool _reportedActive;

        /// <summary>
        /// Frames skipped in a row because a program was already bound. See the
        /// weather binder for the full story; the short version is that a
        /// binder which silently returns looks exactly like one that works.
        /// </summary>
        private int _consecutiveSkips;
        private bool _reportedSkipping;

        /// <summary>Reused so the per-frame upload allocates nothing.</summary>
        private readonly Vec3f _sunDir = new Vec3f();
        private readonly Vec3f _sunColor = new Vec3f();
        private readonly Vec3f _moonDir = new Vec3f();

        /// <summary>
        /// What was derived this frame, so the subsystem and a debug report can
        /// see it without repeating the derivation.
        /// </summary>
        public AtmosphereInputs Current { get; private set; } = AtmosphereInputs.Off;

        public AtmosphereShaderBinder(ICoreClientAPI capi, VintageVisualsModSystem mod)
        {
            _capi = capi;
            _mod = mod;
        }

        public double RenderOrder { get { return UploadOrder; } }

        public int RenderRange { get { return 0; } }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Before) return;
            if (_capi == null || _mod == null) return;

            // Use() throws if a program is already bound and the client does
            // not recover. See ColorGrade for the full story.
            if (_capi.Render.CurrentActiveShader != null)
            {
                _consecutiveSkips++;

                if (_consecutiveSkips > 300 && !_reportedSkipping)
                {
                    _reportedSkipping = true;
                    _capi.Logger.Warning("[VintageVisuals] atmosphere: a shader program has been bound on " +
                        "every frame for several seconds, so no atmosphere uniform has been uploaded at " +
                        "all. Aerial perspective and rain fog are inactive, and nothing else will say so.");
                }

                return;
            }

            _consecutiveSkips = 0;

            AtmosphereConfig config = _mod.ConfigManager.Config.Atmosphere;

            // Derived ONCE per frame, on the CPU, and uploaded to every program
            // unchanged. Not once per program and certainly not once per
            // fragment: this is the whole reason the state/inputs split exists.
            AtmosphereState air = _mod.Environment == null
                ? AtmosphereState.Clear
                : _mod.Environment.Atmosphere;

            SceneGrants grants = _mod.Environment == null
                ? SceneGrants.Full
                : _mod.Environment.Grants;

            Current = AtmosphereInputs.Derive(air, config, grants);

            bool reached = false;
            foreach (EnumShaderProgram id in PatchedPrograms)
            {
                if (Upload(id, Current)) reached = true;
            }

            if (!reached)
            {
                if (!_reportedMissing)
                {
                    _reportedMissing = true;
                    _capi.Logger.Warning("[VintageVisuals] atmosphere: no patched program exposes " +
                        AerialUniform + ", so the atmosphere GLSL did not reach any compiled program. " +
                        "Every shader-side feature is inactive - look for an atmosphere patch failure " +
                        "above. Height haze is unaffected: it does not go through a shader.");
                }
                return;
            }

            if (!_reportedActive)
            {
                _reportedActive = true;
                _capi.Logger.Notification("[VintageVisuals] atmosphere: uniforms reaching the patched " +
                    "programs. Fog has one owner and covers entities and particles as well as terrain.");
            }
        }

        /// <summary>
        /// Pushes the atmosphere uniforms into one program.
        ///
        /// HasUniform per name rather than per program, so a group that rolled
        /// back simply stops matching instead of needing to be tracked.
        /// </summary>
        private bool Upload(EnumShaderProgram id, AtmosphereInputs inputs)
        {
            IShaderProgram program = _capi.Shader.GetProgram((int)id);
            if (program == null) return false;
            if (!program.HasUniform(AerialUniform)) return false;

            // Use() BINDS the program, and it throws if a different one is
            // already bound. This binder uploads to four programs in a loop, so
            // leaving the first one bound made the second call throw
            // "Already a different shader (chunkopaque) in use!" and took the
            // client down with it - the guard at the top of OnRenderFrame does
            // not help, because by then the offending bind is this binder's own.
            //
            // Every other binder in this mod pairs Use() with Stop() for exactly
            // this reason. This one did not, and it is the only one that
            // iterates more than two programs.
            program.Use();

            // Every value uploads every frame, including the zeroes. A skipped
            // upload leaves the LAST value in the program, so unticking a
            // feature would take effect on whichever frame something else
            // happened to rebind - which is not a fix, it is a slower bug.
            program.Uniform(AerialUniform, inputs.Aerial);
            program.Uniform(HorizonUniform, inputs.Horizon);
            program.Uniform(SunScatterUniform, inputs.SunScatter);
            program.Uniform(HeightUniform, inputs.HeightAttenuation);
            program.Uniform(WeatherUniform, inputs.WeatherExtinction);
            program.Uniform(CloudUniform, inputs.CloudAtmosphere);
            program.Uniform(CloudEdgeUniform, inputs.CloudEdge);
            program.Uniform(GodrayUniform, inputs.Godray);
            program.Uniform(PrecipUniform, inputs.Precipitation);
            program.Uniform(MoonUniform, inputs.Moon);
            program.Uniform(DappleUniform, inputs.Dapple);
            program.Uniform(WeatherTintUniform, inputs.WeatherTint);

            _sunDir.Set(inputs.SunDirection.X, inputs.SunDirection.Y, inputs.SunDirection.Z);
            program.Uniform(SunDirUniform, _sunDir);

            // The game's own sun colour, unmodified. See DECISIONS D21 for why
            // this mod does not compute its own.
            _sunColor.Set(inputs.SunColor.R, inputs.SunColor.G, inputs.SunColor.B);
            program.Uniform(SunColorUniform, _sunColor);

            _moonDir.Set(inputs.MoonDirection.X, inputs.MoonDirection.Y, inputs.MoonDirection.Z);
            program.Uniform(MoonDirUniform, _moonDir);

            program.Uniform(SunElevationUniform, inputs.SunElevation);
            program.Uniform(MoonLightUniform, inputs.MoonLight);
            program.Uniform(RainUniform, inputs.Rain);
            program.Uniform(SnowUniform, inputs.Snow);
            program.Uniform(OvercastUniform, inputs.Overcast);
            program.Uniform(BrokenCloudUniform, inputs.BrokenCloud);
            program.Uniform(DensityUniform, inputs.BaseDensity);
            program.Uniform(AltitudeUniform, inputs.Altitude);
            program.Uniform(DebugUniform, inputs.DebugView);

            // Unbind before returning, on every path that bound. Without this
            // the next iteration of the caller's loop throws.
            program.Stop();

            return true;
        }

        /// <summary>
        /// Nothing to release. No framebuffer, no texture, no GL object - this
        /// binder only ever writes uniforms into programs the game owns.
        /// </summary>
        public void Dispose()
        {
        }
    }
}
