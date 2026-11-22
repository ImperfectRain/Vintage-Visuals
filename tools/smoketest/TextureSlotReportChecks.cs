using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using VintageVisuals.Common.Patching;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// The texture slot report must be able to read the game's own OpenTK.
    ///
    /// The report is all reflection, and its first version looked for a
    /// GetActiveUniform overload taking a StringBuilder. The OpenTK the game
    /// ships has no such overload, so the report printed a program header and
    /// then nothing, which reads exactly like "no samplers" - the one answer it
    /// exists to rule out. This resolves every query the report makes against
    /// the real OpenTK.Graphics.dll from the game install, so a binding change
    /// fails here instead of going quiet in a player's log.
    ///
    /// It needs the game install (VINTAGE_STORY), which the build already
    /// requires, and it inspects types only: no GL context is created.
    /// </summary>
    public static class TextureSlotReportChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? "";
            string dll = Path.Combine(game, "Lib", "OpenTK.Graphics.dll");
            check("the game's OpenTK.Graphics.dll is available to check the texture slot report against",
                  File.Exists(dll), dll);
            if (!File.Exists(dll)) return;

            Assembly asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dll));
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            // The binding Vintage Story itself calls, and the one the report prefers.
            Type gl = types.FirstOrDefault(t => t.FullName == "OpenTK.Graphics.OpenGL.GL");
            check("the game's OpenTK exposes OpenTK.Graphics.OpenGL.GL", gl != null, "");
            if (gl == null) return;

            var missing = RuntimeShaderIntrospector.MissingGlQueries(gl);
            check("the texture slot report can read the sampler map on the game's OpenTK",
                  missing.Count == 0, string.Join(", ", missing));
        }
    }
}
