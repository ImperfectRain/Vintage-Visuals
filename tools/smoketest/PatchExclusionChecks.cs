using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using VintageVisuals.Common.Patching;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Two patch groups that declare the same uniform must never both be live.
    ///
    /// THE DEFECT THIS CLOSES HAS NOT HAPPENED YET, AND THAT IS THE POINT.
    /// `pbrterrainmaterial` and `pseudopbr` both inject
    /// `uniform sampler2D vv_materialTex`. GLSL rejects that with "declared more
    /// than once", and a terrain shader that will not compile costs the world
    /// render rather than the feature - the failure mode this project treats as
    /// its worst, because a player cannot diagnose "no world".
    ///
    /// Today it cannot happen: PseudoPbrSubsystem.TerrainShaderPatchesEnabled is
    /// false while the newer terrain path is on. But that is one `const bool` in
    /// the middle of a migration, and flipping it back - which is exactly what
    /// finishing the migration looks like from the other direction - would take
    /// the world out with no warning from `dotnet build` and none from the smoke
    /// suite, because neither compiles GLSL.
    ///
    /// So the conflict is DERIVED rather than listed: every pair of groups that
    /// shares a uniform on a shared target file must be declared exclusive, and
    /// the runtime gating must actually honour that declaration. A new shared
    /// uniform introduced tomorrow is caught the same way as this one.
    /// </summary>
    public static class PatchExclusionChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            Console.WriteLine();
            Console.WriteLine("Patch groups that cannot share a shader");

            string patchDir = Path.Combine(repo, "assets/vintagevisuals/shaderpatches");
            string snippetDir = Path.Combine(repo, "assets/vintagevisuals/shadersnippets");

            // group -> (target filename -> uniforms it declares there)
            var declared = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);

            foreach (string yaml in Directory.GetFiles(patchDir, "*.yaml").OrderBy(f => f))
            {
                string group = Path.GetFileNameWithoutExtension(yaml);
                List<ShaderPatch> patches;
                try
                {
                    patches = ShaderPatchLoader.ParsePatchFile(
                        File.ReadAllText(yaml), group, yaml,
                        n => File.ReadAllText(Path.Combine(snippetDir, n))).ToList();
                }
                catch (Exception ex) { check("patch group " + group + " parses", false, ex.Message); continue; }

                var perFile = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (ShaderPatch p in patches)
                {
                    if (!perFile.TryGetValue(p.Filename, out HashSet<string> names))
                    {
                        names = new HashSet<string>(StringComparer.Ordinal);
                        perFile[p.Filename] = names;
                    }

                    foreach (Match m in Regex.Matches(p.Content ?? "",
                                 @"(?m)^\s*uniform\s+\w+\s+(\w+)\s*(?:\[[^\]]*\])?\s*;"))
                    {
                        names.Add(m.Groups[1].Value);
                    }
                }
                declared[group] = perFile;
            }

            check("the sweep found patch groups to inspect", declared.Count > 4, declared.Count + " groups");

            var undeclaredConflicts = new List<string>();
            var groups = declared.Keys.OrderBy(g => g).ToList();

            for (int i = 0; i < groups.Count; i++)
            {
                for (int j = i + 1; j < groups.Count; j++)
                {
                    string a = groups[i], b = groups[j];

                    foreach (var file in declared[a])
                    {
                        if (!declared[b].TryGetValue(file.Key, out HashSet<string> other)) continue;

                        var shared = file.Value.Intersect(other, StringComparer.Ordinal).OrderBy(x => x).ToList();
                        if (shared.Count == 0) continue;

                        if (!ShaderPatchGroups.AreExclusive(a, b))
                            undeclaredConflicts.Add(a + " and " + b + " both declare " +
                                                    string.Join(", ", shared) + " in " + file.Key);
                    }
                }
            }

            check("every pair of groups sharing a uniform is declared mutually exclusive",
                  undeclaredConflicts.Count == 0, string.Join(" | ", undeclaredConflicts.Take(4)));

            // The declaration is only worth anything if the gating obeys it.
            var flags = typeof(VintageVisuals.PseudoPBR.PseudoPbrSubsystem)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(bool) && f.Name.EndsWith("PatchesEnabled", StringComparison.Ordinal)
                            || f.FieldType == typeof(bool) && f.Name.EndsWith("PatchEnabled", StringComparison.Ordinal))
                .ToDictionary(f => f.Name, f => (bool)f.GetValue(null));

            check("the terrain patch flags are readable", flags.Count >= 2,
                  string.Join(", ", flags.Select(f => f.Key + "=" + f.Value)));

            bool oldPath = flags.TryGetValue("TerrainShaderPatchesEnabled", out bool o) && o;
            bool newPath = flags.TryGetValue("TerrainMaterialPrimaryPatchEnabled", out bool n) && n;

            check("the two terrain material paths are not both switched on",
                  !(oldPath && newPath),
                  "TerrainShaderPatchesEnabled and TerrainMaterialPrimaryPatchEnabled are both true, " +
                  "so pseudopbr and pbrterrainmaterial would both declare vv_materialTex and the " +
                  "terrain shaders would not compile");

            check("exactly one terrain material path is switched on",
                  oldPath || newPath,
                  "neither terrain path is enabled, so no surface material reaches the world shader");
        }
    }
}
