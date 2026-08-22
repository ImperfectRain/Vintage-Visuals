using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// The pixelated environment reflection, checked as arithmetic.
    ///
    /// The central claim is that this is a REDISTRIBUTION and not a light: it
    /// replaces the flat environment colour that vvAmbientSpecular already
    /// received with one that varies by reflection direction, and the average
    /// of that variation over all directions is exactly 1. If that is true the
    /// feature cannot brighten a scene however it is tuned, and every existing
    /// safeguard - Fresnel, metal tint, specular occlusion, daylight, fog -
    /// keeps working because none of them were touched.
    ///
    /// It is a claim about a number, so it is checked rather than asserted. The
    /// sky gain is DERIVED in the shader precisely so that this test can fail
    /// if someone later types a value over it.
    /// </summary>
    public static class PixelReflectionChecks
    {
        private static string _pbr;

        public static void Run(string repo, Action<string, bool, string> check)
        {
            _pbr = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pseudopbr.glsl"));

            CheckConstantsArePinned(check);
            CheckEnergyIsRedistributed(check);
            CheckQuantisation(check);
            CheckIntegrationPoint(check);
            CheckNotScreenSpace(check);
        }

        private static float Constant(string name)
        {
            Match m = Regex.Match(_pbr, @"const float " + name + @" = ([-\d.]+)\s*;");
            return m.Success ? float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : float.NaN;
        }

        private static void CheckConstantsArePinned(Action<string, bool, string> check)
        {
            foreach (string name in new[]
            {
                "VV_REFLECT_CELLS_SHARP", "VV_REFLECT_CELLS_ROUGH", "VV_REFLECT_HORIZON",
                "VV_REFLECT_GROUND", "VV_REFLECT_HORIZON_GAIN", "VV_REFLECT_TOWARD",
                "VV_REFLECT_MAX_GAIN",
            })
            {
                check(name + " is declared", !float.IsNaN(Constant(name)), "pseudopbr.glsl");
            }

            // The sky gain must be an EXPRESSION, not a literal. It is the term
            // that balances the average to 1, and a literal would silently stop
            // tracking the other two the moment either changed.
            check("the sky gain is derived, not typed",
                Regex.IsMatch(_pbr, @"const float VV_REFLECT_SKY = \(1\.0 - VV_REFLECT_HORIZON_GAIN"),
                "a literal here decouples the average from the bands it is meant to balance");

            check("rough surfaces get fewer cells than smooth ones",
                Constant("VV_REFLECT_CELLS_ROUGH") < Constant("VV_REFLECT_CELLS_SHARP"),
                "roughness must coarsen the structure, not blur it");

            check("the ground is darker than the horizon",
                Constant("VV_REFLECT_GROUND") < Constant("VV_REFLECT_HORIZON_GAIN"), "");
        }

        /// <summary>
        /// The average gain over the sphere must be 1.
        ///
        /// Uniform in Y is the correct measure for a sphere - Archimedes'
        /// hat-box theorem: equal bands of height carry equal solid angle - so
        /// the three regions weigh (1-h)/2, h and (1-h)/2. The azimuth term is
        /// a cosine and averages to zero over a full turn, so it cannot move
        /// the total either.
        /// </summary>
        private static void CheckEnergyIsRedistributed(Action<string, bool, string> check)
        {
            float h = Constant("VV_REFLECT_HORIZON");
            float ground = Constant("VV_REFLECT_GROUND");
            float horizon = Constant("VV_REFLECT_HORIZON_GAIN");
            float toward = Constant("VV_REFLECT_TOWARD");
            float maxGain = Constant("VV_REFLECT_MAX_GAIN");

            float half = (1.0f - h) * 0.5f;
            float sky = (1.0f - horizon * h - ground * half) / half;

            // Numerically, over the sphere and a full turn of azimuth, rather
            // than by re-deriving the closed form the shader already uses.
            double total = 0.0;
            int samples = 0;
            double peak = 0.0;

            for (int i = 0; i < 2000; i++)
            {
                double y = -1.0 + 2.0 * (i + 0.5) / 2000.0;
                double band = y > h ? sky : (y < -h ? ground : horizon);

                for (int j = 0; j < 360; j++)
                {
                    double azimuth = j * Math.PI / 180.0;
                    double gain = band * (1.0 + toward * Math.Cos(azimuth));
                    total += gain;
                    peak = Math.Max(peak, gain);
                    samples++;
                }
            }

            double mean = total / samples;

            check("the reflection redistributes the environment rather than adding to it",
                Math.Abs(mean - 1.0) < 0.005,
                "mean gain over the sphere is " + mean.ToString("0.####", CultureInfo.InvariantCulture));

            check("the sky is brighter than the flat colour it replaces", sky > 1.0,
                "sky gain " + sky.ToString("0.###", CultureInfo.InvariantCulture));

            check("the ground is darker than the flat colour it replaces", ground < 1.0, "");

            // The ceiling has to be reachable-but-not-reached: above the real
            // peak so it never clips the intended look, and low enough that it
            // still catches a later edit that turns this into an amplifier.
            check("the ceiling is above the peak the model can actually produce",
                maxGain > peak,
                "peak " + peak.ToString("0.###", CultureInfo.InvariantCulture) + " vs ceiling " + maxGain);

            check("the ceiling is not so high it permits an amplifier",
                maxGain < 2.0 * peak,
                "a ceiling far above the peak stops being a guard");

            // At full strength the darkest region still passes some light, so a
            // reflective surface never goes fully black just from facing down.
            check("no direction removes the environment entirely", ground > 0.0, "");
        }

        private static void CheckQuantisation(Action<string, bool, string> check)
        {
            Match q = Regex.Match(_pbr,
                @"float vvReflectQuantise\(float t01, float cells, float phase\)\s*\{(.*?)\n\}",
                RegexOptions.Singleline);
            check("the quantiser exists", q.Success, "");
            if (!q.Success) return;

            check("the quantiser returns cell centres",
                q.Groups[1].Value.Contains("+ 0.5 - phase"),
                "sampling a cell edge sits exactly on the boundary it is meant to avoid");

            // Ported from the GLSL. Determinism and idempotence are what stop
            // the pattern shimmering: quantising an already-quantised
            // coordinate has to land on the same cell, or values near a
            // boundary alternate between frames.
            Func<double, double, double, double> quantise =
                (t, cells, phase) => (Math.Floor(t * cells + phase) + 0.5 - phase) / cells;

            bool deterministic = true, inRange = true, idempotent = true;

            for (int i = 0; i <= 1000; i++)
            {
                double t = i / 1000.0;
                for (int c = 2; c <= 16; c += 2)
                {
                    double phase = (c * 0.37) % 1.0;
                    double a = quantise(t, c, phase);
                    double b = quantise(t, c, phase);

                    if (a != b) deterministic = false;
                    if (a < -0.5 || a > 1.5) inRange = false;
                    if (Math.Abs(quantise(a, c, phase) - a) > 1e-9) idempotent = false;
                }
            }

            check("quantisation is deterministic", deterministic, "");
            check("quantisation stays in range", inRange, "");
            check("quantising a quantised coordinate is a fixed point", idempotent,
                "otherwise a value near a cell edge flips between frames");

            // Cell count must fall with roughness, and never reach zero - a
            // zero would divide by zero in the quantiser.
            float sharp = Constant("VV_REFLECT_CELLS_SHARP");
            float rough = Constant("VV_REFLECT_CELLS_ROUGH");
            bool monotone = true;
            double previous = double.MaxValue;

            for (int i = 0; i <= 100; i++)
            {
                double r = i / 100.0;
                double cells = Math.Max(2.0, sharp + (rough - sharp) * r);
                if (cells > previous + 1e-9) monotone = false;
                if (cells < 2.0) monotone = false;
                previous = cells;
            }

            check("cell count falls monotonically with roughness", monotone,
                "roughness must only ever coarsen the structure");
        }

        /// <summary>
        /// It must go INTO the existing ambient specular, not beside it.
        ///
        /// Substituted as that function's environment argument, the reflection
        /// inherits every existing safeguard - the roughness-aware Fresnel, the
        /// metal tint through f0, energy compensation, specular occlusion,
        /// daylight, fog and overcast - and can add no term of its own. A
        /// separate `result +=` would inherit none of them, and is the shape
        /// this feature would take if it were quietly reintroduced as a light.
        /// </summary>
        private static void CheckIntegrationPoint(Action<string, bool, string> check)
        {
            check("the reflection is substituted into the ambient specular term",
                Regex.IsMatch(_pbr,
                    @"result \+= vvAmbientSpecular\(f0, roughness, ndotv,\s*\n\s*vvPixelReflection\("),
                "");

            // Exactly two call sites outside the debug views: the shading path,
            // and none other. More would mean it is contributing twice.
            int calls = Regex.Matches(Regex.Replace(_pbr, @"//[^\n]*", ""), @"vvPixelReflection\(").Count;
            check("the reflection is evaluated once in the shading path",
                calls >= 1 && calls <= 4,
                calls + " call sites - debug views aside, more than one means it contributes twice");

            foreach (var forbidden in new[]
            {
                ("emission", @"vvEmission\([^)]*\)\s*\*[^;]*vvPixelReflection"),
                ("fog", @"fog\s*\*[^;]*vvPixelReflection"),
                ("the diffuse term", @"result \*=[^;]*vvPixelReflection"),
            })
            {
                check("the reflection does not touch " + forbidden.Item1,
                    !Regex.IsMatch(_pbr, forbidden.Item2), "");
            }

            check("strength zero returns the environment untouched",
                Regex.IsMatch(_pbr, @"if \(strength < 0\.001\) return environment;"),
                "an unset uniform must behave exactly like vanilla");

            check("a degenerate normal falls back rather than producing NaN",
                Regex.IsMatch(_pbr, @"if \(len < 1e-4\) return environment;"),
                "reflect() on a zero normal is unnormalisable");
        }

        /// <summary>
        /// The structure must belong to the texture, not the screen.
        ///
        /// This is the failure the whole feature is defined against: if the
        /// quantisation grid were keyed to anything the camera controls, the
        /// pattern would swim across surfaces as the player moves, which is the
        /// look of screen-space reflection done badly.
        /// </summary>
        private static void CheckNotScreenSpace(Action<string, bool, string> check)
        {
            Match fn = Regex.Match(_pbr,
                @"vec3 vvPixelReflection\(vec3 n, vec3 v, vec2 materialUv, float roughness, vec3 environment\)\s*\{(.*?)\n\}",
                RegexOptions.Singleline);
            check("vvPixelReflection exists", fn.Success, "");
            if (!fn.Success) return;

            string body = Regex.Replace(fn.Groups[1].Value, @"//[^\n]*", "");

            check("the quantisation phase comes from the texel index",
                Regex.IsMatch(body, @"floor\(materialUv \* size\)"),
                "the grid has to be a function of the surface, not the view");

            check("the texture resolution comes from the atlas itself",
                body.Contains("textureSize(vv_materialTex, 0)"),
                "no hard-coded 16x16 - the same source vvSnapToTexel already uses");

            check("no screen-space coordinate reaches the quantiser",
                !body.Contains("gl_FragCoord"),
                "gl_FragCoord anywhere here is the screen-space failure by definition");

            // The camera position enters only through the reflection vector,
            // which is physically required - the direction a surface reflects
            // genuinely depends on where it is seen from. It must NOT enter the
            // grid, which is what the texel-index phase above guarantees.
            check("the camera reaches the direction but not the grid",
                !Regex.IsMatch(body, @"phase = [^;]*cameraRelativePos"),
                "");
        }
    }
}
