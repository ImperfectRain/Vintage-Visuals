using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Every Use() must be paired with a Stop().
    ///
    /// THIS EXISTS BECAUSE IT SHIPPED AND CRASHED A REAL CLIENT.
    ///
    /// IShaderProgram.Use() BINDS the program and THROWS if a different one is
    /// already bound - "Already a different shader (chunkopaque) in use!" - and
    /// the client does not recover from it. AtmosphereShaderBinder uploads to
    /// four programs in a loop and never unbound between them, so the first
    /// iteration bound chunkopaque and the second threw.
    ///
    /// Nothing caught it. 794 static checks passed, all 48 prefix combinations
    /// compiled, and every mutation test in the suite was green, because the
    /// defect is not in any shader and not in any value - it is in a lifecycle
    /// that only exists at runtime. The mod's own CLAUDE.md warns about Use()
    /// throwing, and the binder even carried a guard for the case where SOMEONE
    /// ELSE holds a program. It could not help: by the second iteration the
    /// offending bind was the binder's own.
    ///
    /// The guard that would have helped is this one. It is deliberately crude -
    /// counting a pairing rather than proving control flow - because the bug was
    /// crude: a missing line, in the one binder that iterates more than two
    /// programs.
    /// </summary>
    public static class ShaderBindingChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            var binders = Directory
                .GetFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories)
                .OrderBy(f => f)
                .Select(f => (Path: f, Text: File.ReadAllText(f)))
                .Where(f => f.Text.Contains(".Use()"))
                .ToList();

            check("there are shader binders to check", binders.Count > 0, binders.Count.ToString());

            var unpaired = new List<string>();

            foreach (var binder in binders)
            {
                string name = Path.GetFileName(binder.Path);

                // Comments talk about Use() and Stop() at length in this
                // repository, so they have to come out before counting or the
                // prose swamps the code.
                string code = Strip(binder.Text);

                int uses = Regex.Matches(code, @"\.Use\(\)").Count;
                int stops = Regex.Matches(code, @"\.Stop\(\)").Count;

                if (uses != stops) unpaired.Add(name + ": " + uses + " Use, " + stops + " Stop");
            }

            check("every shader Use() is paired with a Stop()",
                  unpaired.Count == 0,
                  string.Join("; ", unpaired));

            CheckLoopingBindersUnbind(repo, check);
            CheckCompareWipeReusesTheVanillaExit(repo, check);
        }

        /// <summary>
        /// A binder that uploads to several programs in a loop must unbind
        /// inside the loop body, not after it.
        ///
        /// This is the specific shape that crashed. Pairing alone would be
        /// satisfied by a single Stop() after the loop, which still leaves the
        /// second iteration throwing.
        /// </summary>
        private static void CheckLoopingBindersUnbind(string repo, Action<string, bool, string> check)
        {
            string binder = Path.Combine(repo, "src/Atmosphere/AtmosphereShaderBinder.cs");

            check("the atmosphere binder exists", File.Exists(binder), binder);
            if (!File.Exists(binder)) return;

            string code = Strip(File.ReadAllText(binder));

            // The upload helper is called once per program from a foreach. Both
            // the bind and the unbind have to live inside it.
            Match upload = Regex.Match(code,
                @"private bool Upload\([^)]*\)\s*\{(.*?)\n        \}", RegexOptions.Singleline);

            check("the per-program upload helper is findable", upload.Success, "Upload");
            if (!upload.Success) return;

            string body = upload.Groups[1].Value;

            check("the per-program upload binds and unbinds within itself",
                  body.Contains(".Use()") && body.Contains(".Stop()"),
                  "the caller loops over four programs; leaving one bound makes the next Use() throw");

            check("the unbind comes after the bind",
                  body.IndexOf(".Stop()", StringComparison.Ordinal) >
                  body.IndexOf(".Use()", StringComparison.Ordinal),
                  "Stop before Use unbinds nothing and leaves the program bound");

            // The early exits must happen BEFORE the bind, or they leak a bound
            // program - the same crash by a different route.
            int use = body.IndexOf(".Use()", StringComparison.Ordinal);
            var earlyReturns = Regex.Matches(body, @"return false;")
                .Cast<Match>()
                .Where(m => m.Index > use)
                .ToList();

            check("no early exit leaves a program bound",
                  earlyReturns.Count == 0,
                  earlyReturns.Count + " return(s) after Use() without an unbind");
        }

        /// <summary>
        /// The comparison wipe must take the EXISTING vanilla exit, not a path
        /// of its own.
        ///
        /// A second "render it like vanilla" branch is a second thing to be
        /// wrong, and it would be wrong in the worst possible way: silently, in
        /// the one tool whose entire job is to show what the mod changed. If the
        /// wipe drew something subtly different from what "off" draws, every
        /// comparison made with it would be a lie.
        ///
        /// Every injected function already has that exit, because the zero case
        /// of every strength has always had to mean vanilla. The wipe is only
        /// allowed to reach it early.
        /// </summary>
        private static void CheckCompareWipeReusesTheVanillaExit(string repo, Action<string, bool, string> check)
        {
            string pbr = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pseudopbr.glsl"));
            string atmos = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/atmosphere.glsl"));

            check("the wipe takes pseudopbr's own vanilla exit",
                  Regex.IsMatch(pbr, @"if \(vvCompareVanillaSide\(\)\) return litColor;"),
                  "it must return exactly what the disabled case returns");

            // The wipe must be taken BEFORE any shading, or the "vanilla" side
            // would be a partially shaded pixel rather than vanilla's. What
            // matters is that nothing shades in between, not the literal
            // character distance - so this checks the span for shading calls
            // instead of measuring it.
            int disabled = pbr.IndexOf("if (vv_pbrEnabled < 0.5) return litColor;", StringComparison.Ordinal);
            int wipe = pbr.IndexOf("if (vvCompareVanillaSide()) return litColor;", StringComparison.Ordinal);

            check("the wipe is taken after the disabled exit",
                  disabled >= 0 && wipe > disabled,
                  "both early exits must be at the top of vvApplyPbr");

            if (disabled < 0 || wipe < 0) return;

            string between = pbr.Substring(disabled, wipe - disabled);

            check("nothing shades the pixel before the wipe is taken",
                  !between.Contains("vvSampleMaterial") &&
                  !between.Contains("vvSurfaceNormal") &&
                  !between.Contains("vvApplyEnvironmentLayers") &&
                  !between.Contains("result"),
                  "the vanilla side must be vanilla, not a half-shaded pixel");

            // The seam marker lives in that same early block. It is what stops
            // the wipe being mistaken for a rendering defect - which it was.
            check("the seam is marked, so the wipe announces itself",
                  pbr.Contains("if (vvCompareSeam()) return vec4(vec3(0.5), litColor.a);"),
                  "an unmarked wipe is indistinguishable from a renderer boundary");

            check("the seam marker is also taken before any shading",
                  pbr.IndexOf("vvCompareSeam()) return", StringComparison.Ordinal) < wipe,
                  "the marker must not be shaded either");

            check("the atmosphere wipe reproduces vanilla's own fog mix",
                  atmos.Contains("mix(rgbaPixel.rgb, fogColor, clamp(fogWeight, 0.0, 1.0))"),
                  "vanilla's applyFog is mix(pixel, fogColor, fogWeight) and the wipe must be exactly that");

            // Zero has to disable it, like every other value in this mod, and
            // an unset uniform reads as zero.
            check("a wipe of zero is off",
                  pbr.Contains("if (vv_compareWipe <= 0.0) return false;"),
                  "an unset uniform reads as 0 and must mean 'no wipe'");

            check("each patch group declares its own wipe uniform",
                  pbr.Contains("uniform float vv_compareWipe;") &&
                  atmos.Contains("uniform float vv_atmosCompareWipe;"),
                  "a uniform shared across patch groups couples their rollbacks");
        }

        /// <summary>Removes comments so prose about Use() and Stop() is not counted as code.</summary>
        private static string Strip(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"^\s*//.*$", "", RegexOptions.Multiline);
            return text;
        }
    }
}
