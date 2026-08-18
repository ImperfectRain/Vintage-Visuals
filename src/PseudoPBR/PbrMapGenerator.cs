using System;
using System.Collections.Generic;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// A diffuse texture and its derived maps, as flat row-major arrays.
    /// Flat rather than 2D because this is what eventually gets handed to a
    /// texture upload or a compute shader.
    /// </summary>
    public sealed class LinearTexture
    {
        public int Width;
        public int Height;

        /// <summary>Linear-light RGB, length Width*Height each.</summary>
        public double[] R;
        public double[] G;
        public double[] B;

        /// <summary>Straight alpha in [0,1], length Width*Height.</summary>
        public double[] Alpha;

        public int Index(int x, int y) => y * Width + x;
    }

    public sealed class PbrMaps
    {
        public int Width;
        public int Height;

        /// <summary>Encoded tangent-space normal, Width*Height*3, values in [0,1].</summary>
        public double[] Normal;

        /// <summary>Roughness, Width*Height.</summary>
        public double[] Roughness;

        /// <summary>Specular mask, Width*Height.</summary>
        public double[] Spec;
    }

    /// <summary>
    /// C# port of the validated offline prototype in tools/pbrgen (Phase 4a).
    ///
    /// This is a PORT, not a re-derivation. The Python version is the reference
    /// implementation: its constants were tuned against measured output and its
    /// behaviour is pinned by 31 tests. Every number and every operation order
    /// here matches it deliberately, and tools/smoketest asserts that the two
    /// agree on a fixture. If you change a constant here, change it there too
    /// and re-run both — a silent divergence between the tool people tune with
    /// and the code that actually ships is the worst outcome available.
    ///
    /// Deliberately free of any game API type, so it can be exercised without a
    /// running client. The atlas-side glue (hooking texture upload, caching by
    /// hash) belongs in a separate class; this one only does the maths.
    ///
    /// See tools/pbrgen/README.md for what these passes can and cannot infer -
    /// in particular that albedo does not carry metalness, and Sobel cannot
    /// distinguish painted shading from geometry.
    /// </summary>
    public static class PbrMapGenerator
    {
        // --- Tuning constants. Keep in lockstep with tools/pbrgen/pbrgen.py. ---

        /// <summary>Local luminance std-dev, in linear light, mapping to fully rough.</summary>
        public const double RoughnessStdReference = 0.25;

        /// <summary>Nothing in a hand-painted block texture is a mirror.</summary>
        public const double RoughnessFloor = 0.25;

        /// <summary>RGB distance within which two pixels count as the same material.</summary>
        public const double SpecRegionThreshold = 0.35;

        /// <summary>Regions below this share of the texture are treated as inclusions.</summary>
        public const double SpecInclusionMaxArea = 0.15;
        public const double SpecInclusionBoost = 1.6;

        /// <summary>Pixels at or below this alpha contribute to nothing.</summary>
        public const double AlphaCutoff = 0.5;

        public const double DefaultNormalStrength = 1.0;
        public const int DefaultVarianceRadius = 1;

        // Rec.709 luma weights, matching the primaries the game renders in.
        private const double LumaR = 0.2126;
        private const double LumaG = 0.7152;
        private const double LumaB = 0.0722;

        // --- Colour space ---

        public static double SrgbToLinear(double srgb)
        {
            if (srgb <= 0.0) return 0.0;
            if (srgb >= 1.0) return 1.0;
            return srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }

        public static double LinearToSrgb(double linear)
        {
            if (linear <= 0.0) return 0.0;
            if (linear >= 1.0) return 1.0;
            return linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
        }

        /// <summary>Rec.709 relative luminance of a linear-light texture.</summary>
        public static double[] Luminance(LinearTexture texture)
        {
            var lum = new double[texture.Width * texture.Height];
            for (int i = 0; i < lum.Length; i++)
            {
                lum[i] = texture.R[i] * LumaR + texture.G[i] * LumaG + texture.B[i] * LumaB;
            }
            return lum;
        }

        // --- Neighbourhood sampling ---

        /// <summary>
        /// Reads a texel, wrapping or clamping at the edges.
        ///
        /// Block textures tile, so wrapping is the correct default: clamping
        /// instead produces a ring of wrong values around every block face,
        /// which is obvious in game and easy to mistake for a UV bug.
        /// </summary>
        private static double Sample(double[] data, int width, int height, int x, int y, bool tiling)
        {
            if (tiling)
            {
                x = ((x % width) + width) % width;
                y = ((y % height) + height) % height;
            }
            else
            {
                if (x < 0) x = 0; else if (x >= width) x = width - 1;
                if (y < 0) y = 0; else if (y >= height) y = height - 1;
            }

            return data[y * width + x];
        }

        /// <summary>
        /// Mean over a square window.
        ///
        /// A direct windowed sum rather than the summed-area table the Python
        /// reference uses. At these texture sizes the cost is irrelevant and
        /// this is far easier to check by eye; the SAT there remains the model
        /// for an eventual compute-shader pass, where window size does matter.
        /// </summary>
        private static double[] BoxMean(double[] data, int width, int height, int radius, bool tiling)
        {
            var result = new double[width * height];
            double window = (2 * radius + 1) * (2 * radius + 1);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    double total = 0.0;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            total += Sample(data, width, height, x + dx, y + dy, tiling);
                        }
                    }
                    result[y * width + x] = total / window;
                }
            }

            return result;
        }

        // --- Pass 1: normal from luminance ---

        /// <summary>
        /// Sobel-derived tangent-space normal map, OpenGL convention (+Y up).
        ///
        /// Treats luminance as a height field, which is wrong in a specific and
        /// predictable way: painted shading is read as geometry. See
        /// tools/pbrgen/README.md.
        /// </summary>
        public static double[] GenerateNormalFromLuminance(double[] lum, int width, int height,
                                                           double strength = DefaultNormalStrength,
                                                           bool tiling = true)
        {
            var normal = new double[width * height * 3];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    double tl = Sample(lum, width, height, x - 1, y - 1, tiling);
                    double tc = Sample(lum, width, height, x, y - 1, tiling);
                    double tr = Sample(lum, width, height, x + 1, y - 1, tiling);
                    double ml = Sample(lum, width, height, x - 1, y, tiling);
                    double mr = Sample(lum, width, height, x + 1, y, tiling);
                    double bl = Sample(lum, width, height, x - 1, y + 1, tiling);
                    double bc = Sample(lum, width, height, x, y + 1, tiling);
                    double br = Sample(lum, width, height, x + 1, y + 1, tiling);

                    double gx = (tr + 2.0 * mr + br) - (tl + 2.0 * ml + bl);
                    double gy = (bl + 2.0 * bc + br) - (tl + 2.0 * tc + tr);

                    // n = normalize(-dh/dx, -dh/dy_world, 1). dh/dy_world is
                    // -gy because image rows run downward, which is why y is
                    // +gy and not -gy.
                    double nx = -gx * strength;
                    double ny = gy * strength;
                    double nz = 1.0;

                    double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    int o = (y * width + x) * 3;
                    normal[o] = nx / length * 0.5 + 0.5;
                    normal[o + 1] = ny / length * 0.5 + 0.5;
                    normal[o + 2] = nz / length * 0.5 + 0.5;
                }
            }

            return normal;
        }

        // --- Pass 2: roughness from local variance ---

        /// <summary>
        /// Roughness from local luminance variance, absolute-mapped.
        ///
        /// Not normalised per texture: normalising would stretch every texture
        /// across the full range, so uniform stone would read as rough as
        /// gravel and the whole point - materials differing from each other -
        /// would be lost.
        /// </summary>
        public static double[] GenerateRoughnessFromVariance(double[] lum, int width, int height,
                                                             int radius = DefaultVarianceRadius,
                                                             bool tiling = true,
                                                             double stdReference = RoughnessStdReference,
                                                             double floor = RoughnessFloor)
        {
            var squared = new double[lum.Length];
            for (int i = 0; i < lum.Length; i++) squared[i] = lum[i] * lum[i];

            double[] mean = BoxMean(lum, width, height, radius, tiling);
            double[] meanSquared = BoxMean(squared, width, height, radius, tiling);

            var roughness = new double[lum.Length];
            for (int i = 0; i < lum.Length; i++)
            {
                // Clamp at zero: catastrophic cancellation in E[x^2]-E[x]^2 can
                // go slightly negative on near-uniform regions, and the square
                // root of that is a NaN that propagates into the texture.
                double variance = Math.Max(meanSquared[i] - mean[i] * mean[i], 0.0);
                double std = Math.Sqrt(variance);
                double scaled = std / stdReference;
                if (scaled > 1.0) scaled = 1.0;
                else if (scaled < 0.0) scaled = 0.0;

                roughness[i] = Math.Min(Math.Max(floor + (1.0 - floor) * scaled, 0.0), 1.0);
            }

            return roughness;
        }

        // --- Pass 3: spec mask from colour regions ---

        /// <summary>
        /// Flood-fills connected texels whose colour is close to their seed.
        ///
        /// Compared against the SEED colour, not a running region mean: a
        /// running mean lets a region drift arbitrarily far from where it
        /// started, so a smooth gradient merges into one blob and the ore speck
        /// being isolated is swallowed by the stone around it.
        /// </summary>
        internal static int LabelColourRegions(LinearTexture texture, bool[] valid, double threshold,
                                               bool tiling, int[] labels)
        {
            int width = texture.Width;
            int height = texture.Height;
            for (int i = 0; i < labels.Length; i++) labels[i] = -1;

            int regionCount = 0;
            double thresholdSquared = threshold * threshold;
            var queue = new Queue<int>();

            int[] neighbourDx = { 0, 0, -1, 1 };
            int[] neighbourDy = { -1, 1, 0, 0 };

            for (int startY = 0; startY < height; startY++)
            {
                for (int startX = 0; startX < width; startX++)
                {
                    int start = startY * width + startX;
                    if (labels[start] != -1 || !valid[start]) continue;

                    double seedR = texture.R[start];
                    double seedG = texture.G[start];
                    double seedB = texture.B[start];

                    labels[start] = regionCount;
                    queue.Clear();
                    queue.Enqueue(start);

                    while (queue.Count > 0)
                    {
                        int current = queue.Dequeue();
                        int cx = current % width;
                        int cy = current / width;

                        for (int n = 0; n < 4; n++)
                        {
                            int nx = cx + neighbourDx[n];
                            int ny = cy + neighbourDy[n];

                            if (tiling)
                            {
                                nx = ((nx % width) + width) % width;
                                ny = ((ny % height) + height) % height;
                            }
                            else if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                            {
                                continue;
                            }

                            int neighbour = ny * width + nx;
                            if (labels[neighbour] != -1 || !valid[neighbour]) continue;

                            double dr = texture.R[neighbour] - seedR;
                            double dg = texture.G[neighbour] - seedG;
                            double db = texture.B[neighbour] - seedB;
                            if (dr * dr + dg * dg + db * db > thresholdSquared) continue;

                            labels[neighbour] = regionCount;
                            queue.Enqueue(neighbour);
                        }
                    }

                    regionCount++;
                }
            }

            return regionCount;
        }

        /// <summary>
        /// Specular mask from per-region average colour: bright and desaturated
        /// reads as metal or polish, saturated reads as pigment.
        ///
        /// Per region rather than per pixel, which is what makes ore veins come
        /// out as coherent metallic patches instead of speckled noise.
        /// </summary>
        public static double[] GenerateSpecMaskFromColourAverage(LinearTexture texture,
                                                                 double threshold = SpecRegionThreshold,
                                                                 bool tiling = true)
        {
            int count = texture.Width * texture.Height;
            var spec = new double[count];
            var valid = new bool[count];

            int validCount = 0;
            for (int i = 0; i < count; i++)
            {
                valid[i] = texture.Alpha[i] > AlphaCutoff;
                if (valid[i]) validCount++;
            }

            if (validCount == 0) return spec;

            var labels = new int[count];
            int regionCount = LabelColourRegions(texture, valid, threshold, tiling, labels);

            var areas = new int[regionCount];
            var sumR = new double[regionCount];
            var sumG = new double[regionCount];
            var sumB = new double[regionCount];

            for (int i = 0; i < count; i++)
            {
                int region = labels[i];
                if (region < 0) continue;
                areas[region]++;
                sumR[region] += texture.R[i];
                sumG[region] += texture.G[i];
                sumB[region] += texture.B[i];
            }

            var regionSpec = new double[regionCount];
            for (int region = 0; region < regionCount; region++)
            {
                if (areas[region] == 0) continue;

                double area = areas[region];
                double meanR = sumR[region] / area;
                double meanG = sumG[region] / area;
                double meanB = sumB[region] / area;

                double peak = Math.Max(meanR, Math.Max(meanG, meanB));
                double trough = Math.Min(meanR, Math.Min(meanG, meanB));
                double saturation = peak <= 1e-6 ? 0.0 : (peak - trough) / peak;
                double value = meanR * LumaR + meanG * LumaG + meanB * LumaB;

                double result = (1.0 - saturation) * value;

                // A small bright patch inside a dull field is almost always
                // meant to read as metal or crystal.
                if (area / validCount <= SpecInclusionMaxArea) result *= SpecInclusionBoost;

                regionSpec[region] = Math.Min(result, 1.0);
            }

            for (int i = 0; i < count; i++)
            {
                int region = labels[i];
                if (region < 0) continue;
                spec[i] = Math.Min(Math.Max(regionSpec[region], 0.0), 1.0);
            }

            return spec;
        }

        /// <summary>Runs all three passes over one texture.</summary>
        public static PbrMaps Generate(LinearTexture texture,
                                       double strength = DefaultNormalStrength,
                                       int radius = DefaultVarianceRadius,
                                       bool tiling = true)
        {
            double[] lum = Luminance(texture);

            // Fully transparent texels have arbitrary RGB. Substituting the
            // mean of the visible pixels stops the gradient pass from carving a
            // hard cliff around every leaf edge.
            int visible = 0;
            double visibleSum = 0.0;
            for (int i = 0; i < lum.Length; i++)
            {
                if (texture.Alpha[i] > AlphaCutoff) { visible++; visibleSum += lum[i]; }
            }

            if (visible > 0 && visible < lum.Length)
            {
                double fill = visibleSum / visible;
                for (int i = 0; i < lum.Length; i++)
                {
                    if (texture.Alpha[i] <= AlphaCutoff) lum[i] = fill;
                }
            }

            return new PbrMaps
            {
                Width = texture.Width,
                Height = texture.Height,
                Normal = GenerateNormalFromLuminance(lum, texture.Width, texture.Height, strength, tiling),
                Roughness = GenerateRoughnessFromVariance(lum, texture.Width, texture.Height, radius, tiling),
                Spec = GenerateSpecMaskFromColourAverage(texture, SpecRegionThreshold, tiling)
            };
        }
    }
}
