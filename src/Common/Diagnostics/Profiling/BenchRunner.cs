using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;


namespace VintageVisuals.Common.Diagnostics.Profiling
{
    /// <summary>One configuration a plan measures: "atmosphere compiled out", "caustics 0.5".</summary>
    public sealed class BenchVariant
    {
        public string Key;
        public string Label;

        /// <summary>Puts this variant in place (bench overrides, in-memory config). Must not save.</summary>
        public Action Apply;

        /// <summary>Applying it changes patch gating, so the runner waits for the shader reload.</summary>
        public bool Reloads;
    }

    /// <summary>
    /// One question a bench answers: a baseline and its alternatives, run in
    /// interleaved rounds. The first variant is the baseline every delta is
    /// against.
    /// </summary>
    public sealed class BenchPlan
    {
        public string Kind;      // groups | sweep | custom
        public string Subject;   // the group or setting code
        public string System;    // profile id, or "(unclaimed)"
        public string Title;
        public readonly List<BenchVariant> Variants = new List<BenchVariant>();

        /// <summary>Puts the player's configuration back. Always runs, even on abort.</summary>
        public Action Restore;

        /// <summary>Printed with the result ("forced in: the config has it off").</summary>
        public string Note;

        /// <summary>Why the plan cannot run here; set by the builder, reported instead of runs.</summary>
        public string Skip;
    }

    /// <summary>One recorded run.</summary>
    public sealed class BenchRun
    {
        public BenchPlan Plan;
        public BenchVariant Variant;
        public int Round;
        public double Seconds;
        public FrameProfiler.BenchResult Frame;
        public CpuSpans.BenchSnapshot Cpu;
        public GpuTelemetry.RunStats Gpu;
        public string Scene;

        public double Segment(string key)
        {
            foreach (var s in Frame.Segments) if (s.Key == key) return s.Gpu;
            return double.NaN;
        }

        public double SegmentCpu(string key)
        {
            foreach (var s in Frame.Segments) if (s.Key == key) return s.Cpu;
            return double.NaN;
        }

        /// <summary>The GPU frame as the markers see it, or NaN without timestamps.</summary>
        public double GpuFrame { get { return Segment("frame"); } }

        /// <summary>GPU-bound when the GPU's frame is within 10 % of the wall frame; CPU-bound otherwise.</summary>
        public string Bound
        {
            get
            {
                double g = GpuFrame;
                if (double.IsNaN(g) || Frame.FrameAvg <= 0) return "unknown";
                double r = g / Frame.FrameAvg;
                return r >= 0.9 ? "gpu" : r >= 0.75 ? "mixed" : "cpu";
            }
        }
    }

    /// <summary>
    /// D99. The profiler's one engine: runs a queue of <see cref="BenchPlan"/>s,
    /// each as interleaved rounds A B, B A, A B (an ABBA order, so a linear
    /// drift in clocks or the scene cancels out of the paired differences),
    /// settling after every change, and reports for each variant the median
    /// paired difference against the baseline and its spread. A difference
    /// inside the spread is reported as noise, not as a cost.
    ///
    /// Driven from a 100 ms game tick, so it never blocks a frame. Every exit
    /// path - the end, '.vvbench stop', leaving the world - runs each plan's
    /// Restore and clears <see cref="BenchOverrides"/>.
    /// </summary>
    public sealed class BenchRunner : IDisposable
    {
        public const float ConfigSettleSeconds = 1.5f;
        public const float ReloadSettleSeconds = 5f;
        public const float ReloadTimeoutSeconds = 20f;

        private readonly ICoreClientAPI _capi;
        private readonly ILogger _logger;
        private readonly FrameProfiler _profiler;
        private readonly IProfilerHost _host;
        private readonly Action<string> _report;
        private long _tick;

        private abstract class Step
        {
            public abstract void Start(BenchRunner r);
            /// <summary>True when finished; called every tick after Start.</summary>
            public abstract bool Poll(BenchRunner r, double elapsed);
        }

        private sealed class ActionStep : Step
        {
            private readonly Action<BenchRunner> _a;
            public ActionStep(Action<BenchRunner> a) { _a = a; }
            public override void Start(BenchRunner r) { _a(r); }
            public override bool Poll(BenchRunner r, double elapsed) { return true; }
        }

        private sealed class WaitStep : Step
        {
            private readonly System.Func<BenchRunner, bool> _until;
            private readonly double _min, _timeout;
            private readonly string _what;
            public WaitStep(double min, System.Func<BenchRunner, bool> until = null, double timeout = 0, string what = null)
            { _min = min; _until = until; _timeout = timeout; _what = what; }
            public override void Start(BenchRunner r) { }
            public override bool Poll(BenchRunner r, double elapsed)
            {
                if (elapsed < _min) return false;
                if (_until == null || _until(r)) return true;
                if (_timeout > 0 && elapsed > _timeout)
                {
                    r.Log("bench: gave up waiting for " + (_what ?? "a condition") + " after " +
                          elapsed.ToString("0", CultureInfo.InvariantCulture) + " s; recording anyway.");
                    return true;
                }
                return false;
            }
        }

        private sealed class VariantStep : Step
        {
            private readonly BenchPlan _plan;
            private readonly BenchVariant _v;
            private int _loadsBefore;
            private bool _expectReload;
            private bool _reloadSeen;
            private double _reloadAt;
            public VariantStep(BenchPlan plan, BenchVariant v) { _plan = plan; _v = v; }
            public override void Start(BenchRunner r)
            {
                _loadsBefore = r._host.ShaderLoads;
                string before = r._host.GatingSignature;
                r._current = _v;
                try { _v.Apply?.Invoke(); } catch (Exception ex) { r.Log("bench: variant '" + _v.Label + "' threw on apply: " + ex.Message); }
                r._host.Reapply();
                // Whether this change moved patch gating decides the wait: a
                // shader reload (seconds, then a settle) or a uniform change.
                _expectReload = r._host.GatingSignature != before;
            }
            public override bool Poll(BenchRunner r, double elapsed)
            {
                if (!_expectReload) return elapsed >= ConfigSettleSeconds;
                if (!_reloadSeen && r._host.ShaderLoads != _loadsBefore) { _reloadSeen = true; _reloadAt = elapsed; }
                if (_reloadSeen) return elapsed - _reloadAt >= ReloadSettleSeconds;
                if (elapsed > ReloadTimeoutSeconds)
                {
                    r.Log("bench: no shader reload arrived for '" + _v.Label + "' within " + ReloadTimeoutSeconds + " s; recording anyway.");
                    return true;
                }
                return false;
            }
        }

        private sealed class RecordStep : Step
        {
            private readonly BenchPlan _plan;
            private readonly BenchVariant _v;
            private readonly int _round;
            private readonly double _seconds;
            public RecordStep(BenchPlan plan, BenchVariant v, int round, double seconds) { _plan = plan; _v = v; _round = round; _seconds = seconds; }
            public override void Start(BenchRunner r)
            {
                r._profiler.BeginBench();
                CpuSpans.BeginBench();
                GpuTelemetry.Shared?.BeginRun();
            }
            public override bool Poll(BenchRunner r, double elapsed)
            {
                if (elapsed < _seconds) return false;
                var run = new BenchRun
                {
                    Plan = _plan,
                    Variant = _v,
                    Round = _round,
                    Seconds = elapsed,
                    Frame = r._profiler.EndBench(_plan.Title + " / " + _v.Label + " #" + (_round + 1)),
                    Cpu = CpuSpans.EndBench(),
                    Gpu = GpuTelemetry.Shared?.EndRun(),
                    Scene = r.Scene,
                };
                r._runs.Add(run);
                r.Results?.AppendRun(r.Context, run);
                return true;
            }
        }

        private readonly Queue<Step> _steps = new Queue<Step>();
        private Step _active;
        private double _activeStart;
        private readonly List<BenchRun> _runs = new List<BenchRun>();
        private readonly List<BenchPlan> _plans = new List<BenchPlan>();
        private BenchVariant _current;
        private DateTime _startedAt;
        private int _totalSteps;
        private int _doneSteps;

        public BenchRunner(ICoreClientAPI capi, ILogger logger, FrameProfiler profiler, IProfilerHost host, Action<string> report)
        {
            _capi = capi;
            _logger = logger;
            _profiler = profiler;
            _host = host;
            _report = report;
        }

        public bool Running { get { return _active != null || _steps.Count > 0; } }

        /// <summary>The scene the current runs are in, for the results file.</summary>
        public string Scene { get; set; }

        public BenchContext Context { get; private set; }
        public BenchResultsFile Results { get; private set; }

        /// <summary>"3 of 40 runs, about 6 min left" while running.</summary>
        public string Progress
        {
            get
            {
                if (!Running) return null;
                double perStep = _doneSteps > 0 ? (DateTime.Now - _startedAt).TotalSeconds / _doneSteps : 0;
                int left = Math.Max(0, _totalSteps - _doneSteps);
                return _doneSteps + " of " + _totalSteps + " steps" +
                       (perStep > 0 ? ", about " + Math.Ceiling(perStep * left / 60.0).ToString(CultureInfo.InvariantCulture) + " min left" : "");
            }
        }

        public string Title { get; private set; }

        /// <summary>
        /// Queues the plans and starts. <paramref name="before"/> runs first (a
        /// scene teleport); its steps are the caller's.
        /// </summary>
        public void Start(string title, IList<BenchPlan> plans, int rounds, double seconds, string scene,
                          Action<BenchRunner> prelude = null)
        {
            if (Running) throw new InvalidOperationException("a bench is already running");
            _runs.Clear();
            _plans.Clear();
            Summaries.Clear();
            _steps.Clear();
            Title = title;
            Scene = scene;
            _startedAt = DateTime.Now;
            _doneSteps = 0;

            prelude?.Invoke(this);

            _steps.Enqueue(new ActionStep(r =>
            {
                r.Context = BenchContext.Capture(r._capi, r.Scene);
                r.Results = BenchResultsFile.Open(r._capi, r._logger, r.Context);
                r.Results.BenchLabel = title;
                r.Log("bench " + title + ": " + r.Context.Describe());
            }));

            foreach (BenchPlan plan in plans)
            {
                _plans.Add(plan);
                if (plan.Skip != null) continue;
                BenchPlan p = plan;
                for (int round = 0; round < rounds; round++)
                {
                    // ABBA: odd rounds run the variants backwards.
                    IEnumerable<BenchVariant> order = round % 2 == 0 ? p.Variants : Enumerable.Reverse(p.Variants);
                    foreach (BenchVariant v in order)
                    {
                        _steps.Enqueue(new VariantStep(p, v));
                        _steps.Enqueue(new RecordStep(p, v, round, seconds));
                    }
                }
                _steps.Enqueue(new ActionStep(r => r.FinishPlan(p)));
            }
            _steps.Enqueue(new ActionStep(r => r.FinishAll()));
            _totalSteps = _steps.Count;

            if (_tick == 0) _tick = _capi.Event.RegisterGameTickListener(OnTick, 100);
        }

        /// <summary>Prelude only (inside Start's callback): run an action before the plans.</summary>
        public void PreludeAction(Action action) { _steps.Enqueue(new ActionStep(_ => action())); }

        /// <summary>Prelude only: wait at least <paramref name="min"/> s, then until the condition holds (timeout in s).</summary>
        public void PreludeWait(double min, System.Func<bool> until, double timeout, string what)
        {
            _steps.Enqueue(new WaitStep(min, until == null ? (System.Func<BenchRunner, bool>)null : _ => until(), timeout, what));
        }

        /// <summary>Called after the last plan and after an abort (with false), on the main thread.</summary>
        public Action<bool> Finished;

        /// <summary>Estimated seconds a queue would take, for the start message.</summary>
        public static double Estimate(IList<BenchPlan> plans, int rounds, double seconds)
        {
            double total = 0;
            foreach (BenchPlan p in plans)
            {
                if (p.Skip != null) continue;
                foreach (BenchVariant v in p.Variants)
                    total += rounds * (seconds + (v.Reloads ? ReloadSettleSeconds + 2 : ConfigSettleSeconds));
            }
            return total;
        }

        private void OnTick(float dt)
        {
            try
            {
                double now = _capi.ElapsedMilliseconds / 1000.0;
                while (true)
                {
                    if (_active == null)
                    {
                        if (_steps.Count == 0) { StopTicking(); return; }
                        _active = _steps.Dequeue();
                        _activeStart = now;
                        _active.Start(this);
                    }
                    if (!_active.Poll(this, now - _activeStart)) return;
                    _active = null;
                    _doneSteps++;
                }
            }
            catch (Exception ex)
            {
                Log("bench: stopped by an error: " + ex.Message);
                _logger.Error(ex.ToString());
                Abort("an error");
            }
        }

        private void StopTicking()
        {
            if (_tick != 0) _capi.Event.UnregisterGameTickListener(_tick);
            _tick = 0;
        }

        /// <summary>Stops now, puts every setting back and reports what finished.</summary>
        public void Abort(string why)
        {
            if (!Running && _tick == 0) return;
            if (_profiler.Recording) _profiler.EndBench("aborted");
            CpuSpans.EndBench();
            GpuTelemetry.Shared?.EndRun();
            _steps.Clear();
            _active = null;
            StopTicking();
            RestoreAll();
            foreach (BenchPlan p in _plans.Where(p => _runs.Any(r => r.Plan == p))) Summarise(p);
            try { Finished?.Invoke(false); } catch { }
            _report("bench " + Title + " stopped (" + why + "); settings restored. " + _runs.Count + " run(s) kept" +
                    (Results != null ? " in " + Results.RunsPath : "") + ".");
        }

        private void RestoreAll()
        {
            foreach (BenchPlan p in _plans)
            {
                try { p.Restore?.Invoke(); } catch (Exception ex) { Log("bench: restore of '" + p.Title + "' threw: " + ex.Message); }
            }
            BenchOverrides.Clear();
            _host.Reapply();
        }

        private void FinishPlan(BenchPlan p)
        {
            try { p.Restore?.Invoke(); } catch (Exception ex) { Log("bench: restore of '" + p.Title + "' threw: " + ex.Message); }
            BenchOverrides.Clear();
            _host.Reapply();
            Summarise(p);
        }

        /// <summary>The per-plan summaries, for the panel and the suite report.</summary>
        public readonly List<PlanSummary> Summaries = new List<PlanSummary>();

        private void Summarise(BenchPlan p)
        {
            PlanSummary s = PlanSummary.From(p, _runs.Where(r => r.Plan == p).ToList());
            Summaries.Add(s);
            Results?.AppendSummary(Context, s);
            _report(s.Format());
        }

        private void FinishAll()
        {
            RestoreAll();
            var sb = new StringBuilder();
            sb.Append("bench ").Append(Title).Append(" done: ").Append(_runs.Count).Append(" runs in ")
              .Append(((DateTime.Now - _startedAt).TotalMinutes).ToString("0.0", CultureInfo.InvariantCulture)).Append(" min.");
            foreach (BenchPlan p in _plans.Where(p => p.Skip != null))
                sb.Append("\n  skipped ").Append(p.Title).Append(": ").Append(p.Skip);
            string warn = Context?.Warnings(_runs.Count > 0 ? _runs.Average(r => r.Frame.FrameAvg) : 0);
            if (!string.IsNullOrEmpty(warn)) sb.Append("\n  WARNING ").Append(warn);
            if (_runs.Any(r => r.Gpu != null && GpuTelemetry.Throttled(r.Gpu.Throttle)))
                sb.Append("\n  note: the GPU reported clock limits in some runs (see the gpu_throttle column).");
            if (Results != null) sb.Append("\n  results: ").Append(Results.RunsPath).Append(" and ").Append(Results.SummaryPath);
            _report(sb.ToString());
            try { Finished?.Invoke(true); } catch { }
        }

        private void Log(string line) { _logger.Notification("[VintageVisuals] " + line); }

        public void Dispose()
        {
            if (Running) Abort("leaving the world");
            StopTicking();
        }
    }

    /// <summary>A plan's result: per variant, the median paired delta to the baseline and its spread.</summary>
    public sealed class PlanSummary
    {
        public BenchPlan Plan;
        public string Bound;
        public readonly List<Row> Rows = new List<Row>();

        public sealed class Row
        {
            public BenchVariant Variant;
            public int Runs;
            public double FrameMedian, P99Median, GpuFrameMedian, OpaqueMedian, ModCpuMedian;

            /// <summary>Median of (this - baseline) over the rounds, in ms; positive = this costs more.</summary>
            public double Delta, DeltaMin, DeltaMax;
            public double GpuDelta, GpuDeltaMin, GpuDeltaMax;
            public double CpuDelta;

            /// <summary>
            /// The noise floor: half the paired spread of the PRIMARY figure, never below 0.05 ms.
            /// Judged on the figure the cost is reported in: one round's frame-time hitch
            /// (a chunk upload, a GC) must not hide a GPU saving that held to 0.2 ms in every
            /// round, which is what the first bench in game did to the depth prepass.
            /// </summary>
            public double Noise
            {
                get
                {
                    double spread = double.IsNaN(GpuDelta) ? DeltaMax - DeltaMin : GpuDeltaMax - GpuDeltaMin;
                    return Math.Max(0.05, spread / 2);
                }
            }
            public bool Significant { get { return Math.Abs(Primary) > Noise; } }

            /// <summary>The GPU delta when timestamps exist (the frame can hide it when CPU-bound), else the frame delta.</summary>
            public double Primary { get { return double.IsNaN(GpuDelta) ? Delta : GpuDelta; } }
        }

        public static PlanSummary From(BenchPlan plan, List<BenchRun> runs)
        {
            var s = new PlanSummary { Plan = plan };
            if (runs.Count == 0) return s;
            var bounds = runs.GroupBy(r => r.Bound).OrderByDescending(g => g.Count()).First().Key;
            s.Bound = bounds;
            BenchVariant baseline = plan.Variants[0];
            var baseByRound = runs.Where(r => r.Variant == baseline).ToDictionary(r => r.Round);

            foreach (BenchVariant v in plan.Variants)
            {
                List<BenchRun> mine = runs.Where(r => r.Variant == v).ToList();
                var row = new Row
                {
                    Variant = v,
                    Runs = mine.Count,
                    FrameMedian = Median(mine.Select(r => r.Frame.FrameAvg)),
                    P99Median = Median(mine.Select(r => r.Frame.FrameP99)),
                    GpuFrameMedian = Median(mine.Select(r => r.GpuFrame)),
                    OpaqueMedian = Median(mine.Select(r => r.Segment("opaque"))),
                    ModCpuMedian = Median(mine.Select(r => r.Cpu != null ? r.Cpu.TotalMsPerFrame : double.NaN)),
                };
                var d = new List<double>();
                var g = new List<double>();
                var c = new List<double>();
                foreach (BenchRun r in mine)
                {
                    if (!baseByRound.TryGetValue(r.Round, out BenchRun b)) continue;
                    d.Add(r.Frame.FrameAvg - b.Frame.FrameAvg);
                    if (!double.IsNaN(r.GpuFrame) && !double.IsNaN(b.GpuFrame)) g.Add(r.GpuFrame - b.GpuFrame);
                    if (r.Cpu != null && b.Cpu != null) c.Add(r.Cpu.TotalMsPerFrame - b.Cpu.TotalMsPerFrame);
                }
                row.Delta = Median(d); row.DeltaMin = d.Count > 0 ? d.Min() : double.NaN; row.DeltaMax = d.Count > 0 ? d.Max() : double.NaN;
                row.GpuDelta = Median(g); row.GpuDeltaMin = g.Count > 0 ? g.Min() : double.NaN; row.GpuDeltaMax = g.Count > 0 ? g.Max() : double.NaN;
                row.CpuDelta = Median(c);
                if (double.IsNaN(row.GpuDeltaMin)) { row.GpuDeltaMin = row.GpuDeltaMax = 0; }
                s.Rows.Add(row);
            }
            return s;
        }

        internal static double Median(IEnumerable<double> values)
        {
            double[] v = values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
            if (v.Length == 0) return double.NaN;
            return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
        }

        private static string Ms(double v) { return double.IsNaN(v) ? "n/a" : v.ToString("0.00", CultureInfo.InvariantCulture); }
        private static string Signed(double v) { return double.IsNaN(v) ? "n/a" : (v >= 0 ? "+" : "") + v.ToString("0.00", CultureInfo.InvariantCulture); }

        public string Format()
        {
            var sb = new StringBuilder();
            sb.Append(Plan.Title).Append(" [").Append(Plan.System).Append("]");
            if (!string.IsNullOrEmpty(Bound)) sb.Append(", ").Append(Bound).Append("-bound");
            sb.Append('\n');
            if (!string.IsNullOrEmpty(Plan.Note)) sb.Append("  note: ").Append(Plan.Note).Append('\n');
            foreach (Row r in Rows)
            {
                sb.Append("  ").Append(r.Variant.Label.PadRight(32))
                  .Append(" frame ").Append(Ms(r.FrameMedian).PadLeft(6))
                  .Append(" gpu ").Append(Ms(r.GpuFrameMedian).PadLeft(6))
                  .Append(" p99 ").Append(Ms(r.P99Median).PadLeft(6));
                if (r.Variant != Plan.Variants[0])
                {
                    sb.Append("  cost ").Append(Signed(r.Primary))
                      .Append(" ms (spread ").Append(Signed(double.IsNaN(r.GpuDelta) ? r.DeltaMin : r.GpuDeltaMin))
                      .Append("..").Append(Signed(double.IsNaN(r.GpuDelta) ? r.DeltaMax : r.GpuDeltaMax)).Append(")")
                      .Append(r.Significant ? "" : " noise");
                    if (!double.IsNaN(r.CpuDelta) && Math.Abs(r.CpuDelta) >= 0.02) sb.Append(" mod cpu ").Append(Signed(r.CpuDelta));
                }
                else sb.Append("  baseline");
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
