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
                // The leaf map is a uniform ARRAY (vv_canopyMap), deliberately
                // not a sampler; the legacy canopy texture must stay out.
                !System.Text.RegularExpressions.Regex.IsMatch(resolved, @"sampler2D\s+vv_canopy") &&
                resolved.CountSubstring("uniform sampler") == 4,
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
            string resolveBody = GlslEval.StripComments(
                GlslEval.FunctionBody(terrainSnippet, "vec3 vvTerrainCanopyResolve(") ?? "");
            check("terrain canopy dapple reaches opaque blocks and plants",
                GlslEval.StripComments(primaryBody).Contains("diffuse = vvTerrainCanopyResolve(diffuse,") &&
                floraShadeCode.Contains("result = vvTerrainCanopyResolve(result,") &&
                resolveBody.Contains("vvTerrainApplyCanopy(color, vvTerrainCanopyDapple("),
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
            // Sunflecks (v5). Off at zero, absent without a leaf map, never
            // reading past the toroidal map's edge, closed under cloud, and
            // never brighter than vanilla's own sunlit level.
            string fleckBody = GlslEval.StripComments(
                GlslEval.FunctionBody(terrainSnippet, "vec2 vvTerrainSunfleck(") ?? "");
            string fieldBody = GlslEval.StripComments(
                GlslEval.FunctionBody(terrainSnippet, "vec3 vvTerrainFleckField(") ?? "");
            string overheadBody = GlslEval.StripComments(
                GlslEval.FunctionBody(terrainSnippet, "vec2 vvTerrainCanopyOverhead(") ?? "");
            string cellBody = GlslEval.StripComments(
                GlslEval.FunctionBody(terrainSnippet, "vec2 vvTerrainCanopyCell(") ?? "");
            check("terrain canopy shade fades with overcast and is graded by the crown",
                dappleBody.Contains("mix(1.0, VV_OVERCAST_DIRECT, overcast)") &&
                dappleBody.Contains("float overcast = clamp(vv_sceneOvercast, 0.0, 1.0);") &&
                dappleBody.Contains("float crown = mix(0.6, 1.0, smoothstep(0.1, 0.85, clamp(leafOverhead, 0.0, 1.0)));") &&
                dappleBody.Contains("* under * crown * sun *"),
                "canopy shade is a shadow of the direct sun, and a thin crown passes more sky than a closed one");
            check("terrain sunflecks are exactly off at a slider of zero",
                fleckBody.Contains("if (vv_pbrSunflecks < 0.001 || overhead.x <= 0.0) return vec2(0.0);") &&
                resolveBody.Contains("if (vv_pbrDapple < 0.001 && vv_pbrSunflecks < 0.001) return color;"),
                "");
            check("terrain sunflecks need the leaf map and stay inside it",
                overheadBody.Contains("if (vv_canopyMapValid < 0.5) return vec2(0.0);") &&
                overheadBody.Contains("float reach = max(abs(rel.x), abs(rel.z));") &&
                overheadBody.Contains("if (reach > 28.0) break;") &&
                VintageVisuals.Common.Scene.CanopyMap.Grid * VintageVisuals.Common.Scene.CanopyMap.CellBlocks / 2
                    >= 28 + VintageVisuals.Common.Scene.CanopyMap.CellBlocks,
                "the march must stop inside half the map's span, or the toroidal map returns another place's leaves");
            // INTERACTION: an image of the sun needs the sun. Evaluated from
            // the shipped gate at the cover the v4 flecks drew under in game.
            try
            {
                string clearBody = GlslEval.StripComments(GlslEval.FunctionBody(terrainSnippet, "float vvTerrainClearSky(") ?? "");
                string clearExpr = GlslEval.Rhs(GlslEval.Statement(clearBody, "return"));
                double Clear(double cover) => GlslEval.Scalar(clearExpr, GlslEval.Syms("vv_sceneSkyCover", cover,
                    "VV_TERRAIN_FLECK_OVERCAST_START", GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_OVERCAST_START"),
                    "VV_TERRAIN_FLECK_OVERCAST_END", GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_OVERCAST_END")));
                check("I-terrain sunflecks close under cloud and stay whole under a clear sky",
                    Math.Abs(Clear(0.0) - 1.0) < 1e-9 && Clear(0.3) > 0.99 && Clear(0.74) < 1e-9 && Clear(0.55) > 0.0 && Clear(0.55) < 1.0 &&
                    fleckBody.Contains("float clearSky = vvTerrainClearSky();") &&
                    Regex.IsMatch(fleckBody, @"float gate = [^;]*\* clearSky \*") &&
                    !fleckBody.Contains("VV_OVERCAST_DIRECT"),
                    $"clear sky {Clear(0.0):0.00}, 30% {Clear(0.3):0.00}, 55% {Clear(0.55):0.00}, 74% {Clear(0.74):0.00} (v4 drew at 74%)");
            }
            catch (Exception ex) { check("I-terrain clear-sky gate is readable", false, ex.Message); }
            // Every way a fleck used to be cut by a straight line: a feature
            // reaching past the cells searched, flecks still drawing where the
            // leaf map's reach ends, and the canopy shade cut at the fleck
            // distance.
            try
            {
                double minStretch = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_MIN_STRETCH");
                // A gap: radius up to 0.55 * 1.15 cell, swaying up to 0.06,
                // against 2.2 cells from the furthest centre to the ring past
                // the 5x5 search. A leaf: half-length up to 0.24 cell plus its
                // 0.03 soft edge, and a speck up to 0.07, against 1.2 cells
                // past the 3x3 centre they use.
                double sway = 0.06;
                double gapReach = 0.55 * 1.15 / minStretch + sway;
                double leafReach = (0.24 + 0.03) / minStretch + sway;
                double speckReach = 0.07 / minStretch + sway;
                double fleckEnd = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_FADE_START") + GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_FADE_RANGE");
                double dappleEnd = GlslEval.Const(terrainSnippet, "VV_TERRAIN_DAPPLE_FADE_START") + GlslEval.Const(terrainSnippet, "VV_TERRAIN_DAPPLE_FADE_RANGE");
                check("I-terrain no sunfleck is clipped by its search, the leaf map's reach, or the shade's early exit",
                    fieldBody.Contains("stretch = max(stretch, VV_TERRAIN_FLECK_MIN_STRETCH);") &&
                    fieldBody.Contains("for (int dy = -2; dy <= 2; dy++)") &&
                    fieldBody.Contains("for (int dx = -2; dx <= 2; dx++)") &&
                    fieldBody.Contains("bool centre3 = abs(dx) <= 1 && abs(dy) <= 1;") &&
                    fieldBody.Contains("(cell + vec2(0.2) + r.yz * 0.6 + sway) * VV_TERRAIN_FLECK_CELL") &&
                    fieldBody.Contains("(cell + vec2(0.2) + q.xy * 0.6 + sway) * VV_TERRAIN_FLECK_CELL") &&
                    fieldBody.Contains("(cell + vec2(0.2) + k.yz * 0.6 + sway) * VV_TERRAIN_FLECK_CELL") &&
                    fieldBody.Contains("float radius = VV_TERRAIN_FLECK_CELL * mix(0.10, 0.55, size) * mix(0.85, 1.15, thin);") &&
                    fieldBody.Contains("float kernel = max(0.0, 1.0 - x * x);") &&
                    fieldBody.Contains("VV_TERRAIN_FLECK_CELL * mix(0.12, 0.24, q.w)") &&
                    fieldBody.Contains("VV_TERRAIN_FLECK_CELL * mix(0.035, 0.07, k.w)") &&
                    fieldBody.Contains("smoothstep(-0.03, 0.03, inside / VV_TERRAIN_FLECK_CELL)") &&
                    fieldBody.Contains("float swayAmount = 0.02 + 0.04 * shimmer;") &&
                    gapReach < 2.2 && leafReach < 1.2 && speckReach < 1.2 && fleckEnd <= 28.0 && fleckEnd <= dappleEnd &&
                    GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_VIEW_LIMIT") <= dappleEnd &&
                    fleckBody.Contains("float flatDistance = max(abs(cameraRelativePos.x), abs(cameraRelativePos.z));") &&
                    fleckBody.Contains("smoothstep(VV_TERRAIN_FLECK_VIEW_LIMIT * 0.75, VV_TERRAIN_FLECK_VIEW_LIMIT, length(cameraRelativePos))") &&
                    resolveBody.Contains("if (length(cameraRelativePos) > VV_TERRAIN_DAPPLE_FADE_START + VV_TERRAIN_DAPPLE_FADE_RANGE) return color;"),
                    $"gap reach {gapReach:0.00} (< 2.2), leaf {leafReach:0.00} and speck {speckReach:0.00} (< 1.2) cells, flecks end at {fleckEnd} (<= 28 and <= {dappleEnd})");
            }
            catch (Exception ex) { check("I-terrain sunfleck extent is readable", false, ex.Message); }
            // INTERACTION: a low sun stretches a fleck along its azimuth and
            // leaves it round at noon. Evaluated on the shipped offset.
            try
            {
                string offsetBody = GlslEval.StripComments(GlslEval.FunctionBody(terrainSnippet, "vec2 vvTerrainFleckOffset(") ?? "");
                string dExpr = GlslEval.Rhs(GlslEval.Statement(offsetBody, "vec2 d"));
                string outExpr = GlslEval.Rhs(GlslEval.Statement(offsetBody, "return"));
                double Len(double ax, double az, double px, double pz, double stretch)
                {
                    var syms = GlslEval.Syms("src", GlslEval.Vec(px, pz, 0), "feature", GlslEval.Vec(0, 0, 0),
                                             "az", GlslEval.Vec(ax, az, 0), "stretch", stretch);
                    syms["d"] = GlslEval.Eval(dExpr, syms);
                    var v = GlslEval.Eval(outExpr, syms);
                    return Math.Sqrt(v.X * v.X + v.Y * v.Y);
                }
                double minStretch = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_MIN_STRETCH");
                // A point one unit along the azimuth reads as minStretch away
                // (the fleck reaches 1/minStretch along the sun); across it, 1.
                bool ok = Math.Abs(Len(1, 0, 1, 0, minStretch) - minStretch) < 1e-9 &&
                          Math.Abs(Len(1, 0, 0, 1, minStretch) - 1.0) < 1e-9 &&
                          Math.Abs(Len(1, 0, 1, 0, 1.0) - 1.0) < 1e-9 &&
                          minStretch <= 0.35 &&
                          fleckBody.Contains("vvTerrainFleckField(src, az, l.y,");
                check("I-terrain a low sun stretches flecks along its azimuth, about 3x at the floor", ok,
                      $"min stretch {minStretch}");
            }
            catch (Exception ex) { check("I-terrain sunfleck stretch is readable", false, ex.Message); }
            // INTERACTION: a leaf covers nothing beyond its tips. Evaluated on
            // the shipped profile and edge, on the leaf's own axis.
            try
            {
                string insideExpr = GlslEval.Rhs(GlslEval.Statement(fieldBody, "float inside"));
                double cell = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_CELL");
                bool ok = fieldBody.Contains("leaf = max(leaf, 1.0 - smoothstep(-0.03, 0.03, inside / VV_TERRAIN_FLECK_CELL));");
                string detail = "";
                foreach (double past in new[] { 1.5, 2.0, 4.0 })
                {
                    double halfLength = 0.3, halfWidth = 0.12;
                    var syms = GlslEval.Syms("halfLength", halfLength, "halfWidth", halfWidth);
                    syms["uv"] = new GlslEval.Val(past * halfLength, 0.0, 0.0);
                    double inside = GlslEval.Scalar(insideExpr, syms);
                    if (inside / cell < 0.03) { ok = false; detail += $" {past}x tip -> inside {inside:0.000}"; }
                }
                check("I-terrain a sunfleck leaf covers nothing past its tips", ok, detail);
            }
            catch (Exception ex) { check("I-terrain sunfleck leaf profile is readable", false, ex.Message); }
            // INTERACTION: the penumbra is a half-lit rim that grows with the
            // height of the crown, and nothing else. Evaluated on the shipped
            // steps: under a 3-block crown there is no rim to speak of; under
            // a 20-block one, a field just under the pool threshold is half lit.
            try
            {
                string softExpr = GlslEval.Rhs(GlslEval.Statement(fleckBody, "float soft"));
                string litExpr = GlslEval.Rhs(GlslEval.Statement(fieldBody, "float lit"));
                string penExpr = GlslEval.Rhs(GlslEval.Statement(fieldBody, "float penumbra"));
                double rate = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_PENUMBRA");
                double Soft(double rise) => GlslEval.Scalar(softExpr, GlslEval.Syms("rise", rise, "VV_TERRAIN_FLECK_PENUMBRA", rate));
                double Level(double field, double soft)
                {
                    var syms = GlslEval.Syms("field", field, "soft", soft);
                    return Math.Max(GlslEval.Scalar(litExpr, syms), GlslEval.Scalar(penExpr, syms));
                }
                bool ok = Soft(3.0) <= 0.05 && Soft(20.0) > Soft(3.0) && Soft(200.0) <= 0.3 + 1e-9 &&
                          Math.Abs(Level(0.45, Soft(20.0)) - 0.5) < 1e-9 && Level(0.45, Soft(3.0)) == 0.0 &&
                          Level(0.6, Soft(20.0)) == 1.0 && Level(0.1, Soft(20.0)) == 0.0;
                check("I-terrain the penumbra is a half-lit rim that widens with crown height", ok,
                      $"rim {Soft(3.0):0.000} at 3 blocks, {Soft(20.0):0.000} at 20");
            }
            catch (Exception ex) { check("I-terrain sunfleck penumbra is readable", false, ex.Message); }
            check("terrain sunflecks step at the block texel and project from one canopy plane",
                fleckBody.Contains("vvTerrainIsFlora() ? vec3(0.0) : step(vec3(0.5), abs(faceNormal))") &&
                fleckBody.Contains("world = mix((texel + 0.5) / VV_TERRAIN_FLECK_TEXELS, world, across);") &&
                fleckBody.Contains("float rise = max(vv_canopyPlaneY - world.y, 1.0);") &&
                Math.Abs(GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_TEXELS") - 16.0) < 1e-9 &&
                double.IsNaN(VintageVisuals.Common.Scene.CanopyMap.MeanCanopyY(new int[4])) &&
                Math.Abs(VintageVisuals.Common.Scene.CanopyMap.MeanCanopyY(new[] { 0, VintageVisuals.Common.Scene.CanopyMap.Encode(3, 100), VintageVisuals.Common.Scene.CanopyMap.Encode(7, 110) }) - 106.5) < 1e-9,
                "a per-cell projection height cuts flecks at cell edges; snapping across a face speckles it");
            // Every straight line seen through a fleck came from a step in the
            // leaf-map read: a hard height test, or an early return that
            // switched march steps between neighbouring fragments.
            string leafAtBody = GlslEval.StripComments(
                GlslEval.FunctionBody(terrainSnippet, "float vvTerrainCanopyLeafAt(") ?? "");
            check("terrain leaf-map read is continuous in position (no square flecks)",
                overheadBody.Contains("if (density > best.x) best = vec2(density, p.y - world.y);") &&
                !Regex.IsMatch(overheadBody, @"return\s+vec2\(\s*density") &&
                overheadBody.Contains("f = f * f * (3.0 - 2.0 * f);") &&
                leafAtBody.Contains("smoothstep(cell.y - 1.5, cell.y + 0.5, rayY)") &&
                leafAtBody.Contains("smoothstep(floorY + 0.5, floorY + 2.5, cell.y)"),
                "a hard leaf test or an early return draws axis-aligned squares under a low sun");
            // CPU and GPU must agree on the cell, the index and the encoding.
            check("leaf map encoding agrees between CanopyMap and the shader",
                VintageVisuals.Common.Scene.CanopyMap.Encode(5, 300) == 5 * 512 + 300 &&
                VintageVisuals.Common.Scene.CanopyMap.Grid == 32 &&
                VintageVisuals.Common.Scene.CanopyMap.CellBlocks == 2 &&
                VintageVisuals.Common.Scene.CanopyMap.PackedVectors == 128 &&
                terrainSnippet.Contains("uniform vec4 vv_canopyMap[128];") &&
                cellBody.Contains("ivec2(floor(worldXZ / 2.0))") &&
                cellBody.Contains("int i = iz * 32 + ix;") &&
                cellBody.Contains("(i & 1) == 0 ? mod(cellPair, 4096.0) : floor(cellPair / 4096.0)") &&
                cellBody.Contains("vec2(floor(code / 512.0) / 7.0, mod(code, 512.0))") &&
                VintageVisuals.Common.Scene.CanopyMap.Index(-1, -1) == 31 * 32 + 31 &&
                VintageVisuals.Common.Scene.CanopyMap.Index(4096 + 2, 0) == VintageVisuals.Common.Scene.CanopyMap.Index(2, 0) &&
                4096 % (VintageVisuals.Common.Scene.CanopyMap.Grid * VintageVisuals.Common.Scene.CanopyMap.CellBlocks) == 0 &&
                Math.Abs(GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_CELL") * GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_PERIOD") - 4096.0) < 1e-6 &&
                Math.Abs(GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_CLUSTER_CELL") * GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_CLUSTER_PERIOD") - 4096.0) < 1e-6,
                "");

            // INTERACTION: a fleck lifts shade back to vanilla's sunlit level
            // and never past it. Evaluated from the shipped relight.
            try
            {
                string relightBody = GlslEval.StripComments(
                    GlslEval.FunctionBody(terrainSnippet, "float vvTerrainSunRelight(") ?? "");
                string vanillaExpr = GlslEval.Rhs(GlslEval.Statement(relightBody, "float vanilla"));
                string relightExpr = GlslEval.Rhs(GlslEval.Statement(relightBody, "return"));
                double Relight(double vis, double si)
                {
                    var syms = GlslEval.Syms("sunVisibility", vis, "shadowIntensity", si);
                    syms["vanilla"] = new GlslEval.Val(GlslEval.Scalar(vanillaExpr, syms));
                    return GlslEval.Scalar(relightExpr, syms);
                }
                bool ok = true; string detail = "";
                foreach (double si in new[] { 0.3, 0.6, 1.0 })
                foreach (double vis in new[] { 0.0, 0.5, 1.0 })
                {
                    double shadowedLevel = 1.0 - si * 0.5 * (1.0 - vis);
                    double restored = shadowedLevel * Relight(vis, si);
                    if (restored > 1.0 + 1e-9 || restored < 1.0 - 1e-9) { ok = false; detail += $" si={si} vis={vis} -> {restored:0.000}"; }
                }
                // The resolve may only ever mix TOWARD that level: fleck plus
                // bounce clamped to one, and the bounce capped well under it.
                ok &= resolveBody.Contains("return mix(shaded, lit, clamp(fleck.x + fleck.y, 0.0, 1.0) * local);") &&
                      fleckBody.Contains("float bounce = clamp(field.y * VV_TERRAIN_FLECK_BOUNCE * gate, 0.0, VV_TERRAIN_FLECK_BOUNCE);") &&
                      GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_BOUNCE") <= 0.1;
                check("I-terrain a sunfleck restores vanilla's sunlit level exactly and never exceeds it", ok, detail);
            }
            catch (Exception ex) { check("I-terrain sunfleck relight is readable", false, ex.Message); }
            // INTERACTION: the sunlit tint shifts hue and saturation only, by
            // day, at golden hour and by moonlight. A fleck at vanilla's
            // sunlit level must stay at that luminance, or the relight's cap
            // above means nothing.
            try
            {
                string tintBody = GlslEval.StripComments(
                    GlslEval.FunctionBody(terrainSnippet, "vec3 vvTerrainSunlitTint(") ?? "");
                string yExpr = GlslEval.Rhs(GlslEval.Statement(tintBody, "float y"));
                string hueExpr = GlslEval.Rhs(GlslEval.Statement(tintBody, "vec3 hue"));
                string warmExpr = GlslEval.Rhs(GlslEval.Statement(tintBody, "vec3 warm"));
                string outExpr = GlslEval.Rhs(GlslEval.Statement(tintBody, "return"));
                double sat = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_SATURATION");
                double Luma(GlslEval.Val v) => GlslEval.Scalar("dot(c, vec3(0.2126, 0.7152, 0.0722))", GlslEval.Syms("c", v));
                bool ok = sat >= 1.0 && sat <= 1.1 &&
                          resolveBody.Contains("vec3 lit = vvTerrainSunlitTint(color * vvTerrainSunRelight(sunVisibility), golden, night);");
                string detail = "";
                foreach (var light in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0) })
                foreach (var c in new[] { GlslEval.Vec(0.37, 0.29, 0.20), GlslEval.Vec(0.25, 0.45, 0.12), GlslEval.Vec(0.5, 0.5, 0.5), GlslEval.Vec(0.9, 0.85, 0.6) })
                {
                    var syms = GlslEval.Syms("c", c, "VV_TERRAIN_FLECK_SATURATION", sat, "golden", light.Item1, "night", light.Item2);
                    syms["y"] = new GlslEval.Val(GlslEval.Scalar(yExpr, syms));
                    syms["hue"] = GlslEval.Eval(hueExpr, syms);
                    syms["warm"] = GlslEval.Eval(warmExpr, syms);
                    var after = GlslEval.Eval(outExpr, syms);
                    // the same colour at noon, to compare golden hour and night against
                    var noonSyms = GlslEval.Syms("c", c, "VV_TERRAIN_FLECK_SATURATION", sat, "golden", 0.0, "night", 0.0);
                    noonSyms["y"] = new GlslEval.Val(GlslEval.Scalar(yExpr, noonSyms));
                    noonSyms["hue"] = GlslEval.Eval(hueExpr, noonSyms);
                    noonSyms["warm"] = GlslEval.Eval(warmExpr, noonSyms);
                    var noon = GlslEval.Eval(outExpr, noonSyms);
                    double before = Luma(c);
                    if (Math.Abs(Luma(after) - before) > before * 0.01) { ok = false; detail += $" {before:0.000}->{Luma(after):0.000}"; }
                    // Golden hour is warmer than noon, moonlight cooler.
                    if (light.Item1 > 0.5 && !(after.X / Math.Max(after.Z, 1e-6) > noon.X / Math.Max(noon.Z, 1e-6) + 0.01)) { ok = false; detail += " golden not warmer than noon"; }
                    if (light.Item2 > 0.5 && !(after.Z / Math.Max(after.X, 1e-6) > noon.Z / Math.Max(noon.X, 1e-6) + 0.01)) { ok = false; detail += " moon not cooler than noon"; }
                }
                check("I-terrain the sunfleck tint keeps vanilla's sunlit luminance, warmer at golden hour, cooler by moon", ok, detail);
            }
            catch (Exception ex) { check("I-terrain sunfleck tint is readable", false, ex.Message); }
            // INTERACTION: moonlight flecks are a faint share of the sun's,
            // follow the game's moon, and are nothing with no moon.
            try
            {
                string lightBody = GlslEval.StripComments(GlslEval.FunctionBody(terrainSnippet, "float vvTerrainFleckLight(") ?? "");
                string sunExpr = GlslEval.Rhs(GlslEval.Statement(lightBody, "float sun"));
                string moonExpr = GlslEval.Rhs(GlslEval.Statement(lightBody, "float moon"));
                double moonCap = GlslEval.Const(terrainSnippet, "VV_TERRAIN_MOON_FLECK");
                double Light(double sun, double moonLight, double slider)
                {
                    var syms = GlslEval.Syms("vv_sceneMoonLight", moonLight, "vv_pbrMoonFlecks", slider, "VV_TERRAIN_MOON_FLECK", moonCap);
                    syms["sun"] = new GlslEval.Val(sun);
                    double moon = GlslEval.Scalar(moonExpr, syms);
                    return Math.Max(sun, moon);
                }
                bool ok = sunExpr.Contains("vvTerrainSunPresence()") &&
                          Light(0.0, 0.0, 1.0) == 0.0 && Light(0.0, 1.0, 0.0) == 0.0 &&
                          Light(0.0, 1.0, 1.0) <= 0.5 && Light(0.0, 1.0, 1.0) > 0.1 &&
                          Math.Abs(Light(1.0, 1.0, 1.0) - 1.0) < 1e-9 &&
                          fleckBody.Contains("float light = vvTerrainFleckLight();") &&
                          resolveBody.Contains("if (vvTerrainFleckLight() < 0.01) return color;");
                check("I-terrain moonlight flecks are faint, follow the moon, and are nothing without one", ok,
                      $"full moon at full slider {Light(0.0, 1.0, 1.0):0.00} of the sun's");
            }
            catch (Exception ex) { check("I-terrain moonlight flecks are readable", false, ex.Message); }
            // INTERACTION: wind flutters the gaps and still air does not.
            try
            {
                string weightExpr = GlslEval.Rhs(GlslEval.Statement(fieldBody, "float weight"));
                double Weight(double shimmer, double flutter) => GlslEval.Scalar(weightExpr,
                    GlslEval.Syms("present", 1.0, "size", 1.0, "shimmer", shimmer, "flutter", flutter));
                string swayExpr = GlslEval.Rhs(GlslEval.Statement(fieldBody, "float swayAmount"));
                double Sway(double shimmer) => GlslEval.Scalar(swayExpr, GlslEval.Syms("shimmer", shimmer));
                bool ok = Math.Abs(Weight(0.0, 1.0) - Weight(0.0, -1.0)) < 1e-9 &&
                          Sway(1.0) > Sway(0.0) * 1.5 && Sway(0.0) > 0.0 &&
                          Weight(1.0, 1.0) < Weight(1.0, -1.0) && Weight(1.0, 1.0) >= 0.65 &&
                          fleckBody.Contains("vvTerrainFleckShimmer()") &&
                          GlslEval.StripComments(GlslEval.FunctionBody(terrainSnippet, "float vvTerrainFleckShimmer(") ?? "").Contains("clamp(vv_sceneWind, 0.0, 1.0)") &&
                          !Regex.IsMatch(fieldBody, @"turn \* [0-9]+\.[1-9]");
                check("I-terrain sunflecks flutter in the game's wind, on whole turns of the breeze clock", ok,
                      $"still {Weight(0.0, 1.0):0.00}, gusting {Weight(1.0, 1.0):0.00}..{Weight(1.0, -1.0):0.00}");
            }
            catch (Exception ex) { check("I-terrain sunfleck shimmer is readable", false, ex.Message); }
            // Sun shafts on the modular path: off at zero, only from leaves at
            // real leaf-map gaps, capped, held back where the scene is hard to
            // read, closed under cloud, and humid air carries them.
            try
            {
                string shaftBody = GlslEval.StripComments(GlslEval.FunctionBody(terrainSnippet, "float vvTerrainShaftLevel(") ?? "");
                string humidExpr = GlslEval.Rhs(GlslEval.Statement(shaftBody, "float humid"));
                double Humid(double wet, double low, double fog) => GlslEval.Scalar(humidExpr,
                    GlslEval.Syms("vv_sceneWetness", wet, "lowSun", low, "fog", fog));
                string materialYaml = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/pbrterrainmaterial.yaml"));
                bool ok = shaftBody.Contains("if (vv_pbrShafts < 0.001) return 0.0;") &&
                          shaftBody.Contains("if (vvTerrainFloraClass() != VV_TFLORA_LEAVES) return 0.0;") &&
                          shaftBody.Contains("float crown = vvTerrainCanopyCell(world.xz).x;") &&
                          shaftBody.Contains("float clearSky = vvTerrainClearSky();") &&
                          shaftBody.Contains("* vvSceneVisibilityDampen()") &&
                          shaftBody.Contains("return clamp(level, 0.0, VV_TERRAIN_SHAFT_MAX);") &&
                          GlslEval.Const(terrainSnippet, "VV_TERRAIN_SHAFT_MAX") <= 0.4 &&
                          materialYaml.Contains("godrayLevel = min(1.0, godrayLevel + vvTerrainShaftLevel(worldPos.xyz, fogDensityIn));") &&
                          Math.Abs(Humid(0, 0, 0) - 0.35) < 1e-9 && Math.Abs(Humid(1, 0, 0) - 1.0) < 1e-9 &&
                          Math.Abs(Humid(0, 1, 0) - 1.0) < 1e-9 && Math.Abs(Humid(0, 0, 1) - 1.0) < 1e-9;
                check("I-terrain sun shafts start at real gaps, are capped, and humid air carries them", ok,
                      $"dry noon {Humid(0, 0, 0):0.00}, after rain {Humid(1, 0, 0):0.00}");
            }
            catch (Exception ex) { check("I-terrain sun shafts are readable", false, ex.Message); }
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
                !Regex.IsMatch(opaque, @"sampler2D\s+vv_canopy") && !Regex.IsMatch(topsoil, @"sampler2D\s+vv_canopy"),
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
