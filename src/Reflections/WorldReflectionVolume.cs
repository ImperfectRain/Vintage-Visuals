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
        public const int SizeX = 64;
        public const int SizeY = 64;
        public const int SizeZ = 64;
        public const int AtlasColumns = 8;
        public const int AtlasRows = 8;
        public const int AtlasWidth = SizeX * AtlasColumns;
        public const int AtlasHeight = SizeY * AtlasRows;

        private const int RebuildThresholdBlocks = 16;

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

            return Math.Abs(playerX - _lastPlayerBlockX) >= RebuildThresholdBlocks ||
                   Math.Abs(playerY - _lastPlayerBlockY) >= RebuildThresholdBlocks ||
                   Math.Abs(playerZ - _lastPlayerBlockZ) >= RebuildThresholdBlocks;
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

            int occupied = 0;
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

                        if (IsSupportedOpaqueCube(block))
                        {
                            _pixels[dest + x] = DiagnosticColor(block.Id);
                            occupied++;
                        }
                        else
                        {
                            _pixels[dest + x] = 0;
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
                (SizeX * SizeY * SizeZ) + ", occupied=" + occupied + ", uploadBytes=" +
                (_pixels.Length * 4) + ", atlas=" + AtlasWidth + "x" + AtlasHeight +
                ", elapsedMs=" + clock.ElapsedMilliseconds + ".");
        }

        private static bool IsSupportedOpaqueCube(Block block)
        {
            return block != null && block.Id != 0 && block.AllSidesOpaque;
        }

        private static int DiagnosticColor(int blockId)
        {
            uint h = (uint)blockId * 747796405u + 2891336453u;
            h = ((h >> ((int)(h >> 28) + 4)) ^ h) * 277803737u;
            h = (h >> 22) ^ h;

            int r = 80 + (int)(h & 127u);
            int g = 80 + (int)((h >> 8) & 127u);
            int b = 80 + (int)((h >> 16) & 127u);
            return r | (g << 8) | (b << 16) | (255 << 24);
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
}
