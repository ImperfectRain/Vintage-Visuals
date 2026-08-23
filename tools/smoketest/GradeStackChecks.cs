using System;
using System.IO;
using System.Linq;
using VintageVisuals.ColorGrade;
using VintageVisuals.Common;
using VintageVisuals.Common.Scene;
using VintageVisuals.Weather;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Drives the real GradeStack through the world states it grades for.
    ///
    /// This is the subsystem that decides what the whole screen looks like, and
    /// unlike a shader patch it is ordinary arithmetic that can be checked
    /// exactly. The rules are deliberately free of game types so they can be
    /// driven here rather than only in a running client, which is the entire
    /// reason for the split between GradeStack and WorldGradeSampler.
    ///
    /// The load-bearing check is the first one. Every strength at zero must
    /// give back the player's own settings BIT FOR BIT - not close, exactly -
    /// because this mod has repeatedly shipped features whose "off" was a
    /// slightly different image rather than the same one.
    /// </summary>
    public static class GradeStackChecks
    {
        public static string Repo;

        public static void Run(Action<string, bool, string> check)
        {
            CheckAutomaticLiftBringsItsShoulder(check);

            Action<string, bool> ok = (name, condition) => check(name, condition, "");

            GradeSample basis = new GradeSample(1.1f, 0.95f, 1.05f, 0.1f, 1f, 1f, 1f);

            // A world doing everything at once, so "off" is tested against the
            // largest pull the stack can exert rather than against a calm day.
            EnvironmentState extreme = Build(
                dayLight: 0.0f, rain: 1f, cloudCover: 1f, skyExposure: 0f,
                depth: 1f, temperature: -30f, rainfall: 0f, underwater: 1f);

            AdaptiveGradeConfig off = Weights(0f);
            ok("all strengths zero returns the player's grade exactly",
                Identical(GradeStack.Evaluate(basis, extreme, off), basis));

            AdaptiveGradeConfig disabled = Weights(1f);
            disabled.Enabled = false;
            ok("disabled returns the player's grade exactly",
                Identical(GradeStack.Evaluate(basis, extreme, disabled), basis));

            ok("a null config returns the player's grade exactly",
                Identical(GradeStack.Evaluate(basis, extreme, null), basis));

            ok("the neutral context changes nothing",
                Identical(GradeStack.Evaluate(basis, EnvironmentState.Clear, Weights(1f)), basis));

            AdaptiveGradeConfig on = Weights(1f);

            // --- time of day ---
            GradeSample noon = GradeStack.Evaluate(basis, With(EnvironmentState.Clear, dayLight: 1f), on);
            GradeSample night = GradeStack.Evaluate(basis, With(EnvironmentState.Clear, dayLight: 0f), on);
            GradeSample dusk = GradeStack.Evaluate(basis, With(EnvironmentState.Clear, dayLight: 0.4f), on);

            ok("night drains colour", night.Saturation < noon.Saturation - 0.2f);
            ok("night is cooler than noon", night.Temperature < noon.Temperature - 0.1f);
            ok("night leans blue", night.TintB > night.TintR);
            ok("golden hour is warmer than noon", dusk.Temperature > noon.Temperature + 0.05f);
            ok("golden hour leans warm", dusk.TintR > dusk.TintB);

            // --- weather ---
            GradeSample dry = GradeStack.Evaluate(basis, EnvironmentState.Clear, on);
            GradeSample wet = GradeStack.Evaluate(basis, With(EnvironmentState.Clear, rain: 1f), on);
            ok("rain drains colour", wet.Saturation < dry.Saturation - 0.1f);
            ok("rain flattens contrast", wet.Contrast < dry.Contrast - 0.05f);

            GradeSample overcast = GradeStack.Evaluate(basis, With(EnvironmentState.Clear, cloudCover: 1f), on);
            ok("overcast flattens contrast", overcast.Contrast < dry.Contrast - 0.05f);

            // --- where the player is ---
            GradeSample inside = GradeStack.Evaluate(basis, With(EnvironmentState.Clear, skyExposure: 0f), on);
            ok("indoors is warmer than outdoors", inside.Temperature > dry.Temperature + 0.05f);

            GradeSample deep = GradeStack.Evaluate(basis, With(EnvironmentState.Clear, depth: 1f), on);
            ok("depth drains colour", deep.Saturation < dry.Saturation - 0.1f);

            GradeSample under = GradeStack.Evaluate(basis, With(EnvironmentState.Clear, underwater: 1f), on);
            ok("underwater loses red first", under.TintR < under.TintB - 0.1f);

            // --- biome ---
            GradeSample hot = GradeStack.Evaluate(basis, With(EnvironmentState.Clear, temperature: 34f), on);
            GradeSample cold = GradeStack.Evaluate(basis, With(EnvironmentState.Clear, temperature: -18f), on);
            ok("a hot biome is warmer than a cold one", hot.Temperature > cold.Temperature + 0.2f);
            ok("a cold biome drains colour", cold.Saturation < dry.Saturation - 0.05f);

            GradeSample lush = GradeStack.Evaluate(basis, With(EnvironmentState.Clear, rainfall: 1f), on);
            GradeSample arid = GradeStack.Evaluate(basis, With(EnvironmentState.Clear, rainfall: 0f), on);
            ok("a lush biome is more saturated than an arid one",
                lush.Saturation > arid.Saturation + 0.1f);
            ok("a lush biome leans green", lush.TintG > lush.TintR);

            // --- the sky-exposure gate ---
            //
            // Weather, biome and golden hour are outdoor phenomena. A cellar is
            // the same colour whatever the sky is doing, and a grade that
            // followed the weather indoors would make every doorway a lie.
            AdaptiveGradeConfig weatherOnly = Weights(0f);
            weatherOnly.WeatherStrength = 1f;
            EnvironmentState stormInside = With(EnvironmentState.Clear, rain: 1f, cloudCover: 1f, skyExposure: 0f);
            ok("weather does not reach a fully enclosed space",
                Identical(GradeStack.Evaluate(basis, stormInside, weatherOnly), basis));

            AdaptiveGradeConfig biomeOnly = Weights(0f);
            biomeOnly.BiomeStrength = 1f;
            EnvironmentState desertInside = With(EnvironmentState.Clear, temperature: 40f, rainfall: 0f, skyExposure: 0f);
            ok("biome does not reach a fully enclosed space",
                Identical(GradeStack.Evaluate(basis, desertInside, biomeOnly), basis));

            // --- everything at once stays inside the ranges a player could dial ---
            AdaptiveGradeConfig loud = Weights(2f);
            GradeSample piled = GradeStack.Evaluate(basis, extreme, loud);
            ok("a full stack stays within the exposure range",
                piled.Exposure >= 0.1f && piled.Exposure <= 4.0f);
            ok("a full stack never inverts saturation",
                piled.Saturation >= 0f && piled.Saturation <= 2f);
            ok("a full stack stays within the contrast range",
                piled.Contrast >= 0f && piled.Contrast <= 2f);
            ok("a full stack stays within the temperature range",
                piled.Temperature >= -1f && piled.Temperature <= 1f);
            ok("a full stack never blacks the screen out",
                piled.TintR >= 0.5f && piled.TintG >= 0.5f && piled.TintB >= 0.5f);

            // --- a context full of nonsense must not reach the uniforms ---
            EnvironmentState broken = Build(float.NaN, float.NaN, float.NaN, float.NaN,
                                            float.NaN, float.NaN, float.NaN, float.NaN);
            GradeSample fromBroken = GradeStack.Evaluate(basis, broken, on);
            ok("a NaN context produces a finite grade", Finite(fromBroken));

            // --- easing ---
            GradeSample start = GradeSample.Neutral;
            GradeSample far = new GradeSample(2f, 1.5f, 0.5f, -0.5f, 0.9f, 1f, 1.1f);

            GradeSample oneStep = start.EaseTo(far, 1f, 2.5f);
            GradeSample manySteps = start;
            for (int i = 0; i < 10; i++) manySteps = manySteps.EaseTo(far, 0.1f, 2.5f);
            ok("easing does not depend on frame rate",
                Math.Abs(oneStep.Exposure - manySteps.Exposure) < 0.01f);

            GradeSample settled = start;
            for (int i = 0; i < 400; i++) settled = settled.EaseTo(far, 0.1f, 2.5f);
            ok("easing converges on its target", settled.DistanceTo(far) < 1e-3f);

            ok("a zero timestep does not move the grade",
                Identical(far.EaseTo(start, 0f, 2.5f), far));

            // --- rain and snow are one storm seen through a thermometer ---
            //
            // Exactly one of the two is non-zero at any temperature. If both
            // could fire, a storm at freezing point would drive wetness and
            // snow at once and the ground would read as wet snow.
            ok("above freezing it rains and does not snow",
                WetnessTracker.TargetFor(0.8f, 12f) > 0f && WetnessTracker.SnowTargetFor(0.8f, 12f) == 0f);
            ok("below freezing it snows and does not rain",
                WetnessTracker.SnowTargetFor(0.8f, -12f) > 0f && WetnessTracker.TargetFor(0.8f, -12f) == 0f);
            ok("a storm does not change intensity as it crosses freezing",
                Math.Abs(WetnessTracker.SnowTargetFor(0.8f, -1f) - WetnessTracker.TargetFor(0.8f, 1f)) < 1e-6f);
            // --- the coordinate wrap the shaders assume ---
            //
            // The GLSL builds its ripple grid on a coordinate the CPU wraps.
            // If the two constants ever differ, the wrap lands mid-cell and
            // cuts a ring in half - a seam nobody would trace back to a number
            // in a C# file.
            string glsl = System.IO.File.ReadAllText(System.IO.Path.Combine(
                Repo, "assets/vintagevisuals/shadersnippets/pseudopbr.glsl"));
            ok("the GLSL wrap period matches EnvironmentState.CameraPeriod",
                glsl.Contains("const float VV_ORIGIN_PERIOD = " +
                              EnvironmentState.CameraPeriod.ToString("0.0") + ";"));

            // Both ripple octaves have to tile that period exactly.
            ok("both ripple octave scales tile the wrap period",
                (EnvironmentState.CameraPeriod * 2.0f) % 1f == 0f &&
                (EnvironmentState.CameraPeriod * 1.0f) % 1f == 0f);

            ok("drizzle is ignored whichever way it falls",
                WetnessTracker.SnowTargetFor(0.01f, -12f) == 0f && WetnessTracker.TargetFor(0.01f, 12f) == 0f);
        }

        private static AdaptiveGradeConfig Weights(float strength)
        {
            return new AdaptiveGradeConfig
            {
                Enabled = true,
                TimeOfDayStrength = strength,
                WeatherStrength = strength,
                BiomeStrength = strength,
                IndoorStrength = strength,
                DepthStrength = strength,
                UnderwaterStrength = strength,
            };
        }

        /// <summary>Copies a state with named overrides, so each check reads as one difference.</summary>
        private static EnvironmentState With(EnvironmentState b, float? dayLight = null, float? rain = null,
                                             float? cloudCover = null, float? skyExposure = null,
                                             float? depth = null, float? temperature = null,
                                             float? rainfall = null, float? underwater = null)
        {
            return Build(
                dayLight ?? b.DayLight,
                rain ?? b.Rain,
                cloudCover ?? b.CloudCover,
                skyExposure ?? b.SkyExposure,
                depth ?? b.Depth,
                temperature ?? b.Temperature,
                rainfall ?? b.Humidity,
                underwater ?? b.Underwater);
        }

        /// <summary>
        /// Only the fields grading reads. EnvironmentState carries more than
        /// that - wind, moonlight, camera position - and spelling all of it out
        /// at every call site would bury the one value each check is varying.
        /// </summary>
        private static EnvironmentState Build(float dayLight, float rain, float cloudCover,
                                              float skyExposure, float depth, float temperature,
                                              float rainfall, float underwater)
        {
            return new EnvironmentState(
                dayLight: dayLight, moonLight: 0f, cloudCover: cloudCover,
                windDirection: new Vintagestory.API.MathTools.Vec2f(1f, 0f), windSpeed: 0f,
                precipitation: rain, rain: rain, snow: 0f, wetness: rain,
                temperature: temperature, humidity: rainfall,
                skyExposure: skyExposure, depth: depth, underwater: underwater,
                cameraPosition: new Vintagestory.API.MathTools.Vec3f());
        }

        private static bool Identical(GradeSample a, GradeSample b)
        {
            return a.Exposure == b.Exposure
                && a.Contrast == b.Contrast
                && a.Saturation == b.Saturation
                && a.Temperature == b.Temperature
                && a.TintR == b.TintR
                && a.TintG == b.TintG
                && a.TintB == b.TintB;
        }

        private static bool Finite(GradeSample a)
        {
            return IsFinite(a.Exposure) && IsFinite(a.Contrast) && IsFinite(a.Saturation)
                && IsFinite(a.Temperature) && IsFinite(a.TintR) && IsFinite(a.TintG) && IsFinite(a.TintB);
        }

        private static bool IsFinite(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }
    
        /// <summary>
        /// An automatic exposure lift must bring its own highlight shoulder.
        ///
        /// Eye adaptation multiplies the whole frame by up to DarkGain - 1.6 by
        /// default - and vv_tonemapStrength, the curve that would roll the
        /// result off, defaults to 0. So the shipped combination GUARANTEED
        /// clipping: anything vanilla output above 1/1.6 = 0.625 was pushed past
        /// white and flattened. Around a low sun that is a wide band of sky, and
        /// it was reported from the game as severe highlight clipping.
        ///
        /// colorgrade.glsl's own header already says exposure and the shoulder
        /// belong together. They shipped as independent settings whose defaults
        /// contradict each other.
        ///
        /// The distinction that fixes it without removing the control:
        /// vv_exposure is the PLAYER'S choice and they may clip with it.
        /// Adaptation is the RENDERER'S choice - nobody asked for it - so the
        /// renderer owes the highlights that pay for it.
        /// </summary>
        private static void CheckAutomaticLiftBringsItsShoulder(Action<string, bool, string> check)
        {
            string grade = File.ReadAllText(Path.Combine(Repo,
                "assets/vintagevisuals/shadersnippets/colorgrade.glsl"));

            check("the automatic lift is measured against unity",
                  grade.Contains("float autoLift = clamp(adaptation - 1.0, 0.0, 1.0);"),
                  "only the part of adaptation ABOVE 1 is an unrequested brightening");

            check("the shoulder is at least the automatic lift",
                  grade.Contains("float shoulder = max(clamp(vv_tonemapStrength, 0.0, 1.0), autoLift);"),
                  "a lift with no rolloff clips by construction");

            check("the tonemap blend uses that shoulder",
                  grade.Contains("graded = mix(graded, vvACESFitted(graded), shoulder);"),
                  "computing it and blending by something else changes nothing");

            // The invariant that keeps the player's control intact: with no
            // adaptation, behaviour is bit-identical to before.
            var wrong = new System.Collections.Generic.List<string>();

            foreach (float adaptation in new[] { 0.5f, 1f, 1.2f, 1.6f, 2f })
            {
                foreach (float tonemap in new[] { 0f, 0.5f, 1f })
                {
                    float autoLift = Math.Min(1f, Math.Max(0f, adaptation - 1f));
                    float shoulder = Math.Max(Math.Min(1f, Math.Max(0f, tonemap)), autoLift);

                    // Never below what the player asked for.
                    if (shoulder < tonemap - 1e-6f)
                    {
                        wrong.Add("adaptation " + adaptation + " reduced the player's tonemap");
                    }

                    // At or below unity the player's setting is untouched.
                    if (adaptation <= 1f && Math.Abs(shoulder - tonemap) > 1e-6f)
                    {
                        wrong.Add("adaptation " + adaptation + " changed the shoulder with no lift");
                    }

                    // A large lift must produce a real shoulder.
                    if (adaptation >= 1.5f && shoulder < 0.4f)
                    {
                        wrong.Add("adaptation " + adaptation + " lifts hard with shoulder " + shoulder);
                    }
                }
            }

            check("the shoulder never takes away what the player asked for",
                  wrong.Count == 0, string.Join("; ", wrong.Distinct()));
        }
    }
}
