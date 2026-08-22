using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Owns the GPU copy of the derived material atlas.
    ///
    /// One texture, the same dimensions and layout as the block texture atlas,
    /// so the chunk shader can sample it with the UVs the block geometry
    /// already carries. Nothing about the vertex format changes — which is the
    /// point, because there is no room left in it: VertexFlags is fully packed
    /// (glow 0-7, zoffset 8-10, reflective 11, lod0 12, normal 13-24, wind
    /// 25-31), so a per-face material id has nowhere to live.
    ///
    /// The pixels and the GL texture have deliberately separate lifetimes. The
    /// atlas is derived during asset load, on whatever thread and at whatever
    /// moment the game happens to raise that event; the upload happens later,
    /// on the render thread, at a render stage this mod chose. Creating a
    /// texture binds it to the active texture unit as a side effect, so doing
    /// that at an arbitrary point during startup means clobbering whatever the
    /// game had bound there — the class of fault that has already cost this
    /// subsystem two rounds of debugging.
    /// </summary>
    public sealed class MaterialAtlasTexture : IDisposable
    {
        /// <summary>
        /// Texture unit the material atlas is bound to.
        ///
        /// 15 is the top of the range OpenGL 3.3 guarantees per stage
        /// (GL_MAX_TEXTURE_IMAGE_UNITS is required to be at least 16, so 0..15
        /// are always valid). Vanilla chunkopaque.fsh declares seven samplers,
        /// so taking the top rather than "one past vanilla" leaves eight units
        /// of headroom above an allocation that grows upward from zero.
        ///
        /// Note this is separate from, and does not fix, the sampler
        /// DECLARATION order problem — see pseudopbr.glsl. Both matter.
        /// </summary>
        public const int TextureUnit = 15;

        /// <summary>
        /// Texture unit the second material atlas is bound to.
        ///
        /// 14, immediately below the first. Vanilla chunkopaque.fsh declares
        /// seven samplers, so units 0..6 are the game's and 7..13 remain free
        /// above them - this pair takes the top of the guaranteed range and
        /// grows downward, so the two allocations move away from each other
        /// rather than toward a collision.
        ///
        /// The unit is also set explicitly at bind time rather than left to the
        /// linker, which is the half that actually matters: link-time
        /// assignment is what pushed liquidDepth off the end and turned the
        /// world to water murk the last time a sampler was added here.
        /// </summary>
        public const int SecondTextureUnit = 14;

        private LoadedTexture _texture;

        private int[] _pendingPixels;
        private int _pendingWidth;
        private int _pendingHeight;
        private bool _uploadFailed;

        /// <summary>0 when nothing has been uploaded, which is also GL's "no texture".</summary>
        public int TextureId
        {
            get { return _texture == null ? 0 : _texture.TextureId; }
        }

        public bool IsUploaded
        {
            get { return TextureId != 0; }
        }

        /// <summary>True once pixels exist, whether or not they have reached the GPU.</summary>
        public bool HasPixels
        {
            get { return _pendingPixels != null; }
        }

        /// <summary>
        /// Hands over the derived pixels. Pure CPU work — safe to call from an
        /// asset-load event, and deliberately does no GL at all.
        /// </summary>
        public void SetPending(int width, int height, int[] rgbaPixels)
        {
            _pendingWidth = width;
            _pendingHeight = height;
            _pendingPixels = rgbaPixels;
            _uploadFailed = false;
        }

        /// <summary>
        /// Uploads the pending pixels if they are not on the GPU yet. Must be
        /// called from the render thread. Returns whether the texture is usable.
        ///
        /// Retries are not attempted: a failed upload latches, so a driver
        /// problem produces one log line rather than one per frame.
        /// </summary>
        public bool EnsureUploaded(ICoreClientAPI capi, ILogger logger)
        {
            if (IsUploaded) return true;
            if (_uploadFailed || _pendingPixels == null) return false;

            _uploadFailed = true;

            if (_pendingWidth <= 0 || _pendingHeight <= 0 ||
                _pendingPixels.Length != _pendingWidth * _pendingHeight)
            {
                logger.Warning("[VintageVisuals] pseudopbr: material atlas is " + _pendingWidth + "x" +
                    _pendingHeight + " but carries " + _pendingPixels.Length + " pixels; not uploading. " +
                    "Surface relief stays off and the world renders as vanilla.");
                return false;
            }

            try
            {
                if (_texture == null) _texture = new LoadedTexture(capi);

                // LoadOrUpdateTextureFromRgba reads the target's dimensions to
                // decide whether it can reuse the existing GL texture, so these
                // have to be set before the call, not after it.
                _texture.Width = _pendingWidth;
                _texture.Height = _pendingHeight;

                // linearMag: true. Magnified normals want to be smooth — nearest
                // sampling gives relief hard stair-stepped edges that read as a
                // low-resolution bump map rather than as surface.
                //
                // No mipmaps, which is the cost of that choice: at distance the
                // normals alias. Acceptable for now because the effect fades
                // into fog and vanilla lighting long before it dominates.
                capi.Render.LoadOrUpdateTextureFromRgba(_pendingPixels, true, 0, ref _texture);
            }
            catch (Exception ex)
            {
                logger.Error("[VintageVisuals] pseudopbr: could not upload the material atlas to the GPU. " +
                             "Surface relief is inactive; everything else is unaffected.");
                logger.LogException(EnumLogType.Error, ex);
                return false;
            }

            if (!IsUploaded)
            {
                logger.Warning("[VintageVisuals] pseudopbr: material atlas upload returned texture id 0; " +
                               "surface relief is inactive.");
                return false;
            }

            _uploadFailed = false;
            logger.Notification("[VintageVisuals] pseudopbr: material atlas uploaded as texture " + TextureId +
                                " (" + _pendingWidth + "x" + _pendingHeight + "), nearest filtering, bound at unit " +
                                TextureUnit + ".");
            return true;
        }

        /// <summary>
        /// Frees the GL texture but keeps the pixels, so the subsystem can be
        /// switched off without throwing away minutes of derivation work and
        /// switched back on without rebuilding.
        ///
        /// Must be called from the render thread. Releasing matters: while the
        /// texture exists it stays bound to its unit, and "off" has to mean
        /// this mod holds no shared GL state at all.
        /// </summary>
        public void Release()
        {
            if (_texture == null) return;

            _texture.Dispose();
            _texture = null;
            _uploadFailed = false;
        }

        public void Dispose()
        {
            Release();
            _pendingPixels = null;
        }
    }
}
