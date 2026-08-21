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

            var declared = Regex.Matches(snippets, @"^uniform\s+(?:float|vec2|vec3|vec4)\s+(vv_\w+)\s*(?:\[[^\]]*\])?\s*;", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            check("the shaders declare uniforms to check", declared.Count > 0, declared.Count.ToString());

            // The name has to reach an actual Uniform() call, not merely exist
            // as a const. A declared-but-unused constant is exactly the bug.
            //
            // Uniforms4 counts too - it is how an array of vec4s is pushed, and
            // a check that only knew about Uniform() would report the cloud tile
            // window as never uploaded.
            var uploads = Regex.Matches(binders, @"Uniforms?4?\(\s*(\w+)\s*,")
                .Select(m => m.Groups[1].Value)
                .ToHashSet();

            // SetIfPresent(program, NameUniform, value) is an upload too. It is
            // how the shared scene block is pushed, since a snippet injected
            // into five programs is only partly present in each of them, and
            // this check reported all thirteen of those uniforms as dead the
            // moment they moved behind it.
            foreach (Match m in Regex.Matches(binders, @"SetIfPresent\(\s*\w+\s*,\s*(\w+)\s*,"))
            {
                uploads.Add(m.Groups[1].Value);
            }

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

            // ---------------------------------------------------------------
            // A SHARED SNIPPET NEEDS A SHARED UPLOAD PATH
            // ---------------------------------------------------------------
            //
            // The global check above only asks whether SOMETHING uploads each
            // uniform. That is not enough for a snippet injected into several
            // programs: three of scene.glsl's uniforms were uploaded ad hoc by
            // the terrain and entity paths, so the particle programs - which
            // inject the same snippet - never received them. vv_sceneDayLight
            // read as zero, zero multiplied the visibility term, and particle
            // specular was silently dead.
            //
            // The contract is simple enough to enforce: every uniform declared
            // in scene.glsl is uploaded by UploadScene, and by nothing else.
            // UploadScene is called from every path, so that makes the coverage
            // structural rather than something to remember.
            string sceneGlsl = File.ReadAllText(Path.Combine(snippetDir, "scene.glsl"));

            var sceneUniforms = Regex.Matches(sceneGlsl,
                    @"^uniform\s+(?:float|vec2|vec3|vec4)\s+(vv_\w+)\s*;", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .ToList();

            string binderSource = File.ReadAllText(
                Path.Combine(repo, "src/PseudoPBR/PbrShaderBinder.cs"));

            int uploadSceneStart = binderSource.IndexOf("private void UploadScene(", StringComparison.Ordinal);
            string uploadScene = uploadSceneStart < 0
                ? ""
                : binderSource.Substring(uploadSceneStart,
                    binderSource.IndexOf("\n        }", uploadSceneStart, StringComparison.Ordinal) - uploadSceneStart);

            var constantFor = Regex.Matches(binderSource, @"const string (\w+)\s*=\s*""(vv_\w+)""")
                .ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value);

            var notInUploadScene = sceneUniforms
                .Where(u => !constantFor.TryGetValue(u, out string c) || !uploadScene.Contains(c))
                .ToList();

            check("every scene.glsl uniform is uploaded by UploadScene", notInUploadScene.Count == 0,
                string.Join(", ", notInUploadScene));

            check("UploadScene was found at all", uploadSceneStart >= 0, "");

            // No sentinel, and no unguarded upload.
            //
            // UploadScene used to begin "if (!program.HasUniform(
            // vv_sceneRestraint)) return;" - one uniform standing in for the
            // whole snippet. That is only sound if every program that injects
            // scene.glsl reads that uniform, and only the terrain ones do:
            // vv_sceneRestraint is read from vvSceneVisibilityDampen(), which
            // lives in pseudopbr.glsl alone. In entityanimated, particlescube
            // and particlesquad the compiler removed it, HasUniform said no,
            // and the method returned having uploaded NOTHING - so the fix that
            // moved vv_sceneDayLight into this method moved it behind a guard
            // that was already failing for the path that needed it.
            //
            // The rule that replaces it: ask per name. These two checks pin it,
            // because the sentinel is an easy thing to reintroduce as an
            // optimisation by someone who has not read the paragraph above.
            check("UploadScene has no sentinel early-return",
                !uploadScene.Contains("HasUniform") || uploadScene.Contains("SetIfPresent"),
                "a single HasUniform guard cannot speak for a snippet five programs read differently");

            var unguarded = Regex.Matches(uploadScene, @"^\s*program\.Uniform\(\s*(\w+)",
                                          RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .ToList();

            check("every UploadScene upload is presence-guarded", unguarded.Count == 0,
                string.Join(", ", unguarded));

            // Every path that can reach a shaded program has to call it.
            foreach (string method in new[] { "private bool Upload(", "private void UploadEntities(", "private void UploadParticles(" })
            {
                int at = binderSource.IndexOf(method, StringComparison.Ordinal);
                string body = at < 0 ? "" : binderSource.Substring(at,
                    Math.Min(4000, binderSource.Length - at));

                check("UploadScene is called from " + method.Trim('(').Split(' ').Last(),
                    at >= 0 && body.Contains("UploadScene(program)"), method);
            }

            // pbrcore.glsl is injected into all three shaded programs too, so it
            // has the same contract - every path that can reach a shaded
            // program must upload every uniform it declares. It is checked
            // against the union of the three upload methods rather than against
            // one, because unlike scene.glsl these are genuinely per-path look
            // controls rather than one shared block.
            string coreGlsl = File.ReadAllText(Path.Combine(snippetDir, "pbrcore.glsl"));

            var coreUniforms = Regex.Matches(coreGlsl,
                    @"^uniform\s+(?:float|vec2|vec3|vec4)\s+(vv_\w+)\s*;", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .ToList();

            foreach (string method in new[] { "private bool Upload(", "private void UploadEntities(", "private void UploadParticles(" })
            {
                int at = binderSource.IndexOf(method, StringComparison.Ordinal);
                if (at < 0) continue;

                string body = binderSource.Substring(at, Math.Min(4000, binderSource.Length - at));
                bool callsScene = body.Contains("UploadScene(program)");

                var unreached = coreUniforms
                    .Where(u => constantFor.TryGetValue(u, out string c) &&
                                !body.Contains(c) &&
                                !(callsScene && uploadScene.Contains(c)))
                    .ToList();

                check("every pbrcore.glsl uniform reaches " + method.Trim('(').Split(' ').Last(),
                    unreached.Count == 0, string.Join(", ", unreached));
            }

            check("the material sampler is bound", binders.Contains("BindTexture2D(SamplerUniform"), "");
        }
    }
}
