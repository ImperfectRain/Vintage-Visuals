using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VintageVisuals.Common.Patching;

namespace VintageVisuals.SmokeTest
{
    public static class TerrainBasePatchChecks
    {
        private const string ChunkOpaqueVanilla = @"#version 330 core
uniform sampler2D terrainTex;
uniform sampler2D terrainTexLinear;
uniform sampler2DShadow shadowMapFar;
uniform sampler2DShadow shadowMapNear;
uniform vec3 lightPosition;
uniform sampler2D glow;
uniform sampler2D sky;
uniform sampler2D liquidDepth;
in vec2 uv;
in vec3 normal;
in vec4 worldPos;
in vec4 rgbaFog;
in vec3 blockLight;
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
void main()
{
    vec4 texColor = vec4(1);
    float glow = 0;
    float glowLevel = 0;
    float godrayLevel = 0;
    float fogAmount = 0;
    float murkiness = 0;
    float b = 1;
    outColor = vec4(1);
    outGlow = vec4(glowLevel + glow, godrayLevel, 0, min(1, fogAmount + outColor.a));
}
";

        private const string TopsoilVanilla = @"#version 330 core
uniform sampler2D terrainTex;
uniform sampler2D terrainTexLinear;
uniform sampler2DShadow shadowMapFar;
uniform sampler2DShadow shadowMapNear;
uniform vec3 lightPosition;
uniform sampler2D liquidDepth;
in vec2 uv;
in vec3 normal;
in vec4 worldPos;
in vec4 rgbaFog;
in vec3 blockLight;
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
void main()
{
    float glow = 0;
    float glowLevel = 0;
    float fogAmount = 0;
    float murkiness = 0;
    outColor = vec4(1);
    outGlow = vec4(glowLevel + glow, 0, 0, outColor.a);
}
";

        public static void Run(string repo, Action<string, bool, string> check)
        {
            Func<string, string> resolveSnippet = name =>
                File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shadersnippets", name));

            string yaml = File.ReadAllText(Path.Combine(repo,
                "assets/vintagevisuals/shaderpatches/pbrterrainbase.yaml"));

            var patches = ShaderPatchLoader.ParsePatchFile(yaml, "pbrterrainbase", "test", resolveSnippet).ToList();

            check("pbrterrainbase.yaml parsed into four patches", patches.Count == 4, patches.Count.ToString());
            check("pbrterrainbase group is isolated",
                patches.All(p => p.Group == "pbrterrainbase"),
                string.Join(", ", patches.Select(p => p.Group).Distinct()));
            check("pbrterrainbase targets only terrain fragment shaders",
                patches.All(p => p.AppliesTo("chunkopaque.fsh") || p.AppliesTo("chunktopsoil.fsh")) &&
                !patches.Any(p => p.AppliesTo("chunkopaque.vsh") || p.AppliesTo("chunktopsoil.vsh")),
                "");
            check("pbrterrainbase uses preserving patch operations",
                patches.All(p => p.Kind == ShaderPatchKind.InsertBefore),
                string.Join(", ", patches.Select(p => p.Kind).Distinct()));
            check("pbrterrainbase declares deterministic compiler phases",
                patches.Count(p => p.Phase == "Declarations") == 2 &&
                patches.Count(p => p.Phase == "FinalOutput") == 2,
                string.Join(", ", patches.Select(p => p.Phase)));
            check("pbrterrainbase declares no texture sampler in resolved content",
                !patches.Any(p => p.Content.Contains("uniform sampler") || p.Content.Contains("texture(")),
                "Stage A must not alter the terrain sampler surface");

            var logger = new CollectingLogger();
            var patcher = new ShaderPatcher(logger);
            patcher.SetPatches(patches);

            string opaque = patcher.Patch("chunkopaque.fsh", ChunkOpaqueVanilla);
            string topsoil = patcher.Patch("chunktopsoil.fsh", TopsoilVanilla);

            check("pbrterrainbase group healthy", patcher.IsGroupHealthy("pbrterrainbase"), "");
            check("chunkopaque receives identity color operation",
                opaque.Contains("outColor = vvTerrainBaseIdentity(outColor);"),
                "");
            check("chunktopsoil receives identity color operation",
                topsoil.Contains("outColor = vvTerrainBaseIdentity(outColor);"),
                "");
            check("chunkopaque keeps vanilla lightPosition uniform",
                opaque.Contains("uniform vec3 lightPosition;"),
                "ShaderProgramBase.Use() uploads this vanilla uniform unconditionally");
            check("chunktopsoil keeps vanilla lightPosition uniform",
                topsoil.Contains("uniform vec3 lightPosition;"),
                "ShaderProgramBase.Use() uploads this vanilla uniform unconditionally");
            check("chunkopaque vanilla godray channel preserved",
                opaque.Contains("outGlow = vec4(glowLevel + glow, godrayLevel, 0, min(1, fogAmount + outColor.a));"),
                "");
            check("chunktopsoil vanilla outGlow contract preserved",
                topsoil.Contains("outGlow = vec4(glowLevel + glow, 0, 0, outColor.a);"),
                "");
            check("patched Stage A terrain has no VV texture sampler declarations",
                !opaque.Contains("uniform sampler2D vv_") && !topsoil.Contains("uniform sampler2D vv_"),
                "");
            check("patched Stage A terrain has no PBR or reflection calls",
                !opaque.Contains("vvApplyPbr") && !topsoil.Contains("vvApplyPbr") &&
                !opaque.Contains("vvSceneReflection") && !topsoil.Contains("vvSceneReflection") &&
                !opaque.Contains("vvWorldReflection") && !topsoil.Contains("vvWorldReflection"),
                "");

            ShaderPatchManifestEntry opaqueManifest = patcher.Manifest["chunkopaque.fsh"];
            ShaderPatchManifestEntry topsoilManifest = patcher.Manifest["chunktopsoil.fsh"];

            check("chunkopaque manifest records source hashes",
                opaqueManifest.BaseSourceHash.Length == 64 &&
                opaqueManifest.FinalSourceHash.Length == 64 &&
                opaqueManifest.BaseSourceHash != opaqueManifest.FinalSourceHash,
                opaqueManifest.ToStableText());
            check("chunktopsoil manifest records source hashes",
                topsoilManifest.BaseSourceHash.Length == 64 &&
                topsoilManifest.FinalSourceHash.Length == 64 &&
                topsoilManifest.BaseSourceHash != topsoilManifest.FinalSourceHash,
                topsoilManifest.ToStableText());
            check("chunkopaque manifest records matched anchors",
                opaqueManifest.Operations.Count == 2 &&
                opaqueManifest.Operations.All(o => o.Matches == 1) &&
                opaqueManifest.Operations.Any(o => o.Anchor.Contains("lightPosition")) &&
                opaqueManifest.Operations.Any(o => o.Anchor.Contains("outGlow")),
                opaqueManifest.ToStableText());
            check("Stage A manifest exposes sampler surface unchanged",
                opaqueManifest.SamplersAdded.Count == 0 &&
                opaqueManifest.SamplersRemoved.Count == 0 &&
                topsoilManifest.SamplersAdded.Count == 0 &&
                topsoilManifest.SamplersRemoved.Count == 0,
                opaqueManifest.ToStableText() + topsoilManifest.ToStableText());
            check("Stage A manifest exposes preserved vanilla declarations",
                opaqueManifest.DeclarationsRemoved.Count == 0 &&
                topsoilManifest.DeclarationsRemoved.Count == 0 &&
                opaqueManifest.DeclarationsAfter.Contains("uniform vec3 lightPosition;") &&
                topsoilManifest.DeclarationsAfter.Contains("uniform vec3 lightPosition;"),
                opaqueManifest.ToStableText() + topsoilManifest.ToStableText());
            check("manifest output is deterministic text without CRLF",
                !opaqueManifest.ToStableText().Contains("\r"),
                opaqueManifest.ToStableText());

            CheckMaterialPrimary(repo, resolveSnippet, check);
        }

        private static void CheckMaterialPrimary(
            string repo,
            Func<string, string> resolveSnippet,
            Action<string, bool, string> check)
        {
            string yaml = File.ReadAllText(Path.Combine(repo,
                "assets/vintagevisuals/shaderpatches/pbrterrainmaterial.yaml"));

            var patches = ShaderPatchLoader.ParsePatchFile(
                yaml, "pbrterrainmaterial", "test", resolveSnippet).ToList();

            // Terrain emission goes through the ONE emission model, so every
            // emissive slider reaches it and Emissive strength 0 is vanilla.
            // The first restoration checkpoint added glowLevel * mask straight
            // to the bloom with no slider in the loop.
            string terrainSnippet = File.ReadAllText(Path.Combine(repo,
                "assets/vintagevisuals/shadersnippets/pbrterrainmaterial.glsl"));
            string glowBody = GlslEval.FunctionBody(terrainSnippet, "float vvTerrainEmissiveGlow(");
            string emissionBody = GlslEval.FunctionBody(terrainSnippet, "vec3 vvTerrainEmission(");
            check("terrain bloom from glowing blocks is scaled by the emissive sliders",
                glowBody != null && glowBody.Contains("vvEmissiveGlow(glowLevel)"),
                glowBody ?? "vvTerrainEmissiveGlow not found");
            check("terrain emission colour comes from the shared emission model",
                emissionBody != null && emissionBody.Contains("vvEmission(m.baseColor, glowLevel, cameraRelativePos)"),
                emissionBody ?? "vvTerrainEmission not found");

            // Wood grain stretches only the direct lobe, and a slider at 0 must
            // leave the isotropic lobe untouched rather than "nearly" so.
            string grainBody = GlslEval.FunctionBody(terrainSnippet, "float vvTerrainGrainAnisotropy(");
            string directBody = GlslEval.FunctionBody(terrainSnippet, "vec3 vvTerrainDirectSpecular(");
            check("terrain wood grain is exactly off at a slider of zero",
                grainBody != null && grainBody.Contains("if (vv_pbrGrain <= 0.001) return 0.0;") &&
                directBody != null && directBody.Contains("if (vv_pbrGrain > 0.001)"),
                grainBody ?? "vvTerrainGrainAnisotropy not found");
            check("terrain wood grain reaches the direct specular lobe",
                directBody != null && directBody.Contains("vvDistributionGGXAnisotropic("),
                "");

            // Rain cannot wet what the sky cannot see. The modular path first
            // shipped with one global wetness, so floors under a roof darkened
            // in rain exactly like the lawn outside.
            string wetBody = GlslEval.FunctionBody(terrainSnippet, "float vvTerrainWetness(");
            string snowBody = GlslEval.FunctionBody(terrainSnippet, "float vvTerrainSnow(");
            check("terrain wetness and snow need the sky to see the surface",
                // Every wetness return, plant and block alike, not just one.
                wetBody != null &&
                GlslEval.Statements(wetBody).Where(st => st.Contains("return clamp(vv_sceneWetness")).Count() >= 2 &&
                GlslEval.Statements(wetBody).Where(st => st.Contains("return clamp(vv_sceneWetness"))
                    .All(st => st.Contains("vvTerrainSkyExposure()")) &&
                snowBody != null && snowBody.Contains("vvTerrainSkyExposure()"),
                "");
            string rippleBody = GlslEval.FunctionBody(terrainSnippet, "vec2 vvTerrainRippleField(");
            check("terrain ripples are exactly off at a strength of zero and when dry",
                rippleBody != null && rippleBody.Contains("if (vv_weatherRipples < 0.001 || wetness < 0.001) return vec2(0.0);"),
                "");
            // The sky-exposure varying: both halves in THIS group, so they apply
            // or roll back together. A fragment input with no vertex writer is
            // a link failure that costs the world, not the feature.
            check("the terrain sky-exposure varying is written and read inside one group",
                patches.Count(p => p.Content.Contains("out float vv_sunExposure;")) == 2 &&
                patches.Count(p => p.Content.Contains("vv_sunExposure = rgbaLightIn.a;")) == 2 &&
                patches.Count(p => p.Content.Contains("in float vv_sunExposure;")) == 2,
                "");

            check("pbrterrainmaterial.yaml parsed into twenty-four patches",
                patches.Count == 24,
                patches.Count.ToString());
            check("pbrterrainmaterial group is isolated",
                patches.All(p => p.Group == "pbrterrainmaterial"),
                string.Join(", ", patches.Select(p => p.Group).Distinct()));
            check("pbrterrainmaterial uses only safe explicit operations",
                patches.All(p => p.Kind == ShaderPatchKind.Assert ||
                                 p.Kind == ShaderPatchKind.InsertAfter ||
                                 p.Kind == ShaderPatchKind.InsertBefore ||
                                 (p.Kind == ShaderPatchKind.Replace &&
                                  (p.Content.Contains("in vec4 rgbaFog;") || p.Content.Contains("uniform vec3 lightPosition;")))),
                string.Join(", ", patches.Select(p => p.Kind).Distinct()));
            // The only replaces are the shared snippets, and both paste their
            // anchor back - a replace that dropped it would delete a vanilla
            // declaration the rest of the shader needs.
            check("pbrterrainmaterial injects scene.glsl and pbrcore.glsl once per terrain program",
                patches.Count(p => p.Kind == ShaderPatchKind.Replace && p.Content.Contains("float vvSceneVisibilityDampen()")) == 2 &&
                patches.Count(p => p.Kind == ShaderPatchKind.Replace && p.Content.Contains("vec3 vvEmission(vec3 albedo, float glow, vec3 cameraRelativePos)")) == 2,
                "");
            check("pbrterrainmaterial declares deterministic compiler phases",
                patches.Count(p => p.Phase == "Assertions") == 12 &&
                patches.Count(p => p.Phase == "Declarations") == 8 &&
                patches.Count(p => p.Phase == "Material") == 4,
                string.Join(", ", patches.Select(p => p.Phase)));

            string resolved = string.Join("\n", patches.Select(p => p.Content));
            check("Stage C material terrain declares only material atlas VV samplers",
                resolved.CountSubstring("uniform sampler2D vv_materialTex;") == 2 &&
                resolved.CountSubstring("uniform sampler2D vv_materialTex2;") == 2 &&
                !resolved.Contains("vv_reflectScene") &&
                !resolved.Contains("vv_reflectWorld") &&
                !resolved.Contains("vv_canopy"),
                "Stage C is limited to primary and secondary material atlas samplers");
            check("Stage C terrain material decodes the primary and secondary atlas channels",
                resolved.Contains("struct VvTerrainMaterial") &&
                resolved.Contains("VvTerrainMaterial vvDecodeTerrainMaterial") &&
                resolved.Contains("m.metalness = clamp(secondary.r") &&
                resolved.Contains("m.height = clamp(secondary.g") &&
                resolved.Contains("m.ao = clamp(secondary.b") &&
                resolved.Contains("m.emissionMask = vv_material2Valid < 0.5 ? 1.0"),
                "material response must not silently collapse back to the primary atlas only");
            check("Stage C terrain material contains a bounded microfacet response",
                resolved.Contains("vvDistributionGGXTerrain") &&
                resolved.Contains("vvGeometrySmithTerrain") &&
                resolved.Contains("vvFresnelSchlickTerrain") &&
                resolved.Contains("mix(dielectricF0, m.baseColor, m.metalness)") &&
                resolved.Contains("diffuseEnergy") &&
                resolved.Contains("(1.0 - m.metalness)"),
                "terrain material response must stay energy-aware and metalness-aware");
            // Plant tissue has its own path. It may add light through the leaf,
            // but it must branch off BEFORE the opaque material is decoded, so
            // relief, cavity and atlas specular never reach alpha-cut plants.
            string primaryBody = GlslEval.FunctionBody(terrainSnippet, "vec4 vvTerrainMaterialPrimary(") ?? "";
            string floraShadeBody = GlslEval.FunctionBody(terrainSnippet, "vec4 vvTerrainFloraShade(") ?? "";
            int floraBranch = primaryBody.IndexOf("if (vvTerrainIsFlora())", StringComparison.Ordinal);
            int decode = primaryBody.IndexOf("vvDecodeTerrainMaterial(", StringComparison.Ordinal);
            check("Stage C terrain material skips vanilla wind-mode vegetation",
                resolved.Contains("bool vvTerrainIsFlora()") &&
                floraBranch >= 0 && decode > floraBranch &&
                primaryBody.Substring(floraBranch, decode - floraBranch).Contains("return vvTerrainFloraShade(") &&
                floraShadeBody.Length > 0 &&
                !floraShadeBody.Contains("vvDecodeTerrainMaterial") &&
                !floraShadeBody.Contains("vvDirectionalShadeTerrain") &&
                resolved.Contains("if (vvTerrainIsFlora()) return color;"),
                "alpha-cut plants must not receive opaque terrain cavity/relief");

            // Foliage transmission, ported from the legacy patch.
            string foliageBody = GlslEval.FunctionBody(terrainSnippet, "vec3 vvTerrainFoliageTransmission(") ?? "";
            check("terrain foliage transmission is exactly off at a slider of zero",
                foliageBody.Contains("if (vv_pbrFoliage < 0.001) return vec3(0.0);") &&
                foliageBody.Contains("* vv_pbrFoliage"),
                "");
            check("terrain foliage transmission adds to vanilla's lit leaf rather than replacing it",
                floraShadeBody.Contains("vec3 result = litColor.rgb * mix(1.0, VV_LAYER_WET_LEAF_DARKEN, wet);") &&
                floraShadeBody.Contains("+ through) * clearAir;"),
                "");

            // The translucency sign has inverted the whole effect once: it fired
            // on front-lit leaves and went dark on backlit ones. Evaluate the
            // shipped body for both cases.
            string transBody = GlslEval.StripComments(
                GlslEval.FunctionBody(terrainSnippet, "float vvTerrainTranslucency(") ?? "")
                .Replace("vvTerrainSafeNormalize", "vvSafeNormalize");
            double Translucency(GlslEval.Val l)
            {
                var syms = GlslEval.Syms("l", l, "n", GlslEval.Vec(0, 0, 1), "v", GlslEval.Vec(0, 0, 1));
                string through = GlslEval.Rhs(GlslEval.Statement(transBody, "vec3 through"));
                syms["through"] = GlslEval.Eval(through, syms);
                string ret = GlslEval.Statement(transBody, "return");
                return GlslEval.Scalar(ret.Substring(ret.IndexOf("return", StringComparison.Ordinal) + 6).TrimEnd(';', ' '), syms);
            }
            double backlit = -1, frontlit = -1;
            try
            {
                backlit = Translucency(GlslEval.Vec(0, 0.196, -0.981));
                frontlit = Translucency(GlslEval.Vec(0, 0.196, 0.981));
            }
            catch (Exception ex) { check("terrain translucency is evaluable", false, ex.Message); }
            check("terrain leaves glow with the sun behind them and not in front",
                backlit > 0.3 && frontlit < 0.01,
                "backlit " + backlit.ToString("0.000") + ", front-lit " + frontlit.ToString("0.000"));

            // Sun visibility is read from the shadow map, not from vanilla's `b`.
            // `b` bottoms out at 1 - shadowIntensity/2 and has blockBrightness
            // added, so a shadowed leaf read half lit and a torch read as sun.
            string sunVisBody = GlslEval.StripComments(
                GlslEval.FunctionBody(terrainSnippet, "float vvTerrainSunVisibility(") ?? "");
            string floraShadeCode = GlslEval.StripComments(floraShadeBody);
            check("terrain sun visibility is geometric and sun-only",
                sunVisBody.Contains("texture(shadowMapFar") &&
                !sunVisBody.Contains("shadowIntensity") && !sunVisBody.Contains("blockBrightness") &&
                floraShadeCode.Contains("float sunVisibility = vvTerrainSunVisibility();") &&
                Regex.IsMatch(GlslEval.StripComments(foliageBody), @"\*\s*clamp\(sunVisibility, 0\.0, 1\.0\)") &&
                !floraShadeCode.Contains("shadowBrightness"),
                "flora must not read vanilla's combined brightness as sun visibility");

            // Canopy dapple on the modular path: off at zero, darken-only, and
            // never on the canopy itself.
            string dappleBody = GlslEval.StripComments(
                GlslEval.FunctionBody(terrainSnippet, "float vvTerrainCanopyDapple(") ?? "");
            string applyCanopy = GlslEval.StripComments(
                GlslEval.FunctionBody(terrainSnippet, "vec3 vvTerrainApplyCanopy(") ?? "");
            check("terrain canopy dapple is exactly off at a slider of zero",
                dappleBody.Contains("if (vv_pbrDapple < 0.001) return 0.0;") &&
                dappleBody.Contains("if (!vvTerrainIsCanopyReceiver()) return 0.0;"),
                "");
            check("terrain canopy dapple only darkens and spares local light",
                applyCanopy.Contains("color * (1.0 - canopy)") &&
                applyCanopy.Contains("(1.0 - vvTerrainLocalLightShare())") &&
                !Regex.IsMatch(applyCanopy, @"color\s*\+"),
                "");
            check("terrain canopy dapple reaches opaque blocks and plants",
                GlslEval.StripComments(primaryBody).Contains("diffuse = vvTerrainApplyCanopy(diffuse, vvTerrainCanopyDapple(") &&
                floraShadeCode.Contains("vvTerrainApplyCanopy(result, vvTerrainCanopyDapple("),
                "");
            // INTERACTION: the canopy term composed with the colour it is
            // applied to, evaluated from the shipped body. It may only take
            // light, never add any in any channel, and must leave a fragment
            // whose light is all local exactly as vanilla lit it.
            try
            {
                string canopyExpr = GlslEval.StubCall(GlslEval.Rhs(GlslEval.Statement(applyCanopy, "float canopy")),
                                                      "vvTerrainLocalLightShare", "local");
                string tintExpr = GlslEval.Rhs(GlslEval.Statement(applyCanopy, "float tint"));
                string outExpr = GlslEval.Rhs(GlslEval.Statement(applyCanopy, "return color *"));
                double maxShade = GlslEval.Const(terrainSnippet, "VV_TERRAIN_DAPPLE_SHADE") * 2.0;
                GlslEval.Val Apply(double shaded, double local, double cap)
                {
                    var syms = GlslEval.Syms("shaded", shaded, "local", local, "cap", cap,
                                             "VV_TERRAIN_DAPPLE_GREEN", GlslEval.Const(terrainSnippet, "VV_TERRAIN_DAPPLE_GREEN"));
                    double canopy = GlslEval.Scalar(canopyExpr, syms);
                    if (canopy <= 0.0) return GlslEval.Vec(1, 1, 1);
                    syms["canopy"] = new GlslEval.Val(canopy);
                    syms["tint"] = new GlslEval.Val(GlslEval.Scalar(tintExpr, syms));
                    syms["color"] = GlslEval.Vec(1, 1, 1);
                    return GlslEval.Eval(outExpr, syms);
                }
                var full = Apply(maxShade, 0.0, 0.85);
                var torch = Apply(maxShade, 1.0, 0.85);
                var leaf = Apply(maxShade, 0.0, 0.55);
                check("I-terrain canopy only takes light, in every channel, and spares local light",
                    full.X <= 1.0 && full.Y <= 1.0 && full.Z <= 1.0 && full.Y < 0.99 &&
                    Math.Abs(torch.X - 1.0) < 1e-9 && Math.Abs(torch.Y - 1.0) < 1e-9 && Math.Abs(torch.Z - 1.0) < 1e-9 &&
                    leaf.Y >= 0.45 - 1e-9 && full.Y >= 0.15 - 1e-9 &&
                    // The green is a tint on the shade, not a light: every
                    // channel stays within 5% of plain darkening.
                    new[] { full.X, full.Y, full.Z }.All(c => Math.Abs(c / (1.0 - Math.Min(maxShade, 0.85)) - 1.0) <= 0.05),
                    "full " + full + ", torch " + torch + ", leaf " + leaf);
            }
            catch (Exception ex) { check("I-terrain canopy composition is readable", false, ex.Message); }
            check("terrain leaf sheen is exactly off at a specular strength of zero",
                GlslEval.StripComments(GlslEval.FunctionBody(terrainSnippet, "vec3 vvTerrainCuticleSpecular(") ?? "")
                    .Contains("if (vv_pbrSpecularStrength < 0.001) return vec3(0.0);"),
                "");
            check("Stage C diffuse energy is not view-angle blackening",
                resolved.Contains("vec3 diffuseEnergy = (vec3(1.0) - f0 * 0.35) * (1.0 - m.metalness);") &&
                !resolved.Contains("vec3 diffuseEnergy = (vec3(1.0) - fresnel)"),
                "dielectric terrain diffuse must not collapse merely because the camera is grazing it");
            check("Stage C terrain material has no scene-capture, world-reflection or canopy reads",
                !resolved.Contains("texture(vv_reflect") &&
                !resolved.Contains("textureLod(vv_reflect") &&
                !resolved.Contains("texture(vv_canopy") &&
                !resolved.Contains("textureLod(vv_canopy"),
                "optional terrain resources remain disabled during safe material restoration");

            var logger = new CollectingLogger();
            var patcher = new ShaderPatcher(logger);
            patcher.SetPatches(patches);

            string opaque = patcher.Patch("chunkopaque.fsh", ChunkOpaqueVanilla);
            string topsoil = patcher.Patch("chunktopsoil.fsh", TopsoilVanilla);

            check("pbrterrainmaterial group healthy", patcher.IsGroupHealthy("pbrterrainmaterial"), "");
            check("chunkopaque receives primary material operation before outGlow",
                opaque.IndexOf("outColor = vvTerrainMaterialPrimary(outColor, texColor.rgb, normal, uv, worldPos.xyz, b, fogAmount, murkiness, rgbaFog.rgb, blockLight, glowLevel);", StringComparison.Ordinal) >= 0 &&
                opaque.IndexOf("outColor = vvTerrainMaterialPrimaryDebug(outColor, texColor.rgb, normal, uv, worldPos.xyz, b, fogAmount, murkiness, rgbaFog.rgb, blockLight, glowLevel);", StringComparison.Ordinal) >
                opaque.IndexOf("outColor = vvTerrainMaterialPrimary(outColor, texColor.rgb, normal, uv, worldPos.xyz, b, fogAmount, murkiness, rgbaFog.rgb, blockLight, glowLevel);", StringComparison.Ordinal) &&
                opaque.IndexOf("outGlow = vec4(glowLevel + glow, godrayLevel, 0, min(1, fogAmount + outColor.a));", StringComparison.Ordinal) >
                opaque.IndexOf("outColor = vvTerrainMaterialPrimaryDebug(outColor, texColor.rgb, normal, uv, worldPos.xyz, b, fogAmount, murkiness, rgbaFog.rgb, blockLight, glowLevel);", StringComparison.Ordinal),
                "");
            check("chunktopsoil receives primary material operation before outGlow",
                topsoil.IndexOf("outColor = vvTerrainMaterialPrimary(outColor, outColor.rgb, normal, uv, worldPos.xyz, 1.0, fogAmount, murkiness, rgbaFog.rgb, blockLight, glowLevel);", StringComparison.Ordinal) >= 0 &&
                topsoil.IndexOf("outColor = vvTerrainMaterialPrimaryDebug(outColor, outColor.rgb, normal, uv, worldPos.xyz, 1.0, fogAmount, murkiness, rgbaFog.rgb, blockLight, glowLevel);", StringComparison.Ordinal) >
                topsoil.IndexOf("outColor = vvTerrainMaterialPrimary(outColor, outColor.rgb, normal, uv, worldPos.xyz, 1.0, fogAmount, murkiness, rgbaFog.rgb, blockLight, glowLevel);", StringComparison.Ordinal) &&
                topsoil.IndexOf("outGlow = vec4(glowLevel + glow, 0, 0, outColor.a);", StringComparison.Ordinal) >
                topsoil.IndexOf("outColor = vvTerrainMaterialPrimaryDebug(outColor, outColor.rgb, normal, uv, worldPos.xyz, 1.0, fogAmount, murkiness, rgbaFog.rgb, blockLight, glowLevel);", StringComparison.Ordinal),
                "");
            check("Stage B preserves vanilla glow contracts",
                opaque.Contains("outGlow = vec4(glowLevel + glow, godrayLevel, 0, min(1, fogAmount + outColor.a));") &&
                topsoil.Contains("outGlow = vec4(glowLevel + glow, 0, 0, outColor.a);"),
                "");
            check("Stage B does not restore reflection or canopy resources",
                !opaque.Contains("vv_reflect") && !topsoil.Contains("vv_reflect") &&
                !opaque.Contains("vv_canopy") && !topsoil.Contains("vv_canopy"),
                "");

            ShaderPatchManifestEntry opaqueManifest = patcher.Manifest["chunkopaque.fsh"];
            ShaderPatchManifestEntry topsoilManifest = patcher.Manifest["chunktopsoil.fsh"];

            check("Stage C manifest exposes exactly two material samplers per terrain shader",
                opaqueManifest.SamplersAdded.SequenceEqual(new[] { "sampler2D vv_materialTex", "sampler2D vv_materialTex2" }) &&
                topsoilManifest.SamplersAdded.SequenceEqual(new[] { "sampler2D vv_materialTex", "sampler2D vv_materialTex2" }) &&
                opaqueManifest.SamplersRemoved.Count == 0 &&
                topsoilManifest.SamplersRemoved.Count == 0,
                opaqueManifest.ToStableText() + topsoilManifest.ToStableText());
        }
    }

    internal static class StringCountExtensions
    {
        public static int CountSubstring(this string text, string value)
        {
            int count = 0;
            int index = 0;

            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }
    }
}
