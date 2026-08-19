using System;
using Vintagestory.API.Client;

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
    /// </summary>
    public sealed class MaterialAtlasTexture : IDisposable
    {
        /// <summary>
        /// Texture unit the material atlas is bound to.
        ///
        /// This was 6, which was a guess, and the guess was wrong in the worst
        /// available way. Counting the samplers vanilla chunkopaque.fsh
        /// actually declares gives SEVEN — terrainTex, terrainTexLinear,
        /// shadowMapFar, shadowMapNear, glow, sky, liquidDepth — so units 0..6
        /// are all spoken for and binding here overwrote one of the game's own.
        /// The symptom was not a broken effect but a broken world: a clobbered
        /// liquidDepth drives getUnderwaterMurkiness() to 1 everywhere, and
        /// applyUnderwaterEffects then mixes the entire frame to the water murk
        /// colour.
        ///
        /// 15 is the top of the range OpenGL 3.3 guarantees per stage
        /// (GL_MAX_TEXTURE_IMAGE_UNITS is required to be at least 16, so 0..15
        /// are always valid). Taking the top rather than "one past vanilla"
        /// puts eight units of headroom between this and an allocation that
        /// grows upward from zero.
        ///
        /// Unit bindings are still global GL state, so this remains a
        /// convention rather than a reservation — the binder re-binds every
        /// frame instead of trusting it to survive, and binds nothing at all
        /// when the subsystem is switched off.
        /// </summary>
        public const int TextureUnit = 15;

        private LoadedTexture _texture;

        /// <summary>0 when nothing has been uploaded, which is also GL's "no texture".</summary>
        public int TextureId
        {
            get { return _texture == null ? 0 : _texture.TextureId; }
        }

        public bool IsUploaded
        {
            get { return TextureId != 0; }
        }

        /// <summary>
        /// Uploads (or re-uploads) the atlas. Returns false rather than
        /// throwing: losing the material atlas must cost the effect, not the
        /// session.
        /// </summary>
        public bool Upload(ICoreClientAPI capi, int width, int height, int[] rgbaPixels, PbrDiagnostics diagnostics)
        {
            if (capi == null || rgbaPixels == null) return false;

            if (width <= 0 || height <= 0 || rgbaPixels.Length != width * height)
            {
                diagnostics.Warn("material atlas is " + width + "x" + height + " but carries " +
                                 (rgbaPixels == null ? 0 : rgbaPixels.Length) + " pixels; not uploading.");
                return false;
            }

            try
            {
                if (_texture == null) _texture = new LoadedTexture(capi);

                // LoadOrUpdateTextureFromRgba reads the target's dimensions to
                // decide whether it can reuse the existing GL texture, so these
                // have to be set before the call, not after it.
                _texture.Width = width;
                _texture.Height = height;

                // linearMag: true. Magnified normals want to be smooth — nearest
                // sampling would give the relief hard stair-stepped edges that
                // read as a low-resolution bump map rather than as surface.
                //
                // No mipmaps, which is the cost of that choice: at distance the
                // normals alias. Acceptable for now because the effect fades
                // into fog and vanilla lighting long before it dominates; the
                // fix, if it turns out to matter, is a mipped upload rather
                // than nearest sampling.
                capi.Render.LoadOrUpdateTextureFromRgba(rgbaPixels, true, 0, ref _texture);
            }
            catch (Exception ex)
            {
                diagnostics.Error("could not upload the material atlas to the GPU. Surface relief will be " +
                                  "inactive; everything else is unaffected.", ex);
                return false;
            }

            if (!IsUploaded)
            {
                diagnostics.Warn("material atlas upload returned texture id 0; surface relief is inactive.");
                return false;
            }

            diagnostics.Note("material atlas uploaded to the GPU as texture " + TextureId +
                             " (" + width + "x" + height + "), bound at unit " + TextureUnit + ".");
            return true;
        }

        public void Dispose()
        {
            if (_texture != null)
            {
                _texture.Dispose();
                _texture = null;
            }
        }
    }
}
