using System;
using System.Globalization;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageVisuals.Common.Diagnostics
{
    /// <summary>
    /// The fps baseline: the frame-time readout, the bench commands and the
    /// quality tier, in one place so the mod system only creates and feeds it.
    ///
    ///   .vvperf                 toggle the frame-time readout
    ///   .vvbench [seconds]      record the current settings (default 10 s)
    ///   .vvbench leaves [secs]  record no falling leaves, the old 20-block
    ///                           reach and the new one in turn (D78)
    ///   .vvbench tiers [secs]   record Low, Medium, High, Ultra in turn, then
    ///                           restore the configured tier (default 8 s each)
    ///   .vvbench features [s]   everything on, then each terrain/entity
    ///                           feature off in turn, then everything on again,
    ///                           with each run's cost against the first
    ///                           (default 6 s each)
    ///
    /// Bench results go to chat and to client-main.log, prefixed
    /// "[VintageVisuals] bench", so they can be read back later. Stand still
    /// in the fixed test scene (docs/VISUAL-TESTS.md, "Performance baseline")
    /// while a bench runs: the numbers compare like with like only when the
    /// view does not change.
    /// </summary>
    public sealed partial class PerformanceTools : IDisposable
    {
        private const float SettleSeconds = 1.5f;

        private readonly ICoreClientAPI _capi;
        private readonly ILogger _logger;
        private readonly FrameProfiler _profiler;
        private FrameTimeHud _hud;
        private bool _hudWanted;
        private bool _hudAcquired;

        private long _benchCallback;
        private readonly List<FrameProfiler.BenchResult> _tierResults = new List<FrameProfiler.BenchResult>();

        private readonly Action _reapply;

        /// <param name="reapply">Re-applies the config, so a bench override that gates a shader patch reloads the shaders.</param>
        public PerformanceTools(ICoreClientAPI capi, ILogger logger, Action reapply = null,
                                Profiling.IProfilerHost host = null)
        {
            _capi = capi;
            _logger = logger;
            _reapply = reapply;
            _profiler = new FrameProfiler(capi, logger);
            _host = host;
            RegisterCommands();
            RegisterProfilerCommands();
        }

        public FrameProfiler Profiler { get { return _profiler; } }

        /// <summary>Called whenever the config changes.</summary>
        public void Apply(PerformanceConfig config)
        {
            QualityTier.SetConfigured(config.Quality);
            ProfilerSeconds = config.ProfilerSeconds;
            ProfilerRounds = Math.Max(1, (int)Math.Round(config.ProfilerRounds));
            FrameTimeHud.ShowCpuSpans = config.ProfilerCpuInReadout;
            SetHud(config.ShowFrameTimes);
        }

        private void SetHud(bool wanted)
        {
            _hudWanted = wanted;
            if (wanted && !_hudAcquired)
            {
                _profiler.Acquire();
                _hudAcquired = true;
                if (_hud == null) _hud = new FrameTimeHud(_capi, _profiler);
                _hud.TryOpen();
            }
            else if (!wanted && _hudAcquired)
            {
                _hudAcquired = false;
                _profiler.HitchTrace = null;
            _hud?.TryClose();
                _profiler.Release();
            }
        }

        private void RegisterCommands()
        {
            try
            {
                var parsers = _capi.ChatCommands.Parsers;

                _capi.ChatCommands.Create("vvperf")
                    .WithDescription("Vintage Visuals: show or hide the frame-time readout")
                    .HandleWith(_ =>
                    {
                        SetHud(!_hudWanted);
                        return TextCommandResult.Success("Frame-time readout " + (_hudWanted ? "on" : "off") +
                            (_profiler.GpuAvailable || !_hudWanted ? "" : " (GPU timing not available yet)"));
                    });

                _capi.ChatCommands.Create("vvhitch")
                    .WithDescription("Vintage Visuals: log every frame hitch with its CPU time per render stage and garbage collections")
                    .HandleWith(_ => ToggleHitch());

                _capi.ChatCommands.Create("vvbench")
                    .WithDescription("Vintage Visuals: record frame times. '.vvbench [seconds]', '.vvbench tiers [seconds]', '.vvbench leaves [seconds]', '.vvbench features [seconds]', '.vvbench beams [seconds]', '.vvbench prepass [seconds]', '.vvbench debugcode [seconds]', '.vvbench beamcode [seconds]', '.vvbench vanilla [seconds]'; profiler: '.vvbench groups [all|active|group,..]', '.vvbench sweep <setting|system> [v1,v2,..]', '.vvbench suite [quick]', each with [seconds] [rN] [@scene], and '.vvbench stop|status'")
                    .WithArgs(parsers.OptionalAll("args"))
                    .HandleWith(args =>
                    {
                        string all = (args[0] as string ?? "").Trim();
                        string[] words = all.Length == 0 ? new string[0] : all.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        // D99: the profiler's modes take their own argument lists.
                        TextCommandResult profiled = TryProfilerBench(words);
                        if (profiled != null) return profiled;

                        string mode = words.Length > 0 ? words[0] : null;
                        int seconds = 0;
                        if (words.Length > 1) int.TryParse(words[1], out seconds);
                        if (int.TryParse(mode, out int asNumber)) { seconds = asNumber; mode = null; }
                        return StartBench(mode, seconds);
                    });
            }
            catch (Exception ex)
            {
                _logger.Warning("[VintageVisuals] could not register .vvperf/.vvbench: " + ex.Message);
            }
        }

        private bool _hitchOn;

        /// <summary>'.vvhitch': log every hitch (frame over twice the median) with its CPU stages and GC.</summary>
        private TextCommandResult ToggleHitch()
        {
            _hitchOn = !_hitchOn;
            if (_hitchOn)
            {
                _profiler.HitchTrace = line => _logger.Notification("[VintageVisuals] " + line);
                _profiler.Acquire();
                return TextCommandResult.Success("Hitch trace on: every frame over twice the median is logged to client-main.log " +
                    "with its CPU time per stage and any garbage collection. Play normally; '.vvhitch' again to stop.");
            }
            _profiler.HitchTrace = null;
            _profiler.Release();
            return TextCommandResult.Success("Hitch trace off.");
        }

        private TextCommandResult StartBench(string mode, int seconds)
        {
            if (_profiler.Recording || _benchCallback != 0 || (_runner != null && _runner.Running))
            {
                return TextCommandResult.Error("A bench is already running ('.vvbench stop' ends a profiler bench).");
            }

            if (string.Equals(mode, "leaves", StringComparison.OrdinalIgnoreCase))
            {
                int each = seconds > 0 ? seconds : 10;
                _tierResults.Clear();
                RunLeaves(LeafBench.Off, each);
                return TextCommandResult.Success("Benchmarking falling leaves: none, the old 20-block reach, then the new reach, " +
                    LeafBench.WarmSeconds.ToString("0") + " s to settle and " + each +
                    " s recorded each. Stand still in a forest, facing the trees.");
            }

            if (string.Equals(mode, "beams", StringComparison.OrdinalIgnoreCase))
            {
                int each = seconds > 0 ? seconds : 8;
                _tierResults.Clear();
                // D91: the "terrain shader" runs need the terrain's own march in
                // source, so hold it there for the whole bench (one reload each end).
                VintageVisuals.Common.Rendering.ShaderSpecialisation.BeamBenchHoldsMarch = true;
                _reapply?.Invoke();
                RunFeature(FeatureBench.BeamOrder, 0, each);
                return TextCommandResult.Success("Benchmarking the beams: in the terrain shader, in the fast pass, no air beams, no shafts at all, " +
                    "then the terrain shader again, " + each + " s each. Stand still under trees facing the sun.");
            }

            if (string.Equals(mode, "prepass", StringComparison.OrdinalIgnoreCase))
            {
                int each = seconds > 0 ? seconds : 8;
                _tierResults.Clear();
                RunPrepass(0, each);
                return TextCommandResult.Success("Benchmarking the depth prepass: both pools, leaves only, off, twice, " + each +
                    " s each. Stand still in a forest, facing the trees." +
                    (VintageVisuals.Common.Rendering.DepthPrepass.ShaderReady() ? "" :
                        " The prepass shader group is not applied (Performance.DepthPrepass off?), so 'on' runs will match 'off'."));
            }

            if (string.Equals(mode, "debugcode", StringComparison.OrdinalIgnoreCase))
            {
                if (_reapply == null) return TextCommandResult.Error("This build cannot reload shaders from a bench.");
                int each = seconds > 0 ? seconds : 8;
                _tierResults.Clear();
                RunSpecialisation("debug views", m => VintageVisuals.Common.Rendering.ShaderSpecialisation.DebugCodeBenchOverride = m, 0, each);
                return TextCommandResult.Success("Benchmarking the terrain debug views compiled out, in, out, in (view 0 throughout), " + each +
                    " s each after a shader reload. Stand still in a forest, facing the trees. The world may flicker at each reload.");
            }

            if (string.Equals(mode, "vanilla", StringComparison.OrdinalIgnoreCase))
            {
                if (_reapply == null) return TextCommandResult.Error("This build cannot reload shaders from a bench.");
                int each = seconds > 0 ? seconds : 8;
                _tierResults.Clear();
                RunVanilla(0, each);
                return TextCommandResult.Success("Benchmarking the mod against the game's own shaders: mod, vanilla, vanilla with the depth prepass, twice, " +
                    each + " s each after a shader reload. Stand still in a forest, facing the trees. The world changes look at each reload.");
            }

            if (string.Equals(mode, "beamcode", StringComparison.OrdinalIgnoreCase))
            {
                if (_reapply == null) return TextCommandResult.Error("This build cannot reload shaders from a bench.");
                int each = seconds > 0 ? seconds : 8;
                _tierResults.Clear();
                // "compiled in" = the march held in the terrain source; "out" = as configured
                // (out only while FastBeams is on and the pass is healthy).
                RunSpecialisation("terrain beam march",
                    m => VintageVisuals.Common.Rendering.ShaderSpecialisation.BeamCodeBenchHoldsMarch = m == 1, 0, each);
                return TextCommandResult.Success("Benchmarking the terrain's beam march compiled out, in, out, in (needs FastBeams on), " + each +
                    " s each after a shader reload. Stand still under trees facing the sun. The world may flicker at each reload.");
            }

            if (string.Equals(mode, "features", StringComparison.OrdinalIgnoreCase))
            {
                int each = seconds > 0 ? seconds : 6;
                _tierResults.Clear();
                RunFeature(FeatureBench.Order, 0, each);
                return TextCommandResult.Success("Benchmarking " + FeatureBench.Order.Length + " runs of " + each +
                    " s: everything on, each feature off in turn, then everything on again. About " +
                    (int)(FeatureBench.Order.Length * (each + SettleSeconds) + 0.5f) +
                    " s. Stand still and don't touch anything.");
            }

            if (string.Equals(mode, "tiers", StringComparison.OrdinalIgnoreCase))
            {
                int each = seconds > 0 ? seconds : 8;
                _tierResults.Clear();
                RunTier(QualityTier.Low, each);
                return TextCommandResult.Success("Benchmarking Low, Medium, High and Ultra, " + each +
                    " s each. Stand still and don't touch anything.");
            }

            int length = seconds > 0 ? seconds : 10;
            _benchCallback = _capi.Event.RegisterCallback(_ =>
            {
                _profiler.BeginBench();
                _benchCallback = _capi.Event.RegisterCallback(__ =>
                {
                    _benchCallback = 0;
                    Report(_profiler.EndBench("bench (quality " + QualityTier.Name(QualityTier.Current) + ")"));
                }, length * 1000);
            }, (int)(SettleSeconds * 1000));
            return TextCommandResult.Success("Recording " + length + " s of frames. Stand still.");
        }

        private void RunTier(int tier, int seconds)
        {
            QualityTier.SetOverride(tier);
            _benchCallback = _capi.Event.RegisterCallback(_ =>
            {
                _profiler.BeginBench();
                _benchCallback = _capi.Event.RegisterCallback(__ =>
                {
                    _tierResults.Add(_profiler.EndBench("tier " + QualityTier.Name(tier)));
                    if (tier < QualityTier.Ultra)
                    {
                        RunTier(tier + 1, seconds);
                        return;
                    }

                    _benchCallback = 0;
                    QualityTier.SetOverride(-1);
                    var sb = new StringBuilder();
                    foreach (FrameProfiler.BenchResult r in _tierResults) sb.Append(r.Format());
                    Report(sb.ToString());
                }, seconds * 1000);
            }, (int)(SettleSeconds * 1000));
        }

        /// <summary>
        /// D78. One run of '.vvbench leaves': hold the leaf mode, let the
        /// leaves of that reach fill the air, record, then the next mode.
        /// </summary>
        private void RunLeaves(int mode, int seconds)
        {
            LeafBench.Mode = mode;
            _benchCallback = _capi.Event.RegisterCallback(_ =>
            {
                _profiler.BeginBench();
                _benchCallback = _capi.Event.RegisterCallback(__ =>
                {
                    bool off = mode == LeafBench.Off;
                    _tierResults.Add(_profiler.EndBench(LeafBench.Name(mode) +
                        (off ? "" : " - " + LeafBench.Flying + " flying, " + LeafBench.Litter + " lying")));
                    if (mode == LeafBench.Off) { RunLeaves(LeafBench.Legacy, seconds); return; }
                    if (mode == LeafBench.Legacy) { RunLeaves(LeafBench.Normal, seconds); return; }

                    _benchCallback = 0;
                    LeafBench.Mode = LeafBench.Normal;
                    var sb = new StringBuilder();
                    foreach (FrameProfiler.BenchResult r in _tierResults) sb.Append(r.Format());
                    Report(sb.ToString());
                }, seconds * 1000);
            }, (int)(LeafBench.WarmSeconds * 1000));
        }

        /// <summary>
        /// One run of '.vvbench features': hold one feature off, record, then
        /// the next. The report gives each run's frame and opaque-pass time
        /// against the first all-on run, which is the price of that feature.
        /// </summary>
        private void RunFeature(int[] order, int index, int seconds)
        {
            int mode = order[index];
            FeatureBench.Mode = mode;
            _benchCallback = _capi.Event.RegisterCallback(_ =>
            {
                _profiler.BeginBench();
                _benchCallback = _capi.Event.RegisterCallback(__ =>
                {
                    _tierResults.Add(_profiler.EndBench(FeatureBench.Name(mode)));
                    if (index + 1 < order.Length) { RunFeature(order, index + 1, seconds); return; }

                    _benchCallback = 0;
                    FeatureBench.Mode = FeatureBench.Normal;
                    if (VintageVisuals.Common.Rendering.ShaderSpecialisation.BeamBenchHoldsMarch)
                    {
                        VintageVisuals.Common.Rendering.ShaderSpecialisation.BeamBenchHoldsMarch = false;
                        _reapply?.Invoke();
                    }
                    Report(FormatFeatures(_tierResults));
                }, seconds * 1000);
            }, (int)((index == 0 && VintageVisuals.Common.Rendering.ShaderSpecialisation.BeamBenchHoldsMarch
                        ? ReloadSettleSeconds : SettleSeconds) * 1000));
        }

        /// <summary>
        /// '.vvbench prepass' (D88): both pools, leaves only, off, twice, so drift shows
        /// as the two rounds disagreeing. Values are DepthPrepass pool masks; 0 is off.
        /// </summary>
        private static readonly int[] PrepassOrder = { 3, 2, 0, 3, 2, 0 };

        private void RunPrepass(int index, int seconds)
        {
            int mode = PrepassOrder[index];
            VintageVisuals.Common.Rendering.DepthPrepass.BenchOverride = mode == 0 ? 0 : 1;
            VintageVisuals.Common.Rendering.DepthPrepass.BenchPoolMask = mode == 0 ? -1 : mode;
            _benchCallback = _capi.Event.RegisterCallback(_ =>
            {
                _profiler.BeginBench();
                _benchCallback = _capi.Event.RegisterCallback(__ =>
                {
                    bool ran = VintageVisuals.Common.Rendering.DepthPrepass.RanLastFrame;
                    _tierResults.Add(_profiler.EndBench(mode != 0
                        ? (mode == 3 ? "depth prepass on" : "depth prepass, leaves only") + (ran ? "" : " (DID NOT RUN)")
                        : "depth prepass off"));
                    if (index + 1 < PrepassOrder.Length) { RunPrepass(index + 1, seconds); return; }

                    _benchCallback = 0;
                    VintageVisuals.Common.Rendering.DepthPrepass.BenchOverride = -1;
                    VintageVisuals.Common.Rendering.DepthPrepass.BenchPoolMask = -1;
                    Report(FormatFeatures(_tierResults));
                }, seconds * 1000);
            }, (int)(SettleSeconds * 1000));
        }

        /// <summary>'.vvbench debugcode' (D90): compiled out, in, out, in, each after a shader reload.</summary>
        private static readonly int[] DebugCodeOrder = { 0, 1, 0, 1 };
        private const float ReloadSettleSeconds = 5f;

        /// <summary>'.vvbench vanilla': mod, vanilla, vanilla + prepass, twice, so drift shows.</summary>
        private static readonly int[] VanillaOrder = { 0, 1, 2, 0, 1, 2 };

        private void RunVanilla(int index, int seconds)
        {
            int mode = VanillaOrder[index];
            VintageVisuals.Common.Rendering.ShaderSpecialisation.VanillaBench = mode;
            _reapply();
            _benchCallback = _capi.Event.RegisterCallback(_ =>
            {
                _profiler.BeginBench();
                _benchCallback = _capi.Event.RegisterCallback(__ =>
                {
                    _tierResults.Add(_profiler.EndBench(mode == 0 ? "the mod, as configured"
                        : mode == 1 ? "vanilla shaders" : "vanilla shaders + depth prepass"));
                    if (index + 1 < VanillaOrder.Length) { RunVanilla(index + 1, seconds); return; }

                    _benchCallback = 0;
                    VintageVisuals.Common.Rendering.ShaderSpecialisation.VanillaBench = 0;
                    _reapply();
                    Report(FormatFeatures(_tierResults));
                }, seconds * 1000);
            }, (int)(ReloadSettleSeconds * 1000));
        }

        /// <param name="set">1 compiles the code in, 0 out, -1 restores the normal decision.</param>
        private void RunSpecialisation(string what, Action<int> set, int index, int seconds)
        {
            int mode = DebugCodeOrder[index];
            set(mode);
            _reapply();
            _benchCallback = _capi.Event.RegisterCallback(_ =>
            {
                _profiler.BeginBench();
                _benchCallback = _capi.Event.RegisterCallback(__ =>
                {
                    _tierResults.Add(_profiler.EndBench(what + (mode == 1 ? " compiled in" : " compiled out")));
                    if (index + 1 < DebugCodeOrder.Length) { RunSpecialisation(what, set, index + 1, seconds); return; }

                    _benchCallback = 0;
                    set(-1);
                    _reapply();
                    Report(FormatFeatures(_tierResults));
                }, seconds * 1000);
            }, (int)(ReloadSettleSeconds * 1000));
        }

        internal static string FormatFeatures(List<FrameProfiler.BenchResult> results)
        {
            var sb = new StringBuilder();
            if (results.Count == 0) return "no runs";
            FrameProfiler.BenchResult first = results[0];
            double baseOpaque = OpaqueGpu(first);
            sb.Append("features (quality ").Append(QualityTier.Name(QualityTier.Current)).Append("), saving vs the first run\n");
            foreach (FrameProfiler.BenchResult r in results)
            {
                double opaque = OpaqueGpu(r);
                sb.Append("  ").Append(r.Label.PadRight(34))
                  .Append(" frame ").Append(FrameProfiler.Ms(r.FrameAvg).PadLeft(6))
                  .Append(" (").Append(Signed(first.FrameAvg - r.FrameAvg)).Append(")")
                  .Append("  opaque ").Append(double.IsNaN(opaque) ? "  n/a" : FrameProfiler.Ms(opaque).PadLeft(6))
                  .Append(double.IsNaN(opaque) || double.IsNaN(baseOpaque) ? "" : " (" + Signed(baseOpaque - opaque) + ")")
                  .Append('\n');
            }
            sb.Append("full detail:\n");
            foreach (FrameProfiler.BenchResult r in results) sb.Append(r.Format());
            return sb.ToString();
        }

        private static double OpaqueGpu(FrameProfiler.BenchResult r)
        {
            foreach (var s in r.Segments) if (s.Key == "opaque") return s.Gpu;
            return double.NaN;
        }

        private static string Signed(double v)
        {
            return (v >= 0 ? "+" : "") + FrameProfiler.Ms(v);
        }

        private void Report(FrameProfiler.BenchResult result)
        {
            Report(result.Format());
        }

        /// <summary>
        /// The last bench summary this session, for the tuning panel's
        /// Performance tab. Read-only to everyone else; null until a bench ends.
        /// </summary>
        public static string LastBenchReport { get; private set; }

        /// <summary>Local time the last bench ended, "HH:mm".</summary>
        public static string LastBenchAt { get; private set; }

        private void Report(string text)
        {
            LastBenchReport = text;
            LastBenchAt = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            foreach (string line in text.TrimEnd().Split('\n'))
            {
                _logger.Notification("[VintageVisuals] bench " + line);
            }
            _capi.ShowChatMessage(text.TrimEnd());
        }

        public void Dispose()
        {
            DisposeProfiler();
            if (_benchCallback != 0) _capi.Event.UnregisterCallback(_benchCallback);
            QualityTier.SetOverride(-1);
            FeatureBench.Mode = FeatureBench.Normal;
            VintageVisuals.Common.Rendering.DepthPrepass.BenchOverride = -1;
            VintageVisuals.Common.Rendering.ShaderSpecialisation.DebugCodeBenchOverride = -1;
            VintageVisuals.Common.Rendering.ShaderSpecialisation.BeamBenchHoldsMarch = false;
            VintageVisuals.Common.Rendering.ShaderSpecialisation.BeamCodeBenchHoldsMarch = false;
            VintageVisuals.Common.Rendering.ShaderSpecialisation.VanillaBench = 0;
            VintageVisuals.Common.Rendering.DepthPrepass.BenchPoolMask = -1;
            _hud?.TryClose();
            _hud?.Dispose();
            _profiler.Dispose();
        }
    }
}
