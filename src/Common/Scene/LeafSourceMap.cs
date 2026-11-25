using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageVisuals.Common.Scene
{
    /// <summary>
    /// D78. Where falling leaves can come from, out to 64 blocks around the
    /// player: for each 4x4-block cell, how much leaf there is and how high the
    /// lowest leaf sits. CPU only; nothing uploads it.
    ///
    /// AUTHORITATIVE GAME DATA, read from the world's own blocks
    /// (<see cref="EnumBlockMaterial.Leaves"/>), like <see cref="CanopyMap"/>.
    /// It exists because that map's reach is fixed by the shader that reads it
    /// (32 x 32 cells of 2 blocks, so 32 blocks each way, 128 uniform vec4s),
    /// and the leaf spawner wants 50. Growing the shader's map would cost the
    /// terrain shader four times the uniforms; a second, coarser map that only
    /// the spawner reads costs nothing on the GPU.
    ///
    /// Cheaper per cell than <see cref="CanopyMap"/>: one column per cell, and
    /// the search starts at the rain map, which ends on the crown's top
    /// (leaf blocks are not rain-permeable in the game's block types), walks
    /// down through the crown and stops two blocks after it ends. Open
    /// ground costs about four reads; a crown about its depth plus four.
    /// The column moves round the cell's four diagonal spots on each refresh,
    /// and a cell keeps the densest reading of the last four, so a crown that
    /// leaves one spot open is not lost.
    ///
    /// Cost: <see cref="ColumnsPerScan"/> columns per tick, a full refresh
    /// every Cells / ColumnsPerScan ticks. Not measured in game; at about
    /// eight reads a column that is about 2,000 block reads a tick, against
    /// about 10,000 for <see cref="CanopyMap"/>.
    /// </summary>
    public sealed class LeafSourceMap
    {
        public const int Grid = 32;
        public const int CellBlocks = 4;
        public const int Cells = Grid * Grid;
        public const int ColumnsPerScan = 256;

        /// <summary>Blocks searched above the rain map's top, in case a leaf sits over something that stops rain lower down.</summary>
        public const int SearchUp = 2;

        /// <summary>Blocks below the top searched for the start of a crown before a column is called bare.</summary>
        public const int BareDepth = 2;

        /// <summary>Deepest crown read, in blocks below the top.</summary>
        public const int MaxDepth = 24;

        /// <summary>The span this map covers each way from the player: Grid * CellBlocks / 2.</summary>
        public const int HalfSpan = Grid * CellBlocks / 2;

        private readonly int[] _cells = new int[Cells];
        private readonly int[] _recent = new int[Cells * 4];
        private int _cursor;
        private int _pass;
        private int _scanned;
        private long _requestedAtMs = long.MinValue;
        private int _windowCx = int.MinValue / 2;
        private int _windowCz = int.MinValue / 2;

        public bool Valid { get { return _scanned >= Cells; } }

        public void Request(long nowMs) { _requestedAtMs = nowMs; }

        public bool Wanted(long nowMs) { return nowMs - _requestedAtMs < 2000; }

        /// <summary>
        /// One cell's leaf density (0..7) and lowest leaf Y, for a world block
        /// position. False outside the window last scanned.
        /// </summary>
        public bool TryCell(int worldBlockX, int worldBlockZ, out int density, out int lowestY)
        {
            density = 0;
            lowestY = 0;
            if (!Valid) return false;
            int cx = FloorDiv(worldBlockX, CellBlocks) - _windowCx;
            int cz = FloorDiv(worldBlockZ, CellBlocks) - _windowCz;
            if (cx < 0 || cz < 0 || cx >= Grid || cz >= Grid) return false;
            int code = _cells[Index(worldBlockX, worldBlockZ)];
            density = code / 512;
            lowestY = code % 512;
            return true;
        }

        public static int Index(int worldBlockX, int worldBlockZ)
        {
            int cx = FloorDiv(worldBlockX, CellBlocks);
            int cz = FloorDiv(worldBlockZ, CellBlocks);
            return Mod(cz, Grid) * Grid + Mod(cx, Grid);
        }

        /// <summary>The spot inside a 4x4 cell read on pass <paramref name="pass"/>: the four diagonal spots in turn.</summary>
        public static void Spot(int pass, out int ox, out int oz)
        {
            switch (pass & 3)
            {
                case 0: ox = 1; oz = 1; break;
                case 1: ox = 3; oz = 3; break;
                case 2: ox = 3; oz = 1; break;
                default: ox = 1; oz = 3; break;
            }
        }

        /// <summary>
        /// The column's crown, from a top-down list of whether each block is a
        /// leaf (index 0 is SearchUp above the rain map's top). Returns the
        /// leaf count and sets the lowest leaf's index, -1 for none. Stops two
        /// blocks after the crown ends, or BareDepth below the top if no crown
        /// started. Pure, so tools/smoketest can drive it.
        /// </summary>
        public static int ReadColumn(System.Func<int, bool> isLeafAt, int length, out int lowestIndex)
        {
            int leaves = 0, gap = 0;
            lowestIndex = -1;
            for (int i = 0; i < length; i++)
            {
                if (isLeafAt(i))
                {
                    leaves++;
                    lowestIndex = i;
                    gap = 0;
                    continue;
                }
                if (leaves == 0)
                {
                    if (i >= SearchUp + BareDepth) break;
                    continue;
                }
                if (++gap >= 2) break;
            }
            return leaves;
        }

        public static int Encode(int density, int lowestY)
        {
            return GameMath.Clamp(density, 0, 7) * 512 + GameMath.Clamp(lowestY, 0, 511);
        }

        /// <summary>A crown's density from its leaf count: the same scale as <see cref="CanopyMap"/>.</summary>
        public static int Density(int leaves)
        {
            return leaves == 0 ? 0 : Math.Min(7, 1 + leaves * 3 / 2);
        }

        public void Scan(ICoreClientAPI capi, EntityPos player)
        {
            IBlockAccessor blocks = capi?.World?.BlockAccessor;
            if (blocks == null || player == null) return;

            int px = (int)Math.Floor(player.X);
            int pz = (int)Math.Floor(player.Z);
            int baseCx = FloorDiv(px, CellBlocks) - Grid / 2;
            int baseCz = FloorDiv(pz, CellBlocks) - Grid / 2;
            _windowCx = baseCx;
            _windowCz = baseCz;

            var pos = new BlockPos(player.Dimension);

            for (int n = 0; n < ColumnsPerScan; n++)
            {
                int i = _cursor;
                _cursor++;
                if (_cursor >= Cells) { _cursor = 0; _pass++; }

                int cx = baseCx + i % Grid;
                int cz = baseCz + i / Grid;
                Spot(_pass, out int ox, out int oz);
                int x = cx * CellBlocks + ox;
                int z = cz * CellBlocks + oz;

                int code = 0;
                int top = blocks.GetRainMapHeightAt(x, z);
                if (top > 0)
                {
                    int start = top + SearchUp;
                    _scanBlocks = blocks; _scanPos = pos;
                    _scanX = x; _scanZ = z; _scanStart = start;
                    int leaves = ReadColumn(_isLeafAt ??= IsLeafAt, SearchUp + MaxDepth, out int lowestIndex);
                    if (leaves > 0) code = Encode(Density(leaves), start - lowestIndex);
                }

                // The densest of this cell's last four readings, one per spot.
                int slot = Index(cx * CellBlocks, cz * CellBlocks);
                _recent[slot * 4 + (_pass & 3)] = code;
                int best = 0;
                for (int k = 0; k < 4; k++)
                {
                    int c = _recent[slot * 4 + k];
                    if (c / 512 > best / 512) best = c;
                }
                _cells[slot] = best;
                if (_scanned < Cells) _scanned++;
            }
        }

        // The column being read, held in fields so the per-column read needs no new closure.
        private IBlockAccessor _scanBlocks;
        private BlockPos _scanPos;
        private int _scanX, _scanZ, _scanStart;
        private System.Func<int, bool> _isLeafAt;

        private bool IsLeafAt(int k)
        {
            int y = _scanStart - k;
            if (y < 0) return false;
            _scanPos.Set(_scanX, y, _scanZ);
            Block block = _scanBlocks.GetBlock(_scanPos);
            return block != null && block.BlockMaterial == EnumBlockMaterial.Leaves && !IsLitter(block);
        }

        /// <summary>The game's fallen-leaf decor is leaf material on the ground, not a crown.</summary>
        public static bool IsLitter(Block block)
        {
            string path = block?.Code?.Path;
            return path != null && path.StartsWith("fallenleaves", StringComparison.Ordinal);
        }

        public void Reset()
        {
            Array.Clear(_cells, 0, _cells.Length);
            Array.Clear(_recent, 0, _recent.Length);
            _cursor = 0;
            _pass = 0;
            _scanned = 0;
        }

        private static int FloorDiv(int a, int b) { return (int)Math.Floor(a / (double)b); }

        private static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }
    }
}
