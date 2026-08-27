using System;
using System.IO;
using System.Linq;
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
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
void main()
{
    float glow = 0;
    float glowLevel = 0;
    float godrayLevel = 0;
    float fogAmount = 0;
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
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
void main()
{
    float glow = 0;
    float glowLevel = 0;
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

            check("pbrterrainmaterial.yaml parsed into six patches",
                patches.Count == 6,
                patches.Count.ToString());
            check("pbrterrainmaterial group is isolated",
                patches.All(p => p.Group == "pbrterrainmaterial"),
                string.Join(", ", patches.Select(p => p.Group).Distinct()));
            check("pbrterrainmaterial uses only safe explicit operations",
                patches.All(p => p.Kind == ShaderPatchKind.Assert ||
                                 p.Kind == ShaderPatchKind.InsertAfter ||
                                 p.Kind == ShaderPatchKind.InsertBefore),
                string.Join(", ", patches.Select(p => p.Kind).Distinct()));
            check("pbrterrainmaterial declares deterministic compiler phases",
                patches.Count(p => p.Phase == "Assertions") == 2 &&
                patches.Count(p => p.Phase == "Declarations") == 2 &&
                patches.Count(p => p.Phase == "Material") == 2,
                string.Join(", ", patches.Select(p => p.Phase)));

            string resolved = string.Join("\n", patches.Select(p => p.Content));
            check("Stage B material terrain declares only the primary VV sampler",
                resolved.CountSubstring("uniform sampler2D vv_materialTex;") == 2 &&
                !resolved.Contains("vv_materialTex2") &&
                !resolved.Contains("vv_reflectScene") &&
                !resolved.Contains("vv_reflectWorld") &&
                !resolved.Contains("vv_canopy"),
                "Stage B is limited to one VV sampler");

            var logger = new CollectingLogger();
            var patcher = new ShaderPatcher(logger);
            patcher.SetPatches(patches);

            string opaque = patcher.Patch("chunkopaque.fsh", ChunkOpaqueVanilla);
            string topsoil = patcher.Patch("chunktopsoil.fsh", TopsoilVanilla);

            check("pbrterrainmaterial group healthy", patcher.IsGroupHealthy("pbrterrainmaterial"), "");
            check("chunkopaque receives primary material operation before outGlow",
                opaque.IndexOf("outColor = vvTerrainMaterialPrimary(outColor, uv);", StringComparison.Ordinal) >= 0 &&
                opaque.IndexOf("outColor = vvTerrainMaterialPrimaryDebug(outColor, uv);", StringComparison.Ordinal) >
                opaque.IndexOf("outColor = vvTerrainMaterialPrimary(outColor, uv);", StringComparison.Ordinal) &&
                opaque.IndexOf("outGlow = vec4(glowLevel + glow, godrayLevel, 0, min(1, fogAmount + outColor.a));", StringComparison.Ordinal) >
                opaque.IndexOf("outColor = vvTerrainMaterialPrimaryDebug(outColor, uv);", StringComparison.Ordinal),
                "");
            check("chunktopsoil receives primary material operation before outGlow",
                topsoil.IndexOf("outColor = vvTerrainMaterialPrimary(outColor, uv);", StringComparison.Ordinal) >= 0 &&
                topsoil.IndexOf("outColor = vvTerrainMaterialPrimaryDebug(outColor, uv);", StringComparison.Ordinal) >
                topsoil.IndexOf("outColor = vvTerrainMaterialPrimary(outColor, uv);", StringComparison.Ordinal) &&
                topsoil.IndexOf("outGlow = vec4(glowLevel + glow, 0, 0, outColor.a);", StringComparison.Ordinal) >
                topsoil.IndexOf("outColor = vvTerrainMaterialPrimaryDebug(outColor, uv);", StringComparison.Ordinal),
                "");
            check("Stage B preserves vanilla glow contracts",
                opaque.Contains("outGlow = vec4(glowLevel + glow, godrayLevel, 0, min(1, fogAmount + outColor.a));") &&
                topsoil.Contains("outGlow = vec4(glowLevel + glow, 0, 0, outColor.a);"),
                "");
            check("Stage B does not restore reflection or canopy resources",
                !opaque.Contains("vv_reflect") && !topsoil.Contains("vv_reflect") &&
                !opaque.Contains("vv_canopy") && !topsoil.Contains("vv_canopy") &&
                !opaque.Contains("vv_materialTex2") && !topsoil.Contains("vv_materialTex2"),
                "");

            ShaderPatchManifestEntry opaqueManifest = patcher.Manifest["chunkopaque.fsh"];
            ShaderPatchManifestEntry topsoilManifest = patcher.Manifest["chunktopsoil.fsh"];

            check("Stage B manifest exposes exactly one added sampler per terrain shader",
                opaqueManifest.SamplersAdded.SequenceEqual(new[] { "sampler2D vv_materialTex" }) &&
                topsoilManifest.SamplersAdded.SequenceEqual(new[] { "sampler2D vv_materialTex" }) &&
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
