using System;
using System.IO;
using System.Text.RegularExpressions;

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
