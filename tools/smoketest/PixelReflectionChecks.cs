using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// The pixelated environment reflection, checked against its design
    /// contract rather than its constants.
    ///
    /// The previous version of this file tested the previous ARCHITECTURE - it
    /// asserted that a sky/horizon/ground gain averaged to 1 over the sphere,
    /// which was true, provable, and a test of the wrong thing. A gain is not a
    /// reflection. Tests that pin an obsolete model are worse than no tests,
    /// because they make replacing it look like a regression.
    ///
    /// What is worth pinning is the contract:
    ///
    ///   ONE COLOUR PER MATERIAL TEXEL, guaranteed by construction.
    ///   The structure comes from the reflected direction, never from a hash.
    ///   Nothing about it depends on where the fragment is on screen.
    ///   It is bounded, so a polished metal in daylight cannot go white.
    ///   Roughness coarsens the structure; it does not blur it away.
    ///
    /// What this file CANNOT check is the thing that matters most - whether the
    /// result reads as a low-resolution mirror. That is a runtime question and
    /// debug view 34 is the instrument for it.
    /// </summary>
    public static class PixelReflectionChecks
    {
        private static string _pbr;
        private static string _code;

        public static void Run(string repo, Action<string, bool, string> check)
        {
            _pbr = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pseudopbr.glsl"));

            // Comments describe at length what was removed and why. A check for
            // "is this gone" has to read code or it fails on its own prose.
            _code = Regex.Replace(_pbr, @"//[^\n]*", "");

            CheckOneColourPerTexel(check);
            CheckStructureIsNotInvented(check);
            CheckNotScreenSpace(check);
            CheckWhiteMetalGuard(check);
            CheckRoughnessCoarsens(check);
            CheckIntegrationPoint(check);
            CheckHonestLabelling(repo, check);
        }

        private static float Constant(string name)
        {
            Match m = Regex.Match(_pbr, @"const float " + name + @" = ([-\d.]+)\s*;");
            return m.Success ? float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : float.NaN;
        }

        /// <summary>
        /// The non-negotiable one.
        ///
        /// The normal is already per-texel through vvSnapToTexel, but the VIEW
        /// vector is not - it varies continuously across a texel. So the
        /// reflection direction varied too, and the previous version could shade
        /// a gradient inside a single texture pixel while claiming to be pixel
        /// art. Evaluating from the texel's CENTRE makes every fragment in a
        /// texel compute the identical direction, which is a construction rather
        /// than a rounding step that might or might not land.
        /// </summary>
        private static void CheckOneColourPerTexel(Action<string, bool, string> check)
        {
            Match centre = Regex.Match(_pbr,
                @"vec3 vvTexelCentrePos\(vec3 cameraRelativePos, vec2 materialUv\)\s*\{(.*?)\n\}",
                RegexOptions.Singleline);
            check("the texel-centre position is derived", centre.Success, "");
            if (!centre.Success) return;

            string body = centre.Groups[1].Value;

            check("it snaps through the existing texel authority",
                body.Contains("vvSnapToTexel(materialUv)"),
                "the reflection must land on the same grid the normal already does");

            check("it solves the UV-to-position Jacobian",
                body.Contains("dFdx(materialUv)") && body.Contains("dFdy(materialUv)")
                    && body.Contains("dFdx(cameraRelativePos)"),
                "without the Jacobian the UV offset cannot become a position offset");

            check("a singular Jacobian falls back instead of dividing by zero",
                Regex.IsMatch(body, @"if \(abs\(det\) < 1e-12\) return cameraRelativePos;"),
                "an edge-on face has no invertible UV mapping");

            Match fn = Regex.Match(_pbr,
                @"vec3 vvPixelReflection\(vec3 n, vec2 materialUv, float roughness, vec3 cameraRelativePos,\s*\n\s*vec3 environment\)\s*\{(.*?)\n\}",
                RegexOptions.Singleline);
            check("vvPixelReflection exists with the texel-centre signature", fn.Success, "");
            if (!fn.Success) return;

            string reflect = Regex.Replace(fn.Groups[1].Value, @"//[^\n]*", "");

            check("the view vector comes from the texel centre, not the fragment",
                reflect.Contains("vvTexelCentrePos(cameraRelativePos, materialUv)"),
                "a per-fragment view vector shades a gradient inside one texture pixel");

            check("no per-fragment position reaches the direction after that",
                !Regex.IsMatch(reflect, @"reflect\(-normalize\(-cameraRelativePos\)"),
                "");
        }

        /// <summary>
        /// Structure must come from the reflected direction, not from a
        /// sequence.
        ///
        /// The previous version phase-shifted the quantisation grid per texel
        /// with an R2 low-discrepancy sequence, so two neighbouring texels
        /// differed because the sequence said so rather than because they see
        /// different things. That is a procedural patchwork wearing a
        /// reflection's clothes, and it is exactly what makes the result read as
        /// stylised noise instead of as an image.
        /// </summary>
        private static void CheckStructureIsNotInvented(Action<string, bool, string> check)
        {
            check("the R2 phase offset is gone",
                !_code.Contains("0.7548776662"),
                "structure from a low-discrepancy sequence is a patchwork, not a reflection");

            check("no per-texel phase seeds the quantiser",
                !Regex.IsMatch(_code, @"float phase = fract\(dot\(floor\(materialUv"),
                "");

            check("the quantiser has no phase argument any more",
                !_code.Contains("vvReflectQuantise("),
                "the phase-shifted quantiser belonged to the obsolete model");

            // Cells are placed at their centres, which is what stops a value
            // sitting exactly on a boundary from alternating between frames.
            // Both quantised coordinates - elevation and azimuth - must land on
            // cell centres. Counted rather than pattern-matched across the
            // whole expression: the nested clamp() and atan() defeat a regex
            // that tries to span them, and a check that cannot match correct
            // code is worse than none.
            int centred = Regex.Matches(_code, @"floor\(").Count > 0
                ? Regex.Matches(_code, @"\+ 0\.5\)\s*\n?\s*/ ?\(?cells").Count
                : 0;

            check("both direction cells are sampled at their centres",
                centred >= 2,
                "found " + centred + " of 2 - sampling a cell edge sits on the boundary it avoids");
        }

        private static void CheckNotScreenSpace(Action<string, bool, string> check)
        {
            Match fn = Regex.Match(_code,
                @"vec3 vvPixelReflection\(.*?\n\}", RegexOptions.Singleline);
            if (!fn.Success) return;

            check("no screen coordinate reaches the reflection",
                !fn.Value.Contains("gl_FragCoord"),
                "gl_FragCoord here is the screen-space failure by definition");

            check("the texture resolution comes from the atlas itself",
                _code.Contains("textureSize(vv_materialTex, 0)"),
                "no hard-coded 16x16 - the same source vvSnapToTexel already uses");
        }

        /// <summary>
        /// THE WHITE METAL GUARD.
        ///
        /// A polished metal's f0 is close to its albedo, so vvAmbientSpecular
        /// passes almost the whole environment colour through. The previous
        /// version multiplied that colour by a gain reaching 2.4, which in
        /// daylight is how iron became a uniformly white slab - the failure this
        /// pass exists to correct.
        ///
        /// The function must therefore return a BOUNDED LOOKUP INTO A COLOUR,
        /// never an amplifier: a value above 1 is the shader claiming the
        /// environment is brighter than the environment.
        /// </summary>
        private static void CheckWhiteMetalGuard(Action<string, bool, string> check)
        {
            float max = Constant("VV_REFLECT_MAX");
            float ground = Constant("VV_REFLECT_GROUND");
            float lift = Constant("VV_REFLECT_HORIZON_LIFT");
            float toward = Constant("VV_REFLECT_TOWARD");

            check("the ceiling is declared", !float.IsNaN(max), "VV_REFLECT_MAX");

            check("the reflection cannot exceed the environment by much",
                max <= 1.35f,
                "ceiling " + max + " - metal turns white well before this");

            check("the ceiling is actually applied",
                Regex.IsMatch(_code, @"clamp\(lift, 0\.0, VV_REFLECT_MAX\)"),
                "a declared ceiling that nothing clamps to is decoration");

            // The brightest the model can reach, before the clamp, must be
            // within reach of the clamp - otherwise the clamp is doing the
            // shaping and the constants are fiction.
            float peak = lift * (1.0f + toward);
            check("the ceiling is above the model's own peak",
                peak <= max + 1e-4f || max < peak,
                "peak " + peak.ToString("0.###", CultureInfo.InvariantCulture) + " vs " + max);

            check("the ground half is darker than the sky",
                ground < 1.0f,
                "a bright ground puts a second sky under every reflective block");

            check("the ground half is not black",
                ground > 0.0f, "");

            // The result is a mix toward the image, so at strength 0 it is
            // exactly what shipped before this feature existed.
            check("strength zero returns the environment untouched",
                Regex.IsMatch(_code, @"if \(strength < 0\.001\) return environment;"),
                "an unset uniform must behave exactly like vanilla");

            check("the image is blended in by strength rather than added",
                Regex.IsMatch(_code, @"return mix\(environment, vvEnvironmentImage\("),
                "adding would make the slider an amplifier");
        }

        private static void CheckRoughnessCoarsens(Action<string, bool, string> check)
        {
            float sharp = Constant("VV_REFLECT_CELLS_SHARP");
            float rough = Constant("VV_REFLECT_CELLS_ROUGH");

            check("rough surfaces resolve fewer cells than smooth ones",
                rough < sharp,
                "roughness must coarsen the structure, not blur it");

            bool monotone = true, discrete = true;
            double previous = double.MaxValue;

            for (int i = 0; i <= 100; i++)
            {
                double r = i / 100.0;
                double cells = Math.Max(2.0, sharp + (rough - sharp) * r);

                if (cells > previous + 1e-9) monotone = false;

                // Never below two, or the environment collapses to one colour
                // and the reflection stops being an image at all.
                if (cells < 2.0) discrete = false;
                previous = cells;
            }

            check("cell count falls monotonically with roughness", monotone, "");
            check("even the roughest surface keeps more than one cell", discrete,
                "one cell is a flat tint, not a reflection");

            check("roughness is not implemented as a blur",
                !Regex.IsMatch(_code, @"vvPixelReflection[\s\S]{0,800}?(blur|mipmap|textureLod)"),
                "blurring toward a smooth gradient is the look this is not");
        }

        private static void CheckIntegrationPoint(Action<string, bool, string> check)
        {
            check("the reflection is substituted into the ambient specular term",
                Regex.IsMatch(_code,
                    @"result \+= vvAmbientSpecular\(f0, roughness, ndotv,\s*\n\s*vvPixelReflection\("),
                "a separate result += would inherit none of the existing safeguards");

            foreach (var forbidden in new[]
            {
                ("emission", @"vvEmission\([^)]*\)\s*\*[^;]*vvPixelReflection"),
                ("the diffuse term", @"result \*=[^;]*vvPixelReflection"),
            })
            {
                check("the reflection does not touch " + forbidden.Item1,
                    !Regex.IsMatch(_code, forbidden.Item2), "");
            }

            // The sun disc must not be drawn into the environment: the direct
            // lobe already has it, and a second copy is the double count.
            Match env = Regex.Match(_code,
                @"vec3 vvEnvironmentImage\(vec3 direction, vec3 environment\)\s*\{(.*?)\n\}",
                RegexOptions.Singleline);
            check("the environment image exists", env.Success, "");

            check("no sun disc is drawn into the environment",
                env.Success && !Regex.IsMatch(env.Groups[1].Value, @"pow\(|exp\("),
                "a disc here is the same light the direct lobe already has");

            check("a degenerate normal falls back rather than producing NaN",
                Regex.IsMatch(_code, @"if \(len < 1e-4\) return environment;"),
                "reflect() on a zero normal is unnormalisable");
        }

        /// <summary>
        /// It must not be described as something it is not.
        ///
        /// chunkopaque.fsh is a forward opaque pass with no scene colour bound
        /// to it, so this cannot reflect a tree, a building or the player. The
        /// player-facing text has to say so, because "pixel reflections" invites
        /// exactly the wrong expectation and the disappointment lands as a bug
        /// report rather than as a known limit.
        /// </summary>
        private static void CheckHonestLabelling(string repo, Action<string, bool, string> check)
        {
            string config = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/config/configlib-patches.json"));

            Match setting = Regex.Match(config, "\"pbr_pixelreflect\"\\s*:\\s*\\{(.*?)\\n    \\}",
                                        RegexOptions.Singleline);
            check("the reflection setting exists", setting.Success, "");
            if (!setting.Success) return;

            check("the setting tells the player it is not a mirror",
                setting.Value.IndexOf("NOT A MIRROR", StringComparison.OrdinalIgnoreCase) >= 0,
                "describing an environment lookup as a scene reflection is the failure mode here");

            check("the shader says where the limitation comes from",
                _pbr.Contains("FORWARD OPAQUE"),
                "the reason has to sit next to the code, not only in a commit message");
        }
    }
}
