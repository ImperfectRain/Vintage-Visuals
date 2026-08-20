using System;
using VintageVisuals.ColorGrade;
using VintageVisuals.Common;

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
        public static void Run(Action<string, bool, string> check)
        {
            Action<string, bool> ok = (name, condition) => check(name, condition, "");

            GradeSample basis = new GradeSample(1.1f, 0.95f, 1.05f, 0.1f, 1f, 1f, 1f);

            // A world doing everything at once, so "off" is tested against the
            // largest pull the stack can exert rather than against a calm day.
            GradeContext extreme = new GradeContext(
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
                Identical(GradeStack.Evaluate(basis, GradeContext.Neutral, Weights(1f)), basis));

            AdaptiveGradeConfig on = Weights(1f);

            // --- time of day ---
            GradeSample noon = GradeStack.Evaluate(basis, With(GradeContext.Neutral, dayLight: 1f), on);
            GradeSample night = GradeStack.Evaluate(basis, With(GradeContext.Neutral, dayLight: 0f), on);
            GradeSample dusk = GradeStack.Evaluate(basis, With(GradeContext.Neutral, dayLight: 0.4f), on);

            ok("night drains colour", night.Saturation < noon.Saturation - 0.2f);
            ok("night is cooler than noon", night.Temperature < noon.Temperature - 0.1f);
            ok("night leans blue", night.TintB > night.TintR);
            ok("golden hour is warmer than noon", dusk.Temperature > noon.Temperature + 0.05f);
            ok("golden hour leans warm", dusk.TintR > dusk.TintB);

            // --- weather ---
            GradeSample dry = GradeStack.Evaluate(basis, GradeContext.Neutral, on);
            GradeSample wet = GradeStack.Evaluate(basis, With(GradeContext.Neutral, rain: 1f), on);
            ok("rain drains colour", wet.Saturation < dry.Saturation - 0.1f);
            ok("rain flattens contrast", wet.Contrast < dry.Contrast - 0.05f);

            GradeSample overcast = GradeStack.Evaluate(basis, With(GradeContext.Neutral, cloudCover: 1f), on);
            ok("overcast flattens contrast", overcast.Contrast < dry.Contrast - 0.05f);

            // --- where the player is ---
            GradeSample inside = GradeStack.Evaluate(basis, With(GradeContext.Neutral, skyExposure: 0f), on);
            ok("indoors is warmer than outdoors", inside.Temperature > dry.Temperature + 0.05f);

            GradeSample deep = GradeStack.Evaluate(basis, With(GradeContext.Neutral, depth: 1f), on);
            ok("depth drains colour", deep.Saturation < dry.Saturation - 0.1f);

            GradeSample under = GradeStack.Evaluate(basis, With(GradeContext.Neutral, underwater: 1f), on);
            ok("underwater loses red first", under.TintR < under.TintB - 0.1f);

            // --- biome ---
            GradeSample hot = GradeStack.Evaluate(basis, With(GradeContext.Neutral, temperature: 34f), on);
            GradeSample cold = GradeStack.Evaluate(basis, With(GradeContext.Neutral, temperature: -18f), on);
            ok("a hot biome is warmer than a cold one", hot.Temperature > cold.Temperature + 0.2f);
            ok("a cold biome drains colour", cold.Saturation < dry.Saturation - 0.05f);

            GradeSample lush = GradeStack.Evaluate(basis, With(GradeContext.Neutral, rainfall: 1f), on);
            GradeSample arid = GradeStack.Evaluate(basis, With(GradeContext.Neutral, rainfall: 0f), on);
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
            GradeContext stormInside = With(GradeContext.Neutral, rain: 1f, cloudCover: 1f, skyExposure: 0f);
            ok("weather does not reach a fully enclosed space",
                Identical(GradeStack.Evaluate(basis, stormInside, weatherOnly), basis));

            AdaptiveGradeConfig biomeOnly = Weights(0f);
            biomeOnly.BiomeStrength = 1f;
            GradeContext desertInside = With(GradeContext.Neutral, temperature: 40f, rainfall: 0f, skyExposure: 0f);
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
            GradeContext broken = new GradeContext(float.NaN, float.NaN, float.NaN, float.NaN,
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

        /// <summary>Copies a context with named overrides, so each check reads as one difference.</summary>
        private static GradeContext With(GradeContext b, float? dayLight = null, float? rain = null,
                                         float? cloudCover = null, float? skyExposure = null,
                                         float? depth = null, float? temperature = null,
                                         float? rainfall = null, float? underwater = null)
        {
            return new GradeContext(
                dayLight ?? b.DayLight,
                rain ?? b.Rain,
                cloudCover ?? b.CloudCover,
                skyExposure ?? b.SkyExposure,
                depth ?? b.Depth,
                temperature ?? b.Temperature,
                rainfall ?? b.Rainfall,
                underwater ?? b.Underwater);
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
    }
}
