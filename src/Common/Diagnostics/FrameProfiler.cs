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

            /// <summary>Stamped from code (a Harmony hook), never registered as a renderer.</summary>
            public readonly bool CodeOnly;

            public Marker(FrameProfiler owner, int index, EnumRenderStage stage, double order, bool codeOnly = false)
            {
                _owner = owner; Index = index; Stage = stage; _order = order; CodeOnly = codeOnly;
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
        private long[] _slotFrame;
        private long _benchStartFrame = long.MaxValue;
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
            int beamsStart = Add(EnumRenderStage.Opaque, 0.979);   // BeamPassRenderer runs at 0.98 (D87/D92)
            int beamsEnd = Add(EnumRenderStage.Opaque, 0.981);
            int opaqueEnd = Add(EnumRenderStage.Opaque, Last);
            Span("opaque", "opaque (terrain, entities)", opaqueStart, opaqueEnd);
            AddChunkPassSpans();
            Span("vv.leaves", "vv falling leaves", leavesStart, leavesEnd, true);
            Span("vv.beams", "vv light shafts pass", beamsStart, beamsEnd, true);

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

        // --- inside the opaque stage ---------------------------------------
        //
        // A stage marker cannot see inside the game's own ChunkRenderer, and
        // that is where the terrain cost is. ChunkPassTimers hooks it and
        // stamps these code markers, so a bench says which chunk pool the
        // milliseconds went to and what the depth prepass itself costs.

        /// <summary>Highest EnumChunkRenderPass value timed (Decor).</summary>
        internal const int ChunkPassCount = 9;

        internal int ChunksStart { get; private set; }
        internal int ChunksEnd { get; private set; }
        internal int PrepassStart { get; private set; }
        internal int PrepassEnd { get; private set; }
        internal int PrepassPoolStart(int pass) { return pass == 0 ? _prepass0Start : _prepass1Start; }
        internal int PrepassPoolEnd(int pass) { return pass == 0 ? _prepass0End : _prepass1End; }
        private int _prepass0Start, _prepass0End, _prepass1Start, _prepass1End;
        private readonly int[] _passStart = new int[ChunkPassCount];
        private readonly int[] _passEnd = new int[ChunkPassCount];
        internal int PassStart(int pass) { return _passStart[pass]; }
        internal int PassEnd(int pass) { return _passEnd[pass]; }

        private ChunkPassTimers _chunkTimers;

        private void AddChunkPassSpans()
        {
            ChunksStart = AddCode();
            ChunksEnd = AddCode();
            PrepassStart = AddCode();
            PrepassEnd = AddCode();
            Span("chunks", "  chunk pools, all", ChunksStart, ChunksEnd);
            Span("chunks.prepass", "  vv depth prepass", PrepassStart, PrepassEnd, true);
            _prepass0Start = AddCode(); _prepass0End = AddCode();
            _prepass1Start = AddCode(); _prepass1End = AddCode();
            Span("chunks.prepass.0", "    prepass Opaque", _prepass0Start, _prepass0End, true);
            Span("chunks.prepass.1", "    prepass leaves", _prepass1Start, _prepass1End, true);

            // In the order ChunkRenderer.RenderOpaque draws them (1.22), so the
            // report reads top to bottom like the frame.
            foreach (int pass in new[] { 0, 5, 2, 1, 8, 3, 4, 6, 7 })
            {
                _passStart[pass] = AddCode();
                _passEnd[pass] = AddCode();
                string name = Enum.GetName(typeof(EnumChunkRenderPass), pass) ?? ("pass " + pass);
                if (pass == 1) name += " (leaves)";
                Span("chunks." + pass, "    " + name, _passStart[pass], _passEnd[pass]);
            }
        }

        private int AddCode()
        {
            var m = new Marker(this, _markers.Count, EnumRenderStage.Opaque, 0, codeOnly: true);
            _markers.Add(m);
            return m.Index;
        }

        /// <summary>The profiler a hook should stamp into, or null when nobody is measuring.</summary>
        internal static FrameProfiler Active { get; private set; }

        /// <summary>
        /// Stamp a code marker. <paramref name="once"/> keeps the first stamp of
        /// the frame (a span's start); otherwise the latest wins (its end), which
        /// is how a pass drawn as several per-atlas calls becomes one span.
        /// </summary>
        internal void StampCode(int index, bool once)
        {
            if (!_registered || _queries == null) return;
            if (once && _issued[_slot, index]) return;
            _cpuTicks[_slot, index] = _clock.ElapsedTicks;
            if (_gl != null)
            {
                try { _gl.Stamp(_queries[_slot, index]); }
                catch { _gl = null; }
            }
            _issued[_slot, index] = true;
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
                if (m.CodeOnly) continue;
                _capi.Event.RegisterRenderer(m, m.Stage, "vintagevisuals-profiler-" + m.Index);
            }
            _registered = true;
            Active = this;

            // Hooked on first use, so a player who never measures never has the
            // game's chunk renderer patched by the profiler at all.
            if (_chunkTimers == null)
            {
                _chunkTimers = new ChunkPassTimers(_logger);
                _chunkTimers.Install();
            }
        }

        public void Release()
        {
            if (_users > 0) _users--;
            if (_users > 0 || !_registered) return;
            foreach (Marker m in _markers)
            {
                if (!m.CodeOnly) _capi.Event.UnregisterRenderer(m, m.Stage);
            }
            _registered = false;
            if (Active == this) Active = null;
        }

        private void EnsureGl()
        {
            if (_glTried) return;
            _glTried = true;
            _gl = GlTimestampApi.TryCreate();
            _queries = new int[FramesInFlight, _markers.Count];
            _issued = new bool[FramesInFlight, _markers.Count];
            _cpuTicks = new long[FramesInFlight, _markers.Count];
            _slotFrame = new long[FramesInFlight];

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
                if (HitchTrace != null && _frames > 0) TraceHitch(_slot, deltaTime * 1000.0);

                // A new frame. Harvest the oldest slot, then reuse it.
                _frames++;
                _slot = (int)(_frames % FramesInFlight);
                Harvest(_slot);
                _slotFrame[_slot] = _frames;
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
            if (_hitchLine != null && _slotFrame[slot] == _hitchFrame) FlushHitch(slot);

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

                // Results arrive FramesInFlight frames late, so the first harvests
                // of a bench belong to the frames before it - the previous run's
                // configuration. Only frames drawn after BeginBench count (a span
                // the new run never draws would otherwise report the old run's).
                if (_benchGpu != null && _slotFrame[slot] >= _benchStartFrame)
                {
                    if (!double.IsNaN(gpu)) Collect(_benchGpu, s.Key, gpu);
                    Collect(_benchCpu, s.Key, cpu);
                }
            }
        }

        // --- hitch trace ------------------------------------------------------

        /// <summary>Receives one line per hitch while '.vvhitch' is on; null otherwise.</summary>
        public Action<string> HitchTrace { get; set; }

        private readonly double[] _recent = new double[120];
        private int _recentCount, _recentHead;
        private int _gc0, _gc1, _gc2;
        private long _lastHitchFrame;

        /// <summary>
        /// Called at the start of a frame for the frame that just ended (its
        /// markers are all in <paramref name="slot"/>). A frame over twice the
        /// recent median and at least 12 ms over it is a hitch: log its length,
        /// the CPU time of each stage in it, and any garbage collections since
        /// the last frame - a gen-2 collection is the usual managed-code stutter.
        /// </summary>
        private void TraceHitch(int slot, double ms)
        {
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            int d0 = gc0 - _gc0, d1 = gc1 - _gc1, d2 = gc2 - _gc2;
            _gc0 = gc0; _gc1 = gc1; _gc2 = gc2;
            if (ms <= 0 || ms > 5000) return;

            double median = 0;
            if (_recentCount >= 30)
            {
                double[] sorted = _recent.Take(_recentCount).OrderBy(v => v).ToArray();
                median = sorted[sorted.Length / 2];
            }
            _recent[_recentHead] = ms;
            _recentHead = (_recentHead + 1) % _recent.Length;
            if (_recentCount < _recent.Length) _recentCount++;

            if (median <= 0 || ms < median * 2 || ms < median + 12) return;
            if (_frames - _lastHitchFrame < 10) return; // one line per burst
            _lastHitchFrame = _frames;

            if (_hitchLine != null) FlushHitch(-1);

            var sb = new StringBuilder();
            sb.Append("hitch ").Append(Ms(ms)).Append(" ms (median ").Append(Ms(median)).Append(")");
            // Where the time went: between the frame's first and last render
            // stage (CPU), or after its last stage until this frame began - swap,
            // present, the driver, or the game's main loop outside rendering.
            Segment frame = _segments.FirstOrDefault(x => x.Key == "frame");
            if (frame != null && _issued[slot, frame.From] && _issued[slot, frame.To])
            {
                double inside = (_cpuTicks[slot, frame.To] - _cpuTicks[slot, frame.From]) * 1000.0 / Stopwatch.Frequency;
                double after = (_clock.ElapsedTicks - _cpuTicks[slot, frame.To]) * 1000.0 / Stopwatch.Frequency;
                sb.Append(" stages ").Append(Ms(inside)).Append(" after-final ").Append(Ms(after));
            }
            if (d0 + d1 + d2 > 0) sb.Append(" GC gen0+").Append(d0).Append(" gen1+").Append(d1).Append(" gen2+").Append(d2);
            sb.Append(" | cpu:");
            foreach (Segment s in _segments)
            {
                if (s.Key == "frame" || !_issued[slot, s.From] || !_issued[slot, s.To]) continue;
                double cpu = (_cpuTicks[slot, s.To] - _cpuTicks[slot, s.From]) * 1000.0 / Stopwatch.Frequency;
                if (cpu >= 1.0) sb.Append(' ').Append(s.Key).Append('=').Append(Ms(cpu));
            }
            // The GPU time of this frame is only readable FramesInFlight frames
            // later, so the line waits for that harvest.
            _hitchLine = sb.ToString();
            _hitchFrame = _slotFrame[slot];
        }

        private string _hitchLine;
        private long _hitchFrame;

        /// <summary>Emit the pending hitch line, with its GPU frame time when <paramref name="slot"/> holds it.</summary>
        private void FlushHitch(int slot)
        {
            string line = _hitchLine;
            _hitchLine = null;
            if (line == null) return;
            Segment frame = _segments.FirstOrDefault(x => x.Key == "frame");
            if (slot >= 0 && frame != null && _gl != null && _issued[slot, frame.From] && _issued[slot, frame.To])
            {
                try
                {
                    if (_gl.TryRead(_queries[slot, frame.From], out long a) &&
                        _gl.TryRead(_queries[slot, frame.To], out long b))
                        line += " | gpu frame=" + Ms((b - a) / 1e6);
                }
                catch { }
            }
            try { HitchTrace?.Invoke(line); } catch { }
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
            _benchStartFrame = _frames + 1;
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
                foreach (Marker m in _markers)
                {
                    if (!m.CodeOnly) _capi.Event.UnregisterRenderer(m, m.Stage);
                }
                _registered = false;
            }
            if (Active == this) Active = null;
            _chunkTimers?.Uninstall();
            _chunkTimers = null;

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
