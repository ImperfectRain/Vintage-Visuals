using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VintageVisuals.Common;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Phase 4 subsystem: works out what material every block is made of, and
    /// derives a material atlas from the block textures.
    ///
    /// It does not yet change how the game renders. The atlas is built and
    /// cached, and can be inspected as images, but nothing samples it — that
    /// needs the chunk shaders patched, which is the next step.
    ///
    /// Landing it in this order is deliberate. Each stage produces something
    /// inspectable on its own: the report caught a mis-tuned Ceramic profile
    /// covering 14% of the world before any pixel was drawn, and the preview
    /// images answer "do the grooves read" without a lighting model in place.
    /// </summary>
    public sealed class PseudoPbrSubsystem : IVisualSubsystem
    {
        public const string GroupName = "pseudopbr";

        /// <summary>Where the cache and preview images live, under VintagestoryData.</summary>
        public const string DataDirectory = "VintageVisuals";

        private VintageVisualsModSystem _mod;
        private bool _reportWritten;
        private bool _atlasBuilt;

        public string Name => GroupName;

        public void Initialize(VintageVisualsModSystem mod)
        {
            _mod = mod;
        }

        public void Apply()
        {
            if (_mod == null || _mod.Capi == null) return;

            PseudoPbrConfig config = _mod.ConfigManager.Config.PseudoPBR;

            WriteReportOnce(config);
            BuildAtlasOnce(config);
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

            // Logged before the work, so the log always shows the attempt even
            // if the build hangs or the process dies partway through.
            _mod.Mod.Logger.Notification("[VintageVisuals] pseudopbr: building material atlas…");

            try
            {
                BuildAtlas(config);
            }
            catch (Exception ex)
            {
                _mod.Mod.Logger.Error("[VintageVisuals] pseudopbr: material atlas build failed. " +
                    "Nothing consumes it yet, so rendering is unaffected.");
                _mod.Mod.Logger.LogException(EnumLogType.Error, ex);
            }
        }

        private void BuildAtlas(PseudoPbrConfig config)
        {
            int width = _mod.Capi.BlockTextureAtlas.Size.Width;
            int height = _mod.Capi.BlockTextureAtlas.Size.Height;

            if (width <= 0 || height <= 0)
            {
                _mod.Mod.Logger.Warning("[VintageVisuals] pseudopbr: block texture atlas reports a zero size; " +
                                        "skipping the material atlas.");
                return;
            }

            int skipped;
            List<AtlasRegion> regions = MaterialAtlasSource.Collect(_mod.Capi, 0, _mod.Mod.Logger, out skipped);

            if (regions.Count == 0)
            {
                _mod.Mod.Logger.Warning("[VintageVisuals] pseudopbr: no block textures were collected; " +
                                        "the material atlas would be empty.");
                return;
            }

            string directory = Path.Combine(GamePaths.DataPath, DataDirectory);
            string cachePath = Path.Combine(directory, "material-atlas-0.bin");

            var stopwatch = Stopwatch.StartNew();
            ulong fingerprint = MaterialAtlasBuilder.Fingerprint(width, height, regions,
                                                                MaterialAtlasCache.FormatVersion);

            MaterialAtlasCache.CachedAtlas cached = MaterialAtlasCache.TryLoad(cachePath, fingerprint);

            int[] pixels;
            if (cached != null && cached.Width == width && cached.Height == height)
            {
                pixels = cached.Pixels;
                _mod.Mod.Logger.Notification("[VintageVisuals] pseudopbr: reused cached material atlas (" +
                    width + "x" + height + ") in " + stopwatch.ElapsedMilliseconds + "ms.");
            }
            else
            {
                int written, outOfBounds;
                pixels = MaterialAtlasBuilder.Build(width, height, regions, out written, out outOfBounds);

                _mod.Mod.Logger.Notification("[VintageVisuals] pseudopbr: built material atlas (" +
                    width + "x" + height + ") from " + written + " texture(s) in " +
                    stopwatch.ElapsedMilliseconds + "ms" +
                    (outOfBounds > 0 ? ", " + outOfBounds + " outside the atlas bounds" : "") + ".");

                try
                {
                    MaterialAtlasCache.Save(cachePath, width, height, fingerprint, pixels);
                }
                catch (Exception ex)
                {
                    // Losing the cache costs startup time on the next launch,
                    // nothing else.
                    _mod.Mod.Logger.Warning("[VintageVisuals] pseudopbr: could not cache the atlas: " + ex.Message);
                }
            }

            if (config.WriteAtlasPreview)
            {
                try
                {
                    string[] written = AtlasPreview.WriteAll(directory, width, height, pixels);
                    _mod.Mod.Logger.Notification("[VintageVisuals] pseudopbr: preview images written to " +
                                                 Path.GetDirectoryName(written[0]));
                }
                catch (Exception ex)
                {
                    _mod.Mod.Logger.Warning("[VintageVisuals] pseudopbr: could not write preview images: " + ex.Message);
                }
            }
        }

        public void Dispose()
        {
            _mod = null;
        }
    }
}
