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

            string core = File.ReadAllText(Path.Combine(dir, "reflect.glsl"));
            Parity(check, water, terrain, core);
            Periodic(check, water);
            Compose(check, water);
            Gates(check, water, yaml, repo);
            Tiers(check, repo);
            PerDropRipples(repo, check);
            EyeFromView(check);
            Caustics(check, water, repo);
            ShoreAndFlow(check, water, yaml);
            Scene(check, water, repo);
        }

        /// <summary>
        /// The world in the water and the bent bed (patch group "waterscene",
        /// D82): its own group with the one sampler, below vanilla's last;
        /// zero is the sky-only water; refraction redistributes what the water
        /// already lets through rather than adding or removing light; and the
        /// C# side binds per draw, on a unit nothing else uses.
        /// </summary>
        static void Scene(Action<string, bool, string> check, string water, string repo)
        {
            string dir = Path.Combine(repo, "assets/vintagevisuals/shadersnippets");
            string scene = File.ReadAllText(Path.Combine(dir, "waterscene.glsl"));
            string yaml = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/waterscene.yaml"));
            string bare = StripComments(scene);

            // One sampler, injected below vanilla's last (liquidDepth) - the
            // rule that cost the world render twice (CLAUDE.md).
            int samplers = Regex.Matches(bare, @"^\s*uniform\s+sampler", RegexOptions.Multiline).Count;
            bool anchored = yaml.Contains("tokens: uniform sampler2D liquidDepth;") &&
                            Regex.IsMatch(yaml, @"type: insert_after[^-]*tokens: uniform vec2 frameSize;[^-]*snippet: waterscene\.glsl");
            string refShader = Path.Combine(repo, "reference/game-shaders/chunkliquid.fsh");
            bool below = true;
            string where = "no reference shader";
            if (File.Exists(refShader))
            {
                string r = File.ReadAllText(refShader);
                int ld = r.IndexOf("uniform sampler2D liquidDepth;", StringComparison.Ordinal);
                int fs = r.IndexOf("uniform vec2 frameSize;", StringComparison.Ordinal);
                int lastSampler = Regex.Matches(r, @"^\s*uniform\s+sampler\w*\s+\w+;", RegexOptions.Multiline)
                                       .Cast<Match>().Select(m => m.Index).DefaultIfEmpty(-1).Max();
                below = ld >= 0 && fs > ld && fs > lastSampler;
                where = "liquidDepth at " + ld + ", frameSize at " + fs + ", last sampler at " + lastSampler;
            }
            check("waterscene declares its one sampler below every vanilla sampler", samplers == 1 && anchored && below,
                  samplers + " sampler(s), anchored " + anchored + ", " + where);
            check("waterscene samples only with textureLod (its reads sit in loops and branches)",
                  !Regex.IsMatch(bare, @"\btexture\s*\("), "");

            // Zero is the sky-only water.
            string trace = FunctionBody(scene, "vec4 vvWaterSceneTrace(");
            string first = Statements(trace)[0];
            check("the scene trace misses unless the frame is bound, the tier marches and the mix is on",
                  first.Contains("vv_waterSceneOn < 0.5") && first.Contains("vv_waterSceneSteps < 0.5") &&
                  first.Contains("vv_waterSceneMix < 0.001") && first.Contains("return vec4(0.0)"), first);

            string surf = FunctionBody(water, "vec4 vvWaterSurface(");
            var stmts = Statements(surf);
            int sky = stmts.FindIndex(x => x.StartsWith("image *= skyFactor", StringComparison.Ordinal));
            int mixAt = stmts.FindIndex(x => x.StartsWith("image = mix(image, sceneHit.rgb", StringComparison.Ordinal));
            check("what the trace finds is mixed in after the roof dims the sky, not dimmed by it",
                  sky >= 0 && mixAt > sky && !stmts[mixAt].Contains("skyFactor"), "skyFactor at " + sky + ", scene mix at " + mixAt);
            check("the water marches with the shared core: reflect.glsl injected at the group's own marker, window depth from depthTex",
                  Regex.IsMatch(yaml, @"type: insert_before[^-]*tokens: ""// vintagevisuals-hook: water reflect core""[^-]*snippet: reflect\.glsl") &&
                  bare.Contains("// vintagevisuals-hook: water reflect core") == false && scene.Contains("// vintagevisuals-hook: water reflect core") &&
                  trace.Contains("vvReflectMarch(vv_waterScene, depthTex, 1.0, vec2(zNear, zFar)") &&
                  !Regex.IsMatch(bare, @"\bstruct\s+VvReflectHit\b"), "");
            check("only top faces looking up trace, and only where the reflection can show",
                  surf.Contains("if (top > 0.5 && reflectShare > 0.004)") && surf.Contains("if (rs.y > 0.0) sceneHit = vvWaterSceneTrace("), "");

            // Refraction: with the bent copy equal to the straight view it
            // must change nothing over any background, and it never lowers
            // the opacity. That is the interaction a line-by-line read misses:
            // a colour added and an alpha raised that do not pay each other.
            // The bent copy's share is kept (bentShare) for the caustics, then added.
            string addR = stmts.Any(x => x == "premult += bentShare")
                ? stmts.FirstOrDefault(x => x.StartsWith("bentShare = bentBed.rgb", StringComparison.Ordinal)) ?? "premult += 99.0"
                : "premult += 99.0";
            string alphaR = stmts.FirstOrDefault(x => x.StartsWith("outAlpha = mix(outAlpha, 1.0", StringComparison.Ordinal)) ?? "outAlpha = 99.0";
            double worst = 0, drop = 0;
            for (int ai = 0; ai <= 10; ai++)
                for (int ki = 0; ki <= 10; ki++)
                    for (int bi = 0; bi <= 4; bi++)
                    {
                        double a = ai / 10.0, k = ki / 10.0, bg = bi / 4.0, pre = 0.3 * a;
                        var sy = Syms("outAlpha", a, "k", k);
                        sy["bentBed"] = new Val(bg, bg, bg);
                        sy["bentBed.rgb"] = new Val(bg, bg, bg);
                        double pre2 = pre + Eval(Rhs(addR), sy).X;
                        double a2 = Scalar(Rhs(alphaR), Syms("outAlpha", a, "k", k));
                        worst = Math.Max(worst, Math.Abs((pre2 + (1 - a2) * bg) - (pre + (1 - a) * bg)));
                        drop = Math.Max(drop, a - a2);
                    }
            check("refraction with no bend changes nothing over any background, and never thins the water",
                  worst < 1e-9 && drop < 1e-12, "worst " + worst + ", drop " + drop);
            check("refraction needs the frame bound and its slider above zero",
                  surf.Contains("if (refraction > 0.001 && top > 0.5 && vv_waterSceneOn > 0.5 && path > 0.0)"), "");

            // C#: gated, bound per draw, on a unit nothing else uses.
            string gating = File.ReadAllText(Path.Combine(repo, "src/VintageVisualsModSystem.cs"));
            check("the waterscene group is gated on Water.Enabled and Water.SceneReflections, and toggling it reloads",
                  Regex.IsMatch(gating, @"group == WaterSubsystem\.SceneGroupName\)\s*\{\s*return ConfigManager\.Config\.Water\.Enabled && ConfigManager\.Config\.Water\.SceneReflections;") &&
                  gating.Contains("ConfigManager.Config.Water.SceneReflections ? \"O\""), "");

            string hook = File.ReadAllText(Path.Combine(repo, "src/Water/WaterSceneBindInterceptor.cs"));
            string binder = File.ReadAllText(Path.Combine(repo, "src/Water/WaterShaderBinder.cs"));
            int Unit(string file, string name)
            {
                var m = Regex.Match(File.ReadAllText(Path.Combine(repo, file)), name + @"\s*=\s*(\d+);");
                return m.Success ? int.Parse(m.Groups[1].Value) : -1;
            }
            int mine = Unit("src/Water/WaterSceneBindInterceptor.cs", "public const int TextureUnit");
            var others = new[] {
                Unit("src/Reflections/WorldReflectionVolume.cs", "public const int TextureUnit"),
                Unit("src/Reflections/WorldReflectionVolume.cs", "public const int CanopyTextureUnit"),
                Unit("src/Reflections/SceneCaptureRenderer.cs", "public const int TextureUnit"),
                Unit("src/PseudoPBR/MaterialAtlasTexture.cs", "public const int TextureUnit"),
                Unit("src/PseudoPBR/MaterialAtlasTexture.cs", "public const int SecondTextureUnit") };
            check("the water scene unit is above chunkliquid's five and shared with no other mod texture",
                  mine > 4 && mine < 16 && !others.Contains(mine) && !others.Contains(-1),
                  "water " + mine + ", others " + string.Join(",", others));
            check("the water scene colour is bound when vanilla binds depthTex, filtered on our sampler",
                  hook.Contains("DepthSampler = \"depthTex\"") && hook.Contains("program.HasUniform(SceneSampler)") &&
                  hook.Contains("gl.TrySet(previous)"), "");
            check("the binder publishes no texture unless the program has the scene sampler",
                  Regex.IsMatch(binder, @"if \(!program\.HasUniform\(WaterSceneBindInterceptor\.SceneSampler\)\)\s*\{\s*WaterSceneBindInterceptor\.SetSceneTexture\(0\);"), "");

            // Every vv_water uniform declared in either snippet is uploaded by
            // the binder, and every upload names a declared uniform.
            var declared = Regex.Matches(StripComments(water) + "\n" + bare, @"uniform\s+\w+\s+(vv_water\w*)\s*;")
                                .Cast<Match>().Select(m => m.Groups[1].Value).ToHashSet();
            var uploaded = Regex.Matches(binder, "\"(vv_water\\w*)\"")
                                .Cast<Match>().Select(m => m.Groups[1].Value).ToHashSet();
            uploaded.Remove("vv_waterScene");
            declared.Remove("vv_waterScene");
            var missing = declared.Except(uploaded).Concat(uploaded.Except(declared).Select(u => "~" + u)).ToList();
            check("every water uniform is declared and uploaded, both ways", missing.Count == 0 && declared.Count > 20,
                  string.Join(", ", missing));
        }

        /// <summary>
        /// Shoreline, rivers and far swells (D79), evaluated from the shipped
        /// lines: zero is vanilla, the edge only ever thins the water, foam is
        /// laid over (never under) what is there, no flow is exactly the old
        /// waves, and the swells tile the origin period.
        /// </summary>
        static void ShoreAndFlow(Action<string, bool, string> check, string water, string yaml)
        {
            string surf = FunctionBody(water, "vec4 vvWaterSurface(");
            double soft = Const(water, "VV_WATER_SHORE_SOFT");
            string edgeExpr = Rhs(Statement(surf, "float edge ="));
            double Edge(double depth, double shore, double fade) =>
                Scalar(edgeExpr, Syms("shoreDepth", depth, "shore", shore, "shoreFade", fade, "VV_WATER_SHORE_SOFT", soft));
            double edgeZero = 0, edgeOver = 0;
            foreach (double d in new[] { 0.0, 0.01, 0.03, 0.1, 1.0 })
            {
                edgeZero = Math.Max(edgeZero, Math.Abs(Edge(d, 0, 1) - 1));
                foreach (double sh in new[] { 0.3, 1.0 }) edgeOver = Math.Max(edgeOver, Edge(d, sh, 1) - 1);
            }
            check("shoreline at 0 leaves the water untouched, and its edge only ever thins the water",
                  edgeZero < 1e-12 && edgeOver <= 1e-12 && Edge(0, 1, 1) < 1e-9 && Math.Abs(Edge(1, 1, 1) - 1) < 1e-9,
                  "worst at 0 " + edgeZero + ", worst over " + edgeOver);

            string foamStmt = Statement(surf, "foam = (cover > threshold");
            var fs = Syms("cover", 1.0, "threshold", 0.2, "shore", 0.0, "shoreFade", 1.0, "VV_WATER_SHORE_ALPHA", Const(water, "VV_WATER_SHORE_ALPHA"));
            bool foamOff = Scalar(Rhs(foamStmt), fs) == 0;
            fs["shore"] = new Val(1.0);
            bool foamOn = Scalar(Rhs(foamStmt), fs) > 0;
            string overStmt = Statements(surf).FirstOrDefault(x => x.StartsWith("outAlpha = foam + outAlpha", StringComparison.Ordinal));
            string overExpr = overStmt != null ? Rhs(overStmt) : "outAlpha * 0.5";
            bool over = overStmt != null;
            foreach (double f in new[] { 0.0, 0.4, 0.8 })
                foreach (double a in new[] { 0.0, 0.5, 1.0 })
                    over &= Scalar(overExpr, Syms("foam", f, "outAlpha", a)) >= a - 1e-12;
            check("the foam line is off at 0, and is laid over the water without thinning it", foamOff && foamOn && over, foamStmt);

            string keep = FunctionBody(water, "float vvWaterVanillaFoamKeep(");
            var ks = Statements(keep);
            double keepAtZero = Scalar(Rhs("x = " + ks.Last().Substring("return ".Length)),
                                       Syms("vv_waterShore", 0.0, "VV_WATER_SHORE_KEEP", Const(water, "VV_WATER_SHORE_KEEP")));
            check("vanilla's edge foam is untouched unless the foam line replaces it",
                  yaml.Contains("noise2/2, 0, 1) * vvWaterVanillaFoamKeep();") &&
                  ks.Count > 0 && ks[0].StartsWith("if (vv_water < 0.5) return 1.0", StringComparison.Ordinal) &&
                  Math.Abs(keepAtZero - 1) < 1e-12, "keep at 0 " + keepAtZero);

            // No flow, or wind: the waves are the previous build's exactly.
            string streak = Rhs(Statements(FunctionBody(water, "vec2 vvWaterStreak(")).Last());
            var ss = Syms("flowAmount", 0.0, "VV_WATER_FLOW_STREAK", Const(water, "VV_WATER_FLOW_STREAK"));
            ss["slope"] = Vec(0.3, -0.2, 0); ss["flowDir"] = Vec(0.6, 0.8, 0);
            Val still = Eval(streak, ss);
            ss["flowAmount"] = new Val(1.0);
            Val river = Eval(streak, ss);
            double along = river.X * 0.6 + river.Y * 0.8, alongStill = 0.3 * 0.6 - 0.2 * 0.8;
            double across = -river.X * 0.8 + river.Y * 0.6, acrossStill = -0.3 * 0.8 - 0.2 * 0.6;
            check("with no flow the waves are untouched; a current cuts their slope along it and keeps it across",
                  Math.Abs(still.X - 0.3) < 1e-12 && Math.Abs(still.Y + 0.2) < 1e-12 &&
                  Math.Abs(along) < Math.Abs(alongStill) - 1e-6 && Math.Abs(across - acrossStill) < 1e-9, streak);

            string calm = Rhs(Statement(FunctionBody(water, "vec2 vvWaterWaveSlope("), "height *= mix("));
            Func<double, double, double> calmAt = (flow, wind) => Scalar(calm, Syms("flowAmount", flow, "wind", wind,
                "VV_WATER_STILL_CALM", Const(water, "VV_WATER_STILL_CALM")));
            check("only still water in still air is calmed",
                  Math.Abs(calmAt(1, 0) - 1) < 1e-12 && Math.Abs(calmAt(0, 1) - 1) < 1e-12 && calmAt(0, 0) < 1, calm);

            string swell = FunctionBody(water, "vec2 vvWaterSwellSlope(");
            var sk = Regex.Matches(swell, @"vec2\(\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*\)\s*\*\s*K")
                          .Select(m => (double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                                        double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)))
                          .ToList();
            check("far swells have whole cycles per 4096 blocks",
                  sk.Count == 2 && sk.All(k => k.Item1 == Math.Floor(k.Item1) && k.Item2 == Math.Floor(k.Item2)) && swell.Contains("4096.0"),
                  string.Join(" ", sk));
        }

        /// <summary>
        /// Caustics (D93, replacing D77): the waves REDISTRIBUTE the sun on
        /// the bed. The interactions that matter are evaluated from the
        /// shipped lines: flat water and a bed at the surface focus nothing,
        /// the web's average over the bed is 1 (it neither lifts nor dims the
        /// pond, the D92 lesson), the light needs the same visible sun the
        /// glints do, the slider at 0 is a web of exactly 1, and the
        /// composition scales what shows through by the web without letting a
        /// web of 1 change a single value.
        /// </summary>
        static void Caustics(Action<string, bool, string> check, string water, string repo)
        {
            // --- the lens: flat water and a bed at the surface gather nothing ---
            string rawBody = FunctionBody(water, "float vvWaterCausticRaw(");
            string detExpr = Rhs(Statement(rawBody, "float det ="));
            string rawRet = Rhs(Statements(rawBody).First(x => x.StartsWith("return ", StringComparison.Ordinal)));
            Func<Val, double, double, double> raw = (h, f, s) =>
            {
                var sy = Syms("f", f, "s", s);
                sy["h"] = h;
                sy["det"] = new Val(Scalar(detExpr, sy));
                return Scalar(rawRet, sy);
            };
            double flatWorst = 0, surfaceWorst = 0;
            foreach (double f in new[] { 0.1, 0.5, 1.0 })
                foreach (double s in new[] { 0.05, 0.3, 0.8 })
                    flatWorst = Math.Max(flatWorst, Math.Abs(raw(Vec(0, 0, 0), f, s) - 1.0));
            foreach (var h in new[] { Vec(3, 0.5, -2), Vec(-4, 0, 4), Vec(1, 1, 1) })
                surfaceWorst = Math.Max(surfaceWorst, Math.Abs(raw(h, 0.0, 0.1) - 1.0));
            double focus = raw(Vec(2.0, 0, 0), 0.5, 0.07);
            check("caustics: flat water, and a bed right at the surface, gather no light; a focus is bright",
                  flatWorst < 1e-9 && surfaceWorst < 1e-9 && focus > 5.0,
                  "flat " + flatWorst + ", at the surface " + surfaceWorst + ", at a focus " + focus);

            // --- the web conserves light: its mean over the bed is 1 ---------
            // The shipped waves, warp and tables, driven over a 4 x 4 block
            // patch at a few lens strengths and softnesses. The lens and the
            // shoulder are the shipped statements; only the wave sum is a C#
            // loop, over the wave constants read from the shipped call list.
            string curv = FunctionBody(water, "vec3 vvWaterCausticCurvature(");
            var waves = Regex.Matches(curv, @"vvWaterCausticWave\(world, t, vec2\(\s*(-?[\d.]+),\s*(-?[\d.]+)\),\s*([\d.]+),\s*([\d.]+)\)")
                             .Select(m => new[] { D(m.Groups[1].Value), D(m.Groups[2].Value), D(m.Groups[3].Value), D(m.Groups[4].Value) })
                             .ToList();
            double curve = Const(water, "VV_WATER_CAUSTIC_CURVE"), warp = Const(water, "VV_WATER_CAUSTIC_WARP");
            double[] mean = ConstArray(water, "VV_WATER_CAUSTIC_MEAN"), norm = ConstArray(water, "VV_WATER_CAUSTIC_NORM");
            // The relative-to-average, shoulder and renormalise chain, as it
            // ships: the last four statements of vvWaterCausticWeb, run through
            // the evaluator over a fine grid of m / mean (the chain only sees
            // that ratio) with the second table stubbed to its value.
            string webBody = FunctionBody(water, "vec3 vvWaterCausticWeb(");
            var webStmts = Statements(webBody);
            int qAt = webStmts.FindIndex(x => x.StartsWith("vec3 q = m / mean", StringComparison.Ordinal));
            double shoulder = Const(water, "VV_WATER_CAUSTIC_SHOULDER");
            Func<double, double[]> chainFor = normV =>
            {
                var lut = new double[2001];
                for (int k = 0; k <= 2000 && qAt >= 0; k++)
                {
                    var sy = Syms("mean", 1.0, "VV_WATER_CAUSTIC_SHOULDER", shoulder, "normV", normV);
                    double r = k * 0.02;
                    sy["m"] = Vec(r, r, r);
                    lut[k] = double.NaN;
                    for (int i = qAt; i < webStmts.Count; i++)
                    {
                        string st = StubCall(webStmts[i], "vvWaterCausticTable", "normV");
                        if (st.StartsWith("return ", StringComparison.Ordinal)) { lut[k] = Eval(Rhs(st), sy).X; break; }
                        var mm = Regex.Match(st, @"^(?:vec3 )?(\w+) =");
                        if (mm.Success) sy[mm.Groups[1].Value] = Eval(Rhs(st), sy);
                    }
                }
                return lut;
            };
            string tableBody = FunctionBody(water, "float vvWaterCausticTable(");
            string fiExpr = Rhs(Statement(tableBody, "float fi =")), siExpr = Rhs(Statement(tableBody, "float si ="));
            Func<double[], double, double, double> table = (tab, f, s) =>
            {
                double fi = Scalar(fiExpr, Syms("f", f)), si = Scalar(siExpr, Syms("s", s));
                int a = (int)fi, b = (int)si; double u = fi - a, v = si - b; int i = a * 6 + b;
                return (tab[i] * (1 - v) + tab[i + 1] * v) * (1 - u) + (tab[i + 6] * (1 - v) + tab[i + 7] * v) * u;
            };
            // The sweep below runs the lens in C# for speed; it is the shipped
            // statement, as long as the two agree here.
            Func<double, double, double, double, double, double> Raw = (xx, xz, zz, f, s) =>
            {
                double det = (1 - f * xx) * (1 - f * zz) - f * f * xz * xz;
                return Math.Sqrt(1 + s * s) / Math.Sqrt(det * det + s * s);
            };
            double rawAgree = 0;
            foreach (var h in new[] { Vec(3, 0.5, -2), Vec(-1.2, 0.7, 2.2), Vec(2.0, 0, 0) })
                foreach (double f in new[] { 0.3, 0.5, 0.9 })
                    rawAgree = Math.Max(rawAgree, Math.Abs(raw(h, f, 0.2) - Raw(h.X, h.Y, h.Z, f, 0.2)));
            const double TW = 2 * Math.PI / 4096.0;
            double worstMean = 0;
            string meanDetail = "";
            if (waves.Count == 10 && mean.Length == 54 && norm.Length == 54)
            {
                foreach (double f in new[] { 0.3, 0.55, 0.8 })
                    foreach (double s in new[] { 0.12, 0.25, 0.5 })
                    {
                        double sum = 0; int n = 0;
                        double[] lut = chainFor(table(norm, f, s));
                        double meanFS = table(mean, f, s);
                        foreach (double t in new[] { 2.0, 9.0, 17.0 })
                            for (int ix = 0; ix < 192; ix++)
                                for (int iz = 0; iz < 128; iz++)
                                {
                                    double x = 37.0 + (ix + 0.5) / 16.0, z = 91.0 + (iz + 0.5) / 16.0;
                                    double wx = x + warp * Math.Sin((2300 * x - 1300 * z) * TW - t * 0.7);
                                    double wz = z + warp * Math.Sin((1100 * x + 2500 * z) * TW + t * 0.6);
                                    double hxx = 0, hxz = 0, hzz = 0;
                                    foreach (var w in waves)
                                    {
                                        double kx = w[0] * TW, kz = w[1] * TW;
                                        double sn = Math.Sin(kx * wx + kz * wz - t * w[2]) * w[3] / (kx * kx + kz * kz);
                                        hxx -= kx * kx * sn; hxz -= kx * kz * sn; hzz -= kz * kz * sn;
                                    }
                                    double r = Raw(hxx * curve, hxz * curve, hzz * curve, f, s) / meanFS / 0.02;
                                    int k0 = Math.Min((int)r, 1999);
                                    double fr = Math.Min(r - k0, 1.0);
                                    sum += lut[k0] * (1 - fr) + lut[k0 + 1] * fr; n++;
                                }
                        double m = sum / n;
                        worstMean = Math.Max(worstMean, Math.Abs(m - 1));
                        meanDetail += " f" + f + "/s" + s + "=" + m.ToString("0.000");
                    }
            }
            check("caustics conserve light: the web's mean over the bed stays within 6 % of 1 (no lift, no dimming)",
                  waves.Count == 10 && worstMean < 0.06 && rawAgree < 1e-9, waves.Count + " waves;" + meanDetail);

            // --- the sun gate and the slider ----------------------------------
            string surf = FunctionBody(water, "vec4 vvWaterSurface(");
            var stmts = Statements(surf);
            string sunLightExpr = Rhs(Statement(surf, "float sunLight ="));
            Func<double, double, double, double> sun = (up, clear, shadow) =>
                Scalar(sunLightExpr, Syms("sunUp", up, "clearSky", clear, "shadowBright", shadow));
            check("caustics need a sun that is up, visible past the clouds, and not shadowed at the surface",
                  sun(0, 1, 1) < 1e-12 && sun(1, 0, 1) < 1e-12 && sun(1, 1, 0) < 1e-12 && Math.Abs(sun(1, 1, 1) - 1) < 1e-12,
                  sunLightExpr);

            string webStmt = Statement(surf, "causticWeb = 1.0 +");
            double share = Const(water, "VV_WATER_CAUSTIC_SUN_SHARE");
            Func<double, double, double, Val> webAt = (slider, sunL, fade) =>
            {
                var sy = Syms("sunLight", sunL, "caustics", slider, "fade", fade, "VV_WATER_CAUSTIC_SUN_SHARE", share);
                sy["web"] = Vec(1.8, 0.6, 1.2); sy["downTransmit"] = Vec(0.9, 1, 1);
                return Eval(Rhs(webStmt), sy);
            };
            Val off = webAt(0, 1, 1), shaded = webAt(0.6, 0, 1), on = webAt(0.6, 1, 1);
            check("caustics: the slider at 0, or no sun, is a web of exactly 1; above 0 the web shows",
                  off.X == 1 && off.Y == 1 && off.Z == 1 && shaded.X == 1 && shaded.Y == 1 && on.X > 1.2 && on.Y < 0.9 && share < 1.0,
                  webStmt);

            // --- the composition: the web scales what shows through -----------
            // Blending premult + dst * (1 - outAlpha) over a bed whose colour is
            // dst, the result must equal the plain composition plus
            // dst * through * (web - 1): the bed seen through the water, lit by
            // the web, exactly. A web of 1 changes nothing.
            int stepAt = stmts.FindIndex(x => x.StartsWith("float through = 1.0 - outAlpha", StringComparison.Ordinal));
            string lowExpr = Rhs(Statement(surf, "float low ="));
            string liftExpr = Rhs(Statement(surf, "vec3 lift ="));
            string addExpr = Rhs(Statement(surf, "premult += bentShare * (causticWeb - 1.0)"));
            string alphaExpr = Rhs(stmts.FirstOrDefault(x => x.StartsWith("outAlpha = 1.0 - through", StringComparison.Ordinal)) ?? "outAlpha = 99.0");
            double worstCompose = 0;
            foreach (var web in new[] { Vec(1, 1, 1), Vec(0.5, 0.5, 0.5), Vec(1.6, 1.6, 1.6), Vec(0.8, 1.0, 1.3) })
                foreach (double alpha in new[] { 0.2, 0.7 })
                {
                    var bed = Vec(0.4, 0.35, 0.2); var pre = Vec(0.1, 0.15, 0.12);
                    var sy = Syms("outAlpha", alpha);
                    sy["causticWeb"] = web; sy["bedColor"] = bed; sy["bentShare"] = Vec(0, 0, 0);
                    double through = 1 - alpha;
                    sy["through"] = new Val(through);
                    sy["low"] = new Val(Scalar(lowExpr, sy));
                    sy["lift"] = Eval(liftExpr, sy);
                    Val add = Eval(addExpr, sy);
                    double newAlpha = Scalar(alphaExpr, sy);
                    double[] w = { web.X, web.Y, web.Z }, b = { bed.X, bed.Y, bed.Z }, p = { pre.X, pre.Y, pre.Z }, a = { add.X, add.Y, add.Z };
                    for (int c = 0; c < 3; c++)
                    {
                        double got = p[c] + a[c] + b[c] * (1 - newAlpha);
                        double want = p[c] + b[c] * through * w[c];
                        worstCompose = Math.Max(worstCompose, Math.Abs(got - want));
                    }
                }
            int resultAt = stmts.FindIndex(x => x.StartsWith("vec4 result =", StringComparison.Ordinal));
            int refrAt = stmts.FindIndex(x => x.StartsWith("bentShare = bentBed.rgb", StringComparison.Ordinal));
            check("caustics light the bed seen through the water by the web, exactly, and a web of 1 changes nothing",
                  worstCompose < 1e-9 && stepAt > refrAt && refrAt >= 0 && stepAt < resultAt,
                  "worst " + worstCompose + ", refraction at " + refrAt + ", caustics at " + stepAt + ", result at " + resultAt);

            // Light falls on a face by N.L: a wall under the water takes less
            // than the floor, and a face turned from the sun takes none.
            string facingExpr = Rhs(Statement(surf, "float facing ="));
            Func<Val, double> face = nrm => { var sy = Syms(); sy["bedNormal"] = nrm; sy["down"] = Vec(0.2, -0.95, 0.1); return Scalar(facingExpr, sy); };
            double floorLit = face(Vec(0, 1, 0)), wallLit = face(Vec(1, 0, 0)), awayLit = face(Vec(-0.6, -0.8, 0));
            check("caustics fall on a face by N.L: a wall takes less than the floor, a face turned away none",
                  floorLit > 0.9 && wallLit < 0.5 * floorLit && awayLit == 0 && stmts.Any(x => x.StartsWith("fade *= facing", StringComparison.Ordinal)),
                  "floor " + floorLit + ", wall " + wallLit + ", away " + awayLit);

            // Gone by VV_WATER_CAUSTIC_DEEP, untouched in the shallows...
            string depthFade = Rhs(stmts.FirstOrDefault(x => x.StartsWith("fade *=", StringComparison.Ordinal) && x.Contains("VV_WATER_CAUSTIC_DEEP")) ?? "fade *= 1.0");
            double deep = Const(water, "VV_WATER_CAUSTIC_DEEP");
            Func<double, double> fd = dd => Scalar(depthFade, Syms("depth", dd, "VV_WATER_CAUSTIC_DEEP", deep));
            // ...but NOT by the view path: the web scales only what shows
            // through, so the water body already hides it as much as the bed.
            // A second path fade halved it in a 2-block pond (2026-11-27).
            bool pathFree = !stmts.Any(x => x.StartsWith("fade *=", StringComparison.Ordinal) && Regex.IsMatch(x, @"\bpath\b"));
            check("caustics fade out with depth, and are not dimmed a second time by the view path",
                  fd(deep) < 1e-9 && Math.Abs(fd(1.0) - 1.0) < 1e-9 && deep <= 8.0 && pathFree,
                  "deep " + deep + ", at 1 block " + fd(1.0) + ", path-free " + pathFree);

            bool whole = waves.Count == 10 && waves.All(w => w[0] == Math.Floor(w[0]) && w[1] == Math.Floor(w[1]));
            var warpK = Regex.Matches(curv, @"vec2\(\s*(-?[\d.]+),\s*(-?[\d.]+)\)\s*\*\s*K").Select(m => new[] { D(m.Groups[1].Value), D(m.Groups[2].Value) }).ToList();
            whole &= warpK.Count == 2 && warpK.All(w => w[0] == Math.Floor(w[0]) && w[1] == Math.Floor(w[1]));
            check("caustic waves have whole cycles per 4096 blocks",
                  whole && FunctionBody(water, "vec3 vvWaterCausticWave(").Contains("/ 4096.0") && curv.Contains("/ 4096.0"),
                  waves.Count + " waves, " + warpK.Count + " warp vectors");

            // Compiled in only by its own group: water.glsl never defines the
            // switch, the watercaustics group does, gated on the slider.
            string yamlC = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/watercaustics.yaml"));
            string mod = File.ReadAllText(Path.Combine(repo, "src/VintageVisualsModSystem.cs"));
            check("the caustic code compiles in only through the watercaustics group, gated on Water.Caustics",
                  !Regex.IsMatch(water, @"#define\s+VV_WATER_CAUSTICS") && yamlC.Contains("#define VV_WATER_CAUSTICS 1")
                  && water.Contains("#ifdef VV_WATER_CAUSTICS")
                  && Regex.IsMatch(mod, @"group == WaterSubsystem\.CausticsGroupName\)\s*\{\s*return ConfigManager\.Config\.Water\.Enabled && ConfigManager\.Config\.Water\.CausticsCompiled;")
                  && Regex.IsMatch(mod, @"CausticsCompiled \? ""K"""),
                  "define only in watercaustics.yaml; group gated and in the gating signature");
        }

        static double D(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The values of a `const float NAME[n] = float[n](...)` array.</summary>
        static double[] ConstArray(string source, string name)
        {
            var m = Regex.Match(StripComments(source), @"const float " + name + @"\[\d+\]\s*=\s*float\[\d+\]\(([^)]*)\)");
            if (!m.Success) return new double[0];
            return m.Groups[1].Value.Split(',').Select(x => D(x.Trim())).ToArray();
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

        static void Parity(Action<string, bool, string> check, string water, string terrain, string core)
        {
            var pairs = new[]
            {
                ("uint vvWaterHashU(", "uint vvTerrainHashU("),
                ("vec4 vvWaterCellRandom(", "vec4 vvTerrainCellRandom("),
                ("vec2 vvWaterRippleSlope(", "vec2 vvTerrainRippleSlope("),
            };
            var renames = new[]
            {
                ("vvTerrain", "vvWater"),
                ("VV_TERRAIN_RIPPLE_", "VV_WATER_RIPPLE_"),
            };
            foreach (var (w, t) in pairs)
            {
                string a = Normalise(FunctionBody(water, w));
                string b = Normalise(FunctionBody(terrain, t), renames);
                check("water copy of " + t.TrimEnd('(').Split(' ').Last() + " matches terrain", a == b,
                      a == b ? "" : "bodies differ after the prefix rename");
            }

            // The sky and the direction cells moved into the shared core
            // (reflect.glsl, D81). Until water calls the core itself, any copy
            // it still carries must match the core. Once the copy is gone the
            // question is answered by construction and these drop out.
            var coreRenames = new[]
            {
                ("vvReflect", "vvWater"),
                ("VV_REFLECT_SKY_MAX", "VV_WATER_MAX"),
                ("VV_REFLECT_", "VV_WATER_"),
                // The caller passes what water reads from its own uniforms
                // (lightPosition is not reliably uploaded to chunkliquid).
                ("sunDir.xz", "vv_waterSunDir.xz"),
                ("clamp(dayLight,", "clamp(vv_sceneDayLight,"),
            };
            var corePairs = new[]
            {
                ("vec3 vvWaterReflectCell(", "vec3 vvReflectCell("),
                ("vec3 vvWaterReflectionFallback(", "vec3 vvReflectSky("),
            };
            foreach (var (w, c) in corePairs)
            {
                if (!water.Contains(w)) continue;
                string a = Normalise(FunctionBody(water, w));
                string b = Normalise(FunctionBody(core, c), coreRenames);
                check("water copy of " + c.TrimEnd('(').Split(' ').Last() + " matches the shared core", a == b,
                      a == b ? "" : "bodies differ after the prefix rename");
            }

            var consts = new[]
            {
                ("VV_WATER_RIPPLE_DEPTH", "VV_TERRAIN_RIPPLE_DEPTH"),
                ("VV_WATER_RIPPLE_BAND", "VV_TERRAIN_RIPPLE_BAND"),
                ("VV_WATER_RIPPLE_WAVE", "VV_TERRAIN_RIPPLE_WAVE"),
                ("VV_WATER_RIPPLE_JITTER", "VV_TERRAIN_RIPPLE_JITTER"),
                ("VV_WATER_RIPPLE_TEXELS", "VV_TERRAIN_RIPPLE_TEXELS"),
            };
            var coreConsts = new[]
            {
                ("VV_WATER_REFLECT_CELLS", "VV_REFLECT_CELLS_SHARP"),
                ("VV_WATER_HORIZON", "VV_REFLECT_HORIZON"),
                ("VV_WATER_GROUND", "VV_REFLECT_GROUND"),
                ("VV_WATER_HORIZON_LIFT", "VV_REFLECT_HORIZON_LIFT"),
                ("VV_WATER_TOWARD", "VV_REFLECT_TOWARD"),
                ("VV_WATER_MAX", "VV_REFLECT_SKY_MAX"),
            };
            bool HasConst(string src, string name) => Regex.IsMatch(src, @"const\s+float\s+" + name + @"\b");
            var drift = consts.Where(c => Math.Abs(Const(water, c.Item1) - Const(terrain, c.Item2)) > 1e-9)
                              .Select(c => c.Item1 + " vs " + c.Item2)
                              .Concat(coreConsts.Where(c => HasConst(water, c.Item1) &&
                                                            Math.Abs(Const(water, c.Item1) - Const(core, c.Item2)) > 1e-9)
                                                .Select(c => c.Item1 + " vs " + c.Item2))
                              .ToList();
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

            string dropKeep = FunctionBody(water, "float vvWaterVanillaDropletKeep(");
            check("vanilla's water droplets stay unless this mod's rings replace them",
                  yaml.Contains("f = dropletnoise(uv) * vvWaterVanillaDropletKeep();") &&
                  Statements(dropKeep).FirstOrDefault()?.StartsWith("if (vv_water < 0.5) return 1.0", StringComparison.Ordinal) == true &&
                  dropKeep.Contains("1.0 - clamp(vv_waterOwnDrops, 0.0, 1.0)"), "");
            string surf = FunctionBody(water, "vec4 vvWaterSurface(");
            var coverSyms = new Func<double, double, Dictionary<string, Val>>((cover, overcast) => Syms(
                "vv_sceneSkyCover", cover, "vv_sceneOvercast", overcast,
                "VV_WATER_GLINT_COVER_START", Const(water, "VV_WATER_GLINT_COVER_START"),
                "VV_WATER_GLINT_COVER_END", Const(water, "VV_WATER_GLINT_COVER_END")));
            string clearExpr = Rhs(Statement(surf, "float clearSky ="));
            check("water glints need a visible sun: the game's cloud cover hides them, and they shoulder rather than clip",
                  Scalar(clearExpr, coverSyms(1.0, 0.0)) < 1e-9 && Math.Abs(Scalar(clearExpr, coverSyms(0.0, 0.0)) - 1.0) < 1e-9 &&
                  Statement(surf, "glint *= VV_WATER_GLINT_PEAK /").Length > 0 && !surf.Contains("min(glint * glints, vec3(4.0))"), "");
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
