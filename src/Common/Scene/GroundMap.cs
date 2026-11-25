using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageVisuals.Common.Scene
{
    /// <summary>
    /// v14. Where the low ground and the water are around the player, for the
    /// ground mist.
    ///
    /// Vanilla's height fog is ONE plane for the whole frame, so a mist can lie
    /// in one hollow at a time - the one around the player. This finds it: a
    /// grid of <see cref="Grid"/> x <see cref="Grid"/> columns
    /// <see cref="Spacing"/> blocks apart, centred on the player, each read
    /// from the game's own terrain height map, or from the water's surface
    /// where the game's rain map ends on a liquid. The floor is the low quarter
    /// of those heights, so the deepest few columns (a river bed, a lake, the
    /// bottom of a dell) set it and a single pit does not.
    ///
    /// World geometry, not a config value, so it belongs with the rest of what
    /// the game says (<see cref="EnvironmentTracker"/>). Scanned a row per tick,
    /// only while a consumer has asked in the last two seconds.
    ///
    /// DATA GAP: the game has no notion of a hollow; this is the terrain's
    /// shape read near the player and nothing more. Whether the terrain height
    /// map counts tree trunks is not known here; the low quarter keeps a few
    /// stray columns from moving the floor either way.
    /// </summary>
    public sealed class GroundMap
    {
        public const int Grid = 9;
        public const int Spacing = 8;
        public const int Columns = Grid * Grid;
        public const int ColumnsPerScan = Grid;

        /// <summary>
        /// How far the floor must move before the held floor follows it, in
        /// blocks, and how fast the held floor then eases. A plane that
        /// tracked every reading would bob the mist as the player walked.
        /// </summary>
        public const double FloorHold = 3.0;
        public const double FloorEase = 0.1;

        private readonly int[] _height = new int[Columns];
        private readonly bool[] _water = new bool[Columns];
        private int _cursor;
        private int _scanned;
        private long _requestedAtMs = long.MinValue / 2;
        private double _floorTarget = double.NaN;
        private double _floor = double.NaN;

        public bool Valid { get { return _scanned >= Columns; } }

        /// <summary>World Y of the low ground around the player, held; 0 until a full scan.</summary>
        public float FloorY { get { return double.IsNaN(_floor) ? 0f : (float)_floor; } }

        /// <summary>Share of the scanned columns that end on water, 0..1.</summary>
        public float WaterShare { get; private set; }

        public void Request(long nowMs) { _requestedAtMs = nowMs; }

        public bool Wanted(long nowMs) { return nowMs - _requestedAtMs < 2000; }

        public void Scan(ICoreClientAPI capi, EntityPos player)
        {
            IBlockAccessor blocks = capi?.World?.BlockAccessor;
            if (blocks == null || player == null) return;

            int px = (int)Math.Floor(player.X);
            int pz = (int)Math.Floor(player.Z);
            var pos = new BlockPos(player.Dimension);

            for (int n = 0; n < ColumnsPerScan; n++)
            {
                int i = _cursor;
                _cursor = (_cursor + 1) % Columns;
                int x = px + (i % Grid - Grid / 2) * Spacing;
                int z = pz + (i / Grid - Grid / 2) * Spacing;

                pos.Set(x, 0, z);
                int terrain = blocks.GetTerrainMapheightAt(pos);
                int rain = blocks.GetRainMapHeightAt(x, z);
                bool water = false;
                if (rain > 0)
                {
                    pos.Set(x, rain, z);
                    Block top = blocks.GetBlock(pos, BlockLayersAccess.Fluid);
                    water = top != null && top.IsLiquid();
                }
                // A column not loaded yet reads 0; leave its old value.
                int h = water ? rain : terrain;
                if (h <= 0) continue;
                _height[i] = h;
                _water[i] = water;
                if (_scanned < Columns) _scanned++;
            }

            if (!Valid) return;
            int wet = 0;
            foreach (bool w in _water) if (w) wet++;
            WaterShare = wet / (float)Columns;
            _floorTarget = CanopyMap.HoldPlane(_floorTarget, LowFloor(_height));
            if (double.IsNaN(_floor)) _floor = _floorTarget;
            else _floor += (_floorTarget - _floor) * FloorEase;
        }

        /// <summary>The low quarter of the heights: the mean of the lowest quarter of the columns. Pure.</summary>
        public static double LowFloor(int[] heights)
        {
            if (heights == null || heights.Length == 0) return double.NaN;
            var sorted = (int[])heights.Clone();
            Array.Sort(sorted);
            int take = Math.Max(1, sorted.Length / 4);
            double sum = 0;
            int count = 0;
            for (int i = 0; i < sorted.Length && count < take; i++)
            {
                if (sorted[i] <= 0) continue;
                sum += sorted[i];
                count++;
            }
            return count == 0 ? double.NaN : sum / count;
        }

        public void Reset()
        {
            Array.Clear(_height, 0, _height.Length);
            Array.Clear(_water, 0, _water.Length);
            _scanned = 0;
            _cursor = 0;
            _floor = double.NaN;
            _floorTarget = double.NaN;
            WaterShare = 0f;
        }
    }
}
