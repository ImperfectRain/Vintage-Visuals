using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// The derived material atlas, one page per page of the game's block
    /// texture atlas.
    ///
    /// Vintage Story spills block textures onto a second atlas page once they
    /// no longer fit on one, and a modded install reaches that easily - the
    /// install this was built against needs two pages for 3749 textures. Since
    /// the chunk shader samples our atlas with the diffuse's own uv, and that
    /// uv says nothing about which page a fragment came from, one derived atlas
    /// per page is the only arrangement that can be correct.
    ///
    /// Which page to bind is decided per draw call by
    /// <see cref="TerrainTextureBindInterceptor"/>, because that is the only
    /// moment the answer exists.
    /// </summary>
    public sealed class MaterialAtlasSet : IDisposable
    {
        private readonly List<MaterialAtlasTexture> _pages = new List<MaterialAtlasTexture>();

        public int PageCount
        {
            get { return _pages.Count; }
        }

        /// <summary>True when every page has been derived and is waiting to upload.</summary>
        public bool HasPixels
        {
            get
            {
                if (_pages.Count == 0) return false;

                foreach (MaterialAtlasTexture page in _pages)
                {
                    if (!page.HasPixels) return false;
                }

                return true;
            }
        }

        public void SetPending(int page, int width, int height, int[] rgbaPixels)
        {
            while (_pages.Count <= page) _pages.Add(new MaterialAtlasTexture());
            _pages[page].SetPending(width, height, rgbaPixels);
        }

        /// <summary>
        /// Uploads every page that is not on the GPU yet. Render thread only.
        /// Returns false unless all of them are usable — a partially uploaded
        /// set would render some pages with another page's material data, which
        /// is worse than rendering none.
        /// </summary>
        public bool EnsureUploaded(ICoreClientAPI capi, ILogger logger)
        {
            if (_pages.Count == 0) return false;

            bool complete = true;
            foreach (MaterialAtlasTexture page in _pages)
            {
                if (!page.EnsureUploaded(capi, logger)) complete = false;
            }

            return complete;
        }

        public int TextureIdFor(int page)
        {
            return page >= 0 && page < _pages.Count ? _pages[page].TextureId : 0;
        }

        /// <summary>Frees the GPU copies, keeping the derived pixels.</summary>
        public void Release()
        {
            foreach (MaterialAtlasTexture page in _pages) page.Release();
        }

        public bool AnyUploaded
        {
            get
            {
                foreach (MaterialAtlasTexture page in _pages)
                {
                    if (page.IsUploaded) return true;
                }

                return false;
            }
        }

        public void Dispose()
        {
            foreach (MaterialAtlasTexture page in _pages) page.Dispose();
            _pages.Clear();
        }
    }
}
