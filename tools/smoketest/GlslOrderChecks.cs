using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// No shipped GLSL calls a function it has not declared yet.
    ///
    /// THIS SHIPPED, AND IT TOOK THE WORLD RENDER WITH IT. `vvCanopyShaft`
    /// called `vvLeafBacklightSource` a thousand lines above the definition.
    /// GLSL has no forward references, so chunkopaque.fsh and chunktopsoil.fsh
    /// failed to compile in EVERY define combination - 48 of 48 and 24 of 24 -
    /// with "no matching overloaded function found", which is what a compiler
    /// says when it has not seen the name yet.
    ///
    /// Nothing noticed. `dotnet build` compiles C# and never looks at a shader.
    /// The smoke suite was green at 1116 checks. Only tools/verifypatches
    /// compiles GLSL, it needs glslang installed and the game's own dumped
    /// shaders present, and it takes minutes - so it is the one tool most likely
    /// not to have been run before a push.
    ///
    /// This check needs none of that. Function order is a property of the text,
    /// so it can be read here in milliseconds, on every run, by everyone.
    ///
    /// SAME FILE ONLY. A snippet may legitimately call a function another
    /// snippet defines - pseudopbr.glsl uses pbrcore.glsl throughout - because
    /// injection order decides that, and it is not knowable from one file. What
    /// IS knowable, and what broke, is a file calling forward into itself.
    /// </summary>
    public static class GlslOrderChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            Console.WriteLine();
            Console.WriteLine("Shipped GLSL declares before it calls");

            string dir = Path.Combine(repo, "assets/vintagevisuals/shadersnippets");
            int examined = 0;
            var offenders = new List<string>();

            foreach (string path in Directory.GetFiles(dir, "*.glsl").OrderBy(f => f))
            {
                string source = GlslEval.StripComments(File.ReadAllText(path));
                string[] lines = source.Split('\n');

                // A definition opens a body; a prototype ends the signature with
                // a semicolon. Both satisfy "declared before use", which is the
                // whole point of allowing a prototype at all.
                var definedAt = new Dictionary<string, int>(StringComparer.Ordinal);
                var declaredAt = new Dictionary<string, int>(StringComparer.Ordinal);

                for (int i = 0; i < lines.Length; i++)
                {
                    Match m = Regex.Match(lines[i], @"^\s*(?:[A-Za-z_][A-Za-z_0-9]*)\s+(vv[A-Za-z_0-9]+)\s*\(");
                    if (!m.Success) continue;

                    string name = m.Groups[1].Value;

                    // Walk to the end of the signature: a ';' means prototype,
                    // a '{' means definition.
                    int depth = 0;
                    bool prototype = false, definition = false;
                    for (int j = i; j < lines.Length && j < i + 12; j++)
                    {
                        foreach (char c in lines[j])
                        {
                            if (c == '(') depth++;
                            else if (c == ')') depth--;
                            else if (depth == 0 && c == ';') { prototype = true; break; }
                            else if (depth == 0 && c == '{') { definition = true; break; }
                        }
                        if (prototype || definition) break;
                    }

                    if (prototype && !declaredAt.ContainsKey(name)) declaredAt[name] = i;
                    if (definition && !definedAt.ContainsKey(name)) definedAt[name] = i;
                }

                if (definedAt.Count == 0) continue;
                examined++;

                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (Match call in Regex.Matches(lines[i], @"(?<![A-Za-z_0-9])(vv[A-Za-z_0-9]+)\s*\("))
                    {
                        string name = call.Groups[1].Value;

                        // Only functions this file defines; anything else comes
                        // from another snippet and is injection order's problem.
                        if (!definedAt.TryGetValue(name, out int definition)) continue;

                        if (i >= definition) continue;                       // defined above, fine
                        if (definedAt.ContainsKey(name) && definition == i) continue;
                        if (declaredAt.TryGetValue(name, out int proto) && proto < i) continue;

                        // The definition's own signature line is not a call.
                        if (Regex.IsMatch(lines[i], @"^\s*(?:[A-Za-z_][A-Za-z_0-9]*)\s+" + Regex.Escape(name) + @"\s*\("))
                            continue;

                        offenders.Add(Path.GetFileName(path) + ": " + name + " called on line " +
                                      (i + 1) + " but defined on line " + (definition + 1) +
                                      " with no prototype above the call");
                    }
                }
            }

            check("the sweep read the shipped snippets", examined > 3, examined + " files with functions");

            check("no snippet calls a function it declares later in the same file",
                  offenders.Count == 0, string.Join(" | ", offenders.Distinct().Take(4)));
        }
    }
}
