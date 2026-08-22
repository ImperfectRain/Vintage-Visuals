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
        public static List<AtlasRegion> Collect(ICoreClientAPI capi, int atlasNumber, PbrDiagnostics diagnostics,
                                                out int skippedTextures)
        {
            var regions = new List<AtlasRegion>();
            var seen = new HashSet<int>();
            var skipReasons = new Dictionary<string, int>();
            var skipExamples = new Dictionary<string, string>();
            skippedTextures = 0;
            int resized = 0;
            int reclassified = 0;
            var reclassifiedExamples = new List<string>();

            int atlasWidth = capi.BlockTextureAtlas.Size.Width;
            int atlasHeight = capi.BlockTextureAtlas.Size.Height;

            foreach (Block block in capi.World.Blocks)
            {
                if (block?.Code == null || block.Textures == null) continue;
                if (block.BlockMaterial == EnumBlockMaterial.Air) continue;

                // NOT the profile any more - the fallback for textures whose
                // own evidence says nothing. See MaterialResolver: a block is
                // one classification and a composite block draws several
                // substances, so resolution happens per TEXTURE below.
                EnumBlockMaterial blockMaterial = block.BlockMaterial;

                // Vanilla's own emission level for this block, normalised the
                // way the vertex shader normalises it: chunkopaque.vsh reads
                // glowLevel = (renderFlags & GlowLevelBitMask) / 256.0, and
                // GlowLevel is that same byte. Matching the divisor keeps the
                // CPU's idea of "does this emit" identical to the shader's.
                //
                // This is the ONLY thing that decides whether a texture is
                // allowed an emission mask at all. Nothing about the pixels can
                // grant one.
                float glowLevel = 0f;
                try
                {
                    if (block.VertexFlags != null) glowLevel = block.VertexFlags.GlowLevel / 256f;
                }
                catch (Exception)
                {
                    // A block whose flags cannot be read simply does not emit,
                    // which is the safe direction: no mask rather than a
                    // guessed one.
                }

                foreach (KeyValuePair<string, CompositeTexture> entry in block.Textures)
                {
                    CompositeTexture composite = entry.Value;
                    if (composite?.Baked == null) continue;

                    // Every baked texture the composite produces, not just its
                    // base. A composite with Alternates bakes one variant per
                    // alternate, each with its own TextureSubId and its own
                    // slot in the atlas - and VS uses alternates heavily for
                    // natural blocks, granite being the obvious one. Processing
                    // only the base left every variant slot at the neutral
                    // texel, which in game is a granite block with no surface
                    // at all sitting next to one that has it. Tiled textures
                    // bake the same way.
                    foreach (BakedCompositeTexture baked in EnumerateBaked(composite.Baked))
                    {
                        // Many blocks share a texture - every fence variant of
                        // a wood type points at the same planks. Deriving it
                        // once per block would be thousands of redundant Sobel
                        // passes over the same pixels.
                        if (!seen.Add(baked.TextureSubId)) continue;

                        TextureAtlasPosition position = PositionFor(capi, baked.TextureSubId);
                        if (position == null || position.atlasNumber != atlasNumber) continue;

                        // TextureFilenames[0] is the variant's own base path, so
                        // each alternate resolves to its own PNG rather than to
                        // whatever the composite's base happened to be.
                        AssetLocation source = baked.TextureFilenames != null && baked.TextureFilenames.Length > 0
                            ? baked.TextureFilenames[0]
                            : composite.Base;

                        string name = block.Code.ToString() + ":" + entry.Key;
                        string reason;
                        bool rescaled;

                        // Per texture, not per block. A gate's iron strapping
                        // and its planks are the same block and different
                        // substances.
                        MaterialResolution resolution = MaterialResolver.Resolve(
                            blockMaterial, entry.Key, source?.ToString());

                        if (resolution.Reclassified)
                        {
                            reclassified++;
                            if (reclassifiedExamples.Count < 6)
                            {
                                reclassifiedExamples.Add(name + " -> " + resolution.Material +
                                                         " (from " + resolution.Evidence + ")");
                            }
                        }

                        AtlasRegion region = TryBuildRegion(capi, source, position, resolution.Profile,
                            glowLevel, atlasWidth, atlasHeight, name, out reason, out rescaled);
                        if (rescaled) resized++;

                        if (region != null)
                        {
                            regions.Add(region);
                            continue;
                        }

                        skippedTextures++;

                        // Count why, and keep a few examples. A bare "3749
                        // skipped" says something is wrong but not what, which
                        // is exactly how a fail-soft path turns into a wasted
                        // debugging round.
                        int count;
                        skipReasons.TryGetValue(reason, out count);
                        skipReasons[reason] = count + 1;

                        if (!skipExamples.ContainsKey(reason)) skipExamples[reason] = name;
                    }
                }
            }

            // Coverage, not just counts. "6735 collected, 0 skipped" reads like
            // success and says nothing about slots never enumerated at all -
            // which is exactly how a whole class of blocks came to render with
            // no surface while the log looked clean.
            int allocated = 0;
            TextureAtlasPosition[] allPositions = capi.BlockTextureAtlas.Positions;
            if (allPositions != null)
            {
                foreach (TextureAtlasPosition p in allPositions)
                {
                    if (p != null && p.atlasNumber == atlasNumber) allocated++;
                }
            }

            diagnostics.Note("atlas page " + atlasNumber + ": " + regions.Count +
                             " texture(s) collected, " + skippedTextures + " skipped, " +
                             allocated + " slot(s) allocated" +
                             (allocated > 0
                                 ? " (" + (100 * regions.Count / allocated) +
                                   "% covered; uncovered slots render with no surface detail)"
                                 : "") + ".");

            if (resized > 0)
            {
                diagnostics.Note("  " + resized + " texture(s) were rescaled to their atlas slot; the source " +
                                 "PNG and the slot were different sizes, which would otherwise misalign the " +
                                 "derived maps against the diffuse.");
            }

            if (reclassified > 0)
            {
                diagnostics.Note("  " + reclassified + " texture(s) were classified from their own asset path or " +
                                 "slot name rather than from their block's material - composite blocks like gates, " +
                                 "doors and lanterns draw more than one substance. Examples: " +
                                 string.Join(", ", reclassifiedExamples) + ".");
            }

            foreach (KeyValuePair<string, int> reason in skipReasons)
            {
                diagnostics.Warn("  skipped " + reason.Value + " — " + reason.Key +
                                 " (e.g. " + skipExamples[reason.Key] + ")");
            }

            return regions;
        }

        /// <summary>
        /// Every baked texture one composite produces: itself, its variants
        /// (from Alternates) and its tiles. Each has its own TextureSubId and
        /// therefore its own place in the atlas.
        /// </summary>
        private static IEnumerable<BakedCompositeTexture> EnumerateBaked(BakedCompositeTexture baked)
        {
            if (baked == null) yield break;

            yield return baked;

            // BakedVariants and BakedTiles are documented as also containing
            // the baked name itself, so the seen-set is what stops the base
            // being processed twice rather than any check here.
            if (baked.BakedVariants != null)
            {
                foreach (BakedCompositeTexture variant in baked.BakedVariants)
                {
                    if (variant != null) yield return variant;
                }
            }

            if (baked.BakedTiles != null)
            {
                foreach (BakedCompositeTexture tile in baked.BakedTiles)
                {
                    if (tile != null) yield return tile;
                }
            }
        }

        /// <summary>
        /// The atlas slot for a texture sub-id.
        ///
        /// Positions is indexed by TextureSubId, which is the only way to reach
        /// a variant's slot - GetPosition(block, textureCode) answers for the
        /// composite's base and knows nothing about its alternates.
        /// </summary>
        private static TextureAtlasPosition PositionFor(ICoreClientAPI capi, int textureSubId)
        {
            TextureAtlasPosition[] positions = capi.BlockTextureAtlas.Positions;

            if (positions == null || textureSubId < 0 || textureSubId >= positions.Length) return null;

            return positions[textureSubId];
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
        private static IAsset ResolveTextureAsset(ICoreClientAPI capi, AssetLocation source)
        {
            if (source == null) return null;

            AssetLocation resolved = source.Clone()
                .WithPathPrefixOnce("textures/")
                .WithPathAppendixOnce(".png");

            return capi.Assets.TryGet(resolved);
        }

        private static AtlasRegion TryBuildRegion(ICoreClientAPI capi, AssetLocation source,
                                                  TextureAtlasPosition position, MaterialProfile profile,
                                                  float glowLevel,
                                                  int atlasWidth, int atlasHeight, string name,
                                                  out string skipReason, out bool rescaled)
        {
            skipReason = null;
            rescaled = false;

            try
            {
                IAsset asset = ResolveTextureAsset(capi, source);
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
                    // The slot the game allocated, in pixels. It is NOT safe to
                    // assume this matches the source PNG: the atlas stores
                    // textures at whatever size the game chose for them, and a
                    // texture pack or an upscaled atlas makes the two diverge.
                    // Writing source-sized data into a differently sized slot
                    // puts the derived maps at the wrong scale, so the relief
                    // stops lining up with the texture it came from - which
                    // looks exactly like "soft and muddy" rather than like a
                    // bug.
                    int slotWidth = Math.Max(1, (int)Math.Round((position.x2 - position.x1) * atlasWidth));
                    int slotHeight = Math.Max(1, (int)Math.Round((position.y2 - position.y1) * atlasHeight));

                    rescaled = slotWidth != texture.Width || slotHeight != texture.Height;

                    return new AtlasRegion
                    {
                        X = (int)Math.Round(position.x1 * atlasWidth),
                        Y = (int)Math.Round(position.y1 * atlasHeight),
                        Width = slotWidth,
                        Height = slotHeight,
                        Texture = texture,
                        Profile = profile,
                        GlowLevel = glowLevel,
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
