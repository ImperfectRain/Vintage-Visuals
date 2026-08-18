using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Common;
using VintageVisuals.PseudoPBR;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Exercises the material atlas: packing, assembly, fingerprinting and the
    /// disk cache.
    ///
    /// The atlas is the one place where a mistake is both invisible and
    /// expensive — a texture placed one pixel off, or a stale cache silently
    /// accepted, would show up as "the world looks slightly wrong" with no log
    /// line to follow. All of it is pure array work, so none of it needs a
    /// client to check.
    /// </summary>
    public static class MaterialAtlasChecks
    {
        public static void Run(Action<string, bool, string> check)
        {
            Action<string, bool> ok = (name, condition) => check(name, condition, "");

            // --- channel packing round-trips ---
            int packed = MaterialAtlasBuilder.Pack(0.0f, 0.25f, 0.5f, 1.0f);
            ok("pack writes R in the low byte", (packed & 0xFF) == 0);
            ok("pack writes G in the second byte", ((packed >> 8) & 0xFF) == 64);
            ok("pack writes B in the third byte", ((packed >> 16) & 0xFF) == 128);
            ok("pack writes A in the high byte", ((packed >> 24) & 0xFF) == 255);

            int clamped = MaterialAtlasBuilder.Pack(-5f, 5f, float.NaN, 0.5f);
            ok("pack clamps out-of-range and NaN",
                (clamped & 0xFF) == 0 && ((clamped >> 8) & 0xFF) == 255 && ((clamped >> 16) & 0xFF) == 0);

            // A neutral texel must shade like ordinary matte surface, because
            // every atlas gap is filled with it.
            int neutral = MaterialAtlasBuilder.NeutralTexel();
            ok("neutral texel is a flat normal",
                (neutral & 0xFF) == 128 && ((neutral >> 8) & 0xFF) == 128);
            ok("neutral texel has no specular", ((neutral >> 24) & 0xFF) == 0);

            // --- assembly ---
            const int atlasW = 64, atlasH = 32;
            List<AtlasRegion> regions = new List<AtlasRegion>
            {
                MakeRegion(0, 0, 16, 16, EnumBlockMaterial.Metal, "metal"),
                MakeRegion(16, 0, 16, 16, EnumBlockMaterial.Soil, "soil"),
            };

            int written, skipped;
            int[] atlas = MaterialAtlasBuilder.Build(atlasW, atlasH, regions, out written, out skipped);

            ok("atlas is the requested size", atlas.Length == atlasW * atlasH);
            ok("both regions were written", written == 2 && skipped == 0);

            int gap = atlas[(atlasH - 1) * atlasW + (atlasW - 1)];
            ok("untouched atlas area stays neutral", gap == neutral);

            int metalTexel = atlas[8 * atlasW + 8];
            int soilTexel = atlas[8 * atlasW + 24];
            int metalSpec = (metalTexel >> 24) & 0xFF;
            int soilSpec = (soilTexel >> 24) & 0xFF;
            int metalRough = (metalTexel >> 16) & 0xFF;
            int soilRough = (soilTexel >> 16) & 0xFF;

            check("metal is written far more specular than soil", metalSpec > soilSpec + 60,
                "metal=" + metalSpec + " soil=" + soilSpec);
            check("metal is written smoother than soil", metalRough < soilRough,
                "metal=" + metalRough + " soil=" + soilRough);

            // --- bounds are survivable, not fatal ---
            var outside = new List<AtlasRegion> { MakeRegion(atlasW - 4, 0, 16, 16, EnumBlockMaterial.Stone, "oob") };
            MaterialAtlasBuilder.Build(atlasW, atlasH, outside, out written, out skipped);
            ok("a region outside the atlas is skipped, not thrown", written == 0 && skipped == 1);

            var nullRegion = new List<AtlasRegion> { null };
            MaterialAtlasBuilder.Build(atlasW, atlasH, nullRegion, out written, out skipped);
            ok("a null region is skipped", written == 0 && skipped == 1);

            // --- fingerprint sensitivity: the cache's whole correctness ---
            ulong baseline = MaterialAtlasBuilder.Fingerprint(atlasW, atlasH, regions, 1);
            ok("fingerprint is stable for identical input",
                MaterialAtlasBuilder.Fingerprint(atlasW, atlasH, regions, 1) == baseline);
            ok("fingerprint changes with format version",
                MaterialAtlasBuilder.Fingerprint(atlasW, atlasH, regions, 2) != baseline);
            ok("fingerprint changes with atlas size",
                MaterialAtlasBuilder.Fingerprint(atlasW * 2, atlasH, regions, 1) != baseline);

            var movedRegions = new List<AtlasRegion>
            {
                MakeRegion(0, 0, 16, 16, EnumBlockMaterial.Metal, "metal"),
                MakeRegion(16, 16, 16, 16, EnumBlockMaterial.Soil, "soil"),
            };
            ok("fingerprint changes when a texture moves in the atlas",
                MaterialAtlasBuilder.Fingerprint(atlasW, atlasH, movedRegions, 1) != baseline);

            var retunedRegions = new List<AtlasRegion>
            {
                MakeRegion(0, 0, 16, 16, EnumBlockMaterial.Metal, "metal"),
                MakeRegion(16, 0, 16, 16, EnumBlockMaterial.Stone, "soil"),
            };
            ok("fingerprint changes when a material profile changes",
                MaterialAtlasBuilder.Fingerprint(atlasW, atlasH, retunedRegions, 1) != baseline);

            var repainted = new List<AtlasRegion>
            {
                MakeRegion(0, 0, 16, 16, EnumBlockMaterial.Metal, "metal"),
                MakeRegion(16, 0, 16, 16, EnumBlockMaterial.Soil, "soil"),
            };
            repainted[1].Texture.R[0] = 0.123456;
            ok("fingerprint changes when source pixels change",
                MaterialAtlasBuilder.Fingerprint(atlasW, atlasH, repainted, 1) != baseline);

            // --- cache round trip ---
            string directory = Path.Combine(Path.GetTempPath(), "vv-atlas-check-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "atlas.bin");

            try
            {
                MaterialAtlasCache.Save(path, atlasW, atlasH, baseline, atlas);
                MaterialAtlasCache.CachedAtlas loaded = MaterialAtlasCache.TryLoad(path, baseline);

                ok("cache round-trips", loaded != null);
                if (loaded != null)
                {
                    ok("cache preserves dimensions", loaded.Width == atlasW && loaded.Height == atlasH);

                    bool identical = loaded.Pixels.Length == atlas.Length;
                    for (int i = 0; identical && i < atlas.Length; i++)
                    {
                        if (loaded.Pixels[i] != atlas[i]) identical = false;
                    }
                    ok("cache preserves every pixel", identical);
                }

                ok("cache is rejected when the fingerprint differs",
                    MaterialAtlasCache.TryLoad(path, baseline ^ 1UL) == null);

                // A truncated file must read as "rebuild", not throw.
                byte[] whole = File.ReadAllBytes(path);
                File.WriteAllBytes(path, Trim(whole, whole.Length / 2));
                ok("a truncated cache is rejected rather than throwing",
                    MaterialAtlasCache.TryLoad(path, baseline) == null);

                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
                ok("a garbage cache is rejected", MaterialAtlasCache.TryLoad(path, baseline) == null);

                ok("a missing cache is simply absent",
                    MaterialAtlasCache.TryLoad(Path.Combine(directory, "nope.bin"), baseline) == null);

                // --- preview images ---
                string[] previews = AtlasPreview.WriteAll(directory, atlasW, atlasH, atlas);
                ok("three preview images are written", previews.Length == 3);

                bool allPresent = true;
                foreach (string preview in previews)
                {
                    var info = new FileInfo(preview);
                    // PNG: signature + chunks; must at least exceed the raw pixel count.
                    if (!info.Exists || info.Length < atlasW * atlasH) allPresent = false;
                }
                ok("preview images are complete PNGs", allPresent);
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch (Exception) { }
            }
        }

        private static byte[] Trim(byte[] source, int length)
        {
            var result = new byte[length];
            Array.Copy(source, result, length);
            return result;
        }

        /// <summary>A deterministic patterned texture, so results are reproducible.</summary>
        private static AtlasRegion MakeRegion(int x, int y, int width, int height,
                                              EnumBlockMaterial material, string name)
        {
            var texture = new LinearTexture
            {
                Width = width,
                Height = height,
                R = new double[width * height],
                G = new double[width * height],
                B = new double[width * height],
                Alpha = new double[width * height],
            };

            for (int py = 0; py < height; py++)
            {
                for (int px = 0; px < width; px++)
                {
                    int i = py * width + px;
                    double v = 0.5 + 0.3 * Math.Sin(px * 0.6 + py * 0.4);
                    texture.R[i] = v;
                    texture.G[i] = v;
                    texture.B[i] = v;
                    texture.Alpha[i] = 1.0;
                }
            }

            return new AtlasRegion
            {
                X = x,
                Y = y,
                Texture = texture,
                Profile = MaterialProfiles.For(material),
                Name = name,
            };
        }
    }
}
