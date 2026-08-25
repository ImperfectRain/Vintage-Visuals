using System;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageVisuals.Reflections
{
    /// <summary>
    /// Debug-only nearby block volume for proving world-space reflection rays.
    ///
    /// This is deliberately a 2D slice atlas rather than a sampler3D. The public
    /// render API already has a stable LoadOrUpdateTextureFromRgba path for 2D
    /// textures, and the terrain binder already knows how to keep 2D samplers
    /// rebound per chunk draw.
    /// </summary>
    public sealed class WorldReflectionVolume : IDisposable
    {
        public const int TextureUnit = 12;
        public const int SizeX = 128;
        public const int SizeY = 64;
        public const int SizeZ = 128;
        public const int AtlasColumns = 16;
        public const int AtlasRows = (SizeZ + AtlasColumns - 1) / AtlasColumns;
        public const int AtlasWidth = SizeX * AtlasColumns;
        public const int AtlasHeight = SizeY * AtlasRows;

        private const int RebuildThresholdXzBlocks = 32;
        private const int RebuildThresholdYBlocks = 16;

        private LoadedTexture _texture;
        private int[] _pixels;
        private bool _dirty = true;
        private bool _uploadFailed;
        private bool _reportedUnsupportedColor;

        private int _originX;
        private int _originY;
        private int _originZ;
        private int _lastPlayerBlockX = int.MinValue;
        private int _lastPlayerBlockY = int.MinValue;
        private int _lastPlayerBlockZ = int.MinValue;

        public int TextureId
        {
            get { return _texture == null ? 0 : _texture.TextureId; }
        }

        public Vec3f Size
        {
            get { return new Vec3f(SizeX, SizeY, SizeZ); }
        }

        public Vec2f AtlasSize
        {
            get { return new Vec2f(AtlasWidth, AtlasHeight); }
        }

        public Vec2f SliceSize
        {
            get { return new Vec2f(SizeX, SizeY); }
        }

        public Vec2f AtlasGrid
        {
            get { return new Vec2f(AtlasColumns, AtlasRows); }
        }

        public Vec3f OriginRelativeToPlayer(EntityPos player)
        {
            if (player == null) return new Vec3f();
            return new Vec3f((float)(_originX - player.X),
                             (float)(_originY - player.Y),
                             (float)(_originZ - player.Z));
        }

        /// <summary>
        /// Rebuilds the CPU volume when the player has moved far enough and
        /// uploads the atlas when needed. Must run on the render thread because
        /// the upload path creates or mutates a GL texture.
        /// </summary>
        public bool EnsureUploaded(ICoreClientAPI capi, ILogger logger, EntityPos player)
        {
            if (capi == null || player == null) return false;

            int playerX = Floor(player.X);
            int playerY = Floor(player.Y);
            int playerZ = Floor(player.Z);

            if (NeedsRebuild(playerX, playerY, playerZ))
            {
                Rebuild(capi, logger, player.Dimension, playerX, playerY, playerZ);
            }

            if (!_dirty && TextureId != 0) return true;
            if (_uploadFailed || _pixels == null) return false;

            _uploadFailed = true;

            try
            {
                if (_texture == null) _texture = new LoadedTexture(capi);

                _texture.Width = AtlasWidth;
                _texture.Height = AtlasHeight;
                capi.Render.LoadOrUpdateTextureFromRgba(_pixels, false, 0, ref _texture);
            }
            catch (Exception ex)
            {
                logger.Error("[VintageVisuals] reflections: world reflection volume upload failed. " +
                             "The debug-only world DDA views are inactive; normal rendering is unaffected.");
                logger.LogException(EnumLogType.Error, ex);
                return false;
            }

            if (TextureId == 0)
            {
                logger.Warning("[VintageVisuals] reflections: world reflection volume upload returned texture id 0. " +
                               "The debug-only world DDA views are inactive.");
                return false;
            }

            _dirty = false;
            _uploadFailed = false;
            return true;
        }

        private bool NeedsRebuild(int playerX, int playerY, int playerZ)
        {
            if (_pixels == null) return true;

            return Math.Abs(playerX - _lastPlayerBlockX) >= RebuildThresholdXzBlocks ||
                   Math.Abs(playerY - _lastPlayerBlockY) >= RebuildThresholdYBlocks ||
                   Math.Abs(playerZ - _lastPlayerBlockZ) >= RebuildThresholdXzBlocks;
        }

        private void Rebuild(ICoreClientAPI capi, ILogger logger, int dimension,
                             int playerX, int playerY, int playerZ)
        {
            Stopwatch clock = Stopwatch.StartNew();

            _originX = playerX - SizeX / 2;
            _originY = playerY - SizeY / 2;
            _originZ = playerZ - SizeZ / 2;
            _lastPlayerBlockX = playerX;
            _lastPlayerBlockY = playerY;
            _lastPlayerBlockZ = playerZ;

            if (_pixels == null || _pixels.Length != AtlasWidth * AtlasHeight)
            {
                _pixels = new int[AtlasWidth * AtlasHeight];
            }

            var counts = new int[8];
            var pos = new BlockPos(dimension);

            for (int z = 0; z < SizeZ; z++)
            {
                int sliceX = (z % AtlasColumns) * SizeX;
                int sliceY = (z / AtlasColumns) * SizeY;

                for (int y = 0; y < SizeY; y++)
                {
                    int dest = (sliceY + y) * AtlasWidth + sliceX;

                    for (int x = 0; x < SizeX; x++)
                    {
                        pos.Set(_originX + x, _originY + y, _originZ + z);
                        Block block = capi.World.BlockAccessor.GetBlock(pos);
                        WorldVoxelClass cls = Classify(block);
                        counts[(int)cls]++;

                        if (cls == WorldVoxelClass.FullOpaqueCube)
                        {
                            _pixels[dest + x] = Pack(DiagnosticColor(block.Id), cls);
                        }
                        else
                        {
                            _pixels[dest + x] = cls == WorldVoxelClass.Empty ? 0 : Pack(ClassColor(cls), cls);
                        }
                    }
                }
            }

            clock.Stop();
            _dirty = true;
            _uploadFailed = false;

            if (!_reportedUnsupportedColor)
            {
                _reportedUnsupportedColor = true;
                logger.Notification("[VintageVisuals] reflections: world reflection proof uses deterministic " +
                    "diagnostic colours keyed by block id. It does not yet sample vanilla block albedo.");
            }

            logger.Notification("[VintageVisuals] reflections: rebuilt world reflection volume origin=(" +
                _originX + "," + _originY + "," + _originZ + "), cells=" +
                (SizeX * SizeY * SizeZ) + ", fullOpaque=" + counts[(int)WorldVoxelClass.FullOpaqueCube] +
                ", partial=" + counts[(int)WorldVoxelClass.PartialSolid] +
                ", foliage=" + counts[(int)WorldVoxelClass.CutoutFoliage] +
                ", transparent=" + counts[(int)WorldVoxelClass.Transparent] +
                ", liquid=" + counts[(int)WorldVoxelClass.Liquid] +
                ", emissive=" + counts[(int)WorldVoxelClass.Emissive] +
                ", unsupported=" + counts[(int)WorldVoxelClass.UnsupportedComplex] +
                ", uploadBytes=" +
                (_pixels.Length * 4) + ", atlas=" + AtlasWidth + "x" + AtlasHeight +
                ", elapsedMs=" + clock.ElapsedMilliseconds + ".");
        }

        private static WorldVoxelClass Classify(Block block)
        {
            if (block == null || block.Id == 0 || block.BlockMaterial == EnumBlockMaterial.Air)
                return WorldVoxelClass.Empty;

            switch (block.BlockMaterial)
            {
                case EnumBlockMaterial.Water:
                    return WorldVoxelClass.Liquid;
                case EnumBlockMaterial.Lava:
                case EnumBlockMaterial.Fire:
                    return WorldVoxelClass.Emissive;
                case EnumBlockMaterial.Leaves:
                case EnumBlockMaterial.Plant:
                    return WorldVoxelClass.CutoutFoliage;
                case EnumBlockMaterial.Glass:
                case EnumBlockMaterial.Ice:
                    return WorldVoxelClass.Transparent;
            }

            if (block.AllSidesOpaque) return WorldVoxelClass.FullOpaqueCube;
            if (block.BlockMaterial == EnumBlockMaterial.Meta ||
                block.BlockMaterial == EnumBlockMaterial.Other)
                return WorldVoxelClass.UnsupportedComplex;

            return WorldVoxelClass.PartialSolid;
        }

        private static int DiagnosticColor(int blockId)
        {
            uint h = (uint)blockId * 747796405u + 2891336453u;
            h = ((h >> ((int)(h >> 28) + 4)) ^ h) * 277803737u;
            h = (h >> 22) ^ h;

            int r = 80 + (int)(h & 127u);
            int g = 80 + (int)((h >> 8) & 127u);
            int b = 80 + (int)((h >> 16) & 127u);
            return r | (g << 8) | (b << 16);
        }

        private static int ClassColor(WorldVoxelClass cls)
        {
            switch (cls)
            {
                case WorldVoxelClass.PartialSolid: return PackRgb(220, 180, 80);
                case WorldVoxelClass.CutoutFoliage: return PackRgb(70, 210, 70);
                case WorldVoxelClass.Transparent: return PackRgb(90, 180, 230);
                case WorldVoxelClass.Liquid: return PackRgb(40, 90, 220);
                case WorldVoxelClass.Emissive: return PackRgb(255, 120, 40);
                case WorldVoxelClass.UnsupportedComplex: return PackRgb(210, 70, 210);
                default: return 0;
            }
        }

        private static int PackRgb(int r, int g, int b)
        {
            return r | (g << 8) | (b << 16);
        }

        private static int Pack(int rgb, WorldVoxelClass cls)
        {
            int alpha = Math.Min(255, Math.Max(0, (int)cls * 32));
            return (rgb & 0x00ffffff) | (alpha << 24);
        }

        private static int Floor(double value)
        {
            return (int)Math.Floor(value);
        }

        public void Release()
        {
            if (_texture == null) return;

            _texture.Dispose();
            _texture = null;
            _uploadFailed = false;
            _dirty = true;
        }

        public void Dispose()
        {
            Release();
            _pixels = null;
        }
    }

    public enum WorldVoxelClass
    {
        Empty = 0,
        FullOpaqueCube = 1,
        PartialSolid = 2,
        CutoutFoliage = 3,
        Transparent = 4,
        Liquid = 5,
        Emissive = 6,
        UnsupportedComplex = 7,
    }
}
