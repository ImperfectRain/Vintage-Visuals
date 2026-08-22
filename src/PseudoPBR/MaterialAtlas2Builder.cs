using System;
using System.Collections.Generic;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// The SECOND material atlas: the properties that would not fit in the
    /// first.
    ///
    /// The first atlas spends all four of its channels on the surface's shape
    /// and shine - normal X, normal Y, roughness, specular - with the normal's
    /// Z reconstructed rather than stored precisely because there was no fourth
    /// channel to spare. That left two values <see cref="MaterialProfile"/>
    /// already derives, metalness and roughness variation, computed and then
    /// thrown away before they reached the GPU.
    ///
    /// This page carries what the first could not:
    ///
    ///   R = metalness      is this surface a conductor
    ///   G = height         the surface's own relief, as a normalised field
    ///   B = baked AO       broad occlusion from that height
    ///   A = emission mask  WHERE an already-emitting block emits
    ///
    /// The layout, the slot rectangles and the sampling convention are
    /// deliberately identical to the first atlas, so a single set of UVs
    /// addresses both. That is not a convenience - it is what makes the two
    /// pages one material rather than two textures that happen to be the same
    /// size, and it is checked by test rather than assumed.
    ///
    /// Nothing here invents a material property. Metalness comes from the
    /// block's own <c>EnumBlockMaterial</c> classification; the other three are
    /// derived from the texture the game already ships, exactly as the first
    /// atlas derives its normal and roughness from the same pixels.
    /// </summary>
    public static class MaterialAtlas2Builder
    {
        /// <summary>
        /// Bumped whenever the meaning of a channel changes, so a cache written
        /// by an older layout is rejected rather than reinterpreted.
        ///
        /// Kept apart from the first atlas's version, and mixed into the
        /// fingerprint, so the two caches cannot be confused for one another
        /// even though they describe the same regions at the same size.
        /// </summary>
        public const int FormatVersion = 1;

        /// <summary>
        /// What an untouched texel means: not metal, flat, unoccluded, and not
        /// emitting.
        ///
        /// Every one of those is the value that makes the consumer behave as if
        /// this atlas did not exist. A gap in the atlas, a texture that failed
        /// to process, a page the game barely filled - all of them shade
        /// exactly as they did before this page was added, rather than as some
        /// half-metallic half-glowing surface nobody authored.
        /// </summary>
        public static int NeutralTexel()
        {
            return MaterialAtlasBuilder.Pack(0f, 0.5f, 1f, 0f);
        }

        public static int[] Build(int atlasWidth, int atlasHeight, IEnumerable<AtlasRegion> regions,
                                  out int written, out int skipped)
        {
            if (atlasWidth <= 0 || atlasHeight <= 0) throw new ArgumentException("atlas size must be positive");

            var pixels = new int[atlasWidth * atlasHeight];
            int neutral = NeutralTexel();
            for (int i = 0; i < pixels.Length; i++) pixels[i] = neutral;

            written = 0;
            skipped = 0;

            foreach (AtlasRegion region in regions)
            {
                if (region?.Texture == null) { skipped++; continue; }

                int width = region.Width > 0 ? region.Width : region.Texture.Width;
                int height = region.Height > 0 ? region.Height : region.Texture.Height;

                // Same bounds rule as the first atlas: a region the game placed
                // somewhere we cannot follow costs that texture's data, not the
                // page.
                if (region.X < 0 || region.Y < 0 ||
                    region.X + width > atlasWidth ||
                    region.Y + height > atlasHeight)
                {
                    skipped++;
                    continue;
                }

                WriteRegion(pixels, atlasWidth, region);
                written++;
            }

            return pixels;
        }

        private static void WriteRegion(int[] pixels, int atlasWidth, AtlasRegion region)
        {
            LinearTexture texture = region.Texture;

            double[] luminance = PbrMapGenerator.Luminance(texture);
            double[] height = Normalise(luminance);

            // Tiling ON, matching the first atlas: each region is one block face
            // that tiles against copies of itself, so its neighbourhood must
            // wrap within its own bounds rather than bleed into whatever
            // unrelated texture sits beside it in the atlas.
            double[] mean = PbrMapGenerator.BoxMean(height, texture.Width, texture.Height, 2, true);

            float metalness = MetalnessFor(region);
            double[] emission = EmissionMask(region, luminance);

            int slotWidth = region.Width > 0 ? region.Width : texture.Width;
            int slotHeight = region.Height > 0 ? region.Height : texture.Height;

            for (int y = 0; y < slotHeight; y++)
            {
                int destRow = (region.Y + y) * atlasWidth + region.X;

                // Nearest, exactly as the first atlas does it. The two pages
                // must land on the same texels for the same UV or every
                // consumer that reads both is quietly comparing one texel
                // against its neighbour.
                int sourceY = slotHeight == texture.Height
                    ? y
                    : Math.Min(texture.Height - 1, y * texture.Height / slotHeight);

                int sourceRow = sourceY * texture.Width;

                for (int x = 0; x < slotWidth; x++)
                {
                    int sourceX = slotWidth == texture.Width
                        ? x
                        : Math.Min(texture.Width - 1, x * texture.Width / slotWidth);

                    int source = sourceRow + sourceX;

                    pixels[destRow + x] = MaterialAtlasBuilder.Pack(
                        metalness,
                        (float)height[source],
                        (float)Occlusion(height[source], mean[source]),
                        (float)emission[source]);
                }
            }
        }

        /// <summary>
        /// Metalness for the whole region, from the block's classification.
        ///
        /// Deliberately per-REGION rather than per-texel. Whether a surface is a
        /// conductor is a property of what the block is made of, which the game
        /// already states through EnumBlockMaterial; it is not something to be
        /// guessed from how bright a pixel happens to be. Deriving it from
        /// pixels is how every dark surface ends up metallic.
        /// </summary>
        public static float MetalnessFor(AtlasRegion region)
        {
            float roughness, specular, metalness;
            MaterialProfiles.Combine(region.Profile, 0.5f, 0.5f,
                out roughness, out specular, out metalness);

            return metalness;
        }

        /// <summary>
        /// Occlusion from how far a texel sits below its own neighbourhood.
        ///
        /// Broad and static, unlike the shader's vvCavity which measures
        /// curvature in the normal at a one-texel radius. This is the shape of
        /// the surface; that is the grain of it. They are not the same signal
        /// and the eventual combination rule has to be decided by looking, not
        /// by multiplying them together because both happen to be occlusion.
        ///
        /// Only the recessed half darkens. Brightening the raised half would be
        /// symmetric and wrong for the same reason it is wrong in vvCavity:
        /// occlusion removes light from hollows, it does not add light to bumps.
        /// </summary>
        public static double Occlusion(double height, double localMean)
        {
            double below = localMean - height;
            if (below <= 0.0) return 1.0;

            double occluded = below * 1.6;
            return 1.0 - (occluded > 0.85 ? 0.85 : occluded);
        }

        /// <summary>
        /// Where an emitting block emits.
        ///
        /// Gated absolutely on vanilla's own glow level: a block the game says
        /// does not emit gets zero everywhere, whatever its pixels look like.
        /// That ordering is the whole design. Vanilla decides WHETHER and HOW
        /// STRONGLY; this decides only WHERE.
        ///
        /// Within an emitting texture the mask is conservative by construction:
        /// it is cut at the texture's own brightness distribution rather than at
        /// an absolute threshold, so it selects the hottest part of THIS
        /// texture. A forge's coals clear its stonework; a lantern's flame
        /// clears its metal housing. An absolute threshold would instead make
        /// every pale block emit and every dark one stay dark, which is the
        /// "bright pixels are emissive" mistake this is written to avoid.
        /// </summary>
        public static double[] EmissionMask(AtlasRegion region, double[] luminance)
        {
            var mask = new double[luminance.Length];

            // Vanilla says no. Nothing else is consulted.
            if (region.GlowLevel <= 0f) return mask;

            double low = Percentile(luminance, 0.70);
            double high = Percentile(luminance, 0.95);

            // A texture with no internal contrast - a uniformly glowing block -
            // should emit everywhere rather than nowhere, which is what a
            // degenerate percentile range would otherwise produce.
            if (high - low < 1e-4)
            {
                for (int i = 0; i < mask.Length; i++) mask[i] = 1.0;
                return mask;
            }

            for (int i = 0; i < mask.Length; i++)
            {
                double t = (luminance[i] - low) / (high - low);
                t = t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
                mask[i] = t * t * (3.0 - 2.0 * t);
            }

            return mask;
        }

        /// <summary>
        /// Height, normalised to the texture's own range.
        ///
        /// Per-texture rather than absolute so that a dark block still has
        /// relief. Luminance is a poor stand-in for height in a known way - a
        /// painted-on dark line reads as a groove - and it is the same stand-in
        /// the first atlas already uses for its normal map, so the two agree
        /// with each other even where both are wrong about the real surface.
        /// </summary>
        public static double[] Normalise(double[] values)
        {
            var result = new double[values.Length];
            if (values.Length == 0) return result;

            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] < min) min = values[i];
                if (values[i] > max) max = values[i];
            }

            double range = max - min;

            // A flat texture is flat, not maximally tall. Half rather than zero
            // so the channel's neutral and a genuinely featureless surface
            // agree.
            if (range < 1e-6)
            {
                for (int i = 0; i < result.Length; i++) result[i] = 0.5;
                return result;
            }

            for (int i = 0; i < result.Length; i++) result[i] = (values[i] - min) / range;
            return result;
        }

        private static double Percentile(double[] values, double fraction)
        {
            var sorted = (double[])values.Clone();
            Array.Sort(sorted);

            int index = (int)(fraction * (sorted.Length - 1));
            if (index < 0) index = 0;
            if (index >= sorted.Length) index = sorted.Length - 1;

            return sorted[index];
        }

        /// <summary>
        /// Fingerprint for the cache.
        ///
        /// Reuses the first atlas's fingerprint over the same regions - which
        /// already covers every rectangle, every profile value including
        /// metalness, and the source pixels themselves - and mixes in this
        /// page's own format version and a discriminator. Two caches describing
        /// the same regions at the same size therefore cannot be mistaken for
        /// each other, and any change that invalidates page one invalidates
        /// page two as well.
        /// </summary>
        public static ulong Fingerprint(int atlasWidth, int atlasHeight, IEnumerable<AtlasRegion> regions,
                                        int formatVersion)
        {
            ulong baseHash = MaterialAtlasBuilder.Fingerprint(atlasWidth, atlasHeight, regions, formatVersion);

            const ulong prime = 1099511628211UL;
            ulong hash = baseHash ^ 0x4d41543200000000UL;   // "MAT2"
            hash = (hash ^ (ulong)FormatVersion) * prime;

            // Glow level is ours alone - the first atlas has no reason to hash
            // it, and a block that starts or stops emitting must rebuild this
            // page.
            foreach (AtlasRegion region in regions)
            {
                if (region == null) continue;
                hash = (hash ^ (ulong)BitConverter.SingleToInt32Bits(region.GlowLevel)) * prime;
            }

            return hash;
        }
    }
}
