using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VintageVisuals.Common.Patching;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// D100 split chunk programs. The twins draw parts of the frame the game's
    /// program used to draw, so each check here pins one way a twin could
    /// disagree with the game's program and show it on screen:
    /// depth that differs by an ulp (speckle through every leaf), a define
    /// that leaks into the vertex stage or into the game's program, a swap
    /// that is not undone (the rest of the frame drawn with the twin), alpha
    /// before the prepass's return that depends on a uniform the depth twin
    /// never receives, and the flora twin dropping something on plants.
    /// </summary>
    public static class SplitProgramChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            string snippets = Path.Combine(repo, "assets/vintagevisuals/shadersnippets");
            Func<string, string> resolve = name => File.ReadAllText(Path.Combine(snippets, name));

            // 1. Exact depth across programs needs gl_Position invariant in the
            //    shared vertex source, and nothing else from this group.
            string yaml = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shaderpatches/terrainsplit.yaml"));
            var patches = ShaderPatchLoader.ParsePatchFile(yaml, "terrainsplit", "test", resolve).ToList();
            string vsh = "#version 330 core\nuniform vec3 origin;\nvoid main() { gl_Position = vec4(origin, 1.0); }\n";
            var patcher = new ShaderPatcher(new CollectingLogger());
            patcher.SetPatches(patches);
            string patchedVsh = patcher.Patch("chunkopaque.vsh", vsh);
            check("I-split: the twins share depth to the bit because gl_Position is invariant in the shared vertex source",
                patcher.IsGroupHealthy("terrainsplit") &&
                Regex.IsMatch(patchedVsh, @"(?m)^\s*invariant\s+gl_Position\s*;") &&
                patches.All(p => p.Filename == "chunkopaque.vsh") &&
                !patches.Any(p => p.Content != null && Regex.IsMatch(p.Content, @"\b(uniform|out|in)\s+\w+\s+\w+")),
                "");

            string cs = File.ReadAllText(Path.Combine(repo, "src/Common/Rendering/ChunkProgramSplit.cs"));
            check("I-split: a twin is only built from a program that carries the invariant declaration",
                cs.Contains("public const string VertexMarker = \"invariant gl_Position\";") &&
                cs.Contains("vert.IndexOf(VertexMarker, StringComparison.Ordinal) < 0) return null;"),
                "");

            // 2. The define goes on the fragment stage only. In the vertex stage
            //    it could change the compiled position math between programs.
            check("I-split: the twin's define is prepended to the FRAGMENT prefix only, the vertex stage is the game's own",
                cs.Contains("p.VertexShader.Code = vert;") &&
                cs.Contains("p.VertexShader.PrefixCode = main.VertexShader.PrefixCode;") &&
                cs.Contains("p.FragmentShader.PrefixCode = \"#define \" + define + \" 1\\n\" + (main.FragmentShader.PrefixCode ?? \"\");") &&
                Regex.Matches(cs, @"VertexShader\.PrefixCode\s*=").Count == 1,
                "");

            // 2b. The game raises a chunk vertex shader to #version 430 for its
            //     SSBO face data only when Shader.Filename starts with "chunk".
            //     A twin without the game's file names compiled at 330 and failed
            //     on every machine with allowSSBOs on (the first install).
            check("I-split: each twin shader carries the game's file name, so the game gives it the same #version (SSBO 430)",
                cs.Contains("GetField(\"Filename\", f)") &&
                cs.Contains("_fShaderFilename.SetValue(p.VertexShader, _fShaderFilename.GetValue(main.VertexShader));") &&
                cs.Contains("_fShaderFilename.SetValue(p.FragmentShader, _fShaderFilename.GetValue(main.FragmentShader));") &&
                cs.IndexOf("_fShaderFilename.SetValue(p.VertexShader", StringComparison.Ordinal) <
                    cs.IndexOf("if (!p.Compile())", StringComparison.Ordinal),
                "");

            // 3. The swap is undone: all three fields, and the leaf program unbound.
            int swapOut = cs.IndexOf("private static void SwapOut()", StringComparison.Ordinal);
            string swapOutBody = swapOut >= 0 ? cs.Substring(swapOut, Math.Min(600, cs.Length - swapOut)) : "";
            string prepass = File.ReadAllText(Path.Combine(repo, "src/Common/Rendering/DepthPrepass.cs"));
            check("I-split: every swap is undone - handle, uniform table and texture table - and the game's program rebound",
                swapOutBody.Contains("_fProgramId.SetValue(owner, _savedId);") &&
                swapOutBody.Contains("_fUniforms.SetValue(owner, _savedUniforms);") &&
                swapOutBody.Contains("_fTextures.SetValue(owner, _savedTextures);") &&
                cs.Contains("if (main != null) _gl.UseProgram((int)_fProgramId.GetValue(main));") &&
                prepass.Contains("finally { if (twin) ChunkProgramSplit.ExitTwin(); }") &&
                prepass.Contains("finalizer: new HarmonyMethod(AccessTools.Method(typeof(DepthPrepass), nameof(PoolRenderFinalizer)))") &&
                prepass.Contains("ChunkProgramSplit.AfterPool();"),
                "");

            check("I-split: no twin draws inside a mirror pass or inside the prepass's own pool calls",
                prepass.Contains("if (!_busy && !VintageVisuals.Reflections.Mirrors.MirrorRenderer.MirrorPassActive)") &&
                cs.Contains("if (main == null || !ReferenceEquals(_capi.Render.CurrentActiveShader, main)) return;") &&
                cs.Contains("if (pass != PassOpaqueNoCull || _swapped || !Wanted()) return;"),
                "");

            // 4. The depth twin receives no uniform copy, so everything that can
            //    reach alpha before the prepass's return must be vanilla's or keep
            //    vanilla's alpha whatever the mod's uniforms are. The one mod
            //    function there is the atmosphere's applyFog: every return of
            //    vvAtmosphere that is not a debug view must pass alpha through.
            string atmos = resolve("atmosphere.glsl");
            int fn = atmos.IndexOf("vec4 vvAtmosphere(", StringComparison.Ordinal);
            int open = fn >= 0 ? atmos.IndexOf('{', fn) : -1;
            string body = open >= 0 ? Block(atmos, open) : "";
            var returns = Regex.Matches(body, @"return\s+([^;]+);").Cast<Match>().Select(m => m.Groups[1].Value.Trim()).ToList();
            bool allKeepAlpha = returns.Count > 0 && returns.All(r =>
                r.StartsWith("vvAtmosDebug(", StringComparison.Ordinal) ||
                Regex.IsMatch(r, @",\s*rgbaPixel\.a\s*\)$"));
            check("I-split: alpha before the prepass's return does not depend on any uniform the depth twin lacks",
                allKeepAlpha, string.Join(" | ", returns));

            // 5. The flora twin: plants take the same early return in both
            //    programs, and the features it drops are dropped AFTER the group
            //    defines land and only under VV_FLORA_ONLY.
            string material = resolve("pbrterrainmaterial.glsl");
            int sceneHook = material.IndexOf("// vintagevisuals-hook: terrain scene capture define", StringComparison.Ordinal);
            int mirrorHook = material.IndexOf("// vintagevisuals-hook: terrain mirrors define", StringComparison.Ordinal);
            int floraBlock = material.IndexOf("#ifdef VV_FLORA_ONLY", StringComparison.Ordinal);
            string block = floraBlock >= 0 ? material.Substring(floraBlock, material.IndexOf("#endif", floraBlock, StringComparison.Ordinal) - floraBlock) : "";
            int firstUse = material.IndexOf("#ifdef VV_TERRAIN_SCENE_CAPTURE", floraBlock + 1, StringComparison.Ordinal);
            check("I-split: the flora twin drops only the scene march and the mirrors, after both group defines land",
                sceneHook >= 0 && mirrorHook > sceneHook && floraBlock > mirrorHook && firstUse > floraBlock &&
                Regex.Matches(block, @"#undef\s+\w+").Count == 2 &&
                block.Contains("#undef VV_TERRAIN_SCENE_CAPTURE") && block.Contains("#undef VV_TERRAIN_MIRRORS"),
                "hooks " + sceneHook + "/" + mirrorHook + " block " + floraBlock + " first use " + firstUse);

            int primary = material.IndexOf("vec4 vvTerrainMaterialPrimary(", StringComparison.Ordinal);
            int floraReturn = primary >= 0 ? material.IndexOf("return vvTerrainFloraShade(", primary, StringComparison.Ordinal) : -1;
            int firstDropped = primary >= 0 ? material.IndexOf("#ifdef VV_TERRAIN_MIRRORS", primary, StringComparison.Ordinal) : -1;
            int floraShade = material.IndexOf("vec4 vvTerrainFloraShade(", StringComparison.Ordinal);
            string floraBody = floraShade >= 0 ? Block(material, material.IndexOf('{', floraShade)) : "";
            check("I-split: plants leave the material before anything the flora twin drops, so they shade identically",
                floraReturn > primary && firstDropped > floraReturn && floraBody.Length > 0 &&
                !floraBody.Contains("VV_TERRAIN_SCENE_CAPTURE") && !floraBody.Contains("VV_TERRAIN_MIRRORS") &&
                !floraBody.Contains("vvTerrainPixelReflection") && !floraBody.Contains("vvTerrainMirror"),
                "");

            // 6. The setting gates the group, so off is the source as before.
            string mod = File.ReadAllText(Path.Combine(repo, "src/VintageVisualsModSystem.cs"));
            check("terrainsplit is gated on Performance.SplitPrograms and in the gating signature",
                Regex.IsMatch(mod, @"group == VintageVisuals\.Common\.Rendering\.ChunkProgramSplit\.GroupName\)\s*\{\s*return ConfigManager\.Config\.Performance\.SplitPrograms;") &&
                mod.Contains("(ConfigManager.Config.Performance.SplitPrograms ? \"Y\" : \"-\")"),
                "");
        }

        /// <summary>The text of a brace block starting at <paramref name="open"/>.</summary>
        private static string Block(string s, int open)
        {
            if (open < 0) return "";
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}' && --depth == 0) return s.Substring(open, i - open + 1);
            }
            return "";
        }
    }
}
