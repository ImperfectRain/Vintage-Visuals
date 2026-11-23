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
            // whose light is all local exactly as vanilla lit it. v9: on the
            // autumn floor the shade must come out COOLER and LESS SATURATED
            // than plain darkening (skylit, not a dim copy of the sunlit
            // colour), and that shift must vanish with the canopy term.
            try
            {
                string canopyExpr = GlslEval.StubCall(GlslEval.Rhs(GlslEval.Statement(applyCanopy, "float canopy")),
                                                      "vvTerrainLocalLightShare", "local");
                string darkExpr = GlslEval.Rhs(GlslEval.Statement(applyCanopy, "vec3 dark"));
                string yExpr = GlslEval.Rhs(GlslEval.Statement(applyCanopy, "float y"));
                string muteExpr = GlslEval.Rhs(GlslEval.Statement(applyCanopy, "dark = mix(dark"));
                string tintExpr = GlslEval.Rhs(GlslEval.Statement(applyCanopy, "float tint"));
                string outExpr = GlslEval.Rhs(GlslEval.Statement(applyCanopy, "return", "tint"));
                double maxShade = GlslEval.Const(terrainSnippet, "VV_TERRAIN_DAPPLE_SHADE") * 2.0;
                GlslEval.Val Apply(GlslEval.Val color, double shaded, double local, double cap)
                {
                    var syms = GlslEval.Syms("shaded", shaded, "local", local, "cap", cap,
                                             "VV_TERRAIN_DAPPLE_SKY", GlslEval.Const(terrainSnippet, "VV_TERRAIN_DAPPLE_SKY"),
                                             "VV_TERRAIN_DAPPLE_MUTE", GlslEval.Const(terrainSnippet, "VV_TERRAIN_DAPPLE_MUTE"));
                    syms["color"] = color;
                    double canopy = GlslEval.Scalar(canopyExpr, syms);
                    if (canopy <= 0.0) return color;
                    syms["canopy"] = new GlslEval.Val(canopy);
                    var dark = GlslEval.Eval(darkExpr, syms);
                    syms["dark"] = dark;
                    syms["y"] = new GlslEval.Val(GlslEval.Scalar(yExpr, syms));
                    syms["dark"] = GlslEval.Eval(muteExpr, syms);
                    syms["tint"] = new GlslEval.Val(GlslEval.Scalar(tintExpr, syms));
                    return GlslEval.Eval(outExpr, syms);
                }
                var white = GlslEval.Vec(1, 1, 1);
                var olive = GlslEval.Vec(0.46, 0.42, 0.14);   // autumn grass, vanilla-lit
                var full = Apply(white, maxShade, 0.0, 0.85);
                var torch = Apply(olive, maxShade, 1.0, 0.85);
                var leaf = Apply(white, maxShade, 0.0, 0.55);
                var floor = Apply(olive, 0.4, 0.0, 0.85);
                var faint = Apply(olive, 0.01, 0.0, 0.85);
                var yellow = GlslEval.Vec(0.5, 0.5, 0.0);     // no blue at all: the fill must not invent any
                var yellowShade = Apply(yellow, 0.2, 0.0, 0.85);
                double Sat(GlslEval.Val c) => (Math.Max(c.X, Math.Max(c.Y, c.Z)) - Math.Min(c.X, Math.Min(c.Y, c.Z)))
                                              / Math.Max(c.X, Math.Max(c.Y, c.Z));
                bool takesOnly(GlslEval.Val o, GlslEval.Val c) => o.X <= c.X + 1e-9 && o.Y <= c.Y + 1e-9 && o.Z <= c.Z + 1e-9;
                check("I-terrain canopy only takes light, in every channel, and spares local light",
                    takesOnly(full, white) && takesOnly(floor, olive) && takesOnly(faint, olive) && takesOnly(yellowShade, yellow) && full.Y < 0.99 &&
                    Math.Abs(torch.X - olive.X) < 1e-9 && Math.Abs(torch.Y - olive.Y) < 1e-9 && Math.Abs(torch.Z - olive.Z) < 1e-9 &&
                    leaf.Y >= 0.45 * 0.95 - 1e-9 && full.Y >= 0.15 - 1e-9 &&
                    // The skylight is a tint on the shade, not a light: on a
                    // grey surface every channel stays within 10% of plain darkening.
                    new[] { full.X, full.Y, full.Z }.All(c => Math.Abs(c / (1.0 - Math.Min(maxShade, 0.85)) - 1.0) <= 0.10),
                    "full " + full + ", torch " + torch + ", leaf " + leaf);
                check("I-terrain canopy shade is skylit: cooler and less saturated than the sunlit floor, and only where it shades",
                    Sat(floor) < Sat(olive) - 0.05 &&
                    full.Z > full.X + 0.01 &&
                    floor.Z / floor.X > olive.Z / olive.X * 1.05 &&
                    Math.Abs(Sat(faint) - Sat(olive)) < 0.01 &&
                    // still darker overall than the sunlit colour, by about the canopy term
                    floor.Y < olive.Y * 0.7,
                    $"olive sat {Sat(olive):0.000} -> shade {Sat(floor):0.000}, b/r {olive.Z / olive.X:0.000} -> {floor.Z / floor.X:0.000}, faint sat {Sat(faint):0.000}");
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
                GlslEval.FunctionBody(terrainSnippet, "vec3 vvTerrainCanopyOverhead(") ?? "");
            string cellBody = GlslEval.StripComments(
                GlslEval.FunctionBody(terrainSnippet, "vec2 vvTerrainCanopyCell(") ?? "");
            check("terrain canopy shade fades with overcast and is graded by the crown",
                dappleBody.Contains("mix(1.0, VV_OVERCAST_DIRECT, overcast)") &&
                dappleBody.Contains("float overcast = clamp(vv_sceneOvercast, 0.0, 1.0);") &&
                dappleBody.Contains("float crown = mix(0.3, 1.0, smoothstep(0.1, 0.85, clamp(leafOverhead, 0.0, 1.0)));") &&
                dappleBody.Contains("* under * crown * sun *"),
                "canopy shade is a shadow of the direct sun, and a thin crown passes more sky than a closed one");
            // INTERACTION: canopy shade deepens toward a low sun, so a golden-
            // hour forest floor is dark enough for its flecks and beams to
            // read, and the caller's cap still bounds it.
            try
            {
                string lowExpr = GlslEval.Rhs(GlslEval.Statement(dappleBody, "float lowSun"));
                double Low(double y) => GlslEval.Scalar(lowExpr, GlslEval.Syms("l", GlslEval.Vec(0, y, 0)));
                double gainLow = GlslEval.Const(terrainSnippet, "VV_TERRAIN_DAPPLE_LOW_SUN");
                string applyBody = GlslEval.StripComments(GlslEval.FunctionBody(terrainSnippet, "vec3 vvTerrainApplyCanopy(") ?? "");
                check("I-terrain canopy shade deepens toward a low sun and stays under its cap",
                    Low(0.9) < 1e-9 && Low(0.1) > 0.99 && gainLow > 1.2 && gainLow <= 2.0 &&
                    dappleBody.Contains("* mix(1.0, VV_TERRAIN_DAPPLE_LOW_SUN, lowSun) *") &&
                    applyBody.Contains("float canopy = clamp(shaded, 0.0, cap)"),
                    $"noon gain 1, low sun {gainLow}");
            }
            catch (Exception ex) { check("I-terrain low-sun canopy shade is readable", false, ex.Message); }
            check("terrain sunflecks are exactly off at a slider of zero",
                fleckBody.Contains("if (vv_pbrSunflecks < 0.001 || overhead.x <= 0.0) return vec2(0.0);") &&
                resolveBody.Contains("if (vv_pbrDapple < 0.001 && vv_pbrSunflecks < 0.001) return color;"),
                "");
            check("terrain sunflecks need the leaf map and stay inside it",
                overheadBody.Contains("if (vv_canopyMapValid < 0.5) return vec3(0.0);") &&
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
            // reaching past a cell search (v7 has no search at all), a grid
            // that does not tile the world wrap, flecks still drawing where
            // the leaf map's reach ends, and the canopy shade cut at the
            // fleck distance.
            try
            {
                double cellSize = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_CELL");
                double period = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_PERIOD");
                double lobeCell = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_LOBE_CELL");
                double lobePeriod = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_LOBE_PERIOD");
                double fleckEnd = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_FADE_START") + GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_FADE_RANGE");
                double dappleEnd = GlslEval.Const(terrainSnippet, "VV_TERRAIN_DAPPLE_FADE_START") + GlslEval.Const(terrainSnippet, "VV_TERRAIN_DAPPLE_FADE_RANGE");
                bool tiles = Math.Abs(cellSize * period - 4096.0) < 1e-6 && Math.Abs(lobeCell * lobePeriod - 4096.0) < 1e-6 &&
                             fieldBody.Contains("VV_TERRAIN_FLECK_CELL * 2.0, VV_TERRAIN_FLECK_PERIOD * 0.5") &&
                             fieldBody.Contains("VV_TERRAIN_FLECK_CELL * 0.5, VV_TERRAIN_FLECK_PERIOD * 2.0") &&
                             fieldBody.Contains("VV_TERRAIN_FLECK_CELL, VV_TERRAIN_FLECK_PERIOD,") &&
                             fieldBody.Contains("VV_TERRAIN_FLECK_LOBE_CELL, VV_TERRAIN_FLECK_LOBE_PERIOD,") &&
                             !Regex.IsMatch(fieldBody, @"vvTerrainFleckNoise\([^;]*VV_TERRAIN_FLECK_CELL \* [0-9.]+, VV_TERRAIN_FLECK_PERIOD \* (?!0\.5|2\.0)");
                check("I-terrain no sunfleck is clipped by its search, the leaf map's reach, or the shade's early exit",
                    tiles && !fieldBody.Contains("for (int dx") && !fieldBody.Contains("for (int dy") &&
                    fleckEnd <= 28.0 && fleckEnd <= dappleEnd &&
                    GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_VIEW_LIMIT") <= dappleEnd &&
                    fleckBody.Contains("float flatDistance = max(abs(cameraRelativePos.x), abs(cameraRelativePos.z));") &&
                    fleckBody.Contains("smoothstep(VV_TERRAIN_FLECK_VIEW_LIMIT * 0.75, VV_TERRAIN_FLECK_VIEW_LIMIT, length(cameraRelativePos))") &&
                    resolveBody.Contains("if (length(cameraRelativePos) > VV_TERRAIN_DAPPLE_FADE_START + VV_TERRAIN_DAPPLE_FADE_RANGE) return color;"),
                    $"grids tile the wrap: {tiles}; flecks end at {fleckEnd} (<= 28 and <= {dappleEnd})");
            }
            catch (Exception ex) { check("I-terrain sunfleck extent is readable", false, ex.Message); }
            // INTERACTION: a low sun streaks the field along its azimuth and
            // leaves it unstreaked at noon. Evaluated on the shipped reach.
            try
            {
                string reachExpr = GlslEval.Rhs(GlslEval.Statement(fieldBody, "float reach"));
                double cellSize = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_CELL");
                double minStretch = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_MIN_STRETCH");
                double Reach(double stretch) => GlslEval.Scalar(reachExpr, GlslEval.Syms("stretch", stretch,
                    "VV_TERRAIN_FLECK_CELL", cellSize, "VV_TERRAIN_FLECK_STREAK", GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_STREAK")));
                bool ok = Math.Abs(Reach(1.0)) < 1e-9 && Reach(minStretch) > 0.5 && Reach(minStretch) < 2.0 * cellSize &&
                          Reach(0.5) < Reach(minStretch) && minStretch <= 0.35 &&
                          fieldBody.Contains("stretch = max(stretch, VV_TERRAIN_FLECK_MIN_STRETCH);") &&
                          fieldBody.Contains("vec2 q = p + az * (float(t) * reach);") &&
                          fleckBody.Contains("vvTerrainFleckField(src, az, l.y,");
                check("I-terrain a low sun streaks flecks along its azimuth and noon does not", ok,
                      $"streak reach at noon {Reach(1.0):0.00}, at the lowest sun {Reach(minStretch):0.00} blocks");
            }
            catch (Exception ex) { check("I-terrain sunfleck stretch is readable", false, ex.Message); }
            // INTERACTION (v7, from the player's reference photographs): the
            // lit share of the floor follows the crown - mostly shade with
            // holes under a closed crown, about half lit under a thin one -
            // with one hard, texel-jittered edge. The shipped expansion, cut
            // and coverage are evaluated over a stand-in of the shipped noise
            // sum, so a contrast that piles the field up around the threshold
            // (the v6 fBm defect) or a coverage that no longer reaches either
            // end fails here.
            try
            {
                string covBody = GlslEval.StripComments(GlslEval.FunctionBody(terrainSnippet, "float vvTerrainFleckCoverage(") ?? "");
                string covExpr = GlslEval.Rhs(GlslEval.Statement(covBody, "return"));
                double closed = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_COVER_CLOSED");
                double open = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_COVER_OPEN");
                double Cov(double thin) => GlslEval.Scalar(covExpr, GlslEval.Syms("thin", thin,
                    "VV_TERRAIN_FLECK_COVER_CLOSED", closed, "VV_TERRAIN_FLECK_COVER_OPEN", open));
                string uExpr = GlslEval.Rhs(GlslEval.Statement(fieldBody, "float u"));
                string thrExpr = GlslEval.Rhs(GlslEval.Statement(fieldBody, "float threshold"));
                string levelExpr = GlslEval.Rhs(GlslEval.Statement(fieldBody, "float level"));
                double contrast = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_CONTRAST");
                double ragged = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_RAGGED");
                // Stand-in for the shipped sum: smoothed value noise in the
                // same octave weights, three taps along x for a streak.
                var rnd = new Random(7);
                var lattice = new double[3][,];
                for (int o = 0; o < 3; o++) { lattice[o] = new double[64, 64]; for (int i = 0; i < 64; i++) for (int j = 0; j < 64; j++) lattice[o][i, j] = rnd.NextDouble(); }
                double Noise(int o, double x, double y)
                {
                    int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
                    double fx = x - ix, fy = y - iy; fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
                    double L(int a, int b) => lattice[o][((a % 64) + 64) % 64, ((b % 64) + 64) % 64];
                    return (L(ix, iy) * (1 - fx) + L(ix + 1, iy) * fx) * (1 - fy) + (L(ix, iy + 1) * (1 - fx) + L(ix + 1, iy + 1) * fx) * fy;
                }
                double Sum(double x, double y, double reach)
                {
                    double n = 0;
                    for (int t = -1; t <= 1; t++)
                    {
                        double w = t == 0 ? 0.5 : 0.25, qx = x + t * reach;
                        n += w * (0.55 * Noise(0, qx, y) + 0.45 * Noise(1, qx * 2, y * 2));
                    }
                    return 0.78 * n + 0.22 * Noise(2, x * 4, y * 4);
                }
                double Lit(double coverage, double reach)
                {
                    double streak = Math.Min(1.0, reach);
                    int lit = 0, count = 0;
                    for (int i = 0; i < 160; i++)
                    for (int j = 0; j < 160; j++)
                    {
                        double x = i * 0.37 + 0.11, y = j * 0.37 + 0.23;
                        var syms = GlslEval.Syms("n", Sum(x, y, reach), "VV_TERRAIN_FLECK_CONTRAST", contrast, "streak", streak,
                                                 "coverage", coverage, "VV_TERRAIN_FLECK_RAGGED", ragged, "jitter", rnd.NextDouble());
                        syms["u"] = new GlslEval.Val(GlslEval.Scalar(uExpr, syms));
                        syms["threshold"] = new GlslEval.Val(GlslEval.Scalar(thrExpr, syms));
                        if (GlslEval.Scalar(levelExpr, syms) > 0.5) lit++;
                        count++;
                    }
                    return lit / (double)count;
                }
                string detail = $"coverage {Cov(0.0):0.00}..{Cov(1.0):0.00};";
                bool ok = Cov(0.0) > 0.01 && Cov(0.0) < 0.1 && Cov(1.0) > 0.4 && Cov(1.0) < 0.65 &&
                          ragged > 0.0 && ragged <= 0.06;
                foreach (double reach in new[] { 0.0, 0.8 })
                foreach (double c in new[] { 0.05, 0.3, 0.55 })
                {
                    double got = Lit(c, reach);
                    detail += $" {c:0.00}->{got:0.00}";
                    if (Math.Abs(got - c) > 0.07) ok = false;
                }
                ok &= levelExpr.Contains("step(threshold, u)") &&
                      fieldBody.Contains("mod(floor(src * VV_TERRAIN_FLECK_TEXELS), VV_TERRAIN_FLECK_TEXEL_PERIOD)") &&
                      fieldBody.Contains("float coverage = vvTerrainFleckCoverage(thin) * breathe;") &&
                      fieldBody.Contains("float thin = clamp(0.7 * (1.0 - clamp(density, 0.0, 1.0)) + 0.3 * region, 0.0, 1.0);") &&
                      fieldBody.Contains("n = 0.78 * n + 0.22 * vvTerrainFleckNoise(") &&
                      fieldBody.Contains("n += w * (0.55 * vvTerrainFleckNoise(") &&
                      fieldBody.Contains("+ 0.45 * vvTerrainFleckNoise(") &&
                      !fieldBody.Contains("penumbra");
                check("I-terrain the sunfleck field lights the share of the floor the crown asks for, with one hard edge", ok, detail);

                // INTERACTION (v10): lit patches vary in brightness. Outside a
                // beam group most lit ground is a dim dapple; inside one most is
                // full sun; and the ramp is stepped into a few flat tones, not a
                // blur (the v5 defect). Evaluated from the shipped statements
                // over the same stand-in field.
                string coreExpr = GlslEval.Rhs(GlslEval.Statement(fieldBody, "float core"));
                string depthExpr = GlslEval.Rhs(GlslEval.Statement(fieldBody, "float depth"));
                string brightExpr = GlslEval.Rhs(GlslEval.Statement(fieldBody, "float bright"));
                var brightConsts = new[] { "VV_TERRAIN_FLECK_CORE_WIDE", "VV_TERRAIN_FLECK_CORE_NARROW", "VV_TERRAIN_FLECK_LEVELS", "VV_TERRAIN_FLECK_DIM" };
                double dim = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_DIM");
                var tones = new System.Collections.Generic.HashSet<double>();
                double DimShare(double beams, out double fullShare)
                {
                    int lit = 0, dimLit = 0, full = 0;
                    for (int i = 0; i < 120; i++)
                    for (int j = 0; j < 120; j++)
                    {
                        double x = i * 0.37 + 0.11, y = j * 0.37 + 0.23;
                        var syms = GlslEval.Syms("n", Sum(x, y, 0.0), "VV_TERRAIN_FLECK_CONTRAST", contrast, "streak", 0.0,
                                                 "coverage", 0.3, "VV_TERRAIN_FLECK_RAGGED", ragged, "jitter", 0.5, "beams", beams);
                        foreach (var c in brightConsts) syms[c] = new GlslEval.Val(GlslEval.Const(terrainSnippet, c));
                        syms["u"] = new GlslEval.Val(GlslEval.Scalar(uExpr, syms));
                        syms["threshold"] = new GlslEval.Val(GlslEval.Scalar(thrExpr, syms));
                        if (GlslEval.Scalar(levelExpr, syms) < 0.5) continue;
                        syms["core"] = new GlslEval.Val(GlslEval.Scalar(coreExpr, syms));
                        syms["depth"] = new GlslEval.Val(GlslEval.Scalar(depthExpr, syms));
                        double b = GlslEval.Scalar(brightExpr, syms);
                        tones.Add(Math.Round(b, 6));
                        lit++;
                        if (b < 0.6) dimLit++;
                        if (b > 0.999) full++;
                    }
                    fullShare = full / (double)Math.Max(lit, 1);
                    return dimLit / (double)Math.Max(lit, 1);
                }
                double fullOut, fullIn;
                double dimOut = DimShare(0.0, out fullOut), dimIn = DimShare(1.0, out fullIn);
                check("I-terrain sunflecks vary: dim dapples outside a beam group, full sun inside one, in a few flat tones",
                    dimOut >= 0.5 && fullOut > 0.02 && fullIn >= 0.5 && dimIn < dimOut &&
                    dim >= 0.2 && dim <= 0.5 && tones.Count <= (int)GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_LEVELS") + 1 &&
                    fieldBody.Contains("return vec3(level * bright, halo, coverage);"),
                    $"outside: dim {dimOut:0.00} full {fullOut:0.00}; inside: dim {dimIn:0.00} full {fullIn:0.00}; tones {tones.Count}");
            }
            catch (Exception ex) { check("I-terrain sunfleck coverage is readable", false, ex.Message); }
            // v10: the spots jittered while flying. A hard cut turns any
            // change in its inputs into edges popping, so nothing that sets the
            // cut may move with the camera: not vanilla's shadow map (fitted
            // around the camera), not the leaf map's camera-relative edge fade,
            // and not a canopy plane that follows every new mean.
            {
                double held = VintageVisuals.Common.Scene.CanopyMap.HoldPlane(100.0, 101.7);
                double moved = VintageVisuals.Common.Scene.CanopyMap.HoldPlane(100.0, 104.6);
                double first = VintageVisuals.Common.Scene.CanopyMap.HoldPlane(double.NaN, 99.4);
                double none = VintageVisuals.Common.Scene.CanopyMap.HoldPlane(100.0, double.NaN);
                check("terrain fleck cut reads only world-anchored inputs and the canopy plane holds still",
                    !Regex.IsMatch(fieldBody, @"\bbroken\b") &&
                    fleckBody.Contains("vvTerrainFleckField(src, az, l.y, overhead.z,") &&
                    overheadBody.Contains("raw = max(raw, leaves);") && overheadBody.Contains("return vec3(best, raw);") &&
                    !Regex.IsMatch(GlslEval.Rhs(GlslEval.Statement(overheadBody, "raw = max(raw")), "reach|cameraRelativePos") &&
                    held == 100.0 && moved == 105.0 && first == 99.0 && none == 100.0,
                    $"hold {held}, move {moved}, first {first}, none {none}");
            }
            // v10: a faint bloom from each fleck, scaled by how far it stands
            // out from its shade, zero without a fleck, capped under vanilla's
            // own sunlit glow, and added at both call sites through the fog.
            try
            {
                string glowExpr = GlslEval.Rhs(GlslEval.Statement(resolveBody, "vv_fleckGlow ="));
                string contrastExpr = GlslEval.Rhs(GlslEval.Statement(resolveBody, "float contrast"));
                double glowK = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_GLOW");
                double Glow(double fleck, double shadedY, double litY, double local)
                {
                    var syms = GlslEval.Syms("shaded", GlslEval.Vec(shadedY, shadedY, shadedY), "litY", litY,
                                             "fleck", GlslEval.Vec(fleck, 0, 0), "local", local, "VV_TERRAIN_FLECK_GLOW", glowK);
                    syms["contrast"] = new GlslEval.Val(GlslEval.Scalar(contrastExpr, syms));
                    return GlslEval.Scalar(glowExpr, syms);
                }
                string glowYaml = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/pbrterrainmaterial.yaml"));
                double deep = Glow(1.0, 0.2, 0.8, 1.0), faintContrast = Glow(1.0, 0.7, 0.8, 1.0), dimFleck = Glow(0.3, 0.2, 0.8, 1.0);
                check("I-terrain fleck bloom scales with the fleck and its contrast, is zero without one, and stays faint",
                    Glow(0.0, 0.2, 0.8, 1.0) == 0.0 && Glow(1.0, 0.2, 0.8, 0.0) == 0.0 && Glow(1.0, 0.8, 0.8, 1.0) < 1e-9 &&
                    deep > faintContrast * 3.0 && deep > dimFleck * 2.0 && deep <= 0.1 && glowK <= 0.1 && glowK > 0.0 &&
                    terrainSnippet.Contains("float vv_fleckGlow = 0.0;") &&
                    Regex.Matches(glowYaml, @"glow \+= vv_fleckGlow \* \(1\.0 - fogAmount\);").Count == 2,
                    $"deep shade {deep:0.000}, faint contrast {faintContrast:0.000}, dim fleck {dimFleck:0.000}");
            }
            catch (Exception ex) { check("I-terrain fleck bloom is readable", false, ex.Message); }
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
                string normalExpr = GlslEval.Rhs(GlslEval.Statement(relightBody, "float normalShade"));
                string shadowExpr = GlslEval.Rhs(GlslEval.Statement(relightBody, "float shadow"));
                string shadedExpr = GlslEval.Rhs(GlslEval.Statement(relightBody, "float shaded"));
                string relightExpr = GlslEval.Rhs(GlslEval.Statement(relightBody, "return"));
                string relightYaml = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/pbrterrainmaterial.yaml"));
                double Relight(double vis, double si, double nb)
                {
                    var syms = GlslEval.Syms("sunVisibility", vis, "shadowIntensity", si, "vv_vanillaNormalShade", nb);
                    syms["normalShade"] = new GlslEval.Val(GlslEval.Scalar(normalExpr, syms));
                    syms["shadow"] = new GlslEval.Val(GlslEval.Scalar(shadowExpr, syms));
                    syms["shaded"] = new GlslEval.Val(GlslEval.Scalar(shadedExpr, syms));
                    return GlslEval.Scalar(relightExpr, syms);
                }
                // Vanilla lights a fragment at min(shadow, nb): sunlit is nb,
                // not 1. A fleck must land on nb exactly, for a plant whose
                // per-vertex nb is well under 1 as much as for a top face
                // (v7 lifted a plant at nb 0.6 to 1.7x its sunlit level).
                bool ok = true; string detail = "";
                foreach (double nb in new[] { 1.0, 0.95, 0.6, 0.3 })
                foreach (double si in new[] { 0.3, 0.6, 1.0 })
                foreach (double vis in new[] { 0.0, 0.5, 1.0 })
                {
                    double shadowedLevel = Math.Min(1.0 - si * 0.5 * (1.0 - vis), nb);
                    double restored = shadowedLevel * Relight(vis, si, nb);
                    if (Math.Abs(restored - nb) > 1e-9) { ok = false; detail += $" nb={nb} si={si} vis={vis} -> {restored:0.000}"; }
                }
                ok &= terrainSnippet.Contains("float vv_vanillaNormalShade = 1.0;") &&
                      relightYaml.Contains("vv_vanillaNormalShade = nb;") &&
                      relightYaml.Contains("vv_vanillaNormalShade = getBrightnessFromNormal(normal, 1, intensity);");
                // The resolve may only ever mix TOWARD that level: fleck plus
                // bounce clamped to one, and the bounce capped well under it.
                ok &= resolveBody.Contains("float amount = clamp(fleck.x + fleck.y, 0.0, 1.0) * local;") && resolveBody.Contains("return mix(shaded, lit, amount);") &&
                      fleckBody.Contains("float bounce = clamp(field.y * VV_TERRAIN_FLECK_BOUNCE * gate, 0.0, VV_TERRAIN_FLECK_BOUNCE);") &&
                      GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_BOUNCE") <= 0.1;
                check("I-terrain a sunfleck restores vanilla's sunlit level exactly, normal shade included, and never exceeds it", ok, detail);
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
            // INTERACTION: wind breathes the crown and sways the field; still
            // air holds both.
            try
            {
                string breatheExpr = GlslEval.Rhs(GlslEval.Statement(fieldBody, "float breathe"));
                double Breathe(double shimmer, double phase) => GlslEval.Scalar(breatheExpr.Replace("sin(turn * 5.0 + 6.28318530718 * region)", "phase"),
                    GlslEval.Syms("shimmer", shimmer, "phase", phase));
                var sway = Regex.Match(fieldBody, @"\* \((0\.[0-9]+) \+ (0\.[0-9]+) \* shimmer\);");
                double still = sway.Success ? double.Parse(sway.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                double gust = sway.Success ? still + double.Parse(sway.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                bool ok = Math.Abs(Breathe(0.0, 1.0) - 1.0) < 1e-9 && Math.Abs(Breathe(0.0, -1.0) - 1.0) < 1e-9 &&
                          Breathe(1.0, 1.0) < Breathe(1.0, -1.0) && Breathe(1.0, 1.0) >= 0.8 &&
                          sway.Success && still > 0.0 && gust > still * 1.5 &&
                          fleckBody.Contains("vvTerrainFleckShimmer()") &&
                          GlslEval.StripComments(GlslEval.FunctionBody(terrainSnippet, "float vvTerrainFleckShimmer(") ?? "").Contains("clamp(vv_sceneWind, 0.0, 1.0)") &&
                          !Regex.IsMatch(fieldBody, @"turn \* [0-9]+\.[1-9]");
                check("I-terrain sunflecks flutter in the game's wind, on whole turns of the breeze clock", ok,
                      $"still {Breathe(0.0, 1.0):0.00}, gusting {Breathe(1.0, 1.0):0.00}..{Breathe(1.0, -1.0):0.00}; sway {still:0.00}..{gust:0.00}");
            }
            catch (Exception ex) { check("I-terrain sunfleck shimmer is readable", false, ex.Message); }
            // Sun shafts on the modular path: off at zero, only from leaf
            // texels lining a hole in the leaves at real leaf-map gaps (v7:
            // whole leaf faces filled in the dark gaps between rays), capped, held back where the scene is hard to
            // read, closed under cloud, and humid air carries them.
            try
            {
                string shaftBody = GlslEval.StripComments(GlslEval.FunctionBody(terrainSnippet, "float vvTerrainShaftLevel(") ?? "");
                string humidExpr = GlslEval.Rhs(GlslEval.Statement(shaftBody, "float humid"));
                string rimBody = GlslEval.StripComments(GlslEval.FunctionBody(terrainSnippet, "float vvTerrainLeafHoleRim(") ?? "");
                double Humid(double wet, double low, double fog) => GlslEval.Scalar(humidExpr,
                    GlslEval.Syms("vv_sceneWetness", wet, "lowSun", low, "fog", fog));
                string materialYaml = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/pbrterrainmaterial.yaml"));
                bool ok = shaftBody.Contains("if (vv_pbrShafts < 0.001) return 0.0;") &&
                          shaftBody.Contains("if (vvTerrainFloraClass() != VV_TFLORA_LEAVES) return 0.0;") &&
                          shaftBody.Contains("float crown = vvTerrainCanopyCell(world.xz).x;") &&
                          shaftBody.Contains("float clearSky = vvTerrainClearSky();") &&
                          shaftBody.Contains("float rim = vvTerrainLeafHoleRim();") &&
                          // beams land on flecks: the same canopy-plane projection the flecks use
                          shaftBody.Contains("vec2 src = world.xz + l.xz * (rise / max(l.y, 0.06));") &&
                          fleckBody.Contains("vec2 src = world.xz + l.xz * (rise / max(l.y, 0.06));") &&
                          shaftBody.Contains("float rise = vv_canopyPlaneY - world.y;") &&
                          shaftBody.Contains("vvTerrainFleckField(src, az, l.y, crown,") &&
                          shaftBody.Contains("if (lands <= 0.0) return 0.0;") &&
                          shaftBody.Contains("if (rim <= 0.0) return 0.0;") &&
                          Regex.IsMatch(shaftBody, @"float level = [^;]*\* rim \* near \* gap \* lands \*") &&
                          rimBody.Contains("textureLod(terrainTex, uv + vec2(px.x, 0.0), 0.0).a") &&
                          rimBody.Contains("return holes * 0.25;") &&
                          GlslEval.Const(terrainSnippet, "VV_TERRAIN_SHAFT_RIM_FAR") <= 40.0 &&
                          !terrainSnippet.Contains("uniform sampler2D terrainTex") &&
                          shaftBody.Contains("* vvSceneVisibilityDampen()") &&
                          shaftBody.Contains("return clamp(level, 0.0, VV_TERRAIN_SHAFT_MAX);") &&
                          GlslEval.Const(terrainSnippet, "VV_TERRAIN_SHAFT_MAX") <= 0.85 &&
                          materialYaml.Contains("godrayLevel = min(1.0, godrayLevel + vvTerrainShaftLevel(worldPos.xyz, fogDensityIn));") &&
                          Math.Abs(Humid(0, 0, 0) - 0.6) < 1e-9 && Math.Abs(Humid(1, 0, 0) - 1.0) < 1e-9 &&
                          Math.Abs(Humid(0, 1, 0) - 1.0) < 1e-9 && Math.Abs(Humid(0, 0, 1) - 1.0) < 1e-9;
                string landsExpr = GlslEval.Rhs(GlslEval.Statement(shaftBody, "float lands"));
                double dimK = GlslEval.Const(terrainSnippet, "VV_TERRAIN_FLECK_DIM");
                double Lands(double x) => GlslEval.Scalar(landsExpr, GlslEval.Syms("field", GlslEval.Vec(x, 0, 0), "VV_TERRAIN_FLECK_DIM", dimK));
                ok &= Lands(0.0) == 0.0 && Lands(dimK) == 0.0 && Lands(1.0) > 0.999 &&
                      GlslEval.Const(terrainSnippet, "VV_TERRAIN_SHAFT_GAIN") >= 1.0 &&
                      shaftBody.Contains("* VV_TERRAIN_SHAFT_GAIN *");
                check("I-terrain sun shafts start at the rims of real gaps, are capped, and humid air carries them", ok,
                      $"dry noon {Humid(0, 0, 0):0.00}, after rain {Humid(1, 0, 0):0.00}");
            }
            catch (Exception ex) { check("I-terrain sun shafts are readable", false, ex.Message); }
            // INTERACTION (v11): beams in the air. The god-ray channel only draws
            // while the sun is on screen, so midday had no rays at all (in game,
            // v10). The march must: share the flecks' projection and bright-patch
            // test (a beam is the air above a bright fleck), leave air above the
            // crown alone, stay visible from the side but brighter toward the sun,
            // reach a visible level for a typical crossing at a dry noon, stay
            // under its cap, be exactly off at slider zero, and reach the frame
            // at both call sites through the fog.
            try
            {
                string beamBody = GlslEval.StripComments(GlslEval.FunctionBody(terrainSnippet, "float vvTerrainBeamInscatter(") ?? "");
                string shaftBody2 = GlslEval.StripComments(GlslEval.FunctionBody(terrainSnippet, "float vvTerrainShaftLevel(") ?? "");
                string phaseExpr = GlslEval.Rhs(GlslEval.Statement(beamBody, "float phase"));
                string levelExpr2 = GlslEval.Rhs(GlslEval.Statement(beamBody, "float level"));
                double Phase(double c) => GlslEval.Scalar(phaseExpr, GlslEval.Syms("cosView", c));
                double scatter = GlslEval.Const(terrainSnippet, "VV_TERRAIN_BEAM_SCATTER");
                double cap = GlslEval.Const(terrainSnippet, "VV_TERRAIN_BEAM_MAX");
                double Level(double crossing, double phase, double humid, double slider) =>
                    GlslEval.Scalar(GlslEval.StubCall(levelExpr2, "vvSceneVisibilityDampen", "dampen"),
                        GlslEval.Syms("beam", crossing, "ds", 1.0, "VV_TERRAIN_BEAM_SCATTER", scatter, "phase", phase,
                                      "humid", humid, "clearSky", 1.0, "sun", 1.0, "vv_pbrShafts", slider, "dampen", 1.0));
                string landsShaft = GlslEval.Rhs(GlslEval.Statement(shaftBody2, "float lands"));
                string beamYaml = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/pbrterrainmaterial.yaml"));
                double sideNoon = Level(6.0, Phase(0.0), 0.6, 0.45);
                double towardRain = Level(6.0, Phase(1.0), 1.0, 0.45);
                bool ok = beamBody.Contains("if (vv_pbrShafts < 0.001 || vv_canopyMapValid < 0.5) return 0.0;") &&
                          beamBody.Contains("if (clearSky < 0.004) return 0.0;") &&
                          beamBody.Contains("if (rise < 0.5) continue;") &&
                          beamBody.Contains("vec2 src = p.xz + l.xz * (rise / l.y);") &&
                          beamBody.Contains("vec3 field = vvTerrainFleckField(src, az, l.y, crown, shimmer);") &&
                          beamBody.Contains("beam += " + landsShaft + " *") &&
                          beamBody.Contains("if (max(abs(srcRel.x), abs(srcRel.y)) > 28.0) continue;") &&
                          beamBody.Contains("return clamp(level, 0.0, VV_TERRAIN_BEAM_MAX);") &&
                          Phase(0.0) >= 0.4 && Phase(1.0) >= Phase(0.0) * 2.0 && Phase(-1.0) == Phase(0.0) &&
                          sideNoon >= 0.06 && sideNoon <= cap && towardRain >= sideNoon * 2.0 && cap <= 0.35 &&
                          Level(6.0, 1.0, 1.0, 0.0) == 0.0 &&
                          beamYaml.Contains("outColor.rgb += vvTerrainBeamColor() * (vvTerrainBeamInscatter(worldPos.xyz, fogDensityIn) * (1.0 - fogAmount));") &&
                          beamYaml.Contains("outColor.rgb += vvTerrainBeamColor() * (vvTerrainBeamInscatter(worldPos.xyz, 0.00125) * (1.0 - fogAmount));");
                check("I-terrain beams in the air: the column above a bright fleck, seen from the side at midday, brighter toward the sun, capped", ok,
                      $"side at a dry noon {sideNoon:0.000}, toward the sun after rain {towardRain:0.000} (cap {cap}), phase side {Phase(0.0):0.00} toward {Phase(1.0):0.00}");
            }
            catch (Exception ex) { check("I-terrain beam march is readable", false, ex.Message); }
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
