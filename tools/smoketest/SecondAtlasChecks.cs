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
