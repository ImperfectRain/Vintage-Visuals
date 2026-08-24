using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VintageVisuals.Common;
using VintageVisuals.Ui;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// The boundary between the tuning studio and the configuration it edits.
    ///
    /// A UI is the one part of a mod whose defects are invisible from the code
    /// and obvious from the screen: a slider pointed at the wrong property
    /// compiles, runs, and moves something. So the checks that matter here are
    /// the ones that tie a label to a real property, a friendly debug name to a
    /// real shader arm, and a declared range to the one the config actually
    /// clamps to.
    ///
    /// None of this says the dialog looks right. That stays a runtime question.
    /// </summary>
    public static class UiChecks
    {
        /// <summary>Matches a ConfigLib setting block by its code.</summary>
        const string CONFIGLIB_CODE_PATTERN = "\"([a-z][a-z_0-9]+)\"\\s*:\\s*\\{\\s*\"ingui\"";

        public static void Run(string repo, Action<string, bool, string> check)
        {
            Console.WriteLine();
            Console.WriteLine("Visual tuning studio: metadata against the config it edits");

            var all = VisualSettingRegistry.All;
            check("the registry has settings to show", all.Count > 40, all.Count + " settings");

            // --- every setting points at something real -----------------------
            var broken = all.Where(s => !ConfigAccess.Exists(s.Path)).Select(s => s.Code + " -> " + s.Path).ToList();
            check("every setting points at a real, writable config property",
                  broken.Count == 0, string.Join(", ", broken.Take(5)));

            var dupCodes = all.GroupBy(s => s.Code).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            check("no two settings share a code", dupCodes.Count == 0, string.Join(", ", dupCodes));

            var dupPaths = all.GroupBy(s => s.Path).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            check("no two settings edit the same property", dupPaths.Count == 0, string.Join(", ", dupPaths));

            check("every setting says what it visibly changes",
                  all.All(s => !string.IsNullOrWhiteSpace(s.Description) && s.Description.Length > 20),
                  string.Join(", ", all.Where(s => string.IsNullOrWhiteSpace(s.Description) || s.Description.Length <= 20)
                                       .Select(s => s.Code)));

            check("no label is a C# identifier in disguise",
                  all.All(s => !Regex.IsMatch(s.DisplayName, @"^[A-Z][a-z]+[A-Z]") && !s.DisplayName.Contains("_")),
                  string.Join(", ", all.Where(s => s.DisplayName.Contains("_")).Select(s => s.DisplayName)));

            // --- ranges are coherent -----------------------------------------
            var badRange = all.Where(s => s.Kind == SettingKind.Slider && (s.Min >= s.Max || s.Step <= 0f))
                              .Select(s => s.Code).ToList();
            check("every slider has a usable range", badRange.Count == 0, string.Join(", ", badRange));

            var outside = all.Where(s => s.Kind == SettingKind.Slider)
                             .Where(s => { float d = ConfigAccess.Default(s.Path); return d < s.Min - 1e-6f || d > s.Max + 1e-6f; })
                             .Select(s => s.Code + " default " + ConfigAccess.Default(s.Path) + " outside [" + s.Min + "," + s.Max + "]")
                             .ToList();
            check("every shipped default sits inside the range the UI offers",
                  outside.Count == 0, string.Join(" | ", outside));

            // --- the range the UI offers must survive the config's own clamp ---
            //
            // A slider that lets a player choose a value the config then silently
            // corrects is a slider that lies. Driving the real ClampToValidRanges
            // is the only way to know, because the limits live there and nowhere
            // else.
            var clipped = new List<string>();
            foreach (VisualSetting s in all.Where(x => x.Kind == SettingKind.Slider))
            {
                foreach (float extreme in new[] { s.Min, s.Max })
                {
                    var config = new VintageVisualsConfig();
                    if (!ConfigAccess.Set(config, s.Path, extreme)) continue;
                    config.ClampToValidRanges();

                    float after = ConfigAccess.Get(config, s.Path);
                    if (Math.Abs(after - extreme) > 1e-4f)
                        clipped.Add(s.Code + " offers " + extreme + ", config clamps to " + after);
                }
            }
            check("no slider offers a value the config would silently correct",
                  clipped.Count == 0, string.Join(" | ", clipped.Take(6)));

            // --- dropdowns ----------------------------------------------------
            foreach (VisualSetting s in all.Where(x => x.Kind == SettingKind.Dropdown))
            {
                check("the " + s.Code + " dropdown has choices",
                      s.Choices != null && s.Choices.Length > 1,
                      s.Choices == null ? "none" : s.Choices.Length.ToString());

                string current = ConfigAccess.DefaultString(s.Path);
                check("the " + s.Code + " default is one of its choices",
                      current != null && s.Choices != null && s.Choices.Contains(current, StringComparer.Ordinal),
                      current ?? "null");
            }

            // --- setting a value changes exactly that value --------------------
            //
            // The failure this catches is a copy-pasted path: two sliders that
            // both move one property, which reads on screen as "that control
            // does nothing" and is invisible in the source.
            var leaked = new List<string>();
            foreach (VisualSetting s in all.Where(x => x.Kind == SettingKind.Slider))
            {
                var config = new VintageVisualsConfig();
                var before = all.ToDictionary(x => x.Code, x => ConfigAccess.Get(config, x.Path));

                float target = Math.Abs(ConfigAccess.Get(config, s.Path) - s.Max) > 1e-4f ? s.Max : s.Min;
                ConfigAccess.Set(config, s.Path, target);

                foreach (VisualSetting other in all)
                {
                    if (other.Path == s.Path) continue;
                    if (Math.Abs(ConfigAccess.Get(config, other.Path) - before[other.Code]) > 1e-6f)
                        leaked.Add(s.Code + " also moved " + other.Code);
                }
            }
            check("editing one setting moves exactly one setting",
                  leaked.Count == 0, string.Join(" | ", leaked.Take(5)));

            // --- reset restores the shipped default ----------------------------
            var stuck = new List<string>();
            foreach (VisualSetting s in all)
            {
                var config = new VintageVisualsConfig();
                if (s.Kind == SettingKind.Dropdown) ConfigAccess.SetString(config, s.Path, "Vivid");
                else ConfigAccess.Set(config, s.Path, s.Max);

                ConfigAccess.ResetToDefault(config, s.Path);
                if (ConfigAccess.IsModified(config, s.Path)) stuck.Add(s.Code);
            }
            check("reset restores every setting to its shipped default",
                  stuck.Count == 0, string.Join(", ", stuck));

            ControllerBoundary(repo, check);
            DebugViews(repo, check);
            NativeDialog(repo, check);
            AgainstConfigLib(repo, check);
        }

        static void ControllerBoundary(string repo, Action<string, bool, string> check)
        {
            int notifications = 0;
            var config = new VintageVisualsConfig();
            var controller = new VisualTuningStudioController(() => config, () => notifications++);

            VisualSetting boolSetting = VisualSettingRegistry.All.First(s => s.Kind == SettingKind.Toggle);
            bool beforeBool = ConfigAccess.Get(config, boolSetting.Path) >= 0.5f;
            notifications = 0;
            bool boolChanged = controller.Set(boolSetting, beforeBool ? 0f : 1f);
            check("bool rows mutate bool properties",
                  boolChanged && notifications == 1 && (ConfigAccess.Get(config, boolSetting.Path) >= 0.5f) != beforeBool,
                  "changed=" + boolChanged + ", notifications=" + notifications);

            VisualSetting slider = VisualSettingRegistry.All.First(s => s.Kind == SettingKind.Slider);
            notifications = 0;
            float target = Math.Abs(ConfigAccess.Get(config, slider.Path) - slider.Max) > 1e-4f ? slider.Max : slider.Min;
            bool sliderChanged = controller.Set(slider, target);
            check("slider rows mutate intended float properties",
                  sliderChanged && notifications == 1 && Math.Abs(ConfigAccess.Get(config, slider.Path) - target) < 1e-4f,
                  "changed=" + sliderChanged + ", notifications=" + notifications);

            VisualSetting dropdown = VisualSettingRegistry.All.First(s => s.Kind == SettingKind.Dropdown);
            notifications = 0;
            string choice = dropdown.Choices.First(c => !string.Equals(c, ConfigAccess.GetString(config, dropdown.Path), StringComparison.Ordinal));
            bool dropdownChanged = controller.SetString(dropdown, choice);
            check("dropdown rows mutate intended string properties",
                  dropdownChanged && notifications == 1 && ConfigAccess.GetString(config, dropdown.Path) == choice,
                  "changed=" + dropdownChanged + ", notifications=" + notifications);

            notifications = 0;
            bool reset = controller.Reset(slider);
            check("resetting a setting restores the canonical default",
                  reset && notifications == 1 && !ConfigAccess.IsModified(config, slider.Path),
                  "reset=" + reset + ", notifications=" + notifications);

            notifications = 0;
            controller.Snapshot(VisualSettingRegistry.All.Take(10));
            check("display refresh does not mutate config",
                  notifications == 0,
                  "snapshot raised " + notifications + " notifications");

            notifications = 0;
            DebugView entityWetness = DebugViewRegistry.For(DebugViewRegistry.EntityPath).First(v => v.Value == 2);
            bool debugChanged = controller.SetDebugView(entityWetness);
            check("debug dropdown maps friendly name to correct property/value",
                  debugChanged && notifications == 1 &&
                  Math.Abs(ConfigAccess.Get(config, DebugViewRegistry.EntityPath) - 2f) < 1e-4f,
                  "changed=" + debugChanged + ", notifications=" + notifications);

            notifications = 0;
            bool normal = controller.ReturnToNormalRendering();
            check("Return to Normal restores rendering",
                  normal && notifications == 1 &&
                  DebugViewRegistry.All.Select(v => v.OwnerPath).Distinct()
                      .All(path => Math.Abs(ConfigAccess.Get(config, path)) < 1e-6f),
                  "changed=" + normal + ", notifications=" + notifications);
        }

        /// <summary>
        /// Every friendly debug name points at a shader arm that exists, and
        /// every shader arm has a name.
        ///
        /// BOTH DIRECTIONS, because they fail differently. A name with no arm
        /// behind it is a menu entry that does nothing. An arm with no name is a
        /// diagnostic that exists and cannot be reached without editing JSON,
        /// which is the state this whole tab was built to end.
        /// </summary>
        static void DebugViews(string repo, Action<string, bool, string> check)
        {
            string Snippet(string n) => File.ReadAllText(
                Path.Combine(repo, "assets/vintagevisuals/shadersnippets", n));

            void Compare(string label, string ownerPath, string function, string source)
            {
                var named = DebugViewRegistry.For(ownerPath).ToList();
                check("the " + label + " diagnostics have names", named.Count > 1, named.Count + " named");

                string body;
                try { body = GlslEval.StripComments(GlslEval.FunctionBody(source, function)); }
                catch (Exception ex) { check("the " + label + " debug function is readable", false, ex.Message); return; }

                var inShader = new HashSet<int>(
                    Regex.Matches(body, @"mode\s*==\s*(\d+)").Select(m => int.Parse(m.Groups[1].Value)));

                // 0 is "off" and is a UI concept: the shader reaches its debug
                // function only when the value is non-zero, so it has no arm.
                var namedNumbers = new HashSet<int>(named.Select(v => v.Value).Where(n => n != 0));

                var ghosts = namedNumbers.Where(n => !inShader.Contains(n)).OrderBy(n => n).ToList();
                check("no " + label + " diagnostic is offered that the shader does not implement",
                      ghosts.Count == 0, string.Join(", ", ghosts));

                var unreachable = inShader.Where(n => !namedNumbers.Contains(n)).OrderBy(n => n).ToList();
                check("every " + label + " diagnostic the shader implements can be reached by name",
                      unreachable.Count == 0, string.Join(", ", unreachable));

                var dupes = named.GroupBy(v => v.Value).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                check("no two " + label + " diagnostics share a number", dupes.Count == 0, string.Join(", ", dupes));

                var sameName = named.GroupBy(v => v.Label).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                check("no two " + label + " diagnostics share a name", sameName.Count == 0, string.Join(" | ", sameName));
            }

            Compare("material", DebugViewRegistry.PbrPath, "vec4 vvDebugView(", Snippet("pseudopbr.glsl"));
            Compare("entity", DebugViewRegistry.EntityPath, "vec4 vvEntityDebugView(", Snippet("pbrentity.glsl"));
            Compare("atmosphere", DebugViewRegistry.AtmospherePath, "vec4 vvAtmosDebug(", Snippet("atmosphere.glsl"));

            // And the numbers must be reachable from the slider that carries them.
            foreach (var group in DebugViewRegistry.All.GroupBy(v => v.OwnerPath))
            {
                float max = group.Max(v => v.Value);
                var config = new VintageVisualsConfig();
                ConfigAccess.Set(config, group.Key, max);
                config.ClampToValidRanges();

                check("the " + group.Key + " slider reaches its highest diagnostic",
                      Math.Abs(ConfigAccess.Get(config, group.Key) - max) < 1e-4f,
                      "asked for " + max + ", config clamped to " + ConfigAccess.Get(config, group.Key));
            }
        }

        static void NativeDialog(string repo, Action<string, bool, string> check)
        {
            string dialog = File.ReadAllText(Path.Combine(repo, "src/Ui/VisualTuningStudioDialog.cs"));
            string controller = File.ReadAllText(Path.Combine(repo, "src/Ui/VisualTuningStudioController.cs"));
            string mod = File.ReadAllText(Path.Combine(repo, "src/VintageVisualsModSystem.cs"));
            string ui = dialog + "\n" + controller;

            check("hotkey registration code exists and uses the expected ID/type",
                  mod.Contains("VisualTuningStudioDialog.HotkeyCode") &&
                  mod.Contains("Vintage Visuals: Open Visual Tuning") &&
                  mod.Contains("HotkeyType.GUIOrOtherControls") &&
                  mod.Contains("GlKeys.U"),
                  "open hotkey must be registered through the game input API");

            check("the native dialog uses GuiDialog and GuiComposer",
                  dialog.Contains(": GuiDialog") && dialog.Contains("CreateCompo("),
                  "dialog shell must be native Vintage Story UI");

            check("every displayed control maps to its registry entry",
                  dialog.Contains("VisibleSettings(tab, _advanced)") &&
                  dialog.Contains("AddSettingRow(setting") &&
                  dialog.Contains("setting.Kind == SettingKind.Toggle") &&
                  dialog.Contains("setting.Kind == SettingKind.Dropdown"),
                  "rows must be registry-driven");

            check("native UI mutation calls ConfigManager.NotifyChanged",
                  mod.Contains("new VisualTuningStudioController") &&
                  mod.Contains("() => ConfigManager.NotifyChanged()"),
                  "controller must converge on ConfigManager.NotifyChanged");

            check("while open, config changes refresh the native dialog",
                  mod.Contains("ConfigManager.ConfigChanged += RefreshTuningStudio") &&
                  dialog.Contains("RefreshFromConfig()") &&
                  !dialog.Contains("ConfigAccess.Set(_controller.Config"),
                  "ConfigLib changes must repaint without a write-back");

            check("no UI class references OpenGL or shader APIs",
                  !Regex.IsMatch(ui, @"\bReloadShaders\b|\bIShader\b|\bIShaderProgram\b|\bGl[A-Z]|\bOpenGL\b|\bUniform\("),
                  "UI code may not touch render or shader APIs");

            check("no UI control directly calls ReloadShaders",
                  !dialog.Contains("ReloadShaders") && !controller.Contains("ReloadShaders"),
                  "shader reloads belong to VintageVisualsModSystem gating");
        }

        /// <summary>
        /// Where this registry and ConfigLib's JSON describe the same setting,
        /// they must agree.
        ///
        /// The two lists are maintained by hand and will drift; that is not a
        /// hypothetical, it is what two hand-maintained lists do. Sharing the
        /// codes is what makes the drift detectable rather than merely likely,
        /// and this is the check that turns "documented duplication" into
        /// something better than a promise.
        /// </summary>
        static void AgainstConfigLib(string repo, Action<string, bool, string> check)
        {
            string json;
            try { json = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/config/configlib-patches.json")); }
            catch (Exception ex) { check("the ConfigLib settings file is readable", false, ex.Message); return; }

            int shared = 0;
            var disagree = new List<string>();

            foreach (VisualSetting s in VisualSettingRegistry.All)
            {
                var block = Regex.Match(json,
                    "\"" + Regex.Escape(s.Code) + "\"\\s*:\\s*\\{(?:[^{}]|\\{[^{}]*\\})*\\}");
                if (!block.Success) continue;

                shared++;

                var min = Regex.Match(block.Value, @"""min""\s*:\s*(-?[0-9.]+)");
                var max = Regex.Match(block.Value, @"""max""\s*:\s*(-?[0-9.]+)");
                var def = Regex.Match(block.Value, @"""default""\s*:\s*(-?[0-9.]+)");

                if (def.Success)
                {
                    float d = float.Parse(def.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (Math.Abs(d - ConfigAccess.Default(s.Path)) > 1e-4f)
                        disagree.Add(s.Code + " default: ConfigLib " + d + " vs config " + ConfigAccess.Default(s.Path));
                }

                if (min.Success && max.Success && s.Kind == SettingKind.Slider)
                {
                    float lo = float.Parse(min.Groups[1].Value, CultureInfo.InvariantCulture);
                    float hi = float.Parse(max.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (Math.Abs(lo - s.Min) > 1e-4f || Math.Abs(hi - s.Max) > 1e-4f)
                        disagree.Add(s.Code + " range: ConfigLib [" + lo + "," + hi + "] vs studio [" + s.Min + "," + s.Max + "]");
                }
            }

            check("the two panels describe a shared setting the same way",
                  disagree.Count == 0, string.Join(" | ", disagree.Take(8)));

            check("the comparison actually found settings in common",
                  shared > 20, shared + " codes shared with ConfigLib");

            // THE STUDIO MUST NOT BE A SUBSET OF THE OTHER PANEL.
            //
            // A shared code makes a disagreement detectable; a code only one
            // panel knows makes it invisible, because the comparison above
            // simply skips it. So every setting ConfigLib exposes has to be
            // reachable here too, and the only permitted exceptions are the
            // diagnostics - which the studio deliberately presents as named
            // dropdowns on the Debug tab rather than as numbered sliders.
            var diagnosticsHandledElsewhere = new HashSet<string>(StringComparer.Ordinal)
            {
                "pbr_debugview", "atmosphere_debugview", "weather_clouddebugview",
                "pbr_entitydebug", "compare_wipe",
            };

            var studioCodes = new HashSet<string>(
                VisualSettingRegistry.All.Select(x => x.Code), StringComparer.Ordinal);

            var onlyInConfigLib = Regex.Matches(json, CONFIGLIB_CODE_PATTERN)
                .Select(m => m.Groups[1].Value)
                .Where(c => !studioCodes.Contains(c) && !diagnosticsHandledElsewhere.Contains(c))
                .OrderBy(c => c)
                .ToList();

            check("the studio exposes everything the ConfigLib panel does",
                  onlyInConfigLib.Count == 0,
                  "reachable in ConfigLib and not in the studio: " + string.Join(", ", onlyInConfigLib));

            foreach (string code in diagnosticsHandledElsewhere)
            {
                string path = code == "pbr_debugview" ? DebugViewRegistry.PbrPath
                            : code == "atmosphere_debugview" ? DebugViewRegistry.AtmospherePath
                            : code == "weather_clouddebugview" ? DebugViewRegistry.CloudPath
                            : null;
                if (path == null) continue;

                check("the " + code + " diagnostic is reachable by name instead",
                      DebugViewRegistry.For(path).Any(),
                      "excused from the registry but has no named views either");
            }
        }
    }
}
