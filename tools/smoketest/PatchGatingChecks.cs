using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Every shipped patch group must have an off switch, and every off switch
    /// must name a group that exists.
    ///
    /// This matters more than it looks. A config flag for a shader patch has to
    /// gate the PATCH rather than the effect: muting a uniform leaves the
    /// patched GLSL compiling and occupying a sampler, so when the damage comes
    /// from the source existing at all, the player's off switch does nothing.
    /// IsPatchGroupEnabled is where that happens, and its default is `true` -
    /// so a group whose name is misspelled there is not gated and nothing says
    /// so. The group name and the YAML filename are the same string in two
    /// places with nothing joining them.
    ///
    /// The reverse direction matters too: a case naming a group that no longer
    /// ships is a flag the player can toggle that controls nothing.
    /// </summary>
    public static class PatchGatingChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            string patchDir = Path.Combine(repo, "assets/vintagevisuals/shaderpatches");
            string modSystem = File.ReadAllText(Path.Combine(repo, "src/VintageVisualsModSystem.cs"));
            string pbrSubsystem = File.ReadAllText(Path.Combine(repo, "src/PseudoPBR/PseudoPbrSubsystem.cs"));

            // Group defaults to the file's own name, so the shipped groups are
            // the shipped filenames unless a file overrides it.
            var groups = Directory.GetFiles(patchDir, "*.yaml")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            check("patch groups were found to check", groups.Count > 0, groups.Count.ToString());

            int gateStart = modSystem.IndexOf("private bool IsPatchGroupEnabled(", StringComparison.Ordinal);
            check("IsPatchGroupEnabled was found at all", gateStart >= 0, "");
            if (gateStart < 0) return;

            string gate = modSystem.Substring(gateStart,
                modSystem.IndexOf("\n        }", gateStart, StringComparison.Ordinal) - gateStart);

            // The gate compares against constants, so resolve them first -
            // QUALIFIED by their class. Three subsystems each call theirs
            // "GroupName", so a dictionary keyed on the bare constant name has
            // two of the three overwriting the third, and this check reported
            // colorgrade and weather as ungated when both are gated fine.
            var constants = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string file in Directory.GetFiles(Path.Combine(repo, "src"), "*.cs",
                                                       SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);

                foreach (Match m in Regex.Matches(text, @"const string (\w*GroupName)\s*=\s*""(\w+)"""))
                {
                    // The class a constant belongs to is the last class
                    // declared above it, which for these one-class files is
                    // simply the file's class.
                    Match owner = Regex.Matches(text.Substring(0, m.Index),
                            @"(?:class|struct)\s+(\w+)")
                        .Cast<Match>().LastOrDefault();

                    if (owner == null) continue;

                    constants[owner.Groups[1].Value + "." + m.Groups[1].Value] = m.Groups[2].Value;
                }
            }

            // Which group names the gate can actually return something other
            // than its default `true` for.
            var gated = Regex.Matches(gate, @"(\w+)\.(\w*GroupName)\b")
                .Cast<Match>()
                .Select(m => constants.TryGetValue(m.Groups[1].Value + "." + m.Groups[2].Value, out string g)
                    ? g
                    : null)
                .Where(g => g != null)
                .Distinct()
                .ToList();

            check("the gate's group constants all resolved", gated.Count > 0,
                gated.Count + " of " + Regex.Matches(gate, @"\w+\.\w*GroupName\b").Count + " reference(s)");

            var ungated = groups.Where(g => !gated.Contains(g)).ToList();
            check("every shipped patch group has an off switch", ungated.Count == 0,
                string.Join(", ", ungated) + (ungated.Count == 0 ? "" : " - falls through to `return true`"));

            var orphaned = gated.Where(g => !groups.Contains(g)).ToList();
            check("every gated group is actually shipped", orphaned.Count == 0,
                string.Join(", ", orphaned));

            check("terrain PBR source patches are fail-closed during recovery",
                pbrSubsystem.Contains("public const bool TerrainShaderPatchesEnabled = false;") &&
                gate.Contains("PseudoPbrSubsystem.TerrainShaderPatchesEnabled") &&
                gate.Contains("ConfigManager.Config.PseudoPBR.Enabled") &&
                gate.Contains("PseudoPbrSubsystem.TopsoilGroupName"),
                "chunkopaque and chunktopsoil must receive vanilla source until the terrain corruption is isolated");

            // A flag the gate reads but the signature does not is a toggle that
            // takes effect only after a manual shader reload, which reads to
            // the player as "the setting does nothing until I restart".
            int signatureStart = modSystem.IndexOf("private string PatchGatingSignature(", StringComparison.Ordinal);
            check("PatchGatingSignature was found at all", signatureStart >= 0, "");
            if (signatureStart < 0) return;

            string signature = modSystem.Substring(signatureStart,
                modSystem.IndexOf("\n        }", signatureStart, StringComparison.Ordinal) - signatureStart);

            var read = Regex.Matches(gate, @"ConfigManager\.Config\.(\w+\.\w+)")
                .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();

            var unwatched = read.Where(f => !signature.Contains("Config." + f)).ToList();
            check("every gating flag is in the gating signature", unwatched.Count == 0,
                string.Join(", ", unwatched) + (unwatched.Count == 0 ? "" : " - toggling it would not reload shaders"));
        }
    }
}
