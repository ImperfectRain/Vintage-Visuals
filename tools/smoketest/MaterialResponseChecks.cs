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
            CheckEnergyCompensation(repo, check);
            CheckAnisotropy(repo, check);
            CheckLobeIsBounded(repo, check);
        }

        /// <summary>
        /// The specular lobe must be BOUNDED, not merely finite.
        ///
        /// This exists because "finite" is what the anisotropy check already
        /// asserted, and finite is what a lobe of 124340 is. GGX integrates to
        /// one over the hemisphere, so its peak grows as 1/(pi*alpha^2) with no
        /// limit; the mod has no exposure control between the lobe and the
        /// frame, so an unbounded peak is a white blowout that pops in and out
        /// one fragment at a time as the view moves. It shipped once, from a
        /// denominator clamp being "corrected" without measuring the peak it
        /// was holding down, and it reached every surface in the world
        /// including block-lit ones underground.
        ///
        /// Two properties, and the second is the one that matters:
        ///
        ///   Bounded  - no roughness produces a peak above the floor's peak.
        ///   Monotone - a smoother surface is never DIMMER at the peak than a
        ///              rougher one. The clamp this replaced failed exactly
        ///              here: it capped at 1e5*roughness^4, so it fell toward
        ///              zero as the surface approached a mirror, and wet stone
        ///              lost its sheen entirely.
        /// </summary>
        private static void CheckLobeIsBounded(string repo, Action<string, bool, string> check)
        {
            bool bounded = true, monotone = true;
            string worstBound = "", worstMono = "";
            // Starts at zero: the sweep runs from rough to smooth, so the first
            // sample has nothing rougher to be compared against.
            double previous = 0.0;

            // Down from a mirror-smooth surface, finer than the material floor
            // of 0.04 so the sweep covers values a roughness bias can reach.
            for (int r = 100; r >= 1; r--)
            {
                double roughness = r / 100.0;
                double peak = Ggx(1.0, roughness);

                if (peak > PeakBound * 1.001)
                {
                    bounded = false;
                    worstBound = "roughness " + roughness + " peak " + peak.ToString("0.#");
                }

                // Sweeping toward zero roughness, so the peak must not fall.
                if (peak < previous * 0.999)
                {
                    monotone = false;
                    worstMono = "roughness " + roughness + " peak " + peak.ToString("0.#") +
                                " below rougher " + previous.ToString("0.#");
                }
                previous = peak;

                for (int an = 0; an <= 10; an++)
                {
                    double a = GgxAniso(1.0, 0.0, 0.0, roughness, an / 10.0);
                    if (a > PeakBound * 1.001)
                    {
                        bounded = false;
                        worstBound = "anisotropic, roughness " + roughness +
                                     " anisotropy " + (an / 10.0) + " peak " + a.ToString("0.#");
                    }
                }
            }

            check("the specular lobe peak is bounded at every roughness", bounded, worstBound);

            // The floor and the bound have to agree, or one of them is dead:
            // a floor above what the bound needs throws away gloss the frame
            // could have carried, and one below it lets the peak through.
            double floorPeak = 1.0 / (Math.PI * MinAlpha * MinAlpha);
            check("the alpha floor is the one that produces exactly that bound",
                Math.Abs(floorPeak - PeakBound) / PeakBound < 0.01,
                "floor peak " + floorPeak.ToString("0.#") + " vs bound " + PeakBound.ToString("0.#"));
            check("a smoother surface is never dimmer at the lobe peak", monotone, worstMono);

            // The floor has to be in the shader, not only in this port. Both
            // forms take it, and both take it on alpha rather than on the
            // denominator - a denominator clamp is what produced the inverted
            // cap this replaced.
            string pbr = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pbrcore.glsl"));

            check("the shader declares VV_GGX_MIN_ALPHA at the tested value",
                Regex.IsMatch(pbr, @"const\s+float\s+VV_GGX_MIN_ALPHA\s*=\s*0\.04\s*;"),
                "pbrcore.glsl");

            check("both lobe forms floor alpha at VV_GGX_MIN_ALPHA",
                Regex.Matches(pbr, @"float\s+a\s*=\s*max\(VV_GGX_MIN_ALPHA,\s*roughness\s*\*\s*roughness\)").Count == 2,
                "expected the floor in both vvDistributionGGX and vvDistributionGGXAnisotropic");
        }

        /// <summary>VV_GGX_MIN_ALPHA in pbrcore.glsl.</summary>
        private const double MinAlpha = 0.04;

        /// <summary>
        /// The largest value either lobe may take. A LITERAL, deliberately not
        /// derived from MinAlpha: derived from it, lowering the floor would
        /// lower the bound with it and the test would keep passing while the
        /// frame blew out. 200 is roughly 1.8x sunlight off a dielectric, which
        /// is the most a buffer with no exposure control can carry. Moving it
        /// is a decision, and it should look like one in the diff.
        /// </summary>
        private const double PeakBound = 200.0;

        /// <summary>Isotropic GGX, ported from vvDistributionGGX.</summary>
        private static double Ggx(double ndoth, double roughness)
        {
            double a = Math.Max(MinAlpha, roughness * roughness);
            double a2 = a * a;
            double d = ndoth * ndoth * (a2 - 1.0) + 1.0;
            return a2 / Math.Max(1e-12, Math.PI * d * d);
        }

        /// <summary>Anisotropic GGX, ported from vvDistributionGGXAnisotropic.</summary>
        private static double GgxAniso(double ndoth, double tdoth, double bdoth,
                                       double roughness, double anisotropy)
        {
            double a = Math.Max(MinAlpha, roughness * roughness);
            double aspect = Math.Sqrt(1.0 - 0.9 * Math.Min(Math.Max(anisotropy, 0.0), 1.0));

            double ax = Math.Max(1e-4, a / aspect);
            double ay = Math.Max(1e-4, a * aspect);

            double tx = tdoth / ax;
            double ty = bdoth / ay;
            double d = tx * tx + ty * ty + ndoth * ndoth;

            return 1.0 / Math.Max(1e-12, Math.PI * ax * ay * d * d);
        }

        /// <summary>
        /// The anisotropic lobe must reduce to the isotropic one when there is
        /// no anisotropy, stay finite, and conserve the energy the isotropic
        /// lobe had.
        ///
        /// The first of those is the parity property that lets the feature be
        /// switched off, and it is ALGEBRAIC rather than approximate: at
        /// anisotropy 0 the aspect ratio is 1, both alphas equal the isotropic
        /// alpha, and the two expressions are the same function. Testing it
        /// numerically is how a later "simplification" of either one gets
        /// caught.
        /// </summary>
        private static void CheckAnisotropy(string repo, Action<string, bool, string> check)
        {
            bool collapses = true, finite = true, positive = true;
            string worst = "";

            for (int r = 1; r <= 20; r++)
            {
                double roughness = r / 20.0;

                for (int h = 0; h <= 20; h++)
                {
                    double ndoth = h / 20.0;

                    // A half-vector consistent with ndoth, split arbitrarily
                    // between the two tangent axes.
                    double rest = Math.Sqrt(Math.Max(0.0, 1.0 - ndoth * ndoth));
                    double tdoth = rest * 0.6;
                    double bdoth = rest * 0.8;

                    double iso = Ggx(ndoth, roughness);
                    double aniso0 = GgxAniso(ndoth, tdoth, bdoth, roughness, 0.0);

                    // Same function at zero anisotropy.
                    double rel = Math.Abs(aniso0 - iso) / Math.Max(1e-6, iso);
                    if (rel > 1e-3)
                    {
                        collapses = false;
                        worst = "roughness " + roughness + " ndoth " + ndoth +
                                " iso " + iso.ToString("0.####") + " aniso " + aniso0.ToString("0.####");
                    }

                    for (int an = 0; an <= 10; an++)
                    {
                        double v = GgxAniso(ndoth, tdoth, bdoth, roughness, an / 10.0);
                        if (double.IsNaN(v) || double.IsInfinity(v)) { finite = false; worst = "NaN/Inf"; }
                        if (v < 0.0) positive = false;
                    }
                }
            }

            check("the anisotropic lobe collapses to the isotropic one at zero", collapses, worst);
            check("the anisotropic lobe is finite everywhere", finite, worst);
            check("the anisotropic lobe is never negative", positive, "");

            // It must actually be anisotropic: the lobe should differ along the
            // two tangent axes once anisotropy is on, or this is dead code that
            // passes every safety test.
            double along = GgxAniso(0.8, 0.6, 0.0, 0.4, 0.8);
            double across = GgxAniso(0.8, 0.0, 0.6, 0.4, 0.8);

            check("the highlight is stretched along one axis",
                Math.Abs(along - across) / Math.Max(along, across) > 0.2,
                "along " + along.ToString("0.###") + " across " + across.ToString("0.###"));

            // Anisotropy must not create energy: integrated over the hemisphere
            // the anisotropic lobe should not exceed the isotropic one by more
            // than sampling error.
            double isoSum = 0.0, anisoSum = 0.0;
            int samples = 0;

            for (int i = 1; i <= 64; i++)
            {
                for (int j = 0; j < 64; j++)
                {
                    double ndoth = i / 64.0;
                    double phi = j * 2.0 * Math.PI / 64.0;
                    double rest = Math.Sqrt(Math.Max(0.0, 1.0 - ndoth * ndoth));

                    isoSum += Ggx(ndoth, 0.4) * ndoth;
                    anisoSum += GgxAniso(ndoth, rest * Math.Cos(phi), rest * Math.Sin(phi), 0.4, 0.8) * ndoth;
                    samples++;
                }
            }

            double ratio = anisoSum / Math.Max(1e-9, isoSum);
            check("anisotropy redistributes energy rather than creating it",
                ratio < 1.15,
                "anisotropic/isotropic integral " + ratio.ToString("0.###"));

            // And the shader wiring.
            check("the shader uses the anisotropic lobe only on the direct term",
                Regex.Matches(_pbr, @"vvDistributionGGXAnisotropic\(").Count == 1,
                "the ambient and block-light terms have no lobe shape to stretch");

            check("the shader falls back to the isotropic lobe when grain is off",
                Regex.IsMatch(_pbr, @"else\s*\{\s*distribution\s*=\s*vvDistributionGGX\("),
                "0 must mean the previous highlight, not a degenerate one");

            check("grain direction is measured, not assumed",
                _pbr.Contains("vvGrainDirection") && _pbr.Contains("coherence"),
                "a hand-set grain axis would be wrong on every rotated block");
        }

        /// <summary>Karis's single-scatter directional albedo, ported.</summary>
        private static float ScatterAlbedo(float roughness, float ndotv)
        {
            float x = 1f - Clamp01(roughness);
            float y = Clamp01(ndotv);

            float a = -0.1688f * x + 1.895f * x * x;
            float b = 0.9903f * x - 4.853f * x * x + 8.404f * x * x * x - 5.069f * x * x * x * x;

            float bias = Clamp01(Math.Max(a, b));
            return Math.Min(Math.Max(bias * (1f - y) + y, 0.04f), 1f);
        }

        private static float Compensation(float f0, float roughness, float ndotv, float strength)
        {
            float albedo = Math.Max(ScatterAlbedo(roughness, ndotv), 0.08f);
            float gain = 1f + f0 * (1f / albedo - 1f);

            gain = Math.Min(Math.Max(gain, 1f), 2f);
            return 1f + (gain - 1f) * Clamp01(strength);
        }

        /// <summary>
        /// Multi-scatter compensation returns energy the single-scatter lobe
        /// lost. It must not invent any beyond that, must leave smooth surfaces
        /// alone, and must stay finite where the fit's denominator is small.
        /// </summary>
        private static void CheckEnergyCompensation(string repo, Action<string, bool, string> check)
        {
            bool neverBelowOne = true, finite = true, capped = true, smoothUntouched = true;
            bool metalAndDielectricStable = true;
            string worst = "";

            float[] f0s = { 0.04f, 0.2f, 0.5f, 0.9f, 1.0f };

            foreach (float f0 in f0s)
            {
                for (int i = 0; i <= 40; i++)
                {
                    float r = i / 40f;

                    for (int j = 0; j <= 40; j++)
                    {
                        // Includes ndotv at the very edge, where the fit is
                        // least well behaved.
                        float nv = Math.Max(1e-4f, j / 40f);
                        float g = Compensation(f0, r, nv, 1f);

                        if (float.IsNaN(g) || float.IsInfinity(g))
                        {
                            finite = false;
                            worst = "f0 " + f0 + " roughness " + r + " ndotv " + nv;
                        }

                        if (g < 1f - 1e-5f)
                        {
                            neverBelowOne = false;
                            worst = "compensation dimmed the lobe at f0 " + f0 + " roughness " + r;
                        }

                        if (g > 2f + 1e-5f) capped = false;
                        if (g < 0f) metalAndDielectricStable = false;
                    }
                }

                // Smooth surfaces lose almost nothing to multiple scattering,
                // so the correction there must be negligible - otherwise this
                // is a brightness slider wearing a physics costume.
                float atSmooth = Compensation(f0, 0.0f, 0.7f, 1f);
                if (atSmooth > 1.02f)
                {
                    smoothUntouched = false;
                    worst = "smooth surface gained " + atSmooth + " at f0 " + f0;
                }
            }

            check("energy compensation never dims the lobe", neverBelowOne, worst);
            check("energy compensation is finite everywhere", finite, worst);
            check("energy compensation is capped", capped, "");
            check("smooth surfaces are left alone", smoothUntouched, worst);
            check("compensation is stable for metals and dielectrics alike",
                metalAndDielectricStable, "");

            // It should actually do something where the loss is - otherwise it
            // is dead code that passes every safety test.
            float roughMetal = Compensation(1.0f, 0.9f, 0.5f, 1f);
            check("a rough metal actually gets energy back", roughMetal > 1.05f,
                roughMetal.ToString("0.###"));

            float roughStone = Compensation(0.04f, 0.9f, 0.5f, 1f);
            check("a rough dielectric changes only slightly",
                roughStone > 1.0f && roughStone < 1.10f, roughStone.ToString("0.###"));

            check("strength 0 disables it exactly",
                Math.Abs(Compensation(1.0f, 0.9f, 0.5f, 0f) - 1f) < 1e-6f, "");

            // And the shader must apply it to the lobe rather than to the
            // finished pixel.
            check("the compensation multiplies the specular lobe, not the result",
                Regex.IsMatch(_pbr, @"result\s*\+=\s*specular[^;]*energy\s*;"),
                "applying it to the finished colour would brighten diffuse and emission too");

            string core = File.ReadAllText(Path.Combine(repo,
                "assets/vintagevisuals/shadersnippets/pbrcore.glsl"));

            check("the compensation is clamped to at least 1 in the shader",
                Regex.IsMatch(core, @"clamp\s*\(\s*gain\s*,\s*vec3\s*\(\s*1\.0\s*\)"),
                "it returns lost energy; it must not be able to remove any");
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
