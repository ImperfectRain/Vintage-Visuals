using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// The flora taxonomy, and the ordering rules that make it mean anything.
    ///
    /// Vintage Story classifies its own vegetation per vertex: renderFlags bits
    /// 25-28 carry a wind mode, and vanilla's own applyVertexWarping names each
    /// one - Leaves, Fruit, WaterPlant, "Weak Wind No Bend (for foliage with
    /// non bending stems)", "Weak Wind, Inverse Bend (for vines)". That is a
    /// complete plant taxonomy sitting in the vertex data, correct for modded
    /// content for free, and this mod used to read ONE BIT of it.
    ///
    /// So the checks here are about two things:
    ///
    ///   1. The mod's decode still agrees with the GAME's. The mode numbers are
    ///      transcribed from the dumped shader rather than trusted, because a
    ///      version that renumbered them would silently turn every grass blade
    ///      into a pear.
    ///   2. The ORDERINGS hold. Every constant in the taxonomy is a relative
    ///      judgement - grass is thinner than a canopy, a canopy is thinner than
    ///      fruit - and no amount of retuning should ever put them the wrong way
    ///      round. Pinning the ordering rather than the number leaves the values
    ///      free to be tuned and the meaning fixed.
    /// </summary>
    public static class FloraTaxonomyChecks
    {
        private static string _pbr;

        public static void Run(string repo, Action<string, bool, string> check)
        {
            _pbr = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pseudopbr.glsl"));

            CheckModesMatchTheGame(repo, check);
            CheckLiquidModesAreExcluded(check);
            CheckThinnessOrdering(check);
            CheckFruitIsNotALeaf(check);
            CheckCanopyAndUnderstoryAreDisjoint(check);
            CheckUnderstoryTakesDapple(check);
            CheckShaftsStartAtTheCanopyOnly(check);
            CheckPoolingIsPerPlant(check);
            CheckUnknownFloraIsSafe(check);
            CheckNoBlockIdList(check);
            CheckDebugViewsAreReachable(repo, check);
        }

        /// <summary>
        /// The mode numbers are the GAME's, and the game is the one that can
        /// change them.
        ///
        /// Transcribed from the dumped shader, not from memory. If a Vintage
        /// Story update renumbers a wind mode, every plant in the taxonomy
        /// silently becomes a different plant - grass transmitting like fruit,
        /// a canopy taking the dapple it should be casting - and nothing else
        /// in the repository would notice.
        /// </summary>
        private static void CheckModesMatchTheGame(string repo, Action<string, bool, string> check)
        {
            string dumped = Path.Combine(repo, "reference/game-shaders/chunkopaque.fsh");
            if (!File.Exists(dumped))
            {
                check("the flora modes were checked against the game's own shader", true,
                      "SKIPPED - reference/game-shaders is not present, run tools/verifypatches for the real answer");
                return;
            }

            string game = File.ReadAllText(dumped);

            // vanilla name -> the mod's constant name
            var expected = new (string Vanilla, string Ours)[]
            {
                ("WindModeWeakMask", "VV_FLORA_HERB"),
                ("WindModeNormalMask", "VV_FLORA_GRASS"),
                ("WindModeLeavesMask", "VV_FLORA_LEAVES"),
                ("WindModeBendMask", "VV_FLORA_CROP"),
                ("WindModeTallBendMask", "VV_FLORA_REED"),
                ("WindModeExtraWeakMask", "VV_FLORA_STIFF"),
                ("WindModeFruitMask", "VV_FLORA_FRUIT"),
                ("WindModeWeakWindNoBendMask", "VV_FLORA_BUSH"),
                ("WindModeWeakWindInversedBendMask", "VV_FLORA_VINE"),
                ("WindModeWaterPlant", "VV_FLORA_AQUATIC"),
                ("WindModeWeakLowAlphaTest", "VV_FLORA_THIN"),
            };

            var wrong = new List<string>();
            int compared = 0;

            foreach (var pair in expected)
            {
                Match vanilla = Regex.Match(game,
                    @"const\s+int\s+" + Regex.Escape(pair.Vanilla) + @"\s*=\s*(\d+)\s*<<\s*25\s*;");
                Match ours = Regex.Match(_pbr,
                    @"#define\s+" + Regex.Escape(pair.Ours) + @"\s+(\d+)\b");

                if (!vanilla.Success) { wrong.Add(pair.Vanilla + " not found in the game's shader"); continue; }
                if (!ours.Success) { wrong.Add(pair.Ours + " not defined by the mod"); continue; }

                compared++;
                if (vanilla.Groups[1].Value != ours.Groups[1].Value)
                {
                    wrong.Add(pair.Ours + " is " + ours.Groups[1].Value +
                              " but the game's " + pair.Vanilla + " is " + vanilla.Groups[1].Value);
                }
            }

            check("every flora class matches the game's own wind mode number",
                  wrong.Count == 0, string.Join("; ", wrong));

            check("all eleven flora classes were actually compared",
                  compared == expected.Length, compared + " of " + expected.Length);
        }

        /// <summary>
        /// Modes 6 and 12 are liquid, not plant, and must be mapped out.
        ///
        /// Vanilla uses the same bits for two different things. In chunkliquid
        /// they are LiquidWaterModeBitMask and the wind data overlaps
        /// LiquidExposedToSkyBitMask - so the taxonomy is only meaningful in the
        /// block programs, and even there modes 6 (Water) and 12 (LiquidWarp)
        /// are surfaces rather than vegetation. Treating them as plants would
        /// give a warped water surface leaf transmission.
        /// </summary>
        private static void CheckLiquidModesAreExcluded(Action<string, bool, string> check)
        {
            check("water and liquid-warp modes are excluded from the taxonomy",
                  Regex.IsMatch(_pbr, @"if\s*\(mode\s*==\s*6\s*\|\|\s*mode\s*==\s*12\)\s*return\s+VV_FLORA_NONE\s*;"),
                  "modes 6 and 12 are liquid surfaces, not plants");

            check("the overloading of these bits is written down",
                  _pbr.Contains("LiquidWaterModeBitMask") || _pbr.Contains("OVERLOADED"),
                  "a future reader extending this to chunkliquid needs the warning next to the code");
        }

        /// <summary>
        /// The orderings that carry the whole feature.
        ///
        /// Every thinness value is a relative judgement about how much tissue
        /// light has to cross. The numbers may be tuned; these relationships may
        /// not, because each of them is the difference between two plants that
        /// currently look identical and should not.
        /// </summary>
        private static void CheckThinnessOrdering(Action<string, bool, string> check)
        {
            var t = Thinness();

            check("the taxonomy assigns a thinness to every class",
                  t.Count >= 10, t.Count + " classes found");

            Expect(check, t, "a grass blade transmits more than a tree canopy", "GRASS", "LEAVES");
            Expect(check, t, "a flower petal transmits more than a bush", "HERB", "BUSH");
            Expect(check, t, "a crop transmits more than a canopy", "CROP", "LEAVES");
            Expect(check, t, "a canopy transmits more than fruit", "LEAVES", "FRUIT");
            Expect(check, t, "a reed transmits more than a bush", "REED", "BUSH");

            check("nothing transmits more than a single grass blade",
                  t.Values.Max() <= t["GRASS"] + 1e-6f,
                  "something out-transmits the thinnest tissue in the game");

            check("every thinness stays inside 0..1",
                  t.Values.All(v => v >= 0f && v <= 1f),
                  string.Join(", ", t.Where(kv => kv.Value < 0f || kv.Value > 1f).Select(kv => kv.Key)));
        }

        /// <summary>
        /// Fruit is the case that forced the taxonomy to exist.
        ///
        /// A hanging pear carries a wind mode, so the old single-bit test called
        /// it foliage: it transmitted like a leaf and was tinted toward
        /// yellow-green on the way out, which made ripe fruit read as an unripe
        /// leaf. It is plant tissue and it does transmit a little - but it is
        /// thick, opaque and not green, and the chlorophyll tint has no business
        /// on it.
        /// </summary>
        private static void CheckFruitIsNotALeaf(Action<string, bool, string> check)
        {
            var t = Thinness();

            check("fruit is the least translucent plant tissue",
                  t.ContainsKey("FRUIT") && t["FRUIT"] <= t.Where(kv => kv.Key != "FRUIT").Min(kv => kv.Value),
                  "fruit is a solid ball, not a leaf");

            check("fruit still transmits something rather than nothing",
                  t.ContainsKey("FRUIT") && t["FRUIT"] > 0f,
                  "backlit fruit is faintly translucent and zero would be a different error");

            check("the chlorophyll tint is withheld from fruit",
                  Regex.IsMatch(_pbr, @"chlorophyll\s*=\s*\(vvFloraClass\(\)\s*==\s*VV_FLORA_FRUIT\)\s*\?\s*0\.0\s*:\s*1\.0"),
                  "transmitted light is filtered by chlorophyll, and fruit has none to filter with");

            check("fruit has no tip-to-root gradient",
                  Regex.IsMatch(_pbr, @"flora\s*==\s*VV_FLORA_FRUIT\)\s*return\s+0\.5\s*;"),
                  "vanilla reuses the wind-data bits as a fruit offset, not as a height");
        }

        /// <summary>
        /// A plant is the canopy or it is under one. It cannot be both, and the
        /// two answers drive opposite behaviour.
        /// </summary>
        private static void CheckCanopyAndUnderstoryAreDisjoint(Action<string, bool, string> check)
        {
            Match understory = Regex.Match(_pbr,
                @"bool vvIsUnderstory\(\)\s*\{(.*?)\n\}", RegexOptions.Singleline);

            check("the understory test exists", understory.Success, "");
            if (!understory.Success) return;

            check("the canopy is not in its own understory",
                  !understory.Groups[1].Value.Contains("VV_FLORA_LEAVES"),
                  "leaves cannot be under themselves");

            check("vines are excluded from the understory",
                  !understory.Groups[1].Value.Contains("VV_FLORA_VINE"),
                  "a vine hangs from the occluder, so it is part of it rather than under it");

            check("aquatic flora is excluded from the understory",
                  !understory.Groups[1].Value.Contains("VV_FLORA_AQUATIC"),
                  "the water above it already attenuates the sun, and vanilla handles that");

            check("the canopy test is exactly the leaves class",
                  Regex.IsMatch(_pbr, @"bool vvIsCanopy\(\)\s*\{\s*return vvFloraClass\(\) == VV_FLORA_LEAVES;"),
                  "only tree leaves cast a forest's shade");
        }

        /// <summary>
        /// The forest-floor defect, pinned.
        ///
        /// vvCanopyDapple used to reject every plant, so a forest floor's tall
        /// grass and flowers stayed evenly lit while the soil between them was
        /// dappled - and the two read as different places. Only the canopy is
        /// exempt: grass, herbs, crops and bushes take the broken light like
        /// anything else standing under a tree.
        /// </summary>
        private static void CheckUnderstoryTakesDapple(Action<string, bool, string> check)
        {
            Match dapple = Regex.Match(_pbr,
                @"float vvCanopyDapple\([^)]*\)\s*\{(.*?)\n\}", RegexOptions.Singleline);

            check("the dapple function exists", dapple.Success, "");
            if (!dapple.Success) return;

            string body = dapple.Groups[1].Value;

            check("the canopy itself is never dappled",
                  body.Contains("if (vvIsCanopy()) return 0.0;"),
                  "a tree casts this rather than wearing it");

            check("the understory is no longer excluded along with the canopy",
                  !Regex.IsMatch(body, @"if \(vvIsFoliage\(\)\) return 0\.0;\s*\n\s*(?!.*vvIsUnderstory)"),
                  "rejecting all foliage leaves a forest floor's grass evenly lit");

            check("the understory is admitted explicitly",
                  body.Contains("vvIsUnderstory()"),
                  "grass under a tree is exactly what should catch sunflecks");
        }

        /// <summary>
        /// A beam is sunlight through a gap in something OVERHEAD.
        ///
        /// Grass at ankle height has no gap above it to be the start of one, and
        /// treating every plant as a shaft source put beams in the lawn.
        /// </summary>
        private static void CheckShaftsStartAtTheCanopyOnly(Action<string, bool, string> check)
        {
            Match shaft = Regex.Match(_pbr,
                @"float vvCanopyShaft\([^)]*\)\s*\{(.*?)\n\}", RegexOptions.Singleline);

            check("the shaft function exists", shaft.Success, "");
            if (!shaft.Success) return;

            string body = shaft.Groups[1].Value;

            check("shafts start at backlit canopy",
                  body.Contains("if (vvIsCanopy()) return strength * VV_SHAFT_LEAF;"),
                  "the canopy is the thing with gaps in it");

            check("no other plant is a shaft source",
                  Regex.IsMatch(body, @"if \(vvIsFoliage\(\)\) return 0\.0;"),
                  "ankle-height grass has nothing overhead to let light through");
        }

        /// <summary>
        /// How much rain a plant HOLDS is a fact about its shape, not about
        /// which way the face points. A cupped flower and a grass blade in one
        /// shower end up differently wet.
        /// </summary>
        private static void CheckPoolingIsPerPlant(Action<string, bool, string> check)
        {
            var p = Pooling();

            check("pooling is defined per plant", p.Count >= 3, p.Count + " cases");

            Expect(check, p, "a cupped flower holds more rain than a grass blade", "HERB", "GRASS");

            check("aquatic flora cannot get wetter",
                  p.ContainsKey("AQUATIC") && p["AQUATIC"] == 0f,
                  "it is already in the water; wetness is not a state it can gain");

            check("waxy fruit sheds most of what lands on it",
                  p.ContainsKey("FRUIT") && p["FRUIT"] < p["HERB"],
                  "a waxy skin sheds, and what stays beads rather than soaking");

            check("the wetness gate consults the plant",
                  _pbr.Contains("vvFloraPooling()"),
                  "one number for every plant is what this replaced");
        }

        /// <summary>
        /// An unrecognised wind mode - a future vanilla addition, or a mod using
        /// one this taxonomy has not seen - must land somewhere safe.
        ///
        /// Conservative, not zero and not maximum: a plant that transmitted
        /// nothing would look like painted cardboard, and one that transmitted
        /// like grass would glow. The middle is the only answer that is wrong by
        /// a little in both directions instead of badly in one.
        /// </summary>
        private static void CheckUnknownFloraIsSafe(Action<string, bool, string> check)
        {
            var t = Thinness();

            Match fallback = Regex.Match(_pbr, @"else\s+base\s*=\s*([\d.]+)\s*;\s*//\s*unknown mode");

            check("an unknown wind mode has a documented fallback",
                  fallback.Success, "no default branch in vvFloraThinness");

            if (!fallback.Success) return;

            float value = float.Parse(fallback.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            check("the unknown fallback is conservative rather than zero or maximum",
                  value > 0f && value < t["GRASS"] && value <= t["LEAVES"],
                  "unknown flora is " + value + ", grass " + t["GRASS"] + ", leaves " + t["LEAVES"]);

            check("an unknown mode is still treated as a plant",
                  Regex.IsMatch(_pbr, @"if \(\(renderFlags & WindModeBitMask\) == 0\) return VV_FLORA_NONE;"),
                  "only the absence of a wind mode means 'not a plant'");
        }

        /// <summary>
        /// No block-ID list. The whole point of reading vanilla's wind mode is
        /// that it is already correct for content this mod has never seen.
        /// </summary>
        private static void CheckNoBlockIdList(Action<string, bool, string> check)
        {
            check("flora is classified from vanilla's vertex data",
                  _pbr.Contains("(renderFlags >> WindModePosition) & 0xF"),
                  "the classification must come from the game, not from a list");

            check("no block-name matching leaked into the shader",
                  !Regex.IsMatch(_pbr, @"""(?:tallgrass|flower|sapling|leaves|crop)""", RegexOptions.IgnoreCase),
                  "a hardcoded list would be wrong for every mod that adds a plant");

            // A colour test is the classic wrong answer, and it is wrong in both
            // directions: an autumn canopy is not green and a painted green wall
            // is not a plant.
            check("flora is not identified by being green",
                  !Regex.IsMatch(_pbr, @"albedo\.g\s*>\s*albedo\.r"),
                  "an autumn canopy is not green and a green wall is not a plant");
        }

        private static void CheckDebugViewsAreReachable(string repo, Action<string, bool, string> check)
        {
            string config = File.ReadAllText(Path.Combine(repo, "src/Common/VintageVisualsConfig.cs"));
            Match clamp = Regex.Match(config,
                @"DebugView = ColorGradeConfig\.Clamp\(DebugView, 0\.0f, ([\d.]+)f,");

            check("the PseudoPBR debug slider declares a maximum", clamp.Success, "");
            if (!clamp.Success) return;

            int max = (int)float.Parse(clamp.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            foreach (int view in new[] { 44, 45, 46 })
            {
                check("flora debug view " + view + " is implemented",
                      Regex.IsMatch(_pbr, @"if \(mode == " + view + @"\)"),
                      "view " + view + " is documented but not drawn");

                check("flora debug view " + view + " is reachable from the slider",
                      view <= max, "the slider stops at " + max);
            }
        }

        // --- helpers ---------------------------------------------------------

        private static Dictionary<string, float> Thinness()
        {
            var found = new Dictionary<string, float>();

            foreach (Match m in Regex.Matches(_pbr,
                @"flora == VV_FLORA_(\w+)\)\s*base = ([\d.]+)f?;"))
            {
                found[m.Groups[1].Value] = float.Parse(m.Groups[2].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            Match fruit = Regex.Match(_pbr,
                @"if \(flora == VV_FLORA_FRUIT\) return ([\d.]+);");
            if (fruit.Success)
            {
                found["FRUIT"] = float.Parse(fruit.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            return found;
        }

        private static Dictionary<string, float> Pooling()
        {
            var found = new Dictionary<string, float>();

            Match body = Regex.Match(_pbr,
                @"float vvFloraPooling\(\)\s*\{(.*?)\n\}", RegexOptions.Singleline);
            if (!body.Success) return found;

            foreach (Match m in Regex.Matches(body.Groups[1].Value,
                @"flora == VV_FLORA_(\w+)\) return ([\d.]+);"))
            {
                found[m.Groups[1].Value] = float.Parse(m.Groups[2].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            return found;
        }

        private static void Expect(Action<string, bool, string> check,
                                   Dictionary<string, float> values,
                                   string what, string more, string less)
        {
            bool have = values.ContainsKey(more) && values.ContainsKey(less);

            check(what, have && values[more] > values[less],
                  have ? more + " " + values[more] + " vs " + less + " " + values[less]
                       : "missing " + (values.ContainsKey(more) ? less : more));
        }
    }
}
