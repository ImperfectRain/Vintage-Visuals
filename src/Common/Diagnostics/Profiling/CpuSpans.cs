using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Vintagestory.API.Client;

namespace VintageVisuals.Common.Diagnostics.Profiling
{
    /// <summary>
    /// D99. CPU time and managed allocations per mod system, per frame.
    ///
    /// Always on: a span is two Stopwatch reads and two allocation-counter reads
    /// (tens of nanoseconds), and there are about fifteen per frame, so the
    /// numbers exist the moment someone asks rather than only after a toggle
    /// they did not know about. Main thread only, like every renderer.
    ///
    /// Two feeds:
    ///   - <see cref="ModRenderers.Register"/> wraps every per-frame renderer
    ///     the mod registers, so each is a span under its registration name
    ///     without its code knowing.
    ///   - <see cref="Measure"/> for work that runs inside a Harmony hook or a
    ///     game event rather than a renderer ("mirrors.cull").
    /// </summary>
    public static class CpuSpans
    {
        public sealed class Stat
        {
            public readonly string Name;
            internal long Ticks, Bytes, Calls;
            internal long WinTicks, WinBytes, WinCalls;
            internal long BenchTicks, BenchBytes, BenchCalls;
            internal double MaxMs;
            internal double BenchMaxMs;

            /// <summary>Smoothed ms per frame over the last half-second window (for the readout).</summary>
            public double MsPerFrame;
            public double BytesPerFrame;
            public double CallsPerFrame;

            internal Stat(string name) { Name = name; }
        }

        private static readonly Dictionary<string, Stat> _stats = new Dictionary<string, Stat>(StringComparer.Ordinal);
        private static readonly List<Stat> _ordered = new List<Stat>();
        private static long _windowStartTicks = Stopwatch.GetTimestamp();
        private static long _windowFrames;
        private static long _benchFrames;
        private static bool _benchOn;
        private static int _benchGc0, _benchGc1, _benchGc2;

        public static IReadOnlyList<Stat> All { get { return _ordered; } }

        internal static Stat Get(string name)
        {
            if (!_stats.TryGetValue(name, out Stat s))
            {
                s = new Stat(name);
                _stats[name] = s;
                _ordered.Add(s);
            }
            return s;
        }

        /// <summary>Adds one timed call. Ticks are Stopwatch ticks.</summary>
        internal static void Add(Stat s, long ticks, long bytes)
        {
            if (bytes < 0) bytes = 0;
            s.Ticks += ticks; s.Bytes += bytes; s.Calls++;
            s.WinTicks += ticks; s.WinBytes += bytes; s.WinCalls++;
            double ms = ticks * 1000.0 / Stopwatch.Frequency;
            if (ms > s.MaxMs) s.MaxMs = ms;
            if (_benchOn)
            {
                s.BenchTicks += ticks; s.BenchBytes += bytes; s.BenchCalls++;
                if (ms > s.BenchMaxMs) s.BenchMaxMs = ms;
            }
        }

        /// <summary>
        /// Times a block: <c>using (CpuSpans.Measure("mirrors.cull")) { ... }</c>.
        /// A struct, so it allocates nothing.
        /// </summary>
        public static Scope Measure(string name)
        {
            return new Scope(Get(name));
        }

        public struct Scope : IDisposable
        {
            private readonly Stat _stat;
            private readonly long _t0;
            private readonly long _b0;

            internal Scope(Stat stat)
            {
                _stat = stat;
                _b0 = GC.GetAllocatedBytesForCurrentThread();
                _t0 = Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                if (_stat == null) return;
                long t = Stopwatch.GetTimestamp() - _t0;
                Add(_stat, t, GC.GetAllocatedBytesForCurrentThread() - _b0);
            }
        }

        /// <summary>Called once per frame (by the first wrapped renderer at Before) to roll the readout window.</summary>
        internal static void OnFrame()
        {
            _windowFrames++;
            if (_benchOn) _benchFrames++;
            long now = Stopwatch.GetTimestamp();
            if ((now - _windowStartTicks) * 1000 / Stopwatch.Frequency < 500 || _windowFrames == 0) return;
            foreach (Stat s in _ordered)
            {
                s.MsPerFrame = s.WinTicks * 1000.0 / Stopwatch.Frequency / _windowFrames;
                s.BytesPerFrame = (double)s.WinBytes / _windowFrames;
                s.CallsPerFrame = (double)s.WinCalls / _windowFrames;
                s.WinTicks = s.WinBytes = s.WinCalls = 0;
            }
            _windowFrames = 0;
            _windowStartTicks = now;
        }

        /// <summary>Sum of every span's ms per frame in the readout window.</summary>
        public static double TotalMsPerFrame { get { return _ordered.Sum(s => s.MsPerFrame); } }

        public static void BeginBench()
        {
            foreach (Stat s in _ordered) { s.BenchTicks = s.BenchBytes = s.BenchCalls = 0; s.BenchMaxMs = 0; }
            _benchFrames = 0;
            _benchGc0 = GC.CollectionCount(0);
            _benchGc1 = GC.CollectionCount(1);
            _benchGc2 = GC.CollectionCount(2);
            _benchOn = true;
        }

        /// <summary>One span's figures over a bench run.</summary>
        public struct BenchSpan
        {
            public string Name;
            public double MsPerFrame;
            public double MaxMs;
            public double KbPerFrame;
            public double CallsPerFrame;
        }

        public sealed class BenchSnapshot
        {
            public long Frames;
            public int Gc0, Gc1, Gc2;
            public readonly List<BenchSpan> Spans = new List<BenchSpan>();
            public double TotalMsPerFrame { get { return Spans.Sum(s => s.MsPerFrame); } }
            public double TotalKbPerFrame { get { return Spans.Sum(s => s.KbPerFrame); } }
        }

        public static BenchSnapshot EndBench()
        {
            _benchOn = false;
            var snap = new BenchSnapshot
            {
                Frames = _benchFrames,
                Gc0 = GC.CollectionCount(0) - _benchGc0,
                Gc1 = GC.CollectionCount(1) - _benchGc1,
                Gc2 = GC.CollectionCount(2) - _benchGc2,
            };
            long frames = Math.Max(1, _benchFrames);
            foreach (Stat s in _ordered)
            {
                if (s.BenchCalls == 0) continue;
                snap.Spans.Add(new BenchSpan
                {
                    Name = s.Name,
                    MsPerFrame = s.BenchTicks * 1000.0 / Stopwatch.Frequency / frames,
                    MaxMs = s.BenchMaxMs,
                    KbPerFrame = s.BenchBytes / 1024.0 / frames,
                    CallsPerFrame = (double)s.BenchCalls / frames,
                });
            }
            return snap;
        }

        /// <summary>Test seam: forget everything (smoketest only).</summary>
        internal static void ResetForTests()
        {
            _stats.Clear();
            _ordered.Clear();
            _windowFrames = 0;
            _benchOn = false;
        }
    }

    /// <summary>
    /// D99. The one way the mod registers a per-frame renderer. Wraps it in a
    /// timing shim under its registration name, so every renderer the mod has,
    /// and every one it will have, shows in <see cref="CpuSpans"/> without its
    /// author doing anything else. <c>tools/smoketest</c> fails on a direct
    /// <c>Event.RegisterRenderer</c> call outside this file and the profiler's
    /// own GPU markers.
    /// </summary>
    public static class ModRenderers
    {
        private sealed class Timed : IRenderer
        {
            public readonly IRenderer Inner;
            public readonly CpuSpans.Stat Stat;
            public readonly bool FrameTick;

            public Timed(IRenderer inner, string name, bool frameTick)
            {
                Inner = inner;
                Stat = CpuSpans.Get(name);
                FrameTick = frameTick;
            }

            public double RenderOrder { get { return Inner.RenderOrder; } }
            public int RenderRange { get { return Inner.RenderRange; } }

            public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
            {
                if (FrameTick) CpuSpans.OnFrame();
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    Inner.OnRenderFrame(deltaTime, stage);
                }
                finally
                {
                    long t = Stopwatch.GetTimestamp() - t0;
                    CpuSpans.Add(Stat, t, GC.GetAllocatedBytesForCurrentThread() - b0);
                }
            }

            public void Dispose() { Inner.Dispose(); }
        }

        private static readonly Dictionary<(IRenderer, EnumRenderStage), Timed> _wrapped =
            new Dictionary<(IRenderer, EnumRenderStage), Timed>();
        private static bool _frameTickTaken;

        /// <summary>Every name registered so far, for the coverage report.</summary>
        public static readonly List<string> Names = new List<string>();

        public static void Register(ICoreClientAPI capi, IRenderer renderer, EnumRenderStage stage, string name)
        {
            if (renderer == null) return;
            var key = (renderer, stage);
            if (_wrapped.ContainsKey(key)) return;
            // The first renderer registered at Before rolls the per-frame window.
            // The environment tracker is registered first and runs every frame.
            bool tick = !_frameTickTaken && stage == EnumRenderStage.Before;
            if (tick) _frameTickTaken = true;
            var timed = new Timed(renderer, name, tick);
            _wrapped[key] = timed;
            if (!Names.Contains(name)) Names.Add(name);
            capi.Event.RegisterRenderer(timed, stage, name);
        }

        public static void Unregister(ICoreClientAPI capi, IRenderer renderer, EnumRenderStage stage)
        {
            if (renderer == null) return;
            var key = (renderer, stage);
            if (!_wrapped.TryGetValue(key, out Timed timed))
            {
                capi.Event.UnregisterRenderer(renderer, stage);
                return;
            }
            _wrapped.Remove(key);
            if (timed.FrameTick) _frameTickTaken = false;
            capi.Event.UnregisterRenderer(timed, stage);
        }
    }
}
