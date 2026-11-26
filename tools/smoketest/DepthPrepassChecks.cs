using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VintageVisuals.Common.Patching;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// D88 depth prepass. The prepass is only free of visible change if it
    /// writes depth for EXACTLY the fragments the real pass draws fully opaque,
    /// and never for one the real pass discards or blends. These checks pin
    /// the composition that makes that true: the early-out lands after
    /// vanilla's own discard, it refuses partial alpha and reflective blocks,
    /// the skipped shadow lookup only feeds colour, zero is vanilla, and only
    /// the Opaque and OpaqueNoCull pools draw in it.
    /// </summary>
    public static class DepthPrepassChecks
    {
        // vanilla 1.22 chunkopaque.fsh, the lines the group touches, verbatim.
        private const string ChunkOpaqueVanilla = @"#version 330 core
uniform sampler2D terrainTex;
uniform float alphaTest;
flat in int renderFlags;
in vec4 rgba;
layout(location = 0) out vec4 outColor;
const int ReflectiveBitMask = 1 << 11;
float shadowIntensity = 1;
float getBrightnessFromShadowMap() { return 1.0; }
vec4 applyFogAndShadowFromBrightness(vec4 rgbaPixel, float fogAmount, float b, vec3 worldPos) {
	b *= 1+max(0.0, shadowIntensity * 2.0 - 1.66) / 1.5;

	rgbaPixel *= vec4(b, b, b, 1);

	return rgbaPixel;
}
void main()
{
	vec4 texColor = texture(terrainTex, vec2(0));
	if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPosition*2, 0);
	if (glitchStrength > Epsilon) texColor = applyRustEffect(texColor, normal, vertexPosition, 1);

	float b = getBrightnessFromShadowMap();
	float murkiness=getUnderwaterMurkiness();
	outColor = applyFogAndShadowFromBrightness(texColor, clamp(fogAmount - 50*murkiness, 0, 1), min(b, nb), worldPos.xyz);
	if (haxyFade > 0) {           // test the uniform first, for higher performance
	    if (rgba.a < 0.999) {
			outColor.rgb = mix(skyColor.rgb, outColor.rgb, max(1-dayLight, max(0.0, rgba.a)));
	    }
	}
	outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);
#if NORMALVIEW == 0
	float aTest = outColor.a;
	if (aTest < alphaTest || rgba.a < 0.005) discard;
#endif
	outColor = vvTerrainMaterialPrimary(outColor);
}
";

        public static void Run(string repo, Action<string, bool, string> check)
        {
            Func<string, string> resolveSnippet = name =>
                File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shadersnippets", name));
            string yaml = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/depthprepass.yaml"));
            var patches = ShaderPatchLoader.ParsePatchFile(yaml, "depthprepass", "test", resolveSnippet).ToList();

            check("depthprepass declares no sampler",
                !patches.Any(p => p.Content != null && p.Content.Contains("uniform sampler")), "");

            var patcher = new ShaderPatcher(new CollectingLogger());
            patcher.SetPatches(patches);
            string src = patcher.Patch("chunkopaque.fsh", ChunkOpaqueVanilla);
            check("depthprepass group healthy on vanilla chunkopaque", patcher.IsGroupHealthy("depthprepass"), "");

            int discard = src.IndexOf("if (aTest < alphaTest || rgba.a < 0.005) discard;", StringComparison.Ordinal);
            int early = src.IndexOf("if (vv_depthPrepass > 0.5) {", StringComparison.Ordinal);
            int material = src.IndexOf("vvTerrainMaterialPrimary(", StringComparison.Ordinal);
            string block = early >= 0 ? src.Substring(early, Math.Min(400, src.Length - early)) : "";
            Match threshold = Regex.Match(block, @"outColor\.a\s*<\s*([0-9.]+)");
            double t = threshold.Success ? double.Parse(threshold.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;

            check("I-prepass: depth only where the real pass draws fully opaque, after vanilla's discard, before the material",
                discard >= 0 && early > discard && material > early &&
                block.Contains("discard;") && block.Contains("return;") &&
                block.Contains("if (vv_depthPrepass < 1.5 && (outColor.a <") &&
                t >= 0.99 && t <= 1.0 &&
                Regex.IsMatch(block, @"\(\s*renderFlags\s*&\s*ReflectiveBitMask\s*\)\s*!=\s*0"),
                "discard@" + discard + " early@" + early + " material@" + material + " threshold " + t);

            check("I-prepass: the shadow lookup is skipped only in the prepass and only feeds colour",
                src.Contains("float b = vv_depthPrepass > 0.5 ? 1.0 : getBrightnessFromShadowMap();") &&
                src.Contains("rgbaPixel *= vec4(b, b, b, 1);") &&
                src.Contains("if (glitchStrength > Epsilon) texColor = applyRustEffect(texColor, normal, vertexPosition, 1);"),
                "");

            // Amendment 4: what the prepass skips before the discard must reach only rgb.
            // Between the murk line and the discard nothing may write outColor whole or
            // its alpha, and murkiness may feed only the fog weight and the rgb murk.
            int murk = src.IndexOf("float murkiness = vv_depthPrepass > 0.5 ? 0.0 : getUnderwaterMurkiness();", StringComparison.Ordinal);
            string between = murk >= 0 && discard > murk ? src.Substring(murk, discard - murk) : "";
            string afterMurkLine = between.Length > 0 ? between.Substring(between.IndexOf('\n') + 1) : "";
            check("I-prepass: the skipped murk and sky fade reach only rgb before the discard",
                murk >= 0 && murk < early &&
                src.Contains("if (haxyFade > 0 && vv_depthPrepass < 0.5) {") &&
                Regex.Matches(afterMurkLine, @"outColor\s*=").Count == 1 &&
                afterMurkLine.Contains("outColor = applyFogAndShadowFromBrightness(texColor, clamp(fogAmount - 50*murkiness, 0, 1),") &&
                !Regex.IsMatch(afterMurkLine, @"outColor\.a\s*[-+*/]?=") &&
                Regex.Matches(afterMurkLine, @"\bmurkiness\b").Count == 2,
                "murk@" + murk);

            string cs = File.ReadAllText(Path.Combine(repo, "src/Common/Rendering/DepthPrepass.cs"));
            check("I-prepass: only the Opaque and OpaqueNoCull pools draw in the prepass, and the real pass resets the flag",
                cs.Contains("private const int PassOpaque = 0;") &&
                cs.Contains("private const int PassOpaqueNoCull = 1;") &&
                cs.Contains("pass <= PassOpaqueNoCull && pass < pools.Length") &&
                cs.Contains("if (!prepassed || !hasUniform) return false;") &&
                cs.Contains("float mode = _gl.BlendEnabled(true) ? 1f : 2f;") &&
                cs.Contains("if (hasUniform) shader.Uniform(PrepassUniform, 0f);") &&
                cs.Contains("_capi.Render.GlColorMask(true, true, true, true);") &&
                cs.Contains("GetProgram((int)EnumShaderProgram.Chunkopaque)") && !cs.Contains("GetProgramByName("),
                "");

            check("I-prepass: a pool left out of the prepass mask is neither prepassed nor given GL_LEQUAL",
                cs.Contains("if (ReferenceEquals(perAtlas[i], pool)) return ((PoolMask >> pass) & 1) != 0 ? pass : -1;") &&
                cs.Contains("bool prepassed = PrepassedPassOf(__instance, _chunkRenderer) >= 0;"),
                "");
        }
    }
}
