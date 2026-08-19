using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VintageVisuals.Common.Patching;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Drives the real patch engine against the real pseudopbr.yaml.
    ///
    /// This patch carries more risk than any other in the mod, because
    /// chunkopaque.fsh draws the world: a patch that half-applies does not lose
    /// an effect, it fails to compile and loses the terrain. So the checks are
    /// weighted toward the failure paths — every anchor is knocked out in turn
    /// and the group must roll all the way back to vanilla each time.
    /// </summary>
    public static class PseudoPbrPatchChecks
    {
        /// <summary>
        /// Stand-in for vanilla chunkopaque.fsh, cut down from the real 1.22.7
        /// file as the game hands it to the compiler. Every line the patch
        /// touches is reproduced verbatim; the ~1100 lines of noise between
        /// them are not, and the game's own asset is not vendored into this
        /// repo.
        ///
        /// Three properties of the real file are deliberately preserved,
        /// because each of them broke an earlier version of this patch:
        ///
        ///  - **Includes are already expanded.** The real file has no #include
        ///    at all; fogandlight.fsh's uniforms and helpers are inlined
        ///    partway down. So `uniform vec3 lightPosition` appears BELOW the
        ///    varyings, and anything using it must be injected below that too.
        ///  - **Normal shading happens in the vertex shader.** `nb` arrives as
        ///    a varying and applyFogAndShadowWithNormal, though still defined,
        ///    is never called. Perturbing `normal` in the fragment shader would
        ///    change nothing.
        ///  - **`min(b, nb)` appears twice.** The patch must anchor on the full
        ///    statement, or the engine rejects it as ambiguous.
        ///  - **All seven vanilla samplers, in their real order.** Declaring
        ///    vv_materialTex above any of them shifts that sampler's link-time
        ///    unit, and pushing liquidDepth off the end is what made the world
        ///    render as flat water murk. The order is pinned below.
        /// </summary>
        private const string Vanilla = @"#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

uniform sampler2D terrainTex;
uniform sampler2D terrainTexLinear;

in vec4 rgba;
in float fogAmount;
in vec2 uv;
in float glowLevel;
flat in int renderFlags;
in vec3 normal;
in vec4 worldPos;
in float nb;

uniform float alphaTest;
uniform float dayLight;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

uniform sampler2DShadow shadowMapFar;
uniform sampler2DShadow shadowMapNear;

uniform vec3 lightPosition;
uniform float shadowIntensity = 1;

uniform sampler2D glow;
uniform sampler2D sky;

float getBrightnessFromShadowMap() { return 1.0; }

uniform sampler2D liquidDepth;
uniform vec4 waterMurkColor;

float getUnderwaterMurkiness() { return 0.0; }

float getBrightnessFromNormal(vec3 normal, float normalShadeIntensity, float minNormalShade) {
	float nb = max(minNormalShade, 0.5 + 0.5 * dot(normal, lightPosition));
	nb = max(nb, normal.y * 0.95);
	return mix(1, nb, normalShadeIntensity);
}

vec4 applyFogAndShadowWithNormal(vec4 rgbaPixel, float fogAmount, vec3 normal, float normalShadeIntensity, float minNormalShade, vec3 worldPos) {
	float b = getBrightnessFromShadowMap();
	float nb = getBrightnessFromNormal(normal, normalShadeIntensity, minNormalShade);
	b = min(b, nb);
	return rgbaPixel * vec4(b, b, b, 1);
}

vec4 applyFogAndShadowFromBrightness(vec4 rgbaPixel, float fogAmount, float b, vec3 worldPos) {
	return rgbaPixel * vec4(b, b, b, 1);
}

void main() 
{
	vec4 texColor = texture(terrainTex, uv) * rgba;

	float b = getBrightnessFromShadowMap();

	float murkiness=getUnderwaterMurkiness();
	outColor = applyFogAndShadowFromBrightness(texColor, clamp(fogAmount - 50*murkiness, 0, 1), min(b, nb), worldPos.xyz); 

	float glow = 0;
	float godrayLevel = 0;
	outGlow = vec4(glowLevel + glow, godrayLevel, 0, min(1, fogAmount + outColor.a));
}
";

        public static void Run(string repo, Action<string, bool, string> check)
        {
            Action<string, bool> ok = (name, condition) => check(name, condition, "");

            Func<string, string> resolveSnippet = name =>
                File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shadersnippets", name));

            string yaml = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/pseudopbr.yaml"));

            List<ShaderPatch> patches;
            try
            {
                patches = ShaderPatchLoader.ParsePatchFile(yaml, "pseudopbr", "test", resolveSnippet).ToList();
                ok("pseudopbr.yaml parsed", true);
            }
            catch (Exception ex)
            {
                check("pseudopbr.yaml parsed", false, ex.ToString());
                return;
            }

            ok("6 patches produced", patches.Count == 6);
            ok("all in group 'pseudopbr'", patches.All(p => p.Group == "pseudopbr"));
            ok("all target chunkopaque.fsh", patches.All(p => p.AppliesTo("chunkopaque.fsh")));

            // Every patch must be anchored. A 'start' patch here would inject
            // above lightPosition's declaration and fail to compile — the
            // mistake this file's whole layout exists to prevent.
            ok("every patch is anchored, none injects at the top",
                patches.All(p => p.Kind == ShaderPatchKind.Token || p.Kind == ShaderPatchKind.Regex));

            ok("snippet resolved into content",
                patches.Any(p => p.Content.Contains("float vvSurfaceBrightness(float vanillaBrightness")));

            // The whole shipped surface has to be ASCII; AsciiChecks scans the
            // files, this pins the resolved patch content the loader produces.
            ok("resolved patch content is plain ASCII",
                patches.All(p => p.Content.All(c => c <= '~' || c == '\n' || c == '\r' || c == '\t')));

            // --- the happy path ---
            var logger = new CollectingLogger();
            var patcher = new ShaderPatcher(logger);
            patcher.SetPatches(patches);

            string result = patcher.Patch("chunkopaque.fsh", Vanilla);

            ok("group healthy", patcher.IsGroupHealthy("pseudopbr"));
            ok("sampler injected", result.Contains("uniform sampler2D vv_materialTex;"));
            ok("tangent frame injected", result.Contains("mat3 vvTangentFrame(vec3 n)"));
            ok("uv assertion applied", CountOf(result, "in vec2 uv; // vintagevisuals") == 1);
            ok("normal assertion applied", CountOf(result, "in vec3 normal; // vintagevisuals") == 1);
            ok("brightness call patched",
                result.Contains("min(b, vvSurfaceBrightness(nb, normal, uv, worldPos.xyz))"));
            ok("microfacet pass runs on the lit colour",
                result.Contains("outColor = vvApplyPbr(outColor, texColor.rgb, normal, uv, worldPos.xyz, b, fogAmount, murkiness);"));
            ok("Cook-Torrance terms injected",
                result.Contains("float vvDistributionGGX(float NdotH, float roughness)") &&
                result.Contains("float vvGeometrySmith(float NdotV, float NdotL, float roughness)") &&
                result.Contains("vec3 vvFresnelSchlick(float VdotH, vec3 f0)"));
            ok("debug view applied before the glow write",
                result.IndexOf("vvDebugView(outColor", StringComparison.Ordinal) <
                result.IndexOf("outGlow = vec4(glowLevel", StringComparison.Ordinal));
            ok("lightPosition assertion applied",
                CountOf(result, "uniform vec3 lightPosition; // vintagevisuals") == 1);

            // THE check this file exists for. vv_materialTex must be declared
            // after every vanilla sampler: declaring it earlier shifts the
            // link-time unit of every sampler below it, and pushing liquidDepth
            // off the end made getUnderwaterMurkiness() saturate and mixed the
            // whole world to waterMurkColor.
            string[] vanillaSamplers = {
                "uniform sampler2D terrainTex;",
                "uniform sampler2D terrainTexLinear;",
                "uniform sampler2DShadow shadowMapFar;",
                "uniform sampler2DShadow shadowMapNear;",
                "uniform sampler2D glow;",
                "uniform sampler2D sky;",
                "uniform sampler2D liquidDepth;",
            };
            int oursAt = result.IndexOf("uniform sampler2D vv_materialTex;", StringComparison.Ordinal);
            bool declaredLast = oursAt >= 0;
            string firstAfter = null;
            foreach (string sampler in vanillaSamplers)
            {
                int at = result.IndexOf(sampler, StringComparison.Ordinal);
                if (at < 0 || at > oursAt) { declaredLast = false; firstAfter = sampler; break; }
            }
            check("vv_materialTex declared after every vanilla sampler", declaredLast,
                firstAfter == null ? "" : firstAfter + " comes after it");

            ok("liquidDepth survives being used as the anchor",
                CountOf(result, "\nuniform sampler2D liquidDepth;") == 1);

            // The unmodulated brightness must be gone from the call, or the
            // patch reported success while doing nothing.
            ok("raw nb no longer reaches applyFogAndShadowFromBrightness",
                !result.Contains("0, 1), min(b, nb), worldPos.xyz)"));

            // The snippet REPLACES the lightPosition declaration, so it has to
            // paste it back. Losing it would take out every vanilla helper that
            // reads it, in a way no other check would notice.
            ok("lightPosition survives being used as the anchor",
                CountOf(result, "\nuniform vec3 lightPosition;") == 1);

            // Declaration order is the whole reason this patch anchors where it
            // does, so pin it rather than trusting the anchor's position.
            int declAt = result.IndexOf("\nuniform vec3 lightPosition;", StringComparison.Ordinal);
            int usedAt = result.IndexOf("float vvDirectionalShade(vec3 n)", StringComparison.Ordinal);
            int callAt = result.IndexOf("vvSurfaceBrightness(nb, normal, uv", StringComparison.Ordinal);
            ok("lightPosition declared before our functions use it", declAt >= 0 && declAt < usedAt);
            ok("our functions defined before the call site", usedAt >= 0 && usedAt < callAt);

            ok("#version still first line", result.TrimStart().StartsWith("#version"));
            ok("braces balanced", result.Count(c => c == '{') == result.Count(c => c == '}'));

            int mainCount = System.Text.RegularExpressions.Regex.Matches(result, @"\bvoid\s+main\s*\(").Count;
            ok("exactly one main()", mainCount == 1);

            ok("final.fsh untouched by this group", patcher.Patch("final.fsh", Vanilla) == Vanilla);

            // --- every anchor, knocked out in turn ---
            //
            // This is the set that matters. A rename in any one of these lines
            // must produce vanilla GLSL, not GLSL that references
            // vvSurfaceBrightness without the uv or normal it needs — the
            // latter would take terrain down for everyone on a game update.
            CheckRollback(resolveSnippet, yaml, check, "uv varying renamed",
                Vanilla.Replace("in vec2 uv;", "in vec2 texUv;"));

            CheckRollback(resolveSnippet, yaml, check, "normal varying renamed",
                Vanilla.Replace("in vec3 normal;", "in vec3 faceNormal;"));

            CheckRollback(resolveSnippet, yaml, check, "lightPosition renamed",
                Vanilla.Replace("uniform vec3 lightPosition;", "uniform vec3 sunDirection;"));

            CheckRollback(resolveSnippet, yaml, check, "liquidDepth renamed",
                Vanilla.Replace("uniform sampler2D liquidDepth;", "uniform sampler2D waterDepth;"));

            CheckRollback(resolveSnippet, yaml, check, "glow write reworded",
                Vanilla.Replace("outGlow = vec4(glowLevel + glow, godrayLevel, 0, min(1, fogAmount + outColor.a));",
                                "outGlow = vec4(glowLevel, 0, 0, outColor.a);"));

            RunTopsoil(repo, check);

            CheckRollback(resolveSnippet, yaml, check, "brightness call reworded",
                Vanilla.Replace(
                    "outColor = applyFogAndShadowFromBrightness(texColor, clamp(fogAmount - 50*murkiness, 0, 1), min(b, nb), worldPos.xyz);",
                    "outColor = applyFogAndShadowWithNormal(texColor, fogAmount, normal, 1, 0.45, worldPos.xyz);"));
        }


        /// <summary>
        /// Stand-in for vanilla chunktopsoil.fsh. Differs from chunkopaque in
        /// the two ways that drive its patch: FIVE samplers rather than seven,
        /// and it still calls applyFogAndShadowWithNormal, which chunkopaque no
        /// longer does.
        /// </summary>
        private const string VanillaTopsoil = @"#version 330 core

uniform sampler2D terrainTex;
uniform sampler2D terrainTexLinear;

in vec4 rgba;
in float fogAmount;
in vec2 uv;
in float glowLevel;
flat in int renderFlags;
in vec3 normal;
in vec4 worldPos;

uniform float alphaTest;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

uniform sampler2DShadow shadowMapFar;
uniform sampler2DShadow shadowMapNear;

uniform vec3 lightPosition;
uniform float shadowIntensity = 1;

float getBrightnessFromShadowMap() { return 1.0; }

uniform sampler2D liquidDepth;

float getUnderwaterMurkiness() { return 0.0; }

vec4 applyFogAndShadowWithNormal(vec4 c, float f, vec3 n, float i, float m, vec3 w) { return c; }

void main()
{
	outColor = texture(terrainTex, uv) * rgba;
	float intensity = 0.45;
	float murkiness=getUnderwaterMurkiness();
	outColor = applyFogAndShadowWithNormal(outColor, clamp(fogAmount - 50*murkiness, 0, 1), normal, 1, intensity, worldPos.xyz);

	float glow = 0;
	outGlow = vec4(glowLevel + glow, 0, 0, outColor.a);
}
";

        private static void RunTopsoil(string repo, Action<string, bool, string> check)
        {
            Action<string, bool> ok = (name, condition) => check(name, condition, "");

            Func<string, string> resolveSnippet = name =>
                File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shadersnippets", name));

            string yaml = File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shaderpatches/pseudopbrtopsoil.yaml"));

            List<ShaderPatch> patches = ShaderPatchLoader
                .ParsePatchFile(yaml, "pseudopbrtopsoil", "test", resolveSnippet).ToList();

            ok("pseudopbrtopsoil.yaml parsed into 6 patches", patches.Count == 6);
            ok("all target chunktopsoil.fsh", patches.All(p => p.AppliesTo("chunktopsoil.fsh")));

            // Its own group. Sharing pseudopbr's would mean a reworded
            // chunktopsoil line switching relief off for every wall and log too.
            ok("topsoil is its own patch group",
                patches.All(p => p.Group == "pseudopbrtopsoil"));

            var patcher = new ShaderPatcher(new CollectingLogger());
            patcher.SetPatches(patches);

            string result = patcher.Patch("chunktopsoil.fsh", VanillaTopsoil);

            ok("topsoil group healthy", patcher.IsGroupHealthy("pseudopbrtopsoil"));
            ok("relief goes in through the normal",
                result.Contains("vvSurfaceNormal(normal, uv, worldPos.xyz), 1, intensity, worldPos.xyz)"));
            ok("albedo captured before the lighting call",
                result.IndexOf("vec3 vvAlbedo = outColor.rgb;", StringComparison.Ordinal) <
                result.IndexOf("outColor = applyFogAndShadowWithNormal", StringComparison.Ordinal));
            ok("microfacet pass runs on the lit topsoil colour",
                result.Contains("outColor = vvApplyPbr(outColor, vvAlbedo, normal, uv, worldPos.xyz, vvShadow, fogAmount, murkiness);"));
            ok("topsoil braces balanced", result.Count(c => c == '{') == result.Count(c => c == '}'));

            // Same rule as chunkopaque, and it has to hold independently: our
            // sampler after every one of vanilla's, or their link-time units
            // shift underneath them.
            int oursAt = result.IndexOf("uniform sampler2D vv_materialTex;", StringComparison.Ordinal);
            string[] vanillaSamplers = {
                "uniform sampler2D terrainTex;",
                "uniform sampler2D terrainTexLinear;",
                "uniform sampler2DShadow shadowMapFar;",
                "uniform sampler2DShadow shadowMapNear;",
                "uniform sampler2D liquidDepth;",
            };
            bool last = oursAt >= 0;
            foreach (string sampler in vanillaSamplers)
            {
                int at = result.IndexOf(sampler, StringComparison.Ordinal);
                if (at < 0 || at > oursAt) { last = false; break; }
            }
            ok("topsoil declares vv_materialTex after every vanilla sampler", last);

            // A chunkopaque rename must not disable the forest floor, and vice
            // versa. That independence is the entire reason for two groups.
            var mixed = new ShaderPatcher(new CollectingLogger());
            var all = new List<ShaderPatch>(patches);
            all.AddRange(ShaderPatchLoader.ParsePatchFile(
                File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/pseudopbr.yaml")),
                "pseudopbr", "test", resolveSnippet));
            mixed.SetPatches(all);

            mixed.Patch("chunkopaque.fsh", Vanilla.Replace("in vec2 uv;", "in vec2 texUv;"));
            string topsoilAfter = mixed.Patch("chunktopsoil.fsh", VanillaTopsoil);

            ok("a chunkopaque anchor failure leaves topsoil working",
                mixed.IsGroupHealthy("pseudopbrtopsoil") && !mixed.IsGroupHealthy("pseudopbr") &&
                topsoilAfter.Contains("vvSurfaceNormal"));
        }

        private static int CountOf(string haystack, string needle)
        {
            int count = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
            return count;
        }

        private static void CheckRollback(Func<string, string> resolveSnippet, string yaml,
                                          Action<string, bool, string> check, string label, string vanilla)
        {
            var logger = new CollectingLogger();
            var patcher = new ShaderPatcher(logger);
            patcher.SetPatches(ShaderPatchLoader.ParsePatchFile(yaml, "pseudopbr", "test", resolveSnippet));

            string rolled = patcher.Patch("chunkopaque.fsh", vanilla);

            check(label + ": returns vanilla untouched", rolled == vanilla, "");
            check(label + ": no half-applied GLSL",
                !rolled.Contains("vvSurfaceBrightness") && !rolled.Contains("vv_materialTex"), "");
            check(label + ": group marked unhealthy", !patcher.IsGroupHealthy("pseudopbr"), "");
            check(label + ": logged CRITICAL", logger.Lines.Any(l => l.Contains("CRITICAL")), "");
        }
    }
}
