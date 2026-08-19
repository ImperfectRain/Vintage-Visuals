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
            string snippet = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pseudopbr.glsl"));
            string binder = File.ReadAllText(Path.Combine(repo, "src/PseudoPBR/PbrShaderBinder.cs"));

            // Samplers are bound with BindTexture2D rather than Uniform, so they
            // are checked separately below.
            var declared = Regex.Matches(snippet, @"^uniform\s+float\s+(vv_\w+)\s*;", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            check("the shader declares float uniforms to check", declared.Count > 0, declared.Count.ToString());

            // The name has to reach an actual Uniform() call, not merely exist
            // as a const. A declared-but-unused constant is exactly the bug.
            var uploads = Regex.Matches(binder, @"Uniform\(\s*(\w+)\s*,")
                .Select(m => m.Groups[1].Value)
                .ToHashSet();

            var constants = Regex.Matches(binder, @"const string (\w+)\s*=\s*""(vv_\w+)""")
                .ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value);

            var missing = declared
                .Where(u => !constants.ContainsKey(u) || !uploads.Contains(constants[u]))
                .ToList();

            check("every shader uniform is uploaded by the binder", missing.Count == 0,
                missing.Count == 0
                    ? ""
                    : string.Join(", ", missing.Select(u =>
                        u + (constants.ContainsKey(u) ? " (constant exists, never uploaded)" : " (no constant)"))));

            var orphans = constants.Keys
                .Where(u => !declared.Contains(u) && !snippet.Contains("uniform sampler2D " + u))
                .ToList();

            check("every uploaded uniform is declared in the shader", orphans.Count == 0,
                string.Join(", ", orphans));

            check("the material sampler is bound", binder.Contains("BindTexture2D(SamplerUniform"), "");
        }
    }
}
