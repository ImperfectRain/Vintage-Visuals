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

        /// <summary>Columns scanned per call to <see cref="Scan"/>. A full refresh takes Cells / this calls.</summary>
        public const int ColumnsPerScan = 128;

        /// <summary>Blocks below and above the player searched for leaves.</summary>
        public const int SearchBelow = 12;
        public const int SearchAbove = 40;

        private readonly float[] _packed = new float[PackedFloats];
        private readonly int[] _cells = new int[Cells];
        private int _cursor;
        private int _scanned;
        private long _requestedAtMs = long.MinValue;

        /// <summary>The packed uniform array, <see cref="PackedVectors"/> vec4s.</summary>
        public float[] Packed { get { return _packed; } }

        /// <summary>True once every cell has been scanned at least once.</summary>
        public bool Valid { get { return _scanned >= Cells; } }

        /// <summary>Bumped whenever <see cref="Packed"/> changes.</summary>
        public int Version { get; private set; }

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
        /// not free, about 50 per column.
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
                // One column per cell, alternating corner by cell parity so a
                // two-block tree trunk gap is not always missed the same way.
                int x = cx * CellBlocks + (cz & 1);
                int z = cz * CellBlocks + (cx & 1);

                int leaves = 0;
                int lowest = -1;
                for (int y = py - SearchBelow; y <= py + SearchAbove; y++)
                {
                    if (y < 0) continue;
                    pos.Set(x, y, z);
                    Block block = blocks.GetBlock(pos);
                    if (block != null && block.BlockMaterial == EnumBlockMaterial.Leaves)
                    {
                        leaves++;
                        if (lowest < 0) lowest = y;
                    }
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
            }
        }

        public void Reset()
        {
            Array.Clear(_cells, 0, _cells.Length);
            Array.Clear(_packed, 0, _packed.Length);
            _cursor = 0;
            _scanned = 0;
            Version++;
        }

        private static int FloorDiv(int a, int b) { return (int)Math.Floor(a / (double)b); }

        private static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }
    }
}
