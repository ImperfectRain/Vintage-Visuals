using System;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageVisuals.Reflections
{
    /// <summary>
    /// Nearby classified block volume for world-space reflection rays.
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
        public const int CanopyTextureUnit = 11;
        public const int CanopyWidth = SizeX;
        public const int CanopyHeight = SizeZ;

        private const int RebuildThresholdXzBlocks = 32;
        private const int RebuildThresholdYBlocks = 16;

        private LoadedTexture _texture;
        private LoadedTexture _canopyTexture;
        private int[] _pixels;
        private int[] _canopyPixels;
        private bool _dirty = true;
        private bool _volumeDirty = true;
        private bool _uploadFailed;
        private bool _reportedUnsupportedColor;
        private int _pendingInvalidations;
        private string _lastInvalidationReason = "initial build";

        private int _originX;
        private int _originY;
        private int _originZ;
        private int _currentDimension;
        private int _lastPlayerBlockX = int.MinValue;
        private int _lastPlayerBlockY = int.MinValue;
        private int _lastPlayerBlockZ = int.MinValue;

        public int TextureId
        {
            get { return _texture == null ? 0 : _texture.TextureId; }
        }

        public int CanopyTextureId
        {
            get { return _canopyTexture == null ? 0 : _canopyTexture.TextureId; }
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

            string rebuildReason = RebuildReason(playerX, playerY, playerZ);
            if (rebuildReason != null)
            {
                Rebuild(capi, logger, player.Dimension, playerX, playerY, playerZ, rebuildReason);
            }

            if (!_dirty && TextureId != 0 && CanopyTextureId != 0) return true;
            if (_uploadFailed || _pixels == null) return false;

            _uploadFailed = true;

            try
            {
                if (_texture == null) _texture = new LoadedTexture(capi);
                if (_canopyTexture == null) _canopyTexture = new LoadedTexture(capi);

                _texture.Width = AtlasWidth;
                _texture.Height = AtlasHeight;
                capi.Render.LoadOrUpdateTextureFromRgba(_pixels, false, 0, ref _texture);

                _canopyTexture.Width = CanopyWidth;
                _canopyTexture.Height = CanopyHeight;
                capi.Render.LoadOrUpdateTextureFromRgba(_canopyPixels, false, 0, ref _canopyTexture);
            }
            catch (Exception ex)
            {
                logger.Error("[VintageVisuals] reflections: world reflection volume upload failed. " +
                             "World-volume reflection fallback and its debug views are inactive.");
                logger.LogException(EnumLogType.Error, ex);
                return false;
            }

            if (TextureId == 0 || CanopyTextureId == 0)
            {
                logger.Warning("[VintageVisuals] reflections: world volume upload returned texture id 0. " +
                               "World-volume reflection and canopy context debug views are inactive.");
                return false;
            }

            _dirty = false;
            _uploadFailed = false;
            return true;
        }

        public void MarkBlockDirty(BlockPos pos, Block oldBlock)
        {
            if (pos == null || _pixels == null) return;
            if (pos.dimension != _currentDimension) return;

            if (pos.X < _originX || pos.X >= _originX + SizeX ||
                pos.Y < _originY || pos.Y >= _originY + SizeY ||
                pos.Z < _originZ || pos.Z >= _originZ + SizeZ)
            {
                return;
            }

            MarkDirty("block changed at " + pos.X + "," + pos.Y + "," + pos.Z);
        }

        public void MarkChunkDirty(Vec3i chunkCoord, EnumChunkDirtyReason reason)
        {
            if (chunkCoord == null || _pixels == null) return;

            int chunkSize = Vintagestory.API.Config.GlobalConstants.ChunkSize;
            int minX = chunkCoord.X * chunkSize;
            int minY = chunkCoord.Y * chunkSize;
            int minZ = chunkCoord.Z * chunkSize;
            int maxX = minX + chunkSize;
            int maxY = minY + chunkSize;
            int maxZ = minZ + chunkSize;

            if (maxX <= _originX || minX >= _originX + SizeX ||
                maxY <= _originY || minY >= _originY + SizeY ||
                maxZ <= _originZ || minZ >= _originZ + SizeZ)
            {
                return;
            }

            MarkDirty("chunk dirty " + chunkCoord.X + "," + chunkCoord.Y + "," + chunkCoord.Z +
                      " (" + reason + ")");
        }

        private void MarkDirty(string reason)
        {
            _volumeDirty = true;
            _dirty = true;
            _pendingInvalidations++;
            _lastInvalidationReason = reason;
        }

        private string RebuildReason(int playerX, int playerY, int playerZ)
        {
            if (_pixels == null) return "initial upload";
            if (_volumeDirty) return _lastInvalidationReason;

            if (Math.Abs(playerX - _lastPlayerBlockX) >= RebuildThresholdXzBlocks ||
                Math.Abs(playerZ - _lastPlayerBlockZ) >= RebuildThresholdXzBlocks)
            {
                return "player moved " +
                    Math.Abs(playerX - _lastPlayerBlockX) + "," +
                    Math.Abs(playerZ - _lastPlayerBlockZ) + " xz blocks";
            }

            if (Math.Abs(playerY - _lastPlayerBlockY) >= RebuildThresholdYBlocks)
            {
                return "player moved " + Math.Abs(playerY - _lastPlayerBlockY) + " y blocks";
            }

            return null;
        }

        private void Rebuild(ICoreClientAPI capi, ILogger logger, int dimension,
                             int playerX, int playerY, int playerZ, string rebuildReason)
        {
            Stopwatch clock = Stopwatch.StartNew();

            _originX = playerX - SizeX / 2;
            _originY = playerY - SizeY / 2;
            _originZ = playerZ - SizeZ / 2;
            _currentDimension = dimension;
            _lastPlayerBlockX = playerX;
            _lastPlayerBlockY = playerY;
            _lastPlayerBlockZ = playerZ;

            if (_pixels == null || _pixels.Length != AtlasWidth * AtlasHeight)
            {
                _pixels = new int[AtlasWidth * AtlasHeight];
            }
            if (_canopyPixels == null || _canopyPixels.Length != CanopyWidth * CanopyHeight)
            {
                _canopyPixels = new int[CanopyWidth * CanopyHeight];
            }

            var counts = new int[8];
            var canopyCounts = new int[SizeX * SizeZ];
            var canopyR = new int[SizeX * SizeZ];
            var canopyG = new int[SizeX * SizeZ];
            var canopyB = new int[SizeX * SizeZ];
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
                            _pixels[dest + x] = Pack(RepresentativeColor(capi, block, pos), cls);
                        }
                        else if (cls == WorldVoxelClass.CutoutFoliage)
                        {
                            int color = RepresentativeColor(capi, block, pos);
                            _pixels[dest + x] = Pack(color, cls);

                            if (block.BlockMaterial == EnumBlockMaterial.Leaves)
                            {
                                int column = z * SizeX + x;
                                canopyCounts[column]++;
                                canopyR[column] += ColorUtil.ColorR(color);
                                canopyG[column] += ColorUtil.ColorG(color);
                                canopyB[column] += ColorUtil.ColorB(color);
                            }
                        }
                        else
                        {
                            _pixels[dest + x] = cls == WorldVoxelClass.Empty ? 0 : Pack(ClassColor(cls), cls);
                        }
                    }
                }
            }

            for (int z = 0; z < SizeZ; z++)
            {
                int row = z * CanopyWidth;
                for (int x = 0; x < SizeX; x++)
                {
                    int column = z * SizeX + x;
                    int count = canopyCounts[column];
                    if (count <= 0)
                    {
                        _canopyPixels[row + x] = 0;
                        continue;
                    }

                    int r = canopyR[column] / count;
                    int g = canopyG[column] / count;
                    int b = canopyB[column] / count;
                    int density = Math.Min(255, Math.Max(0, (int)(255f * Math.Min(1f, count / 18f) + 0.5f)));
                    _canopyPixels[row + x] = PackRgb(r, g, b) | (density << 24);
                }
            }

            clock.Stop();
            _dirty = true;
            _volumeDirty = false;
            _uploadFailed = false;

            if (!_reportedUnsupportedColor)
            {
                _reportedUnsupportedColor = true;
                logger.Notification("[VintageVisuals] reflections: world reflection proof uses deterministic " +
                    "diagnostic colours only as a fallback. Full cube cells use the block's game-reported " +
                    "colour multiplied by the local light RGB.");
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
                ", reason=" + rebuildReason +
                ", invalidations=" + _pendingInvalidations +
                ", uploadBytes=" +
                ((_pixels.Length + _canopyPixels.Length) * 4) + ", atlas=" + AtlasWidth + "x" + AtlasHeight +
                ", canopy=" + CanopyWidth + "x" + CanopyHeight +
                ", elapsedMs=" + clock.ElapsedMilliseconds + ".");

            _pendingInvalidations = 0;
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

        private static int RepresentativeColor(ICoreClientAPI capi, Block block, BlockPos pos)
        {
            if (capi == null || block == null) return 0;

            int color;
            try
            {
                color = block.GetColor(capi, pos);
            }
            catch
            {
                try { color = block.GetColorWithoutTint(capi, pos); }
                catch { color = DiagnosticColor(block.Id); }
            }

            int r = ColorUtil.ColorR(color);
            int g = ColorUtil.ColorG(color);
            int b = ColorUtil.ColorB(color);

            Vec4f light = capi.World.BlockAccessor.GetLightRGBs(pos);
            r = LightChannel(r, light == null ? 1f : light.X);
            g = LightChannel(g, light == null ? 1f : light.Y);
            b = LightChannel(b, light == null ? 1f : light.Z);

            return PackRgb(r, g, b);
        }

        private static int LightChannel(int channel, float light)
        {
            if (float.IsNaN(light) || float.IsInfinity(light)) light = 1f;
            if (light > 1.5f) light /= 255f;
            light = GameMath.Clamp(light, 0f, 1f);
            return GameMath.Clamp((int)(channel * light + 0.5f), 0, 255);
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
            if (_texture != null)
            {
                _texture.Dispose();
                _texture = null;
            }

            if (_canopyTexture != null)
            {
                _canopyTexture.Dispose();
                _canopyTexture = null;
            }
            _uploadFailed = false;
            _dirty = true;
        }

        public void Dispose()
        {
            Release();
            _pixels = null;
            _canopyPixels = null;
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
