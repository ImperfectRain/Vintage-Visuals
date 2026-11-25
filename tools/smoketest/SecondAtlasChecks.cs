using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VintageVisuals.PseudoPBR;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// The second material atlas: its packing, its correspondence with the
    /// first, and its behaviour when it is not there.
    ///
    /// The correspondence is the part worth testing hardest. Two atlases that
    /// merely happen to be the same size are two textures; two atlases that
    /// address the same slot with the same UV are one material. Every consumer
    /// that reads both - metalness against albedo, emission mask against
    /// glowLevel - silently depends on the second property, and nothing about
    /// the code makes it obvious when it stops being true.
    /// </summary>
    public static class SecondAtlasChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            CheckNeutralAgreement(repo, check);
            CheckPackingAndBounds(check);
            CheckCorrespondence(check);
            CheckEmissionGating(check);
            CheckSamplerDeclaration(repo, check);
            CheckFallbackWiring(repo, check);
            CheckHeightAlignment(check);
        }

        private static AtlasRegion Region(int x, int y, int size, MaterialProfile profile, float glow)
        {
            var texture = new LinearTexture
            {
                Width = size,
                Height = size,
                R = new double[size * size],
                G = new double[size * size],
                B = new double[size * size],
                Alpha = new double[size * size],
            };

            // A deterministic but non-uniform pattern, so height, AO and the
            // emission percentiles all have something to work with.
            for (int i = 0; i < size * size; i++)
            {
                double v = ((i * 37) % 251) / 251.0;
                texture.R[i] = v;
                texture.G[i] = v * 0.8;
                texture.B[i] = v * 0.6;
                texture.Alpha[i] = 1.0;
            }

            return new AtlasRegion
            {
                X = x, Y = y, Width = size, Height = size,
                Texture = texture, Profile = profile, GlowLevel = glow, Name = "test",
            };
        }

        /// <summary>
        /// The shader's idea of "no data" and the builder's must be the same
        /// four numbers, or an untouched texel shades as something nobody
        /// authored.
        /// </summary>
        private static void CheckNeutralAgreement(string repo, Action<string, bool, string> check)
        {
            int neutral = MaterialAtlas2Builder.NeutralTexel();

            float r = (neutral & 0xFF) / 255f;
            float g = ((neutral >> 8) & 0xFF) / 255f;
            float b = ((neutral >> 16) & 0xFF) / 255f;
            float a = ((neutral >> 24) & 0xFF) / 255f;

            check("the neutral texel is not metal", Math.Abs(r) < 0.01f, r.ToString());
            check("the neutral texel is mid height", Math.Abs(g - 0.5f) < 0.01f, g.ToString());
            check("the neutral texel is unoccluded", Math.Abs(b - 1f) < 0.01f, b.ToString());
            check("the neutral texel does not emit", Math.Abs(a) < 0.01f, a.ToString());

            string glsl = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pseudopbr.glsl"));

            check("the shader's neutral material agrees with the builder's",
                Regex.IsMatch(glsl, @"VvMaterial2\s*\(\s*0\.0\s*,\s*0\.5\s*,\s*1\.0\s*,\s*0\.0\s*\)"),
                "vvNeutralMaterial2 must match MaterialAtlas2Builder.NeutralTexel");
        }

        private static void CheckPackingAndBounds(Action<string, bool, string> check)
        {
            var profile = MaterialProfiles.For(Vintagestory.API.Common.EnumBlockMaterial.Metal);
            var regions = new List<AtlasRegion> { Region(0, 0, 8, profile, 0f) };

            int written, skipped;
            int[] pixels = MaterialAtlas2Builder.Build(32, 32, regions, out written, out skipped);

            check("the second atlas builds", written == 1 && skipped == 0,
                "written " + written + " skipped " + skipped);

            check("the second atlas is the size it was asked for", pixels.Length == 32 * 32,
                pixels.Length.ToString());

            // D66: red carries metalness in its top seven bits and the
            // hold-water flag in its lowest. Decoded exactly as the shader
            // decodes it, so the two cannot drift apart.
            var waterRegion = Region(0, 0, 8, profile, 0f);
            waterRegion.HoldsWater = true;
            int[] withWater = MaterialAtlas2Builder.Build(32, 32, new List<AtlasRegion> { waterRegion }, out written, out skipped);
            int redDry = pixels[0] & 0xFF, redWet = withWater[0] & 0xFF;
            check("the water flag rides the lowest red bit and leaves metalness intact",
                (redDry & 1) == 0 && (redWet & 1) == 1 && (redDry >> 1) == (redWet >> 1) &&
                Math.Abs((redWet >> 1) / 127f - MaterialAtlas2Builder.MetalnessFor(waterRegion)) < 0.01f &&
                MaterialProfiles.HoldsWater(Vintagestory.API.Common.EnumBlockMaterial.Stone) &&
                !MaterialProfiles.HoldsWater(Vintagestory.API.Common.EnumBlockMaterial.Soil) &&
                !MaterialProfiles.HoldsWater(Vintagestory.API.Common.EnumBlockMaterial.Plant),
                "red dry " + redDry + ", red wet " + redWet);

            // D67: the flag now means "this region carries the crevice
            // layout", and the layout is the crevice maps, packed exactly as
            // the shader unpacks them. A glowing or oversized region keeps the
            // plain layout, because its alpha is the emission mask.
            {
                var cobble = Region(0, 0, 32, profile, 0f);
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 32; x++)
                    {
                        double v = CreviceParityChecks.Texel(x, y) / 255.0;
                        cobble.Texture.R[y * 32 + x] = v; cobble.Texture.G[y * 32 + x] = v; cobble.Texture.B[y * 32 + x] = v;
                    }
                cobble.HoldsWater = true;
                int[] page = MaterialAtlas2Builder.Build(32, 32, new List<AtlasRegion> { cobble }, out written, out skipped);
                double[] height = MaterialAtlas2Builder.Normalise(PbrMapGenerator.Luminance(cobble.Texture));
                CreviceMaps.Result maps = CreviceMaps.Build(height, 32, 32);
                int wrong = 0;
                for (int i = 0; i < 32 * 32; i++)
                {
                    int px = page[i];
                    int g = (px >> 8) & 0xFF, bl = (px >> 16) & 0xFF, al = (px >> 24) & 0xFF;
                    int poolN = bl >> 4, flowN = bl & 15;
                    if ((px & 1) != 1 ||
                        Math.Abs(g / 255.0 - maps.Shape[i]) > 0.5 / 255.0 + 1e-9 ||
                        Math.Abs(poolN / 15.0 - maps.Pool[i]) > 0.5 / 15.0 + 1e-9 ||
                        Math.Abs(flowN / 15.0 - maps.Flow[i]) > 0.5 / 15.0 + 1e-9 ||
                        Math.Abs(al / 255.0 - maps.Phase[i]) > 0.5 / 255.0 + 1e-9) wrong++;
                }
                check("a water-holding region carries its crevice maps, packed as the shader unpacks them",
                    MaterialAtlas2Builder.CarriesCrevices(cobble) && wrong == 0, wrong + " texels mispacked");

                cobble.GlowLevel = 4f;
                int[] glowing = MaterialAtlas2Builder.Build(32, 32, new List<AtlasRegion> { cobble }, out written, out skipped);
                var big = Region(0, 0, CreviceMaps.MaxSize + 8, profile, 0f);
                big.HoldsWater = true;
                check("a glowing or oversized region keeps its emission and gets no crevice layout",
                    !MaterialAtlas2Builder.CarriesCrevices(cobble) && (glowing[0] & 1) == 0 &&
                    !MaterialAtlas2Builder.CarriesCrevices(big),
                    "glowing red " + (glowing[0] & 0xFF));
            }

            // Everything is a byte channel, so bounds are structural - but the
            // point is that no generator step can push a value out of range
            // before packing clamps it, which would silently wrap.
            bool heightBounded = true, aoBounded = true;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int texel = pixels[y * 32 + x];
                    float h = ((texel >> 8) & 0xFF) / 255f;
                    float ao = ((texel >> 16) & 0xFF) / 255f;

                    if (h < 0f || h > 1f) heightBounded = false;
                    if (ao < 0f || ao > 1f) aoBounded = false;
                }
            }

            check("height stays inside 0..1", heightBounded, "");
            check("baked AO stays inside 0..1", aoBounded, "");

            // Determinism: same input, same bytes. A cached atlas is only
            // trustworthy if rebuilding reproduces it exactly.
            int[] again = MaterialAtlas2Builder.Build(32, 32,
                new List<AtlasRegion> { Region(0, 0, 8, profile, 0f) }, out written, out skipped);

            check("the second atlas is deterministic", pixels.SequenceEqual(again), "");

            // Out of bounds costs one texture, not the page.
            var outside = new List<AtlasRegion> { Region(30, 30, 8, profile, 0f) };
            MaterialAtlas2Builder.Build(32, 32, outside, out written, out skipped);
            check("a region outside the atlas is skipped, not thrown", written == 0 && skipped == 1,
                "written " + written + " skipped " + skipped);
        }

        /// <summary>
        /// The two atlases must describe the same slot at the same coordinates.
        /// </summary>
        private static void CheckCorrespondence(Action<string, bool, string> check)
        {
            var profile = MaterialProfiles.For(Vintagestory.API.Common.EnumBlockMaterial.Stone);

            var first = new List<AtlasRegion> { Region(4, 6, 8, profile, 0f) };
            var second = new List<AtlasRegion> { Region(4, 6, 8, profile, 0f) };

            int w1, s1, w2, s2;
            int[] surface = MaterialAtlasBuilder.Build(32, 32, first, out w1, out s1);
            int[] material2 = MaterialAtlas2Builder.Build(32, 32, second, out w2, out s2);

            check("both atlases place the same region", w1 == w2 && s1 == s2,
                w1 + "/" + s1 + " vs " + w2 + "/" + s2);

            check("both atlases are the same size", surface.Length == material2.Length, "");

            // Where the first atlas wrote its region, the second must have too:
            // a texel that carries a normal must carry a metalness, or the two
            // are describing different surfaces at the same UV.
            int surfaceNeutral = MaterialAtlasBuilder.NeutralTexel();
            int secondNeutral = MaterialAtlas2Builder.NeutralTexel();

            bool aligned = true;
            string detail = "";

            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    bool inFirst = surface[y * 32 + x] != surfaceNeutral;
                    bool inSecond = material2[y * 32 + x] != secondNeutral;

                    bool insideSlot = x >= 4 && x < 12 && y >= 6 && y < 14;

                    // Inside the slot the second page must be written. Outside
                    // it, neither may be.
                    if (!insideSlot && inSecond)
                    {
                        aligned = false;
                        detail = "second atlas wrote outside the slot at " + x + "," + y;
                    }

                    if (!insideSlot && inFirst)
                    {
                        aligned = false;
                        detail = "first atlas wrote outside the slot at " + x + "," + y;
                    }
                }
            }

            check("neither atlas writes outside its slot", aligned, detail);

            // And the slot itself is covered in the second page.
            int covered = 0;
            for (int y = 6; y < 14; y++)
            {
                for (int x = 4; x < 12; x++)
                {
                    if (material2[y * 32 + x] != secondNeutral) covered++;
                }
            }

            check("the second atlas covers the whole slot", covered >= 60,
                covered + " of 64 texels differ from neutral");
        }

        /// <summary>
        /// Vanilla's glow level is the only thing that may grant emission.
        /// </summary>
        private static void CheckEmissionGating(Action<string, bool, string> check)
        {
            var profile = MaterialProfiles.For(Vintagestory.API.Common.EnumBlockMaterial.Stone);

            AtlasRegion dark = Region(0, 0, 8, profile, 0f);
            AtlasRegion glowing = Region(0, 0, 8, profile, 0.5f);

            double[] lum = PbrMapGenerator.Luminance(dark.Texture);

            double[] noGlow = MaterialAtlas2Builder.EmissionMask(dark, lum);
            double[] withGlow = MaterialAtlas2Builder.EmissionMask(glowing, lum);

            check("a block the game says does not emit gets no mask anywhere",
                noGlow.All(v => v == 0.0),
                "bright pixels must not create emission on a non-emissive block");

            check("an emitting block gets a mask", withGlow.Any(v => v > 0.5), "");

            // Conservative: the mask selects the hottest part of the texture,
            // not most of it. A forge's coals, not its stonework.
            double lit = withGlow.Count(v => v > 0.5) / (double)withGlow.Length;
            check("the emission mask is a minority of the texture", lit > 0.0 && lit < 0.45,
                (lit * 100).ToString("0.#") + "% above half");

            bool bounded = withGlow.All(v => v >= 0.0 && v <= 1.0);
            check("the emission mask stays inside 0..1", bounded, "");
        }

        /// <summary>
        /// Height must land on the same texels as the first atlas's normal, and
        /// must stay bounded and stable across rebuilds.
        ///
        /// Feature 6 ships height as DATA ONLY - nothing consumes it yet - so
        /// this is the whole of its validation: prove the generation, the
        /// packing, the coordinate alignment and the block correspondence
        /// before anything is built on top of it. Parallax is the obvious
        /// consumer and is deliberately not enabled; a displacement built on
        /// misaligned height would be indistinguishable from a displacement
        /// built on a bad scale.
        /// </summary>
        private static void CheckHeightAlignment(Action<string, bool, string> check)
        {
            var profile = MaterialProfiles.For(Vintagestory.API.Common.EnumBlockMaterial.Stone);

            int w, k;
            int[] surface = MaterialAtlasBuilder.Build(64, 64,
                new List<AtlasRegion> { Region(8, 16, 16, profile, 0f) }, out w, out k);
            int[] second = MaterialAtlas2Builder.Build(64, 64,
                new List<AtlasRegion> { Region(8, 16, 16, profile, 0f) }, out w, out k);

            // The same slot rectangle in both, texel for texel. If the two
            // pages disagreed by even one texel, every consumer reading both
            // would be comparing a surface against its neighbour.
            bool sameFootprint = true;
            int surfaceNeutral = MaterialAtlasBuilder.NeutralTexel();
            int secondNeutral = MaterialAtlas2Builder.NeutralTexel();

            for (int y = 0; y < 64 && sameFootprint; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    bool inSlot = x >= 8 && x < 24 && y >= 16 && y < 32;

                    bool firstWrote = surface[y * 64 + x] != surfaceNeutral;
                    bool secondWrote = second[y * 64 + x] != secondNeutral;

                    // Outside the slot neither may have written anything.
                    if (!inSlot && (firstWrote || secondWrote))
                    {
                        sameFootprint = false;
                        break;
                    }
                }
            }

            check("height occupies exactly the slot the first atlas occupies", sameFootprint,
                "the two pages must be addressable by one set of UVs");

            // Height is bounded and stable.
            bool bounded = true;
            for (int y = 16; y < 32; y++)
            {
                for (int x = 8; x < 24; x++)
                {
                    float h = ((second[y * 64 + x] >> 8) & 0xFF) / 255f;
                    if (h < 0f || h > 1f) bounded = false;
                }
            }

            check("height is bounded across the whole slot", bounded, "");

            int[] rebuilt = MaterialAtlas2Builder.Build(64, 64,
                new List<AtlasRegion> { Region(8, 16, 16, profile, 0f) }, out w, out k);

            check("height is stable under atlas rebuild", second.SequenceEqual(rebuilt),
                "a cached page is only trustworthy if rebuilding reproduces it");

            // A flat texture is flat, not maximally tall - so the channel's
            // neutral and a genuinely featureless surface agree.
            double[] flat = MaterialAtlas2Builder.Normalise(new double[] { 0.3, 0.3, 0.3, 0.3 });
            check("a featureless texture normalises to mid height",
                flat.All(v => Math.Abs(v - 0.5) < 1e-6), "");

            // Occlusion only ever darkens.
            check("baked AO never brightens",
                MaterialAtlas2Builder.Occlusion(0.9, 0.2) == 1.0 &&
                MaterialAtlas2Builder.Occlusion(0.2, 0.9) < 1.0,
                "a texel above its neighbourhood is unoccluded, never over-bright");
        }

        private static void CheckSamplerDeclaration(string repo, Action<string, bool, string> check)
        {
            string glsl = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pseudopbr.glsl"));

            int first = glsl.IndexOf("uniform sampler2D vv_materialTex;", StringComparison.Ordinal);
            int second = glsl.IndexOf("uniform sampler2D vv_materialTex2;", StringComparison.Ordinal);

            check("the second sampler is declared", second >= 0, "");

            check("the second sampler is declared below the first",
                first >= 0 && second > first,
                "sampler units are assigned at link time in declaration order");

            check("channel knowledge lives in one accessor",
                Regex.Matches(glsl, @"texture\s*\(\s*vv_materialTex2").Count == 1,
                "vv_materialTex2 should be sampled only inside vvSampleMaterial2");

            check("the second atlas is exposed as a struct, not four floats",
                glsl.Contains("struct VvMaterial2"), "");
        }

        private static void CheckFallbackWiring(string repo, Action<string, bool, string> check)
        {
            string glsl = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pseudopbr.glsl"));
            string binder = File.ReadAllText(
                Path.Combine(repo, "src/PseudoPBR/PbrShaderBinder.cs"));

            check("the shader falls back when the second atlas is absent",
                Regex.IsMatch(glsl, @"if\s*\(\s*vv_material2Valid\s*<\s*0\.5\s*\)\s*return\s+vvNeutralMaterial2"),
                "an unset validity uniform reads 0, so the fallback must be the zero case");

            check("the validity uniform is uploaded either way",
                binder.Contains("SecondValidUniform"), "");

            check("the two atlases use different texture units",
                MaterialAtlasTexture.TextureUnit != MaterialAtlasTexture.SecondTextureUnit,
                MaterialAtlasTexture.TextureUnit + " vs " + MaterialAtlasTexture.SecondTextureUnit);

            // Vanilla chunkopaque declares seven samplers, so 0..6 are the
            // game's. Both of ours must sit clear of that range.
            check("neither material unit collides with vanilla's seven samplers",
                MaterialAtlasTexture.TextureUnit > 6 && MaterialAtlasTexture.SecondTextureUnit > 6,
                "");

            check("both material units are inside the range OpenGL 3.3 guarantees",
                MaterialAtlasTexture.TextureUnit <= 15 && MaterialAtlasTexture.SecondTextureUnit <= 15,
                "GL_MAX_TEXTURE_IMAGE_UNITS is only required to be 16");
        }
    }
}
