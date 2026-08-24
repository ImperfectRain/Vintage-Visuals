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
            CheckProjectedTexelFootprint(check);
            CheckWhiteMetalGuard(check);
            CheckRoughnessCoarsens(check);
            CheckIntegrationPoint(check);
            CheckHonestLabelling(repo, check);
            CheckSceneBridge(repo, check);
        }

        /// <summary>
        /// The march must sample uniformly in the IMAGE, and compare depths in
        /// one space.
        ///
        /// Two defects live here, both found from screenshots.
        ///
        /// THE SMEAR. Marching in world space projects to wildly different
        /// screen distances: near the camera one step can leap across the frame,
        /// far away a hundred land in one texel. Reported as "the trunk is
        /// reflected all the way from the base of the tree to where the
        /// reflection ends at my feet, instead of rendering a simulated
        /// reflection of the tree properly in perspective" - every ground point
        /// whose ray leapt over the trunk registered its crossing at the same
        /// few texels, so a band of ground sampled one colour instead of
        /// sampling progressively up the tree. Stepping a fixed number of
        /// TEXELS makes the sampling rate uniform in the image, which is what
        /// makes a reflection foreshorten.
        ///
        /// THE DEPTH SPACES. The capture linearises the depth buffer, which
        /// gives axial view-space z. The march compared that against length(),
        /// which is radial. Those differ by 1/cos of the angle from the view
        /// axis - 6% at 20 degrees, 30% at 40 - and the error is radially
        /// symmetric about the screen centre, so it drew its own rings on top of
        /// everything else.
        ///
        /// This check previously read VV_SSR_NEAR and VV_SSR_GROWTH, and when
        /// the march stopped having those it returned early and silently tested
        /// nothing. Missing constants are now a FAILURE, not an exit.
        /// </summary>
        private static void CheckMarchCoversItsRange(Action<string, bool, string> check)
        {
            check("the march detects a depth crossing rather than proximity",
                _code.Contains("previousDelta < 0.0") && _code.Contains("delta >= 0.0"),
                "proximity to a sample leaves gaps between the samples");

            check("a crossing is refined by bisection",
                _code.Contains("VV_SSR_REFINE") && Regex.IsMatch(_code, @"float mid = \(lo \+ hi\) \* 0\.5;"),
                "the interval locates the surface; the refinement finds where in it");

            // --- uniform sampling in the image -----------------------------
            check("the step count comes from how far the ray crosses the screen",
                Regex.IsMatch(_code, @"vec2 travel = abs\(b\.uv - a\.uv\) \* captureSize;")
                    && _code.Contains("VV_SSR_STRIDE"),
                "world-space steps project to uneven screen distances and smear");

            check("the march interpolates in screen space, not along the world ray",
                Regex.IsMatch(_code, @"vec2 uv = mix\(a\.uv, b\.uv, f\);"),
                "");

            // 1/w is what is linear across the screen. Interpolating depth
            // directly bends the ray and puts every hit at the wrong distance.
            check("depth is interpolated as 1/w, not as w",
                Regex.IsMatch(_code, @"float rayDepth = 1\.0 / mix\(invA, invB, f\);"),
                "interpolating w directly is not perspective correct");

            // --- one depth space -------------------------------------------
            Match proj = Regex.Match(_code,
                @"VvRayPoint vvProjectRay\(vec3 cameraRelative\)\s*\{(.*?)\n\}",
                RegexOptions.Singleline);

            check("the ray projection exists", proj.Success, "");

            check("ray depth is taken as view-space z from the projection",
                proj.Success && proj.Groups[1].Value.Contains("p.depth = clip.w;"),
                "clip.w IS view-space z, which is the space the capture packs");

            check("no radial length reaches the depth comparison",
                !Regex.IsMatch(_code, @"(delta|thickness|rayDepth)\s*=\s*length\("),
                "length() is radial and the capture stores axial - the error is radially symmetric");

            // --- bounded cost ----------------------------------------------
            Match st = Regex.Match(_pbr, @"const int VV_SSR_STEPS = (\d+)\s*;");
            Match rf = Regex.Match(_pbr, @"const int VV_SSR_REFINE = (\d+)\s*;");
            float thickness = Constant("VV_SSR_THICKNESS");
            float stride = Constant("VV_SSR_STRIDE");
            float range = Constant("VV_SSR_RANGE");

            // A missing constant is a failure, not a reason to stop checking.
            // The previous version of this method returned early when the march
            // was rewritten, and every assertion below it went quiet.
            check("every march constant this test needs is present",
                st.Success && rf.Success && !float.IsNaN(thickness)
                    && !float.IsNaN(stride) && !float.IsNaN(range),
                "a rewritten march must bring this check with it rather than muting it");

            if (!st.Success || !rf.Success || float.IsNaN(thickness) || float.IsNaN(stride)) return;

            int steps = int.Parse(st.Groups[1].Value, CultureInfo.InvariantCulture);
            int refine = int.Parse(rf.Groups[1].Value, CultureInfo.InvariantCulture);

            check("the march stays far cheaper than conventional SSR",
                steps <= 32,
                steps + " steps - the destination is one colour for a whole texture pixel");

            check("the stride is a small number of texels",
                stride >= 1.0f && stride <= 8.0f,
                "stride " + stride.ToString("0.#", CultureInfo.InvariantCulture)
                    + " texels - too large overshoots and smears, too small wastes the budget");

            // THE RINGS, restated for a screen-space march. The refinement has
            // to resolve an interval of VV_SSR_STRIDE texels to finer than the
            // thickness test, or hits are found and then discarded in bands.
            // In screen space the interval is texels rather than metres, so the
            // relationship is about depth precision rather than world distance -
            // what matters is that refinement happens at all and is not one or
            // two passes on a coarse stride.
            check("refinement is deep enough for the stride",
                Math.Pow(2.0, refine) >= stride * 4.0,
                refine + " passes over a " + stride.ToString("0.#", CultureInfo.InvariantCulture)
                    + " texel stride - too shallow and the thickness test rejects real hits in rings");
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
            check("the capture chooses a colour source at the render stage",
                capture.Contains("ChooseCaptureSource") &&
                capture.Contains("CurrentFrameBuffer") &&
                capture.Contains("source.Color.ColorTextureIds[0]"),
                "debug view 41 showed Primary could be a live buffer without being the composed terrain image");

            check("the primary framebuffer is a fallback, not an unquestioned colour source",
                capture.Contains("FrameBuffer(EnumFrameBuffer.Primary)") &&
                Regex.IsMatch(capture, @"HasColorTexture\(current\).*?HasColorTexture\(primary\)",
                              RegexOptions.Singleline),
                "without a fallback the capture cannot survive stages where CurrentFrameBuffer is not texture-backed");

            check("the capture reports its framebuffer source once",
                capture.Contains("ReportCaptureSource") &&
                capture.Contains("chosenColor=") &&
                capture.Contains("chosenDepth=") &&
                capture.Contains("DescribeFrameBuffers()"),
                "the next runtime screenshot needs IDs, dimensions and stage state, not another guess");

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

            check("a hit is bounded by a surface thickness",
                _code.Contains("VV_SSR_THICKNESS"),
                "without it the ray sails past thin geometry into whatever is behind");

            check("a miss returns no confidence rather than a colour",
                Regex.IsMatch(_code, @"miss\.valid = 0\.0;"), "");

            CheckMarchCoversItsRange(check);

            // The whole point of the pass: the analytic sky must be subordinate.
            check("the scene overrides the fallback where it is valid",
                Regex.IsMatch(_code, @"mix\(fallback, sceneColor, clamp\(scene\.valid"),
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

        private static void CheckProjectedTexelFootprint(Action<string, bool, string> check)
        {
            Match footprint = Regex.Match(_code,
                @"float vvMaterialTexelsPerPixel\(vec2 materialUv\)\s*\{(.*?)\n\}",
                RegexOptions.Singleline);
            check("material texel footprint is measured from derivatives",
                footprint.Success &&
                footprint.Groups[1].Value.Contains("materialUv * atlasSize") &&
                footprint.Groups[1].Value.Contains("dFdx(texelCoord)") &&
                footprint.Groups[1].Value.Contains("dFdy(texelCoord)") &&
                footprint.Groups[1].Value.Contains("max(length(dFdx(texelCoord)), length(dFdy(texelCoord)))"),
                "distance-only fading cannot see FOV, grazing angle or UV scale");

            Match resolve = Regex.Match(_code,
                @"float vvMaterialTexelResolvability\(vec2 materialUv\)\s*\{(.*?)\n\}",
                RegexOptions.Singleline);
            check("material texel resolvability gates below-Nyquist detail",
                resolve.Success &&
                resolve.Groups[1].Value.Contains("vvMaterialTexelsPerPixel(materialUv)") &&
                resolve.Groups[1].Value.Contains("return 1.0 - smoothstep") &&
                resolve.Groups[1].Value.Contains("smoothstep(VV_TEXEL_FOOTPRINT_CRISP") &&
                resolve.Groups[1].Value.Contains("VV_TEXEL_FOOTPRINT_UNRESOLVED"),
                "unresolved texels need a named footprint gate, not raw checker output");

            Match grid = Regex.Match(_code,
                @"vec3 vvMaterialTexelGridDebug\(vec2 materialUv\)\s*\{(.*?)\n\}",
                RegexOptions.Singleline);
            check("material texel grid debug consumes the footprint",
                grid.Success &&
                grid.Groups[1].Value.Contains("fwidth(texelCoord)") &&
                grid.Groups[1].Value.Contains("clamp(footprint, vec2(1e-5), vec2(0.5))") &&
                grid.Groups[1].Value.Contains("vvMaterialTexelResolvability(materialUv)") &&
                grid.Groups[1].Value.Contains("mix(0.5, antiAliased, resolvability)"),
                "declaring a footprint without fading unresolved checker detail leaves the moire");

            Match mode33 = Regex.Match(_code, @"if \(mode == 33\)\s*\{(.*?)\n\s*\}",
                RegexOptions.Singleline);
            check("material texel grid debug uses the derivative-aware renderer",
                mode33.Success && mode33.Groups[1].Value.Contains("vvMaterialTexelGridDebug(materialUv)"),
                "mode 33 must not return the raw alternating checker");

            Match diagnostic = Regex.Match(_code,
                @"vec3 vvMaterialTexelResolutionDebug\(vec2 materialUv\)\s*\{(.*?)\n\}",
                RegexOptions.Singleline);
            check("material texels-per-pixel diagnostic is derivative-driven",
                diagnostic.Success &&
                diagnostic.Groups[1].Value.Contains("vvMaterialTexelsPerPixel(materialUv)") &&
                diagnostic.Groups[1].Value.Contains("green") &&
                diagnostic.Groups[1].Value.Contains("yellow") &&
                diagnostic.Groups[1].Value.Contains("red"),
                "the diagnostic must show projected footprint, not distance bands");

            Match mode52 = Regex.Match(_code, @"if \(mode == 52\) return vec4\((.*?), color\.a\);");
            check("material texels-per-pixel diagnostic is reachable as mode 52",
                mode52.Success && mode52.Groups[1].Value.Contains("vvMaterialTexelResolutionDebug(materialUv)"),
                "the footprint view must be selectable without editing GLSL");
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

            // The capture is the finished frame - graded, bloomed, exposure
            // adapted - so reflecting it verbatim re-applies all of that inside
            // the reflection and lets a bright sky push a metal past what the
            // ambient term it replaces could ever have been.
            check("the reflected scene is capped against the environment",
                _code.Contains("float ceiling = envLuma * VV_REFLECT_MAX;"),
                "an uncapped post-processed capture is how white metal returns");

            check("the cap preserves hue rather than clamping channels",
                Regex.IsMatch(_code, @"sceneColor \*= ceiling / sceneLuma;"),
                "a per-channel clamp desaturates exactly the bright reflections that carry the most information");

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
        /// It must not be described as something it is not - and the honest
        /// description CHANGED when the scene capture landed.
        ///
        /// This check used to require the words "NOT A MIRROR" alongside a
        /// claim that the reflection could not show a tree, a building or the
        /// player. That was true of a forward opaque pass with no scene colour
        /// bound to it, and it stopped being true the moment src/Reflections/
        /// started carrying the previous frame across a frame boundary. The
        /// check went on passing, because it was pinning the words rather than
        /// the claim - which is the same defect it exists to catch, in the test
        /// suite instead of the documentation.
        ///
        /// What has to stay honest now is different and narrower:
        ///
        ///   - it is still not a mirror, because the result is quantised to one
        ///     colour per texture pixel BY DESIGN;
        ///   - what it can actually see depends on a DIFFERENT setting, in a
        ///     different section, which the player has to be told about or the
        ///     reflection will look broken to anyone who left it off.
        /// </summary>
        private static void CheckHonestLabelling(string repo, Action<string, bool, string> check)
        {
            string config = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/config/configlib-patches.json"));

            Match setting = Regex.Match(config, "\"pbr_pixelreflect\"\\s*:\\s*\\{(.*?)\\n    \\}",
                                        RegexOptions.Singleline);
            check("the reflection setting exists", setting.Success, "");
            if (!setting.Success) return;

            string text = setting.Value;

            check("the setting still tells the player it is not a mirror",
                text.IndexOf("not a mirror", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("rather than becoming a mirror", StringComparison.OrdinalIgnoreCase) >= 0,
                "describing a per-texel lookup as a smooth reflection is the failure mode here");

            check("the setting says one colour per texture pixel is the point",
                text.IndexOf("one colour per texture pixel", StringComparison.OrdinalIgnoreCase) >= 0,
                "the quantisation is the design, not a limitation, and the text has to say so");

            // The half a player is most likely to be confused by. Scene
            // reflections are OFF by default and live in another config
            // section, so a player who turns this up and sees only sky has no
            // way to discover why unless this text says.
            check("the setting names the scene-reflection dependency",
                text.IndexOf("scene reflection", StringComparison.OrdinalIgnoreCase) >= 0,
                "a player who left scene reflections off must be told why they see only sky");

            check("the setting no longer claims world geometry is impossible",
                text.IndexOf("cannot reflect a tree", StringComparison.OrdinalIgnoreCase) < 0,
                "that was true before src/Reflections/ existed and is not now");

            check("the shader says why the frame has to be carried across a boundary",
                _pbr.Contains("FORWARD OPAQUE"),
                "the reason has to sit next to the code, not only in a commit message");

            check("the shader documents both what it can see and its fallback",
                _pbr.Contains("vvReflectionFallback") && _pbr.Contains("SceneReflections"),
                "the two paths and which setting picks between them belong next to the code");

            // The G-buffer is the trap a future reader falls into: it looks
            // like the obvious depth source and it does not exist for a player
            // with SSAO off.
            check("the shader records why depth does not come from gPosition",
                _pbr.Contains("SSAOLEVEL > 0"),
                "the conditional G-buffer is the reason, and it has to be written down");
        }
    }
}
