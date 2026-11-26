using System;
using System.IO;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// D90 shader specialisation. The terrain debug views are compiled in only
    /// by the pbrterraindebug group, and that group is applied only while a
    /// view is selected. Each half fails silently: a define placed below the
    /// debug function, or an inverted #if, compiles cleanly and leaves either
    /// dead views or the dead code back in every fragment; a gate missing from
    /// the patch-gating signature means moving the slider never reloads.
    /// </summary>
    public static class ShaderSpecialisationChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            string snippet = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pbrterrainmaterial.glsl")).Replace("\r\n", "\n");
            string yaml = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/pbrterraindebug.yaml"));
            string mod = File.ReadAllText(Path.Combine(repo, "src/VintageVisualsModSystem.cs"));
            string spec = File.ReadAllText(Path.Combine(repo, "src/Common/Rendering/ShaderSpecialisation.cs"));

            int anchor = snippet.IndexOf("uniform float vv_pbrEnabled;\nuniform float vv_pbrDebugView;", StringComparison.Ordinal);
            int debugFn = snippet.IndexOf("vec4 vvTerrainMaterialPrimaryDebug(", StringComparison.Ordinal);
            int body = debugFn < 0 ? -1 : snippet.IndexOf("{", debugFn, StringComparison.Ordinal);
            string head = body < 0 ? "" : snippet.Substring(body, Math.Min(260, snippet.Length - body));

            check("D90: without the debug group the debug views are not compiled, and the define lands above them",
                anchor >= 0 && debugFn > anchor &&
                yaml.Contains("tokens: \"uniform float vv_pbrEnabled; uniform float vv_pbrDebugView;\"") &&
                yaml.Contains("#define VV_TERRAIN_DEBUG_VIEWS 1") &&
                head.Contains("#ifndef VV_TERRAIN_DEBUG_VIEWS") &&
                head.IndexOf("return color;", StringComparison.Ordinal) < head.IndexOf("#else", StringComparison.Ordinal) &&
                head.IndexOf("#else", StringComparison.Ordinal) < head.IndexOf("int mode", StringComparison.Ordinal),
                "anchor@" + anchor + " debug@" + debugFn);

            check("D90: the debug views compile in only while one is selected, and a change reloads the shaders",
                spec.Contains("return (int)(debugView + 0.5f) != 0;") &&
                mod.Contains("if (group == PseudoPbrSubsystem.TerrainDebugGroupName)") &&
                mod.Contains("(VintageVisuals.Common.Rendering.ShaderSpecialisation.DebugCodeWanted(ConfigManager.Config.PseudoPBR.DebugView) ? \"V\" : \"-\")"),
                "");

            // D91. The terrain's beam march compiled out while the fast pass draws.
            string fastYaml = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/pbrterrainfastbeams.yaml"));
            string beamSource = File.ReadAllText(Path.Combine(repo, "src/PseudoPBR/BeamPassSource.cs"));
            string beamPass = File.ReadAllText(Path.Combine(repo, "src/PseudoPBR/BeamPassRenderer.cs"));
            int march = snippet.IndexOf("float vvTerrainBeamInscatter(vec3 cameraRelativePos, float fogDensity)\n{\n", StringComparison.Ordinal);
            string marchHead = march < 0 ? "" : snippet.Substring(march, Math.Min(220, snippet.Length - march));

            check("D91: the define compiles out only the terrain's beam march, never the beam pass's",
                march > anchor && anchor >= 0 &&
                marchHead.Contains("{\n#ifdef VV_TERRAIN_BEAMS_IN_PASS\n") &&
                fastYaml.Contains("tokens: \"uniform float vv_pbrEnabled; uniform float vv_pbrDebugView;\"") &&
                fastYaml.Contains("#define VV_TERRAIN_BEAMS_IN_PASS 1") &&
                !beamSource.Contains("VV_TERRAIN_BEAMS_IN_PASS"),
                "march@" + march);

            check("D91: the march stays in source unless the fast pass is wanted and healthy, and a pass failure rebuilds the terrain",
                spec.Contains("return !BeamBenchHoldsMarch && !BeamCodeBenchHoldsMarch && pbrEnabled && sunShafts >= 0.001f && fastBeams && !passFailed &&") &&
                spec.Contains("!DebugCodeWanted(debugView);") &&
                mod.Contains("(TerrainBeamsInPass() ? \"Q\" : \"-\")") &&
                mod.Contains("if (group == PseudoPbrSubsystem.TerrainFastBeamsGroupName)") &&
                beamPass.Contains("try { Failed?.Invoke(); } catch { }"),
                "");

            check("vanilla bench: every patch group off, or all but the depth prepass, and the choice reloads",
                mod.Contains("return vanilla == 2 && group == VintageVisuals.Common.Rendering.DepthPrepass.GroupName;") &&
                mod.IndexOf("int vanilla = VintageVisuals.Common.Rendering.ShaderSpecialisation.VanillaBench;", StringComparison.Ordinal) <
                    mod.IndexOf("if (group == PseudoPbrSubsystem.TerrainBaseGroupName)", StringComparison.Ordinal) &&
                mod.Contains("VintageVisuals.Common.Rendering.ShaderSpecialisation.VanillaBench;\n        }"),
                "");
        }
    }
}
