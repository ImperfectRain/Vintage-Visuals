using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Every byte of GLSL this mod ships must be 7-bit ASCII.
    ///
    /// This check exists because of one specific afternoon. Thirteen em dashes
    /// in the COMMENTS of pseudopbr.glsl made NVIDIA's GLSL frontend reject
    /// chunkopaque.fsh outright:
    ///
    ///     error C0000: syntax error, unexpected $end at token "&lt;EOF&gt;"
    ///
    /// That is the shader which draws the world, so the symptom was terrain
    /// vanishing — and it was chased through four wrong explanations, because
    /// glslangValidator compiled the identical file cleanly in all 48 settings
    /// combinations. GLSL's source character set is ASCII; a lenient validator
    /// agreeing with you proves nothing about the driver a player has.
    ///
    /// Checking the shipped assets is not enough on its own — the loader
    /// refuses non-ASCII at runtime too — but this fails in the two seconds of
    /// a smoke-test run rather than on someone else's GPU.
    /// </summary>
    public static class AsciiChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            string[] directories =
            {
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets"),
                Path.Combine(repo, "assets/vintagevisuals/shaderpatches"),
                Path.Combine(repo, "assets/vintagevisuals/shaders"),
            };

            int scanned = 0;

            foreach (string directory in directories)
            {
                if (!Directory.Exists(directory)) continue;

                foreach (string file in Directory.GetFiles(directory))
                {
                    scanned++;
                    string text = File.ReadAllText(file);
                    int offset = FirstNonAscii(text);

                    check(Path.GetFileName(file) + " is plain ASCII", offset < 0,
                        offset < 0
                            ? ""
                            : "U+" + ((int)text[offset]).ToString("X4") + " '" + text[offset] +
                              "' at offset " + offset);
                }
            }

            check("shader assets were found to scan", scanned > 0, scanned + " file(s)");
        }

        private static int FirstNonAscii(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] > '~' && text[i] != '\n' && text[i] != '\r' && text[i] != '\t') return i;
            }

            return -1;
        }

        /// <summary>
        /// Nothing may be declared in a snippet and never read.
        ///
        /// A dead uniform is not free in this mod. GLSL optimises an unread one
        /// away, HasUniform then reports it as absent, and the binder goes on
        /// writing to a name the program does not have - once a frame, forever.
        /// Two of them shipped that way (vv_sceneAutumn, vv_sceneWinter) after
        /// being declared for a shader that did not exist yet.
        ///
        /// The read corpus includes the patch YAML, because a function is very
        /// often called only from a patch's content block and is not dead at
        /// all.
        /// </summary>
        public static void RunDeadCodeCheck(string repo, Action<string, bool, string> check)
        {
            string snippetDir = Path.Combine(repo, "assets/vintagevisuals/shadersnippets");
            string patchDir = Path.Combine(repo, "assets/vintagevisuals/shaderpatches");

            var snippets = Directory.GetFiles(snippetDir, "*.glsl")
                .ToDictionary(f => Path.GetFileName(f), File.ReadAllText);

            string corpus = string.Join("\n", snippets.Values) + "\n" +
                            string.Join("\n", Directory.GetFiles(patchDir, "*.yaml").Select(File.ReadAllText));

            var deadUniforms = new List<string>();
            var deadFunctions = new List<string>();

            foreach (var pair in snippets)
            {
                foreach (Match m in Regex.Matches(pair.Value,
                             @"^uniform\s+\w+\s+(vv_\w+)\s*(?:\[[^\]]*\])?\s*;", RegexOptions.Multiline))
                {
                    if (Regex.Matches(corpus, @"\b" + m.Groups[1].Value + @"\b").Count <= 1)
                    {
                        deadUniforms.Add(m.Groups[1].Value + " (" + pair.Key + ")");
                    }
                }

                foreach (Match m in Regex.Matches(pair.Value,
                             @"^(?:float|vec[234]|bool|uint|VvSurface)\s+(vv\w+)\s*\(", RegexOptions.Multiline))
                {
                    if (Regex.Matches(corpus, @"\b" + m.Groups[1].Value + @"\s*\(").Count <= 1)
                    {
                        deadFunctions.Add(m.Groups[1].Value + " (" + pair.Key + ")");
                    }
                }
            }

            check("no shipped uniform is declared and never read", deadUniforms.Count == 0,
                string.Join(", ", deadUniforms));

            check("no shipped function is defined and never called", deadFunctions.Count == 0,
                string.Join(", ", deadFunctions));
        }
        }
    }
