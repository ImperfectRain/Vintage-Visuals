using System;
using System.Collections.Generic;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>One block texture's place in the atlas, plus what it is made of.</summary>
    public sealed class AtlasRegion
    {
        /// <summary>Top-left corner in atlas pixels.</summary>
        public int X;
        public int Y;

        /// <summary>Source diffuse, already converted to linear light.</summary>
        public LinearTexture Texture;

        /// <summary>Light response for the block this texture belongs to.</summary>
        public MaterialProfile Profile;

        /// <summary>For logging and the cache key. Not used in the maths.</summary>
        public string Name;
    }

    /// <summary>
    /// Assembles the derived material atlas: one texture, laid out to match the
    /// block texture atlas exactly, so the shader can sample it with the UVs the
    /// block already has.
    ///
    /// Channel packing — the single decision everything downstream depends on:
    ///
    ///   R = tangent-space normal X   (0.5 is flat)
    ///   G = tangent-space normal Y   (0.5 is flat)
    ///   B = roughness                (0 mirror, 1 fully diffuse)
    ///   A = specular reflectance     (0 matte, 1 fully reflective)
    ///
    /// Normal Z is not stored. It is reconstructed in the shader as
    /// sqrt(1 - x^2 - y^2), which is exact for a unit normal and buys the fourth
    /// channel for something that cannot be derived.
    ///
    /// Metalness is deliberately NOT in this atlas, even though
    /// <see cref="MaterialProfile"/> carries it. There are five values worth
    /// storing and four channels, and metalness is the one that can be dropped
    /// with the least visible loss: its main effect is tinting the specular
    /// highlight by albedo rather than white, which is a refinement on top of
    /// "is this shiny at all". Storing it needs either a second atlas — a whole
    /// extra texture unit — or a bit-packing trick that bilinear filtering would
    /// destroy. Revisit if metal ends up looking like shiny plastic; the fix is
    /// a second atlas, not a cleverer pack.
    /// </summary>
    public static class MaterialAtlasBuilder
    {
        /// <summary>
        /// Value written where no block texture covers the atlas. Flat normal,
        /// the default profile's roughness, no specular — so any gap, and any
        /// texture this mod failed to process, shades exactly like an ordinary
        /// matte surface rather than like a hole.
        /// </summary>
        public static int NeutralTexel()
        {
            return Pack(0.5f, 0.5f, MaterialProfiles.Default.Roughness, 0f);
        }

        /// <summary>
        /// Packs four 0..1 channels into the 0xAABBGGRR layout the game's
        /// LoadOrUpdateTextureFromRgba expects.
        /// </summary>
        public static int Pack(float r, float g, float b, float a)
        {
            return ToByte(r) | (ToByte(g) << 8) | (ToByte(b) << 16) | (ToByte(a) << 24);
        }

        private static int ToByte(float value)
        {
            if (float.IsNaN(value)) value = 0f;
            int scaled = (int)(value * 255f + 0.5f);
            return scaled < 0 ? 0 : (scaled > 255 ? 255 : scaled);
        }

        /// <summary>
        /// Builds the atlas. Returns the pixel buffer; <paramref name="written"/>
        /// reports how many regions were actually placed.
        ///
        /// Regions that fall outside the atlas are skipped rather than throwing:
        /// atlas layout comes from the game and a mismatch should cost one
        /// texture's material data, not the whole subsystem.
        /// </summary>
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

                LinearTexture texture = region.Texture;
                if (region.X < 0 || region.Y < 0 ||
                    region.X + texture.Width > atlasWidth ||
                    region.Y + texture.Height > atlasHeight)
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

            // Tiling is deliberately ON per source texture. Each region is one
            // block face that tiles against copies of itself in the world, so
            // its gradients must wrap within its own bounds - not bleed into
            // whatever unrelated texture happens to sit beside it in the atlas.
            PbrMaps maps = PbrMapGenerator.Generate(texture, region.Profile.NormalStrength, 1, true);

            for (int y = 0; y < texture.Height; y++)
            {
                int destRow = (region.Y + y) * atlasWidth + region.X;
                int sourceRow = y * texture.Width;

                for (int x = 0; x < texture.Width; x++)
                {
                    int source = sourceRow + x;

                    float roughness, specular, metalness;
                    MaterialProfiles.Combine(region.Profile,
                        (float)maps.Roughness[source], (float)maps.Spec[source],
                        out roughness, out specular, out metalness);

                    pixels[destRow + x] = Pack(
                        (float)maps.Normal[source * 3],
                        (float)maps.Normal[source * 3 + 1],
                        roughness,
                        specular);
                }
            }
        }

        /// <summary>
        /// Fingerprint of everything that affects the output, so a cached atlas
        /// can be trusted or discarded without rebuilding it to find out.
        ///
        /// FNV-1a rather than a cryptographic hash: this guards against a stale
        /// cache after a game update or a texture pack change, not against an
        /// attacker. It covers the atlas dimensions, every region's rectangle
        /// and profile, and the source pixels themselves — a retexture that
        /// keeps the same layout must still invalidate.
        /// </summary>
        public static ulong Fingerprint(int atlasWidth, int atlasHeight, IEnumerable<AtlasRegion> regions,
                                        int formatVersion)
        {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            ulong hash = offsetBasis;
            hash = Mix(hash, prime, (ulong)formatVersion);
            hash = Mix(hash, prime, (ulong)atlasWidth);
            hash = Mix(hash, prime, (ulong)atlasHeight);

            foreach (AtlasRegion region in regions)
            {
                if (region?.Texture == null) continue;

                hash = Mix(hash, prime, (ulong)region.X);
                hash = Mix(hash, prime, (ulong)region.Y);
                hash = Mix(hash, prime, (ulong)region.Texture.Width);
                hash = Mix(hash, prime, (ulong)region.Texture.Height);

                // Profile values are floats; hash their bit patterns so a
                // retune invalidates the cache exactly.
                hash = Mix(hash, prime, (ulong)BitConverter.SingleToInt32Bits(region.Profile.Roughness));
                hash = Mix(hash, prime, (ulong)BitConverter.SingleToInt32Bits(region.Profile.Metalness));
                hash = Mix(hash, prime, (ulong)BitConverter.SingleToInt32Bits(region.Profile.SpecularScale));
                hash = Mix(hash, prime, (ulong)BitConverter.SingleToInt32Bits(region.Profile.NormalStrength));
                hash = Mix(hash, prime, (ulong)BitConverter.SingleToInt32Bits(region.Profile.RoughnessVariation));

                LinearTexture texture = region.Texture;
                for (int i = 0; i < texture.R.Length; i++)
                {
                    // Quantise to the 8 bits the source actually had. Hashing
                    // raw doubles would make the key depend on floating-point
                    // noise from the sRGB conversion.
                    hash = Mix(hash, prime, (ulong)(byte)(texture.R[i] * 255.0));
                    hash = Mix(hash, prime, (ulong)(byte)(texture.G[i] * 255.0));
                    hash = Mix(hash, prime, (ulong)(byte)(texture.B[i] * 255.0));
                    hash = Mix(hash, prime, (ulong)(byte)(texture.Alpha[i] * 255.0));
                }
            }

            return hash;
        }

        private static ulong Mix(ulong hash, ulong prime, ulong value)
        {
            for (int shift = 0; shift < 64; shift += 8)
            {
                hash ^= (value >> shift) & 0xFF;
                hash *= prime;
            }
            return hash;
        }
    }
}
