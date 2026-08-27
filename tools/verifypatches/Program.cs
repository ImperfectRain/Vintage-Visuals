using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VintageVisuals.Common.Patching;
using VintageVisuals.SmokeTest;

// Applies every shipped patch group to the game's OWN shaders and, where
// glslangValidator is available, compiles the result.
//
// tools/smoketest exercises the patch engine against stand-in shaders written
// to look like vanilla. This exercises it against vanilla, which is a different
// question and the one that has actually gone wrong: an anchor can match a
// hand-written fixture perfectly and miss the real file because the game
// reworded a line, moved a declaration, or dropped a function entirely.
//
// Reference shaders come from EnableShaderDebugDump and are gitignored - they
// are Vintage Story's assets, not this project's. See CLAUDE.md.

namespace VintageVisuals.VerifyPatches
{
    static class Program
    {
        static readonly string Repo = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        // The game supplies these as prefix code, so glslang has to be told.
        // Every combination is swept because a patch can compile under one and
        // fail under another - anything guarded by #if is invisible otherwise.
        //
        // Which ones apply is derived per shader rather than fixed, because
        // they are not universal: the terrain shaders want the first five and
        // know nothing of OIT, while cloudvolumetric.fsh will not compile
        // without it. A fixed list made vanilla itself fail to build, which the
        // tool then correctly reported as its own fault rather than the patch's.

        static readonly (string Name, int[] Values)[] CandidateDefines =
        {
            ("SSAOLEVEL", new[] { 0, 1 }),
            ("SHADOWQUALITY", new[] { 0, 1, 2 }),
            ("GODRAYS", new[] { 0, 1 }),
            ("NORMALVIEW", new[] { 0, 1 }),
            ("SHINYEFFECT", new[] { 0, 1 }),
            ("USEOIT", new[] { 0, 1 }),
        };

        /// <summary>The defines a given shader actually mentions.</summary>
        static (string Name, int[] Values)[] DefinesFor(string source)
        {
            return CandidateDefines.Where(d => source.Contains(d.Name)).ToArray();
        }

        static int failures;

        static void Fail(string message)
        {
            Console.WriteLine("  FAIL  " + message);
            failures++;
        }

        static void Pass(string message)
        {
            Console.WriteLine("  ok    " + message);
        }

        static int Main(string[] args)
        {
            string referenceDir = Environment.GetEnvironmentVariable("VINTAGE_VISUALS_SHADERS");
            if (string.IsNullOrEmpty(referenceDir))
            {
                referenceDir = Path.Combine(Repo, "reference", "game-shaders");
            }

            if (!Directory.Exists(referenceDir))
            {
                Console.WriteLine("No reference shaders at " + referenceDir + ".");
                Console.WriteLine("Dump them with EnableShaderDebugDump and every subsystem switched OFF,");
                Console.WriteLine("then copy VintagestoryData/ShaderDebug/ there. See CLAUDE.md.");
                return 2;
            }

            bool haveGlslang = HaveGlslang();
            Console.WriteLine("Reference shaders: " + referenceDir);
            Console.WriteLine(haveGlslang
                ? "glslangValidator found - patched output will be compiled."
                : "glslangValidator NOT found - anchors will be checked but nothing compiled. " +
                  "Install glslang-tools for the full check.");
            Console.WriteLine();

            string patchDir = Path.Combine(Repo, "assets", "vintagevisuals", "shaderpatches");

            string[] yamlPaths = Directory.GetFiles(patchDir, "*.yaml").OrderBy(p => p).ToArray();

            foreach (string yamlPath in yamlPaths)
            {
                VerifyGroup(yamlPath, referenceDir, haveGlslang);
            }

            VerifyCombined(yamlPaths, referenceDir, haveGlslang);

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL PATCHES VERIFIED" : failures + " FAILURE(S)");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Applies EVERY group to each shader at once, the way the game does.
        ///
        /// The per-group passes above are not this check. They apply one group
        /// to vanilla and prove it survives on its own; in a running game all
        /// of them land on the same file together, and a whole class of failure
        /// only exists there - two groups anchoring on the same token, a
        /// declaration made twice, a patch whose anchor was consumed by the
        /// group that ran before it.
        ///
        /// chunkopaque.fsh currently takes three groups. Nothing had ever
        /// compiled them together outside the game.
        /// </summary>
        static void VerifyCombined(string[] yamlPaths, string referenceDir, bool haveGlslang)
        {
            Console.WriteLine("== ALL GROUPS TOGETHER (as the game applies them)");

            var all = new List<ShaderPatch>();
            var groups = new List<string>();

            foreach (string yamlPath in yamlPaths)
            {
                string group = Path.GetFileNameWithoutExtension(yamlPath);
                try
                {
                    all.AddRange(ShaderPatchLoader.ParsePatchFile(
                        File.ReadAllText(yamlPath), group, yamlPath,
                        n => File.ReadAllText(Path.Combine(Repo, "assets/vintagevisuals/shadersnippets", n))));
                    groups.Add(group);
                }
                catch (Exception)
                {
                    // Already reported by the per-group pass.
                }
            }

            foreach (string filename in all.Select(p => p.Filename).Distinct().OrderBy(f => f))
            {
                string path = Path.Combine(referenceDir, filename);
                if (!File.Exists(path)) continue;

                string vanilla = File.ReadAllText(path);
                if (vanilla.Contains("vintagevisuals") || vanilla.Contains("vv_materialTex") ||
                    vanilla.Contains("vvApplyColorGrade"))
                {
                    continue;
                }

                string[] hitting = all.Where(p => p.Filename == filename)
                    .Select(p => p.Group).Distinct().OrderBy(g => g).ToArray();

                if (hitting.Length < 2) continue;

                // A combination the game cannot produce is not a failure.
                //
                // This pass applies every group it finds on disk, which is right
                // for the ones that genuinely stack and wrong for the ones the
                // runtime gating keeps apart. Reporting a compile error for a
                // state that cannot occur is how a real failure gets ignored, so
                // the exclusion is read from the same declaration the gating
                // uses rather than guessed at here.
                string[] exclusive = ShaderPatchGroups.FirstExclusivePair(hitting);
                if (exclusive != null)
                {
                    Console.WriteLine("  skip  " + filename + ": '" + exclusive[0] + "' and '" +
                                      exclusive[1] + "' are mutually exclusive, so the game never " +
                                      "applies both. Verified separately above.");
                    continue;
                }

                var logger = new CollectingLogger();
                var patcher = new ShaderPatcher(logger);
                patcher.SetPatches(all);

                string patched = patcher.Patch(filename, vanilla);

                string unhealthy = hitting.FirstOrDefault(g => !patcher.IsGroupHealthy(g));
                if (unhealthy != null)
                {
                    string reason = logger.Lines.FirstOrDefault(l => l.Contains("anchor not found"))
                                    ?? logger.Lines.FirstOrDefault(l => l.Contains("CRITICAL"))
                                    ?? "(no reason logged)";
                    Fail(filename + " with " + string.Join(" + ", hitting) + ": group '" + unhealthy +
                         "' broke when combined - " + reason.Trim());
                    continue;
                }

                // A uniform declared twice in one translation unit does not
                // compile, and two groups injecting the same shared snippet is
                // exactly how that happens.
                string duplicate = FindDuplicateUniform(patched);
                if (duplicate != null)
                {
                    Fail(filename + " with " + string.Join(" + ", hitting) +
                         ": uniform '" + duplicate + "' is declared more than once");
                    continue;
                }

                string dumpDir = Environment.GetEnvironmentVariable("VINTAGE_VISUALS_DUMP");
                if (!string.IsNullOrEmpty(dumpDir))
                {
                    Directory.CreateDirectory(dumpDir);
                    File.WriteAllText(Path.Combine(dumpDir, "combined." + filename), patched);
                }

                Console.WriteLine("  ok    " + filename + ": " + string.Join(" + ", hitting) +
                                  " all applied together");

                if (haveGlslang) Compile(filename, vanilla, patched);
            }
        }

        /// <summary>Finds a vv_ uniform declared more than once, which never compiles.</summary>
        static string FindDuplicateUniform(string source)
        {
            var seen = new HashSet<string>();

            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(source,
                         @"^uniform\s+\w+\s+(vv_\w+)\s*(?:\[[^\]]*\])?\s*;",
                         System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                if (!seen.Add(m.Groups[1].Value)) return m.Groups[1].Value;
            }

            return null;
        }

        static void VerifyGroup(string yamlPath, string referenceDir, bool haveGlslang)
        {
            string group = Path.GetFileNameWithoutExtension(yamlPath);
            Console.WriteLine("== " + group);

            List<ShaderPatch> patches;
            try
            {
                patches = ShaderPatchLoader.ParsePatchFile(
                    File.ReadAllText(yamlPath), group, yamlPath,
                    n => File.ReadAllText(Path.Combine(Repo, "assets/vintagevisuals/shadersnippets", n))).ToList();
            }
            catch (Exception ex)
            {
                Fail(group + " did not parse: " + ex.Message);
                return;
            }

            foreach (string filename in patches.Select(p => p.Filename).Distinct().OrderBy(f => f))
            {
                string path = Path.Combine(referenceDir, filename);

                if (!File.Exists(path))
                {
                    Fail(filename + " is not in the reference set");
                    continue;
                }

                string vanilla = File.ReadAllText(path);

                // A dump taken with the mod switched on contains the mod's own
                // injections. Patching that would report success while proving
                // nothing, which is worse than having no reference at all.
                if (vanilla.Contains("vintagevisuals") || vanilla.Contains("vv_materialTex") ||
                    vanilla.Contains("vvApplyColorGrade"))
                {
                    Fail(filename + " already contains this mod's injections - it was dumped with the mod " +
                         "switched on. Re-dump with every subsystem off.");
                    continue;
                }

                var logger = new CollectingLogger();
                var patcher = new ShaderPatcher(logger);
                patcher.SetPatches(patches);

                string patched = patcher.Patch(filename, vanilla);

                // Dump on request. "The patch compiles" and "the patched code
                // runs" are different claims, and reading the merged source is
                // the only way to check the second without a GPU - which is how
                // the cloud-shadow wrapper was finally pinned down.
                string dumpDir = Environment.GetEnvironmentVariable("VINTAGE_VISUALS_DUMP");
                if (!string.IsNullOrEmpty(dumpDir))
                {
                    Directory.CreateDirectory(dumpDir);
                    File.WriteAllText(Path.Combine(dumpDir, group + "." + filename), patched);
                }

                if (!patcher.IsGroupHealthy(group))
                {
                    string reason = logger.Lines.FirstOrDefault(l => l.Contains("anchor not found"))
                                    ?? logger.Lines.FirstOrDefault(l => l.Contains("CRITICAL"))
                                    ?? "(no reason logged)";
                    Fail(filename + ": " + reason.Trim());
                    continue;
                }

                if (ReferenceEquals(patched, vanilla) || patched == vanilla)
                {
                    Fail(filename + ": every anchor matched but the source is unchanged");
                    continue;
                }

                Pass(filename + ": all " + patches.Count(p => p.AppliesTo(filename)) + " anchors matched");

                if (haveGlslang) Compile(filename, vanilla, patched);
            }
        }

        static void Compile(string filename, string vanilla, string patched)
        {
            int compared = 0, skipped = 0, patchedFailures = 0;
            string firstError = null;

            foreach (Dictionary<string, int> defines in Combinations(DefinesFor(vanilla)))
            {
                // Vanilla first, and it decides whether the combination counts.
                // Some shaders genuinely do not build under every setting -
                // cloudvolumetric.fsh needs OIT on and defines its own
                // constants when it is - and a configuration the game itself
                // cannot compile is not this patch's to answer for. Comparing
                // against vanilla rather than against an absolute is what keeps
                // the tool from reporting its own gaps as patch failures.
                if (!TryCompile(vanilla, defines, filename, out _))
                {
                    skipped++;
                    continue;
                }

                compared++;

                string error;
                if (!TryCompile(patched, defines, filename, out error))
                {
                    patchedFailures++;
                    if (firstError == null) firstError = Describe(defines) + ": " + error;
                }
            }

            if (compared == 0)
            {
                Fail(filename + ": vanilla compiles in none of the " + skipped +
                     " combinations tried, so the patch could not be checked at all");
                return;
            }

            if (patchedFailures == 0)
            {
                Pass(filename + ": compiles in all " + compared + " combinations vanilla supports" +
                     (skipped > 0 ? " (" + skipped + " skipped, vanilla does not build there either)" : ""));
                return;
            }

            Fail(filename + ": patched fails " + patchedFailures + "/" + compared +
                 " combinations where vanilla compiles. " + firstError);
        }

        static IEnumerable<Dictionary<string, int>> Combinations((string Name, int[] Values)[] defines)
        {
            if (defines.Length == 0)
            {
                yield return new Dictionary<string, int>();
                yield break;
            }

            var indices = new int[defines.Length];

            while (true)
            {
                var result = new Dictionary<string, int>();
                for (int i = 0; i < defines.Length; i++) result[defines[i].Name] = defines[i].Values[indices[i]];
                yield return result;

                int carry = defines.Length - 1;
                while (carry >= 0)
                {
                    indices[carry]++;
                    if (indices[carry] < defines[carry].Values.Length) break;
                    indices[carry] = 0;
                    carry--;
                }

                if (carry < 0) yield break;
            }
        }

        static string Describe(Dictionary<string, int> defines)
        {
            return string.Join(" ", defines.Select(d => d.Key + "=" + d.Value));
        }

        static bool TryCompile(string source, Dictionary<string, int> defines, string filename, out string error)
        {
            error = null;

            string[] lines = source.Replace("\r\n", "\n").Split('\n');

            // #version must stay the first line, and glslang does not know the
            // GL_ARB extensions the game asks for, so those are dropped.
            var body = new List<string> { lines[0] };
            body.AddRange(defines.Select(d => "#define " + d.Key + " " + d.Value));
            body.AddRange(lines.Skip(1).Where(l => !l.TrimStart().StartsWith("#extension")));

            // glslang picks the shader stage from the extension, so the
            // temp file has to keep the original one - a vertex shader
            // compiled as a fragment shader fails on its outputs, not on
            // anything the patch did.
            string extension = Path.GetExtension(filename) == ".vsh" ? ".vert" : ".frag";
            string temp = Path.Combine(Path.GetTempPath(),
                "vv-verify-" + Guid.NewGuid().ToString("N") + extension);

            try
            {
                File.WriteAllText(temp, string.Join("\n", body));

                var process = Process.Start(new ProcessStartInfo("glslangValidator", "\"" + temp + "\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0) return true;

                error = output.Split('\n').FirstOrDefault(l => l.Contains("ERROR"))?.Trim() ?? "compile failed";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }

        static bool HaveGlslang()
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo("glslangValidator", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });

                process.WaitForExit();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
