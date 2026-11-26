using System;
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
    public sealed class PerformanceTools : IDisposable
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

        public PerformanceTools(ICoreClientAPI capi, ILogger logger)
        {
            _capi = capi;
            _logger = logger;
            _profiler = new FrameProfiler(capi, logger);
            RegisterCommands();
        }

        public FrameProfiler Profiler { get { return _profiler; } }

        /// <summary>Called whenever the config changes.</summary>
        public void Apply(PerformanceConfig config)
        {
            QualityTier.SetConfigured(config.Quality);
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

                _capi.ChatCommands.Create("vvbench")
                    .WithDescription("Vintage Visuals: record frame times. '.vvbench [seconds]', '.vvbench tiers [seconds]', '.vvbench leaves [seconds]' or '.vvbench features [seconds]'")
                    .WithArgs(parsers.OptionalWord("mode"), parsers.OptionalInt("seconds", 0))
                    .HandleWith(args =>
                    {
                        string mode = args[0] as string;
                        int seconds = args[1] is int s ? s : 0;
                        if (int.TryParse(mode, out int asNumber)) { seconds = asNumber; mode = null; }
                        return StartBench(mode, seconds);
                    });
            }
            catch (Exception ex)
            {
                _logger.Warning("[VintageVisuals] could not register .vvperf/.vvbench: " + ex.Message);
            }
        }

        private TextCommandResult StartBench(string mode, int seconds)
        {
            if (_profiler.Recording || _benchCallback != 0)
            {
                return TextCommandResult.Error("A bench is already running.");
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

            if (string.Equals(mode, "features", StringComparison.OrdinalIgnoreCase))
            {
                int each = seconds > 0 ? seconds : 6;
                _tierResults.Clear();
                RunFeature(0, each);
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
        private void RunFeature(int index, int seconds)
        {
            int mode = FeatureBench.Order[index];
            FeatureBench.Mode = mode;
            _benchCallback = _capi.Event.RegisterCallback(_ =>
            {
                _profiler.BeginBench();
                _benchCallback = _capi.Event.RegisterCallback(__ =>
                {
                    _tierResults.Add(_profiler.EndBench(FeatureBench.Name(mode)));
                    if (index + 1 < FeatureBench.Order.Length) { RunFeature(index + 1, seconds); return; }

                    _benchCallback = 0;
                    FeatureBench.Mode = FeatureBench.Normal;
                    Report(FormatFeatures(_tierResults));
                }, seconds * 1000);
            }, (int)(SettleSeconds * 1000));
        }

        internal static string FormatFeatures(List<FrameProfiler.BenchResult> results)
        {
            var sb = new StringBuilder();
            if (results.Count == 0) return "no runs";
            FrameProfiler.BenchResult first = results[0];
            double baseOpaque = OpaqueGpu(first);
            sb.Append("features (quality ").Append(QualityTier.Name(QualityTier.Current)).Append("), saving vs everything on\n");
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

        private void Report(string text)
        {
            foreach (string line in text.TrimEnd().Split('\n'))
            {
                _logger.Notification("[VintageVisuals] bench " + line);
            }
            _capi.ShowChatMessage(text.TrimEnd());
        }

        public void Dispose()
        {
            if (_benchCallback != 0) _capi.Event.UnregisterCallback(_benchCallback);
            QualityTier.SetOverride(-1);
            FeatureBench.Mode = FeatureBench.Normal;
            _hud?.TryClose();
            _hud?.Dispose();
            _profiler.Dispose();
        }
    }
}
