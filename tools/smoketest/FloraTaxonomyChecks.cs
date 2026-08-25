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
            CheckOpticalRoleIsNotTheTaxonomy(check);
            CheckDappleAsksTheOpticalQuestion(check);
            CheckTransmissionNeedsABeam(check);
            CheckTransmissionPeaksWhenBacklit(check);
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

            check("the pigment model keeps fruit on its own colour",
                  _pbr.Contains("vvFoliageTransmissionPigment") &&
                  Regex.IsMatch(_pbr, @"fruit\s*=\s*vvFloraClass\(\) == VV_FLORA_FRUIT \? 1\.0 : 0\.0") &&
                  _pbr.Contains("pigment = mix(pigment, chroma, fruit)"),
                  "fruit must not inherit the leaf chlorophyll prior");

            check("fruit has no tip-to-root gradient",
                  Regex.IsMatch(_pbr, @"if \(!\(flora == VV_FLORA_HERB[\s\S]*?return 0\.5;"),
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
        /// The optical role must not be the taxonomy wearing a different name.
        ///
        /// vvFloraClass answers "what kind of plant is this". vvIsCanopyReceiver
        /// answers "can the sunlight arriving here have come through leaves".
        /// Those are different questions, and the first standing in for the
        /// second is a specific bug rather than an untidiness:
        ///
        ///   A pear hanging under a tree is not understory - it is fruit,
        ///   ecologically part of the canopy plant. It is physically BELOW
        ///   leaves, so it should be shaded like everything else under that
        ///   tree. While the dapple gate asked the ecological question it was
        ///   the one lit object in a shaded wood.
        ///
        /// So this pins the two apart. If the receiver set ever collapses back
        /// onto the understory set, that pear is lit again.
        /// </summary>
        private static void CheckOpticalRoleIsNotTheTaxonomy(Action<string, bool, string> check)
        {
            Match receiver = Regex.Match(_pbr,
                @"bool vvIsCanopyReceiver\(\)\s*\{(.*?)
\}", RegexOptions.Singleline);
            Match understory = Regex.Match(_pbr,
                @"bool vvIsUnderstory\(\)\s*\{(.*?)
\}", RegexOptions.Singleline);

            check("an optical role exists separately from the taxonomy",
                  receiver.Success, "vvIsCanopyReceiver not found");
            check("the ecological classification still exists too",
                  understory.Success, "vvIsUnderstory not found - the separation needs both sides");

            if (!receiver.Success || !understory.Success) return;

            string r = receiver.Groups[1].Value;
            string u = understory.Groups[1].Value;

            check("the optical role is not the ecological one under another name",
                  r.Trim() != u.Trim(),
                  "vvIsCanopyReceiver and vvIsUnderstory have identical bodies");

            // The receiver set is a SHORT EXCLUSION LIST, not a membership list.
            // Almost everything can be under leaves - stone, soil, trunks and
            // every plant that is not the canopy - so enumerating what receives
            // is how classes get forgotten. Fruit was the one that was.
            check("the optical role excludes rather than enumerates",
                  r.Contains("return true;") && !r.Contains("flora == VV_FLORA_GRASS"),
                  "listing what receives is how a class gets left out; list what does not");

            check("the canopy is excluded from receiving, because it is the occluder",
                  r.Contains("VV_FLORA_LEAVES") && r.Contains("return false"),
                  "a leaf cannot be in its own shadow");

            check("aquatic flora is excluded, because water owns that attenuation",
                  r.Contains("VV_FLORA_AQUATIC"),
                  "vanilla already attenuates through water; a second term double-counts");

            // The regression this whole correction exists for.
            check("fruit is NOT excluded from receiving canopy light",
                  !r.Contains("VV_FLORA_FRUIT"),
                  "a pear under a tree is in that tree's shade whatever its botany says");

            check("vines are NOT excluded from receiving canopy light",
                  !r.Contains("VV_FLORA_VINE"),
                  "whether a strand is in a sunbeam is what the shadow map already knows");
        }

        /// <summary>
        /// The dapple gate must ask the optical question.
        ///
        /// It used to reject every plant, which left a forest floor's grass
        /// evenly lit beside dappled soil. Then it asked "is this understory",
        /// which fixed the grass and left fruit and vines lit. It now asks
        /// whether sunlight arriving here could have come through leaves, which
        /// is the question it was always trying to ask.
        ///
        /// HOW MUCH the light was filtered is not decided here at all - that is
        /// measured per fragment from vanilla's shadow map. This only decides
        /// whether the question applies.
        /// </summary>
        private static void CheckDappleAsksTheOpticalQuestion(Action<string, bool, string> check)
        {
            Match dapple = Regex.Match(_pbr,
                @"float vvCanopyDapple\([^)]*\)\s*\{(.*?)
\}", RegexOptions.Singleline);

            check("the dapple function exists", dapple.Success, "");
            if (!dapple.Success) return;

            string body = dapple.Groups[1].Value;

            check("dapple gates on the optical role",
                  body.Contains("if (!vvIsCanopyReceiver()) return 0.0;"),
                  "the gate must ask whether light could have come through leaves");

            check("dapple no longer gates on the ecological classification",
                  !body.Contains("vvIsUnderstory()"),
                  "an ecological list cannot answer an optical question");

            check("the canopy is still never dappled",
                  body.Contains("vvIsCanopyReceiver"),
                  "the receiver test is what keeps a tree from wearing its own shade");

            // The amount has to stay a measurement. A class-based strength would
            // be the taxonomy deciding the optical result again, one level down.
            check("the amount of shade is still measured, not classified",
                  body.Contains("vvCanopyEvidence()"),
                  "how much light was filtered comes from the shadow map, per fragment");

            check("the measurement is continuous rather than banded",
                  !Regex.IsMatch(body, @"(?<!smooth)step\s*\(") ,
                  "a hard step here would read as 'dapple mode activated'");
        }

        /// <summary>
        /// Transmission needs a directional beam, and must collapse without one.
        ///
        /// A clear sky is a small very bright source: light arrives at a leaf
        /// from one direction, passes through, and comes out the other side
        /// pointed at the camera. An overcast sky replaces it with a source the
        /// size of the sky - light enters from everywhere at once and there is
        /// no direction to glow ALONG.
        ///
        /// The direct specular lobe already models exactly this, so the two use
        /// the SAME constant. That is the point of the check: two places
        /// modelling one physical fact with two constants is how they drift, and
        /// the symptom would be a leaf that glows on an overcast day while the
        /// highlight beside it has correctly gone flat.
        ///
        /// It is also what makes a low clear sun read as special. An effect that
        /// is always on cannot mean anything by being on.
        /// </summary>
        private static void CheckTransmissionNeedsABeam(Action<string, bool, string> check)
        {
            Match transmission = Regex.Match(_pbr,
                @"vec3 vvFoliageTransmission\([^)]*\)\s*\{(.*?)
\}", RegexOptions.Singleline);

            check("the transmission function exists", transmission.Success, "");
            if (!transmission.Success) return;

            string body = transmission.Groups[1].Value;

            check("transmission collapses under overcast",
                  body.Contains("vv_sceneOvercast"),
                  "a backlit leaf under a flat grey sky has no beam to glow along");

            check("transmission uses the same overcast constant as the direct lobe",
                  body.Contains("VV_OVERCAST_DIRECT"),
                  "one physical fact, two constants, is how the two drift apart");

            check("transmission still stops in shadow",
                  body.Contains("solarVisibility"),
                  "a leaf with no sun behind it has nothing to transmit");

            check("transmission still stops at night",
                  body.Contains("vvSunPresence()"),
                  "there is no sun to come through after dark");

            // The term that made a sunset forest look like a midday one.
            // Transmission peaks when the sun is low and behind the leaves;
            // scaling by "0 midnight, 1 noon" suppressed it at that exact
            // moment. A gate, not a dimmer.
            check("transmission is gated by the sun, not dimmed by it",
                  !Regex.IsMatch(body, @"\*\s*clamp\(vv_sceneDayLight"),
                  "backlighting peaks at a low sun and daylight scaling removes it there");

            check("transmission has explicit weather optics",
                  body.Contains("wetness") && body.Contains("snow") && body.Contains("frost") &&
                  _pbr.Contains("vvFoliageOpticalWeather"),
                  "wet/snow/frost may not reach transmission accidentally through albedo ordering");
        }

        /// <summary>
        /// Transmission must PEAK when the leaf is backlit.
        ///
        /// This is the entire definition of the effect, and it shipped
        /// inverted. The bent ray was built from -l instead of l, which made
        /// the response reduce to dot(v, l): minus one exactly when the sun is
        /// behind the leaf - clamped away to nothing - and plus one when the sun
        /// is behind the CAMERA, where a leaf is front-lit and has nothing to
        /// transmit.
        ///
        /// So the effect fired where it is meaningless and switched off where it
        /// is the whole point, and a sunset forest looked flat.
        ///
        /// Nothing static caught it. It is a sign, in a formula that is
        /// dimensionally fine either way, and every check asked whether the
        /// effect existed rather than which direction it pointed. It was found
        /// by photographing one debug view twice from one spot.
        ///
        /// So this drives the actual arithmetic rather than reading the source:
        /// the ORDERING backlit &gt; side-lit &gt; front-lit is the invariant,
        /// and no retuning of distortion or power may reverse it.
        /// </summary>
        private static void CheckTransmissionPeaksWhenBacklit(Action<string, bool, string> check)
        {
            // A leaf facing the camera. l points to the sun, v to the viewer.
            var n = new[] { 0f, 0f, 1f };
            var v = new[] { 0f, 0f, 1f };

            float backlit = Translucency(n, new[] { 0f, 0f, -1f }, v);   // sun behind the leaf
            float side    = Translucency(n, new[] { 1f, 0f, 0f },  v);   // sun to one side
            float front   = Translucency(n, new[] { 0f, 0f, 1f },  v);   // sun behind the camera

            check("a backlit leaf transmits",
                  backlit > 0.01f,
                  "backlit reads " + backlit.ToString("0.####") + " - the effect exists for this case alone");

            check("backlighting beats side lighting",
                  backlit > side,
                  "backlit " + backlit.ToString("0.####") + " vs side " + side.ToString("0.####"));

            check("side lighting beats front lighting",
                  side >= front,
                  "side " + side.ToString("0.####") + " vs front " + front.ToString("0.####"));

            check("a front-lit leaf transmits nothing",
                  front < 0.01f,
                  "front reads " + front.ToString("0.####") + " - there is nothing behind it to come through");

            // The specific inversion, named, so reintroducing it fails loudly
            // rather than as a subtle ordering change.
            check("the bent ray is built from the light direction, not its negation",
                  _pbr.Contains("normalize(l + n * distortion)"),
                  "building it from -l inverts the effect");
        }

        /// <summary>
        /// vvTranslucency, transcribed. Deliberately a transcription rather than
        /// a call: the point is to check what the GLSL computes, so it has to be
        /// written out independently and compared against the shader's source by
        /// the last check above.
        /// </summary>
        private static float Translucency(float[] n, float[] l, float[] v)
        {
            const float distortion = 0.35f;
            const float power = 3f;

            var through = new float[3];
            for (int i = 0; i < 3; i++) through[i] = l[i] + n[i] * distortion;

            float len = (float)Math.Sqrt(through[0] * through[0] +
                                         through[1] * through[1] +
                                         through[2] * through[2]);
            if (len < 1e-6f) return 0f;

            float dot = 0f;
            for (int i = 0; i < 3; i++) dot += v[i] * (-through[i] / len);

            return (float)Math.Pow(Math.Max(0f, dot), power);
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
                  _pbr.Contains("if (vvIsCanopy())") &&
                  _pbr.Contains("vvLeafBacklightSource(") &&
                  _pbr.Contains("return strength * VV_SHAFT_LEAF * leafSource;"),
                  "the canopy must be solar-visible and backlit before it feeds shafts");

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
