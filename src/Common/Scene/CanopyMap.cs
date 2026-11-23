using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageVisuals.Common.Scene
{
    /// <summary>
    /// Where the leaves are, around the player: for each 2x2-block cell, how
    /// much leaf is overhead and how high the lowest leaf sits.
    ///
    /// AUTHORITATIVE GAME DATA, read from the world's own blocks
    /// (<see cref="EnumBlockMaterial.Leaves"/>). It exists because vanilla's
    /// shadow map cannot answer "is foliage casting this shadow": under a solid
    /// crown it draws one flat shadow with no gaps, so the ring-count canopy
    /// test finds nothing there, and a wall's shadow looks the same as a tree's.
    /// This map is the gate for canopy shade and sunflecks; the fleck PATTERN is
    /// still invented (see pbrterrainmaterial.glsl, DATA GAP).
    ///
    /// Storage is TOROIDAL in world cells: cell (cx, cz) lives at
    /// (cx mod 32, cz mod 32). The shader computes the same index from wrapped
    /// world coordinates, which agree because the wrap period (4096) is a
    /// multiple of the map's span (64). Moving does not invalidate what was
    /// scanned; the rolling scan refreshes every cell within about a second.
    ///
    /// Encoding, per cell, 12 bits: density (0..7) * 512 + lowest leaf Y
    /// (0..511). Two cells per float, which a float32 holds exactly. A density
    /// of 0 means no leaves, which is the harmless value.
    /// </summary>
    public sealed class CanopyMap
    {
        public const int Grid = 32;
        public const int CellBlocks = 2;
        public const int Cells = Grid * Grid;
        public const int PackedFloats = Cells / 2;
        public const int PackedVectors = PackedFloats / 4;

        /// <summary>Cells scanned per call to <see cref="Scan"/>. A full refresh takes Cells / this calls.</summary>
        public const int ColumnsPerScan = 128;

        /// <summary>
        /// Blocks searched below and above each column's rain-map height, the
        /// top of whatever stops rain there - the crown's top in a forest.
        /// Anchored to the world, not the player: a search window around the
        /// player's Y missed the whole canopy when the player flew above it.
        /// Above as well as below, in case a leaf block lets rain through and
        /// the rain map reads the ground under the tree instead.
        /// </summary>
        public const int SearchDepth = 20;
        public const int SearchUp = 20;

        /// <summary>The player-relative window used where a column's rain map reads 0.</summary>
        public const int FallbackAbove = 40;
        public const int FallbackBelow = 12;

        private readonly float[] _packed = new float[PackedFloats];
        private readonly int[] _cells = new int[Cells];
        private int _cursor;
        private int _scanned;
        private long _requestedAtMs = long.MinValue;
        private double _planeTarget = double.NaN;
        private double _plane = double.NaN;

        /// <summary>The packed uniform array, <see cref="PackedVectors"/> vec4s.</summary>
        public float[] Packed { get { return _packed; } }

        /// <summary>True once every cell has been scanned at least once.</summary>
        public bool Valid { get { return _scanned >= Cells; } }

        /// <summary>Bumped whenever <see cref="Packed"/> changes.</summary>
        public int Version { get; private set; }

        /// <summary>
        /// The world Y the sunfleck pattern is projected from: the mean lowest
        /// leaf of every leafy cell, plus a block and a half into the crown.
        /// ONE height for the scene, eased over a couple of seconds, because a
        /// per-cell height shifts the pattern at every cell edge and a jump in
        /// this one slides every fleck at once. 0 until a leaf is seen.
        /// </summary>
        public float PlaneY { get { return double.IsNaN(_plane) ? 0f : (float)_plane; } }

        /// <summary>Weight of one scan step in easing <see cref="PlaneY"/> toward its target.</summary>
        public const double PlaneEase = 0.05;

        /// <summary>
        /// Called by a consumer every frame it wants the map. Scanning stops two
        /// seconds after the last request, so a player with every canopy effect
        /// off pays nothing.
        /// </summary>
        public void Request(long nowMs) { _requestedAtMs = nowMs; }

        public bool Wanted(long nowMs) { return nowMs - _requestedAtMs < 2000; }

        public static int Encode(int density, int lowestY)
        {
            return GameMath.Clamp(density, 0, 7) * 512 + GameMath.Clamp(lowestY, 0, 511);
        }

        public static int Index(int worldBlockX, int worldBlockZ)
        {
            int cx = FloorDiv(worldBlockX, CellBlocks);
            int cz = FloorDiv(worldBlockZ, CellBlocks);
            return Mod(cz, Grid) * Grid + Mod(cx, Grid);
        }

        /// <summary>
        /// Scans the next <see cref="ColumnsPerScan"/> cells of the window
        /// centred on the player. Render thread only; block reads are cheap but
        /// not free: two columns of SearchDepth blocks per cell.
        /// </summary>
        public void Scan(ICoreClientAPI capi, EntityPos player)
        {
            IBlockAccessor blocks = capi?.World?.BlockAccessor;
            if (blocks == null || player == null) return;

            int px = (int)Math.Floor(player.X);
            int py = (int)Math.Floor(player.Y);
            int pz = (int)Math.Floor(player.Z);
            int baseCx = FloorDiv(px, CellBlocks) - Grid / 2;
            int baseCz = FloorDiv(pz, CellBlocks) - Grid / 2;

            var pos = new BlockPos(player.Dimension);
            bool changed = false;

            for (int n = 0; n < ColumnsPerScan; n++)
            {
                int i = _cursor;
                _cursor = (_cursor + 1) % Cells;

                int cx = baseCx + i % Grid;
                int cz = baseCz + i / Grid;
                // Both diagonal columns of the cell. One column missed every
                // crown that happened to leave that corner open, and each miss
                // showed as a square hole in the canopy, two blocks wide.
                int x = cx * CellBlocks;
                int z = cz * CellBlocks;

                int leaves = 0;
                int lowest = -1;
                for (int corner = 0; corner < 2; corner++)
                {
                    int columnLeaves = 0;
                    // A column whose rain map is not loaded reads 0. Fall back
                    // to the old window around the player rather than finding
                    // no leaves, so a missing map degrades instead of erasing.
                    int top = blocks.GetRainMapHeightAt(x + corner, z + corner);
                    int depth = SearchDepth + SearchUp;
                    if (top <= 0) { top = py + FallbackAbove; depth = FallbackAbove + FallbackBelow; }
                    else top += SearchUp;
                    for (int y = top; y > top - depth && y >= 0; y--)
                    {
                        pos.Set(x + corner, y, z + corner);
                        Block block = blocks.GetBlock(pos);
                        if (block != null && block.BlockMaterial == EnumBlockMaterial.Leaves)
                        {
                            columnLeaves++;
                            if (lowest < 0 || y < lowest) lowest = y;
                        }
                    }
                    leaves = Math.Max(leaves, columnLeaves);
                }

                // Four leaf blocks in a column is a full crown; one is a
                // sparse twig.
                int density = leaves == 0 ? 0 : Math.Min(7, 1 + leaves * 3 / 2);
                int code = leaves == 0 ? 0 : Encode(density, lowest);

                int slot = Index(x, z);
                if (_cells[slot] != code)
                {
                    _cells[slot] = code;
                    changed = true;
                }
                if (_scanned < Cells) _scanned++;
            }

            if (changed)
            {
                for (int f = 0; f < PackedFloats; f++)
                    _packed[f] = _cells[f * 2] + _cells[f * 2 + 1] * 4096f;
                Version++;
                _planeTarget = MeanCanopyY(_cells);
            }
            EasePlane();
        }

        /// <summary>Mean lowest leaf Y plus 1.5 over the leafy cells, or NaN when there are none.</summary>
        public static double MeanCanopyY(int[] cells)
        {
            double sum = 0;
            int count = 0;
            foreach (int code in cells)
            {
                if (code < 512) continue;
                sum += code % 512;
                count++;
            }
            return count == 0 ? double.NaN : sum / count + 1.5;
        }

        private void EasePlane()
        {
            if (double.IsNaN(_planeTarget)) return;
            _plane = double.IsNaN(_plane) ? _planeTarget : _plane + (_planeTarget - _plane) * PlaneEase;
        }

        public void Reset()
        {
            Array.Clear(_cells, 0, _cells.Length);
            Array.Clear(_packed, 0, _packed.Length);
            _cursor = 0;
            _scanned = 0;
            _planeTarget = double.NaN;
            _plane = double.NaN;
            Version++;
        }

        private static int FloorDiv(int a, int b) { return (int)Math.Floor(a / (double)b); }

        private static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }
    }
}
