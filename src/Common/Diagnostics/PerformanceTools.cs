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
    ///   .vvbench tiers [secs]   record Low, Medium, High, Ultra in turn, then
    ///                           restore the configured tier (default 8 s each)
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
                    .WithDescription("Vintage Visuals: record frame times. '.vvbench [seconds]' or '.vvbench tiers [seconds]'")
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
            _hud?.TryClose();
            _hud?.Dispose();
            _profiler.Dispose();
        }
    }
}
