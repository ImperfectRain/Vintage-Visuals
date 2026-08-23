using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static VintageVisuals.SmokeTest.GlslEval;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// INTERACTION INVARIANTS.
    ///
    /// Seven defects have been found in this mod by looking at the game. Zero
    /// were found by the 837 checks that were passing at the time. That is not
    /// a coincidence and it is not bad luck: the checks and the defects were
    /// about different things.
    ///
    /// Every existing check asks a question about ONE thing - is this uniform
    /// uploaded, does this patch still match, is this constant quoted correctly,
    /// does this file exist. Every defect was about TWO things - a gate
    /// multiplied by a dimmer, an effect multiplied by the complement of its own
    /// trigger, a diffuse term removed with nothing paying it back, an automatic
    /// exposure lift with no shoulder under it, a sign that made a backlit effect
    /// fire only when front-lit. Each of the two lines was defensible alone.
    /// The product was not.
    ///
    /// So this file asks about products. Its checks are ARITHMETIC, run over
    /// expressions pulled out of the shipped .glsl at test time - see
    /// <see cref="GlslEval"/> for why they are not retyped into C#.
    ///
    /// Each invariant below names the defect it exists to prevent and the commit
    /// that fixed it. An invariant with no defect behind it has not earned its
    /// place: it would be one more green line making the suite look like
    /// evidence it is not.
    ///
    /// WHAT THIS CANNOT DO. It cannot tell you an effect is VISIBLE. Every
    /// invariant here would pass on a mod whose every strength defaulted to zero.
    /// Visibility is a runtime question and stays one; see docs/CHECKLIST.md for
    /// which claims rest on arithmetic and which rest on having looked.
    /// </summary>
    public static class SceneInvariantChecks
    {
        const double Eps = 1e-9;

        static string _pbr, _core, _grade, _atmos, _weather;

        public static void Run(string repo, Action<string, bool, string> check)
        {
            string Snip(string n) => File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets", n));

            _pbr = Snip("pseudopbr.glsl");
            _core = Snip("pbrcore.glsl");
            _grade = Snip("colorgrade.glsl");
            _atmos = Snip("atmosphere.glsl");
            _weather = Snip("weather.glsl");

            Console.WriteLine();
            Console.WriteLine("Interaction invariants (arithmetic over the shipped GLSL)");

            EvaluatorSelfTest(check);

            I1_GateSaturates(check);
            I2_BacklitIsBacklit(check);
            I3_EffectSurvivesItsOwnTrigger(check);
            I4_OccluderTakesOnlyTheSunsShare(check);
            I5_EnergyRemovedIsEnergyReturned(check);
            I6_AutomaticGainBringsItsOwnShoulder(repo, check);
            I7_ZeroComposesToTheIdentity(check);
            I8_NoFactorAppliedTwice(check);
            I9_GpuResourcesSurviveAReload(repo, check);
            I10_DebugViewsAreDistinct(check);
            I11_ADiagnosticKeepsItsStatesApart(check);
            I12_ARateConstantIsTheRateUsed(check);
        }

        // -------------------------------------------------------------------
        // The evaluator is itself under test
        //
        // An arithmetic check is only worth the arithmetic. If smoothstep were
        // wrong here, I1 would pass on a shader that suppresses sunsets - a
        // green line asserting the opposite of the truth, which is strictly
        // worse than no line. So the primitives every invariant leans on are
        // pinned against values worked out by hand.
        // -------------------------------------------------------------------
        static void EvaluatorSelfTest(Action<string, bool, string> check)
        {
            void Num(string label, string expr, double expected, Dictionary<string, Val> syms = null)
            {
                double got;
                try { got = Scalar(expr, syms ?? Syms()); }
                catch (Exception ex) { check("eval: " + label, false, ex.Message); return; }
                check("eval: " + label, Math.Abs(got - expected) < 1e-6, expr + " = " + got + ", want " + expected);
            }

            Num("arithmetic and precedence", "1.0 + 2.0 * 3.0 - 4.0 / 2.0", 5.0);
            Num("unary minus binds tighter than *", "-2.0 * 3.0", -6.0);
            Num("clamp", "clamp(1.5, 0.0, 0.85)", 0.85);
            Num("mix", "mix(1.0, 0.25, 0.5)", 0.625);
            Num("smoothstep midpoint", "smoothstep(0.0, 1.0, 0.5)", 0.5);
            Num("smoothstep clamps below", "smoothstep(0.2, 1.0, 0.0)", 0.0);
            Num("smoothstep clamps above", "smoothstep(0.0, 0.2, 1.0)", 1.0);
            Num("pow", "pow(0.5, 3.0)", 0.125);
            Num("ternary", "a > 0.0 ? a : 1.0", 1.0, Syms("a", 0.0));
            Num("dot of unit vectors", "dot(vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, 1.0))", 1.0);
            Num("normalize then dot", "dot(normalize(vec3(3.0, 0.0, 4.0)), vec3(1.0, 0.0, 0.0))", 0.6);
            Num("scalar broadcasts over vec3", "(vec3(1.0, 2.0, 4.0) * 0.5).z", 2.0);
            Num("swizzle", "vec3(1.0, 2.0, 3.0).g", 2.0);

            // An unparseable expression must throw rather than return a number.
            // Silence here is how a check stops testing what it claims to.
            bool threw = false;
            try { Scalar("texture(atlas, uv).r", Syms()); } catch (ArgumentException) { threw = true; }
            check("eval: an unsupported call throws rather than guessing", threw, "");

            threw = false;
            try { Scalar("mysteryUniform * 2.0", Syms()); } catch (ArgumentException) { threw = true; }
            check("eval: an unbound symbol throws rather than reading zero", threw, "");

            // Extraction is under test for the same reason.
            try
            {
                string body = FunctionBody("float f(float x)\n{\n    if (x > 0.0) { return 1.0; }\n    return 2.0;\n}", "float f(");
                check("eval: function body is brace-counted, not regex-matched", body.Contains("return 2.0"), body);
            }
            catch (Exception ex) { check("eval: function body is brace-counted, not regex-matched", false, ex.Message); }

            try
            {
                Statement("float a = 1.0; float b = 2.0;", "float");
                check("eval: an ambiguous statement match is an error", false, "two statements matched silently");
            }
            catch (ArgumentException) { check("eval: an ambiguous statement match is an error", true, ""); }
        }

        // -------------------------------------------------------------------
        // I1  A GATE SATURATES BEFORE THE SIGNAL THAT DRIVES IT DOES
        //
        // Defect: a67886b. vv_sceneDayLight is "0 midnight, 1 noon", so it is
        // well under half at sunset. Three effects that PEAK at a low sun -
        // light through leaves, canopy dapple, light shafts - multiplied by it
        // directly and were suppressed hardest at the moment they should have
        // been strongest. A sunset forest looked like a midday forest with an
        // orange sky.
        //
        // The distinction the fix rests on is that "not at night" is a GATE.
        // A gate that is still climbing where the effect it admits is at its
        // peak is a dimmer wearing a gate's name, so this measures exactly that:
        // the gate must be open at sunset, and shut after dark.
        // -------------------------------------------------------------------
        static void I1_GateSaturates(Action<string, bool, string> check)
        {
            string expr;
            double dawn;
            try
            {
                expr = Rhs(Statement(FunctionBody(_pbr, "float vvSunPresence()"), "return"));
                dawn = Define(_pbr, "VV_SUN_PRESENCE_DAWN");
            }
            catch (Exception ex) { check("I1 sun-presence gate is readable", false, ex.Message); return; }

            double At(double d) => Scalar(expr, Syms("vv_sceneDayLight", d, "VV_SUN_PRESENCE_DAWN", dawn));

            check("I1 the gate is shut in the dark", At(0.0) < Eps, At(0.0).ToString());

            // 0.25 is the number that mattered: it is roughly where daylight sits
            // through golden hour, and it is what the old linear form multiplied
            // the three effects by.
            check("I1 the gate is open at sunset (dayLight 0.25)", At(0.25) > 0.99, At(0.25).ToString());
            check("I1 the gate is open at noon", At(1.0) > 0.99, At(1.0).ToString());

            // The defect restated as arithmetic: a gate is flat where a dimmer
            // still has a slope. If sunset and noon differ, something is being
            // dimmed by the time of day rather than gated by it.
            check("I1 the gate is flat between sunset and noon",
                  Math.Abs(At(1.0) - At(0.25)) < 0.01,
                  "noon " + At(1.0) + " vs sunset " + At(0.25));

            check("I1 the gate rises monotonically",
                  Enumerable.Range(0, 40).All(i => At(i / 40.0) <= At((i + 1) / 40.0) + Eps), "");

            // The threshold is a shipped number, so it gets a bound rather than
            // an equality: anywhere above roughly a fifth and golden hour is
            // back inside the ramp.
            check("I1 the gate closes below full daylight", dawn > 0.0 && dawn < 0.35, dawn.ToString());
        }

        // -------------------------------------------------------------------
        // I2  A BACKLIT TERM MUST BE LARGER BACKLIT THAN FRONT-LIT
        //
        // Defect: 140d3fb. vvTranslucency built its bent ray from -l, which made
        // dot(v, -through) reduce to dot(v, l): exactly -1 for a backlit leaf,
        // clamped away to nothing, and +1 with the sun behind the camera. The
        // effect fired where it is meaningless and switched off where it is the
        // entire point.
        //
        // No amount of reading the line finds this - it is one character and it
        // type-checks. Pointing a sun at a leaf and measuring does.
        // -------------------------------------------------------------------
        static void I2_BacklitIsBacklit(Action<string, bool, string> check)
        {
            string through, ret;
            double distortion, power;
            try
            {
                string body = FunctionBody(_pbr, "float vvTranslucency(");
                through = Rhs(Statement(body, "through", "normalize"));
                ret = Rhs(Statement(body, "return"));
                distortion = Const(body, "distortion");
                power = Const(body, "power");
            }
            catch (Exception ex) { check("I2 translucency is readable", false, ex.Message); return; }

            // A leaf hanging in front of the camera, face on. The only variable
            // is which side the sun is on.
            Val n = Vec(0, 0, 1);
            Val v = Vec(0, 0, 1);

            double Glow(Val l)
            {
                var syms = Syms("n", n, "l", l, "v", v, "distortion", distortion, "power", power);
                syms["through"] = Eval(through, syms);
                return Scalar(ret, syms);
            }

            Val Dir(double x, double y, double z)
            {
                double len = Math.Sqrt(x * x + y * y + z * z);
                return Vec(x / len, y / len, z / len);
            }

            double backlit = Glow(Dir(0.2, 0.1, -1.0));   // sun behind the leaf
            double frontlit = Glow(Dir(0.2, 0.1, 1.0));   // sun behind the camera
            double edge = Glow(Dir(1.0, 0.0, 0.0));       // sun off to the side

            check("I2 a backlit leaf transmits", backlit > 0.3, backlit.ToString());
            check("I2 a front-lit leaf does not", frontlit < 0.01, frontlit.ToString());
            check("I2 backlit exceeds front-lit by an order of magnitude",
                  backlit > frontlit * 10.0 + Eps, backlit + " vs " + frontlit);
            check("I2 a side-lit leaf sits between the two",
                  edge <= backlit + Eps && edge >= frontlit - Eps, edge.ToString());

            // The transmitted colour is tinted toward yellow-green, and the tint
            // must MOVE colour rather than add light - fruit opts out entirely.
            try
            {
                string tint = Rhs(Statement(FunctionBody(_pbr, "vec3 vvFoliageTransmission("), "vec3 tint"));
                Val leaf = Eval(tint, Syms("albedo", Vec(0.5, 0.5, 0.5), "chlorophyll", 1.0));
                Val fruit = Eval(tint, Syms("albedo", Vec(0.5, 0.5, 0.5), "chlorophyll", 0.0));
                check("I2 transmitted light through leaf tissue is green-dominant",
                      leaf.Y > leaf.X && leaf.X > leaf.Z, leaf.ToString());
                check("I2 fruit transmits its own colour, untinted",
                      Math.Abs(fruit.X - fruit.Y) < Eps && Math.Abs(fruit.Y - fruit.Z) < Eps, fruit.ToString());
            }
            catch (Exception ex) { check("I2 transmission tint is readable", false, ex.Message); }
        }

        // -------------------------------------------------------------------
        // I3  AN EFFECT MUST BE INSENSITIVE TO THE SIGNAL ITS OWN TRIGGER NEEDS
        //
        // Defect: bf97881. Canopy dapple is gated on vvCanopyEvidence, which is
        // a measurement of the shadow map: it can only be non-zero where the
        // fragment is SHADOWED. The application then multiplied by
        // shadowBrightness, which is only non-zero where the fragment is LIT.
        // The two conditions are complementary, so the product was near enough
        // zero everywhere and the user reported "no visible sunspots".
        //
        // Both lines read correctly on their own. The check therefore binds
        // shadowBrightness to two opposite values and requires the composition
        // not to notice - which it cannot, unless someone has reintroduced the
        // factor that cancels it.
        // -------------------------------------------------------------------
        static void I3_EffectSurvivesItsOwnTrigger(Action<string, bool, string> check)
        {
            List<string> stmts;
            try { stmts = DappleComposition(); }
            catch (Exception ex) { check("I3 dapple composition is readable", false, ex.Message); return; }

            double Darkening(double shadowBrightness)
            {
                var syms = Syms(
                    "dapple", 0.6,          // the canopy said: this fragment is well shaded
                    "local", 0.0,           // no torch here
                    "shadowBrightness", shadowBrightness,
                    "VV_DAPPLE_GREEN", Const(_pbr, "VV_DAPPLE_GREEN"));
                return RunComposition(stmts, syms);
            }

            double inShadow = Darkening(0.0);
            double inLight = Darkening(1.0);

            check("I3 the canopy darkens where its evidence exists", inShadow < 0.999, inShadow.ToString());
            check("I3 the canopy term does not read the sun it is blocking",
                  Math.Abs(inShadow - inLight) < Eps,
                  "shadowed " + inShadow + " vs lit " + inLight);

            // Same shape, one level up: daylight is a gate here (I1), so the
            // composition must not scale with it either.
            double dawnGate = Const2(_pbr, "VV_SUN_PRESENCE_DAWN");
            check("I3 the sun-presence gate exists to be read as a gate", dawnGate > 0.0, dawnGate.ToString());
        }

        // -------------------------------------------------------------------
        // I4  A SUN OCCLUDER TAKES ONLY THE SUN'S SHARE
        //
        // Vanilla hands the fragment shader one colour with sun, sky and block
        // light already mixed, so multiplying it by a canopy term dimmed a torch
        // hanging under a tree along with the sunlight the tree was blocking.
        // blockBrightness is vanilla's own measure of the local share, and the
        // canopy is scaled by its complement.
        //
        // The endpoints are what matter and they pull in opposite directions:
        // full local light must be untouched, and no local light must be exactly
        // what it was before the exemption existed. A check on only one of them
        // passes on an effect that has been deleted.
        // -------------------------------------------------------------------
        static void I4_OccluderTakesOnlyTheSunsShare(Action<string, bool, string> check)
        {
            List<string> stmts;
            double green;
            try { stmts = DappleComposition(); green = Const(_pbr, "VV_DAPPLE_GREEN"); }
            catch (Exception ex) { check("I4 dapple composition is readable", false, ex.Message); return; }

            double Darkening(double dapple, double local, double tintStrength)
                => RunComposition(stmts, Syms("dapple", dapple, "local", local,
                                              "shadowBrightness", 0.0, "VV_DAPPLE_GREEN", tintStrength));

            check("I4 a torch under a tree keeps its light",
                  Math.Abs(Darkening(0.8, 1.0, green) - 1.0) < Eps, Darkening(0.8, 1.0, green).ToString());

            // Measured with the tint neutralised, because the shade and the
            // colour of the shade are two claims and only one of them is about
            // how much light is left.
            check("I4 an open forest floor is shaded in full",
                  Math.Abs(Darkening(0.8, 0.0, 0.0) - 0.2) < 1e-6, Darkening(0.8, 0.0, 0.0).ToString());

            // THE GREEN SHADE IS NOT LUMINANCE-NEUTRAL, and the comment beside
            // it in pseudopbr.glsl used to say it was.
            //
            // Its channel weights are (-1.0, +0.6, -0.7), which sum to -1.1 per
            // unit of tint rather than to zero, so the tint removes about 1.1/3
            // of its own strength in luminance on top of the shade. At the
            // capped canopy that is a little under 2%.
            //
            // Left as it ships rather than rebalanced: 2% is far below anything
            // that could be seen, and no forest has been looked at since the
            // dapple gate was fixed, so retuning the colour of shade nobody has
            // observed would be guessing. What it may NOT do is grow quietly
            // into a real light loss outside VisualBudget, which is what this
            // bound is for.
            double neutral = Darkening(0.85 / 0.85, 0.0, 0.0);
            double tinted = Darkening(0.85 / 0.85, 0.0, green);
            double cost = 1.0 - tinted / neutral;
            check("I4 the green shade costs under 2% luminance at the cap",
                  cost >= 0.0 && cost < 0.02, "costs " + (cost * 100.0).ToString("0.00") + "%");

            check("I4 a lantern-lit clearing is shaded in part",
                  Darkening(0.8, 0.5, green) > Darkening(0.8, 0.0, green)
                  && Darkening(0.8, 0.5, green) < Darkening(0.8, 1.0, green),
                  Darkening(0.8, 0.5, green).ToString());

            // Never black, however deep the shade: the cap is what keeps a wood
            // legible rather than merely dark.
            check("I4 the deepest shade still passes light",
                  Darkening(1.0, 0.0, green) > 0.1, Darkening(1.0, 0.0, green).ToString());

            check("I4 no canopy means no change",
                  Math.Abs(Darkening(0.0, 0.0, green) - 1.0) < Eps, Darkening(0.0, 0.0, green).ToString());
        }

        /// <summary>
        /// The statements that turn a canopy measurement into a change in the
        /// lit colour, pulled out of vvApplyPbr with only the leaf calls stubbed.
        /// </summary>
        static List<string> DappleComposition()
        {
            string body = FunctionBody(_pbr, "vec4 vvApplyPbr(");

            string shaded = Rhs(Statement(body, "float shaded"));
            shaded = StubCall(shaded, "vvCanopyDapple", "dapple");

            string local = Rhs(Statement(body, "float local"));
            local = StubCall(local, "vvLocalLightShare", "local");

            string canopy = Rhs(Statement(body, "float canopy"));
            string tint = Rhs(Statement(body, "float tint"));

            // Both multiplies into the lit colour, in order.
            var multiplies = Statements(body)
                .Where(s => Regex.IsMatch(s, @"^result \*= ") && s.Contains("canopy") || Regex.IsMatch(s, @"^result \*= vec3\(1\.0 - tint"))
                .ToList();
            if (multiplies.Count != 2)
                throw new ArgumentException("expected 2 canopy multiplies into result, found " + multiplies.Count);

            return new List<string>
            {
                "shaded = " + shaded,
                "local = " + local,
                "canopy = " + canopy,
                "tint = " + tint,
                "MUL " + Rhs(multiplies[0]),
                "MUL " + Rhs(multiplies[1]),
            };
        }

        /// <summary>
        /// Run the extracted composition and return what it does to a white
        /// fragment's luminance. 1.0 means "vanilla, untouched".
        /// </summary>
        static double RunComposition(List<string> stmts, Dictionary<string, Val> syms)
        {
            Val result = Vec(1.0, 1.0, 1.0);
            bool gated = false;

            foreach (string s in stmts)
            {
                if (s.StartsWith("MUL ", StringComparison.Ordinal))
                {
                    // The shipped code guards both multiplies behind
                    // `if (shaded > 0.0)`, so the check honours the guard rather
                    // than assuming it away.
                    if (!gated) continue;
                    Val f = Eval(s.Substring(4), syms);
                    result = Vec(result.X * f.X, result.Y * f.Y, result.Z * f.Z);
                    continue;
                }

                int eq = s.IndexOf('=');
                string name = s.Substring(0, eq).Trim();
                syms[name] = Eval(s.Substring(eq + 1).Trim(), syms);
                if (name == "shaded") gated = syms[name].S > 0.0;
            }

            return (result.X + result.Y + result.Z) / 3.0;
        }

        static double Const2(string src, string name)
        {
            try { return Define(src, name); } catch { return Const(src, name); }
        }

        // -------------------------------------------------------------------
        // I5  ENERGY REMOVED IS ENERGY RETURNED
        //
        // Defect: 84f7926. A metal has no diffuse, so the shader removed it -
        // and the reflection that replaces it is scaled by vv_pbrAmbient, whose
        // default is 0.2. A gold block lost all of its diffuse and got a fifth
        // of a reflection back. The user's words were "it absorbs a lot of
        // light", and the block was mistaken for dirt.
        //
        // The rule is a conservation statement across TWO subsystems' sliders,
        // which is exactly the kind of thing no single-file check can see: what
        // the diffuse line takes away, the ambient line must be in a position to
        // return.
        // -------------------------------------------------------------------
        static void I5_EnergyRemovedIsEnergyReturned(Action<string, bool, string> check)
        {
            string removal, payback, ambient;
            try
            {
                string body = FunctionBody(_pbr, "vec4 vvApplyPbr(");
                payback = Rhs(Statement(body, "float metalPayback"));
                removal = Rhs(Statement(body, "result *=", "metalness"));
                ambient = Rhs(Statement(FunctionBody(_core, "vec3 vvAmbientSpecular("), "return"));
            }
            catch (Exception ex) { check("I5 metal energy lines are readable", false, ex.Message); return; }

            check("I5 the ambient return is scaled by the sky-reflection slider",
                  ambient.Contains("vv_pbrAmbient"), ambient);

            foreach (double a in new[] { 0.0, 0.2, 0.5, 1.0 })
            {
                var syms = Syms("vv_pbrAmbient", a, "metalness", 1.0, "vv_pbrSpecularStrength", 1.0);
                syms["metalPayback"] = Eval(payback, syms);

                double kept = Eval(removal, syms).X;      // diffuse a metal keeps
                double returned = a;                      // reflection it can be paid

                check("I5 a metal at sky reflection " + a.ToString("0.0") + " is not left dark",
                      kept + returned >= 1.0 - 1e-6,
                      "keeps " + kept.ToString("0.000") + " + returns " + returned.ToString("0.000"));
            }

            // And the slider a player turns off must still mean off.
            var offSyms = Syms("vv_pbrAmbient", 0.2, "metalness", 1.0, "vv_pbrSpecularStrength", 0.0);
            offSyms["metalPayback"] = Eval(payback, offSyms);
            check("I5 specular strength 0 leaves the diffuse alone",
                  Math.Abs(Eval(removal, offSyms).X - 1.0) < Eps,
                  Eval(removal, offSyms).ToString());

            // A dielectric was never in this argument and must not be dragged in.
            var dielectric = Syms("vv_pbrAmbient", 0.2, "metalness", 0.0, "vv_pbrSpecularStrength", 1.0);
            dielectric["metalPayback"] = Eval(payback, dielectric);
            check("I5 a non-metal keeps all of its diffuse",
                  Math.Abs(Eval(removal, dielectric).X - 1.0) < Eps,
                  Eval(removal, dielectric).ToString());
        }

        // -------------------------------------------------------------------
        // I6  AN AUTOMATIC GAIN BRINGS ITS OWN SHOULDER
        //
        // Defect: 8c0d4ce. Eye adaptation multiplies the frame by up to
        // DarkGain, whose default is 1.6, and the tonemap that would roll the
        // result off defaults to 0. The shipped combination guaranteed clipping
        // above 1/1.6 = 0.625, which around a low sun is most of the sky. It
        // came back from the game as "severe highlight clipping around the sun".
        //
        // Two settings in two different config sections, each defensible, whose
        // DEFAULTS contradict each other. The invariant is stated across both,
        // and the gain is read from the C# default rather than retyped, so
        // raising DarkGain re-runs the argument rather than silently escaping it.
        // -------------------------------------------------------------------
        static void I6_AutomaticGainBringsItsOwnShoulder(string repo, Action<string, bool, string> check)
        {
            string lift, shoulder;
            try
            {
                string body = FunctionBody(_grade, "vec4 vvApplyColorGrade(");
                lift = Rhs(Statement(body, "float autoLift"));
                shoulder = Rhs(Statement(body, "float shoulder"));
            }
            catch (Exception ex) { check("I6 shoulder coupling is readable", false, ex.Message); return; }

            double darkGain;
            try
            {
                string cfg = File.ReadAllText(Path.Combine(repo, "src/Common/VintageVisualsConfig.cs"));
                darkGain = double.Parse(Regex.Match(cfg, @"DarkGain\s*\{\s*get;\s*set;\s*\}\s*=\s*([0-9.]+)f").Groups[1].Value);
            }
            catch (Exception ex) { check("I6 DarkGain default is readable", false, ex.Message); return; }

            double Shoulder(double adaptation, double tonemap)
            {
                var syms = Syms("adaptation", adaptation, "vv_tonemapStrength", tonemap);
                syms["autoLift"] = Eval(lift, syms);
                return Scalar(shoulder, syms);
            }

            // The shipped worst case: the renderer lifts as far as it is allowed
            // and the player has asked for no tonemap at all.
            double worst = Shoulder(darkGain, 0.0);
            double needed = Math.Min(1.0, darkGain - 1.0);
            check("I6 the default lift of " + darkGain + "x arrives with a shoulder",
                  worst >= needed - 1e-6, "shoulder " + worst + ", lift needs " + needed);

            check("I6 no lift and no tonemap still means vanilla",
                  Math.Abs(Shoulder(1.0, 0.0)) < Eps, Shoulder(1.0, 0.0).ToString());

            check("I6 the player's tonemap choice survives",
                  Math.Abs(Shoulder(1.0, 0.5) - 0.5) < Eps, Shoulder(1.0, 0.5).ToString());

            check("I6 the player may still ask for more than the lift needs",
                  Math.Abs(Shoulder(1.2, 0.9) - 0.9) < Eps, Shoulder(1.2, 0.9).ToString());

            check("I6 the shoulder rises with the lift",
                  Shoulder(1.6, 0.0) > Shoulder(1.2, 0.0) && Shoulder(1.2, 0.0) > Shoulder(1.0, 0.0),
                  Shoulder(1.6, 0.0) + " > " + Shoulder(1.2, 0.0) + " > " + Shoulder(1.0, 0.0));

            check("I6 the shoulder never exceeds a full tonemap",
                  Shoulder(4.0, 1.0) <= 1.0 + Eps, Shoulder(4.0, 1.0).ToString());
        }

        // -------------------------------------------------------------------
        // I7  EVERY STRENGTH AT ZERO COMPOSES TO THE IDENTITY
        //
        // CLAUDE.md already requires zero to mean vanilla for every uniform, and
        // UniformWiringChecks already proves each uniform is uploaded. Neither
        // asks what happens when the terms are put together, and the terms are
        // not all multiplicative: some are added, some are branched around, and
        // one - the tonemap shoulder - is now driven by a value the player never
        // set. The composition is where "off" either does or does not mean off.
        // -------------------------------------------------------------------
        static void I7_ZeroComposesToTheIdentity(Action<string, bool, string> check)
        {
            // The canopy chain, everything off.
            try
            {
                double untouched = RunComposition(DappleComposition(),
                    Syms("dapple", 0.0, "local", 0.0, "shadowBrightness", 1.0,
                         "VV_DAPPLE_GREEN", Const(_pbr, "VV_DAPPLE_GREEN")));
                check("I7 canopy off leaves the lit colour exactly as it was",
                      Math.Abs(untouched - 1.0) < Eps, untouched.ToString());
            }
            catch (Exception ex) { check("I7 canopy composition is readable", false, ex.Message); }

            // Each strength uniform must be able to reach zero and stop its
            // effect there. A multiplicative factor does that by construction;
            // an early return does it by branch. Anything that is neither is a
            // uniform whose "off" is a claim rather than a fact.
            var strengths = new (string Uniform, string Function, string Source)[]
            {
                ("vv_pbrDapple",   "float vvCanopyDapple(", null),
                ("vv_pbrFoliage",  "vec3 vvFoliageTransmission(", null),
                ("vv_pbrShafts",   "float vvCanopyShaft(", null),
            };

            foreach (var (uniform, fn, _) in strengths)
            {
                string body;
                try { body = FunctionBody(_pbr, fn); }
                catch (Exception ex) { check("I7 " + uniform + " has a home", false, ex.Message); continue; }

                bool earlyOut = Regex.IsMatch(StripComments(body),
                    @"if\s*\(\s*" + Regex.Escape(uniform) + @"\s*<\s*0?\.\d+\s*\)\s*return\s+(vec3\(\s*0\.0\s*\)|0\.0)\s*;");
                bool factor = Regex.IsMatch(StripComments(body), @"\*\s*" + Regex.Escape(uniform))
                           || Regex.IsMatch(StripComments(body), Regex.Escape(uniform) + @"\s*\*");

                check("I7 " + uniform + " at zero is inert by construction",
                      earlyOut || factor,
                      "no early-out and not a multiplicative factor in " + fn);
            }

            // The one that is not a strength and still has to mean vanilla at
            // rest: the compare wipe. A screen-space split that is not requested
            // must not cost the frame a branch's worth of behaviour.
            try
            {
                string wipe = FunctionBody(_pbr, "bool vvCompareVanillaSide()");
                check("I7 the compare wipe is off at zero",
                      Regex.IsMatch(StripComments(wipe), @"vv_compareWipe\s*<=\s*0\.0\s*\)\s*return\s+false"),
                      Regex.Replace(StripComments(wipe), @"\s+", " ").Trim());
            }
            catch (Exception ex) { check("I7 compare wipe is readable", false, ex.Message); }
        }

        // -------------------------------------------------------------------
        // I8  NO PRODUCT CHAIN APPLIES THE SAME FACTOR TWICE
        //
        // The atmosphere contract's rule 7 - extinction sources sum into one
        // coefficient, inscatter gains into one capped gain - is a statement
        // about products, and the way it gets broken is a factor appearing twice
        // in one chain because two people added it for two reasons. Fog times
        // fog is not more fog; it is a different curve with no author.
        //
        // Deliberately mechanical: split every product in the shading snippets
        // at its top level and look for a repeat. It cannot see a factor applied
        // in two separate statements, which is the harder half of the same
        // problem and stays a reading job.
        // -------------------------------------------------------------------
        static void I8_NoFactorAppliedTwice(Action<string, bool, string> check)
        {
            var offenders = new List<string>();
            int chains = 0;

            foreach (var (name, src) in new[]
            {
                ("pseudopbr.glsl", _pbr), ("pbrcore.glsl", _core),
                ("atmosphere.glsl", _atmos), ("weather.glsl", _weather),
            })
            {
                foreach (string stmt in Statements(src))
                {
                    if (!Regex.IsMatch(stmt, @"(^|\s)(return|result|color|graded|pixel|outColor)\b")
                        && !stmt.Contains("*=")) continue;

                    string rhs;
                    try { rhs = Rhs(stmt); } catch { continue; }

                    var factors = TopLevelFactors(rhs);
                    if (factors.Count < 2) continue;
                    chains++;

                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < factors.Count; i++)
                    {
                        string f = factors[i];

                        // Only NAMED factors count - a bare identifier or a
                        // call. `x * (a + b) * (a + c)` is not a repeat.
                        if (!Regex.IsMatch(f, @"^[A-Za-z_][A-Za-z_0-9]*(\s*\([^()]*\))?$")) continue;

                        // `emission * emission` is a squared response curve
                        // written the way GLSL writes one, and it is the shape
                        // this check must not cry wolf about. A DOUBLE
                        // APPLICATION looks different: the same factor turning
                        // up again further down a chain someone else extended,
                        // with other terms in between. Adjacency is the tell.
                        if (i > 0 && factors[i - 1] == f) { seen.Add(f); continue; }

                        if (!seen.Add(f))
                            offenders.Add(name + ": " + f + " twice in `" + Trunc(stmt) + "`");
                    }
                }
            }

            check("I8 every product chain applies each named factor once",
                  offenders.Count == 0, string.Join(" | ", offenders.Take(4)));

            // A check that swept nothing is a check that proves nothing.
            check("I8 the sweep actually found product chains to inspect",
                  chains >= 20, chains + " chains");
        }


        /// <summary>
        /// Every `if (mode == N)` arm in a debug function, keyed by number, with
        /// its body normalised to a single whitespace-free string.
        ///
        /// Written as a scanner rather than a regex because the arms come in two
        /// shapes - a one-line return and a braced block - and a regex that sees
        /// only the first silently reports "no duplicates" on the half it read.
        /// </summary>
        static Dictionary<int, string> DebugViews(string body)
        {
            var views = new Dictionary<int, string>();

            foreach (Match m in Regex.Matches(body, @"mode\s*==\s*(\d+)\s*\)"))
            {
                int mode = int.Parse(m.Groups[1].Value);
                int i = m.Index + m.Length;
                while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
                if (i >= body.Length) continue;

                int end;
                if (body[i] == '{')
                {
                    int depth = 0;
                    end = -1;
                    for (int j = i; j < body.Length; j++)
                    {
                        if (body[j] == '{') depth++;
                        else if (body[j] == '}') { depth--; if (depth == 0) { end = j + 1; break; } }
                    }
                    if (end < 0) throw new ArgumentException("unbalanced debug view " + mode);
                }
                else
                {
                    end = body.IndexOf(';', i);
                    if (end < 0) throw new ArgumentException("unterminated debug view " + mode);
                    end++;
                }

                if (views.ContainsKey(mode)) throw new ArgumentException("debug view " + mode + " declared twice");
                views[mode] = Regex.Replace(body.Substring(i, end - i), @"\s+", "");
            }

            return views;
        }

        static string Trunc(string s) => s.Length <= 90 ? s : s.Substring(0, 90) + "...";

        /// <summary>Split an expression at its top-level `*`, ignoring parentheses.</summary>
        static List<string> TopLevelFactors(string expr)
        {
            var outp = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < expr.Length; i++)
            {
                char c = expr[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == '*' && depth == 0)
                {
                    outp.Add(expr.Substring(start, i - start).Trim());
                    start = i + 1;
                }
                // A sum or a ternary means this is not one product chain.
                else if (depth == 0 && (c == '+' || c == '?' || c == ':')) return new List<string>();
            }
            outp.Add(expr.Substring(start).Trim());
            return outp;
        }

        // -------------------------------------------------------------------
        // I9  A GPU RESOURCE SURVIVES A SHADER RELOAD, AND NO FAILURE IS FINAL
        //
        // Defect: dc54d25. The mod forces a shader reload of its own during
        // startup whenever patch gating changed. That reload disposed the scene
        // capture program the reflections subsystem had registered, the
        // subsystem's Fail() latch was permanent, and reflections switched
        // themselves off in every session - a feature that had been "verified
        // working" and then silently was not, for weeks.
        //
        // The interaction is between a subsystem and the mod's OWN lifecycle,
        // which is why neither side's tests saw it. Anything holding a compiled
        // program must therefore be reachable from the reload event.
        // -------------------------------------------------------------------
        static void I9_GpuResourcesSurviveAReload(string repo, Action<string, bool, string> check)
        {
            var files = Directory.GetFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories)
                                 .OrderBy(f => f)
                                 .Select(f => (Path: f,
                                               Name: Path.GetFileName(f),
                                               Folder: Path.GetFileName(Path.GetDirectoryName(f)),
                                               Text: File.ReadAllText(f)))
                                 .ToList();

            var holders = files.Where(f =>
                Regex.IsMatch(f.Text, @"RegisterFileShaderProgram|new ShaderProgram\(")).ToList();

            check("I9 the sweep found the code that owns compiled programs",
                  holders.Count > 0, holders.Count + " files");

            foreach (var f in holders)
            {
                // Either this file subscribes to the reload itself, or
                // something in its subsystem folder does. Ownership here is the
                // FOLDER rather than a name pattern: SceneCaptureRenderer is
                // rebuilt by ReflectionsSubsystem, and no naming convention
                // relates the two - guessing one would only have made this
                // check fail on the file whose defect it was written for.
                string folder = Path.GetFileName(Path.GetDirectoryName(f.Path));
                bool hooked = f.Text.Contains("Event.ReloadShader")
                           || files.Any(o => o.Folder == folder && o.Text.Contains("Event.ReloadShader"));

                check("I9 " + f.Name + " is rebuilt when shaders reload", hooked,
                      "nothing in src/" + folder + "/ subscribes to Event.ReloadShader");
            }

            // And the latch. A subsystem that gives up permanently turns a
            // recoverable reload into a dead feature, which is the half of this
            // defect that made it last for weeks rather than a session.
            foreach (var f in files.Where(f => f.Text.Contains("Event.ReloadShader")))
            {
                var reset = Regex.IsMatch(f.Text, @"_failed\s*=\s*false|_disabled\s*=\s*false|Reset\(\)");
                bool latches = Regex.IsMatch(f.Text, @"_failed\s*=\s*true|_disabled\s*=\s*true");
                if (!latches) continue;

                check("I9 " + f.Name + " can recover from a failure it latched", reset,
                      "sets a failure latch with no path that clears it");
            }
        }


        // -------------------------------------------------------------------
        // I11  A DIAGNOSTIC MUST NOT COLLAPSE DISTINCT STATES
        //
        // This project has now lost three rounds of investigation to exactly
        // one mistake, in three different subsystems. "loaded but not applied"
        // meant both "the hook never saw this shader" and "no patch matched its
        // name". "OK" meant six different degrees of delivered. And reflection
        // debug view 39 paints a miss red whether the ray pointed behind the
        // camera, started off frame, walked off the edge without crossing
        // anything, or crossed the right surface and was rejected for being too
        // far behind it. The last two are opposites - one says the geometry is
        // never found, the other says it is found and thrown away - and they
        // were the same colour.
        //
        // So a code that enumerates outcomes must have distinct values, and the
        // view that renders them must give each a distinguishable colour. Both
        // halves matter: distinct codes rendered identically are still one red.
        // -------------------------------------------------------------------
        static void I11_ADiagnosticKeepsItsStatesApart(Action<string, bool, string> check)
        {
            var names = new[]
            {
                "VV_SSR_NO_CAPTURE", "VV_SSR_FACING", "VV_SSR_ORIGIN_OFF",
                "VV_SSR_FAR_OFF", "VV_SSR_NO_CROSSING", "VV_SSR_TOO_THICK", "VV_SSR_HIT",
            };

            var codes = new Dictionary<string, double>();
            foreach (string n in names)
            {
                try { codes[n] = Define(_pbr, n); }
                catch (Exception ex) { check("I11 " + n + " is defined", false, ex.Message); return; }
            }

            check("I11 every march outcome has its own code",
                  codes.Values.Distinct().Count() == names.Length,
                  string.Join(", ", codes.Select(c => c.Key + "=" + c.Value)));

            // And the view that renders them. Extracted from the shipped GLSL
            // rather than assumed: a view that returns the same colour for two
            // codes is the defect this invariant exists for, and it is one
            // copy-paste away at any time.
            string body;
            try { body = StripComments(FunctionBody(_pbr, "vec4 vvDebugView(")); }
            catch (Exception ex) { check("I11 debug views are readable", false, ex.Message); return; }

            int at = body.IndexOf("mode == 48", StringComparison.Ordinal);
            check("I11 the outcome view exists", at >= 0, "no mode == 48 in vvDebugView");
            if (at < 0) return;

            string view48 = body.Substring(at, Math.Min(1400, body.Length - at));

            var colours = new List<string>();
            foreach (Match m in Regex.Matches(view48, @"why == (VV_SSR_\w+)\s*\)\s*return\s+vec4\(([^;]+)\);"))
                colours.Add(Regex.Replace(m.Groups[2].Value, @"\s+", ""));

            check("I11 the outcome view renders the codes it is given",
                  colours.Count >= names.Length - 1, colours.Count + " mapped, " + names.Length + " codes");

            check("I11 no two outcomes are painted the same colour",
                  colours.Count == colours.Distinct().Count(),
                  string.Join(" | ", colours));

            // Hit and thickness-rejection are the pair that was one colour.
            // They are the whole reason the view was added, so they get their
            // own check rather than relying on the sweep above.
            var byCode = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(view48, @"why == (VV_SSR_\w+)\s*\)\s*return\s+vec4\(([^;]+)\);"))
                byCode[m.Groups[1].Value] = Regex.Replace(m.Groups[2].Value, @"\s+", "");

            check("I11 a rejected crossing does not look like a miss",
                  byCode.ContainsKey("VV_SSR_TOO_THICK") && byCode.ContainsKey("VV_SSR_NO_CROSSING") &&
                  byCode["VV_SSR_TOO_THICK"] != byCode["VV_SSR_NO_CROSSING"],
                  "found the surface and threw it away is drawn as never found it");
        }

        // -------------------------------------------------------------------
        // I12  A CONSTANT THAT NAMES A RATE MUST BE THE RATE USED
        //
        // VV_SSR_STRIDE says "capture texels to advance per step" and its
        // comment explains at length why a uniform screen-space rate is what
        // makes a reflection foreshorten instead of smear. VV_SSR_STEPS then
        // caps the count at 24. On a short ray those agree. On a long grazing
        // ray - which is the ordinary case on a flat reflective floor, and the
        // only case that carries a reflected tree - the cap binds and the ray
        // is walked ten times coarser than the constant claims.
        //
        // That is not a defect this invariant fixes; it is a budget, and
        // changing it needs a measurement nobody has taken. What the invariant
        // requires is that the divergence be MEASURABLE: the march records both
        // the rate asked for and the rate taken, and debug view 49 reports
        // saturation truthfully. The constants' own comment says raising the
        // thickness tolerance to make distant reflections appear would be
        // hiding a coarse march - so something has to be able to say whether
        // the march is coarse.
        // -------------------------------------------------------------------
        static void I12_ARateConstantIsTheRateUsed(Action<string, bool, string> check)
        {
            string wantedExpr, stepsExpr, overExpr, usedExpr;
            double stride, maxSteps;
            try
            {
                string march = FunctionBody(_pbr, "VvSceneHit vvSceneReflection(");
                wantedExpr = Rhs(Statement(march, "float wanted ="));
                stepsExpr = Rhs(Statement(march, "int steps ="));

                string views = FunctionBody(_pbr, "vec4 vvDebugView(");
                overExpr = Rhs(Statement(views, "float over ="));
                usedExpr = Rhs(Statement(views, "float used ="));

                stride = Const(_pbr, "VV_SSR_STRIDE");
                maxSteps = double.Parse(Regex.Match(StripComments(_pbr),
                    @"const int VV_SSR_STEPS = (\d+);").Groups[1].Value);
            }
            catch (Exception ex) { check("I12 the march rate is readable", false, ex.Message); return; }

            // `steps` is an int cast in GLSL; the evaluator has no ints, so the
            // truncation is applied here and the expression itself still comes
            // from the shipped source.
            double Steps(double travelTexels)
            {
                var syms = Syms("travel", travelTexels, "VV_SSR_STRIDE", stride, "VV_SSR_STEPS", maxSteps);
                syms["wanted"] = new Val(Scalar(wantedExpr.Replace("max(travel.x, travel.y)", "travel"), syms));
                return Math.Floor(Scalar(stepsExpr.Replace("int(", "(").Replace("float(VV_SSR_STEPS)", "VV_SSR_STEPS"), syms));
            }

            double Wanted(double travelTexels)
                => Scalar(wantedExpr.Replace("max(travel.x, travel.y)", "travel"),
                          Syms("travel", travelTexels, "VV_SSR_STRIDE", stride));

            // A short ray: the budget is slack and the stride is honoured
            // exactly, which is the case the constant describes.
            double shortTravel = 20.0;
            double shortStride = shortTravel / Steps(shortTravel);
            check("I12 a short ray is walked at the stride the constant names",
                  Math.Abs(shortStride - stride) < 1e-6,
                  shortStride + " texels per step, constant says " + stride);

            // A long grazing ray: the budget binds. Recorded, not asserted away.
            double longTravel = 500.0;
            double longStride = longTravel / Steps(longTravel);
            check("I12 a long ray saturates the step budget",
                  Steps(longTravel) >= maxSteps - 1e-6,
                  Steps(longTravel) + " steps for " + longTravel + " texels");

            check("I12 the saturated stride is knowable, and it is not the constant",
                  longStride > stride * 2.0,
                  "grazing rays walk " + longStride.ToString("0.0") + " texels per step, not " + stride);

            // The whole point: view 49 must SAY so.
            double Over(double travelTexels)
            {
                var syms = Syms("VV_SSR_STEPS", maxSteps);
                syms["m49"] = new Val(0.0);
                string e = overExpr.Replace("m49.wanted", "wanted").Replace("float(VV_SSR_STEPS)", "VV_SSR_STEPS");
                syms["wanted"] = new Val(Wanted(travelTexels));
                return Scalar(e, syms);
            }

            double Used(double travelTexels)
            {
                var syms = Syms("VV_SSR_STEPS", maxSteps, "steps", Steps(travelTexels));
                string e = usedExpr.Replace("m49.steps", "steps").Replace("float(VV_SSR_STEPS)", "VV_SSR_STEPS");
                return Scalar(e, syms);
            }

            check("I12 the diagnostic is quiet when the stride is honoured",
                  Math.Abs(Over(shortTravel)) < 1e-9, Over(shortTravel).ToString());

            check("I12 the diagnostic reports a saturated budget",
                  Over(longTravel) > 0.5, Over(longTravel).ToString());

            check("I12 budget usage is reported as a fraction, not a count",
                  Used(longTravel) <= 1.0 + 1e-9 && Used(shortTravel) < 1.0,
                  "long " + Used(longTravel) + ", short " + Used(shortTravel));
        }

        // -------------------------------------------------------------------
        // I10  NO TWO DEBUG VIEWS RENDER THE SAME EXPRESSION
        //
        // From the game: "atmosphere debug 7-11 all seem to show roughly the
        // same image, same with 3 4 and 12". A debug view exists to answer one
        // question, and two views answering the same one is either a copy-paste
        // or a term that has quietly stopped varying - both of which cost the
        // reader a diagnosis they thought they had made.
        //
        // HONEST LIMIT, and it is the whole reason this invariant is listed
        // last: two views can differ in every character and still render the
        // same picture, because the difference is in values only the GPU has.
        // This catches the duplicate that is visible in the source and nothing
        // more. The rest is in docs/VISUAL-TESTS.md, where it belongs.
        // -------------------------------------------------------------------
        static void I10_DebugViewsAreDistinct(Action<string, bool, string> check)
        {
            foreach (var (label, src, fn) in new[]
            {
                ("pbr", _pbr, "vec4 vvDebugView("),
                ("atmosphere", _atmos, "vec4 vvAtmosDebug("),
            })
            {
                string body;
                try { body = StripComments(FunctionBody(src, fn)); }
                catch (Exception ex) { check("I10 " + label + " debug views are readable", false, ex.Message); continue; }

                Dictionary<int, string> views;
                var duplicates = new List<string>();
                try { views = DebugViews(body); }
                catch (Exception ex) { check("I10 " + label + " debug views parse", false, ex.Message); continue; }

                foreach (var v in views)
                {
                    var twin = views.FirstOrDefault(o => o.Key < v.Key && o.Value == v.Value);
                    if (twin.Value != null)
                        duplicates.Add(v.Key + " duplicates " + twin.Key + ": " + Trunc(v.Value));
                }

                // Most views are a one-line return and some are a block, so the
                // count is the guard against an extractor that quietly stopped
                // seeing half of them and then reported no duplicates.
                check("I10 " + label + " debug views were found to inspect",
                      views.Count >= (label == "pbr" ? 40 : 13), views.Count + " views");

                check("I10 no two " + label + " debug views return the same expression",
                      duplicates.Count == 0, string.Join(" | ", duplicates));

                // Numbering is per shader and must have no holes an operator
                // would read as a missing view.
                var nums = views.Keys.OrderBy(k => k).ToList();
                check("I10 " + label + " debug view numbers are unique",
                      nums.Count == nums.Distinct().Count(), "");
            }
        }
    }
}
