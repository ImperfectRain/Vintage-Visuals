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

        public const string AerialUniform = "vv_atmosAerial";
        public const string SunDirUniform = "vv_atmosSunDir";
        public const string SunColorUniform = "vv_atmosSunColor";
        public const string SunElevationUniform = "vv_atmosSunElevation";
        public const string RainUniform = "vv_atmosRain";
        public const string FogStrengthUniform = "vv_atmosFogStrength";
        public const string FogTintUniform = "vv_atmosFogTint";
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
            WeatherConfig weather = _mod.ConfigManager.Config.Weather;

            bool reached = false;
            foreach (EnumShaderProgram id in PatchedPrograms)
            {
                if (Upload(id, config, weather)) reached = true;
            }

            if (!reached)
            {
                if (!_reportedMissing)
                {
                    _reportedMissing = true;
                    _capi.Logger.Warning("[VintageVisuals] atmosphere: no patched program exposes " +
                        AerialUniform + ", so the atmosphere GLSL did not reach any compiled program. " +
                        "Aerial perspective and rain fog are inactive - look for an atmosphere patch " +
                        "failure above. Height haze is unaffected: it does not go through a shader.");
                }
                return;
            }

            if (!_reportedActive)
            {
                _reportedActive = true;
                _capi.Logger.Notification("[VintageVisuals] atmosphere: uniforms reaching the patched " +
                    "programs. Fog now has one owner and covers entities and particles as well as terrain.");
            }
        }

        /// <summary>
        /// Pushes the atmosphere uniforms into one program.
        ///
        /// HasUniform per name rather than per program, so a group that rolled
        /// back simply stops matching instead of needing to be tracked.
        /// </summary>
        private bool Upload(EnumShaderProgram id, AtmosphereConfig config, WeatherConfig weather)
        {
            IShaderProgram program = _capi.Shader.GetProgram((int)id);
            if (program == null) return false;
            if (!program.HasUniform(AerialUniform)) return false;

            AtmosphereState air = _mod.Environment == null
                ? AtmosphereState.Clear
                : _mod.Environment.Atmosphere;

            program.Use();

            // Everything the player switched off has to upload as zero rather
            // than simply not upload. A skipped upload leaves the LAST value in
            // the program, so unticking the box would take effect on whichever
            // frame something else happened to rebind.
            bool on = config.Enabled;

            program.Uniform(AerialUniform, on ? config.AerialPerspective : 0f);

            _sunDir.Set(air.SunDirection == null ? 0f : air.SunDirection.X,
                        air.SunDirection == null ? 1f : air.SunDirection.Y,
                        air.SunDirection == null ? 0f : air.SunDirection.Z);
            program.Uniform(SunDirUniform, _sunDir);

            // The game's own sun colour, unmodified. See DECISIONS D21 for why
            // this mod does not compute its own.
            _sunColor.Set(air.SunColor == null ? 1f : air.SunColor.R,
                          air.SunColor == null ? 1f : air.SunColor.G,
                          air.SunColor == null ? 1f : air.SunColor.B);
            program.Uniform(SunColorUniform, _sunColor);

            program.Uniform(SunElevationUniform, air.SunElevation);

            // Rain that is FALLING, not rain that fell. The air clears within
            // seconds of a shower stopping even though the ground stays wet for
            // a minute, and driving both off one number leaves the world hazy
            // long after the sky cleared.
            //
            // Weather still decides how much; it just no longer renders it.
            bool raining = weather.Enabled && _mod.Weather != null;
            program.Uniform(RainUniform, raining ? _mod.Weather.Rain : 0f);

            // The GRANT, not the config value. Three subsystems used to wash out
            // the same rainy afternoon independently.
            float granted = _mod.Environment == null ? 1f : _mod.Environment.Grants.RainFog;
            program.Uniform(FogStrengthUniform, weather.Enabled ? weather.FogStrength * granted : 0f);
            program.Uniform(FogTintUniform, weather.Enabled ? weather.FogTint : 0f);

            program.Uniform(DebugUniform, on ? config.AirDebugView : 0f);

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
