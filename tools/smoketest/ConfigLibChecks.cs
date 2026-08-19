using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Validates configlib-patches.json against ConfigLibBridge.
    ///
    /// This file is data the mod cannot check at compile time and does not read
    /// itself - ConfigLib parses it, builds the F7 panel from it, and raises
    /// events back by setting code. Every link in that chain is a string, and a
    /// mistake anywhere in it fails the same way: the panel is simply empty, or
    /// a slider silently does nothing.
    ///
    /// It has now failed twice. Once because the settings were written as a
    /// JSON array when ConfigLib expects a keyed object, and once because two
    /// settings shared a weight - which blanked the entire panel, every mod
    /// setting gone, from one duplicated integer. Neither showed up in a build,
    /// a test, or a log.
    /// </summary>
    public static class ConfigLibChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            Action<string, bool> ok = (name, condition) => check(name, condition, "");

            string path = Path.Combine(repo, "assets/vintagevisuals/config/configlib-patches.json");
            string bridge = File.ReadAllText(Path.Combine(repo, "src/Common/ConfigLibBridge.cs"));

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(path));
                ok("configlib-patches.json parses", true);
            }
            catch (Exception ex)
            {
                check("configlib-patches.json parses", false, ex.Message);
                return;
            }

            using (document)
            {
                JsonElement settings = document.RootElement.GetProperty("settings");

                // Keyed objects, not arrays. ConfigLib iterates JProperties, so
                // an array parses as valid JSON and yields zero settings.
                ok("settings are keyed objects, not arrays",
                    settings.EnumerateObject().All(c => c.Value.ValueKind == JsonValueKind.Object));

                var codes = new List<string>();
                var weights = new Dictionary<int, string>();
                bool duplicateWeight = false;
                string duplicateDetail = "";
                bool rangesSane = true;
                string rangeDetail = "";
                bool allClientSide = true;

                foreach (JsonProperty category in settings.EnumerateObject())
                {
                    foreach (JsonProperty setting in category.Value.EnumerateObject())
                    {
                        codes.Add(setting.Name);

                        JsonElement value = setting.Value;

                        if (value.TryGetProperty("clientSide", out JsonElement clientSide) &&
                            clientSide.ValueKind != JsonValueKind.True)
                        {
                            allClientSide = false;
                        }

                        if (value.TryGetProperty("weight", out JsonElement weight))
                        {
                            int w = weight.GetInt32();
                            if (weights.ContainsKey(w))
                            {
                                duplicateWeight = true;
                                duplicateDetail = "weight " + w + " on both " + weights[w] + " and " + setting.Name;
                            }
                            else
                            {
                                weights[w] = setting.Name;
                            }
                        }

                        if (!value.TryGetProperty("range", out JsonElement range)) continue;

                        double min = range.GetProperty("min").GetDouble();
                        double max = range.GetProperty("max").GetDouble();
                        double step = range.GetProperty("step").GetDouble();
                        double def = value.GetProperty("default").GetDouble();

                        if (min >= max || step <= 0 || def < min || def > max)
                        {
                            rangesSane = false;
                            rangeDetail = setting.Name + " min=" + min + " max=" + max +
                                          " step=" + step + " default=" + def;
                        }
                    }
                }

                // THE check. Two settings sharing a weight emptied the whole F7
                // panel - not one setting, all of them.
                check("every setting weight is unique", !duplicateWeight, duplicateDetail);

                ok("every setting has a distinct code", codes.Distinct().Count() == codes.Count);
                ok("every setting is clientSide", allClientSide);
                check("every range is sane and contains its default", rangesSane, rangeDetail);

                // Both directions. A setting with no case is a slider that does
                // nothing; a case with no setting is a control nobody can reach.
                var missingCase = codes.Where(c => !bridge.Contains("case \"" + c + "\":")).ToList();
                check("every setting has a case in ConfigLibBridge", missingCase.Count == 0,
                    string.Join(", ", missingCase));

                var declared = System.Text.RegularExpressions.Regex
                    .Matches(bridge, "case \"([a-z_]+)\":")
                    .Select(m => m.Groups[1].Value)
                    .ToList();

                var orphanCase = declared.Where(c => !codes.Contains(c)).ToList();
                check("every ConfigLibBridge case has a setting", orphanCase.Count == 0,
                    string.Join(", ", orphanCase));

                ok("all four subsystems' settings are present",
                    codes.Any(c => c.StartsWith("colorgrade_")) &&
                    codes.Any(c => c.StartsWith("adaptive_")) &&
                    codes.Any(c => c.StartsWith("pbr_")));
            }
        }
    }
}
