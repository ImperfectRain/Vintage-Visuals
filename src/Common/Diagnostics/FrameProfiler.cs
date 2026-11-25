using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageVisuals.Common.Diagnostics
{
    /// <summary>
    /// Where the frame goes: GPU and CPU milliseconds per render stage, plus
    /// the mod's own extra passes, without stalling the pipeline to find out.
    ///
    /// HOW. A marker renderer is registered at the very start and the very end
    /// of each render stage (render orders -1e6 and +1e6), and either side of
    /// the mod's own passes at their known orders. Each marker lays down a GPU
    /// timestamp query and a CPU stopwatch reading. Results are read three
    /// frames later and only if the GPU says they are ready, so measuring
    /// never makes the CPU wait for the GPU.
    ///
    /// WHAT IT CAN AND CANNOT SAY. A stage figure is everything drawn in that
    /// stage - vanilla's work and this mod's shader patches together. The
    /// patched shaders run INSIDE vanilla's draws, so their cost can only be
    /// isolated by comparison: the same scene with a feature on and off, or
    /// with the quality tier changed. That is what <c>.vvbench tiers</c> is
    /// for. The mod's own passes (the scene capture, the leaf cards) are
    /// bracketed directly.
    ///
    /// COST WHEN OFF: nothing. The markers are only registered while the
    /// readout is visible or a bench is recording.
    /// </summary>
    public sealed class FrameProfiler : IDisposable
    {
        private const double First = -1e6;
        private const double Last = 1e6;
        private const int FramesInFlight = 4;
        private const int Window = 240;

        /// <summary>A named span between two markers.</summary>
        public sealed class Segment
        {
            public readonly string Key;
            public readonly string Label;
            public readonly int From;
            public readonly int To;
            public readonly bool ModPass;

            internal readonly double[] Gpu = new double[Window];
            internal readonly double[] Cpu = new double[Window];
            internal int Count;
            internal int Head;

            public Segment(string key, string label, int from, int to, bool modPass)
            {
                Key = key; Label = label; From = from; To = to; ModPass = modPass;
            }

            internal void Push(double gpuMs, double cpuMs)
            {
                Gpu[Head] = gpuMs;
                Cpu[Head] = cpuMs;
                Head = (Head + 1) % Window;
                if (Count < Window) Count++;
            }

            public double GpuAverage { get { return Average(Gpu, Count); } }
            public double CpuAverage { get { return Average(Cpu, Count); } }
            public bool HasSamples { get { return Count > 0; } }
        }

        private sealed class Marker : IRenderer
        {
            private readonly FrameProfiler _owner;
            public readonly int Index;
            public readonly EnumRenderStage Stage;
            private readonly double _order;

            public Marker(FrameProfiler owner, int index, EnumRenderStage stage, double order)
            {
                _owner = owner; Index = index; Stage = stage; _order = order;
            }

            public double RenderOrder { get { return _order; } }
            public int RenderRange { get { return 0; } }
            public void OnRenderFrame(float deltaTime, EnumRenderStage stage) { _owner.Hit(Index, deltaTime); }
            public void Dispose() { }
        }

        private readonly ICoreClientAPI _capi;
        private readonly ILogger _logger;
        private readonly List<Marker> _markers = new List<Marker>();
        private readonly List<Segment> _segments = new List<Segment>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private GlTimestampApi _gl;
        private bool _glTried;
        private int[,] _queries;
        private bool[,] _issued;
        private long[,] _cpuTicks;
        private int _slot;
        private long _frames;
        private int _dropped;

        private readonly double[] _frameMs = new double[Window];
        private int _frameCount;
        private int _frameHead;

        private bool _registered;
        private int _users;

        private List<double> _benchFrames;
        private Dictionary<string, List<double>> _benchGpu;
        private Dictionary<string, List<double>> _benchCpu;

        public FrameProfiler(ICoreClientAPI capi, ILogger logger)
        {
            _capi = capi;
            _logger = logger;

            int frameStart = Add(EnumRenderStage.Before, First);
            int beforeEnd = Add(EnumRenderStage.Before, Last);
            Span("before", "before (updates, mod ticks)", frameStart, beforeEnd);

            Bracket("shadowfar", "shadows far", EnumRenderStage.ShadowFar);
            Bracket("shadownear", "shadows near", EnumRenderStage.ShadowNear);

            int opaqueStart = Add(EnumRenderStage.Opaque, First);
            int leavesStart = Add(EnumRenderStage.Opaque, 0.499);
            int leavesEnd = Add(EnumRenderStage.Opaque, 0.501);
            int opaqueEnd = Add(EnumRenderStage.Opaque, Last);
            Span("opaque", "opaque (terrain, entities)", opaqueStart, opaqueEnd);
            Span("vv.leaves", "vv falling leaves", leavesStart, leavesEnd, true);

            Bracket("oit", "transparent (water, glass)", EnumRenderStage.OIT);
            Bracket("afteroit", "after transparent", EnumRenderStage.AfterOIT);

            int postStart = Add(EnumRenderStage.AfterPostProcessing, First);
            int captureStart = Add(EnumRenderStage.AfterPostProcessing, 0.999);
            int captureEnd = Add(EnumRenderStage.AfterPostProcessing, 1.001);
            int postEnd = Add(EnumRenderStage.AfterPostProcessing, Last);
            Span("post", "after post-processing", postStart, postEnd);
            Span("vv.capture", "vv scene capture", captureStart, captureEnd, true);

            Bracket("blit", "blit", EnumRenderStage.AfterBlit);
            Bracket("ortho", "2D / HUD", EnumRenderStage.Ortho);
            int finalStart = Add(EnumRenderStage.AfterFinalComposition, First);
            int finalEnd = Add(EnumRenderStage.AfterFinalComposition, Last);
            Span("final", "final composition", finalStart, finalEnd);

            // The whole GPU frame, as far as the markers can see it: from the
            // first marker of the frame to the last. Swap and driver work
            // after the final composition are outside it.
            Span("frame", "GPU frame (markers)", frameStart, finalEnd);
        }

        public IReadOnlyList<Segment> Segments { get { return _segments; } }
        public bool GpuAvailable { get { return _gl != null; } }
        public string GpuUnavailableReason { get { return GlTimestampApi.LastError; } }
        public bool Recording { get { return _benchFrames != null; } }
        public int Dropped { get { return _dropped; } }

        public double FrameAverageMs { get { return Average(_frameMs, _frameCount); } }
        public double FramePercentileMs(double p) { return Percentile(_frameMs.Take(_frameCount), p); }

        private int Add(EnumRenderStage stage, double order)
        {
            var m = new Marker(this, _markers.Count, stage, order);
            _markers.Add(m);
            return m.Index;
        }

        private void Span(string key, string label, int from, int to, bool modPass = false)
        {
            _segments.Add(new Segment(key, label, from, to, modPass));
        }

        private void Bracket(string key, string label, EnumRenderStage stage)
        {
            int a = Add(stage, First);
            int b = Add(stage, Last);
            Span(key, label, a, b);
        }

        /// <summary>One consumer (the readout, a bench) wants measurements.</summary>
        public void Acquire()
        {
            _users++;
            if (_registered) return;
            foreach (Marker m in _markers)
            {
                _capi.Event.RegisterRenderer(m, m.Stage, "vintagevisuals-profiler-" + m.Index);
            }
            _registered = true;
        }

        public void Release()
        {
            if (_users > 0) _users--;
            if (_users > 0 || !_registered) return;
            foreach (Marker m in _markers) _capi.Event.UnregisterRenderer(m, m.Stage);
            _registered = false;
        }

        private void EnsureGl()
        {
            if (_glTried) return;
            _glTried = true;
            _gl = GlTimestampApi.TryCreate();
            _queries = new int[FramesInFlight, _markers.Count];
            _issued = new bool[FramesInFlight, _markers.Count];
            _cpuTicks = new long[FramesInFlight, _markers.Count];

            if (_gl == null)
            {
                _logger.Warning("[VintageVisuals] profiler: GPU timing unavailable (" + GlTimestampApi.LastError +
                                "); the readout shows CPU time only.");
                return;
            }

            try
            {
                for (int f = 0; f < FramesInFlight; f++)
                    for (int i = 0; i < _markers.Count; i++)
                        _queries[f, i] = _gl.Create();
                _logger.Notification("[VintageVisuals] profiler: GPU timestamp queries ready (" +
                                     (FramesInFlight * _markers.Count) + ").");
            }
            catch (Exception ex)
            {
                _logger.Warning("[VintageVisuals] profiler: creating GPU queries failed (" + ex.Message +
                                "); CPU time only.");
                _gl = null;
            }
        }

        private void Hit(int index, float deltaTime)
        {
            EnsureGl();

            if (index == 0)
            {
                // A new frame. Harvest the oldest slot, then reuse it.
                _frames++;
                _slot = (int)(_frames % FramesInFlight);
                Harvest(_slot);
                for (int i = 0; i < _markers.Count; i++) _issued[_slot, i] = false;

                double ms = deltaTime * 1000.0;
                if (ms > 0 && ms < 1000)
                {
                    _frameMs[_frameHead] = ms;
                    _frameHead = (_frameHead + 1) % Window;
                    if (_frameCount < Window) _frameCount++;
                    if (_benchFrames != null) _benchFrames.Add(ms);
                }
            }

            _cpuTicks[_slot, index] = _clock.ElapsedTicks;
            if (_gl != null)
            {
                try { _gl.Stamp(_queries[_slot, index]); }
                catch { _gl = null; }
            }
            _issued[_slot, index] = true;
        }

        private void Harvest(int slot)
        {
            if (_frames <= FramesInFlight) return;

            foreach (Segment s in _segments)
            {
                if (!_issued[slot, s.From] || !_issued[slot, s.To]) continue;

                double cpu = (_cpuTicks[slot, s.To] - _cpuTicks[slot, s.From]) * 1000.0 / Stopwatch.Frequency;
                double gpu = double.NaN;
                if (_gl != null)
                {
                    try
                    {
                        if (_gl.TryRead(_queries[slot, s.From], out long a) &&
                            _gl.TryRead(_queries[slot, s.To], out long b))
                        {
                            gpu = (b - a) / 1e6;
                        }
                        else
                        {
                            _dropped++;
                        }
                    }
                    catch { _gl = null; }
                }

                if (double.IsNaN(gpu) && _gl != null) continue;
                if (gpu < 0 || gpu > 1000) continue;
                s.Push(double.IsNaN(gpu) ? 0 : gpu, cpu);

                if (_benchGpu != null)
                {
                    if (!double.IsNaN(gpu)) Collect(_benchGpu, s.Key, gpu);
                    Collect(_benchCpu, s.Key, cpu);
                }
            }
        }

        private static void Collect(Dictionary<string, List<double>> into, string key, double value)
        {
            if (!into.TryGetValue(key, out List<double> list)) into[key] = list = new List<double>();
            list.Add(value);
        }

        // --- benchmark -------------------------------------------------------

        public void BeginBench()
        {
            Acquire();
            _benchFrames = new List<double>();
            _benchGpu = new Dictionary<string, List<double>>();
            _benchCpu = new Dictionary<string, List<double>>();
        }

        /// <summary>Stops recording and returns the summary, one line per figure.</summary>
        public BenchResult EndBench(string label)
        {
            var r = new BenchResult
            {
                Label = label,
                Frames = _benchFrames != null ? _benchFrames.Count : 0,
                FrameAvg = _benchFrames != null && _benchFrames.Count > 0 ? _benchFrames.Average() : 0,
                FrameP50 = _benchFrames != null ? Percentile(_benchFrames, 0.50) : 0,
                FrameP95 = _benchFrames != null ? Percentile(_benchFrames, 0.95) : 0,
                FrameP99 = _benchFrames != null ? Percentile(_benchFrames, 0.99) : 0,
            };

            foreach (Segment s in _segments)
            {
                double gpu = _benchGpu != null && _benchGpu.TryGetValue(s.Key, out List<double> g) ? g.Average() : double.NaN;
                double cpu = _benchCpu != null && _benchCpu.TryGetValue(s.Key, out List<double> c) ? c.Average() : double.NaN;
                if (double.IsNaN(gpu) && double.IsNaN(cpu)) continue;
                r.Segments.Add((s.Key, s.Label, gpu, cpu));
            }

            _benchFrames = null;
            _benchGpu = null;
            _benchCpu = null;
            Release();
            return r;
        }

        public sealed class BenchResult
        {
            public string Label;
            public int Frames;
            public double FrameAvg, FrameP50, FrameP95, FrameP99;
            public readonly List<(string Key, string Label, double Gpu, double Cpu)> Segments =
                new List<(string, string, double, double)>();

            public string Format()
            {
                var sb = new StringBuilder();
                sb.Append(Label).Append(": ").Append(Frames).Append(" frames, avg ")
                  .Append(Ms(FrameAvg)).Append(" ms (").Append(Fps(FrameAvg)).Append(" fps), p50 ")
                  .Append(Ms(FrameP50)).Append(", p95 ").Append(Ms(FrameP95)).Append(", p99 ")
                  .Append(Ms(FrameP99)).Append('\n');
                foreach (var s in Segments)
                {
                    sb.Append("  ").Append(s.Label.PadRight(30)).Append(" gpu ")
                      .Append(double.IsNaN(s.Gpu) ? "  n/a" : Ms(s.Gpu).PadLeft(6)).Append("  cpu ")
                      .Append(double.IsNaN(s.Cpu) ? "  n/a" : Ms(s.Cpu).PadLeft(6)).Append('\n');
                }
                return sb.ToString();
            }
        }

        // --- helpers ---------------------------------------------------------

        internal static string Ms(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

        internal static string Fps(double ms)
        {
            return ms > 0 ? (1000.0 / ms).ToString("0", CultureInfo.InvariantCulture) : "-";
        }

        private static double Average(double[] values, int count)
        {
            if (count == 0) return 0;
            double sum = 0;
            for (int i = 0; i < count; i++) sum += values[i];
            return sum / count;
        }

        private static double Percentile(IEnumerable<double> values, double p)
        {
            double[] sorted = values.OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return 0;
            int i = (int)Math.Min(sorted.Length - 1, Math.Max(0, Math.Ceiling(p * sorted.Length) - 1));
            return sorted[i];
        }

        public void Dispose()
        {
            if (_registered)
            {
                foreach (Marker m in _markers) _capi.Event.UnregisterRenderer(m, m.Stage);
                _registered = false;
            }

            if (_gl != null && _queries != null)
            {
                try
                {
                    foreach (int q in _queries) _gl.Delete(q);
                }
                catch { }
            }
            _gl = null;
        }
    }
}
