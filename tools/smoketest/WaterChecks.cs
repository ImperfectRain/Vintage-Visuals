using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static VintageVisuals.SmokeTest.GlslEval;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// The water surface (water.glsl / water.yaml) and the quality tier.
    ///
    /// Parity: water.glsl carries copies of the terrain's ripple and sky-cell
    /// functions, because each patch group must roll back alone. A copy that
    /// drifts is a second implementation nobody decided to write, so the
    /// bodies are compared token for token after the prefix rename.
    ///
    /// Composition: the surface splits light by Fresnel into a reflected share
    /// and the water body. The compose lines are evaluated straight from the
    /// shipped GLSL, so a retune cannot quietly make water MORE transparent by
    /// adding a reflection, or stop zero from meaning vanilla.
    /// </summary>
    public static class WaterChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            string dir = Path.Combine(repo, "assets/vintagevisuals/shadersnippets");
            string water = File.ReadAllText(Path.Combine(dir, "water.glsl"));
            string terrain = File.ReadAllText(Path.Combine(dir, "pbrterrainmaterial.glsl"));
            string yaml = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/water.yaml"));

            Parity(check, water, terrain);
            Periodic(check, water);
            Compose(check, water);
            Gates(check, water, yaml, repo);
            Tiers(check, repo);
            PerDropRipples(repo, check);
            EyeFromView(check);
        }

        /// <summary>
        /// The eye recovered from a view matrix built the way a camera builds
        /// one: rotate by yaw and pitch, translate by -R * eye. Column-major,
        /// as Vintage Story stores CameraMatrixOriginf.
        /// </summary>
        static void EyeFromView(Action<string, bool, string> check)
        {
            double worst = 0;
            foreach (var (yaw, pitch, ex, ey, ez) in new[] { (0.0, 0.0, 0.0, 1.62, 0.0), (0.7, -0.4, 0.0, 1.62, 0.0),
                                                              (2.5, 0.9, 1.5, 2.3, -3.0), (-1.2, -1.3, -4.0, 1.0, 2.0) })
            {
                double cy = Math.Cos(yaw), sy = Math.Sin(yaw), cp = Math.Cos(pitch), sp = Math.Sin(pitch);
                // R = Rx(pitch) * Ry(yaw), rows
                double[,] r = { { cy, 0, -sy }, { sy * sp, cp, cy * sp }, { sy * cp, -sp, cy * cp } };
                var m = new float[16];
                for (int row = 0; row < 3; row++)
                {
                    for (int col = 0; col < 3; col++) m[col * 4 + row] = (float)r[row, col];
                    m[12 + row] = (float)-(r[row, 0] * ex + r[row, 1] * ey + r[row, 2] * ez);
                }
                m[15] = 1f;
                var eye = new Vintagestory.API.MathTools.Vec3f();
                VintageVisuals.Water.WaterShaderBinder.EyeFromView(m, eye);
                worst = Math.Max(worst, Math.Abs(eye.X - ex) + Math.Abs(eye.Y - ey) + Math.Abs(eye.Z - ez));
            }
            var none = new Vintagestory.API.MathTools.Vec3f(9, 9, 9);
            VintageVisuals.Water.WaterShaderBinder.EyeFromView(null, none);
            check("the water eye is recovered from the view matrix, and no matrix means the old origin",
                  worst < 1e-4 && none.X == 0 && none.Y == 0 && none.Z == 0, "worst " + worst);
        }

        static string Normalise(string body, params (string From, string To)[] renames)
        {
            string s = Regex.Replace(body, @"//[^\n]*", "");
            foreach (var r in renames) s = s.Replace(r.From, r.To);
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        static void Parity(Action<string, bool, string> check, string water, string terrain)
        {
            var pairs = new[]
            {
                ("uint vvWaterHashU(", "uint vvTerrainHashU("),
                ("vec4 vvWaterCellRandom(", "vec4 vvTerrainCellRandom("),
                ("vec2 vvWaterRippleSlope(", "vec2 vvTerrainRippleSlope("),
                ("vec3 vvWaterReflectCell(", "vec3 vvTerrainReflectCell("),
                ("vec3 vvWaterReflectionFallback(", "vec3 vvTerrainReflectionFallback("),
            };
            var renames = new[]
            {
                ("vvTerrain", "vvWater"),
                ("VV_TERRAIN_RIPPLE_", "VV_WATER_RIPPLE_"),
                ("VV_TREFLECT_TWO_PI", "VV_WATER_TWO_PI"),
                ("VV_TREFLECT_HORIZON_LIFT", "VV_WATER_HORIZON_LIFT"),
                ("VV_TREFLECT_HORIZON", "VV_WATER_HORIZON"),
                ("VV_TREFLECT_GROUND", "VV_WATER_GROUND"),
                ("VV_TREFLECT_TOWARD", "VV_WATER_TOWARD"),
                ("VV_TREFLECT_MAX", "VV_WATER_MAX"),
                // The ONE intended difference: terrain reads the sun azimuth
                // from vanilla's lightPosition, water from its own uniform
                // (lightPosition is not reliably uploaded to chunkliquid).
                ("lightPosition.xz", "vv_waterSunDir.xz"),
            };
            foreach (var (w, t) in pairs)
            {
                string a = Normalise(FunctionBody(water, w));
                string b = Normalise(FunctionBody(terrain, t), renames);
                check("water copy of " + t.TrimEnd('(').Split(' ').Last() + " matches terrain", a == b,
                      a == b ? "" : "bodies differ after the prefix rename");
            }

            var consts = new[]
            {
                ("VV_WATER_RIPPLE_DEPTH", "VV_TERRAIN_RIPPLE_DEPTH"),
                ("VV_WATER_RIPPLE_BAND", "VV_TERRAIN_RIPPLE_BAND"),
                ("VV_WATER_RIPPLE_WAVE", "VV_TERRAIN_RIPPLE_WAVE"),
                ("VV_WATER_RIPPLE_JITTER", "VV_TERRAIN_RIPPLE_JITTER"),
                ("VV_WATER_RIPPLE_TEXELS", "VV_TERRAIN_RIPPLE_TEXELS"),
                ("VV_WATER_REFLECT_CELLS", "VV_TREFLECT_CELLS_SHARP"),
                ("VV_WATER_HORIZON", "VV_TREFLECT_HORIZON"),
                ("VV_WATER_GROUND", "VV_TREFLECT_GROUND"),
                ("VV_WATER_HORIZON_LIFT", "VV_TREFLECT_HORIZON_LIFT"),
                ("VV_WATER_TOWARD", "VV_TREFLECT_TOWARD"),
                ("VV_WATER_MAX", "VV_TREFLECT_MAX"),
            };
            var drift = consts.Where(c => Math.Abs(Const(water, c.Item1) - Const(terrain, c.Item2)) > 1e-9)
                              .Select(c => c.Item1 + " vs " + c.Item2).ToList();
            check("water ripple and sky-cell constants match terrain", drift.Count == 0, string.Join(", ", drift));
        }

        // Every pattern must tile the CPU's 4096-block origin wrap, or the
        // wrap shows as a seam (CLAUDE.md, float precision).
        static void Periodic(Action<string, bool, string> check, string water)
        {
            string waves = FunctionBody(water, "vec2 vvWaterWaveSlope(");
            var ks = Regex.Matches(waves, @"vec2\(\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*\)\s*\*\s*K")
                          .Select(m => (double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                                        double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)))
                          .ToList();
            bool whole = ks.Count >= 3 && ks.All(k => k.Item1 == Math.Floor(k.Item1) && k.Item2 == Math.Floor(k.Item2));
            check("water waves have whole cycles per 4096 blocks", whole && waves.Contains("4096.0"),
                  ks.Count + " wave vectors: " + string.Join(" ", ks));

            string chop = FunctionBody(water, "vec2 vvWaterChop(");
            var octaves = Regex.Matches(chop, @"world\s*\*\s*([\d.]+)[^;]*?,\s*([\d.]+)\)")
                               .Select(m => (double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                                             double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)))
                               .ToList();
            bool tiled = octaves.Count == 2 && octaves.All(o => Math.Abs(o.Item1 * 4096.0 - o.Item2) < 1e-6);
            check("water chop cells wrap with the 4096-block period", tiled,
                  string.Join(" ", octaves.Select(o => "scale " + o.Item1 + " period " + o.Item2)));

            double texels = Const(water, "VV_WATER_TEXELS");
            check("water texel grid divides the origin period", 4096.0 * texels == Math.Floor(4096.0 * texels) && texels >= 16,
                  "VV_WATER_TEXELS " + texels);
        }

        static void Compose(Action<string, bool, string> check, string water)
        {
            string body = FunctionBody(water, "vec4 vvWaterSurface(");
            string alphaExpr = Rhs(Statement(body, "float outAlpha =", "reflectShare"));
            string premultExpr = Rhs(Statement(body, "vec3 premult ="));
            string glintAlphaExpr = Rhs(Statement(body, "outAlpha = clamp(outAlpha + min(glintAlpha"));
            double glintAlphaCap = Const(water, "VV_WATER_GLINT_ALPHA");

            double Alpha(double r, double a)
            {
                return Scalar(alphaExpr, Syms("reflectShare", r, "bodyAlpha", a));
            }

            double Out(double r, double image, double bodyC, double a, double glint, out double alpha)
            {
                alpha = Alpha(r, a);
                double pre = Scalar(premultExpr.Replace("image", "imageS").Replace("body ", "bodyS ").Replace("glint", "glintS"),
                                    Syms("reflectShare", r, "imageS", image, "bodyS", bodyC, "bodyAlpha", a, "glintS", glint));
                double ga = Math.Min(1, Math.Max(0, glint));
                alpha = Scalar(glintAlphaExpr, Syms("outAlpha", alpha, "glintAlpha", ga,
                                                    "VV_WATER_GLINT_ALPHA", glintAlphaCap));
                return pre / Math.Max(alpha, 1e-3);
            }

            double worstIdentity = 0, worstAlphaDrop = 0, worstHull = 0;
            for (int ai = 1; ai <= 10; ai++)
            {
                double a = ai / 10.0;
                for (int ci = 0; ci <= 10; ci++)
                {
                    double c = ci / 10.0;
                    double o = Out(0, 0.7, c, a, 0, out double alpha0);
                    worstIdentity = Math.Max(worstIdentity, Math.Max(Math.Abs(o - c), Math.Abs(alpha0 - a)));
                    for (int ri = 0; ri <= 10; ri++)
                    {
                        double r = ri / 10.0;
                        double img = 1.0 - c;
                        double v = Out(r, img, c, a, 0, out double alpha);
                        worstAlphaDrop = Math.Max(worstAlphaDrop, a - alpha);
                        double lo = Math.Min(img, c), hi = Math.Max(img, c);
                        worstHull = Math.Max(worstHull, Math.Max(lo - v, v - hi));
                    }
                }
            }
            check("water with no reflection and no glint composes to vanilla's colour", worstIdentity < 1e-9,
                  "worst " + worstIdentity);
            check("water reflection never makes the water more transparent", worstAlphaDrop < 1e-9,
                  "worst alpha drop " + worstAlphaDrop);
            check("water colour stays between the reflection and the body", worstHull < 1e-9,
                  "worst " + worstHull);

            // The ring crest raises the reflected share only: no ring is the
            // Fresnel share exactly, a ring never lowers it, and it does not
            // read the reflection slider (two toggles, two effects).
            string ringExpr = Rhs(Statement(body, "reflectShare = max(reflectShare,"));
            double ringShare = Const(water, "VV_WATER_RING_SHARE");
            double ringWorst = 0;
            bool ringRaises = !ringExpr.Contains("vv_waterSky");
            if (!ringRaises) ringExpr = "reflectShare";
            for (int fi = 0; fi <= 10; fi++)
            {
                double f = fi / 10.0;
                double atZero = Scalar(ringExpr, Syms("reflectShare", f, "ring", 0.0, "VV_WATER_RING_SHARE", ringShare));
                ringWorst = Math.Max(ringWorst, Math.Abs(atZero - f));
                for (int ri = 1; ri <= 10; ri++)
                    ringRaises &= Scalar(ringExpr, Syms("reflectShare", f, "ring", ri / 10.0, "VV_WATER_RING_SHARE", ringShare)) >= f - 1e-12;
            }
            check("water without a rain ring keeps the Fresnel share exactly", ringWorst < 1e-12, "worst " + ringWorst);
            check("a rain ring never lowers the reflected share, and ignores the reflection slider",
                  ringRaises && ringShare > 0 && !ringExpr.Contains("vv_waterSky"), ringExpr);

            // The eye, not the player's feet: fWorldPos has the player at the
            // origin, and from the feet water at standing height is edge-on.
            check("the water view vector starts at the eye",
                  Statement(body, "vec3 v =").Contains("vv_waterEye - centre") &&
                  Statement(body, "vec3 vFrag =").Contains("vv_waterEye - pos"), "");

            // Near-horizontal reflections meet the banks, and the sky image is
            // never brighter than the sky.
            string sky = FunctionBody(water, "vec3 vvWaterSkyImage(");
            check("water reflections near the horizon give way to the banks",
                  sky.Contains("VV_WATER_BANK_LOW") && sky.Contains("VV_WATER_BANK_HIGH") &&
                  Const(water, "VV_WATER_BANK_SHADE") < 1.0, "");
            string capIf = Statements(sky).FirstOrDefault(x => x.Contains("sky *= cap / skyLuma")) ?? "";
            check("the reflected sky is capped at the sky's own brightness",
                  capIf.StartsWith("if (skyLuma > cap && skyLuma > 1e-5) sky *= cap / skyLuma", StringComparison.Ordinal), capIf);
        }

        /// <summary>
        /// Rain drops are hashed per DROP, not per cell: the drop count runs on
        /// across clock wraps (the epoch), so a drop never lands on the same
        /// spot twice. All three copies, since terrain shares the field.
        /// </summary>
        public static void PerDropRipples(string repo, Action<string, bool, string> check)
        {
            string dir = Path.Combine(repo, "assets/vintagevisuals/shadersnippets");
            foreach (var (file, fn) in new[] { ("water.glsl", "vec2 vvWaterRippleSlope("),
                                               ("pbrterrainmaterial.glsl", "vec2 vvTerrainRippleSlope("),
                                               ("pseudopbr.glsl", "vec2 vvRippleSlope(") })
            {
                string b = FunctionBody(File.ReadAllText(Path.Combine(dir, file)), fn);
                string drop = Statement(b, "float drop =");
                bool perDrop = drop.Contains("floor(phase)") && drop.Contains("epoch * rate")
                               && Statement(b, "if (hit.x > density)").Length > 0
                               && Statement(b, "vec2 centre =").Contains("hit.yz")
                               && !b.Contains("rnd.x > density");
                check("rain drops land somewhere new each time (" + file + ")", perDrop, drop);
            }
        }

        static void Gates(Action<string, bool, string> check, string water, string yaml, string repo)
        {
            string body = FunctionBody(water, "vec4 vvWaterSurface(");
            var stmts = Statements(body);
            int gate = stmts.FindIndex(s => s.StartsWith("if (vv_water < 0.5) return color", StringComparison.Ordinal));
            int firstBranch = stmts.FindIndex(s => s.StartsWith("if", StringComparison.Ordinal));
            check("water master off returns vanilla's colour before anything else branches",
                  gate >= 0 && gate == firstBranch, "gate statement index " + gate);
            check("water derivatives are taken before the first branch",
                  stmts.Take(Math.Max(0, firstBranch)).Count(s => s.Contains("dFdx")) >= 2, "");

            string keep = FunctionBody(water, "float vvWaterVanillaSpecularKeep(");
            var ks = Statements(keep);
            check("vanilla's water specular is untouched while the group is not live",
                  ks.Count > 0 && ks[0].StartsWith("if (vv_water < 0.5) return 1.0", StringComparison.Ordinal), "");
            double atZero = Scalar(Rhs("x = " + ks.Last().Substring("return ".Length)),
                                   Syms("vv_waterGlints", 0.0, "VV_WATER_SPEC_KEEP", Const(water, "VV_WATER_SPEC_KEEP")));
            check("vanilla's water specular is untouched at glints 0", Math.Abs(atZero - 1.0) < 1e-9, "keep " + atZero);

            check("the water surface is never applied to lava", yaml.Contains("if (!isLava) texColor = vvWaterSurface("), "");
            // Asserting vanilla's depthTex is fine; declaring one of our own is
            // not - a sampler above vanilla's shifts every unit below it.
            bool declares = Regex.IsMatch(water, @"^\s*uniform\s+sampler", RegexOptions.Multiline) ||
                            yaml.Split('\n').Any(l => l.Contains("uniform sampler") && !l.TrimStart().StartsWith("tokens:", StringComparison.Ordinal));
            check("the water group declares no sampler of its own", !declares, "");

            string gating = File.ReadAllText(Path.Combine(repo, "src/VintageVisualsModSystem.cs"));
            check("the water group is gated on Water.Enabled",
                  Regex.IsMatch(gating, @"group == WaterSubsystem\.GroupName\)\s*\{\s*return ConfigManager\.Config\.Water\.Enabled;"),
                  "");
            check("toggling water rebuilds shaders", gating.Contains("ConfigManager.Config.Water.Enabled ? \"L\""), "");

            string binder = File.ReadAllText(Path.Combine(repo, "src/Water/WaterShaderBinder.cs"));
            check("water reads the scene block through the one shared upload path",
                  binder.Contains(".UploadSceneTo(program)") && !binder.Contains("\"vv_scene"), "");
        }

        // High is the reference: every scale exactly 1 there.
        static void Tiers(Action<string, bool, string> check, string repo)
        {
            string src = File.ReadAllText(Path.Combine(repo, "src/Common/Diagnostics/QualityTier.cs"));
            var picks = Regex.Matches(src, @"public static float (\w+)\s*\{\s*get\s*\{\s*return Pick\(([^)]*)\);")
                             .Cast<Match>()
                             .Select(m => (m.Groups[1].Value, m.Groups[2].Value.Split(',').Select(x => x.Trim().TrimEnd('f')).ToArray()))
                             .ToList();
            var scales = picks.Where(p => p.Item1.EndsWith("Scale", StringComparison.Ordinal)).ToList();
            var bad = scales.Where(p => p.Item2.Length != 4 || p.Item2[2] != "1").Select(p => p.Item1).ToList();
            check("every quality scale is exactly 1 at High", scales.Count >= 2 && bad.Count == 0,
                  string.Join(", ", bad) + " (" + scales.Count + " scales)");

            string binder = File.ReadAllText(Path.Combine(repo, "src/PseudoPBR/PbrShaderBinder.cs"));
            check("the terrain binder applies the tier scales",
                  binder.Contains("QualityTier.DetailDistanceScale") && binder.Contains("QualityTier.CanopyRadiusScale"), "");
        }
    }
}
