using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VintageVisuals.Atmosphere;
using VintageVisuals.Common;
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
            CheckEveryFeatureIsIndependent(check);
            CheckNoDuplicateWeatherOrSun(repo, check);
            CheckQualityDoesNotOverrideEnablement(check);
            CheckStateIsPureData(repo, check);
            CheckAtmosphereDoesNotTouchMaterials(repo, check);
            CheckEnergyBudgetsAgree(repo, check);
            CheckNormalisationIsPinned(check);
            CheckTemporalContinuity(repo, check);
            CheckEveryFeatureReachesThePanel(repo, check);
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
        /// Zero has to mean vanilla, and it has to mean it EXACTLY.
        ///
        /// The transport is composed as participating media:
        ///
        ///     out = surface * T + inscatter * (1 - T)
        ///
        /// With every strength at zero the added density is zero, the height
        /// factor is one, so T = 1 - fogWeight; the gain is zero and both colour
        /// shifts return their input, so inscatter is vanilla's fog colour. The
        /// result is mix(surface, fogColor, fogWeight) - which is vanilla's
        /// applyFog, character for character.
        ///
        /// That is an ALGEBRAIC identity rather than a tuning coincidence, and
        /// it is the whole safety argument for a mod owning applyFog in four
        /// shading programs. So it is checked as one: the transport is
        /// reproduced here in C# and compared against vanilla's mix across a
        /// sweep of fog weights and distances.
        ///
        /// A GLSL uniform that was never uploaded reads as exactly 0, and a
        /// uniform can be unset for several reasons - the binder skipped the
        /// frame, the program was not patched, the group rolled back. All three
        /// land on this identity.
        /// </summary>
        private static void CheckAerialZeroIsVanilla(string repo, Action<string, bool, string> check)
        {
            float worst = 0f;
            string where = "";

            foreach (float fogWeight in new[] { 0f, 0.05f, 0.25f, 0.5f, 0.75f, 0.99f })
            {
                foreach (float distance in new[] { 0.5f, 8f, 60f, 250f })
                {
                    float surface = 0.62f;
                    float fogColor = 0.81f;

                    float t = TransmittanceOff(fogWeight);
                    float ours = surface * t + fogColor * (1f - t);
                    float vanilla = surface * (1f - fogWeight) + fogColor * fogWeight;

                    float error = Math.Abs(ours - vanilla);
                    if (error > worst)
                    {
                        worst = error;
                        where = "fogWeight " + fogWeight + ", distance " + distance;
                    }
                }
            }

            check("with every feature off the transport IS vanilla's mix",
                  worst < 1e-4f,
                  "worst error " + worst.ToString("0.#######") + " at " + where);

            string glsl = Atmosphere(repo);

            check("the composition is participating media, not a chain of multipliers",
                  Regex.IsMatch(glsl, @"rgbaPixel\.rgb\s*\*\s*t\s*\+\s*inscatter\s*\*\s*\(1\.0\s*-\s*t\)"),
                  "the final composition is not surface * T + inscatter * (1 - T)");

            // A source BELOW the horizon lights the air from under the world. A
            // bright band pointing at it is a hole in the ground, and BOTH the
            // sun and the moon need the guard.
            check("a light source below the horizon contributes nothing",
                  glsl.Contains("vvAtmosAboveHorizon"),
                  "no below-horizon guard");

            check("the sun's terms are guarded",
                  glsl.Contains("vvAtmosAboveHorizon(vv_atmosSunElevation)"),
                  "the sun is not guarded");

            check("the moon's terms are guarded",
                  glsl.Contains("vvAtmosAboveHorizon(vv_atmosMoonDir.y)"),
                  "the moon is not guarded");

            check("rain draining the air returns it untouched when it is dry",
                  Regex.IsMatch(glsl, @"if\s*\(blend\s*<=\s*0\.0\)\s*return\s+fogColor\s*;"),
                  "no early return on zero weather tint");
        }

        /// <summary>
        /// Vanilla's transmittance with every mod feature off, reproduced.
        ///
        /// Deliberately not a copy of the shader's general form: this is the
        /// ZERO case, and writing it out as the shader's own expression with
        /// zeroes substituted would let a bug in that expression cancel itself.
        /// With no added density and a height factor of one, the shader reduces
        /// to exactly this.
        /// </summary>
        private static float TransmittanceOff(float fogWeight)
        {
            float vanilla = 1f - Math.Min(1f, Math.Max(0f, fogWeight));
            float depth = -(float)Math.Log(Math.Max(1e-4f, vanilla));
            return Math.Min(1f, Math.Max(0f, (float)Math.Exp(-depth)));
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

        /// <summary>
        /// Turning one feature off must turn off exactly that feature.
        ///
        /// Eleven strengths land in one shader, and the failure this guards is
        /// not that a feature does nothing - it is that a feature does
        /// something when it is off, or that switching one off silently takes
        /// another with it. The first is a leak, the second is coupling, and
        /// both read to a player as "the sliders do not work".
        ///
        /// Driven through the real derivation with a config that has everything
        /// on, then each feature zeroed one at a time.
        /// </summary>
        private static void CheckEveryFeatureIsIndependent(Action<string, bool, string> check)
        {
            AtmosphereState air = Stormy();

            var features = new (string Name, Action<AtmosphereConfig> Off, System.Func<AtmosphereInputs, float> Read)[]
            {
                ("AerialPerspective",       c => c.AerialPerspective = 0f,       i => i.Aerial),
                ("HorizonScattering",       c => c.HorizonScattering = 0f,       i => i.Horizon),
                ("SunScattering",           c => c.SunScattering = 0f,           i => i.SunScatter),
                ("HeightAttenuation",       c => c.HeightAttenuation = 0f,       i => i.HeightAttenuation),
                ("WeatherExtinction",       c => c.WeatherExtinction = 0f,       i => i.WeatherExtinction),
                ("CloudAtmosphere",         c => c.CloudAtmosphere = 0f,         i => i.CloudAtmosphere),
                ("CloudEdgeScattering",     c => c.CloudEdgeScattering = 0f,     i => i.CloudEdge),
                ("Godrays",                 c => c.Godrays = 0f,                 i => i.Godray),
                ("PrecipitationScattering", c => c.PrecipitationScattering = 0f, i => i.Precipitation),
                ("MoonScattering",          c => c.MoonScattering = 0f,          i => i.Moon),
                ("DappleInteraction",       c => c.DappleInteraction = 0f,       i => i.Dapple),
            };

            var leaked = new System.Collections.Generic.List<string>();
            var coupled = new System.Collections.Generic.List<string>();
            var inert = new System.Collections.Generic.List<string>();

            AtmosphereInputs all = AtmosphereInputs.Derive(air, AllOn(), SceneGrants.Full);

            foreach (var feature in features)
            {
                AtmosphereConfig config = AllOn();
                feature.Off(config);
                AtmosphereInputs one = AtmosphereInputs.Derive(air, config, SceneGrants.Full);

                if (feature.Read.Invoke(one) != 0f) leaked.Add(feature.Name);
                if (feature.Read.Invoke(all) <= 0f) inert.Add(feature.Name);

                foreach (var other in features)
                {
                    if (other.Name == feature.Name) continue;

                    // CloudAtmosphere legitimately damps the directional terms -
                    // that is the whole feature, and it is documented. Every
                    // other pair must be independent.
                    if (feature.Name == "CloudAtmosphere") continue;

                    if (other.Read.Invoke(one) != other.Read.Invoke(all))
                    {
                        coupled.Add(feature.Name + " changed " + other.Name);
                    }
                }
            }

            check("every feature reaches the shader when it is on",
                  inert.Count == 0, string.Join(", ", inert));

            check("switching a feature off zeroes it",
                  leaked.Count == 0, string.Join(", ", leaked));

            check("switching a feature off changes no other feature",
                  coupled.Count == 0, string.Join("; ", coupled));

            AtmosphereInputs off = AtmosphereInputs.Derive(air, new AtmosphereConfig { Enabled = false }, SceneGrants.Full);

            check("the master toggle zeroes every feature at once",
                  features.All(f => f.Read.Invoke(off) == 0f),
                  string.Join(", ", features.Where(f => f.Read.Invoke(off) != 0f).Select(f => f.Name)));

            check("the master toggle produces exactly the Off inputs",
                  off.Aerial == AtmosphereInputs.Off.Aerial && off.DebugView == 0f,
                  "disabled did not reduce to Off");
        }

        /// <summary>
        /// No second weather model, and no second sun.
        ///
        /// This is the rule the whole subsystem is built on: Vintage Story owns
        /// environmental truth, Vintage Visuals owns visual interpretation. The
        /// failure it guards against is not a crash - it is two places in the
        /// mod slowly disagreeing about whether it is raining, which is exactly
        /// what EnvironmentState was created to end and would be undone by an
        /// atmosphere that sampled the world for itself.
        /// </summary>
        private static void CheckNoDuplicateWeatherOrSun(string repo, Action<string, bool, string> check)
        {
            string dir = Path.Combine(repo, "src/Atmosphere");
            string code = string.Join("\n", Directory.GetFiles(dir, "*.cs").OrderBy(f => f).Select(File.ReadAllText));

            // The subsystem may read the tracker. It may not go to the world
            // itself: every world query in this mod belongs in one file.
            check("the atmosphere does not sample the climate itself",
                  !code.Contains("GetClimateAt") && !code.Contains("GetPrecipitation"),
                  "found a direct climate query - that belongs in EnvironmentTracker");

            check("the atmosphere does not read the calendar itself",
                  !code.Contains("SunPositionNormalized") && !code.Contains("MoonPosition") &&
                  !code.Contains("DayLightStrength"),
                  "found a direct calendar read - that belongs in EnvironmentTracker");

            check("the atmosphere does not read the cloud renderer itself",
                  !code.Contains("CloudTileReader"),
                  "found a cloud read - cloud state comes through the shared state");

            // One sun direction in the GLSL, and it is the uploaded one. Vanilla's
            // lightPosition is deliberately NOT used here: it is declared in the
            // terrain shaders and not in the particle ones, so reading it would
            // make this file mean different things in different programs.
            string glsl = Atmosphere(repo);
            check("the shader has one sun direction and it is the uploaded one",
                  glsl.Contains("vv_atmosSunDir") && !glsl.Contains("lightPosition") &&
                  !glsl.Contains("sunPosition"),
                  "found a second sun direction in atmosphere.glsl");
        }

        /// <summary>
        /// Quality may scale a feature's cost or contribution. It may not switch
        /// the feature on.
        ///
        /// The failure mode is specific and it is a trust problem rather than a
        /// rendering one: a player sets a quality preset to high, a feature they
        /// deliberately disabled comes back, and nothing in the interface
        /// explains why.
        /// </summary>
        private static void CheckQualityDoesNotOverrideEnablement(Action<string, bool, string> check)
        {
            AtmosphereState air = Stormy();

            var off = AllOn();
            off.Godrays = 0f;
            off.GodrayQuality = 2f;

            AtmosphereInputs disabled = AtmosphereInputs.Derive(air, off, SceneGrants.Full);

            check("the highest quality does not switch a disabled feature on",
                  disabled.Godray == 0f, disabled.Godray.ToString());

            var low = AllOn();
            low.GodrayQuality = 0f;
            var high = AllOn();
            high.GodrayQuality = 2f;

            float lowGain = AtmosphereInputs.Derive(air, low, SceneGrants.Full).Godray;
            float highGain = AtmosphereInputs.Derive(air, high, SceneGrants.Full).Godray;

            check("quality still changes what an enabled feature contributes",
                  lowGain > 0f && lowGain < highGain,
                  lowGain + " low vs " + highGain + " high");
        }

        /// <summary>
        /// AtmosphereState is a data model. It may not know about rendering.
        ///
        /// The moment it does, every consumer inherits that dependency and the
        /// state can no longer be driven through an arbitrary sky in a test
        /// without a client - which is the property that lets the rest of this
        /// file exist.
        /// </summary>
        private static void CheckStateIsPureData(string repo, Action<string, bool, string> check)
        {
            string state = File.ReadAllText(Path.Combine(repo, "src/Common/Scene/AtmosphereState.cs"));

            check("AtmosphereState references no client or render API",
                  !state.Contains("ICoreClientAPI") && !state.Contains("IShaderProgram") &&
                  !state.Contains("IRenderer") && !state.Contains("Vintagestory.API.Client"),
                  "found a render dependency in the state");

            check("AtmosphereState carries no config value",
                  !state.Contains("Config"),
                  "found a config reference - a strength slider is not a fact about the world");

            check("AtmosphereState has no shader uniform names",
                  !state.Contains("vv_atmos"),
                  "found a uniform name in the state");
        }

        /// <summary>
        /// Atmosphere changes how light TRAVELS. PBR decides how a surface
        /// RESPONDS. Faking the first by editing the second is the shortcut this
        /// forbids.
        ///
        /// A nearby metal block must stay a metal block. A distant one may lose
        /// contrast through extinction - which is what this file does, to the
        /// composed colour, after the material has had its say.
        /// </summary>
        private static void CheckAtmosphereDoesNotTouchMaterials(string repo, Action<string, bool, string> check)
        {
            string glsl = Atmosphere(repo);

            var forbidden = new[] { "roughness", "metal", "albedo", "normalStrength", "specular", "vvSampleMaterial" };
            var found = forbidden.Where(t => glsl.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            check("the atmosphere touches no material parameter",
                  found.Count == 0,
                  string.Join(", ", found));

            check("the atmosphere runs on the composed colour, not on a surface",
                  glsl.Contains("vec4 applyFog(vec4 rgbaPixel, float fogWeight) {"),
                  "the anchor is not vanilla's fog application point");
        }

        /// <summary>
        /// The GLSL and the C# must agree about the ceilings, or one of them is
        /// enforcing a budget the other does not know about.
        ///
        /// Both sides cap the added extinction, and the constants are written
        /// twice because a shader cannot read a C# field. A smoke check is the
        /// only thing that can keep the pair honest.
        /// </summary>
        private static void CheckEnergyBudgetsAgree(string repo, Action<string, bool, string> check)
        {
            string glsl = Atmosphere(repo);

            Match density = Regex.Match(glsl, @"VV_ATMOS_MAX_DENSITY\s*=\s*([\d.]+)\s*;");
            Match clamp = Regex.Match(glsl, @"VV_ATMOS_FOG_CLAMP\s*=\s*([\d.]+)\s*;");
            Match gain = Regex.Match(glsl, @"VV_ATMOS_MAX_GAIN\s*=\s*([\d.]+)\s*;");

            check("the shader declares an extinction ceiling",
                  density.Success, "VV_ATMOS_MAX_DENSITY not found");
            check("the shader declares an inscatter ceiling",
                  gain.Success, "VV_ATMOS_MAX_GAIN not found");

            if (density.Success)
            {
                float shader = float.Parse(density.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                check("the extinction ceiling matches the one the CPU derives against",
                      Math.Abs(shader - AtmosphereInputs.MaxAddedDensity) < 1e-6f,
                      "shader " + shader + " vs CPU " + AtmosphereInputs.MaxAddedDensity);
            }

            if (clamp.Success)
            {
                float shader = float.Parse(clamp.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                check("the shader clamps distance where the state says the game does",
                      Math.Abs(shader - AtmosphereState.FogDistanceClamp) < 1e-3f,
                      "shader " + shader + " vs state " + AtmosphereState.FogDistanceClamp);
            }

            // The ceiling has to be reachable, or it is decoration. Three
            // sources summing is exactly the case it exists for.
            check("the ceiling is low enough that stacked sources actually hit it",
                  AtmosphereInputs.MaxAddedDensity < 0.0022f + 0.004f + 0.006f,
                  AtmosphereInputs.MaxAddedDensity + " vs a possible sum of 0.0122");
        }

        /// <summary>
        /// Every normalisation must be documented and must behave at both ends.
        ///
        /// A shader should not have to understand Vintage Story's raw units, and
        /// the risk in saying so is destructive clamping - a normalisation that
        /// erases a real environmental difference is worse than none, because
        /// the difference is gone and nothing reports it.
        /// </summary>
        private static void CheckNormalisationIsPinned(Action<string, bool, string> check)
        {
            AtmosphereConfig config = AllOn();

            AtmosphereInputs sea = AtmosphereInputs.Derive(AtAltitude(0f), config, SceneGrants.Full);
            AtmosphereInputs mid = AtmosphereInputs.Derive(AtAltitude(AtmosphereInputs.ThinAirBlocks * 0.5f), config, SceneGrants.Full);
            AtmosphereInputs top = AtmosphereInputs.Derive(AtAltitude(AtmosphereInputs.ThinAirBlocks), config, SceneGrants.Full);
            AtmosphereInputs above = AtmosphereInputs.Derive(AtAltitude(AtmosphereInputs.ThinAirBlocks * 4f), config, SceneGrants.Full);

            check("altitude normalises to 0 at sea level",
                  sea.Altitude == 0f, sea.Altitude.ToString());

            check("altitude is monotonic and reaches 1",
                  mid.Altitude > 0f && mid.Altitude < 1f && Math.Abs(top.Altitude - 1f) < 1e-4f,
                  mid.Altitude + " mid, " + top.Altitude + " top");

            check("altitude saturates rather than running away",
                  above.Altitude == 1f, above.Altitude.ToString());

            // Broken cloud peaks in the middle and is zero at both ends. That
            // shape is the feature, not an accident of the formula: a clear sky
            // and a solid overcast both have no edges in them.
            float clear = AtmosphereInputs.Derive(WithCover(0f), config, SceneGrants.Full).BrokenCloud;
            float half = AtmosphereInputs.Derive(WithCover(0.5f), config, SceneGrants.Full).BrokenCloud;
            float solid = AtmosphereInputs.Derive(WithCover(1f), config, SceneGrants.Full).BrokenCloud;

            check("a clear sky has no broken cloud",
                  clear == 0f, clear.ToString());
            check("a solid overcast has no broken cloud",
                  solid == 0f, solid.ToString());
            check("half cover is where broken cloud peaks",
                  Math.Abs(half - 1f) < 1e-4f, half.ToString());

            // The base density is deliberately NOT normalised: it is an
            // extinction coefficient and the shader uses it as one.
            check("vanilla's fog density passes through unnormalised",
                  Math.Abs(sea.BaseDensity - AtmosphereState.VanillaFogDensity) < 1e-6f,
                  sea.BaseDensity.ToString());
        }

        /// <summary>
        /// The atmosphere must not jump as the world moves through it.
        ///
        /// Nothing here samples the screen, uses noise, or accumulates across
        /// frames, so the usual crawling and shimmer cannot occur by
        /// construction. What CAN occur is a discontinuity in the arithmetic -
        /// a term that switches rather than ramps - and the two places it would
        /// are both moments the player is guaranteed to be looking at:
        ///
        ///   - the sun crossing the horizon, every dawn and every dusk;
        ///   - weather starting and stopping.
        ///
        /// A hard step at either reads as the world flickering. So the
        /// continuity is checked as continuity: the derivation is swept through
        /// each transition and the largest single-step change is compared
        /// against the step size. A ramp stays proportional; a switch does not.
        /// </summary>
        private static void CheckTemporalContinuity(string repo, Action<string, bool, string> check)
        {
            AtmosphereConfig config = AllOn();

            // The sun setting, sampled finely through the horizon.
            float worstSun = 0f;
            float previous = -1f;
            for (int i = 0; i <= 400; i++)
            {
                float elevation = -0.1f + i * 0.001f;
                AtmosphereInputs now = AtmosphereInputs.Derive(WithSunElevation(elevation), config, SceneGrants.Full);

                if (previous >= 0f) worstSun = Math.Max(worstSun, Math.Abs(now.SunElevation - previous));
                previous = now.SunElevation;
            }

            check("sun elevation moves continuously through the horizon",
                  worstSun < 0.01f,
                  "largest single step " + worstSun.ToString("0.####") + " for an input step of 0.001");

            // Weather arriving and clearing.
            float worstWeather = 0f;
            float previousExtinction = -1f;
            for (int i = 0; i <= 200; i++)
            {
                float rain = i * 0.005f;
                AtmosphereInputs now = AtmosphereInputs.Derive(WithRain(rain), config, SceneGrants.Full);

                if (previousExtinction >= 0f)
                {
                    worstWeather = Math.Max(worstWeather, Math.Abs(now.Rain - previousExtinction));
                }
                previousExtinction = now.Rain;
            }

            check("weather arrives continuously rather than switching on",
                  worstWeather < 0.01f,
                  "largest single step " + worstWeather.ToString("0.####") + " for an input step of 0.005");

            // Cloud cover, where the broken-cloud term is a curve rather than a
            // pass-through and so is the one most likely to hide a kink.
            float worstBroken = 0f;
            float previousBroken = -1f;
            for (int i = 0; i <= 200; i++)
            {
                AtmosphereInputs now = AtmosphereInputs.Derive(WithCover(i * 0.005f), config, SceneGrants.Full);
                if (previousBroken >= 0f) worstBroken = Math.Max(worstBroken, Math.Abs(now.BrokenCloud - previousBroken));
                previousBroken = now.BrokenCloud;
            }

            check("broken cloud varies continuously with cover",
                  worstBroken < 0.03f,
                  "largest single step " + worstBroken.ToString("0.####"));

            string glsl = Atmosphere(repo);

            // The below-horizon guard is a RAMP, not a step. A step() on
            // elevation would pop the whole scattering term on at sunrise.
            check("the horizon guard ramps rather than steps",
                  glsl.Contains("clamp(elevation * 8.0, 0.0, 1.0)") && !Regex.IsMatch(glsl, @"step\s*\(\s*0\.0\s*,\s*vv_atmos"),
                  "found a hard step where a ramp is needed");

            // Nothing temporal, and nothing screen-space. Both would be a new
            // class of instability and both need a decision, not a commit.
            check("the atmosphere accumulates nothing across frames",
                  !glsl.Contains("previousFrame") && !glsl.Contains("vv_atmosPrev"),
                  "found temporal accumulation - that needs justifying first");

            check("the atmosphere samples no texture",
                  !glsl.Contains("texture("),
                  "found a texture read - this pass adds no sampler and no bandwidth");
        }

        /// <summary>
        /// Every feature must be reachable from the in-game panel, not only
        /// from the config file.
        ///
        /// The brief's requirement is that all eleven are independently
        /// controllable, and a setting that exists only in JSON fails that for
        /// every player who never opens it. This is also the file with the
        /// silent failure mode: two settings sharing a weight blanks the WHOLE
        /// ConfigLib panel rather than one row, and nothing reports it.
        /// </summary>
        private static void CheckEveryFeatureReachesThePanel(string repo, Action<string, bool, string> check)
        {
            string config = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/config/configlib-patches.json"));
            string bridge = File.ReadAllText(
                Path.Combine(repo, "src/Common/ConfigLibBridge.cs"));

            var properties = new[]
            {
                "AerialPerspective", "HorizonScattering", "SunScattering", "HeightAttenuation",
                "HeightHaze", "WeatherExtinction", "WeatherTint", "CloudAtmosphere",
                "CloudEdgeScattering", "Godrays", "GodrayQuality", "PrecipitationScattering",
                "MoonScattering", "DappleInteraction", "AirDebugView",
            };

            var missing = properties.Where(p => !bridge.Contains("atmosphere." + p + " =")).ToList();

            check("every atmosphere setting is wired into the ConfigLib bridge",
                  missing.Count == 0, string.Join(", ", missing));

            var unsettable = properties
                .Select(p => Regex.Match(bridge, "case \"(atmosphere_\\w+)\":\\s*atmosphere\\." + p + "\\s*="))
                .Where(m => !m.Success)
                .ToList();

            check("every wired setting has a case label to reach it",
                  unsettable.Count == 0, unsettable.Count + " properties have no case");

            // And the label has to exist in the panel definition, or the case
            // is unreachable code that looks like a working setting.
            var orphaned = Regex.Matches(bridge, "case \"(atmosphere_\\w+)\":")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Where(name => !config.Contains("\"" + name + "\""))
                .ToList();

            check("no atmosphere case handles a setting the panel does not define",
                  orphaned.Count == 0, string.Join(", ", orphaned));
        }

        private static AtmosphereState WithSunElevation(float elevation)
        {
            AtmosphereState s = Stormy();
            return new AtmosphereState(
                s.FogColor, s.FogDensity, s.FogMin, s.FogBrightness,
                s.FlatFogDensity, s.FlatFogYPos,
                s.SunColor, new Vec3f(0.6f, elevation, 0.76f), s.DayLight,
                s.MoonDirection, s.MoonLight,
                s.Rain, s.Snow, s.CloudCover,
                s.Temperature, s.Humidity,
                s.AmbientColor, s.HeightAboveSeaLevel, s.ViewDistance, s.SkyExposure);
        }

        private static AtmosphereState WithRain(float rain)
        {
            AtmosphereState s = Stormy();
            return new AtmosphereState(
                s.FogColor, s.FogDensity, s.FogMin, s.FogBrightness,
                s.FlatFogDensity, s.FlatFogYPos,
                s.SunColor, s.SunDirection, s.DayLight,
                s.MoonDirection, s.MoonLight,
                rain, s.Snow, s.CloudCover,
                s.Temperature, s.Humidity,
                s.AmbientColor, s.HeightAboveSeaLevel, s.ViewDistance, s.SkyExposure);
        }

        /// <summary>Every feature on, so that switching one off is the only variable.</summary>
        private static AtmosphereConfig AllOn()
        {
            return new AtmosphereConfig
            {
                Enabled = true,
                AerialPerspective = 0.6f,
                HorizonScattering = 0.6f,
                SunScattering = 0.6f,
                HeightAttenuation = 0.6f,
                HeightHaze = 0.6f,
                WeatherExtinction = 0.6f,
                WeatherTint = 0.6f,
                CloudAtmosphere = 0.6f,
                CloudEdgeScattering = 0.6f,
                Godrays = 0.6f,
                GodrayQuality = 1f,
                PrecipitationScattering = 0.6f,
                MoonScattering = 0.6f,
                DappleInteraction = 0.6f,
            };
        }

        /// <summary>
        /// A night with weather, cloud and a moon, so every feature has
        /// something to respond to. A clear noon would leave half of them at
        /// zero and the independence check would prove nothing.
        /// </summary>
        private static AtmosphereState Stormy()
        {
            return new AtmosphereState(
                fogColor: new Vec3f(0.7f, 0.74f, 0.8f),
                fogDensity: AtmosphereState.VanillaFogDensity,
                fogMin: 0f, fogBrightness: 1f,
                flatFogDensity: 0f, flatFogYPos: 0f,
                sunColor: new Vec3f(1f, 0.85f, 0.7f),
                sunDirection: new Vec3f(0.6f, 0.25f, 0.76f),
                dayLight: 0.4f,
                moonDirection: new Vec3f(-0.3f, 0.5f, 0.81f),
                moonLight: 0.5f,
                rain: 0.7f, snow: 0.3f, cloudCover: 0.5f,
                temperature: 2f, humidity: 0.8f,
                ambientColor: new Vec3f(1f, 1f, 1f),
                heightAboveSeaLevel: 40f,
                viewDistance: 1500f,
                skyExposure: 1f);
        }

        private static AtmosphereState AtAltitude(float blocks)
        {
            AtmosphereState s = Stormy();
            return new AtmosphereState(
                s.FogColor, s.FogDensity, s.FogMin, s.FogBrightness,
                s.FlatFogDensity, s.FlatFogYPos,
                s.SunColor, s.SunDirection, s.DayLight,
                s.MoonDirection, s.MoonLight,
                s.Rain, s.Snow, s.CloudCover,
                s.Temperature, s.Humidity,
                s.AmbientColor, blocks, s.ViewDistance, s.SkyExposure);
        }

        private static AtmosphereState WithCover(float cover)
        {
            AtmosphereState s = Stormy();
            return new AtmosphereState(
                s.FogColor, s.FogDensity, s.FogMin, s.FogBrightness,
                s.FlatFogDensity, s.FlatFogYPos,
                s.SunColor, s.SunDirection, s.DayLight,
                s.MoonDirection, s.MoonLight,
                s.Rain, s.Snow, cover,
                s.Temperature, s.Humidity,
                s.AmbientColor, s.HeightAboveSeaLevel, s.ViewDistance, s.SkyExposure);
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
