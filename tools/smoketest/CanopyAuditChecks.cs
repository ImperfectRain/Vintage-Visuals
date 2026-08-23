using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// The canopy audit instrument: vanilla's own sun occlusion, and the
    /// measurement that may replace vv_sunExposure as the canopy signal.
    ///
    /// None of this shades anything yet, and that is deliberate. The dapple
    /// system's problem was never its pattern - it was that its GATE asked the
    /// wrong question. vv_sunExposure is a flood-filled sky light value: it is
    /// a LIGHTING RESULT, identical under a canopy and under a roof, and
    /// identical at noon and at midnight because it knows nothing about where
    /// the sun is. Building a directional effect on it means a forest floor and
    /// a cellar ceiling are indistinguishable to the shader.
    ///
    /// Vanilla renders a real directional sun shadow map, and foliage is in it
    /// - chunkshadowmap.fsh alpha-discards below 0.02, so leaf cutouts punch
    /// real holes. What is checked here is that the mod reads that data as
    /// OCCLUSION rather than as brightness, and that the measurement derived
    /// from it is bounded and behaves at both uniform extremes.
    /// </summary>
    public static class CanopyAuditChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            string pbr = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pseudopbr.glsl"));

            CheckSunVisibilityIsOcclusionNotBrightness(pbr, check);
            CheckDegradesWithoutShadows(pbr, check);
            CheckBreakupMeasure(pbr, check);
            CheckDebugViewsAreReachable(repo, pbr, check);
            CheckDappleTouchesSunlightOnly(pbr, check);
            CheckGateIsGeometric(pbr, check);
            CheckStructureCountsOccluders(pbr, check);
            CheckNoInventedPattern(pbr, check);
        }

        /// <summary>
        /// Neither dapple nor the shafts may generate a pattern of their own.
        ///
        /// Vanilla's shadow map resolves individual leaf gaps and renders them
        /// onto the forest floor every frame, with the real canopy's shape and
        /// the real canopy's wind. A procedural fleck field is therefore a
        /// SECOND description of the same leaves, and the two cannot agree - the
        /// invented one has no access to where the branches actually are.
        ///
        /// What is left for the mod is the part vanilla does not do: deepen the
        /// shade between the gaps so the gaps read as bright by contrast, and
        /// tint that shade green because the light reaching it came through
        /// leaves. Darken-only is not a compromise in that model, it is the
        /// only operator with anywhere to go - a lit gap is already at full sun
        /// and has no headroom above it.
        /// </summary>
        private static void CheckNoInventedPattern(string source, Action<string, bool, string> check)
        {
            // Comments discuss what was removed and why, at length. A check for
            // "is this gone" has to read CODE or it fails on its own
            // explanation - which is exactly what it did.
            string pbr = StripComments(source);

            check("the procedural fleck generator is gone",
                !pbr.Contains("float vvSunflecks("),
                "a disabled generator beside the real one invites being switched back on");

            foreach (var dead in new[]
            {
                "VV_DAPPLE_SCALE", "VV_DAPPLE_DENSITY", "VV_DAPPLE_RADIUS",
                "VV_DAPPLE_JITTER", "VV_DAPPLE_PENUMBRA", "VV_DAPPLE_COVER",
                "VV_DAPPLE_BLINK_LOW", "VV_DAPPLE_BLINK_HIGH", "VV_DAPPLE_MAX_THROW",
            })
            {
                check("the tuning constant " + dead + " is gone with it",
                    !pbr.Contains(dead),
                    "left behind, it reads as load-bearing");
            }

            // The two halves of the model are complements of one measurement:
            // broken and shadowed is canopy shade, broken and lit is a sunfleck.
            Match fleck = Regex.Match(pbr, @"float vvCanopySunfleck\(\)\s*\{(.*?)\n\}",
                                      RegexOptions.Singleline);
            check("a sunfleck is defined as lit ground under broken canopy",
                fleck.Success
                    && fleck.Groups[1].Value.Contains("vvSunVisibility()")
                    && fleck.Groups[1].Value.Contains("vvCanopyStructure("),
                "");

            Match ev = Regex.Match(pbr, @"float vvCanopyEvidence\(\)\s*\{(.*?)\n\}",
                                   RegexOptions.Singleline);
            check("canopy shade is its complement, not a second measurement",
                ev.Success && ev.Groups[1].Value.Contains("1.0 - vvSunVisibility()"),
                "");

            // The shafts had the same flat gate the dapple did.
            Match shaft = Regex.Match(pbr, @"float vvCanopyShaft\(vec3 cameraRelativePos\)\s*\{(.*?)\n\}",
                                      RegexOptions.Singleline);
            check("the shaft mask no longer reads sun exposure",
                shaft.Success && !shaft.Groups[1].Value.Contains("vv_sunExposure"),
                "measured flat at ~1 across a whole forest scene");

            check("the shafts start at real sunflecks",
                shaft.Success && shaft.Groups[1].Value.Contains("vvCanopySunfleck()"),
                "a beam has to start where the sun actually got through");
        }

        /// <summary>
        /// The total-variation count, ported and checked against the shapes it
        /// has to tell apart.
        ///
        /// Walking a ring of taps in angular order and summing the absolute
        /// differences gives 2 per feature crossed: 0 for a uniform
        /// neighbourhood, 2 for one straight edge however it is placed, 2N for
        /// N separate gaps. The gate's threshold has to sit ABOVE 2, or every
        /// wall, terrace lip, cliff and roof in the world reads as canopy.
        /// </summary>
        private static void CheckStructureCountsOccluders(string pbr, Action<string, bool, string> check)
        {
            // The raw count and the threshold live in separate functions, so
            // that a debug view can show the count unthresholded - "there is no
            // structure here" and "the band is too high" look identical on
            // screen and need opposite fixes.
            Match raw = Regex.Match(pbr, @"float vvCanopyVariation\(float radiusTexels, out float mean\)\s*\{(.*?)\n\}",
                                    RegexOptions.Singleline);
            check("vvCanopyVariation exists", raw.Success, "");

            Match fn = Regex.Match(pbr, @"float vvCanopyStructure\(float radiusTexels\)\s*\{(.*?)\n\}",
                                   RegexOptions.Singleline);
            check("vvCanopyStructure exists", fn.Success, "");
            if (!fn.Success || !raw.Success) return;

            string body = fn.Groups[1].Value;

            check("the ring is closed",
                raw.Groups[1].Value.Contains("abs(first - previous)"),
                "an unclosed ring miscounts any feature at the seam, in a fixed screen direction");

            check("the raw count is exposed unthresholded",
                Regex.IsMatch(pbr, @"mode == 31") && pbr.Contains("vvCanopyVariation(clamp(vv_pbrCanopyRadius"),
                "without a raw view, a silent gate cannot be told from a wrong band");

            Match band = Regex.Match(body, @"smoothstep\(([\d.]+), ([\d.]+), variation\)");
            check("the count has a threshold band", band.Success, "");
            if (!band.Success) return;

            double low = double.Parse(band.Groups[1].Value, CultureInfo.InvariantCulture);
            double high = double.Parse(band.Groups[2].Value, CultureInfo.InvariantCulture);

            // Total variation around a closed ring for the shapes that matter.
            const double uniform = 0.0;
            const double oneEdge = 2.0;
            const double twoFeatures = 4.0;

            Func<double, double> gate = tv =>
            {
                double t = Math.Min(Math.Max((tv - low) / (high - low), 0.0), 1.0);
                return t * t * (3.0 - 2.0 * t);
            };

            check("a uniform neighbourhood is not canopy",
                gate(uniform) < 1e-9, "");

            check("a single straight edge is not canopy",
                gate(oneEdge) < 1e-9,
                "threshold band starts at " + low + ", which a wall reaches");

            check("several separate gaps do read as canopy",
                gate(twoFeatures) > 0.05,
                "band is so high that real canopy scores nothing");

            check("the band saturates within reach of a real canopy",
                high <= 8.0,
                "12 taps cap total variation at 12; a band ending near that is unreachable");
        }

        /// <summary>
        /// Dapple must be applied BEFORE every term that is not sunlight.
        ///
        /// It shipped at the very end of vvApplyPbr, multiplying the finished
        /// pixel - so it scaled block-light specular, the sky term, foliage
        /// transmission and emission. A canopy dimmed a torch and dimmed a
        /// glowing forge. Nothing caught it: the value was correct, the sign
        /// was correct, it was bounded, it conserved, and it was in the wrong
        /// place. Position in the function is the only thing that encodes
        /// "this is a statement about sunlight", so position is what gets
        /// pinned.
        ///
        /// The diffuse term used to be knowingly scaled too - vanilla hands it
        /// over with block light already mixed in - and that limitation stood
        /// because separating them looked impossible from inside this function.
        /// It is not: vanilla computes blockBrightness for its own use, which is
        /// the strongest of glow, point light and block light net of half the
        /// sun, and that is exactly the local share the canopy must not touch.
        /// The last three checks below pin the separation.
        /// </summary>
        private static void CheckDappleTouchesSunlightOnly(string pbr, Action<string, bool, string> check)
        {
            int dapple = pbr.IndexOf("float shaded = vvCanopyDapple(", StringComparison.Ordinal);
            check("the dapple application site is findable", dapple >= 0, "vvApplyPbr");
            if (dapple < 0) return;

            foreach (var term in new[]
            {
                ("torchlight highlights", "result += vvBlockLightSpecular(f0"),
                ("the sky specular term", "result += vvAmbientSpecular(f0"),
                ("light through leaves", "result += vvFoliageTransmission(albedo"),
                ("emission", "result += vvEmission(albedo"),
            })
            {
                int at = pbr.IndexOf(term.Item2, StringComparison.Ordinal);
                check("dapple does not dim " + term.Item1,
                    at > dapple,
                    at < 0 ? "term not found: " + term.Item2
                           : "dapple is applied after it, so it scales it");
            }

            // The diffuse half. A canopy blocks the SUN; it does not block a
            // torch hanging under it. vanilla hands over one colour with sun,
            // sky and block light already mixed, so the canopy term has to be
            // scaled by the complement of vanilla's own local-light measure
            // rather than applied to the whole thing.
            // The protected value must be DEFINED and USED. Checking only that
            // it is defined is the dead-check failure this suite has been
            // bitten by before: reverting the application while leaving the
            // definition in place passed a first draft of this check.
            check("the canopy term computes a local-light-sparing value",
                pbr.Contains("float local = vvLocalLightShare();") &&
                pbr.Contains("clamp(shaded, 0.0, 0.85) * (1.0 - local)"),
                "a torch under a tree is dimmed by the canopy without this");

            check("the canopy term actually applies the sparing value",
                pbr.Contains("result *= 1.0 - canopy;") &&
                !Regex.IsMatch(pbr, @"result \*= 1\.0 - clamp\(shaded, 0\.0, 0\.85\);"),
                "the raw shaded value is being applied, so the sparing is computed and discarded");

            check("the local share is vanilla's own measure, not an invention",
                Regex.IsMatch(pbr, @"float vvLocalLightShare\(\)[\s\S]{0,220}?blockBrightness"),
                "blockBrightness is the game's answer; a second one would disagree with it");

            // blockBrightness lives inside `#if SHADOWQUALITY > 0`. Referencing
            // it unguarded fails to compile for a player with shadows off - and
            // that player has no shadow map, so the canopy term is inert anyway.
            check("the local share degrades safely with shadows off",
                Regex.IsMatch(pbr, @"float vvLocalLightShare\(\)[\s\S]{0,260}?#else[\s\S]{0,60}?return 0\.0;"),
                "vanilla declares blockBrightness only under SHADOWQUALITY > 0");

            // The green tint is light that came THROUGH a leaf. Torchlight did
            // not, so it must ride the same protected fraction.
            check("the canopy's green tint spares local light too",
                pbr.Contains("float tint = canopy * VV_DAPPLE_GREEN;"),
                "torchlight did not pass through a leaf and did not pick up its colour");
        }

        /// <summary>
        /// The gate must be the geometric evidence, not the photometric one.
        ///
        /// Measured in game: debug view 16 on a birch forest floor at midday
        /// reads essentially 1 everywhere - open ground, under the crowns, on
        /// the terraces alike. vv_sunExposure carries almost no canopy
        /// information in a real scene, so a gate built on it passed nearly
        /// nothing under actual trees and passed its maximum wherever
        /// something else held sun light down. Meanwhile view 25 showed
        /// vanilla's shadow map resolving individual leaf shadows.
        /// </summary>
        private static void CheckGateIsGeometric(string pbr, Action<string, bool, string> check)
        {
            Match fn = Regex.Match(pbr, @"float vvCanopyDapple\(.*?\n\}", RegexOptions.Singleline);
            check("vvCanopyDapple is findable", fn.Success, "");
            if (!fn.Success) return;

            string body = fn.Value;

            check("the dapple gate reads the shadow map, not sun exposure",
                body.Contains("vvCanopyEvidence()"),
                "");

            check("vv_sunExposure no longer gates dapple",
                !body.Contains("vv_sunExposure"),
                "measured flat at ~1 across a whole forest scene");

            // COUNTING, not edge detection. 4p(1-p) is maximal where taps
            // disagree and zero where they agree, so it can only outline a
            // shadow - seen in game as "only an outline around already present
            // shadows". The gate must not go back to it.
            Match ev = Regex.Match(pbr, @"float vvCanopyEvidence\(\)\s*\{(.*?)\n\}", RegexOptions.Singleline);
            check("the canopy evidence counts occluders",
                ev.Success && Regex.IsMatch(ev.Groups[1].Value, @"vvCanopyStructure\("),
                "");

            check("the canopy evidence is not a bare edge detector",
                ev.Success && !ev.Groups[1].Value.Contains("vvSunShadowBreakup("),
                "4p(1-p) at a point can only trace a boundary, never fill a region");

            check("the canopy evidence requires the sun to be blocked",
                ev.Success && ev.Groups[1].Value.Contains("1.0 - vvSunVisibility()"),
                "a lit fragment has no shadow to break up");
        }

        /// <summary>
        /// vvSunVisibility must not be contaminated by block light.
        ///
        /// This is the whole reason it exists rather than reusing the value the
        /// mod already passes around as "shadow". That value is vanilla's
        /// getBrightnessFromShadowMap(), and its last line before returning is
        /// `b = clamp(b + blockBrightness, 0, 1)` - a torch RAISES it. It
        /// answers "how bright is this fragment", not "does the sun reach it",
        /// and next to any light source those diverge completely.
        ///
        /// Also pinned: no shadowIntensity. That uniform is a graphics setting
        /// describing how dark the game chooses to DRAW a shadow. A player who
        /// turned shadows down still has a tree over their head, and a
        /// geometric query has no business reading a quality slider.
        /// </summary>
        private static void CheckSunVisibilityIsOcclusionNotBrightness(
            string pbr, Action<string, bool, string> check)
        {
            Match fn = Regex.Match(pbr, @"float vvSunVisibility\(\)\s*\{(.*?)\n\}", RegexOptions.Singleline);
            check("vvSunVisibility exists", fn.Success, "pseudopbr.glsl");
            if (!fn.Success) return;

            string body = fn.Groups[1].Value;

            check("sun visibility is not raised by block light",
                !body.Contains("blockBrightness"),
                "blockBrightness makes it a brightness, not an occlusion");

            check("sun visibility does not read the shadow quality slider",
                !body.Contains("shadowIntensity"),
                "shadowIntensity is how dark shadows are DRAWN, not where the sun goes");

            check("sun visibility samples both shadow cascades",
                body.Contains("shadowMapFar") && body.Contains("shadowMapNear"),
                "");

            // The cascade weights are complementary by construction in
            // chunkopaque.vsh - the far one subtracts the near one - so the two
            // occlusions ADD. Averaging them instead would halve every shadow.
            check("the two cascades are summed, not averaged",
                Regex.Matches(body, @"occlusion \+=").Count == 2,
                "");
        }

        /// <summary>
        /// With shadows switched off there is no occlusion data at all, and the
        /// only honest answer is "fully lit".
        ///
        /// Anything built on this has to degrade to vanilla rather than invent
        /// a shadow the player explicitly turned off. Returning 0 instead would
        /// put the entire world in permanent shade at SHADOWQUALITY 0, which
        /// compiles, runs, and is catastrophic.
        /// </summary>
        private static void CheckDegradesWithoutShadows(string pbr, Action<string, bool, string> check)
        {
            Match fn = Regex.Match(pbr, @"float vvSunVisibility\(\)\s*\{(.*?)\n\}", RegexOptions.Singleline);
            if (!fn.Success) return;

            string body = fn.Groups[1].Value;

            Match guard = OuterElse(body);
            check("sun visibility has a shadows-off branch", guard.Success, "");

            check("with shadows off, the sun is treated as unoccluded",
                guard.Success && Regex.IsMatch(guard.Groups[1].Value, @"return\s+1\.0\s*;"),
                "must return 1.0, or the world goes dark at SHADOWQUALITY 0");

            Match breakup = Regex.Match(pbr, @"float vvSunShadowBreakup\(float radiusTexels\)\s*\{(.*?)\n\}",
                                        RegexOptions.Singleline);
            Match bguard = breakup.Success ? OuterElse(breakup.Groups[1].Value) : Match.Empty;

            check("with shadows off, nothing reads as broken",
                bguard.Success && Regex.IsMatch(bguard.Groups[1].Value, @"return\s+0\.0\s*;"),
                "no shadow map means no evidence of a canopy, which is 0 not 1");
        }

        /// <summary>
        /// The LAST #else in a function body, which is the outermost one.
        ///
        /// Not the first: vvSunShadowBreakup has an inner
        /// `#if SHADOWQUALITY > 1 / #else / #endif` picking the cascade, and a
        /// non-greedy match lands on that instead of the shadows-off fallback.
        /// The check failed on exactly this and was right to - the test was
        /// reading the wrong branch and would have passed a function with no
        /// fallback at all.
        /// </summary>
        /// <summary>
        /// Line comments removed, so a textual check reads code rather than the
        /// prose describing it. Block comments are not used in these snippets.
        /// </summary>
        private static string StripComments(string glsl)
        {
            return Regex.Replace(glsl, @"//[^\n]*", "");
        }

        private static Match OuterElse(string body)
        {
            Match last = Match.Empty;
            foreach (Match m in Regex.Matches(body, @"#else(.*?)#endif", RegexOptions.Singleline)) last = m;
            return last;
        }

        /// <summary>
        /// The breakup measure, ported and checked at its extremes.
        ///
        /// 4p(1-p) over the fraction of lit taps. The property that matters is
        /// that it is zero at BOTH ends: an open field (every tap lit) and a
        /// cave interior (every tap shadowed) must both read as "no canopy
        /// here". A measure that only vanished at one end would call every
        /// enclosed space a forest, which is the exact failure the current
        /// vv_sunExposure gate has at its lower edge.
        /// </summary>
        private static void CheckBreakupMeasure(string pbr, Action<string, bool, string> check)
        {
            check("the breakup measure is the pinned form",
                Regex.IsMatch(pbr, @"4\.0 \* mean \* \(1\.0 - mean\)"),
                "port below assumes 4p(1-p)");

            Func<double, double> breakup = p => 4.0 * p * (1.0 - p);

            check("a fully lit neighbourhood reads as unbroken",
                Math.Abs(breakup(1.0)) < 1e-9, "open field must be 0");

            check("a fully shadowed neighbourhood reads as unbroken",
                Math.Abs(breakup(0.0)) < 1e-9, "cave interior must be 0");

            bool bounded = true, peaks = true;
            for (int i = 0; i <= 1000; i++)
            {
                double v = breakup(i / 1000.0);
                if (v < -1e-9 || v > 1.0 + 1e-9) bounded = false;
            }
            peaks = Math.Abs(breakup(0.5) - 1.0) < 1e-9;

            check("the breakup measure stays within 0..1", bounded, "");
            check("it peaks where the taps are evenly split", peaks, "");

            // Nine taps, so the measure is quantised. Worth pinning because it
            // sets the finest distinction the mask could ever draw: with 9
            // binary samples the lowest non-zero breakup is 4*(1/9)*(8/9).
            double finest = breakup(1.0 / 9.0);
            check("nine taps give a usable smallest step",
                finest > 0.3 && finest < 0.4,
                "smallest non-zero breakup is " + finest.ToString("0.###", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Every debug view the shader implements must be reachable from the
        /// slider, and the slider must not promise views that do not exist.
        ///
        /// The existing range-versus-clamp check catches the slider and the
        /// config disagreeing with each other. It cannot catch both of them
        /// agreeing on a number the SHADER does not implement, or a view being
        /// added to the GLSL and left unreachable - which is how four views
        /// were once dead at the same time.
        /// </summary>
        private static void CheckDebugViewsAreReachable(string repo, string pbr,
                                                        Action<string, bool, string> check)
        {
            Match view = Regex.Match(pbr, @"vec4 vvDebugView\((.*)", RegexOptions.Singleline);
            check("vvDebugView exists", view.Success, "");
            if (!view.Success) return;

            var modes = new HashSet<int>(
                Regex.Matches(view.Value, @"mode == (\d+)")
                     .Cast<Match>()
                     .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));

            check("the shader implements debug views", modes.Count > 0, "");

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/config/configlib-patches.json")));

            JsonElement range = doc.RootElement
                .GetProperty("settings").GetProperty("float")
                .GetProperty("pbr_debugview").GetProperty("range");

            int max = (int)range.GetProperty("max").GetSingle();

            var unreachable = modes.Where(m => m > max).OrderBy(m => m).ToList();
            check("every implemented debug view is reachable from the slider",
                unreachable.Count == 0,
                unreachable.Count == 0 ? "" : "modes " + string.Join(",", unreachable) + " exceed the slider max " + max);

            check("the slider stops at the highest implemented view",
                modes.Max() == max,
                "slider max " + max + ", highest implemented " + modes.Max());

            // The audit views specifically. These are the ones the whole
            // investigation depends on being able to look at, and a silently
            // dropped one would mean measuring nothing and concluding anyway.
            foreach (int m in new[] { 25, 26, 27, 28 })
            {
                check("canopy audit view " + m + " is implemented", modes.Contains(m), "");
            }
        }
    }
}
