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

        Console.WriteLine("A group is applied once, however many times the source comes back");
        // Hooking every LoadShader overload means one program can reach the
        // postfix through more than one route. Applying a group twice is not a
        // doubled effect - it is a duplicate function definition and a shader
        // that will not compile, so it costs the world render rather than the
        // feature.
        string twice = patcher.Patch("final.fsh", result);
        Check("re-patching already-patched source changes nothing", twice == result,
              (twice.Length - result.Length) + " chars added");

        int gradeDefs = Regex.Matches(twice, @"vec4\s+vvApplyColorGrade\s*\(vec4").Count;
        Check("exactly one vvApplyColorGrade definition after two passes", gradeDefs == 1, gradeDefs.ToString());

        int mainsTwice = Regex.Matches(twice, @"(?<![A-Za-z0-9_])void\s+main\s*\(\s*void\s*\)").Count;
        Check("still exactly one main() after two passes", mainsTwice == 1, mainsTwice.ToString());

        Check("the sentinel names its group", result.Contains("// VintageVisuals:colorgrade"));

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

        censusPatcher.Patch("final.fsh", Vanilla);
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

        DocumentationChecks.Run(Repo, Check);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}
}
