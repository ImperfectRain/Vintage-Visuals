using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VintageVisuals.Common.Patching;

// Drives the REAL compiled VintageVisuals patch engine against the REAL
// colorgrade.yaml. Everything before this was a Python port of the algorithm;
// this exercises the shipped IL.

namespace VintageVisuals.SmokeTest
{
static class Program
{
    // Walk up from the built exe (tools/smoketest/bin/<cfg>/<tfm>/) to the
    // repo root, so the harness runs from any checkout on any machine.
    static readonly string Repo = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    static int failures = 0;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + (ok ? "" : "   " + detail));
        if (!ok) failures++;
    }

    // Stand-in shaped like vanilla final.fsh.
    const string Vanilla = @"#version 330 core
#extension GL_ARB_explicit_attrib_location : enable

uniform sampler2D primaryScene;
uniform sampler2D bloomParts;
uniform vec2 invFrameSize;

in vec2 texCoord;

layout(location = 0) out vec4 outColor;

vec4 fxaaTexturePixel(sampler2D tex, vec2 uv, vec2 inv) { return texture(tex, uv); }

void main(void) {
    vec4 color = fxaaTexturePixel(primaryScene, texCoord, invFrameSize);
#if BLOOM == 1
    color.rgb += texture(bloomParts, texCoord).rgb;
#endif
    outColor = color;
}
";

    static string ResolveSnippet(string name)
        => File.ReadAllText(Path.Combine(Repo, "assets/vintagevisuals/shadersnippets", name));

    static List<ShaderPatch> LoadRealPatches()
        => ShaderPatchLoader.ParsePatchFile(
               File.ReadAllText(Path.Combine(Repo, "assets/vintagevisuals/shaderpatches/colorgrade.yaml")),
               "colorgrade", "test", ResolveSnippet).ToList();

    static int Main()
    {
        Console.WriteLine("YAML deserialization (private nested class, public properties)");
        List<ShaderPatch> patches;
        try
        {
            patches = LoadRealPatches();
            Check("colorgrade.yaml parsed", true);
        }
        catch (Exception ex)
        {
            Check("colorgrade.yaml parsed", false, ex.ToString());
            return 1;
        }

        Check("4 patches produced", patches.Count == 4, patches.Count.ToString());
        Check("all in group 'colorgrade'", patches.All(p => p.Group == "colorgrade"));
        Check("all target final.fsh", patches.All(p => p.AppliesTo("final.fsh")));
        Check("kinds are start/token/token/end",
              string.Join(",", patches.Select(p => p.Kind.ToString())) == "Start,Token,Token,End",
              string.Join(",", patches.Select(p => p.Kind.ToString())));
        Check("snippet resolved into content",
              patches[0].Content.Contains("vvApplyColorGrade"),
              patches[0].Content.Length + " chars");
        Check("bool 'optional' defaulted to false", patches.All(p => !p.Optional));

        Console.WriteLine("ShaderPatcher applies the group");
        var logger = new CollectingLogger();
        var patcher = new ShaderPatcher(logger);
        patcher.SetPatches(patches);

        string result = patcher.Patch("final.fsh", Vanilla);
        Check("group healthy", patcher.IsGroupHealthy("colorgrade"));
        Check("uniforms injected", result.Contains("uniform float vv_enabled;"));
        Check("grading function injected", result.Contains("vec4 vvApplyColorGrade(vec4 color)"));
        Check("vanilla main renamed", result.Contains("void vvSceneMain(void) {"));
        Check("new main appended", result.Contains("vvSceneMain();"));
        Check("outColor graded", result.Contains("outColor = vvApplyColorGrade(outColor);"));
        Check("output-name assertion applied", result.Contains("output name asserted"));

        int mainCount = Regex.Matches(result, @"(?<![A-Za-z0-9_])void\s+main\s*\(\s*void\s*\)").Count;
        Check("exactly one main()", mainCount == 1, mainCount.ToString());
        Check("#version still first line", result.TrimStart().StartsWith("#version"));
        Check("braces balanced", result.Count(c => c == '{') == result.Count(c => c == '}'));

        int versionLine = result.Split('\n').ToList().FindIndex(l => l.StartsWith("#version"));
        int uniformLine = result.Split('\n').ToList().FindIndex(l => l.Contains("vv_enabled"));
        Check("injection after #version", uniformLine > versionLine);

        Console.WriteLine("The patched source is the vanilla source plus the patch, and nothing else");
        // The mod once appended a marker line of its own to every patched file.
        // It bought nothing that a single hook does not already give, and it put
        // this mod's text into shaders it had no reason to touch, so a shader
        // now leaves the patcher carrying its patches and no bookkeeping.
        Check("no mod bookkeeping is left in the shader", !result.Contains("// VintageVisuals:"),
              "the patcher is writing a marker into shipped GLSL");

        Console.WriteLine("The census says whether a target ever reached the patcher");
        var censusLog = new CollectingLogger();
        var censusPatcher = new ShaderPatcher(censusLog);
        censusPatcher.SetPatches(LoadRealPatches());

        censusPatcher.Patch("gui.fsh", Vanilla);
        censusPatcher.LogCensus();
        Check("a target the hook never delivered is named",
              censusLog.Lines.Any(l => l.Contains("NEVER reached the patcher") && l.Contains("final.fsh")),
              string.Join(" | ", censusLog.Lines));
        Check("the names that DID arrive are listed, so a wrong filename is visible",
              censusLog.Lines.Any(l => l.Contains("did deliver") && l.Contains("gui.fsh")),
              string.Join(" | ", censusLog.Lines));

        Console.WriteLine("The delivery table keeps the six states apart");
        censusLog.Lines.Clear();
        censusPatcher.LogDelivery();
        Check("a target that never reached the hook says so",
              censusLog.Lines.Any(l => l.Contains("final.fsh") && l.Contains("NEVER reached the hook")),
              string.Join(" | ", censusLog.Lines));

        censusPatcher.Patch("final.fsh", Vanilla);
        censusPatcher.RecordDelivery("final.fsh", "ShaderProgram", "FragmentShader",
                                     Vanilla.Length, Vanilla.Length + 100, true);
        censusLog.Lines.Clear();
        censusPatcher.LogDelivery();

        string finalRow = censusLog.Lines.FirstOrDefault(l => l.Contains("final.fsh")) ?? "";
        Check("a delivered target reports what happened to it",
              finalRow.Contains("applicable=4") && finalRow.Contains("applied=4")
              && finalRow.Contains("writtenBack=yes"),
              finalRow);
        Check("delivery separates 'a patch matched' from 'the source was written back'",
              finalRow.Contains("applicable=") && finalRow.Contains("writtenBack="), finalRow);

        censusLog.Lines.Clear();
        censusPatcher.LogCensus();
        Check("the census stays silent once every target has arrived",
              censusLog.Lines.Count == 0, string.Join(" | ", censusLog.Lines));

        Console.WriteLine("Non-target shaders are untouched");
        Check("chunkopaque.fsh unchanged", patcher.Patch("chunkopaque.fsh", Vanilla) == Vanilla);

        Console.WriteLine("Rollback when an anchor goes stale");
        var logger2 = new CollectingLogger();
        var patcher2 = new ShaderPatcher(logger2);
        patcher2.SetPatches(LoadRealPatches());

        string renamedOutput = Vanilla.Replace("out vec4 outColor;", "out vec4 fragColour;");
        string rolled = patcher2.Patch("final.fsh", renamedOutput);

        Check("returns vanilla untouched", rolled == renamedOutput);
        Check("no half-applied GLSL", !rolled.Contains("vvApplyColorGrade") && !rolled.Contains("vvSceneMain"));
        Check("group marked unhealthy", !patcher2.IsGroupHealthy("colorgrade"));
        Check("logged CRITICAL", logger2.Lines.Any(l => l.Contains("CRITICAL")),
              string.Join(" | ", logger2.Lines));
        Check("group skipped on later shaders",
              patcher2.Patch("final.fsh", Vanilla) == Vanilla);

        Console.WriteLine("Patcher never throws into the game's loader");
        try
        {
            patcher.Patch("final.fsh", null);
            patcher.Patch("final.fsh", "");
            patcher.Patch(null, Vanilla);
            Check("null/empty inputs survive", true);
        }
        catch (Exception ex) { Check("null/empty inputs survive", false, ex.Message); }

        Console.WriteLine("Malformed YAML fails loudly, not silently");
        foreach (var (label, yaml) in new[]
        {
            ("unknown type", "- type: bogus\n  filename: final.fsh\n  content: x"),
            ("missing filename", "- type: end\n  content: x"),
            ("token without tokens", "- type: token\n  filename: final.fsh\n  content: x"),
            ("content and snippet together", "- type: start\n  filename: final.fsh\n  content: x\n  snippet: colorgrade.glsl"),
        })
        {
            try
            {
                ShaderPatchLoader.ParsePatchFile(yaml, "g", "t", ResolveSnippet).ToList();
                Check(label + " rejected", false, "no exception");
            }
            catch (ArgumentException) { Check(label + " rejected", true); }
        }

        var empty = ShaderPatchLoader.ParsePatchFile("# only a comment\n", "g", "t", ResolveSnippet).ToList();
        Check("comment-only file yields no patches", empty.Count == 0);

        Console.WriteLine("Explicit patch operations preserve anchors by construction");
        string operationYaml = @"
- type: assert
  filename: final.fsh
  tokens: layout(location = 0) out vec4 outColor;
- type: insert_before
  filename: final.fsh
  tokens: vec4 color = fxaaTexturePixel(primaryScene, texCoord, invFrameSize);
  content: |
    // before color sample
- type: insert_after
  filename: final.fsh
  tokens: vec4 color = fxaaTexturePixel(primaryScene, texCoord, invFrameSize);
  content: |
    // after color sample
- type: wrap
  filename: final.fsh
  tokens: void main(void) {
  content: |
    ${MATCH}
    // wrapped main marker
- type: replace
  filename: final.fsh
  tokens: color.rgb += texture(bloomParts, texCoord).rgb;
  content: |
    color.rgb += vec3(1.0);
";
        var operationPatcher = new ShaderPatcher(new CollectingLogger());
        var operationPatches = ShaderPatchLoader.ParsePatchFile(operationYaml, "ops", "ops", ResolveSnippet).ToList();
        operationPatcher.SetPatches(operationPatches);
        string operations = operationPatcher.Patch("final.fsh", Vanilla);
        Check("assert operation does not delete its anchor",
              operations.Contains("layout(location = 0) out vec4 outColor;"));
        Check("insert_before preserves the matched source",
              operations.Contains("// before color sample") &&
              operations.Contains("vec4 color = fxaaTexturePixel(primaryScene, texCoord, invFrameSize);"));
        Check("insert_after preserves the matched source",
              operations.Contains("// after color sample") &&
              operations.IndexOf("// after color sample", StringComparison.Ordinal) >
              operations.IndexOf("vec4 color = fxaaTexturePixel(primaryScene, texCoord, invFrameSize);", StringComparison.Ordinal));
        Check("wrap operation expands ${MATCH}",
              operations.Contains("void main(void) {") &&
              operations.Contains("// wrapped main marker"));
        Check("replace operation is the only explicit deletion here",
              operations.Contains("color.rgb += vec3(1.0);") &&
              !operations.Contains("color.rgb += texture(bloomParts, texCoord).rgb;"));
        Check("patch manifest records operations and before/after facts",
              operationPatcher.Manifest.ContainsKey("final.fsh") &&
              operationPatcher.Manifest["final.fsh"].Operations.Count == 5 &&
              operationPatcher.Manifest["final.fsh"].DeclarationsBefore.Contains("layout(location = 0) out vec4 outColor;") &&
              operationPatcher.Manifest["final.fsh"].DeclarationsAfter.Contains("layout(location = 0) out vec4 outColor;"),
              operationPatcher.Manifest.ContainsKey("final.fsh")
                  ? operationPatcher.Manifest["final.fsh"].ToStableText()
                  : "no manifest");
        Check("generated patch text uses canonical LF newlines",
              !operations.Contains("\r"),
              "CRLF found in generated shader text");

        try
        {
            var badWrap = ShaderPatchLoader.ParsePatchFile(
                "- type: wrap\n  filename: final.fsh\n  tokens: void main(void) {\n  content: no placeholder",
                "ops", "ops", ResolveSnippet).ToList();
            var badPatcher = new ShaderPatcher(new CollectingLogger());
            badPatcher.SetPatches(badWrap);
            string bad = badPatcher.Patch("final.fsh", Vanilla);
            Check("wrap without ${MATCH} fails closed", bad == Vanilla && !badPatcher.IsGroupHealthy("ops"));
        }
        catch (Exception ex)
        {
            Check("wrap without ${MATCH} fails closed", false, ex.Message);
        }

        Console.WriteLine("Patch ordering is phase/dependency driven and legacy use is visible");
        string orderedYaml = @"
- type: insert_before
  group: late
  after: early
  phase: FinalOutput
  filename: final.fsh
  tokens: outColor = color;
  content: |
    color.rgb += vec3(0.25);
- type: insert_before
  group: early
  phase: Declarations
  filename: final.fsh
  tokens: uniform vec2 invFrameSize;
  content: |
    uniform float vv_orderProbe;
";
        var orderedPatcher = new ShaderPatcher(new CollectingLogger());
        var orderedPatches = ShaderPatchLoader.ParsePatchFile(orderedYaml, "order", "order", ResolveSnippet).ToList();
        orderedPatcher.SetPatches(orderedPatches);
        string ordered = orderedPatcher.Patch("final.fsh", Vanilla);
        Check("dependency ordering applies declarations before final output",
              ordered.IndexOf("uniform float vv_orderProbe;", StringComparison.Ordinal) <
              ordered.IndexOf("color.rgb += vec3(0.25);", StringComparison.Ordinal),
              ordered);

        var legacyLog = new CollectingLogger();
        var legacyPatcher = new ShaderPatcher(legacyLog);
        var legacyPatches = ShaderPatchLoader.ParsePatchFile(
            "- type: token\n  filename: final.fsh\n  tokens: outColor = color;\n  content: outColor = color;",
            "newlegacy", "newlegacy", ResolveSnippet).ToList();
        legacyPatcher.SetPatches(legacyPatches);
        Check("unallowlisted legacy token patches warn",
              legacyLog.Lines.Any(l => l.Contains("legacy destructive Token")),
              string.Join(" | ", legacyLog.Lines));

        legacyLog.Lines.Clear();
        legacyPatcher.SetPatches(ShaderPatchLoader.ParsePatchFile(
            "- type: token\n  filename: final.fsh\n  allowLegacy: true\n  tokens: outColor = color;\n  content: outColor = color;",
            "newlegacy", "newlegacy", ResolveSnippet).ToList());
        Check("allowlisted legacy token patches stay quiet",
              !legacyLog.Lines.Any(l => l.Contains("legacy destructive")),
              string.Join(" | ", legacyLog.Lines));

        Console.WriteLine("Whitespace tolerance on real anchors");
        var reflowed = Vanilla
            .Replace("void main(void) {", "void  main( void )\n{")
            .Replace("layout(location = 0) out vec4 outColor;", "layout( location=0 ) out   vec4   outColor ;");
        var patcher3 = new ShaderPatcher(new CollectingLogger());
        patcher3.SetPatches(LoadRealPatches());
        patcher3.Patch("final.fsh", reflowed);
        Check("reflowed vanilla still matches", patcher3.IsGroupHealthy("colorgrade"));

        Console.WriteLine("Block material classification");
        MaterialProfileChecks.Run(Check);

        Console.WriteLine("Material atlas: packing, assembly, cache");
        MaterialAtlasChecks.Run(Check);

        Console.WriteLine("Scene intent, budget and arbitration");
        SceneIntentChecks.Run(Check);

        Console.WriteLine("Adaptive grading responds to the world");
        GradeStackChecks.Repo = Repo;
        GradeStackChecks.Run(Check);

        Console.WriteLine("Eye adaptation model");
        AdaptiveExposureChecks.Run(Check);

        Console.WriteLine("PBR port agrees with the validated Python prototype");
        PbrParityChecks.Run(Repo, Check);

        Console.WriteLine("PseudoPBR shader patch applies and rolls back cleanly");
        PseudoPbrPatchChecks.Run(Repo, Check);

        Console.WriteLine("Terrain PBR zero-sampler baseline patch is isolated");
        TerrainBasePatchChecks.Run(Repo, Check);

        Console.WriteLine("Shipped GLSL is plain ASCII");
        AsciiChecks.Run(Repo, Check);
        AsciiChecks.RunDeadCodeCheck(Repo, Check);

        Console.WriteLine("ConfigLib settings file agrees with the bridge");
        ConfigLibChecks.Run(Repo, Check);

        Console.WriteLine("Every patch group can be switched off");
        PatchGatingChecks.Run(Repo, Check);

        Console.WriteLine("World-anchored fields are sampled every frame");
        WorldAnchorChecks.Run(Repo, Check);

        Console.WriteLine("Effects respect the light they are allowed to change");
        ConservationChecks.Run(Repo, Check);

        Console.WriteLine("Material response invariants");
        MaterialResponseChecks.Run(Repo, Check);

        Console.WriteLine("The second material atlas");
        SecondAtlasChecks.Run(Repo, Check);

        Console.WriteLine("What a surface is, versus what its block is made of");
        MaterialResolverChecks.Run(Repo, Check);

        Console.WriteLine("Every shader uniform is actually uploaded");
        UniformWiringChecks.Run(Repo, Check);

        Console.WriteLine();
        Console.WriteLine("Canopy audit: vanilla's own sun occlusion");
        CanopyAuditChecks.Run(Repo, Check);

        Console.WriteLine();
        Console.WriteLine("Pixelated environment reflection");
        PixelReflectionChecks.Run(Repo, Check);

        Console.WriteLine();
        Console.WriteLine("Documentation still describes the code");
        ShaderBindingChecks.Run(Repo, Check);

        FloraTaxonomyChecks.Run(Repo, Check);

        AtmosphereChecks.Run(Repo, Check);

        SceneInvariantChecks.Run(Repo, Check);

        UiChecks.Run(Repo, Check);

        DocumentationChecks.Run(Repo, Check);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}
}
