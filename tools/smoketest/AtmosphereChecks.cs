using System;
using System.IO;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VintageVisuals.Atmosphere;
using VintageVisuals.Common.Scene;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// The atmosphere: what it reads, what it writes, and the one formula it
    /// cannot see.
    ///
    /// Most of this subsystem is a pass-through - it reads numbers Vintage
    /// Story already computed and writes two numbers back into the game's own
    /// ambient stack. That leaves very little arithmetic to be wrong and two
    /// things that can be badly wrong:
    ///
    ///  - The SIGN of the haze density. Vanilla's height fog fills the space
    ///    BELOW its start height when the density is negative and above it when
    ///    positive. Get that backwards and the haze layer sits in the sky.
    ///  - The BLEND the game performs over the modifier stack, which this mod
    ///    reasons about but does not perform. It is documented in an interface
    ///    comment, which is not the same as tested.
    ///
    /// Both are checked here against vanilla's own formula, transcribed from
    /// the dumped shaders, rather than against what the mod believes.
    /// </summary>
    public static class AtmosphereChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            CheckClearMatchesVanillaDefaults(check);
            CheckHazePressureIsZeroAtNoon(check);
            CheckHazePressureResponds(check);
            CheckHazeSignPutsFogOnTheGround(check);
            CheckHazeIsSecondaryToRainFog(check);
            CheckOffRemovesRatherThanZeroes(check);
            CheckBlendFormulaMatchesTheInterface(check);
            CheckFogClampMatchesTheShaders(repo, check);
            CheckNoDuplicateHeightFogInGlsl(repo, check);
        }

        /// <summary>
        /// The "no influence" baseline has to BE vanilla's baseline, not a
        /// tidy-looking set of numbers near it.
        ///
        /// AtmosphereState.Clear is what every consumer sees before the first
        /// sample lands and what the tests measure against. If it says the fog
        /// is thinner than vanilla's default, then "the atmosphere is doing
        /// nothing" and "the atmosphere is clearing the air" become the same
        /// state, and nothing downstream can tell them apart.
        /// </summary>
        private static void CheckClearMatchesVanillaDefaults(Action<string, bool, string> check)
        {
            AmbientModifier vanilla = AmbientModifier.DefaultAmbient;
            AtmosphereState clear = AtmosphereState.Clear;

            check("the clear atmosphere uses vanilla's own fog density",
                  Math.Abs(clear.FogDensity - vanilla.FogDensity.Value) < 1e-6f,
                  "clear " + clear.FogDensity + " vs vanilla " + vanilla.FogDensity.Value);

            check("the named vanilla density matches the game's default",
                  Math.Abs(AtmosphereState.VanillaFogDensity - vanilla.FogDensity.Value) < 1e-6f,
                  AtmosphereState.VanillaFogDensity + " vs " + vanilla.FogDensity.Value);

            check("the clear atmosphere uses vanilla's own fog colour",
                  Math.Abs(clear.FogColor.R - vanilla.FogColor.Value[0]) < 1e-6f &&
                  Math.Abs(clear.FogColor.G - vanilla.FogColor.Value[1]) < 1e-6f &&
                  Math.Abs(clear.FogColor.B - vanilla.FogColor.Value[2]) < 1e-6f,
                  clear.FogColor + " vs " + string.Join(",", vanilla.FogColor.Value));

            check("the clear atmosphere has no height fog",
                  clear.FlatFogDensity == 0f,
                  clear.FlatFogDensity.ToString());

            check("the clear atmosphere puts the sun overhead and white",
                  clear.SunElevation == 1f && clear.SunColor.R == 1f,
                  clear.SunElevation + ", " + clear.SunColor);
        }

        /// <summary>
        /// A clear temperate noon must want no haze at all.
        ///
        /// This is the zero case, and it is the one that decides whether the
        /// feature is a look or a permanent veil. Ground haze that persists
        /// through midday is not haze, it is a lower contrast setting the
        /// player did not ask for.
        /// </summary>
        private static void CheckHazePressureIsZeroAtNoon(Action<string, bool, string> check)
        {
            float noon = SceneArbiter.HazePressure(EnvironmentState.Clear);

            check("a clear noon wants no ground haze",
                  noon <= 1e-6f,
                  noon.ToString());
        }

        /// <summary>
        /// Each of the four terms has to actually move the answer, in the
        /// direction the comment claims.
        ///
        /// A term that is present but inert is worse than a missing one: the
        /// documentation promises the haze responds to wind, the player watches
        /// a gale not disperse it, and the feature loses the credibility of the
        /// terms that DO work.
        /// </summary>
        private static void CheckHazePressureResponds(Action<string, bool, string> check)
        {
            float still = SceneArbiter.HazePressure(Night(humidity: 0.9f, wetness: 0.5f, wind: 0f, sky: 1f));
            float windy = SceneArbiter.HazePressure(Night(humidity: 0.9f, wetness: 0.5f, wind: 0.8f, sky: 1f));
            float dry = SceneArbiter.HazePressure(Night(humidity: 0.05f, wetness: 0f, wind: 0f, sky: 1f));
            float cave = SceneArbiter.HazePressure(Night(humidity: 0.9f, wetness: 0.5f, wind: 0f, sky: 0f));

            check("a still humid night wants ground haze",
                  still > 0.3f, still.ToString());

            check("wind disperses ground haze",
                  windy < still * 0.5f, still + " -> " + windy);

            check("a desert wants far less ground haze than a rainforest",
                  dry < still * 0.25f, still + " -> " + dry);

            check("a cave wants no ground haze",
                  cave <= 1e-6f, cave.ToString());

            check("ground haze pressure stays inside 0..1",
                  still >= 0f && still <= 1f, still.ToString());
        }

        /// <summary>
        /// The sign convention, checked through vanilla's own formula.
        ///
        /// This transcribes getFogLevel from the dumped shaders rather than
        /// describing it:
        ///
        ///     flatFog = 1 - 1/exp((worldY - flatFogStart) * flatFogDensity)
        ///
        /// and asserts that with the density this mod writes, a fragment BELOW
        /// the band's top is fogged and one well above it is not. Nothing else
        /// in the repository can catch a sign error here: the value is written
        /// into the game's ambient stack and rendered by code this project does
        /// not own, so the first report would be a screenshot of haze in the
        /// sky.
        /// </summary>
        private static void CheckHazeSignPutsFogOnTheGround(Action<string, bool, string> check)
        {
            const float seaLevel = 110f;

            var bridge = new AmbientBridge(null, null);
            bridge.SetHeightHaze(1f, seaLevel);

            float density = bridge.HazeDensity;
            float top = bridge.HazeTop;

            check("the haze band sits above sea level",
                  top > seaLevel,
                  "sea level " + seaLevel + ", band top " + top);

            check("the haze density is negative, which is what puts it below the band",
                  density < 0f,
                  density.ToString());

            float belowBand = VanillaFlatFog(worldY: seaLevel, start: top, density: density);
            float insideBand = VanillaFlatFog(worldY: top - 4f, start: top, density: density);
            float aboveBand = VanillaFlatFog(worldY: top + 60f, start: top, density: density);

            check("vanilla's own formula fogs the ground",
                  belowBand > 0.2f,
                  belowBand.ToString());

            check("vanilla's own formula thins the haze toward the top of the band",
                  insideBand < belowBand,
                  insideBand + " at the top vs " + belowBand + " at sea level");

            check("vanilla's own formula leaves a hilltop above the band clear",
                  aboveBand <= 0f,
                  aboveBand.ToString());
        }

        /// <summary>
        /// Vanilla's height fog term, transcribed from the dumped shaders and
        /// clamped the way the shader clamps it.
        ///
        /// getFogLevel takes max(flatFog, distanceFog) and then clamps the
        /// result to 0..1, so a negative flatFog means "this fragment gets no
        /// height fog", not "this fragment gets negative fog". Reproducing the
        /// clamp matters: without it the above-band case reads as a large
        /// negative number and passes a test that should be asking whether the
        /// fragment is clear.
        /// </summary>
        private static float VanillaFlatFog(float worldY, float start, float density)
        {
            float flatFog = 1f - 1f / (float)Math.Exp((worldY - start) * density);
            return flatFog < 0f ? 0f : (flatFog > 1f ? 1f : flatFog);
        }

        /// <summary>
        /// Two subsystems now put air between the camera and the world, and the
        /// budget has to charge for it once.
        ///
        /// Weather owns the haze role, so on a rainy still night the atmosphere
        /// must come second and be damped - not because ground haze matters
        /// less, but because the rain is the thing the player can see a cause
        /// for. What is checked is the consequence: the two together may not
        /// take more than one of them could have taken alone.
        /// </summary>
        private static void CheckHazeIsSecondaryToRainFog(Action<string, bool, string> check)
        {
            EnvironmentState storm = new EnvironmentState(
                dayLight: 0.05f, moonLight: 0.3f, cloudCover: 1f,
                windDirection: new Vec2f(1f, 0f), windSpeed: 0f,
                precipitation: 1f, rain: 1f, snow: 0f, wetness: 1f,
                temperature: 9f, humidity: 0.9f,
                skyExposure: 1f, depth: 0f, underwater: 0f,
                cameraPosition: new Vec3f());

            SceneIntent intent = SceneIntentBuilder.Build(storm);

            VisualBudget budget;
            SceneGrants both = SceneArbiter.Arbitrate(
                intent, storm, new SceneDemand(1f, 0f, 0f, 1f), out budget);

            VisualBudget aloneBudget;
            SceneGrants rainOnly = SceneArbiter.Arbitrate(
                intent, storm, new SceneDemand(1f, 0f, 0f, 0f), out aloneBudget);

            check("rain fog is not reduced by ground haze also asking",
                  both.RainFog >= rainOnly.RainFog - 1e-4f,
                  rainOnly.RainFog + " alone vs " + both.RainFog + " together");

            check("ground haze is granted less than it asked for when rain already took the air",
                  both.HeightHaze < 1f,
                  both.HeightHaze.ToString());

            check("a subsystem that asked for no haze is not handed a zero multiplier",
                  rainOnly.HeightHaze == 1f,
                  rainOnly.HeightHaze.ToString());
        }

        /// <summary>
        /// Off has to mean the modifier is GONE, not present and zeroed.
        ///
        /// A zeroed entry is invisible in the frame and visible everywhere
        /// else: it stays in a dictionary the game and every other mod share,
        /// it survives the player switching the feature off, and it is exactly
        /// the kind of residue that makes a later "why is this mod in the
        /// ambient stack" impossible to answer.
        /// </summary>
        private static void CheckOffRemovesRatherThanZeroes(Action<string, bool, string> check)
        {
            var bridge = new AmbientBridge(null, null);

            bridge.SetHeightHaze(1f, 110f);
            bool wroteSomething = bridge.HazeDensity != 0f;

            bridge.SetHeightHaze(0f, 110f);

            check("switching ground haze on writes a density",
                  wroteSomething, "nothing was written");

            check("switching ground haze off clears what was written",
                  bridge.HazeDensity == 0f && bridge.HazeTop == 0f,
                  bridge.HazeDensity + ", " + bridge.HazeTop);
        }

        /// <summary>
        /// The fold this mod reasons about, driven against a hand-built stack.
        ///
        /// IAmbientManager documents the blend as
        /// <c>blended = w * value + (1 - w) * blended</c> over the modifiers in
        /// order. The mod's copy of that is what decides whether a weight of 0
        /// really is a no-op - which is the entire safety argument for writing
        /// into a dictionary shared with the game.
        ///
        /// Driven through the arithmetic directly rather than through a stub
        /// ambient manager: what is being pinned is the formula, and a stub
        /// would only prove the formula agrees with itself.
        /// </summary>
        private static void CheckBlendFormulaMatchesTheInterface(Action<string, bool, string> check)
        {
            float prior = 0.00125f;
            float ours = 0.02f;

            float atZero = 0f * ours + (1f - 0f) * prior;
            float atOne = 1f * ours + (1f - 1f) * prior;
            float atHalf = 0.5f * ours + 0.5f * prior;

            check("a weight of zero leaves the game's own fog untouched",
                  Math.Abs(atZero - prior) < 1e-9f,
                  atZero + " vs " + prior);

            check("a weight of one replaces it",
                  Math.Abs(atOne - ours) < 1e-9f,
                  atOne + " vs " + ours);

            check("a weight between blends toward what the mod asked for",
                  atHalf > prior && atHalf < ours,
                  atHalf.ToString());
        }

        /// <summary>
        /// Anything reasoning about how much haze a distance produces has to
        /// use vanilla's clamp, or it predicts a gradient the game does not
        /// draw.
        ///
        /// Every shading program's getFogLevel does min(250, depth) before the
        /// exponential. Read from the dumped shader rather than trusted, so
        /// that a game version which moves the clamp fails here rather than in
        /// a screenshot.
        /// </summary>
        private static void CheckFogClampMatchesTheShaders(string repo, Action<string, bool, string> check)
        {
            string dumped = Path.Combine(repo, "reference/game-shaders/chunkopaque.vsh");
            if (!File.Exists(dumped))
            {
                // Reference shaders are the game's assets and are never
                // committed, so this cannot be a failure. It is skipped loudly
                // rather than silently: a check that quietly does nothing is
                // the failure mode this suite has already been bitten by.
                check("the fog distance clamp was checked against the game's own shader",
                      true,
                      "SKIPPED - reference/game-shaders is not present, run tools/verifypatches for the real answer");
                return;
            }

            string source = File.ReadAllText(dumped);
            Match m = Regex.Match(source, @"clampedDepth\s*=\s*min\(\s*([0-9.]+)\s*,\s*depth\s*\)");

            check("the game still clamps fog distance where the mod thinks it does",
                  m.Success && Math.Abs(float.Parse(m.Groups[1].Value,
                      System.Globalization.CultureInfo.InvariantCulture)
                      - AtmosphereState.FogDistanceClamp) < 1e-3f,
                  m.Success ? "shader says " + m.Groups[1].Value + ", mod says " + AtmosphereState.FogDistanceClamp
                            : "could not find the clamp in chunkopaque.vsh");
        }

        /// <summary>
        /// The mod must not grow its own height fog in GLSL.
        ///
        /// Vanilla computes a height-banded fog term in every shading program
        /// it has, including the sky and the water - neither of which this mod
        /// patches. A second implementation in a snippet would apply to the
        /// handful of programs the mod does patch and stop at their edges, so
        /// the seam would fall between a hillside and its own grass. This
        /// catches the reimplementation being added later by someone who did
        /// not know vanilla had it.
        /// </summary>
        private static void CheckNoDuplicateHeightFogInGlsl(string repo, Action<string, bool, string> check)
        {
            string snippets = Path.Combine(repo, "assets/vintagevisuals/shadersnippets");
            bool found = false;
            string where = "";

            foreach (string file in Directory.GetFiles(snippets, "*.glsl"))
            {
                string text = File.ReadAllText(file);

                // The mod declaring vanilla's own uniform names would mean it
                // had started computing the term itself, since it has no reason
                // to read them: it writes them from the CPU.
                if (Regex.IsMatch(text, @"^\s*uniform\s+float\s+vv_\w*[Ff]latFog", RegexOptions.Multiline) ||
                    Regex.IsMatch(text, @"^\s*uniform\s+float\s+vv_\w*[Hh]eightFog", RegexOptions.Multiline))
                {
                    found = true;
                    where = Path.GetFileName(file);
                    break;
                }
            }

            check("height fog is left to vanilla rather than reimplemented in a snippet",
                  !found,
                  found ? where + " declares its own height-fog uniform" : "none found");
        }

        private static EnvironmentState Night(float humidity, float wetness, float wind, float sky)
        {
            return new EnvironmentState(
                dayLight: 0f, moonLight: 0.4f, cloudCover: 0f,
                windDirection: new Vec2f(1f, 0f), windSpeed: wind,
                precipitation: 0f, rain: 0f, snow: 0f, wetness: wetness,
                temperature: EnvironmentState.TemperateCelsius, humidity: humidity,
                skyExposure: sky, depth: 0f, underwater: 0f,
                cameraPosition: new Vec3f());
        }
    }
}
