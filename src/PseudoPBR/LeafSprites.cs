using System;
using System.Collections.Generic;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// v15. Cuts single leaf shapes out of a tree's own leaf texture.
    ///
    /// A Vintage Story leaf texture is a greyscale CLUSTER of overlapping
    /// leaves (64x64 for the broadleaf trees), tinted at draw time by the
    /// block's climate and season colour maps. A window of it is a fragment of
    /// a cluster, not a leaf. So each sprite grows from one seed texel by
    /// 4-connected flood fill through the opaque texels, limited to a cell
    /// around the seed: what comes out is a leaf-sized connected piece of the
    /// real art, with its real outline and its real shading, and nothing that
    /// is not in the texture. The grey stays grey; the tint is applied per
    /// leaf when it is drawn, from the tree it fell from.
    ///
    /// Pure: pixels in, a sprite sheet out. The caller loads the PNG and
    /// uploads the sheet.
    /// </summary>
    public static class LeafSprites
    {
        /// <summary>Sprites per sheet side; the sheet holds Side x Side leaves.</summary>
        public const int Side = 4;

        /// <summary>A sprite's cell as a share of the texture's width: 12 texels on a 64 texture.</summary>
        public const float CellShare = 0.1875f;
        public const int MinCell = 6;

        /// <summary>A sprite must fill this share of its cell, or it is a sliver or a clump, not a leaf.</summary>
        public const float MinFill = 0.2f;
        public const float MaxFill = 0.7f;
        public const float TargetFill = 0.4f;

        public const int AlphaCut = 128;

        /// <summary>
        /// How far the fill may stray from the seed's grey. The art shades each
        /// leaf as one tone against its neighbours, so a fill that keeps to the
        /// seed's tone stops at the leaf's own edge instead of running on into
        /// the leaf it overlaps.
        /// </summary>
        public const int GreyTolerance = 40;

        /// <summary>The fill stays within this share of the cell from the seed: a round leaf, not a square cut.</summary>
        public const float Radius = 0.5f;

        public static int CellSize(int textureWidth)
        {
            return Math.Max(MinCell, (int)Math.Round(textureWidth * CellShare));
        }

        public static int Alpha(int argb) { return (argb >> 24) & 0xff; }

        public static int Grey(int argb)
        {
            return (((argb >> 16) & 0xff) + ((argb >> 8) & 0xff) + (argb & 0xff)) / 3;
        }

        private static int At(int[] argb, int w, int h, int x, int y)
        {
            // Leaf textures tile, so the fill wraps across the edge as the art does.
            x %= w; if (x < 0) x += w;
            y %= h; if (y < 0) y += h;
            return argb[y * w + x];
        }

        /// <summary>
        /// One sprite: flood fill from (sx, sy) through opaque texels within
        /// <see cref="GreyTolerance"/> of the seed's grey and within
        /// <see cref="Radius"/> x cell of it, wrapping as the texture tiles.
        /// Returns the cell's pixels (ARGB, transparent outside the fill), how
        /// many texels it took, and how many of those touch the radius limit
        /// (a leaf that stopped at its own edge touches it little).
        /// </summary>
        public static int[] Grow(int[] argb, int w, int h, int sx, int sy, int cell, out int filled, out int atLimit)
        {
            var sprite = new int[cell * cell];
            filled = 0;
            atLimit = 0;
            int seed = At(argb, w, h, sx, sy);
            if (Alpha(seed) < AlphaCut) return sprite;
            int g0 = Grey(seed);

            int ox = sx - cell / 2, oy = sy - cell / 2;
            float r = cell * Radius, r2 = r * r, edge2 = (r - 1.5f) * (r - 1.5f);
            var seen = new bool[cell * cell];
            var stack = new Stack<int>();
            int first = (sy - oy) * cell + (sx - ox);
            stack.Push(first);
            seen[first] = true;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int cx = i % cell, cy = i / cell;
                int tx = cx + ox, ty = cy + oy;
                sprite[i] = At(argb, w, h, tx, ty) | unchecked((int)0xFF000000);
                filled++;
                float ex = tx - sx, ey = ty - sy;
                if (ex * ex + ey * ey > edge2) atLimit++;
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    int ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= cell || ny >= cell) continue;
                    float dx = nx + ox - sx, dy = ny + oy - sy;
                    if (dx * dx + dy * dy > r2) continue;
                    int j = ny * cell + nx;
                    if (seen[j]) continue;
                    seen[j] = true;
                    int c = At(argb, w, h, nx + ox, ny + oy);
                    if (Alpha(c) < AlphaCut || Math.Abs(Grey(c) - g0) > GreyTolerance) continue;
                    stack.Push(j);
                }
            }
            return sprite;
        }

        /// <summary>
        /// A Side x Side sheet of sprites cut from one texture, as ARGB pixels
        /// of (Side * cell) square, or null if the texture yields no leaf at
        /// all. Each sprite is the best of up to 60 seeds: a fill near
        /// <see cref="TargetFill"/>, that stopped at its own edge rather than
        /// at the radius, from a light seed (a front leaf, whole, rather than
        /// one half hidden behind it).
        /// </summary>
        public static int[] BuildSheet(int[] argb, int w, int h, int seed, out int cell, out int sprites)
        {
            cell = CellSize(w);
            sprites = 0;
            if (argb == null || w <= 0 || h <= 0 || argb.Length < w * h) return null;

            int size = Side * cell;
            var sheet = new int[size * size];
            var rnd = new Random(seed);

            for (int s = 0; s < Side * Side; s++)
            {
                int[] best = null;
                float bestScore = float.MaxValue;
                for (int t = 0; t < 60; t++)
                {
                    int sx = rnd.Next(w), sy = rnd.Next(h);
                    int[] sprite = Grow(argb, w, h, sx, sy, cell, out int filled, out int atLimit);
                    float fill = filled / (float)(cell * cell);
                    if (fill < MinFill || fill > MaxFill) continue;
                    float score = Math.Abs(fill - TargetFill) + atLimit / (float)filled
                                + (255 - Grey(argb[sy * w + sx])) / 255f * 0.3f;
                    if (score < bestScore) { bestScore = score; best = sprite; }
                }
                if (best == null) continue;
                sprites++;
                int ox = (s % Side) * cell, oy = (s / Side) * cell;
                for (int y = 0; y < cell; y++)
                    Array.Copy(best, y * cell, sheet, (oy + y) * size + ox, cell);
            }
            return sprites == 0 ? null : sheet;
        }

        /// <summary>
        /// The game's tint for a greyscale texture: what its colour maps make of
        /// the texture's grey, per channel, as a multiplier. From tinted samples
        /// the game itself produced (Block.GetRandomColor) over the texture's
        /// own mean grey. Pure; 1 where nothing is known.
        /// </summary>
        public static void Tint(int[] tintedArgb, int count, float meanGrey, out float r, out float g, out float b)
        {
            r = g = b = 1f;
            if (tintedArgb == null || count <= 0 || !(meanGrey > 1f)) return;
            double sr = 0, sg = 0, sb = 0;
            for (int i = 0; i < count; i++)
            {
                int c = tintedArgb[i];
                sr += (c >> 16) & 0xff;
                sg += (c >> 8) & 0xff;
                sb += c & 0xff;
            }
            r = (float)Math.Min(2.0, sr / count / meanGrey);
            g = (float)Math.Min(2.0, sg / count / meanGrey);
            b = (float)Math.Min(2.0, sb / count / meanGrey);
        }

        /// <summary>Mean grey (mean of R, G and B) of the opaque texels. Pure.</summary>
        public static float MeanGrey(int[] argb)
        {
            if (argb == null) return 0f;
            double sum = 0;
            int n = 0;
            foreach (int c in argb)
            {
                if (Alpha(c) < AlphaCut) continue;
                sum += (((c >> 16) & 0xff) + ((c >> 8) & 0xff) + (c & 0xff)) / 3.0;
                n++;
            }
            return n == 0 ? 0f : (float)(sum / n);
        }
    }
}
