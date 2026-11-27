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
            ("FOAMEFFECT", new[] { 0, 1 }),
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

            VerifyBeamPass(haveGlslang);

            VerifyTwinLink(haveGlslang);

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

                // A combination the game cannot produce is not a failure, but
                // SKIPPING the file is: it used to skip chunkopaque outright
                // because the legacy and modular terrain groups exclude each
                // other, so the combination the game actually runs (modular +
                // base + weather + atmosphere) was never patched together here,
                // and an anchor collision between them shipped. Instead, split
                // on every exclusive pair and verify each set the game CAN apply.
                // The exclusion is read from the declaration the gating uses.
                var sets = new List<string[]> { hitting };
                for (bool split = true; split; )
                {
                    split = false;
                    var next = new List<string[]>();
                    foreach (string[] set in sets)
                    {
                        string[] pair = ShaderPatchGroups.FirstExclusivePair(set);
                        if (pair == null) { next.Add(set); continue; }
                        split = true;
                        next.Add(set.Where(g => g != pair[0]).ToArray());
                        next.Add(set.Where(g => g != pair[1]).ToArray());
                    }
                    sets = next.GroupBy(x => string.Join("+", x)).Select(g => g.First()).ToList();
                }

                foreach (string[] set in sets.Where(x => x.Length >= 2))
                {
                    string orphan = set.FirstOrDefault(g => ShaderPatchGroups.RequiredBy(g).Any(r => !set.Contains(r)));
                    if (orphan != null)
                        VerifyFailsClosed(filename, vanilla, set, orphan, all);
                    else
                        VerifyCombination(filename, vanilla, set, all, haveGlslang);
                }
            }
        }

        /// <summary>
        /// A group whose base group is absent (rolled back, or excluded) must
        /// fail its own assert and take nothing else with it.
        /// </summary>
        static void VerifyFailsClosed(string filename, string vanilla, string[] set, string orphan,
                                      List<ShaderPatch> allPatches)
        {
            var patcher = new ShaderPatcher(new CollectingLogger());
            patcher.SetPatches(allPatches.Where(p => set.Contains(p.Group)));
            patcher.Patch(filename, vanilla);

            // Every group whose base is missing from this set is expected to
            // fail closed with it: two dependents of the same base (the scene
            // capture bridge and the mirrors, D97) are both orphans here, and
            // neither failing is collateral of the other.
            string[] collateral = set.Where(g => g != orphan && !patcher.IsGroupHealthy(g) &&
                                                 !ShaderPatchGroups.RequiredBy(g).Any(r => !set.Contains(r))).ToArray();
            string[] otherOrphans = set.Where(g => g != orphan &&
                                                   ShaderPatchGroups.RequiredBy(g).Any(r => !set.Contains(r)) &&
                                                   patcher.IsGroupHealthy(g)).ToArray();
            if (otherOrphans.Length > 0)
                Fail(filename + ": '" + string.Join(", ", otherOrphans) + "' applied without its required group instead of failing closed");
            if (patcher.IsGroupHealthy(orphan))
                Fail(filename + ": '" + orphan + "' applied without " +
                     string.Join(", ", ShaderPatchGroups.RequiredBy(orphan)) + " instead of failing closed");
            else if (collateral.Length > 0)
                Fail(filename + ": '" + orphan + "' failing closed also broke " + string.Join(", ", collateral));
            else
                Pass(filename + ": '" + orphan + "' fails closed without " +
                     string.Join(", ", ShaderPatchGroups.RequiredBy(orphan)) + ", and " +
                     string.Join(" + ", set.Where(g => g != orphan)) + " still apply");
        }

        static void VerifyCombination(string filename, string vanilla, string[] hitting,
                                      List<ShaderPatch> allPatches, bool haveGlslang)
        {
            {
                var all = allPatches.Where(p => hitting.Contains(p.Group)).ToList();

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
                    return;
                }

                // A uniform declared twice in one translation unit does not
                // compile, and two groups injecting the same shared snippet is
                // exactly how that happens.
                string duplicate = FindDuplicateUniform(patched);
                if (duplicate != null)
                {
                    Fail(filename + " with " + string.Join(" + ", hitting) +
                         ": uniform '" + duplicate + "' is declared more than once");
                    return;
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

                // D100. The game's program is compiled again as two twins, each
                // with one fragment define. A define that breaks the source only
                // breaks it in the twin, which no per-group pass ever compiles.
                if (filename == "chunkopaque.fsh" || filename == "chunkopaque.vsh")
                    CombinedChunkopaque[filename] = patched;
                if (haveGlslang && filename == "chunkopaque.fsh")
                {
                    foreach (string twin in TwinDefines.Where(d => TwinApplies(patched, d)))
                        Compile(filename + " [" + twin + " twin]", vanilla, WithTwinDefine(patched, twin));
                }
            }
        }

        static readonly string[] TwinDefines = { "VV_DEPTH_ONLY", "VV_FLORA_ONLY" };

        /// <summary>The last combined chunkopaque stages, for the twin link check.</summary>
        static readonly Dictionary<string, string> CombinedChunkopaque = new Dictionary<string, string>();

        static bool TwinApplies(string fragment, string define)
        {
            return define == "VV_DEPTH_ONLY" ? fragment.Contains("VV_PREPASS_ON") : fragment.Contains("#ifdef VV_FLORA_ONLY");
        }

        /// <summary>Same as ChunkProgramSplit: the define on the line after #version.</summary>
        static string WithTwinDefine(string source, string define)
        {
            int cut = source.IndexOf('\n') + 1;
            return source.Substring(0, cut) + "#define " + define + " 1\n" + source.Substring(cut);
        }

        /// <summary>
        /// D100. Links each twin's fragment stage with the game's (patched)
        /// vertex stage, the pair the game would link. A varying the twin still
        /// reads that the vertex stage stopped writing compiles and fails here.
        /// </summary>
        static void VerifyTwinLink(bool haveGlslang)
        {
            Console.WriteLine();
            Console.WriteLine("split chunk programs (D100, chunkopaque compiled again with one fragment define)");
            string vert, frag;
            if (!CombinedChunkopaque.TryGetValue("chunkopaque.vsh", out vert) || !CombinedChunkopaque.TryGetValue("chunkopaque.fsh", out frag))
            {
                Fail("split chunk programs: no combined chunkopaque stages to link");
                return;
            }
            if (!vert.Contains("invariant gl_Position")) Fail("split chunk programs: the combined vertex stage lacks 'invariant gl_Position'");
            else Pass("split chunk programs: the combined vertex stage declares gl_Position invariant");
            if (!haveGlslang) return;

            var prefixes = new[]
            {
                new Dictionary<string, int> { { "SHADOWQUALITY", 0 }, { "SSAOLEVEL", 0 }, { "GODRAYS", 0 }, { "NORMALVIEW", 0 }, { "SHINYEFFECT", 0 } },
                new Dictionary<string, int> { { "SHADOWQUALITY", 2 }, { "SSAOLEVEL", 1 }, { "GODRAYS", 1 }, { "NORMALVIEW", 0 }, { "SHINYEFFECT", 1 } },
            };
            foreach (string twin in new[] { (string)null }.Concat(TwinDefines))
            {
                if (twin != null && !TwinApplies(frag, twin)) { Fail("split chunk programs: " + twin + " has nothing to act on in the combined source"); continue; }
                foreach (var prefix in prefixes)
                {
                    string label = "chunkopaque " + (twin ?? "game program") + " links [" + Describe(prefix) + "]";
                    string f = twin == null ? frag : WithTwinDefine(frag, twin);
                    if (TryLink(StripExtensions(vert), StripExtensions(f), prefix, out string error)) Pass(label);
                    else Fail(label + ": " + error);
                }
            }
        }

        static string StripExtensions(string source)
        {
            return string.Join("\n", source.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("#extension")));
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

            // A group that patches on top of another applies with it, never on
            // vanilla alone; alone it must fail closed, which the combined pass
            // checks. Only the files this group targets are taken from the base.
            var ownFiles = new HashSet<string>(patches.Select(p => p.Filename));
            foreach (string required in ShaderPatchGroups.RequiredBy(group))
            {
                string requiredPath = Path.Combine(Path.GetDirectoryName(yamlPath), required + ".yaml");
                patches.AddRange(ShaderPatchLoader.ParsePatchFile(
                    File.ReadAllText(requiredPath), required, requiredPath,
                    n => File.ReadAllText(Path.Combine(Repo, "assets/vintagevisuals/shadersnippets", n)))
                    .Where(p => ownFiles.Contains(p.Filename)));
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

        /// <summary>
        /// D87. The fast beam pass is not a patch: it is a program the mod
        /// builds in memory from the same snippets the terrain group injects.
        /// So it is compiled and LINKED here (vertex with each fragment stage),
        /// with no prefix and with the prefix defines the game might prepend,
        /// because a memory program's prefix is not something this repo has
        /// seen. A link failure is the failure that matters: a fragment input
        /// nothing writes compiles cleanly and does not link.
        /// </summary>
        static void VerifyBeamPass(bool haveGlslang)
        {
            Console.WriteLine();
            Console.WriteLine("beam pass (D87, built in memory from the shipped snippets)");

            string snippetDir = Path.Combine(Repo, "assets", "vintagevisuals", "shadersnippets");
            string march = VintageVisuals.PseudoPBR.BeamPassSource.March(name =>
            {
                string path = Path.Combine(snippetDir, name);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            });

            if (march == null) { Fail("beam pass: the march source could not be assembled from the snippets"); return; }
            Pass("beam pass: march source assembled (" + march.Length + " chars)");

            if (!haveGlslang) return;

            var prefixes = new[]
            {
                new Dictionary<string, int>(),
                new Dictionary<string, int> { { "SHADOWQUALITY", 2 }, { "SSAOLEVEL", 1 }, { "GODRAYS", 1 }, { "NORMALVIEW", 1 } },
            };

            foreach (var prefix in prefixes)
            {
                foreach (var stage in new[] { ("march", march), ("composite", VintageVisuals.PseudoPBR.BeamPassSource.Composite) })
                {
                    string label = "beam pass " + stage.Item1 + " links with prefix [" + Describe(prefix) + "]";
                    if (TryLink(VintageVisuals.PseudoPBR.BeamPassSource.Vertex, stage.Item2, prefix, out string error))
                        Pass(label);
                    else
                        Fail(label + ": " + error);
                }
            }
        }

        static string WithPrefix(string source, Dictionary<string, int> defines)
        {
            string[] lines = source.Replace("\r\n", "\n").Split('\n');
            var body = new List<string> { lines[0] };
            body.AddRange(defines.Select(d => "#define " + d.Key + " " + d.Value));
            body.AddRange(lines.Skip(1));
            return string.Join("\n", body);
        }

        static bool TryLink(string vertex, string fragment, Dictionary<string, int> defines, out string error)
        {
            error = null;
            string stem = Path.Combine(Path.GetTempPath(), "vv-beam-" + Guid.NewGuid().ToString("N"));
            string v = stem + ".vert";
            string f = stem + ".frag";
            try
            {
                File.WriteAllText(v, WithPrefix(vertex, defines));
                File.WriteAllText(f, WithPrefix(fragment, defines));
                var process = Process.Start(new ProcessStartInfo("glslangValidator", "-l \"" + v + "\" \"" + f + "\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0) return true;
                error = output.Split('\n').FirstOrDefault(l => l.Contains("ERROR"))?.Trim() ?? "link failed";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { File.Delete(v); } catch { }
                try { File.Delete(f); } catch { }
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
