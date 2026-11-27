using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VintageVisuals.Common.Diagnostics;
using VintageVisuals.Common.Diagnostics.Profiling;
using VintageVisuals.Ui;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// D99. The profiler can only claim to measure "every feature" if every
    /// feature is in its catalogue. These checks build the catalogue the way
    /// the game does (reflection over IProfileDeclarer) and fail on the first
    /// patch group, panel setting, per-frame renderer or mod GPU span that no
    /// system declares, and on any renderer registered around the timing shim.
    /// A new feature that forgets to declare itself fails here, by name, before
    /// it ships - and is still measured under its raw name if it ships anyway.
    /// </summary>
    public static class ProfilingChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            ProfileRegistry reg = ProfileRegistry.Build(typeof(ProfileRegistry).Assembly);

            check("profiler: every declaration builds without an error",
                  reg.Errors.Count == 0, string.Join("; ", reg.Errors));
            check("profiler: the catalogue has the systems it had at D99",
                  reg.Systems.Count >= 20, reg.Systems.Count + " systems");

            // ---- patch groups -------------------------------------------------
            string patchDir = Path.Combine(repo, "assets/vintagevisuals/shaderpatches");
            List<string> groups = Directory.GetFiles(patchDir, "*.yaml")
                .Select(Path.GetFileNameWithoutExtension).OrderBy(g => g, StringComparer.Ordinal).ToList();
            List<string> unclaimed = groups.Where(g => reg.ForGroup(g) == null).ToList();
            check("profiler: every shipped patch group belongs to a profiled system",
                  unclaimed.Count == 0, "unclaimed: " + string.Join(", ", unclaimed));
            List<string> phantom = reg.Systems.SelectMany(s => s.PatchGroups.Concat(s.RequiresGroups)).Distinct()
                .Where(g => !groups.Contains(g)).ToList();
            check("profiler: every declared or required group exists as a patch file",
                  phantom.Count == 0, "missing: " + string.Join(", ", phantom));

            // ---- panel settings -----------------------------------------------
            List<VisualSetting> loose = VisualSettingRegistry.All
                .Where(s => !s.DebugOnly && reg.ForSettingPath(s.Path) == null).ToList();
            check("profiler: every tuning panel setting belongs to a profiled system",
                  loose.Count == 0, "unclaimed: " + string.Join(", ", loose.Select(s => s.Code + " (" + s.Path + ")")));
            List<string> badSweeps = reg.Systems.SelectMany(s => s.Sweeps.Select(w => s.Id + ":" + w.SettingCode))
                .Where(x => VisualSettingRegistry.ByCode(x.Substring(x.IndexOf(':') + 1)) == null).ToList();
            check("profiler: every declared sweep names a real panel setting",
                  badSweeps.Count == 0, string.Join(", ", badSweeps));
            List<string> badPaths = reg.Systems.SelectMany(s => s.SettingPaths)
                .Where(p => !ConfigAccess.Exists(p)).ToList();
            check("profiler: every exact setting path a system claims exists in the config",
                  badPaths.Count == 0, string.Join(", ", badPaths));
            check("profiler: the panel's system-to-sweep choice lists every system with sweeps",
                  VisualSettingRegistry.SweepSystemNames().Length == reg.Systems.Count(s => s.Sweeps.Count > 0), "");

            // ---- CPU: renderers go through the timing shim ---------------------
            var sources = Directory.GetFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories)
                .Select(f => (Path: f.Substring(repo.Length + 1).Replace('\\', '/'), Text: File.ReadAllText(f))).ToList();
            List<string> raw = sources
                .Where(f => f.Path != "src/Common/Diagnostics/Profiling/CpuSpans.cs" &&
                            f.Path != "src/Common/Diagnostics/FrameProfiler.cs" &&
                            Regex.IsMatch(f.Text, @"\.Event\.RegisterRenderer\("))
                .Select(f => f.Path).ToList();
            check("profiler: no renderer is registered around ModRenderers (it would run untimed)",
                  raw.Count == 0, string.Join(", ", raw));

            var names = new List<string>();
            foreach (var f in sources)
                foreach (Match m in Regex.Matches(f.Text, @"ModRenderers\.Register\([^;]*?""([a-z0-9\-]+)""\s*\)", RegexOptions.Singleline))
                    names.Add(m.Groups[1].Value);
            check("profiler: the renderer census found the mod's renderers", names.Count >= 12, names.Count + " found");
            List<string> untimed = names.Distinct().Where(n => reg.ForCpuSpan(n) == null).ToList();
            check("profiler: every registered renderer's CPU span belongs to a system",
                  untimed.Count == 0, "unclaimed: " + string.Join(", ", untimed));

            var scopes = reg.Systems.SelectMany(s => s.CpuScopes).ToList();
            List<string> deadScopes = scopes.Where(sc =>
                !sources.Any(f => f.Text.Contains("CpuSpans.Measure(\"" + sc + "\")")) &&
                !sources.Any(f => Regex.IsMatch(f.Text, @"const string \w+ = """ + Regex.Escape(sc) + @""";") &&
                                  f.Text.Contains("CpuSpans.Measure("))).ToList();
            check("profiler: every declared CPU scope is measured somewhere",
                  deadScopes.Count == 0, string.Join(", ", deadScopes));

            // ---- GPU: every mod pass segment is someone's ---------------------
            string fp = File.ReadAllText(Path.Combine(repo, "src/Common/Diagnostics/FrameProfiler.cs"));
            List<string> modSpans = Regex.Matches(fp, @"Span\(""([a-z0-9.]+)"",\s*""[^""]*"",[^;]*?,\s*true\)")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            check("profiler: the GPU span census found the mod passes", modSpans.Count >= 5, modSpans.Count + " found");
            List<string> loneGpu = modSpans.Where(k => reg.ForGpuSpan(k) == null).ToList();
            check("profiler: every mod GPU span belongs to a system",
                  loneGpu.Count == 0, "unclaimed: " + string.Join(", ", loneGpu));

            // ---- the override is consulted before the config -------------------
            string mod = File.ReadAllText(Path.Combine(repo, "src/VintageVisualsModSystem.cs"));
            int over = mod.IndexOf("BenchOverrides.GroupOverride(group)", StringComparison.Ordinal);
            int body = mod.IndexOf("private bool IsPatchGroupEnabledByConfig(string group)", StringComparison.Ordinal);
            check("profiler: a bench's group override decides before the config does",
                  over > 0 && body > over && mod.Contains("BenchOverrides.Signature"), "");

            // ---- prerequisites: forcing a group out takes its dependents with it
            List<string> matDeps = reg.DependentsOf("pbrterrainmaterial");
            check("profiler: compiling the terrain material out also holds out what anchors in it",
                  new[] { "pbrterraindebug", "pbrterrainfastbeams", "pbrterrainreflect", "pbrterrainmirror" }.All(matDeps.Contains),
                  string.Join(", ", matDeps));
            List<string> waterDeps = reg.DependentsOf("water");
            check("profiler: compiling the water out also holds out its scene and caustic groups",
                  waterDeps.Contains("waterscene") && waterDeps.Contains("watercaustics"), string.Join(", ", waterDeps));
            check("profiler: a dependent group's bench holds its prerequisite in",
                  reg.PrerequisitesOf("watercaustics").Contains("water"), "");

            // ---- the statistics ----------------------------------------------
            CheckSummary(check);

            // ---- sweep values ----------------------------------------------------
            var slider = new SweepTarget { Min = 0, Max = 1, Step = 0.05f };
            check("profiler: a slider sweeps 0, 25, 50 and 100 % of its range",
                  slider.DefaultValues().SequenceEqual(new[] { 0f, 0.25f, 0.5f, 1f }), string.Join(",", slider.DefaultValues()));
            var toggle = new SweepTarget { IsToggle = true };
            check("profiler: a toggle sweeps off then on", toggle.DefaultValues().SequenceEqual(new[] { 0f, 1f }), "");
            var tier = new SweepTarget { IsChoice = true, Min = 0, Step = 1, Choices = new[] { "Low", "Medium", "High", "Ultra" } };
            check("profiler: a choice sweeps every step", tier.DefaultValues().Length == 4 && tier.Label(2) == "High", "");

            check("profiler: CSV cells with commas or quotes are quoted",
                  BenchResultsFile.Csv("a,b") == "\"a,b\"" && BenchResultsFile.Csv("say \"x\"") == "\"say \"\"x\"\"\"" &&
                  BenchResultsFile.Csv("plain") == "plain", "");
            check("profiler: the runs CSV header has one name per column it writes",
                  BenchResultsFile.RunsHeader.Split(',').Length == 47, BenchResultsFile.RunsHeader.Split(',').Length + " columns");
        }

        /// <summary>
        /// A variant 2 ms slower in every round, with 1 ms of drift per round
        /// across the bench: the paired ABBA median must come out at 2 ms and
        /// significant, and a variant equal to the baseline must read as noise.
        /// </summary>
        private static void CheckSummary(Action<string, bool, string> check)
        {
            var plan = new BenchPlan { Kind = "groups", Subject = "x", System = "x", Title = "x" };
            var a = new BenchVariant { Key = "out", Label = "out" };
            var b = new BenchVariant { Key = "in", Label = "in" };
            var c = new BenchVariant { Key = "same", Label = "same" };
            plan.Variants.Add(a); plan.Variants.Add(b); plan.Variants.Add(c);
            var runs = new List<BenchRun>();
            for (int round = 0; round < 3; round++)
            {
                double drift = round * 1.0;
                runs.Add(Run(plan, a, round, 10 + drift));
                runs.Add(Run(plan, b, round, 12 + drift + (round == 1 ? 0.1 : 0)));
                runs.Add(Run(plan, c, round, 10 + drift + (round == 2 ? 0.02 : 0)));
            }
            PlanSummary s = PlanSummary.From(plan, runs);
            PlanSummary.Row rb = s.Rows.First(r => r.Variant == b);
            PlanSummary.Row rc = s.Rows.First(r => r.Variant == c);
            check("profiler: paired deltas cancel drift between rounds",
                  Math.Abs(rb.Primary - 2.0) < 0.11 && rb.Significant, "cost " + rb.Primary + ", noise " + rb.Noise);
            check("profiler: a variant equal to the baseline reads as noise",
                  !rc.Significant, "cost " + rc.Primary + ", noise " + rc.Noise);
            check("profiler: the summary names the bottleneck", s.Bound == "gpu", s.Bound);
        }

        private static BenchRun Run(BenchPlan plan, BenchVariant v, int round, double ms)
        {
            var frame = new FrameProfiler.BenchResult { Label = v.Label, Frames = 600, FrameAvg = ms, FrameP50 = ms, FrameP95 = ms + 1, FrameP99 = ms + 2 };
            frame.Segments.Add(("frame", "GPU frame", ms * 0.95, ms * 0.5));
            return new BenchRun { Plan = plan, Variant = v, Round = round, Seconds = 6, Frame = frame };
        }
    }
}
