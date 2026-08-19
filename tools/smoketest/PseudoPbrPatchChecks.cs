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
    /// an effect, it fails to compile and loses the terrain. So the checks here
    /// are weighted toward the failure paths — every anchor is knocked out in
    /// turn and the group must roll all the way back to vanilla each time.
    ///
    /// What these checks CANNOT tell you is whether the anchors match the
    /// installed game's chunkopaque.fsh. The stand-in below is shaped like
    /// vanilla, not copied from it.
    /// </summary>
    public static class PseudoPbrPatchChecks
    {
        /// <summary>
        /// Stand-in shaped like vanilla chunkopaque.fsh. The lines the patch
        /// anchors on are the ones corroborated by Volumetric Shading's own
        /// patch set and by the chunk shader idioms it ships.
        /// </summary>
        private const string Vanilla = @"#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

uniform sampler2D terrainTex;
uniform float alphaTest;

in vec2 uv;
in vec4 rgba;
in vec4 worldPos;
in vec3 normal;
flat in int renderFlags;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

#include fogandlight.fsh
#include vertexflagbits.ash

void main()
{
    vec4 texColor = texture(terrainTex, uv) * rgba;
    if (texColor.a < alphaTest) discard;

    float intensity = 0.45 + (renderFlags & Lod0BitMask) * 0.1;
    outColor = applyFogAndShadowWithNormal(texColor, fogAmount, normal, 1, intensity);

    float glow = 0;
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

            ok("3 patches produced", patches.Count == 3);
            ok("all in group 'pseudopbr'", patches.All(p => p.Group == "pseudopbr"));
            ok("all target chunkopaque.fsh", patches.All(p => p.AppliesTo("chunkopaque.fsh")));
            ok("snippet resolved into content",
                patches.Any(p => p.Content.Contains("vec3 vvPerturbNormal(vec3 faceNormal, vec2 materialUv)")));

            // --- the happy path ---
            var logger = new CollectingLogger();
            var patcher = new ShaderPatcher(logger);
            patcher.SetPatches(patches);

            string result = patcher.Patch("chunkopaque.fsh", Vanilla);

            ok("group healthy", patcher.IsGroupHealthy("pseudopbr"));
            ok("sampler injected", result.Contains("uniform sampler2D vv_materialTex;"));
            ok("perturb function injected", result.Contains("mat3 vvTangentFrame(vec3 n)"));
            ok("uv assertion applied", result.Contains("varying name asserted"));
            ok("lighting call now takes a perturbed normal",
                result.Contains("applyFogAndShadowWithNormal(texColor, fogAmount, vvPerturbNormal(normal, uv), 1, intensity)"));

            // The flat normal must be gone from the lighting call, or the patch
            // silently did nothing while reporting success.
            ok("flat normal no longer reaches the lighting call",
                !result.Contains("applyFogAndShadowWithNormal(texColor, fogAmount, normal,"));

            ok("#version still first line", result.TrimStart().StartsWith("#version"));
            ok("braces balanced", result.Count(c => c == '{') == result.Count(c => c == '}'));

            int mainCount = System.Text.RegularExpressions.Regex.Matches(result, @"\bvoid\s+main\s*\(").Count;
            ok("exactly one main()", mainCount == 1);

            // Injection has to land after #version, or the GLSL compiler
            // rejects the whole file on a directive-order error.
            ok("injection after #version",
                result.IndexOf("uniform sampler2D vv_materialTex;", StringComparison.Ordinal) >
                result.IndexOf("#version", StringComparison.Ordinal));

            ok("final.fsh untouched by this group", patcher.Patch("final.fsh", Vanilla) == Vanilla);

            // --- every anchor, knocked out in turn ---
            //
            // This is the check that matters. A rename in any one of these
            // lines must produce vanilla GLSL, not GLSL that references
            // vvPerturbNormal without the uv it needs — the latter would take
            // the terrain down for everyone on the next game update.
            CheckRollback(repo, resolveSnippet, yaml, check, "uv varying renamed",
                Vanilla.Replace("in vec2 uv;", "in vec2 texUv;"));

            CheckRollback(repo, resolveSnippet, yaml, check, "lighting call reworded",
                Vanilla.Replace("applyFogAndShadowWithNormal(texColor, fogAmount, normal, 1, intensity)",
                                "applyFogAndShadow(texColor, fogAmount)"));
        }

        private static void CheckRollback(string repo, Func<string, string> resolveSnippet, string yaml,
                                          Action<string, bool, string> check, string label, string vanilla)
        {
            var logger = new CollectingLogger();
            var patcher = new ShaderPatcher(logger);
            patcher.SetPatches(ShaderPatchLoader.ParsePatchFile(yaml, "pseudopbr", "test", resolveSnippet));

            string rolled = patcher.Patch("chunkopaque.fsh", vanilla);

            check(label + ": returns vanilla untouched", rolled == vanilla, "");
            check(label + ": no half-applied GLSL",
                !rolled.Contains("vvPerturbNormal") && !rolled.Contains("vv_materialTex"), "");
            check(label + ": group marked unhealthy", !patcher.IsGroupHealthy("pseudopbr"), "");
            check(label + ": logged CRITICAL", logger.Lines.Any(l => l.Contains("CRITICAL")), "");
        }
    }
}
