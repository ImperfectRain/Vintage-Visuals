using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Deterministic checks that the documentation still describes the code.
    ///
    /// WHY THIS EXISTS. Documentation in this repository goes stale in one
    /// specific way: a pass implements something, writes a new section
    /// describing it, and leaves the OLD claim in place somewhere else. The
    /// result is a repository that contradicts itself, and the contradiction is
    /// invisible until someone reads both halves. The reconciliation pass that
    /// added this file found STATUS.md and README.md both asserting
    /// "src/Reflections/ is an empty directory" while a shipped scene-capture
    /// reflection system lived in it.
    ///
    /// These checks are deliberately dumb and literal. They cannot tell whether
    /// prose is TRUE - only whether it still refers to things that exist, and
    /// whether numbers quoted in docs match the constants they quote. That is
    /// enough to catch the failure mode this project actually has, and a check
    /// that cannot be argued with is worth more than one that tries to be clever.
    ///
    /// Anything a check here cannot verify belongs in the manual checklist in
    /// docs/CHECKLIST.md instead of being faked with a regex.
    /// </summary>
    public static class DocumentationChecks
    {
        /// <summary>Docs whose claims are checked against the tree.</summary>
        private static readonly string[] Documents =
        {
            "README.md",
            "CLAUDE.md",
            "docs/STATUS.md",
            "docs/ARCHITECTURE.md",
            "docs/IMPLEMENTATION_PLAN.md",
            "docs/DECISIONS.md",
            "docs/CHECKLIST.md",
            "docs/MATERIAL-PIPELINE.md",
            "docs/WORKFLOW.md",
            "docs/VISUAL-TESTS.md",
            "docs/VISUAL-LANGUAGE.md",
            "src/ColorGrade/README.md",
            "src/PseudoPBR/README.md",
            "src/Weather/README.md",
            "src/Reflections/README.md",
        };

        public static void Run(string repo, Action<string, bool, string> check)
        {
            CheckDocumentsExist(repo, check);
            CheckReferencedPathsExist(repo, check);
            CheckNoStaleEmptyClaims(repo, check);
            CheckQuotedConstantsMatch(repo, check);
            CheckDebugViewRangeAgrees(repo, check);
            CheckEverySubsystemHasAHome(repo, check);
        }

        private static string Read(string repo, string relative)
        {
            string path = Path.Combine(repo, relative);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static void CheckDocumentsExist(string repo, Action<string, bool, string> check)
        {
            foreach (string doc in Documents)
            {
                check("the document " + doc + " exists",
                    File.Exists(Path.Combine(repo, doc)),
                    "listed as authoritative but missing from the tree");
            }
        }

        /// <summary>
        /// Every repo-relative path a document names must exist.
        ///
        /// This is the check that catches a subsystem being moved or renamed
        /// without the docs following. Only paths that look like real repo paths
        /// are considered - a bare word is not a path, and neither is a glob.
        /// </summary>
        private static void CheckReferencedPathsExist(string repo, Action<string, bool, string> check)
        {
            var missing = new List<string>();
            int examined = 0;

            foreach (string doc in Documents)
            {
                string text = Read(repo, doc);
                if (text == null) continue;

                // Backtick-quoted things that look like paths into the repo.
                foreach (Match m in Regex.Matches(text, @"`([A-Za-z0-9_][A-Za-z0-9_./-]*/[A-Za-z0-9_./-]*)`"))
                {
                    string candidate = m.Groups[1].Value.TrimEnd('/');

                    // Globs and wildcards describe a family, not a file.
                    if (candidate.Contains("*")) continue;

                    // Only paths rooted at a real top-level directory of this
                    // repository. Anything else is prose that happens to have a
                    // slash in it - a URL, a ratio, an API path.
                    // assets/game/ is VINTAGE STORY'S tree, not this one. The
                    // docs reference it constantly and correctly - it is where
                    // the shaders being patched live - and it is not on disk
                    // here.
                    if (candidate.StartsWith("assets/game/")) continue;

                    if (!Regex.IsMatch(candidate, @"^(src|docs|tools|assets|reference|Properties)/")) continue;

                    examined++;

                    string full = Path.Combine(repo, candidate);
                    if (!File.Exists(full) && !Directory.Exists(full))
                    {
                        missing.Add(doc + " -> " + candidate);
                    }
                }
            }

            check("the documentation references real paths",
                missing.Count == 0,
                missing.Count == 0
                    ? examined + " path references checked"
                    : string.Join("; ", missing.Take(6)));

            check("there were paths to check at all",
                examined > 20,
                "only " + examined + " - the extractor may have stopped matching");
        }

        /// <summary>
        /// A directory documented as empty must actually be empty.
        ///
        /// The exact failure that prompted this file. Two documents said
        /// src/Reflections/ was an empty directory with nothing started, while
        /// it held the scene capture renderer and the reflections subsystem.
        /// </summary>
        private static void CheckNoStaleEmptyClaims(string repo, Action<string, bool, string> check)
        {
            var wrong = new List<string>();

            foreach (string doc in Documents)
            {
                string text = Read(repo, doc);
                if (text == null) continue;

                foreach (Match m in Regex.Matches(text,
                    @"`(src/[A-Za-z0-9_/]+)/?`[^.\n]{0,80}?(empty directory|is empty|nothing here has been started|not been started)",
                    RegexOptions.IgnoreCase))
                {
                    string dir = Path.Combine(repo, m.Groups[1].Value);

                    if (Directory.Exists(dir) && Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length > 0)
                    {
                        wrong.Add(doc + " calls " + m.Groups[1].Value + " empty, but it has files");
                    }
                }
            }

            check("no document calls a populated directory empty",
                wrong.Count == 0,
                string.Join("; ", wrong.Take(4)));
        }

        /// <summary>
        /// Numbers quoted in prose must match the constants they describe.
        ///
        /// Only a handful, and only ones that have actually drifted. A doc that
        /// says "quarter resolution" beside a constant of 0.5 is worse than no
        /// doc, because a reader has no way to know which is current.
        /// </summary>
        private static void CheckQuotedConstantsMatch(string repo, Action<string, bool, string> check)
        {
            string capture = Read(repo, "src/Reflections/SceneCaptureRenderer.cs");
            check("the scene capture renderer exists", capture != null, "");
            if (capture == null) return;

            Match scale = Regex.Match(capture, @"CaptureScale = ([\d.]+)f");
            check("the capture scale is declared", scale.Success, "");
            if (!scale.Success) return;

            float value = float.Parse(scale.Groups[1].Value, CultureInfo.InvariantCulture);

            // "quarter" and "half" are the two words that have been used for
            // this, and they cannot both be right.
            string word = Math.Abs(value - 0.25f) < 0.001f ? "quarter"
                        : Math.Abs(value - 0.5f) < 0.001f ? "half"
                        : null;

            check("the capture scale is a value the docs have a word for",
                word != null,
                "CaptureScale is " + value + " - update this check and the prose together");

            if (word == null) return;

            string wrongWord = word == "quarter" ? "quarter" : "quarter";
            var contradictions = new List<string>();

            foreach (string doc in Documents.Concat(new[] { "src/Reflections/SceneCaptureRenderer.cs" }))
            {
                string text = Read(repo, doc);
                if (text == null) continue;

                // Look for the OTHER word used to describe this capture.
                foreach (Match m in Regex.Matches(text, @"(quarter|half)[- ]resolution", RegexOptions.IgnoreCase))
                {
                    if (!string.Equals(m.Groups[1].Value, word, StringComparison.OrdinalIgnoreCase))
                    {
                        contradictions.Add(doc + " says " + m.Groups[1].Value + "-resolution");
                    }
                }
            }

            check("no document describes the capture at the wrong resolution",
                contradictions.Count == 0,
                "CaptureScale is " + value + " (" + word + "): " + string.Join("; ", contradictions.Take(4)));
        }

        /// <summary>
        /// The debug view range quoted in documentation must match the slider,
        /// which the ConfigLib checks already tie to the shader.
        /// </summary>
        private static void CheckDebugViewRangeAgrees(string repo, Action<string, bool, string> check)
        {
            string config = Read(repo, "assets/vintagevisuals/config/configlib-patches.json");
            check("the configlib patch file exists", config != null, "");
            if (config == null) return;

            Match setting = Regex.Match(config, "\"pbr_debugview\"\\s*:\\s*\\{.*?\"max\"\\s*:\\s*([\\d.]+)",
                                        RegexOptions.Singleline);
            check("the debug view slider declares a maximum", setting.Success, "");
            if (!setting.Success) return;

            int max = (int)float.Parse(setting.Groups[1].Value, CultureInfo.InvariantCulture);

            var wrong = new List<string>();

            foreach (string doc in Documents)
            {
                string text = Read(repo, doc);
                if (text == null) continue;

                // Statements of the form "debug views 0-N" or "views 32-N".
                foreach (Match m in Regex.Matches(text, @"[Vv]iews?\s+\d+\s*[-–]\s*(\d+)"))
                {
                    int quoted = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (quoted > max) wrong.Add(doc + " cites view " + quoted + ", slider stops at " + max);
                }
            }

            check("no document cites a debug view the slider cannot reach",
                wrong.Count == 0,
                string.Join("; ", wrong.Take(4)));
        }

        /// <summary>
        /// Every subsystem directory must be described somewhere authoritative.
        ///
        /// Catches the opposite failure from a stale claim: a subsystem that
        /// exists and is documented NOWHERE. src/Reflections/ was in that state
        /// - present in the tree, absent from ARCHITECTURE.md entirely.
        /// </summary>
        private static void CheckEverySubsystemHasAHome(string repo, Action<string, bool, string> check)
        {
            string src = Path.Combine(repo, "src");
            if (!Directory.Exists(src)) return;

            string status = Read(repo, "docs/STATUS.md") ?? "";
            string architecture = Read(repo, "docs/ARCHITECTURE.md") ?? "";

            var undocumented = new List<string>();
            var withoutReadme = new List<string>();

            foreach (string dir in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(dir);
                if (name == "Common") continue;   // cross-cutting, documented as such

                if (!status.Contains(name) || !architecture.Contains(name))
                {
                    undocumented.Add(name);
                }

                if (!File.Exists(Path.Combine(dir, "README.md")))
                {
                    withoutReadme.Add(name);
                }
            }

            check("every subsystem appears in both STATUS and ARCHITECTURE",
                undocumented.Count == 0,
                string.Join(", ", undocumented));

            check("every subsystem has its own README",
                withoutReadme.Count == 0,
                string.Join(", ", withoutReadme));
        }
    }
}
