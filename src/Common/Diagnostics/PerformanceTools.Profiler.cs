using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

using VintageVisuals.Common.Diagnostics.Profiling;

namespace VintageVisuals.Common.Diagnostics
{
    /// <summary>
    /// D99. The full-mod profiler's commands, on top of the older benches:
    ///
    ///   .vvbench groups [all|active|g1,g2] [secs] [rN] [@scene]
    ///        every patch group compiled out vs in, interleaved ABBA rounds
    ///   .vvbench sweep &lt;setting|system&gt; [v1,v2,..] [secs] [rN] [@scene]
    ///        any tuning panel setting stepped through values, or every sweep a
    ///        system declares
    ///   .vvbench suite [quick] [secs] [rN] [@scene1 @scene2 ..]
    ///        groups + every declared sweep, in each scene in turn
    ///   .vvbench stop | status
    ///   .vvscene save &lt;name&gt; [rain=0..1] [wind=code] [note..] | list | go &lt;name&gt; | delete &lt;name&gt; | release
    ///   .vvprofile [coverage|cpu|systems]
    ///
    /// What is measured is decided by <see cref="ProfileRegistry"/> plus the
    /// shipped patch groups and panel settings, never by a list here, so a new
    /// feature is in every one of these from its first build.
    /// </summary>
    public sealed partial class PerformanceTools
    {
        private readonly IProfilerHost _host;

        /// <summary>The live instance, for the tuning panel's Performance tab; null outside a world.</summary>
        public static PerformanceTools Current { get; private set; }
        private BenchRunner _runner;
        private BenchScenes _scenes;
        private readonly Queue<string> _pendingScenes = new Queue<string>();
        private System.Func<BenchPlan[]> _pendingPlans;
        private string _pendingTitle;
        private int _pendingRounds;
        private double _pendingSeconds;
        private FrameSettleProbe _probe;

        /// <summary>Defaults the panel sets (Performance.Profiler*); commands may override per run.</summary>
        public int ProfilerRounds { get; set; } = 3;
        public float ProfilerSeconds { get; set; } = 6f;

        public ProfileRegistry Registry { get { return ProfileRegistry.Shared; } }
        public BenchRunner Runner { get { return _runner; } }
        public BenchScenes Scenes { get { return _scenes ?? (_scenes = new BenchScenes(_capi, _logger)); } }

        /// <summary>"groups: 4 of 40 steps, about 6 min left" while a profiler bench runs; null otherwise.</summary>
        public string ProfilerStatus
        {
            get { return _runner != null && _runner.Running ? _runner.Title + ": " + _runner.Progress : null; }
        }

        private void RegisterProfilerCommands()
        {
            Current = this;
            try
            {
                var parsers = _capi.ChatCommands.Parsers;
                _capi.ChatCommands.Create("vvscene")
                    .WithDescription("Vintage Visuals: saved bench scenes. '.vvscene save <name> [rain=0..1] [wind=code] [note]', 'list', 'go <name>', 'delete <name>', 'release'")
                    .WithArgs(parsers.OptionalAll("args"))
                    .HandleWith(args => SceneCommand(Words(args[0] as string)));

                _capi.ChatCommands.Create("vvprofile")
                    .WithDescription("Vintage Visuals: profiler coverage and CPU timers. '.vvprofile coverage', '.vvprofile cpu', '.vvprofile systems'")
                    .WithArgs(parsers.OptionalAll("args"))
                    .HandleWith(args => ProfileCommand(Words(args[0] as string)));
            }
            catch (Exception ex)
            {
                _logger.Warning("[VintageVisuals] could not register .vvscene/.vvprofile: " + ex.Message);
            }
        }

        private static string[] Words(string all)
        {
            all = (all ?? "").Trim();
            return all.Length == 0 ? new string[0] : all.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        // ---- .vvbench groups / sweep / suite ------------------------------------

        private sealed class BenchArgs
        {
            public readonly List<string> Words = new List<string>();
            public readonly List<string> Scenes = new List<string>();
            public double Seconds;
            public int Rounds;
            public bool Quick;
        }

        private BenchArgs Parse(IEnumerable<string> words)
        {
            var a = new BenchArgs { Seconds = ProfilerSeconds, Rounds = ProfilerRounds };
            foreach (string w in words)
            {
                if (w.StartsWith("@", StringComparison.Ordinal) && w.Length > 1) a.Scenes.Add(w.Substring(1));
                else if ((w.StartsWith("r", StringComparison.OrdinalIgnoreCase) || w.StartsWith("x", StringComparison.OrdinalIgnoreCase)) &&
                         int.TryParse(w.Substring(1), out int r) && r > 0) a.Rounds = Math.Min(10, r);
                else if (double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0 && !w.Contains(","))
                    a.Seconds = Math.Max(2, Math.Min(120, s));
                else if (string.Equals(w, "quick", StringComparison.OrdinalIgnoreCase)) a.Quick = true;
                else a.Words.Add(w);
            }
            return a;
        }

        /// <summary>The profiler's .vvbench modes; null when the words are an older mode.</summary>
        private TextCommandResult TryProfilerBench(string[] words)
        {
            if (words.Length == 0) return null;
            string mode = words[0].ToLowerInvariant();
            if (mode != "groups" && mode != "sweep" && mode != "suite" && mode != "stop" && mode != "status") return null;

            if (mode == "stop")
            {
                if (_runner == null || !_runner.Running) return TextCommandResult.Success("No profiler bench is running.");
                _pendingScenes.Clear();
                _runner.Abort("stopped by '.vvbench stop'");
                Scenes.Release();
                return TextCommandResult.Success("Stopping.");
            }
            if (mode == "status")
            {
                return TextCommandResult.Success(ProfilerStatus ?? "No profiler bench is running.");
            }

            if (_host == null) return TextCommandResult.Error("This build cannot run the profiler benches (no host).");
            if (_profiler.Recording || _benchCallback != 0 || (_runner != null && _runner.Running))
                return TextCommandResult.Error("A bench is already running ('.vvbench stop' ends a profiler bench).");

            BenchArgs a = Parse(words.Skip(1));
            foreach (string name in a.Scenes)
            {
                if (Scenes.Find(name) == null)
                    return TextCommandResult.Error("No saved scene '" + name + "'. '.vvscene list' shows them; '.vvscene save " + name + "' saves where you stand.");
            }

            System.Func<BenchPlan[]> build;
            string title;
            if (mode == "groups")
            {
                string filter = a.Words.FirstOrDefault() ?? "all";
                build = () => GroupPlans(filter).ToArray();
                title = "groups " + filter;
            }
            else if (mode == "sweep")
            {
                if (a.Words.Count == 0)
                    return TextCommandResult.Error("Which setting? '.vvbench sweep <code> [0,0.5,1]'; codes are on each panel row's details line, or name a system ('.vvprofile systems').");
                string subject = a.Words[0];
                float[] values = a.Words.Count > 1 ? ParseValues(a.Words[1]) : null;
                BenchPlan[] plans = SweepPlans(subject, values, out string error).ToArray();
                if (plans.Length == 0) return TextCommandResult.Error(error ?? "Nothing to sweep for '" + subject + "'.");
                build = () => SweepPlans(subject, values, out _).ToArray();
                title = "sweep " + subject;
            }
            else
            {
                build = () => SuitePlans(a.Quick).ToArray();
                title = "suite" + (a.Quick ? " quick" : "");
            }

            BenchPlan[] first = build();
            double est = BenchRunner.Estimate(first, a.Rounds, a.Seconds) * Math.Max(1, a.Scenes.Count);
            StartQueued(title, build, a.Rounds, a.Seconds, a.Scenes);
            int runnable = first.Count(p => p.Skip == null);
            return TextCommandResult.Success("Profiling " + title + ": " + runnable + " plan(s)" +
                (first.Length > runnable ? " (" + (first.Length - runnable) + " skipped, listed at the end)" : "") +
                ", " + a.Rounds + " interleaved round(s) of " + a.Seconds.ToString("0", CultureInfo.InvariantCulture) + " s" +
                (a.Scenes.Count > 0 ? " in " + string.Join(", ", a.Scenes) : "") +
                ", about " + Math.Ceiling(est / 60).ToString(CultureInfo.InvariantCulture) + " min. " +
                "Don't touch anything; the world may flicker at each shader reload. '.vvbench stop' ends it and restores everything.");
        }

        private static float[] ParseValues(string list)
        {
            var values = new List<float>();
            foreach (string part in list.Split(','))
            {
                if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) values.Add(v);
            }
            return values.Count > 0 ? values.ToArray() : null;
        }

        private void StartQueued(string title, System.Func<BenchPlan[]> build, int rounds, double seconds, List<string> scenes)
        {
            _pendingScenes.Clear();
            foreach (string s in scenes) _pendingScenes.Enqueue(s);
            _pendingPlans = build;
            _pendingTitle = title;
            _pendingRounds = rounds;
            _pendingSeconds = seconds;
            StartNextScene();
        }

        private void StartNextScene()
        {
            string scene = _pendingScenes.Count > 0 ? _pendingScenes.Dequeue() : null;
            if (_runner == null)
            {
                _runner = new BenchRunner(_capi, _logger, _profiler, _host, Report);
                _runner.Finished = completed =>
                {
                    if (completed && _pendingScenes.Count > 0)
                    {
                        _capi.Event.RegisterCallback(_ => StartNextScene(), 0);
                        return;
                    }
                    Scenes.Release();
                    StopProbe();
                };
            }

            BenchScene target = scene != null ? Scenes.Find(scene) : null;
            _runner.Start(_pendingTitle + (scene != null ? " @" + scene : ""), _pendingPlans(), _pendingRounds, _pendingSeconds, scene,
                target == null ? (Action<BenchRunner>)null : r => ScenePrelude(r, target));
        }

        /// <summary>Teleport, hold time and weather, wait to arrive, then until the frame time stops moving.</summary>
        private void ScenePrelude(BenchRunner r, BenchScene s)
        {
            r.PreludeAction(() =>
            {
                List<string> sent = Scenes.Go(s);
                _logger.Notification("[VintageVisuals] bench scene " + BenchScenes.Describe(s) + ": " + string.Join("; ", sent));
                StartProbe();
            });
            r.PreludeWait(1.0, () => Scenes.Arrived(s), 30, "the teleport to " + s.Name);
            r.PreludeAction(() => Scenes.Look(s));
            r.PreludeWait(6.0, () => Scenes.ChunkLoaded() && _probe != null && _probe.Stable(1000.0 / Math.Max(1, _profiler.FrameAverageMs)),
                          60, "chunks to load and the frame time to settle at " + s.Name);
            r.PreludeAction(() => { Scenes.Look(s); StopProbe(); });
        }

        private void StartProbe()
        {
            if (_probe != null) return;
            _probe = new FrameSettleProbe();
            _profiler.Acquire();
            _profiler.FrameObserved = ms => _probe?.Push(ms);
        }

        private void StopProbe()
        {
            if (_probe == null) return;
            _probe = null;
            _profiler.FrameObserved = null;
            _profiler.Release();
        }

        /// <summary>
        /// One plan per patch group: compiled out (baseline) against compiled in,
        /// with the group's prerequisites held in for both. A group the config has
        /// off is forced in with no uniforms uploaded, so "in" is the price of its
        /// source alone; a group the config has on runs as configured.
        /// </summary>
        internal IEnumerable<BenchPlan> GroupPlans(string filter)
        {
            ProfileRegistry reg = Registry;
            IEnumerable<string> groups = _host.PatchGroups;
            if (string.Equals(filter, "active", StringComparison.OrdinalIgnoreCase))
                groups = groups.Where(_host.GroupEnabledByConfig);
            else if (!string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
            {
                var wanted = new HashSet<string>(filter.Split(','), StringComparer.OrdinalIgnoreCase);
                // A system id names all of its groups.
                foreach (ProfiledSystem sys in reg.Systems.Where(x => wanted.Contains(x.Id)))
                    foreach (string g in sys.PatchGroups) wanted.Add(g);
                groups = groups.Where(g => wanted.Contains(g));
            }

            foreach (string group in groups)
            {
                string g = group;
                ProfiledSystem owner = reg.ForGroup(g);
                List<string> prereqs = reg.PrerequisitesOf(g);
                List<string> dependents = reg.DependentsOf(g);
                bool configured = _host.GroupEnabledByConfig(g);
                var plan = new BenchPlan
                {
                    Kind = "groups",
                    Subject = g,
                    System = owner != null ? owner.Id : "(unclaimed)",
                    Title = "compiled cost of " + g + (owner != null ? " (" + owner.DisplayName + ")" : ""),
                    Restore = BenchOverrides.Clear,
                };
                var notes = new List<string>();
                if (!configured) notes.Add("forced in for the bench: the config has it off, so 'in' is its source with no uniforms (zero = vanilla)");
                if (prereqs.Count > 0) notes.Add("held in for both halves: " + string.Join(", ", prereqs));
                if (dependents.Count > 0) notes.Add("held out for both halves (they need it): " + string.Join(", ", dependents));
                if (owner == null) notes.Add("no system declares this group (IProfileDeclarer); measured under its file name");
                if (owner?.Notes != null) notes.Add(owner.Notes);
                plan.Note = string.Join("; ", notes);

                plan.Variants.Add(new BenchVariant
                {
                    Key = "out", Label = g + " compiled out", Reloads = true,
                    Apply = () => { BenchOverrides.Clear(); Hold(prereqs, dependents); BenchOverrides.ForceGroup(g, false); },
                });
                plan.Variants.Add(new BenchVariant
                {
                    Key = "in", Label = g + (configured ? " in (as configured)" : " in (forced, idle)"), Reloads = true,
                    Apply = () => { BenchOverrides.Clear(); Hold(prereqs, dependents); BenchOverrides.ForceGroup(g, true); },
                });
                yield return plan;
            }
        }

        private static void Hold(List<string> prereqs, List<string> dependents)
        {
            BenchOverrides.Clear();
            foreach (string p in prereqs) BenchOverrides.ForceGroup(p, true);
            foreach (string d in dependents) BenchOverrides.ForceGroup(d, false);
        }

        /// <summary>
        /// Sweeps: a setting code (any panel row), or a system id (each sweep it
        /// declares). The baseline is the first value; each other value's cost is
        /// against it.
        /// </summary>
        internal IEnumerable<BenchPlan> SweepPlans(string subject, float[] values, out string error)
        {
            error = null;
            var plans = new List<BenchPlan>();
            ProfiledSystem sys = Registry.ById(subject);
            if (sys != null)
            {
                if (sys.Sweeps.Count == 0) { error = "System '" + sys.Id + "' declares no sweeps; name a setting code instead."; return plans; }
                foreach (SweepSpec spec in sys.Sweeps)
                {
                    BenchPlan p = SweepPlan(spec.SettingCode, values ?? spec.Values, out string e);
                    if (p != null) plans.Add(p); else plans.Add(new BenchPlan { Kind = "sweep", Subject = spec.SettingCode, System = sys.Id, Title = "sweep " + spec.SettingCode, Skip = e });
                }
                return plans;
            }
            BenchPlan single = SweepPlan(subject, values, out error);
            if (single != null) plans.Add(single);
            return plans;
        }

        private BenchPlan SweepPlan(string code, float[] values, out string error)
        {
            error = null;
            SweepTarget t = _host.Targets.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase) ||
                                                              string.Equals(x.Path, code, StringComparison.OrdinalIgnoreCase));
            if (t == null)
            {
                error = "No panel setting '" + code + "'. '.vvprofile systems' lists the sweeps each system declares.";
                return null;
            }
            float[] steps = values ?? t.DefaultValues();
            float original = _host.Get(t.Path);
            ProfiledSystem owner = Registry.ForSettingPath(t.Path);
            var plan = new BenchPlan
            {
                Kind = "sweep",
                Subject = t.Code,
                System = owner != null ? owner.Id : "(unclaimed)",
                Title = "sweep " + t.DisplayName + " (" + t.Code + ")",
                Restore = () => _host.Set(t.Path, original),
                Note = (t.RequiresShaderReload ? "changing it reloads shaders, so each step includes that settle; " : "") +
                       "you had " + t.Label(original) +
                       (owner != null && !owner.IsActive(_host.Config) ? "; " + owner.DisplayName + " is off in the config, so every step may read the same" : ""),
            };
            foreach (float v in steps)
            {
                float value = v;
                plan.Variants.Add(new BenchVariant
                {
                    Key = value.ToString("0.###", CultureInfo.InvariantCulture),
                    Label = t.DisplayName + " " + t.Label(value),
                    Reloads = t.RequiresShaderReload,
                    Apply = () => _host.Set(t.Path, value),
                });
            }
            if (plan.Variants.Count < 2) { error = "A sweep needs at least two values."; return null; }
            return plan;
        }

        /// <summary>Everything: every patch group, then every declared sweep of every system the config has on.</summary>
        internal IEnumerable<BenchPlan> SuitePlans(bool quick)
        {
            foreach (BenchPlan p in GroupPlans(quick ? "active" : "all")) yield return p;
            foreach (ProfiledSystem sys in Registry.Systems)
            {
                foreach (SweepSpec spec in sys.Sweeps)
                {
                    if (!sys.IsActive(_host.Config))
                    {
                        yield return new BenchPlan { Kind = "sweep", Subject = spec.SettingCode, System = sys.Id, Title = "sweep " + spec.SettingCode,
                                                     Skip = sys.DisplayName + " is off in the config (its compiled cost is in the group runs)" };
                        continue;
                    }
                    BenchPlan p = SweepPlan(spec.SettingCode, quick && spec.Values == null ? QuickValues(spec.SettingCode) : spec.Values, out string e);
                    yield return p ?? new BenchPlan { Kind = "sweep", Subject = spec.SettingCode, System = sys.Id, Title = "sweep " + spec.SettingCode, Skip = e };
                }
            }
        }

        private float[] QuickValues(string code)
        {
            SweepTarget t = _host.Targets.FirstOrDefault(x => x.Code == code);
            if (t == null || t.IsToggle || t.IsChoice) return null;
            return new[] { t.Min, t.Max };
        }

        // ---- .vvscene ------------------------------------------------------------

        private TextCommandResult SceneCommand(string[] w)
        {
            string sub = w.Length > 0 ? w[0].ToLowerInvariant() : "list";
            switch (sub)
            {
                case "save":
                {
                    if (w.Length < 2) return TextCommandResult.Error("'.vvscene save <name> [rain=0..1] [wind=code] [note]'");
                    float rain = 0f;
                    string wind = "";
                    var note = new List<string>();
                    foreach (string x in w.Skip(2))
                    {
                        if (x.StartsWith("rain=", StringComparison.OrdinalIgnoreCase))
                        {
                            string v = x.Substring(5);
                            rain = v == "auto" ? -1f : float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ? Math.Max(0, Math.Min(1, r)) : 0f;
                        }
                        else if (x.StartsWith("wind=", StringComparison.OrdinalIgnoreCase)) wind = x.Substring(5);
                        else note.Add(x);
                    }
                    BenchScene s = Scenes.Save(w[1], rain, wind, string.Join(" ", note));
                    return TextCommandResult.Success("Saved " + BenchScenes.Describe(s) +
                        ". Benches run there with '@" + s.Name + "' (time stopped, rain held at " + (rain < 0 ? "the game's choice" : rain.ToString("0.##", CultureInfo.InvariantCulture)) + ").");
                }
                case "go":
                {
                    if (w.Length < 2) return TextCommandResult.Error("'.vvscene go <name>'");
                    BenchScene s = Scenes.Find(w[1]);
                    if (s == null) return TextCommandResult.Error("No scene '" + w[1] + "'.");
                    Scenes.Go(s);
                    _capi.Event.RegisterCallback(_ => Scenes.Look(s), 1500);
                    return TextCommandResult.Success("Going to " + BenchScenes.Describe(s) + ". Time and weather stay held until '.vvscene release'.");
                }
                case "delete":
                    if (w.Length < 2) return TextCommandResult.Error("'.vvscene delete <name>'");
                    return Scenes.Delete(w[1]) ? TextCommandResult.Success("Deleted " + w[1] + ".") : TextCommandResult.Error("No scene '" + w[1] + "'.");
                case "release":
                    Scenes.Release();
                    return TextCommandResult.Success("Time and weather run again.");
                default:
                {
                    IReadOnlyList<BenchScene> all = Scenes.All;
                    if (all.Count == 0)
                        return TextCommandResult.Success("No saved scenes in this world. Stand somewhere, look the way to measure, and '.vvscene save F'. " +
                            "docs/VISUAL-TESTS.md suggests F forest at 17:00 facing the sun, P pond at noon, V valley at 480 blocks, R storm (rain=1), C torch-lit cave, M mirror room, E 20+ entities.");
                    return TextCommandResult.Success(string.Join("\n", all.Select(BenchScenes.Describe)));
                }
            }
        }

        // ---- .vvprofile -------------------------------------------------------------

        private TextCommandResult ProfileCommand(string[] w)
        {
            string sub = w.Length > 0 ? w[0].ToLowerInvariant() : "coverage";
            if (sub == "cpu") return TextCommandResult.Success(CpuReport());
            if (sub == "systems") return TextCommandResult.Success(SystemsReport());

            string text = CoverageReport();
            string path = null;
            try { path = BenchResultsFile.WriteText(_capi, "coverage.txt", text); } catch { }
            foreach (string line in text.TrimEnd().Split('\n')) _logger.Notification("[VintageVisuals] coverage " + line);
            string headline = text.Split('\n').FirstOrDefault() ?? "";
            return TextCommandResult.Success(headline + (path != null ? " Full report: " + path : ""));
        }

        /// <summary>CPU time per mod system this half-second, worst first, with its owning system.</summary>
        public string CpuReport()
        {
            var sb = new StringBuilder();
            sb.Append("mod CPU ").Append(CpuSpans.TotalMsPerFrame.ToString("0.000", CultureInfo.InvariantCulture)).Append(" ms/frame\n");
            foreach (CpuSpans.Stat s in CpuSpans.All.OrderByDescending(x => x.MsPerFrame))
            {
                ProfiledSystem owner = Registry.ForCpuSpan(s.Name);
                sb.Append("  ").Append(s.Name.Replace("vintagevisuals-", "").PadRight(24))
                  .Append(s.MsPerFrame.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(7)).Append(" ms  ")
                  .Append((s.BytesPerFrame / 1024.0).ToString("0.0", CultureInfo.InvariantCulture).PadLeft(6)).Append(" KB/frame  ")
                  .Append(owner != null ? owner.Id : "(unclaimed)").Append('\n');
            }
            return sb.ToString();
        }

        public string SystemsReport()
        {
            var sb = new StringBuilder();
            foreach (ProfiledSystem s in Registry.Systems)
            {
                sb.Append(s.Id).Append(" - ").Append(s.DisplayName).Append(s.IsActive(_host?.Config) ? "" : " (off)").Append('\n');
                if (s.PatchGroups.Count > 0) sb.Append("  groups: ").Append(string.Join(", ", s.PatchGroups)).Append('\n');
                if (s.Sweeps.Count > 0) sb.Append("  sweeps: ").Append(string.Join(", ", s.Sweeps.Select(x => x.SettingCode))).Append('\n');
                if (s.Renderers.Count + s.CpuScopes.Count > 0) sb.Append("  cpu: ").Append(string.Join(", ", s.Renderers.Concat(s.CpuScopes))).Append('\n');
                if (s.GpuSpans.Count > 0) sb.Append("  gpu: ").Append(string.Join(", ", s.GpuSpans)).Append('\n');
                if (s.Scenes.Count > 0) sb.Append("  scenes: ").Append(string.Join(", ", s.Scenes)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// What the profiler can and cannot attribute: every shipped group,
        /// every panel setting, every timed renderer and every GPU segment,
        /// against the declarations. First line is the headline.
        /// </summary>
        public string CoverageReport()
        {
            ProfileRegistry reg = Registry;
            var sb = new StringBuilder();
            IReadOnlyList<string> groups = _host?.PatchGroups ?? new List<string>();
            List<string> unclaimedGroups = groups.Where(g => reg.ForGroup(g) == null).ToList();
            List<SweepTarget> settings = (_host?.Targets ?? new List<SweepTarget>()).Where(t => !t.DebugOnly).ToList();
            List<SweepTarget> unclaimedSettings = settings.Where(t => reg.ForSettingPath(t.Path) == null).ToList();
            List<string> renderers = ModRenderers.Names.Concat(CpuSpans.All.Select(x => x.Name)).Distinct().ToList();
            List<string> unclaimedCpu = renderers.Where(n => reg.ForCpuSpan(n) == null).ToList();
            List<string> modSegments = _profiler.Segments.Where(s => s.ModPass).Select(s => s.Key).ToList();
            List<string> unclaimedGpu = modSegments.Where(k => reg.ForGpuSpan(k) == null).ToList();
            int gaps = unclaimedGroups.Count + unclaimedSettings.Count + unclaimedCpu.Count + unclaimedGpu.Count + reg.Errors.Count;

            sb.Append("Profiler coverage: ").Append(reg.Systems.Count).Append(" systems, ").Append(groups.Count).Append(" patch groups, ")
              .Append(settings.Count).Append(" panel settings, ").Append(renderers.Count).Append(" CPU spans, ")
              .Append(modSegments.Count).Append(" mod GPU spans; ")
              .Append(gaps == 0 ? "everything is attributed." : gaps + " gap(s), listed below (each is still measured, under its raw name).").Append('\n');
            foreach (string e in reg.Errors) sb.Append("  declaration error: ").Append(e).Append('\n');
            foreach (string g in unclaimedGroups) sb.Append("  unclaimed patch group: ").Append(g).Append('\n');
            foreach (SweepTarget t in unclaimedSettings) sb.Append("  unclaimed setting: ").Append(t.Code).Append(" (").Append(t.Path).Append(")\n");
            foreach (string n in unclaimedCpu) sb.Append("  unclaimed CPU span: ").Append(n).Append('\n');
            foreach (string k in unclaimedGpu) sb.Append("  unclaimed GPU span: ").Append(k).Append('\n');
            sb.Append('\n');

            foreach (ProfiledSystem s in reg.Systems)
            {
                sb.Append(s.Id).Append(" - ").Append(s.DisplayName).Append(s.IsActive(_host?.Config) ? "" : " (off in config)").Append('\n');
                foreach (string g in s.PatchGroups)
                    sb.Append("  compiled cost: .vvbench groups ").Append(g)
                      .Append(_host == null ? "" : _host.GroupEnabledByConfig(g) ? " (compiled in now)" : " (compiled out now; bench forces it in)").Append('\n');
                foreach (SweepSpec w in s.Sweeps)
                    sb.Append("  sweep: .vvbench sweep ").Append(w.SettingCode)
                      .Append(w.Values != null ? " " + string.Join(",", w.Values.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))) : "").Append('\n');
                int owned = settings.Count(t => reg.ForSettingPath(t.Path) == s);
                if (owned > 0) sb.Append("  panel settings owned: ").Append(owned).Append(" (any can be swept)\n");
                foreach (string r in s.Renderers.Concat(s.CpuScopes))
                {
                    CpuSpans.Stat st = CpuSpans.All.FirstOrDefault(x => x.Name == r);
                    sb.Append("  cpu: ").Append(r).Append(st != null ? " " + st.MsPerFrame.ToString("0.000", CultureInfo.InvariantCulture) + " ms/frame now" : " (not running)").Append('\n');
                }
                foreach (string k in s.GpuSpans) sb.Append("  gpu span: ").Append(k).Append('\n');
                if (s.Scenes.Count > 0) sb.Append("  scenes: ").Append(string.Join(", ", s.Scenes)).Append('\n');
                if (s.Notes != null) sb.Append("  note: ").Append(s.Notes).Append('\n');
            }
            return sb.ToString();
        }

        private void DisposeProfiler()
        {
            if (Current == this) Current = null;
            _pendingScenes.Clear();
            _runner?.Dispose();
            _runner = null;
            StopProbe();
            BenchOverrides.Clear();
        }
    }
}
