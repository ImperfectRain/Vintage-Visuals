using System.Globalization;
using System.Text;
using Vintagestory.API.Client;

namespace VintageVisuals.Common.Diagnostics
{
    /// <summary>
    /// The frame-time readout: a small text panel in the top-left corner,
    /// refreshed twice a second from <see cref="FrameProfiler"/>.
    ///
    /// A HUD, not a dialog: it never takes focus or the mouse, so it can stay
    /// up while playing. Toggled with <c>.vvperf</c> or
    /// Performance.ShowFrameTimes.
    /// </summary>
    public sealed class FrameTimeHud : HudElement
    {
        private const string TextKey = "vvperf";

        /// <summary>D99. Show the mod's CPU time per system under the stages (Performance.ProfilerCpuInReadout).</summary>
        public static bool ShowCpuSpans { get; set; } = true;
        private readonly FrameProfiler _profiler;
        private long _lastUpdate;

        public FrameTimeHud(ICoreClientAPI capi, FrameProfiler profiler) : base(capi)
        {
            _profiler = profiler;
            Compose();
        }

        public override string ToggleKeyCombinationCode { get { return null; } }
        public override bool Focusable { get { return false; } }

        private void Compose()
        {
            ElementBounds bounds = ElementBounds.Fixed(EnumDialogArea.LeftTop, 12, 60, 380, 420);
            ElementBounds textBounds = ElementBounds.Fixed(0, 0, 380, 420);
            ElementBounds bg = ElementBounds.Fill.WithFixedPadding(6);
            bg.BothSizing = ElementSizing.FitToChildren;
            bg.WithChildren(textBounds);

            SingleComposer = capi.Gui.CreateCompo("vintagevisuals-frametimes", bounds)
                .AddGameOverlay(bg)
                .AddDynamicText("waiting for frames...", CairoFont.WhiteSmallText().WithFontSize(13),
                                textBounds, TextKey)
                .Compose();
        }

        public override void OnRenderGUI(float deltaTime)
        {
            long now = capi.ElapsedMilliseconds;
            if (now - _lastUpdate > 500)
            {
                _lastUpdate = now;
                SingleComposer.GetDynamicText(TextKey)?.SetNewText(BuildText());
            }
            base.OnRenderGUI(deltaTime);
        }

        private string BuildText()
        {
            var sb = new StringBuilder();
            double avg = _profiler.FrameAverageMs;
            sb.Append("Vintage Visuals frame ").Append(FrameProfiler.Ms(avg)).Append(" ms (")
              .Append(FrameProfiler.Fps(avg)).Append(" fps)  p99 ")
              .Append(FrameProfiler.Ms(_profiler.FramePercentileMs(0.99))).Append('\n');
            sb.Append("Quality: ").Append(QualityTier.Name(QualityTier.Current)).Append('\n');

            if (!_profiler.GpuAvailable)
            {
                sb.Append("GPU timing unavailable; CPU ms shown\n");
            }

            foreach (FrameProfiler.Segment s in _profiler.Segments)
            {
                if (!s.HasSamples) continue;
                double v = _profiler.GpuAvailable ? s.GpuAverage : s.CpuAverage;
                if (s.Key != "frame" && v < 0.005 && !s.ModPass) continue;
                sb.Append(s.ModPass ? "  + " : "  ").Append(s.Label).Append("  ")
                  .Append(v.ToString("0.00", CultureInfo.InvariantCulture)).Append(" ms");
                if (_profiler.GpuAvailable)
                {
                    sb.Append("  (cpu ").Append(s.CpuAverage.ToString("0.00", CultureInfo.InvariantCulture)).Append(')');
                }
                sb.Append('\n');
            }

            if (ShowCpuSpans && Profiling.CpuSpans.All.Count > 0)
            {
                sb.Append("mod CPU ").Append(Profiling.CpuSpans.TotalMsPerFrame.ToString("0.00", CultureInfo.InvariantCulture)).Append(" ms/frame\n");
                int shown = 0;
                foreach (Profiling.CpuSpans.Stat s in System.Linq.Enumerable.OrderByDescending(Profiling.CpuSpans.All, x => x.MsPerFrame))
                {
                    if (shown++ >= 5 || s.MsPerFrame < 0.005) break;
                    sb.Append("  ").Append(s.Name.Replace("vintagevisuals-", "")).Append("  ")
                      .Append(s.MsPerFrame.ToString("0.00", CultureInfo.InvariantCulture)).Append(" ms");
                    if (s.BytesPerFrame >= 512) sb.Append("  ").Append((s.BytesPerFrame / 1024).ToString("0", CultureInfo.InvariantCulture)).Append(" KB");
                    sb.Append('\n');
                }
            }
            string status = PerformanceTools.Current?.ProfilerStatus;
            if (status != null) sb.Append("bench ").Append(status).Append('\n');
            return sb.ToString();
        }
    }
}
