using System;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// v15. The travelling-gust field of <c>pbrterrainmaterial.glsl</c>
    /// (<c>vvTerrainGust</c>), evaluated on the CPU, so a particle can know
    /// whether the gust the player sees bending the grass is passing over the
    /// place it spawns.
    ///
    /// A PORT, not a second field. Every constant and the integer hash are the
    /// shader's, and <c>tools/smoketest</c> reads them back out of the shipped
    /// GLSL and fails if either side moves without the other. Two fields that
    /// drifted apart would put a flurry of leaves where the grass is still.
    ///
    /// FOUNDATION ONLY (atmosphere rule 10), as the shader's is: the game has no
    /// gust field. Where a gust is, is invented; whether there is wind, which
    /// way it blows and how hard are the game's.
    ///
    /// Positions are world X/Z wrapped to <see cref="Common.Scene.EnvironmentState.CameraPeriod"/>
    /// in double before they get here, the same wrap the shader's origin
    /// uniform carries; the grid tiles 4096 blocks exactly (250 cells of
    /// 16.384), so the wrap lands on a cell boundary. Pure.
    /// </summary>
    public static class GustField
    {
        public const float Cell = 16.384f;
        public const float Period = 250f;
        public const float Travel = 96f;
        public const float Contrast = 1.6f;

        /// <summary>The shader's cut on the re-expanded noise: below the first is calm air, above the second the heart of a gust.</summary>
        public const float CutLow = 0.55f;
        public const float CutHigh = 0.8f;

        /// <summary>The game's wind below which there are no gusts at all, and above which they are at full size.</summary>
        public const float WindLow = 0.08f;
        public const float WindHigh = 0.5f;

        public const uint HashMulA = 0x7feb352du;
        public const uint HashMulB = 0x846ca68bu;
        public const uint CellMulX = 0x9e3779b9u;
        public const uint CellAddY = 0x85ebca6bu;

        /// <summary>The two copies' keys, as in the shader: (71, 23) and (79, 47).</summary>
        public const float KeyAX = 71f, KeyAY = 23f, KeyBX = 79f, KeyBY = 47f;

        public static uint HashU(uint x)
        {
            unchecked
            {
                x ^= x >> 16; x *= HashMulA;
                x ^= x >> 15; x *= HashMulB;
                x ^= x >> 16;
                return x;
            }
        }

        /// <summary>vvTerrainCellRandom(cell).x: GLSL's uvec2(ivec2(cell)), so a negative cell wraps as two's complement.</summary>
        public static float CellRandom(float cx, float cy)
        {
            unchecked
            {
                uint x = (uint)(int)cx;
                uint y = (uint)(int)cy;
                uint h = HashU(x * CellMulX ^ HashU(y + CellAddY));
                return (float)HashU(h) / 4294967295f;
            }
        }

        private static float GlslMod(float a, float b)
        {
            return a - b * (float)Math.Floor(a / b);
        }

        private static float Smooth(float e0, float e1, float x)
        {
            float t = Math.Max(0f, Math.Min(1f, (x - e0) / (e1 - e0)));
            return t * t * (3f - 2f * t);
        }

        /// <summary>vvTerrainFleckNoise: value noise, 0..1, on a grid that tiles in <paramref name="period"/> cells.</summary>
        public static float Noise(float x, float z, float cell, float period, float keyX, float keyY)
        {
            float gx = x / cell, gz = z / cell;
            float ix = (float)Math.Floor(gx), iz = (float)Math.Floor(gz);
            float fx = gx - ix, fz = gz - iz;
            fx = fx * fx * (3f - 2f * fx);
            fz = fz * fz * (3f - 2f * fz);
            float a = CellRandom(GlslMod(ix, period) + keyX, GlslMod(iz, period) + keyY);
            float b = CellRandom(GlslMod(ix + 1f, period) + keyX, GlslMod(iz, period) + keyY);
            float c = CellRandom(GlslMod(ix, period) + keyX, GlslMod(iz + 1f, period) + keyY);
            float d = CellRandom(GlslMod(ix + 1f, period) + keyX, GlslMod(iz + 1f, period) + keyY);
            float ab = a + (b - a) * fx;
            float cd = c + (d - c) * fx;
            return ab + (cd - ab) * fz;
        }

        /// <summary>
        /// 0 calm to 1 the heart of a gust, at a wrapped world X/Z: the shader's
        /// <c>vvTerrainGust</c>. <paramref name="gusts"/> is the slider,
        /// <paramref name="wind"/> the game's wind 0..1, <paramref name="breeze"/>
        /// the tracker's breeze clock, <paramref name="dirX"/>/<paramref name="dirZ"/>
        /// the game's wind direction (any length; zero reads as +X, as there).
        /// </summary>
        public static float At(float x, float z, float gusts, float wind, float breeze, float dirX, float dirZ)
        {
            if (!(gusts >= 0.001f)) return 0f;
            float w = Smooth(WindLow, WindHigh, Math.Max(0f, Math.Min(1f, wind)));
            if (w <= 0f) return 0f;

            float len2 = dirX * dirX + dirZ * dirZ;
            float dx = 1f, dz = 0f;
            if (len2 > 1e-4f)
            {
                float len = (float)Math.Sqrt(len2);
                dx = dirX / len;
                dz = dirZ / len;
            }

            float a = breeze - (float)Math.Floor(breeze);
            float b = (breeze + 0.5f) - (float)Math.Floor(breeze + 0.5f);
            float wa = 1f - Math.Abs(2f * a - 1f);
            float wb = 1f - wa;

            float na = Noise(x - dx * (Travel * (a - 0.5f)), z - dz * (Travel * (a - 0.5f)), Cell, Period, KeyAX, KeyAY) - 0.5f;
            float nb = Noise(x - dx * (Travel * (b - 0.5f)), z - dz * (Travel * (b - 0.5f)), Cell, Period, KeyBX, KeyBY) - 0.5f;
            float n = na * wa + nb * wb;
            n = 0.5f + n * Contrast / (float)Math.Sqrt(Math.Max(wa * wa + wb * wb, 0.5f));
            return Smooth(CutLow, CutHigh, n) * w * Math.Min(1f, gusts);
        }
    }
}
