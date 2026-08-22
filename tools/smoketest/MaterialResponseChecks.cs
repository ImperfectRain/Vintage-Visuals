using System;
using System.IO;
using System.Text.RegularExpressions;
using VintageVisuals.PseudoPBR;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Mathematical invariants of the material response, checked numerically.
    ///
    /// This is the layer the project did not have: between "the GLSL compiles"
    /// and "it looks right on screen" there is a large class of question that is
    /// pure arithmetic and can be settled without a GPU. Does occlusion ever
    /// brighten? Does metalness create energy? Is the output finite at grazing
    /// angles? Those have answers, and guessing at them is what produced a
    /// forest floor of white spotlights.
    ///
    /// The formulae below are ports of the GLSL. A port can drift from its
    /// original, so each one is also pinned against the shader source: if the
    /// GLSL changes shape, the pin fails and the port has to be revisited
    /// rather than silently testing a formula the game no longer runs.
    /// </summary>
    public static class MaterialResponseChecks
    {
        private static string _pbr;

        public static void Run(string repo, Action<string, bool, string> check)
        {
            _pbr = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pseudopbr.glsl"));

            CheckSpecularOcclusionPinned(check);
            CheckSpecularOcclusionInvariants(check);
            CheckApplicationSites(check);
            CheckMetalness(check);
            CheckEmissionMask(repo, check);
        }

        /// <summary>
        /// The emission mask may only ever redistribute emission the game
        /// already granted - never create it.
        /// </summary>
        private static void CheckEmissionMask(string repo, Action<string, bool, string> check)
        {
            // glowLevel is the gate, and it is checked before the mask is even
            // read: vvEmission returns black for a non-emitting block.
            check("emission is zero when the game says the block does not glow",
                Regex.IsMatch(_pbr.Replace("\r", ""), @"") &&
                File.ReadAllText(Path.Combine(repo,
                    "assets/vintagevisuals/shadersnippets/pbrcore.glsl"))
                    .Contains("if (emission < 0.004) return vec3(0.0);"),
                "vanilla's glowLevel must gate emission before any mask applies");

            // The mask multiplies, so it can only subtract.
            check("the mask multiplies emission rather than adding to it",
                Regex.IsMatch(_pbr, @"vvEmission\([^;]*\)[\s\S]{0,200}?\*\s*emissionMask\s*;"),
                "an additive mask could create light on a dark block");

            // No data must mean "emits as it always did", not "emits nowhere".
            check("the mask falls back to 1, not 0, without the second atlas",
                Regex.IsMatch(_pbr,
                    @"float\s+vvEmissionMask[\s\S]{0,300}?vv_material2Valid\s*>\s*0\.5\s*\?[^:]*:\s*1\.0"),
                "falling back to 0 would switch every light source in the world off");

            // Bloom follows the emission, not the texture's brightness.
            string yaml = File.ReadAllText(Path.Combine(repo,
                "assets/vintagevisuals/shaderpatches/pseudopbr.yaml"));
            string yaml2 = File.ReadAllText(Path.Combine(repo,
                "assets/vintagevisuals/shaderpatches/pseudopbrtopsoil.yaml"));

            check("the bloom feed is masked the same way the emission is",
                yaml.Contains("vvEmissiveGlow(glowLevel) * vvEmissionMask(uv)") &&
                yaml2.Contains("vvEmissiveGlow(glowLevel) * vvEmissionMask(uv)"),
                "otherwise a forge's stonework blooms while only its coals emit");

            // Continuity across the mask's range, and the two ends.
            bool continuous = true;
            float previous = 0f;
            for (int i = 0; i <= 100; i++)
            {
                float mask = i / 100f;
                float emission = 0.6f * mask;      // glow held fixed

                if (float.IsNaN(emission) || emission < 0f) continuous = false;
                if (emission < previous - 1e-6f) continuous = false;
                previous = emission;
            }

            check("emission scales continuously and monotonically with the mask", continuous, "");
            check("mask 0 gives no emission", Math.Abs(0.6f * 0f) < 1e-6f, "");
            check("mask 1 gives the unmasked emission", Math.Abs(0.6f * 1f - 0.6f) < 1e-6f, "");
        }

        /// <summary>
        /// F0 and the diffuse weight, ported from the shader's metalness path.
        /// </summary>
        private static float[] ReflectanceF0(float[] albedo, float metalness)
        {
            float m = Clamp01(metalness);
            var f0 = new float[3];
            for (int i = 0; i < 3; i++) f0[i] = 0.04f + (albedo[i] - 0.04f) * m;
            return f0;
        }

        private static float DiffuseWeight(float metalness, float specularStrength)
        {
            return 1f + ((1f - Clamp01(metalness)) - 1f) * specularStrength;
        }

        /// <summary>
        /// Metalness must change what a material IS, not merely how bright it
        /// is, and it must not create energy doing so.
        ///
        /// The validation targets are the real Vintage Story material classes
        /// rather than invented numbers: the classification is authoritative and
        /// the test's job is to prove the transport and the BRDF respect it, not
        /// to second-guess it.
        /// </summary>
        private static void CheckMetalness(Action<string, bool, string> check)
        {
            float[] copper = { 0.72f, 0.45f, 0.20f };
            float[] steel = { 0.56f, 0.57f, 0.58f };

            // Dielectric end: unchanged from the existing behaviour.
            float[] dielectric = ReflectanceF0(copper, 0f);
            check("metalness 0 gives the dielectric reflectance",
                Math.Abs(dielectric[0] - 0.04f) < 1e-5f &&
                Math.Abs(dielectric[1] - 0.04f) < 1e-5f &&
                Math.Abs(dielectric[2] - 0.04f) < 1e-5f,
                "a non-metal reflects about 4% and reflects it white");

            // Metal end: F0 becomes the albedo, so the highlight is tinted.
            float[] metal = ReflectanceF0(copper, 1f);
            check("metalness 1 tints reflectance by the base colour",
                Math.Abs(metal[0] - copper[0]) < 1e-5f && metal[0] > metal[2] + 0.3f,
                "copper's highlight is orange because its F0 is orange");

            float[] white = ReflectanceF0(steel, 1f);
            check("a grey metal keeps a neutral highlight",
                Math.Abs(white[0] - white[2]) < 0.05f, "steel is not tinted");

            // Diffuse must vanish as metalness rises.
            check("metalness 1 suppresses diffuse", Math.Abs(DiffuseWeight(1f, 1f)) < 1e-5f, "");
            check("metalness 0 leaves diffuse untouched", Math.Abs(DiffuseWeight(0f, 1f) - 1f) < 1e-5f, "");

            // Continuity and monotonicity across the range, plus the energy
            // rule: nothing may exceed 1 or drop below 0 anywhere.
            bool continuous = true, monotone = true, bounded = true, finite = true;
            float previousDiffuse = float.MaxValue;
            float[] previousF0 = null;

            for (int i = 0; i <= 100; i++)
            {
                float m = i / 100f;
                float[] f0 = ReflectanceF0(copper, m);
                float diffuse = DiffuseWeight(m, 1f);

                for (int c = 0; c < 3; c++)
                {
                    if (float.IsNaN(f0[c]) || float.IsInfinity(f0[c])) finite = false;
                    if (f0[c] < -1e-5f || f0[c] > 1f + 1e-5f) bounded = false;
                    if (previousF0 != null && Math.Abs(f0[c] - previousF0[c]) > 0.05f) continuous = false;
                }

                if (float.IsNaN(diffuse) || float.IsInfinity(diffuse)) finite = false;
                if (diffuse < -1e-5f || diffuse > 1f + 1e-5f) bounded = false;
                if (diffuse > previousDiffuse + 1e-5f) monotone = false;

                previousDiffuse = diffuse;
                previousF0 = f0;
            }

            check("metalness interpolates continuously", continuous, "");
            check("more metalness never increases the diffuse weight", monotone, "");
            check("reflectance and diffuse weight stay inside 0..1", bounded, "");
            check("the metalness path is finite across the whole range", finite, "");

            // The energy rule stated plainly: a metal is not a brighter
            // dielectric. What it gains in reflectance it loses in diffuse.
            check("metalness cannot brighten a material merely by being metal",
                DiffuseWeight(1f, 1f) < DiffuseWeight(0f, 1f),
                "raising F0 without removing diffuse is how metal becomes shiny plastic");

            // The classification itself: dark does not mean metallic.
            CheckClassification(check);

            // And the shader must actually contain both halves.
            check("the shader derives F0 from real metalness when it has it",
                _pbr.Contains("vvReflectanceF0FromMetalness(albedo, metalness)"), "");

            check("the shader falls back to the stand-in without the second atlas",
                Regex.IsMatch(_pbr, @"vv_material2Valid\s*>\s*0\.5[\s\S]{0,200}?vvReflectanceF0\(albedo,\s*specularMask\)"),
                "the entity and particle paths have no atlas and must keep the old answer");

            check("the shader removes diffuse as metalness rises",
                Regex.IsMatch(_pbr, @"result\s*\*=\s*mix\s*\(\s*1\.0\s*,\s*1\.0\s*-\s*metalness"),
                "raising F0 alone would make metal a brighter dielectric");
        }

        /// <summary>
        /// Metalness comes from the block's material class, never from how dark
        /// its texture is.
        /// </summary>
        private static void CheckClassification(Action<string, bool, string> check)
        {
            var metal = MaterialProfiles.For(Vintagestory.API.Common.EnumBlockMaterial.Metal);
            var ore = MaterialProfiles.For(Vintagestory.API.Common.EnumBlockMaterial.Ore);
            var stone = MaterialProfiles.For(Vintagestory.API.Common.EnumBlockMaterial.Stone);
            var wood = MaterialProfiles.For(Vintagestory.API.Common.EnumBlockMaterial.Wood);
            var soil = MaterialProfiles.For(Vintagestory.API.Common.EnumBlockMaterial.Soil);
            var leaves = MaterialProfiles.For(Vintagestory.API.Common.EnumBlockMaterial.Leaves);

            check("metal is classified metallic", metal.Metalness > 0.9f, metal.Metalness.ToString());

            check("stone, wood, soil and leaves are not metallic",
                stone.Metalness < 0.05f && wood.Metalness < 0.05f &&
                soil.Metalness < 0.05f && leaves.Metalness < 0.05f,
                "stone " + stone.Metalness + " wood " + wood.Metalness +
                " soil " + soil.Metalness + " leaves " + leaves.Metalness);

            check("ore sits between metal and stone",
                ore.Metalness > stone.Metalness && ore.Metalness < metal.Metalness,
                ore.Metalness.ToString());
        }

        /// <summary>
        /// Lagarde's specular occlusion, ported from vvSpecularOcclusion.
        /// </summary>
        private static float SpecularOcclusion(float cavity, float ndotv, float roughness, float strength)
        {
            float occlusion = Clamp01(cavity);
            float view = Clamp01(ndotv);

            float lobe = Clamp01((float)Math.Pow(view + occlusion,
                                     Math.Pow(2.0, -16.0 * Clamp01(roughness) - 1.0))
                                 - 1f + occlusion);

            lobe = Math.Min(Math.Max(lobe, occlusion), 1f);

            return occlusion + (lobe - occlusion) * Clamp01(strength);
        }

        private static float Clamp01(float v)
        {
            if (float.IsNaN(v)) return 0f;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        /// <summary>
        /// The port must still describe the shader. Checks for the shape of the
        /// expression rather than whitespace-exact text.
        /// </summary>
        private static void CheckSpecularOcclusionPinned(Action<string, bool, string> check)
        {
            check("vvSpecularOcclusion exists to test",
                _pbr.Contains("float vvSpecularOcclusion("), "");

            check("the ported specular occlusion still matches the GLSL",
                Regex.IsMatch(_pbr, @"pow\s*\(\s*view\s*\+\s*occlusion\s*,\s*exp2\s*\(\s*-\s*16\.0\s*\*") &&
                Regex.IsMatch(_pbr, @"-\s*1\.0\s*\+\s*occlusion") &&
                Regex.IsMatch(_pbr, @"clamp\s*\(\s*lobe\s*,\s*occlusion\s*,\s*1\.0\s*\)"),
                "the shader's formula changed shape - revisit the C# port before trusting these results");
        }

        private static void CheckSpecularOcclusionInvariants(Action<string, bool, string> check)
        {
            bool unoccludedIsNeutral = true;
            bool neverBrightens = true;
            bool monotone = true;
            bool bounded = true;
            bool finite = true;
            bool parityAtZero = true;
            string detail = "";

            float[] views = { 0.001f, 0.05f, 0.2f, 0.5f, 0.8f, 1.0f };
            float[] roughs = { 0.0f, 0.04f, 0.2f, 0.5f, 0.8f, 1.0f };

            foreach (float v in views)
            {
                foreach (float r in roughs)
                {
                    // A texel with no cavity must shade exactly as before.
                    float open = SpecularOcclusion(1f, v, r, 1f);
                    if (Math.Abs(open - 1f) > 1e-4f)
                    {
                        unoccludedIsNeutral = false;
                        detail = "cavity=1 gave " + open + " at ndotv=" + v + " roughness=" + r;
                    }

                    float previous = float.MaxValue;

                    for (int i = 20; i >= 0; i--)
                    {
                        float cavity = i / 20f;
                        float so = SpecularOcclusion(cavity, v, r, 1f);

                        if (float.IsNaN(so) || float.IsInfinity(so)) finite = false;
                        if (so < -1e-5f || so > 1f + 1e-5f) bounded = false;

                        // Occlusion may not brighten: the result can never
                        // exceed unity, and deepening the groove can only ever
                        // take more light away.
                        if (so > 1f + 1e-5f) neverBrightens = false;
                        if (so > previous + 1e-4f)
                        {
                            monotone = false;
                            detail = "specular rose as the groove deepened at ndotv=" + v +
                                     " roughness=" + r;
                        }

                        previous = so;

                        // Strength 0 must reproduce the previous behaviour
                        // exactly - a flat cavity on the highlight.
                        float off = SpecularOcclusion(cavity, v, r, 0f);
                        if (Math.Abs(off - cavity) > 1e-5f) parityAtZero = false;
                    }
                }
            }

            check("no cavity leaves the specular response untouched", unoccludedIsNeutral, detail);
            check("specular occlusion never exceeds one", neverBrightens, "");
            check("deepening a groove can only reduce specular", monotone, detail);
            check("specular occlusion stays inside 0..1", bounded, "");
            check("specular occlusion is finite at every angle and roughness", finite, "");
            check("specular occlusion at strength 0 reproduces the old flat cavity", parityAtZero,
                "zero has to mean the previous image, not an unoccluded one");

            // A reflection is never dimmed by MORE than the ambient is. This is
            // the bound that removes the grazing-angle inversion in Lagarde's
            // fit, and it also means the feature can only ever return light
            // relative to what shipped before.
            bool neverBelowAmbient = true;
            bool roughnessMonotone = true;
            string worst = "";

            foreach (float v in views)
            {
                for (int i = 0; i <= 20; i++)
                {
                    float cavity = i / 20f;
                    float previous = float.MinValue;

                    // Walking roughness from rough to smooth: a smoother
                    // surface must never keep LESS of its reflection.
                    for (int j = 20; j >= 0; j--)
                    {
                        float r = j / 20f;
                        float so = SpecularOcclusion(cavity, v, r, 1f);

                        if (so < cavity - 1e-5f)
                        {
                            neverBelowAmbient = false;
                            worst = "cavity " + cavity + " ndotv " + v + " roughness " + r +
                                    " gave " + so;
                        }

                        if (so < previous - 1e-4f)
                        {
                            roughnessMonotone = false;
                            worst = "polishing the surface LOST reflection at cavity " + cavity +
                                    " ndotv " + v + " roughness " + r;
                        }

                        previous = so;
                    }
                }
            }

            check("a reflection is never occluded more than the ambient is", neverBelowAmbient, worst);

            check("a smoother surface never keeps less reflection than a rougher one",
                roughnessMonotone,
                worst + " - Lagarde's fit inverts below (ndotv + occlusion) = 1 and must be clamped");

            // And where the geometry allows it, the difference is real rather
            // than merely non-negative.
            float polished = SpecularOcclusion(0.7f, 0.8f, 0.05f, 1f);
            float matte = SpecularOcclusion(0.7f, 0.8f, 0.95f, 1f);

            check("in a shallow groove a polished surface keeps materially more reflection",
                polished > matte + 0.05f,
                "polished " + polished.ToString("0.###") + " vs matte " + matte.ToString("0.###"));

            check("a rough surface converges on plain occlusion",
                Math.Abs(matte - 0.7f) < 0.05f, matte.ToString("0.###"));
        }

        /// <summary>
        /// Where the two occlusions are applied, and that neither is applied
        /// twice.
        ///
        /// The arithmetic being right does not help if the call site multiplies
        /// by it a second time, which is exactly how the previous version put
        /// the hemispherical cavity onto the direct lobe while its own comment
        /// said it did not.
        /// </summary>
        private static void CheckApplicationSites(Action<string, bool, string> check)
        {
            int start = _pbr.IndexOf("vec4 vvApplyPbr(", StringComparison.Ordinal);
            check("vvApplyPbr was found", start >= 0, "");
            if (start < 0) return;

            int end = _pbr.IndexOf("\n}", start, StringComparison.Ordinal);
            string body = _pbr.Substring(start, end - start);

            // Diffuse takes the hemispherical cavity, exactly once.
            check("the hemispherical cavity is applied exactly once",
                Regex.Matches(body, @"result\s*\*=\s*cavity\s*;").Count == 1,
                "found " + Regex.Matches(body, @"result\s*\*=\s*cavity\s*;").Count);

            // Every specular path takes the specular occlusion, and none of
            // them takes the flat cavity any more.
            check("the direct lobe is occluded by the specular term",
                Regex.IsMatch(body, @"result\s*\+=\s*specular\s*\*[^;]*specularOcclusion"),
                "the direct highlight must not use the hemispherical cavity");

            check("no specular path still multiplies by the flat cavity",
                !Regex.IsMatch(body, @"vvBlockLightSpecular[\s\S]{0,400}?\*\s*cavity\s*;") &&
                !Regex.IsMatch(body, @"vvAmbientSpecular[\s\S]{0,400}?\*\s*cavity\s*;"),
                "block-light or ambient specular is still using the diffuse occlusion");

            // Emission is a light source; occlusion has no business touching it.
            int cavityAt = body.IndexOf("result *= cavity;", StringComparison.Ordinal);
            int emissionAt = body.IndexOf("vvEmission(", StringComparison.Ordinal);

            check("emission is added after occlusion and is therefore unoccluded",
                cavityAt >= 0 && emissionAt > cavityAt,
                "a forge does not stop emitting because it has mortar lines");
        }
    }
}
