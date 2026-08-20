using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Every uniform the shaders declare must actually be uploaded.
    ///
    /// This exists because five of them were not. The constants were declared
    /// in PbrShaderBinder and the GLSL read them, but the lines that push the
    /// values were never added - a string edit that silently matched nothing.
    /// The build was clean, every other test passed, and the effects those
    /// uniforms drove simply did nothing. One of them, vv_pbrDayLight, gates
    /// the specular term, so the entire highlight model read as permanent
    /// night.
    ///
    /// Nothing about that is visible from C# alone: an unset GLSL uniform is
    /// zero, and zero is a legal value for all of them. Only comparing the two
    /// sides catches it.
    /// </summary>
    public static class UniformWiringChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            // Every snippet against every binder. Scoping this to one pair
            // let vv_weatherCover through: it is declared in pseudopbr.glsl and
            // uploaded by WeatherShaderBinder, so a check that knew about only
            // one of the two reported a bug that was not there and would have
            // missed a real one going the other way.
            string snippetDir = Path.Combine(repo, "assets/vintagevisuals/shadersnippets");
            string snippets = string.Join("\n",
                Directory.GetFiles(snippetDir, "*.glsl").OrderBy(f => f).Select(File.ReadAllText));

            string binders = string.Join("\n",
                Directory.GetFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories)
                    .OrderBy(f => f)
                    .Select(File.ReadAllText));

            var declared = Regex.Matches(snippets, @"^uniform\s+(?:float|vec2|vec3|vec4)\s+(vv_\w+)\s*;", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            check("the shaders declare uniforms to check", declared.Count > 0, declared.Count.ToString());

            // The name has to reach an actual Uniform() call, not merely exist
            // as a const. A declared-but-unused constant is exactly the bug.
            var uploads = Regex.Matches(binders, @"Uniform\(\s*(\w+)\s*,")
                .Select(m => m.Groups[1].Value)
                .ToHashSet();

            var constants = Regex.Matches(binders, @"const string (\w+)\s*=\s*""(vv_\w+)""")
                .ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value, StringComparer.Ordinal);

            // ColorGrade names its uniforms inline rather than through
            // constants, which is just as valid - what matters is that the name
            // reaches a Uniform call, not how it got there.
            var literals = Regex.Matches(binders, @"Uniform\(\s*""(vv_\w+)""")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            var missing = declared
                .Where(u => !literals.Contains(u) &&
                            (!constants.ContainsKey(u) || !uploads.Contains(constants[u])))
                .ToList();

            check("every shader uniform is uploaded by some binder", missing.Count == 0,
                missing.Count == 0
                    ? ""
                    : string.Join(", ", missing.Select(u =>
                        u + (constants.ContainsKey(u) ? " (constant exists, never uploaded)" : " (no constant)"))));

            var orphans = constants.Keys.Concat(literals)
                .Distinct()
                .Where(u => !declared.Contains(u) && !snippets.Contains("uniform sampler2D " + u))
                .ToList();

            check("every uploaded uniform is declared in a shader", orphans.Count == 0,
                string.Join(", ", orphans));

            check("the material sampler is bound", binders.Contains("BindTexture2D(SamplerUniform"), "");
        }
    }
}
