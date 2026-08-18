using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Reads the game's block texture atlas and turns it into the region list
    /// the builder needs: every block texture, where it sits in the atlas, and
    /// what material the block that owns it is made of.
    ///
    /// This is the join between the two data sources. The atlas knows WHERE a
    /// texture lives; the block knows WHAT it is. Neither alone is enough.
    ///
    /// Source pixels come from the texture assets rather than by reading the
    /// atlas back off the GPU. A readback would need a render context, a stall,
    /// and a format assumption; the assets are already on disk, already
    /// addressable, and can be read on any thread.
    ///
    /// Everything here is public API. The atlas hook that would let this run
    /// automatically at upload time is not — see the subsystem README.
    /// </summary>
    public static class MaterialAtlasSource
    {
        /// <summary>
        /// Collects regions for one atlas page.
        ///
        /// Vintage Story splits its block textures across several atlas
        /// textures once they no longer fit in one, so a derived atlas is built
        /// per page and <paramref name="atlasNumber"/> selects which.
        /// </summary>
        public static List<AtlasRegion> Collect(ICoreClientAPI capi, int atlasNumber, ILogger logger,
                                                out int skippedTextures)
        {
            var regions = new List<AtlasRegion>();
            var seen = new HashSet<int>();
            var skipReasons = new Dictionary<string, int>();
            var skipExamples = new Dictionary<string, string>();
            skippedTextures = 0;

            int atlasWidth = capi.BlockTextureAtlas.Size.Width;
            int atlasHeight = capi.BlockTextureAtlas.Size.Height;

            foreach (Block block in capi.World.Blocks)
            {
                if (block?.Code == null || block.Textures == null) continue;
                if (block.BlockMaterial == EnumBlockMaterial.Air) continue;

                MaterialProfile profile = MaterialProfiles.For(block.BlockMaterial);

                foreach (KeyValuePair<string, CompositeTexture> entry in block.Textures)
                {
                    CompositeTexture composite = entry.Value;
                    if (composite?.Baked == null) continue;

                    // Many blocks share a texture - every fence variant of a
                    // wood type points at the same planks. Deriving it once per
                    // block would be thousands of redundant Sobel passes over
                    // the same pixels.
                    if (!seen.Add(composite.Baked.TextureSubId)) continue;

                    TextureAtlasPosition position =
                        capi.BlockTextureAtlas.GetPosition(block, entry.Key, true);

                    if (position == null || position.atlasNumber != atlasNumber) continue;

                    string name = block.Code.ToString() + ":" + entry.Key;
                    string reason;
                    AtlasRegion region = TryBuildRegion(capi, composite, position, profile,
                        atlasWidth, atlasHeight, name, out reason);

                    if (region != null)
                    {
                        regions.Add(region);
                        continue;
                    }

                    skippedTextures++;

                    // Count why, and keep a few examples. A bare "3749 skipped"
                    // says something is wrong but not what, which is exactly
                    // how a fail-soft path turns into a wasted debugging round.
                    int count;
                    skipReasons.TryGetValue(reason, out count);
                    skipReasons[reason] = count + 1;

                    if (!skipExamples.ContainsKey(reason)) skipExamples[reason] = name;
                }
            }

            logger.Notification("[VintageVisuals] pseudopbr: atlas page " + atlasNumber + " — " +
                                regions.Count + " texture(s) collected, " + skippedTextures + " skipped.");

            foreach (KeyValuePair<string, int> reason in skipReasons)
            {
                logger.Warning("[VintageVisuals] pseudopbr:   skipped " + reason.Value + " — " + reason.Key +
                               " (e.g. " + skipExamples[reason.Key] + ")");
            }

            return regions;
        }

        /// <summary>
        /// Finds the PNG behind a composite texture.
        ///
        /// Two traps here, both of which silently returned null on every one of
        /// 3749 textures before they were found:
        ///
        /// 1. Texture asset locations are declared without their category or
        ///    extension - "block/stone/granite" - and must be resolved as
        ///    "textures/block/stone/granite.png". This is the same
        ///    WithPathPrefixOnce/WithPathAppendixOnce pattern the game's own
        ///    ITextureSource uses.
        /// 2. BakedName is NOT a filename. CompositeTexture.Bake appends
        ///    synthetic suffixes for overlays, rotation and alpha, so a baked
        ///    name can describe a composite that exists only in the atlas and
        ///    has no file behind it at all.
        ///
        /// So resolution goes through Base, the plain source path, and falls
        /// back to BakedName only for the simple case where they are equal.
        /// The cost is that overlays, rotation and alpha are not composited into
        /// the derived maps - material response comes from the base texture.
        /// For roughness and surface relief that is a fair approximation; if an
        /// overlaid block ever looks wrong, this is the reason.
        /// </summary>
        private static IAsset ResolveTextureAsset(ICoreClientAPI capi, CompositeTexture composite)
        {
            AssetLocation source = composite.Base ?? composite.Baked.BakedName;
            if (source == null) return null;

            AssetLocation resolved = source.Clone()
                .WithPathPrefixOnce("textures/")
                .WithPathAppendixOnce(".png");

            return capi.Assets.TryGet(resolved);
        }

        private static AtlasRegion TryBuildRegion(ICoreClientAPI capi, CompositeTexture composite,
                                                  TextureAtlasPosition position, MaterialProfile profile,
                                                  int atlasWidth, int atlasHeight, string name,
                                                  out string skipReason)
        {
            skipReason = null;

            try
            {
                IAsset asset = ResolveTextureAsset(capi, composite);
                if (asset == null)
                {
                    skipReason = "texture asset not found";
                    return null;
                }

                BitmapRef bitmap = asset.ToBitmap(capi);
                if (bitmap == null)
                {
                    skipReason = "asset could not be decoded as a bitmap";
                    return null;
                }

                using (bitmap)
                {
                    LinearTexture texture = ToLinearTexture(bitmap);
                    if (texture == null)
                    {
                        skipReason = "bitmap had no usable pixels";
                        return null;
                    }

                    // Atlas positions are normalised UVs; the builder works in
                    // pixels. Rounding rather than truncating because the UVs
                    // carry sub-pixel padding and truncation lands a texture one
                    // pixel left of where the shader will sample it.
                    return new AtlasRegion
                    {
                        X = (int)Math.Round(position.x1 * atlasWidth),
                        Y = (int)Math.Round(position.y1 * atlasHeight),
                        Texture = texture,
                        Profile = profile,
                        Name = name,
                    };
                }
            }
            catch (Exception ex)
            {
                // One unreadable texture must cost that texture's material data
                // and nothing else - but the reason still has to survive, or a
                // systematic failure looks identical to a handful of odd files.
                skipReason = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>Converts a loaded bitmap to linear-light channels.</summary>
        public static LinearTexture ToLinearTexture(IBitmap bitmap)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            if (width <= 0 || height <= 0) return null;

            int[] argb = bitmap.Pixels;
            if (argb == null || argb.Length < width * height) return null;

            var texture = new LinearTexture
            {
                Width = width,
                Height = height,
                R = new double[width * height],
                G = new double[width * height],
                B = new double[width * height],
                Alpha = new double[width * height],
            };

            for (int i = 0; i < width * height; i++)
            {
                int pixel = argb[i];

                // The game's Pixels array is 0xAABBGGRR.
                double r = (pixel & 0xFF) / 255.0;
                double g = ((pixel >> 8) & 0xFF) / 255.0;
                double b = ((pixel >> 16) & 0xFF) / 255.0;
                double a = ((pixel >> 24) & 0xFF) / 255.0;

                texture.R[i] = PbrMapGenerator.SrgbToLinear(r);
                texture.G[i] = PbrMapGenerator.SrgbToLinear(g);
                texture.B[i] = PbrMapGenerator.SrgbToLinear(b);
                texture.Alpha[i] = a;
            }

            return texture;
        }
    }
}
