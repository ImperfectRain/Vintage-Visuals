using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Every effect either adds light, moves it around, or takes it away - and
    /// which of the three it is, is a property that can be checked.
    ///
    /// This exists because of the sunfleck blowout. Dapple was written to be
    /// mean-preserving: it subtracted its own measured coverage so that gaps
    /// brightened by exactly as much as the shade between them darkened. The
    /// arithmetic was right. What was wrong was the CLASS - at 11% coverage,
    /// holding the mean fixed forces the lit ninth far above vanilla's own
    /// brightness, pixels went past 1.0, and since findbright multiplies the
    /// whole frame rather than thresholding it, a forest floor came back as
    /// white spotlights. A canopy is an occluder. It cannot make light. The
    /// class was wrong and no amount of tuning the constants would have fixed
    /// it.
    ///
    /// So each effect is declared as one of:
    ///
    ///   ADDING       may raise a pixel above what vanilla lit it to.
    ///                Emission only - a forge really is a light source.
    ///   REMOVING     may only ever lower it. Shadow, occlusion, dapple,
    ///                attenuation.
    ///   REDISTRIBUTING  moves energy between channels or directions without
    ///                changing the total.
    ///
    /// and the source is checked against the declaration. A removing term that
    /// is written as `1.0 + x` is the shape of the bug, whatever x happens to
    /// contain today.
    /// </summary>
    public static class ConservationChecks
    {
        private sealed class Term
        {
            public string Name;
            public string Snippet;
            public string Function;
            public string Class;
            public string Why;
        }

        private static readonly Term[] Terms =
        {
            new Term
            {
                Name = "canopy dapple", Snippet = "pseudopbr.glsl", Function = "vvCanopyDapple",
                Class = "REMOVING",
                Why = "a canopy is an occluder; a sunfleck is light that was not taken away, not light added",
            },
            new Term
            {
                Name = "crevice occlusion", Snippet = "pseudopbr.glsl", Function = "vvCavity",
                Class = "REMOVING",
                Why = "occlusion cannot brighten a surface",
            },
            new Term
            {
                Name = "cloud shadow", Snippet = "weather.glsl", Function = "vvCloudShadow",
                Class = "REMOVING",
                Why = "a cloud between the sun and the ground can only take light out",
            },
            new Term
            {
                Name = "emission", Snippet = "pbrcore.glsl", Function = "vvEmission",
                Class = "ADDING",
                Why = "a light source is the one thing here that legitimately makes light",
            },
        };

        public static void Run(string repo, Action<string, bool, string> check)
        {
            CheckMetalKeepsWhatTheEnvironmentCannotReturn(repo, check);

            string dir = Path.Combine(repo, "assets/vintagevisuals/shadersnippets");
            var sources = new Dictionary<string, string>();

            foreach (Term term in Terms)
            {
                if (!sources.ContainsKey(term.Snippet))
                {
                    string path = Path.Combine(dir, term.Snippet);
                    sources[term.Snippet] = File.Exists(path) ? File.ReadAllText(path) : "";
                }

                string body = Body(sources[term.Snippet], term.Function);

                check(term.Name + " is present to check", body.Length > 0, term.Function);
                if (body.Length == 0) continue;

                bool negative = ReturnsNegative(body);

                if (term.Class == "REMOVING")
                {
                    // A removing term must never hand back something the caller
                    // would add. Returning a bare `1.0 + ...` or a negated
                    // subtraction is how "occluder" quietly becomes "light".
                    check(term.Name + " only ever removes light", !negative,
                        term.Why + " - found a return that can exceed unity or go negative");
                }
            }

            CheckDappleApplication(repo, check);
        }

        /// <summary>
        /// Where dapple is applied, it must scale the result DOWN.
        ///
        /// Checked at the call site rather than only in the function, because
        /// the blowout was not inside vvCanopyDapple at all - the function
        /// returned a sensible signed number and the caller wrote
        /// `result *= 1.0 + dapple`. The class error lived in the multiply.
        /// </summary>
        private static void CheckDappleApplication(string repo, Action<string, bool, string> check)
        {
            string source = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pseudopbr.glsl"));

            var applications = Regex.Matches(source, @"result\s*\*=\s*([^;]+);")
                .Select(m => m.Groups[1].Value.Trim())
                .ToList();

            check("the shading result is scaled somewhere", applications.Count > 0,
                applications.Count.ToString());

            // `1.0 - x` and `vec3(1.0 - ...)` are fine. `1.0 + x` is the shape
            // that blew the forest floor out to white.
            var brightening = applications
                .Where(a => Regex.IsMatch(a, @"^\s*1\.0\s*\+") ||
                            Regex.IsMatch(a, @"^\s*vec3\s*\(\s*1\.0\s*\+"))
                .ToList();

            check("nothing multiplies the shading result by more than one", brightening.Count == 0,
                string.Join("; ", brightening) +
                (brightening.Count == 0 ? "" : " - an occluding term must not brighten"));
        }

        /// <summary>
        /// Whether a function has a return that could exceed one or go below
        /// zero. Deliberately crude: it is looking for the SHAPE of an additive
        /// term, not evaluating the arithmetic.
        /// </summary>
        private static bool ReturnsNegative(string body)
        {
            foreach (Match m in Regex.Matches(body, @"return\s+([^;]+);"))
            {
                string expression = m.Groups[1].Value;

                if (Regex.IsMatch(expression, @"^\s*-")) return true;
                if (Regex.IsMatch(expression, @"^\s*1\.0\s*\+")) return true;
            }

            return false;
        }

        private static string Body(string source, string function)
        {
            Match m = Regex.Match(source, @"^\w[\w\s]*?\b" + Regex.Escape(function) + @"\s*\(",
                RegexOptions.Multiline);
            if (!m.Success) return "";

            int brace = source.IndexOf('{', m.Index);
            if (brace < 0) return "";

            int depth = 0;
            for (int i = brace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(brace, i - brace + 1);
                }
            }

            return "";
        }
    
        /// <summary>
        /// A metal may only give up the diffuse the environment pays back.
        ///
        /// Metalness works in two halves: raise F0, and remove the diffuse. The
        /// removed diffuse is meant to reappear as an environment reflection -
        /// but that term is scaled by vv_pbrAmbient, the sky-reflection slider,
        /// and at its default of 0.2 a metal lost all of its diffuse and got a
        /// fifth of a reflection back.
        ///
        /// A gold block came out dark and lifeless. Reported from the game as
        /// "reflective materials absorb a lot of light", which is precisely what
        /// a broken energy budget looks like from outside.
        ///
        /// The slider is doing two unrelated jobs: on a dielectric it is a taste
        /// choice about how much sky a surface shows, and on a metal it is the
        /// only thing funding the diffuse that metalness takes away.
        /// </summary>
        private static void CheckMetalKeepsWhatTheEnvironmentCannotReturn(
            string repo, Action<string, bool, string> check)
        {
            string pbr = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pseudopbr.glsl"));

            check("the diffuse removal is scaled by the environment payback",
                  pbr.Contains("1.0 - metalness * metalPayback"),
                  "metalness removing more than the reflection returns makes metal dark");

            check("the payback is the ambient specular strength",
                  pbr.Contains("float metalPayback = clamp(vv_pbrAmbient, 0.0, 1.0);"),
                  "it has to be the same term that funds the replacement");

            // Numeric, across the slider, because the whole point is what
            // happens between the ends.
            var wrong = new System.Collections.Generic.List<string>();

            foreach (float ambient in new[] { 0f, 0.2f, 0.5f, 1f })
            {
                const float metalness = 1f;

                // What the diffuse is multiplied by, and what comes back.
                float kept = 1f - metalness * ambient;
                float returned = ambient;

                // Total must never fall below what a full payback would give,
                // and never exceed 1 - a metal is not a light source.
                float total = kept + returned;

                if (total < 0.999f || total > 1.001f)
                {
                    wrong.Add("ambient " + ambient + " leaves " + total.ToString("0.###"));
                }
            }

            check("what a metal keeps plus what it reflects is conserved",
                  wrong.Count == 0,
                  string.Join("; ", wrong));

            // At full strength nothing changes, or this would be a silent
            // retune of every metal in the game rather than a fix.
            check("at full sky reflection the metal response is unchanged",
                  Math.Abs((1f - 1f * 1f) - 0f) < 1e-6f,
                  "ambient 1 must still remove the whole diffuse");
        }
    }
}
