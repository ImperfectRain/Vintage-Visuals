using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VintageVisuals.PseudoPBR;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// The shipped crevice maps (src/PseudoPBR/CreviceMaps.cs) against the
    /// reference they were tuned and approved on (tools/pbrgen/crevice.py), and
    /// the property the whole redesign exists for: no speck becomes a pool.
    ///
    /// v17 to v19 of the crevice water were each reviewed, built and shipped,
    /// and each read the texture's painted speckle as crevices. A parity check
    /// alone would not catch a regression of that kind if both sides moved, so
    /// the speck rule is asserted on its own as well.
    /// </summary>
    public static class CreviceParityChecks
    {
        // Per-texel, absolute. The two sides run the same float64 operations in
        // the same order on a bit-identical input, so anything above rounding
        // is a real divergence; the JSON keeps six decimals.
        private const double Tolerance = 2e-6;

        /// <summary>Mirror of crevice_fixture.py's texel(). Integer-only, so
        /// both languages build the same input bit for bit.</summary>
        public static int Texel(int x, int y)
        {
            int course = y / 11, row = y % 11;
            int offset = (course * 7) % 16;
            int col = (x + offset) % 16;
            if (row < 2 || col < 2) return 40 + ((x * 13 + y * 7) % 5) * 4;
            int v = 150 + ((x * 3 + y * 5) % 9) * 6 - row * 3;
            if (IsSpeck(x, y)) v = 45;
            return v;
        }

        /// <summary>A 2x2 speck as dark as mortar: deep enough to seed a
        /// joint, short enough that only the length filter removes it.</summary>
        private static bool IsSpeck(int x, int y)
        {
            bool S(int a, int b) => (a * 7 + b * 11) % 23 == 0;
            return S(x, y) || S(x - 1, y) || S(x, y - 1) || S(x - 1, y - 1);
        }

        public static double[] FixtureHeight(int size)
        {
            var lum = new double[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    lum[y * size + x] = Texel(x, y) / 255.0;
            return MaterialAtlas2Builder.Normalise(lum);
        }

        public static void Run(string repoRoot, Action<string, bool, string> check)
        {
            string path = Path.Combine(repoRoot, "tools", "pbrgen", "crevice_fixture.json");
            if (!File.Exists(path))
            {
                check("crevice fixture present", false, "missing " + path + " - run tools/pbrgen/crevice_fixture.py");
                return;
            }

            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                JsonElement root = doc.RootElement;
                int size = root.GetProperty("size").GetInt32();
                CreviceMaps.Result maps = CreviceMaps.Build(FixtureHeight(size), size, size);

                var mine = new Dictionary<string, double[]>
                {
                    { "joint", maps.Joint }, { "pool", maps.Pool }, { "shape", maps.Shape },
                    { "flow", maps.Flow }, { "phase", maps.Phase },
                };

                foreach (KeyValuePair<string, double[]> kv in mine)
                {
                    JsonElement expected = root.GetProperty(kv.Key);
                    int bad = 0, first = -1;
                    double worst = 0.0;
                    int i = 0;
                    foreach (JsonElement e in expected.EnumerateArray())
                    {
                        double diff = Math.Abs(e.GetDouble() - kv.Value[i]);
                        // Phase is periodic: 0.999999 and 0.0 are the same drop.
                        if (kv.Key == "phase") diff = Math.Min(diff, 1.0 - diff);
                        if (diff > Tolerance) { bad++; if (first < 0) first = i; }
                        if (diff > worst) worst = diff;
                        i++;
                    }
                    check("crevice map '" + kv.Key + "' matches the approved reference texel for texel",
                        i == kv.Value.Length && bad == 0,
                        bad == 0 ? "" : bad + " texels differ, first at (" + (first % size) + "," + (first / size) +
                                        "), worst " + worst.ToString("R"));
                }

                SpeckRule(maps, FixtureHeight(size), size, check);
                FlowRule(maps, size, check);
            }
        }

        /// <summary>Every kept joint component is long and large, and none of
        /// the fixture's isolated specks pools.</summary>
        private static void SpeckRule(CreviceMaps.Result maps, double[] height, int size, Action<string, bool, string> check)
        {
            int small = 0, speckPools = 0, specks = 0;
            var seen = new bool[size * size];
            for (int s = 0; s < seen.Length; s++)
            {
                if (maps.Joint[s] < 0.5 || seen[s]) continue;
                var stack = new Stack<int>();
                stack.Push(s); seen[s] = true;
                int n = 0;
                while (stack.Count > 0)
                {
                    int c = stack.Pop(); n++;
                    int cy = c / size, cx = c % size;
                    int[] nb = { ((cy + 1) % size) * size + cx, ((cy + size - 1) % size) * size + cx,
                                 cy * size + (cx + 1) % size, cy * size + (cx + size - 1) % size };
                    foreach (int q in nb) if (maps.Joint[q] > 0.5 && !seen[q]) { seen[q] = true; stack.Push(q); }
                }
                if (n < CreviceMaps.JointMinTexels) small++;
            }

            // A speck is a texel the formula darkened that sits inside a stone
            // (two texels clear of any mortar line).
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int course = y / 11, row = y % 11, col = (x + (course * 7) % 16) % 16;
                    // Rows stop at 7: the 32-texel fixture does not tile its
                    // 11-row courses, so row 8 of the last course touches
                    // the first course's mortar across the wrap.
                    bool interior = row >= 4 && row <= 7 && col >= 4 && col <= 13;
                    if (!interior || !IsSpeck(x, y)) continue;
                    specks++;
                    if (maps.Pool[y * size + x] > 0.0) speckPools++;
                }

            check("no kept joint is smaller than a joint can be", small == 0, small + " small components kept");
            check("a dark speck inside a stone never pools", specks > 0 && speckPools == 0,
                speckPools + " of " + specks + " interior specks pool");
        }

        /// <summary>Streams gather: most of the face carries little water and
        /// the strongest paths are narrow, or the flow map is a film again.</summary>
        private static void FlowRule(CreviceMaps.Result maps, int size, Action<string, bool, string> check)
        {
            int strong = 0;
            foreach (double v in maps.Flow) if (v > 0.9) strong++;
            double share = strong / (double)(size * size);
            check("rain gathers into streams rather than coating the whole face",
                share > 0.02 && share < 0.6, "share of texels in a full stream " + share.ToString("F3"));
        }
    }
}
