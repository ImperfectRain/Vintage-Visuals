using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VintageVisuals.Common;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Phase 4 subsystem: works out what material every block is made of,
    /// derives a material atlas from the block textures, and feeds that atlas
    /// to the chunk shader so surfaces catch light according to what they are.
    ///
    /// Landing it in stages was deliberate, and each stage produced something
    /// inspectable on its own: the material report caught a mis-tuned Ceramic
    /// profile covering 14% of the world before any pixel was drawn, and the
    /// preview images answered "do the grooves read" before a shader existed
    /// to render them.
    ///
    /// What reaches the screen today is surface relief only — the derived
    /// normals replace the flat per-face block normal in vanilla's own
    /// lighting call. Roughness and specular are in the atlas and are not yet
    /// read by any shader; they need a light direction chunkopaque.fsh does
    /// not currently hand us.
    /// </summary>
    public sealed class PseudoPbrSubsystem : IVisualSubsystem
    {
        public const string GroupName = "pseudopbr";

        /// <summary>Where the cache and preview images live, under VintagestoryData.</summary>
        public const string DataDirectory = "VintageVisuals";

        private VintageVisualsModSystem _mod;
        private bool _reportWritten;
        private bool _atlasBuilt;

        private MaterialAtlasTexture _atlasTexture;
        private PbrShaderBinder _binder;

        /// <summary>
        /// False when the block atlas needed more than one page. See
        /// <see cref="CheckSinglePageAtlas"/> for why that switches the shader
        /// half off rather than rendering something wrong.
        /// </summary>
        private bool _atlasIsSinglePage;

        public string Name => GroupName;

        public void Initialize(VintageVisualsModSystem mod)
        {
            _mod = mod;

            _atlasTexture = new MaterialAtlasTexture();
            _binder = new PbrShaderBinder(mod.Capi, _atlasTexture);

            // Registered up front and left registered. It is a no-op until the
            // atlas is uploaded, and registering it here rather than at upload
            // time keeps renderer lifetime tied to subsystem lifetime, which is
            // the only pairing Dispose() can honour.
            mod.Capi.Event.RegisterRenderer(_binder, EnumRenderStage.Opaque, "vintagevisuals-pbr");
        }

        public void Apply()
        {
            if (_mod == null || _mod.Capi == null) return;

            PseudoPbrConfig config = _mod.ConfigManager.Config.PseudoPBR;

            WriteReportOnce(config);
            BuildAtlasOnce(config);
            UpdateShaderState(config);
        }

        /// <summary>
        /// Pushes the current config to the renderer. Cheap and idempotent, so
        /// it runs on every Apply rather than trying to detect changes.
        ///
        /// Three things must all hold before the shader is allowed to do
        /// anything, and each of them fails to "vanilla normals" rather than to
        /// something broken.
        /// </summary>
        private void UpdateShaderState(PseudoPbrConfig config)
        {
            if (_binder == null) return;

            bool active = config.Enabled && _atlasTexture != null && _atlasTexture.IsUploaded && _atlasIsSinglePage;
            _binder.SetState(active, config.NormalStrength, config.SpecularStrength, config.DebugView);
        }

        private void WriteReportOnce(PseudoPbrConfig config)
        {
            if (!config.WriteMaterialReport) { _reportWritten = false; return; }
            if (_reportWritten) return;

            try
            {
                MaterialReport.Write(_mod.Capi, _mod.Mod.Logger);
            }
            catch (Exception ex)
            {
                _mod.Mod.Logger.Warning("[VintageVisuals] pseudopbr: could not write the material report: " + ex.Message);
            }

            _reportWritten = true;
        }

        private void BuildAtlasOnce(PseudoPbrConfig config)
        {
            if (!config.BuildMaterialAtlas)
            {
                // Say so rather than returning quietly. A feature that does
                // nothing AND logs nothing is indistinguishable from a feature
                // that is broken, and that ambiguity has already cost this
                // project a debugging round once.
                if (_atlasBuilt)
                {
                    _mod.Mod.Logger.Notification("[VintageVisuals] pseudopbr: material atlas disabled by config " +
                                                 "(PseudoPBR.BuildMaterialAtlas is false).");
                }
                _atlasBuilt = false;
                return;
            }

            if (_atlasBuilt) return;

            // Set before the work, not after: if this throws we do not want to
            // retry it on every subsequent config change.
            _atlasBuilt = true;

            var diagnostics = new PbrDiagnostics(_mod.Mod.Logger);
            string directory = Path.Combine(GamePaths.DataPath, DataDirectory);

            // Noted before the work, so the transcript shows the attempt even if
            // the build hangs or the process dies partway through.
            diagnostics.Note("building material atlas…");

            try
            {
                BuildAtlas(config, diagnostics, directory);
            }
            catch (Exception ex)
            {
                diagnostics.Error("material atlas build failed. Surface relief stays off and the world " +
                                  "renders as vanilla; nothing else in the mod is affected.", ex);
            }

            diagnostics.WriteTo(directory);
        }

        private void BuildAtlas(PseudoPbrConfig config, PbrDiagnostics diagnostics, string directory)
        {
            int width = _mod.Capi.BlockTextureAtlas.Size.Width;
            int height = _mod.Capi.BlockTextureAtlas.Size.Height;

            if (width <= 0 || height <= 0)
            {
                diagnostics.Warn("block texture atlas reports a zero size; skipping the material atlas.");
                return;
            }

            _atlasIsSinglePage = CheckSinglePageAtlas(diagnostics);

            int skipped;
            List<AtlasRegion> regions = MaterialAtlasSource.Collect(_mod.Capi, 0, diagnostics, out skipped);

            if (regions.Count == 0)
            {
                diagnostics.Warn("no block textures were collected; the material atlas would be empty.");
                return;
            }

            string cachePath = Path.Combine(directory, "material-atlas-0.bin");

            var stopwatch = Stopwatch.StartNew();
            ulong fingerprint = MaterialAtlasBuilder.Fingerprint(width, height, regions,
                                                                MaterialAtlasCache.FormatVersion);

            MaterialAtlasCache.CachedAtlas cached = MaterialAtlasCache.TryLoad(cachePath, fingerprint);

            int[] pixels;
            if (cached != null && cached.Width == width && cached.Height == height)
            {
                pixels = cached.Pixels;
                diagnostics.Note("reused cached material atlas (" + width + "x" + height + ") in " +
                                 stopwatch.ElapsedMilliseconds + "ms.");
            }
            else
            {
                int written, outOfBounds;
                pixels = MaterialAtlasBuilder.Build(width, height, regions, out written, out outOfBounds);

                diagnostics.Note("built material atlas (" + width + "x" + height + ") from " + written +
                                 " texture(s) in " + stopwatch.ElapsedMilliseconds + "ms" +
                                 (outOfBounds > 0 ? ", " + outOfBounds + " outside the atlas bounds" : "") + ".");

                try
                {
                    MaterialAtlasCache.Save(cachePath, width, height, fingerprint, pixels);
                }
                catch (Exception ex)
                {
                    // Losing the cache costs startup time on the next launch,
                    // nothing else.
                    diagnostics.Warn("could not cache the atlas: " + ex.Message);
                }
            }

            _atlasTexture.Upload(_mod.Capi, width, height, pixels, diagnostics);

            if (config.WriteAtlasPreview)
            {
                try
                {
                    string[] previews = AtlasPreview.WriteAll(directory, width, height, pixels);
                    diagnostics.Note("preview images written: " + string.Join(", ",
                        Array.ConvertAll(previews, Path.GetFileName)));
                }
                catch (Exception ex)
                {
                    diagnostics.Warn("could not write preview images: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Whether the block atlas fits on one page.
        ///
        /// The chunk shader samples our atlas with the same `uv` it uses for
        /// the diffuse — which is exactly right, and works only because both
        /// atlases have the same layout. When the game needs more than one
        /// page it binds a different terrain texture per draw call, and `uv`
        /// alone no longer says which page a fragment came from. There is
        /// nowhere left in the vertex format to tell us (VertexFlags is fully
        /// packed), so the honest answer is to render vanilla rather than to
        /// sample page 0 for every page and paint each block with some other
        /// block's surface.
        ///
        /// In practice a 1.22.7 install packs its ~3700 block textures into a
        /// single 4096x2048 page with room to spare, so this is a guard for
        /// heavily modded installs rather than the common case. Lifting it
        /// means uploading one material atlas per page and binding alongside
        /// whichever terrain page is active, which needs a hook into the chunk
        /// draw call this mod does not currently take.
        /// </summary>
        private bool CheckSinglePageAtlas(PbrDiagnostics diagnostics)
        {
            int pages = _mod.Capi.BlockTextureAtlas.AtlasTextures == null
                ? 0
                : _mod.Capi.BlockTextureAtlas.AtlasTextures.Count;

            if (pages <= 1) return true;

            diagnostics.Warn("the block texture atlas needed " + pages + " pages. Surface relief only supports a " +
                             "single-page atlas, so it stays off — the world renders exactly as vanilla. The " +
                             "material report and the derived atlas are still written.");
            return false;
        }

        public void Dispose()
        {
            if (_mod != null && _mod.Capi != null && _binder != null)
            {
                _mod.Capi.Event.UnregisterRenderer(_binder, EnumRenderStage.Opaque);
            }

            if (_atlasTexture != null)
            {
                _atlasTexture.Dispose();
                _atlasTexture = null;
            }

            _binder = null;
            _mod = null;
        }
    }
}
