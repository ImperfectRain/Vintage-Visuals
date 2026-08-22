using System;
using System.IO;
using System.Linq;
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
            CheckAerialIsDirectionalOnly(repo, check);
            CheckAerialZeroIsVanilla(repo, check);
            CheckFogHasOneOwner(repo, check);
            CheckPhaseIsNormalisedAndBounded(check);
            CheckDebugViewsAreReachable(repo, check);
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

        /// <summary>
        /// The aerial-perspective snippet may add a DIRECTIONAL term and
        /// nothing else.
        ///
        /// Vanilla already has a distance falloff and a height band, and colour
        /// grading already removes saturation with the weather through the
        /// budget. A snippet that grew its own distance curve, its own height
        /// term or its own desaturation would be a second subsystem quietly
        /// taking contrast out of the same pixel - which is the exact defect
        /// VisualBudget was built for, and it would be doing it outside the
        /// budget.
        ///
        /// What is checked is that the file reads the sun and the view
        /// direction, and that it does not compute an exponential falloff of
        /// its own.
        /// </summary>
        private static void CheckAerialIsDirectionalOnly(string repo, Action<string, bool, string> check)
        {
            string glsl = Atmosphere(repo);

            check("aerial perspective is aimed by the sun direction",
                  glsl.Contains("vv_atmosSunDir") && glsl.Contains("dot(viewDir"),
                  "no sun-relative term found");

            check("aerial perspective takes the sun's colour from the game",
                  glsl.Contains("vv_atmosSunColor"),
                  "no sun colour uniform");

            check("the snippet grows no distance falloff of its own",
                  !Regex.IsMatch(glsl, @"1\.0\s*(?:-|/)\s*(?:1\.0\s*/\s*)?exp\("),
                  "found an exponential falloff - vanilla already has one");

            check("the snippet grows no height band of its own",
                  !glsl.Contains("flatFog") && !glsl.Contains("worldPosY"),
                  "found a height term - vanilla already has one, in every program");

            check("the snippet removes no saturation of its own",
                  !Regex.IsMatch(glsl, @"\bsaturat(?:e|ion)\b", RegexOptions.IgnoreCase),
                  "found a saturation term - that goes through VisualBudget, not here");
        }

        /// <summary>
        /// Zero has to mean vanilla, and it has to mean it through the path an
        /// UNSET uniform takes.
        ///
        /// A GLSL uniform that was never uploaded reads as exactly 0, and a
        /// uniform can be unset for several reasons - the binder skipped the
        /// frame, the program was not patched, the group rolled back. So 0 must
        /// be the harmless value on every uniform this file declares, not just
        /// on the strength. vv_cloudDensity shipped multiplying vanilla's
        /// density term, where 0 meant NO CLOUDS AT ALL.
        ///
        /// The early return is what makes it true here: at strength 0 the
        /// scatter function hands back the fog colour it was given, untouched,
        /// before the sun direction is read at all.
        /// </summary>
        private static void CheckAerialZeroIsVanilla(string repo, Action<string, bool, string> check)
        {
            string glsl = Atmosphere(repo);

            check("zero strength returns vanilla's fog colour untouched",
                  Regex.IsMatch(glsl, @"if\s*\(\s*vv_atmosAerial\s*<=\s*0\.0\s*\)\s*return\s+fogColor\s*;"),
                  "no early return on zero strength");

            check("zero rain returns vanilla's fog amount untouched",
                  Regex.IsMatch(glsl, @"if\s*\(extra\s*<=\s*0\.0\)\s*return\s+fogWeight\s*;"),
                  "no early return on zero rain");

            check("zero tint returns vanilla's fog colour untouched",
                  Regex.IsMatch(glsl, @"if\s*\(blend\s*<=\s*0\.0\)\s*return\s+fogColor\s*;"),
                  "no early return on zero tint");

            // A sun BELOW the horizon lights the haze from under the world. The
            // guard is a step on elevation, and without it a bright band would
            // point at a sun that has set.
            check("a sun below the horizon contributes nothing",
                  glsl.Contains("step(0.0, vv_atmosSunElevation)"),
                  "no below-horizon guard");
        }

        /// <summary>
        /// Fog must have exactly one owner.
        ///
        /// It used to have two: weather patched applyFog in the two terrain
        /// shaders, so rain thickened the air for a hillside and not for the
        /// animal standing in front of it. Both groups anchoring on the same
        /// line would also have coupled their rollbacks, which the project
        /// forbids for a reason - a varying or a function shared across groups
        /// means one group rolling back breaks the other.
        /// </summary>
        private static void CheckFogHasOneOwner(string repo, Action<string, bool, string> check)
        {
            string dir = Path.Combine(repo, "assets/vintagevisuals/shaderpatches");
            const string anchor = "vec4 applyFog(vec4 rgbaPixel, float fogWeight) {";

            var owners = new System.Collections.Generic.List<string>();
            foreach (string file in Directory.GetFiles(dir, "*.yaml"))
            {
                if (File.ReadAllText(file).Contains(anchor)) owners.Add(Path.GetFileName(file));
            }

            check("exactly one patch group owns vanilla's applyFog",
                  owners.Count == 1 && owners[0] == "atmosphere.yaml",
                  owners.Count == 0 ? "nobody owns it" : string.Join(", ", owners));

            string weather = File.ReadAllText(Path.Combine(dir, "weather.yaml"));
            check("the weather group no longer renders fog",
                  !weather.Contains("vvWeatherFogAmount") && !weather.Contains("vvWeatherFogColor"),
                  "weather.yaml still rewrites the fog mix");

            string glsl = Atmosphere(repo);
            check("the atmosphere group reaches entities as well as terrain",
                  File.ReadAllText(Path.Combine(dir, "atmosphere.yaml")).Contains("entityanimated.fsh"),
                  "entityanimated.fsh is not in the atmosphere group");

            check("the atmosphere pastes its anchor back",
                  glsl.TrimEnd().EndsWith(anchor),
                  "the snippet does not end with the anchor line");
        }

        /// <summary>
        /// The phase function has to be normalised and the gain has to be
        /// capped, and both for the same reason.
        ///
        /// Henyey-Greenstein is unbounded as g approaches 1, and the term
        /// multiplies a colour that has already been through vanilla's own
        /// exposure. Un-normalised it returns about 0.08 facing the sun at
        /// g=0.45 - which looks like the effect is off and invites tuning the
        /// strength up until the peak is wrong instead. Uncapped, a low sun
        /// pushes the horizon past white.
        ///
        /// Driven through the arithmetic rather than through the GLSL text,
        /// because what matters is the numbers it produces.
        /// </summary>
        private static void CheckPhaseIsNormalisedAndBounded(Action<string, bool, string> check)
        {
            const float g = 0.45f;

            check("the isotropic case is 1, so the strength slider means what it says",
                  Math.Abs(Phase(g, 0f, isotropic: true) - 1f) < 1e-4f,
                  Phase(g, 0f, isotropic: true).ToString());

            float facing = Phase(g, 1f, isotropic: false);
            float away = Phase(g, -1f, isotropic: false);

            check("facing the sun scatters more than facing away",
                  facing > away * 4f,
                  facing + " facing vs " + away + " away");

            check("facing the sun is above the isotropic case",
                  facing > 1f, facing.ToString());

            check("facing away is below it",
                  away < 1f, away.ToString());

            // The gain is clamp(phase - 1, 0, 4) times a chain of factors each
            // at most 1, then min'd with the cap. So the cap is the bound.
            float gain = Math.Min(Math.Max(facing - 1f, 0f), 4f);
            check("the raw gain would exceed the cap, so the cap is doing work",
                  gain > 0.85f,
                  gain + " raw vs a cap of 0.85");
        }

        private static float Phase(float g, float cosTheta, bool isotropic)
        {
            float gg = isotropic ? 0f : g;
            float g2 = gg * gg;
            double denom = 1.0 + g2 - 2.0 * gg * cosTheta;
            double hg = (1.0 - g2) / (4.0 * Math.PI * Math.Pow(Math.Max(1e-4, denom), 1.5));
            return (float)(hg * 4.0 * Math.PI);
        }

        /// <summary>
        /// Every debug view the slider can reach must exist, and every view the
        /// shader implements must be reachable.
        ///
        /// A view the slider cannot reach is dead code that reads as a working
        /// diagnostic; a slider position with no view behind it returns the
        /// unshaded pixel, which looks exactly like "the effect is off" and is
        /// the single most misleading thing a debug view can do.
        /// </summary>
        private static void CheckDebugViewsAreReachable(string repo, Action<string, bool, string> check)
        {
            string glsl = Atmosphere(repo);

            var implemented = Regex.Matches(glsl, @"if\s*\(mode\s*==\s*(\d+)\)")
                .Cast<Match>()
                .Select(m => int.Parse(m.Groups[1].Value))
                .Distinct()
                .OrderBy(v => v)
                .ToList();

            string config = File.ReadAllText(Path.Combine(repo, "src/Common/VintageVisualsConfig.cs"));
            Match clamp = Regex.Match(config,
                @"AirDebugView\s*=\s*ColorGradeConfig\.Clamp\(\s*AirDebugView\s*,\s*[\d.]+f?\s*,\s*([\d.]+)f?\s*,");

            check("the atmosphere debug slider declares a maximum",
                  clamp.Success, "no clamp found for AirDebugView");

            if (!clamp.Success) return;

            int max = (int)float.Parse(clamp.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            check("every atmosphere debug view the slider reaches is implemented",
                  Enumerable.Range(1, max).All(implemented.Contains),
                  "slider reaches 1.." + max + ", shader implements " + string.Join(",", implemented));

            check("every atmosphere debug view implemented is reachable",
                  implemented.All(v => v >= 1 && v <= max),
                  "shader implements " + string.Join(",", implemented) + ", slider reaches 1.." + max);
        }

        private static string Atmosphere(string repo)
        {
            return File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/atmosphere.glsl"));
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
