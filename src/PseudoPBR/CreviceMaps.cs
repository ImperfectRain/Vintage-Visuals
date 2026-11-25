using System;
using System.Collections.Generic;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// The joint network, stone shape and rain flow paths of one tiling block
    /// texture, computed once at load (D67).
    ///
    /// A PORT of tools/pbrgen/crevice.py, which is where the effect was tuned
    /// and approved on the game's own stone textures. tools/smoketest compares
    /// every texel of this port against crevice_fixture.json, so a retune means
    /// editing both and regenerating the fixture. The operation order below
    /// follows the Python line for line, including tie rules, because a
    /// threshold that flips on one texel can move a whole joint.
    ///
    /// Why it exists: luminance is the mod's only height signal and on Vintage
    /// Story stone it is mostly painted speckle, so every per-texel test in the
    /// shader (v17 to v19) read each speck as a dip. The STRUCTURE - long
    /// connected dark joints around lighter stones - only shows at a scale of
    /// several texels, which a fragment shader cannot afford to look at and a
    /// load-time pass can.
    ///
    /// All arrays are row-major width*height, values 0..1, wrapping at edges.
    /// </summary>
    public static class CreviceMaps
    {
        public const double JointPercentile = 82.0;
        public const double JointMinSeed = 0.05;
        public const double JointGrow = 0.75;
        public const int JointMinTexels = 10;
        public const double JointMinRun = 0.3;
        public const double PoolFloor = 0.6;
        public const double PillowReach = 3.0;
        public const double PillowPaint = 0.15;
        public const double FlowChannelDrop = 0.25;
        public const double FlowSidePreference = 0.015;
        public const double FlowLedge = 0.02;
        public const double FlowLedgeSlack = 0.01;
        public const int FlowSteps = 96;
        public const double FlowNormalise = 0.35;
        public const double FlowStart = 0.15;
        public const double FlowRange = 0.35;
        public const double PhasePeriod = 9.0;

        /// <summary>Above this edge length a texture keeps the plain luminance
        /// maps: the flow trace is O(texels * steps) and no stone texture in
        /// the game is anywhere near it.</summary>
        public const int MaxSize = 128;

        public sealed class Result
        {
            public double[] Joint;
            public double[] Pool;
            public double[] Shape;
            public double[] Flow;
            public double[] Phase;
        }

        public static Result Build(double[] height, int w, int h)
        {
            if (height == null || height.Length != w * h || w <= 0 || h <= 0)
                throw new ArgumentException("height must be width*height");

            double[] keep;
            double[] pool = Joints(height, w, h, out keep);
            double[] shape = Shape(keep, height, w, h);
            double[] amount, phase;
            Flow(shape, keep, w, h, out amount, out phase);
            return new Result { Joint = keep, Pool = pool, Shape = shape, Flow = amount, Phase = phase };
        }

        // result[y, x] = a[y - dy, x - dx], wrapping (numpy.roll).
        private static double At(double[] a, int w, int h, int y, int x)
        {
            y = ((y % h) + h) % h;
            x = ((x % w) + w) % w;
            return a[y * w + x];
        }

        private static readonly int[,] GaussKernel =
        {
            { 0, 0, 4 }, { 0, 1, 2 }, { 0, -1, 2 }, { 1, 0, 2 }, { -1, 0, 2 },
            { 1, 1, 1 }, { 1, -1, 1 }, { -1, 1, 1 }, { -1, -1, 1 },
        };

        public static double[] Gauss(double[] a, int w, int h, int passes)
        {
            for (int p = 0; p < passes; p++)
            {
                var o = new double[a.Length];
                for (int k = 0; k < GaussKernel.GetLength(0); k++)
                {
                    int dy = GaussKernel[k, 0], dx = GaussKernel[k, 1];
                    double wt = GaussKernel[k, 2];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            o[y * w + x] = o[y * w + x] + At(a, w, h, y - dy, x - dx) * wt;
                }
                for (int i = 0; i < o.Length; i++) o[i] = o[i] / 16.0;
                a = o;
            }
            return a;
        }

        private static List<int[]> Disk(int r)
        {
            var list = new List<int[]>();
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dy * dy <= r * r) list.Add(new[] { dy, dx });
            return list;
        }

        private static double[] Morph(double[] a, int w, int h, int r, bool max)
        {
            var o = (double[])a.Clone();
            foreach (int[] d in Disk(r))
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        double v = At(a, w, h, y - d[0], x - d[1]);
                        int i = y * w + x;
                        o[i] = max ? Math.Max(o[i], v) : Math.Min(o[i], v);
                    }
            return o;
        }

        /// <summary>numpy's default (linear) percentile.</summary>
        public static double Percentile(double[] values, double p)
        {
            var s = (double[])values.Clone();
            Array.Sort(s);
            double pos = (s.Length - 1) * p / 100.0;
            int lo = (int)Math.Floor(pos);
            int hi = Math.Min(lo + 1, s.Length - 1);
            return s[lo] + (s[hi] - s[lo]) * (pos - lo);
        }

        private static readonly int[,] Four = { { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } };

        public static double[] Joints(double[] height, int w, int h, out double[] keep)
        {
            int n = w * h;
            double[] hs = Gauss(height, w, h, 1);
            double[] closed = Morph(Morph(hs, w, h, 2, true), w, h, 2, false);
            var depth = new double[n];
            for (int i = 0; i < n; i++) depth[i] = Math.Max(0.0, closed[i] - hs[i]);

            double hi = Math.Max(JointMinSeed, Percentile(depth, JointPercentile));
            double lo = hi * JointGrow;

            var weak = new bool[n];
            var grown = new bool[n];
            for (int i = 0; i < n; i++) { weak[i] = depth[i] > lo; grown[i] = depth[i] > hi; }

            // Hysteresis: grow the strong seeds through weak texels, one ring
            // per pass, as numpy does it (each pass reads the previous pass).
            for (int it = 0; it < Math.Max(w, h); it++)
            {
                var next = (bool[])grown.Clone();
                bool changed = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (next[i] || !weak[i]) continue;
                        for (int k = 0; k < 4; k++)
                        {
                            int yy = ((y - Four[k, 0]) % h + h) % h, xx = ((x - Four[k, 1]) % w + w) % w;
                            if (grown[yy * w + xx]) { next[i] = true; changed = true; break; }
                        }
                    }
                grown = next;
                if (!changed) break;
            }

            // Keep a component only if it is a joint: enough texels, and long
            // in at least one direction. A speck or a chisel mark is neither.
            keep = new double[n];
            var label = new int[n];
            for (int i = 0; i < n; i++) label[i] = -1;
            var stack = new Stack<int>();
            var members = new List<int>();
            int next_label = 0;
            for (int start = 0; start < n; start++)
            {
                if (!grown[start] || label[start] >= 0) continue;
                members.Clear();
                label[start] = next_label;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int c = stack.Pop();
                    members.Add(c);
                    int cy = c / w, cx = c % w;
                    for (int k = 0; k < 4; k++)
                    {
                        int ny = (cy + Four[k, 0] + h) % h, nx = (cx + Four[k, 1] + w) % w;
                        int ni = ny * w + nx;
                        if (grown[ni] && label[ni] < 0) { label[ni] = next_label; stack.Push(ni); }
                    }
                }
                next_label++;

                var rows = new HashSet<int>();
                var cols = new HashSet<int>();
                foreach (int m in members) { rows.Add(m / w); cols.Add(m % w); }
                double run = Math.Max(rows.Count / (double)h, cols.Count / (double)w);
                if (members.Count >= JointMinTexels && run >= JointMinRun)
                    foreach (int m in members) keep[m] = 1.0;
            }

            var pool = new double[n];
            for (int i = 0; i < n; i++)
            {
                double t = depth[i] / hi;
                t = t < PoolFloor ? PoolFloor : (t > 1.0 ? 1.0 : t);
                pool[i] = keep[i] * t;
            }
            return pool;
        }

        public static double[] DistanceTo(double[] mask, int w, int h, int maxd)
        {
            var d = new double[mask.Length];
            for (int i = 0; i < d.Length; i++) d[i] = mask[i] > 0.5 ? 0.0 : maxd;
            for (int p = 0; p < maxd; p++)
                for (int k = 0; k < 4; k++)
                {
                    var o = new double[d.Length];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            o[y * w + x] = Math.Min(d[y * w + x], At(d, w, h, y - Four[k, 0], x - Four[k, 1]) + 1.0);
                    d = o;
                }
            return d;
        }

        public static double[] Shape(double[] keep, double[] height, int w, int h)
        {
            double[] d = DistanceTo(keep, w, h, 6);
            double[] paint = Gauss(height, w, h, 2);
            var mix = new double[d.Length];
            for (int i = 0; i < d.Length; i++)
            {
                double t = d[i] / PillowReach;
                t = t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
                double dome = 1.0 - (1.0 - t) * (1.0 - t);
                mix[i] = dome * (1.0 - PillowPaint) + paint[i] * PillowPaint;
            }
            double[] sh = Gauss(mix, w, h, 1);
            for (int i = 0; i < sh.Length; i++) sh[i] = sh[i] < 0.0 ? 0.0 : (sh[i] > 1.0 ? 1.0 : sh[i]);
            return sh;
        }

        /// <summary>Next texel downhill from each texel of a wall face (rows
        /// run down), as a flat index.</summary>
        public static int[] FlowNext(double[] shape, double[] keep, int w, int h)
        {
            int n = w * h;
            var hh = new double[n];
            for (int i = 0; i < n; i++) hh[i] = shape[i] - keep[i] * FlowChannelDrop;

            var next = new int[n];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double c = hh[y * w + x];
                    double best = 0.0;
                    bool have = false;
                    int by = 0, bx = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        int dx = k == 0 ? 0 : (k == 1 ? -1 : 1);
                        double pref = k == 0 ? 0.0 : FlowSidePreference;
                        int yy = (y + 1) % h, xx = ((x + dx) % w + w) % w;
                        double cost = hh[yy * w + xx] - c + pref;
                        if (!have || cost < best) { have = true; best = cost; by = yy; bx = xx; }
                    }
                    if (best > FlowLedge)
                    {
                        double l = hh[y * w + (x - 1 + w) % w], r = hh[y * w + (x + 1) % w];
                        int side = l < r ? -1 : 1;
                        if (Math.Min(l, r) <= c + FlowLedgeSlack) { by = y; bx = ((x + side) % w + w) % w; }
                    }
                    next[y * w + x] = by * w + bx;
                }
            return next;
        }

        public static double Hash01(long v)
        {
            ulong u = (ulong)v * 2654435761UL % 4294967296UL % 10007UL;
            return u / 10007.0;
        }

        public static void Flow(double[] shape, double[] keep, int w, int h, out double[] amount, out double[] phase)
        {
            int n = w * h;
            int[] next = FlowNext(shape, keep, w, h);
            var a = new double[n];
            var s = new double[n];
            var r = new long[n];
            var seen = new int[n];   // stamp of the path that last visited; replaces Python's per-path set
            for (int i = 0; i < n; i++) seen[i] = -1;

            for (int y0 = 0; y0 < h; y0++)
                for (int x0 = 0; x0 < w; x0++)
                {
                    int path = y0 * w + x0;
                    int c = path;
                    for (int step = 0; step < FlowSteps; step++)
                    {
                        if (seen[c] == path) break;
                        seen[c] = path;
                        a[c] += 1.0;
                        if (step > s[c]) { s[c] = step; r[c] = (long)x0 * 31 + y0; }
                        c = next[c];
                    }
                }

            double maxA = 0.0;
            for (int i = 0; i < n; i++) if (a[i] > maxA) maxA = a[i];
            double norm = Math.Log(1.0 + maxA * FlowNormalise);

            amount = new double[n];
            phase = new double[n];
            for (int i = 0; i < n; i++)
            {
                double an = Math.Log(1.0 + a[i]) / norm;
                an = an < 0.0 ? 0.0 : (an > 1.0 ? 1.0 : an);
                double t = (an - FlowStart) / FlowRange;
                amount[i] = t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
                double ph = (s[i] / PhasePeriod + Hash01(r[i])) % 1.0;
                phase[i] = ph < 0.0 ? ph + 1.0 : ph;
            }
        }
    }
}
