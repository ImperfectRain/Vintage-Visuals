using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using VintageVisuals.PseudoPBR;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Asserts the C# PBR port agrees with the validated Python prototype.
    ///
    /// src/PseudoPBR/PbrMapGenerator.cs is a port of tools/pbrgen/pbrgen.py,
    /// whose constants were tuned against measured output and whose behaviour
    /// is pinned by 31 tests. Nothing stops the two drifting apart except a
    /// check like this, and a drift would mean the tool people tune with no
    /// longer predicts what the mod actually does.
    ///
    /// The fixture input is built from an arithmetic formula rather than a
    /// seeded RNG, because RNG streams differ between languages and both sides
    /// must be able to construct a bit-identical input independently.
    /// </summary>
    public static class PbrParityChecks
    {
        /// <summary>
        /// Tolerance per statistic.
        ///
        /// 1e-6, not something tighter, for a measured reason. The Python side
        /// computes its box mean from a summed-area table; this port sums each
        /// window directly. On a near-uniform window the SAT subtracts two
        /// large accumulated sums, and the residue leaves the variance a hair
        /// above zero where direct summation gets exactly zero. At 1e-9 that
        /// showed up as roughness min python=0.2500000112 vs csharp=0.25 - the
        /// floor, reached from opposite sides of the same arithmetic.
        ///
        /// Note which one is right: the direct sum is the *more* accurate of
        /// the two, so this is not the port bending to match a reference. It is
        /// two correct implementations disagreeing in the last few bits.
        ///
        /// 1e-6 is still three orders of magnitude tighter than any behavioural
        /// change could be - roughness spans 0.25 to 1.0, and a retuned
        /// constant moves it by 1e-2 or more.
        /// </summary>
        private const double Tolerance = 1e-6;

        public static void Run(string repoRoot, Action<string, bool, string> check)
        {
            string fixturePath = Path.Combine(repoRoot, "tools", "pbrgen", "parity_fixture.json");

            if (!File.Exists(fixturePath))
            {
                check("parity fixture present", false,
                    "missing " + fixturePath + " - regenerate with tools/pbrgen/parity_fixture.py");
                return;
            }

            using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixturePath)))
            {
                JsonElement root = document.RootElement;

                int width = root.GetProperty("width").GetInt32();
                int height = root.GetProperty("height").GetInt32();

                CheckConstants(root.GetProperty("constants"), check);

                LinearTexture texture = BuildFixtureTexture(width, height);
                PbrMaps maps = PbrMapGenerator.Generate(texture);

                var actual = new Dictionary<string, double[]>
                {
                    { "normal", maps.Normal },
                    { "roughness", maps.Roughness },
                    { "spec", maps.Spec },
                };

                foreach (JsonElement expected in root.GetProperty("maps").EnumerateArray())
                {
                    string name = expected.GetProperty("name").GetString();
                    double[] values;
                    if (!actual.TryGetValue(name, out values))
                    {
                        check("parity: " + name + " produced", false, "no such map in the C# port");
                        continue;
                    }

                    CompareMap(name, expected, values, check);
                }
            }
        }

        /// <summary>
        /// Constants must be identical, not merely close: they are the thing
        /// most likely to be edited on one side only.
        /// </summary>
        private static void CheckConstants(JsonElement constants, Action<string, bool, string> check)
        {
            var expected = new[]
            {
                new KeyValuePair<string, double>("roughness_std_reference", PbrMapGenerator.RoughnessStdReference),
                new KeyValuePair<string, double>("roughness_floor", PbrMapGenerator.RoughnessFloor),
                new KeyValuePair<string, double>("spec_region_threshold", PbrMapGenerator.SpecRegionThreshold),
                new KeyValuePair<string, double>("spec_inclusion_max_area", PbrMapGenerator.SpecInclusionMaxArea),
                new KeyValuePair<string, double>("spec_inclusion_boost", PbrMapGenerator.SpecInclusionBoost),
                new KeyValuePair<string, double>("alpha_cutoff", PbrMapGenerator.AlphaCutoff),
                new KeyValuePair<string, double>("normal_strength", PbrMapGenerator.DefaultNormalStrength),
            };

            foreach (KeyValuePair<string, double> pair in expected)
            {
                double theirs = constants.GetProperty(pair.Key).GetDouble();
                check("constant " + pair.Key + " matches pbrgen.py", Math.Abs(theirs - pair.Value) < 1e-12,
                    "python=" + theirs.ToString(CultureInfo.InvariantCulture) +
                    " csharp=" + pair.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void CompareMap(string name, JsonElement expected, double[] values,
                                       Action<string, bool, string> check)
        {
            int count = expected.GetProperty("count").GetInt32();
            check("parity: " + name + " length", values.Length == count,
                "python=" + count + " csharp=" + values.Length);
            if (values.Length != count) return;

            double min = double.MaxValue, max = double.MinValue, sum = 0.0;
            foreach (double v in values)
            {
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }

            Compare(name, "mean", expected, sum / values.Length, check);
            Compare(name, "min", expected, min, check);
            Compare(name, "max", expected, max, check);

            // Statistics alone would miss a transposed or mirrored map, so pin
            // specific texels as well.
            bool probesMatch = true;
            string firstFailure = "";

            foreach (JsonProperty probe in expected.GetProperty("probes").EnumerateObject())
            {
                int index = int.Parse(probe.Name, CultureInfo.InvariantCulture);
                double theirs = probe.Value.GetDouble();

                if (Math.Abs(values[index] - theirs) > Tolerance)
                {
                    probesMatch = false;
                    if (firstFailure.Length == 0)
                    {
                        firstFailure = "index " + index + ": python=" +
                            theirs.ToString("R", CultureInfo.InvariantCulture) + " csharp=" +
                            values[index].ToString("R", CultureInfo.InvariantCulture);
                    }
                }
            }

            check("parity: " + name + " probes", probesMatch, firstFailure);
        }

        private static void Compare(string map, string stat, JsonElement expected, double ours,
                                    Action<string, bool, string> check)
        {
            double theirs = expected.GetProperty(stat).GetDouble();
            check("parity: " + map + " " + stat, Math.Abs(theirs - ours) <= Tolerance,
                "python=" + theirs.ToString("R", CultureInfo.InvariantCulture) +
                " csharp=" + ours.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Rebuilds the fixture input. MUST stay identical to build_input() in
        /// tools/pbrgen/parity_fixture.py - that identity is the entire basis
        /// of the comparison.
        /// </summary>
        private static LinearTexture BuildFixtureTexture(int width, int height)
        {
            var texture = new LinearTexture
            {
                Width = width,
                Height = height,
                R = new double[width * height],
                G = new double[width * height],
                B = new double[width * height],
                Alpha = new double[width * height],
            };

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;

                    double r = 0.5 + 0.4 * Math.Sin(x * 0.7 + y * 0.3);
                    double g = 0.5 + 0.4 * Math.Sin(x * 0.31 + y * 0.91 + 1.3);
                    double b = 0.5 + 0.4 * Math.Sin(x * 1.13 - y * 0.17 + 2.7);

                    texture.R[i] = PbrMapGenerator.SrgbToLinear(Clamp01(r));
                    texture.G[i] = PbrMapGenerator.SrgbToLinear(Clamp01(g));
                    texture.B[i] = PbrMapGenerator.SrgbToLinear(Clamp01(b));
                    texture.Alpha[i] = (x >= 3 && x < 8 && y >= 2 && y < 6) ? 0.0 : 1.0;
                }
            }

            return texture;
        }

        private static double Clamp01(double v)
        {
            return v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);
        }
    }
}
