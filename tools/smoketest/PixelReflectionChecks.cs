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
            CheckSceneBridge(repo, check);
        }

        /// <summary>
        /// The march must cover its whole range, with no gaps.
        ///
        /// THE DEFECT THIS PINS. The first version took a fixed number of
        /// samples and accepted a hit only if a surface lay within a tolerance
        /// of one of them. That is a march of discrete SHELLS: with
        /// geometrically growing steps the gaps outrun the tolerance almost
        /// immediately, and past a few blocks most distances could not be hit at
        /// all. Whether a texel found anything depended on whether its ray
        /// length happened to land near a shell.
        ///
        /// It was reported from a screenshot before it was understood - the
        /// valid pixels formed "a circular checkerboard", which is what
        /// concentric bands of reachable distance look like on a surface, and
        /// they vanished on movement because walking changes every ray length at
        /// once.
        ///
        /// Crossing detection has no gaps by construction: a ray point is in
        /// front of the captured surface or behind it, and the flip locates a
        /// surface anywhere in the interval. This checks that the code actually
        /// works that way, because adding steps to a shell march looks like a
        /// fix and only makes the bands finer.
        /// </summary>
        private static void CheckMarchCoversItsRange(Action<string, bool, string> check)
        {
            check("the march detects a depth crossing rather than proximity",
                _code.Contains("previousDelta < 0.0") && _code.Contains("delta >= 0.0"),
                "proximity to a sample leaves gaps between the samples");

            check("a crossing is refined by bisection",
                _code.Contains("VV_SSR_REFINE") && Regex.IsMatch(_code, @"float mid = \(lo \+ hi\) \* 0\.5;"),
                "the interval locates the surface; the refinement finds where in it");

            float near = Constant("VV_SSR_NEAR");
            float growth = Constant("VV_SSR_GROWTH");
            Match st = Regex.Match(_pbr, @"const int VV_SSR_STEPS = (\d+)\s*;");
            check("the march length is declared", st.Success, "");
            if (!st.Success || float.IsNaN(near) || float.IsNaN(growth)) return;

            int steps = int.Parse(st.Groups[1].Value, CultureInfo.InvariantCulture);

            // Reproduce the sample distances, then measure what fraction of the
            // marched range a crossing test can reach versus what a proximity
            // test could. The first is 100 per cent by construction; the second
            // is what shipped, and it is what the numbers below make visible.
            var distances = new System.Collections.Generic.List<double>();
            double t = near, step = near;
            for (int i = 0; i < steps; i++) { distances.Add(t); step *= growth; t += step; }

            double reach = distances[distances.Count - 1];
            check("the march reaches a useful distance",
                reach > 20.0 && reach < 200.0,
                "reaches " + reach.ToString("0.#", CultureInfo.InvariantCulture) + " blocks");

            // The old scheme's coverage, computed so the regression has a
            // number attached rather than a description.
            const double oldTolerance = 1.25;
            double covered = 0.0;
            for (int i = 1; i < distances.Count; i++)
            {
                double gap = distances[i] - distances[i - 1];
                covered += Math.Min(gap, 2.0 * oldTolerance);
            }

            double fraction = covered / (reach - distances[0]);

            check("a proximity march would leave most of the range unreachable",
                fraction < 0.9,
                "proximity coverage " + (fraction * 100.0).ToString("0.#", CultureInfo.InvariantCulture)
                    + "% - if this is near 100 the steps are dense enough that this test proves nothing");

            // Steps must grow, or the near field is wasted and the far field is
            // never reached.
            bool growsButNotWildly = growth > 1.0 && growth < 2.0;
            check("step growth is between one and two", growsButNotWildly,
                "growth " + growth.ToString("0.##", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The reprojection identity, checked as arithmetic.
        ///
        /// The shader holds a point as `cameraRelative = world - currentOrigin`
        /// and must hand the captured matrix `world - captureOrigin`. Those
        /// differ by exactly (currentOrigin - captureOrigin), so:
        ///
        ///   cameraRelative + delta == world - captureOrigin
        ///
        /// must hold. It shipped with the subtraction the other way round, which
        /// moves every reflected point the wrong way by twice the camera's
        /// travel. That is worse than having no correction at all, and it was
        /// invisible in every debug view except a coordinate field, because a
        /// reflection of the wrong part of the world still looks like a
        /// reflection.
        ///
        /// Pinned numerically rather than by matching the expression, so it
        /// cannot be satisfied by a rewrite that keeps the shape and flips the
        /// meaning.
        /// </summary>
        private static void CheckReprojectionSign(string binder, Action<string, bool, string> check)
        {
            Match m = Regex.Match(binder,
                @"new Vec3f\(\(float\)\((\w+)\.X - (\w+)\.X\)");

            check("the camera delta is computed", m.Success, "");
            if (!m.Success) return;

            bool currentMinusCapture = m.Groups[1].Value == "now" && m.Groups[2].Value == "then";

            // Concrete numbers, so the assertion is about behaviour and not
            // about which identifier is spelled first.
            const double world = 100.0;
            const double captureOrigin = 10.0;
            const double currentOrigin = 13.0;

            double cameraRelative = world - currentOrigin;
            double delta = currentMinusCapture ? currentOrigin - captureOrigin
                                               : captureOrigin - currentOrigin;

            double projected = cameraRelative + delta;
            double wanted = world - captureOrigin;

            check("a point reprojects into the captured frame exactly",
                Math.Abs(projected - wanted) < 1e-9,
                "got " + projected + ", the captured matrix needs " + wanted
                    + " - the subtraction is the wrong way round");

            check("the shader adds the delta rather than subtracting it",
                _code.Contains("cameraRelative + vv_reflectCameraDelta"),
                "the sign convention is split across two files and must agree");

            // The origin is the PLAYER, both ends. CameraMatrixOriginf is
            // documented as "player camera matrix with player positioned at
            // 0,0,0", and chunkopaque.vsh builds worldPos as xyz + origin with
            // origin chunk-relative-to-player. Introducing CameraOffset here
            // would add an error rather than remove one.
            // Comments stripped: the explanation of why CameraOffset is WRONG
            // here names it, and a check that reads prose fails on its own
            // reasoning. Third time this file has been caught by that.
            string code = Regex.Replace(binder, @"//[^\n]*", "");

            check("the capture records the same origin the matrix uses",
                code.Contains("Player?.Entity?.Pos") && !code.Contains("CameraOffset"),
                "CameraMatrixOriginf puts the PLAYER at the origin, not the camera");
        }

        /// <summary>
        /// The render-stage bridge: the thing that makes this a reflection of
        /// the world rather than of the sky.
        ///
        /// The terrain shader knows the texture grid but cannot see the scene;
        /// the post pass can see the scene but not the grid. The bridge carries
        /// the scene ACROSS A FRAME instead of across a pass, so both live in
        /// one place. What is pinned here is that each end of it is actually
        /// connected - a capture nothing reads, or a sampler nothing binds, look
        /// exactly like a working feature that happens to reflect only sky.
        /// </summary>
        private static void CheckSceneBridge(string repo, Action<string, bool, string> check)
        {
            string capture = File.ReadAllText(
                Path.Combine(repo, "src/Reflections/SceneCaptureRenderer.cs"));
            string binder = File.ReadAllText(
                Path.Combine(repo, "src/PseudoPBR/PbrShaderBinder.cs"));

            // --- the capture end -------------------------------------------
            check("the capture reads the engine's primary framebuffer",
                capture.Contains("EnumFrameBuffer.Primary") && capture.Contains("ColorTextureIds"),
                "without the real scene texture there is nothing to reflect");

            check("depth comes from the depth attachment, not from gPosition",
                capture.Contains("DepthTextureId") && !capture.Contains("gPosition"),
                "gPosition lives inside #if SSAOLEVEL > 0 and vanishes when SSAO is off");

            check("the capture runs after the scene is composed",
                capture.Contains("AfterPostProcessing")
                    || File.ReadAllText(Path.Combine(repo, "src/Reflections/ReflectionsSubsystem.cs"))
                           .Contains("AfterPostProcessing"),
                "reading the primary buffer any earlier reads a frame mid-render");

            check("the capture is smaller than the screen",
                Regex.IsMatch(capture, @"CaptureScale = 0\.\d+f"),
                "a full-resolution capture is detail the destination texel cannot express");

            check("an off-screen ray cannot wrap to the far side of the frame",
                capture.Contains("EnumTextureWrap.ClampToEdge"),
                "Repeat here paints unrelated geometry onto surfaces");

            check("every failure path disables rather than throws",
                capture.Contains("private void Fail(") && capture.Contains("catch (Exception"),
                "an optional visual feature must not take the client down");

            // --- the shader end --------------------------------------------
            check("the shader declares the captured scene sampler",
                _pbr.Contains("uniform sampler2D vv_reflectScene;"), "");

            check("the sampler is declared below every vanilla sampler",
                _pbr.IndexOf("uniform sampler2D vv_reflectScene;", StringComparison.Ordinal)
                    > _pbr.IndexOf("uniform sampler2D vv_materialTex;", StringComparison.Ordinal),
                "a sampler above vanilla's shifts every unit below it at link time");

            check("the binder actually binds the captured scene",
                binder.Contains("BindTexture2D(ReflectSceneUniform"), "");

            // THE BUG THIS PINS. A texture unit is global GL state, not
            // per-program. Binding once a frame in the binder does not survive
            // to the chunk draws - anything the game binds in between replaces
            // it - so the reflection sampled whatever texture happened to be on
            // its unit at draw time. Debug view 41 showed the block atlas where
            // the captured frame should have been, while view 39 reported
            // confident hits against that garbage.
            //
            // The material atlases already had this problem and the interceptor
            // was written to solve it. The capture has to ride the same path.
            string interceptor = File.ReadAllText(
                Path.Combine(repo, "src/PseudoPBR/TerrainTextureBindInterceptor.cs"));

            check("the capture is rebound per draw call, not once per frame",
                interceptor.Contains("BindTexture2D(PbrShaderBinder.ReflectSceneUniform"),
                "a per-frame bind and a per-draw bind are identical in every static test");

            check("the per-draw capture id is cleared when there is no capture",
                Regex.IsMatch(binder, @"SetSceneCapture\(0\)"),
                "a stale id keeps a destroyed texture bound");

            check("the binder uploads a validity on EVERY path",
                Regex.Matches(binder, @"Uniform\(ReflectValidUniform").Count >= 2,
                "one path that skips it leaves the uniform at whatever was there before");

            check("no capture means validity zero",
                Regex.IsMatch(binder, @"capture == null[^;]*\|\|[^;]*HasCapture[\s\S]{0,200}?Uniform\(ReflectValidUniform, 0f\)"),
                "the safe default has to be the fallback, not a stale texture id");

            // --- the geometry ----------------------------------------------
            Match ssr = Regex.Match(_code,
                @"VvSceneHit vvSceneReflection\(vec3 n, vec2 materialUv, vec3 cameraRelativePos\)\s*\{(.*?)\n\}",
                RegexOptions.Singleline);
            check("the scene reflection exists", ssr.Success, "");
            if (!ssr.Success) return;

            string body = ssr.Groups[1].Value;

            check("the ray starts at the texel centre",
                body.Contains("vvTexelCentrePos(cameraRelativePos, materialUv)"),
                "one colour per texel comes from the ray origin, not from rounding afterwards");

            check("the march accounts for camera movement since the capture",
                _code.Contains("vv_reflectCameraDelta"),
                "without it the reflection slides across surfaces as the player walks");

            CheckReprojectionSign(binder, check);

            check("the march is short",
                Regex.IsMatch(_code, @"VV_SSR_STEPS = ([2-9]|1[0-6])\s*;"),
                "a long march is what makes conventional screen-space reflection expensive");

            check("a hit is bounded by a surface thickness",
                _code.Contains("VV_SSR_THICKNESS"),
                "without it the ray sails past thin geometry into whatever is behind");

            check("a miss returns no confidence rather than a colour",
                Regex.IsMatch(_code, @"miss\.valid = 0\.0;"), "");

            CheckMarchCoversItsRange(check);

            // The whole point of the pass: the analytic sky must be subordinate.
            check("the scene overrides the fallback where it is valid",
                Regex.IsMatch(_code, @"mix\(fallback, scene\.color, clamp\(scene\.valid"),
                "the fallback winning would make this the previous architecture again");
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
                Regex.IsMatch(_code, @"return mix\(environment, image, strength\);"),
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
            // Named for its ROLE. It is what is shown when the world cannot be
            // seen, not the reflection model - see section 41 of the brief and
            // the architecture note above vvSceneReflection.
            Match env = Regex.Match(_code,
                @"vec3 vvReflectionFallback\(vec3 direction, vec3 environment\)\s*\{(.*?)\n\}",
                RegexOptions.Singleline);
            check("the analytic path is named as a fallback", env.Success,
                "vvEnvironmentImage read as though it were the model");

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
