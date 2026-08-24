using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageVisuals.Ui
{
    /// <summary>
    /// Native Vintage Story dialog for live visual tuning.
    ///
    /// This class owns layout only. The mutation path stays:
    /// widget -> VisualTuningStudioController -> ConfigAccess -> ConfigManager.
    /// </summary>
    public sealed class VisualTuningStudioDialog : GuiDialog
    {
        public const string HotkeyCode = "vintagevisuals_visualtuning";

        private const double DialogWidth = 860;
        private const double DialogHeight = 610;
        private const double DialogPadding = 10;
        private const double SidebarWidth = 196;
        private const double SidebarPadding = 18;
        private const double ContentGap = 24;
        private const double ContentLeft = DialogPadding + SidebarWidth + ContentGap;
        private const double ContentRightPadding = 30;
        private const double ContentWidth = DialogWidth - ContentLeft - ContentRightPadding - 10;
        private const double HeaderY = 44;
        private const double BodyY = 88;
        private const double FooterY = 548;
        private const double BodyHeight = FooterY - BodyY - 18;
        private const double RowLeft = 0;
        private const double RowHeight = 56;
        private const double HeaderHeight = 26;
        private const double LabelWidth = 210;
        private const double InfoWidth = 28;
        private const double ValueWidth = 86;
        private const double ControlWidth = 154;
        private const double ResetWidth = 54;
        private const int RecomposeDelayMs = 50;
        private const double InfoX = RowLeft + LabelWidth + 8;
        private const double ValueX = InfoX + InfoWidth + 10;
        private const double ControlX = ValueX + ValueWidth + 14;
        private const double ResetX = ControlX + ControlWidth + 14;
        private const string ScrollbarKey = "settings-scrollbar";

        private readonly VisualTuningStudioController _controller;
        private readonly Dictionary<SettingTab, double> _scrollByTab = new Dictionary<SettingTab, double>();

        private readonly GuiTab[] _tabs =
        {
            new GuiTab { DataInt = (int)SettingTab.Overview, Name = "Overview" },
            new GuiTab { DataInt = (int)SettingTab.Color, Name = "Color" },
            new GuiTab { DataInt = (int)SettingTab.Materials, Name = "Materials" },
            new GuiTab { DataInt = (int)SettingTab.Weather, Name = "Weather" },
            new GuiTab { DataInt = (int)SettingTab.Atmosphere, Name = "Atmosphere" },
            new GuiTab { DataInt = (int)SettingTab.Reflections, Name = "Reflections" },
            new GuiTab { DataInt = (int)SettingTab.Debug, Name = "Debug" },
        };

        private readonly DebugSystem[] _debugSystems =
        {
            new DebugSystem("Materials", DebugViewRegistry.PbrPath,
                v => v.Group != "Pixel reflection" && v.Group != "Scene reflection bridge" && v.Group != "Reflection march"),
            new DebugSystem("Reflections", DebugViewRegistry.PbrPath,
                v => v.Value == 0 || v.Group == "Pixel reflection" || v.Group == "Scene reflection bridge" || v.Group == "Reflection march"),
            new DebugSystem("Entities", DebugViewRegistry.EntityPath, v => true),
            new DebugSystem("Weather clouds", DebugViewRegistry.CloudPath, v => true),
            new DebugSystem("Atmosphere", DebugViewRegistry.AtmospherePath, v => true),
        };

        private SettingTab _tab = SettingTab.Overview;
        private bool _advanced;
        private string _debugOwnerPath = DebugViewRegistry.PbrPath;
        private int _debugSystemIndex = 0;
        private double _currentScroll;
        private double _currentContentHeight;
        private double _currentBodyHeight = BodyHeight;
        private int _eventSequence;
        private int _composerGeneration;
        private bool _isComposing;
        private bool _recomposePending;
        private bool _ignoreNextConfigRefresh;

        public VisualTuningStudioDialog(ICoreClientAPI capi, VisualTuningStudioController controller)
            : base(capi)
        {
            _controller = controller;
            _advanced = false;
            ComposeDialog();
        }

        public override string ToggleKeyCombinationCode { get { return HotkeyCode; } }
        public override EnumDialogType DialogType { get { return EnumDialogType.Dialog; } }

        public override void OnGuiOpened()
        {
            Log("ENTER OnGuiOpened");
            base.OnGuiOpened();
            Log("EXIT OnGuiOpened");
        }

        public override void OnGuiClosed()
        {
            Log("ENTER OnGuiClosed");
            base.OnGuiClosed();
            Log("EXIT OnGuiClosed");
        }

        public void RefreshFromConfig()
        {
            Log("ENTER RefreshFromConfig");
            if (!IsOpened()) return;

            if (_ignoreNextConfigRefresh)
            {
                _ignoreNextConfigRefresh = false;
                Log("EXIT RefreshFromConfig skipped internal mutation");
                return;
            }

            ScheduleCompose("external config refresh");
            Log("EXIT RefreshFromConfig scheduled");
        }

        public override void OnMouseWheel(MouseWheelEventArgs args)
        {
            base.OnMouseWheel(args);

            if (_tab == SettingTab.Overview) return;

            double delta = args.deltaPrecise != 0 ? args.deltaPrecise : args.delta;
            if (Math.Abs(delta) <= 0.001) return;

            ScrollTo(CurrentScroll() - delta * 34);
            args.SetHandled();
        }

        public override void Dispose()
        {
            Log("ENTER Dispose");
            base.Dispose();
            Log("EXIT Dispose");
        }

        private void ComposeDialog()
        {
            if (_isComposing)
            {
                _recomposePending = true;
                Log("ComposeDialog nested request deferred");
                return;
            }

            _isComposing = true;
            try
            {
                _composerGeneration++;
                int generation = _composerGeneration;
                Log("ENTER ComposeDialog generation " + generation);

                ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                    .WithAlignment(EnumDialogArea.CenterMiddle)
                    .WithFixedSize(DialogWidth, DialogHeight);

                ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
                bgBounds.BothSizing = ElementSizing.FitToChildren;

                _currentScroll = CurrentScroll();

                SingleComposer = capi.Gui.CreateCompo("vintagevisuals-tuning-studio", dialogBounds)
                    .AddShadedDialogBG(bgBounds)
                    .AddDialogTitleBar("Vintage Visuals", OnTitleBarClose)
                    .BeginChildElements(bgBounds)
                        .AddStaticText(PageTitle(), CairoFont.WhiteMediumText(), ElementBounds.Fixed(ContentLeft, HeaderY, 380, 26))
                        .AddInset(ElementBounds.Fixed(DialogPadding, 42, SidebarWidth, 506), 2)
                        .AddInset(ElementBounds.Fixed(ContentLeft - 10, BodyY - 8, ContentWidth + 20, BodyHeight + 16), 2)
                        .AddStaticText("Advanced Settings: Off", CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentLeft, 552, 190, 24))
                        .AddStaticText("Crash isolation build", CairoFont.WhiteDetailText(), ElementBounds.Fixed(ContentLeft + 198, 556, 180, 20))
                        .AddButton("Reset Tab", () => Guard(generation, "ResetTab", OnResetTabClicked), ElementBounds.Fixed(DialogWidth - 136, 548, 96, 28),
                            CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "reset-tab");

                AddTabs(generation);

                if (_tab == SettingTab.Overview) AddOverview();
                else if (_tab == SettingTab.Debug) AddDebug();
                else AddSettingsTab(_tab);

                SingleComposer.EndChildElements().Compose();
                ConfigureScrollbar();
                Log("EXIT ComposeDialog generation " + generation);
            }
            finally
            {
                _isComposing = false;
            }

            if (_recomposePending)
            {
                _recomposePending = false;
                ScheduleCompose("pending after compose");
            }
        }

        private void AddTabs(int generation)
        {
            foreach (GuiTab tab in _tabs) tab.Active = tab.DataInt == (int)_tab;

            double tabX = DialogPadding + SidebarPadding;
            double tabWidth = SidebarWidth - 2 * SidebarPadding;
            double y = 58;
            foreach (GuiTab tab in _tabs)
            {
                bool active = tab.DataInt == (int)_tab;
                CairoFont font = active
                    ? CairoFont.WhiteSmallText().WithColor(GuiStyle.ActiveButtonTextColor)
                    : CairoFont.WhiteSmallText();

                SingleComposer.AddButton(tab.Name, () => Guard(generation, "SelectTab", () =>
                {
                    SelectTab((SettingTab)tab.DataInt);
                    return true;
                }), ElementBounds.Fixed(tabX, y, tabWidth, 28), font, EnumButtonStyle.Normal, "tab-" + tab.DataInt);

                y += 38;
            }
        }

        private string PageTitle()
        {
            if (_tab == SettingTab.Materials) return "PBR & Materials";
            return _tabs.First(t => t.DataInt == (int)_tab).Name;
        }

        private void SelectTab(SettingTab tab)
        {
            Log("ENTER SelectTab " + tab);
            if (_tab == tab)
            {
                Log("EXIT SelectTab unchanged " + tab);
                return;
            }

            SaveScroll();
            _tab = tab;
            ScheduleCompose("tab change");
            Log("EXIT SelectTab " + tab);
        }

        private bool OnResetTabClicked()
        {
            Log("ENTER ResetTab");
            _ignoreNextConfigRefresh = true;
            _controller.ResetTab(_tab, _advanced);
            ScheduleCompose("reset tab");
            Log("EXIT ResetTab");
            return true;
        }

        private bool OnCloseClicked()
        {
            Log("ENTER Close");
            TryClose();
            Log("EXIT Close");
            return true;
        }

        private void OnTitleBarClose()
        {
            Log("ENTER TitleBarClose");
            TryClose();
            Log("EXIT TitleBarClose");
        }

        private void AddOverview()
        {
            double y = BodyY + 6;
            SingleComposer.AddStaticText("Systems", CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentLeft, y, 180, 22));
            y += 34;

            AddOverviewRow("COLOR & EXPOSURE", "ColorGrade.Enabled", SettingTab.Color,
                "Exposure " + ValueByCode("colorgrade_exposure") + " · Eye adaptation " + OnOff("AdaptiveExposure.Enabled"),
                "Open exposure, tonemap, colour style and adaptive grading controls.", y);
            y += 78;

            AddOverviewRow("MATERIALS", "PseudoPBR.Enabled", SettingTab.Materials,
                "Surface Relief " + ValueByCode("pbr_normalstrength") + " · Reflections " + OnOff("Reflections.SceneReflections"),
                "Open relief, roughness, specular response, foliage light and emissive controls.", y);
            y += 78;

            AddOverviewRow("WEATHER", "Weather.Enabled", SettingTab.Weather,
                "Wetness " + ValueByCode("weather_wetness") + " · Cloud Shadows " + ValueByCode("weather_cloudshadow"),
                "Open wet surfaces, puddle ripples, rain response and cloud shadow controls.", y);
            y += 78;

            AddOverviewRow("ATMOSPHERE", "Atmosphere.Enabled", SettingTab.Atmosphere,
                "Scattering " + ValueByCode("atmosphere_scattering") + " · Ground Haze " + ValueByCode("atmosphere_groundhaze"),
                "Open air scattering, extinction, aerial perspective and haze controls.", y);
            y += 78;

            AddOverviewRow("REFLECTIONS", "Reflections.SceneReflections", SettingTab.Reflections,
                "Scene reflections " + OnOff("Reflections.SceneReflections") + " · Pixel strength " + ValueByCode("pbr_pixelreflect"),
                "Open screen-space reflections, edge fading, stride and debug controls.", y);
        }

        private void AddOverviewRow(string label, string path, SettingTab target, string summary, string description, double y)
        {
            bool on = ConfigAccess.Get(_controller.Config, path) >= 0.5f;
            int generation = _composerGeneration;

            SingleComposer
                .AddInset(ElementBounds.Fixed(ContentLeft, y, ContentWidth - 34, 68), 2, 0.42f)
                .AddStaticText(label, CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentLeft + 14, y + 8, 230, 20))
                .AddStaticText(on ? "● On" : "○ Off", CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentLeft + 256, y + 8, 80, 20))
                .AddStaticText(summary, CairoFont.WhiteDetailText(), ElementBounds.Fixed(ContentLeft + 14, y + 31, 300, 18))
                .AddStaticText(description, CairoFont.WhiteDetailText(), ElementBounds.Fixed(ContentLeft + 14, y + 49, 430, 18))
                .AddButton("Open  >", () =>
                {
                    if (!IsCurrentGeneration(generation, "OverviewRow")) return false;
                    SelectTab(target);
                    return true;
                }, ElementBounds.Fixed(ContentLeft + ContentWidth - 122, y + 20, 76, 28),
                    CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "overview-" + target);
        }

        private string OnOff(string path)
        {
            return ConfigAccess.Get(_controller.Config, path) >= 0.5f ? "On" : "Off";
        }

        private string ValueByCode(string code)
        {
            VisualSetting setting = VisualSettingRegistry.ByCode(code);
            return setting == null ? "n/a" : _controller.DisplayValue(setting);
        }

        private void AddSettingsTab(SettingTab tab)
        {
            List<RowItem> rows = RowsFor(tab).ToList();
            AddScrollableRows(rows, BodyY, BodyHeight);
        }

        private IEnumerable<RowItem> RowsFor(SettingTab tab)
        {
            string currentSection = null;
            foreach (VisualSetting setting in _controller.VisibleSettings(tab, _advanced))
            {
                if (!string.Equals(currentSection, setting.Section, StringComparison.Ordinal))
                {
                    currentSection = setting.Section;
                    yield return RowItem.ForSection(currentSection);
                }
                yield return RowItem.ForSetting(setting);
            }
        }

        private void AddScrollableRows(List<RowItem> rows, double bodyY, double bodyHeight)
        {
            _currentBodyHeight = bodyHeight;

            ElementBounds clipBounds = ElementBounds.Fixed(ContentLeft, bodyY, ContentWidth, bodyHeight);
            ElementBounds scrollbarBounds = ElementBounds.Fixed(ContentLeft + ContentWidth + 6, bodyY, 16, bodyHeight);
            _currentContentHeight = MeasureRows(rows);
            _currentScroll = ClampScroll(_currentScroll);
            SaveScroll();

            SingleComposer.BeginClip(clipBounds);

            double y = -_currentScroll;
            foreach (RowItem row in rows)
            {
                if (row.IsSection)
                {
                    AddSection(row.Section, y, bodyHeight);
                    y += HeaderHeight;
                }
                else
                {
                    AddSettingRow(row.Setting, y, bodyHeight);
                    y += RowHeight;
                }
            }

            SingleComposer.EndClip();

            if (_currentContentHeight > bodyHeight)
            {
                SingleComposer.AddVerticalScrollbar(OnScrollbarChanged, scrollbarBounds, ScrollbarKey);
            }
        }

        private void AddSection(string label, double y, double bodyHeight)
        {
            if (y < -HeaderHeight || y > bodyHeight) return;

            SingleComposer
                .AddStaticText(label, CairoFont.WhiteSmallText(), ElementBounds.Fixed(RowLeft, y + 4, 210, 20))
                .AddInset(ElementBounds.Fixed(RowLeft + 150, y + 13, ContentWidth - 180, 1), 1, 0.45f);
        }

        private void AddSettingRow(VisualSetting setting, double y, double bodyHeight)
        {
            if (y < -RowHeight || y > bodyHeight) return;

            bool modified = _controller.IsModified(setting);

            SingleComposer
                .AddStaticText(setting.DisplayName, CairoFont.WhiteSmallText(), ElementBounds.Fixed(RowLeft + 18, y + 6, LabelWidth - 18, 22))
                .AddStaticText(ShortDescription(setting), CairoFont.WhiteDetailText(), ElementBounds.Fixed(RowLeft + 18, y + 29, LabelWidth + InfoWidth, 18));

            if (modified)
            {
                SingleComposer.AddButton("Reset", () => ResetSetting(setting), ElementBounds.Fixed(ResetX, y + 14, ResetWidth, 24),
                    CairoFont.WhiteSmallText(), EnumButtonStyle.Small, "reset-" + setting.Code);
            }

            if (setting.Kind != SettingKind.Toggle)
            {
                SingleComposer.AddStaticText(_controller.DisplayValue(setting), CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(ValueX, y + 16, ValueWidth, 22));
            }

            if (setting.Kind == SettingKind.Toggle) AddToggle(setting, y);
            else if (setting.Kind == SettingKind.Dropdown) AddDropdown(setting, y);
            else AddSlider(setting, y);
        }

        private string Tooltip(VisualSetting setting)
        {
            string text = setting.DisplayName + "\n\n" +
                          setting.Description + "\n\n" +
                          "Default: " + DefaultValue(setting) + "\n" +
                          "Performance: " + CostLabel(setting.Cost);

            if (setting.Unit != null) text += "\nUnit: " + setting.Unit;
            if (setting.RequiresShaderReload) text += "\nChanging this rebuilds shaders.";
            if (setting.Advanced) text += "\nAdvanced setting.";

            return text;
        }

        private string DefaultValue(VisualSetting setting)
        {
            if (setting.Kind == SettingKind.Dropdown) return ConfigAccess.DefaultString(setting.Path) ?? "";
            if (setting.Kind == SettingKind.Toggle) return ConfigAccess.Default(setting.Path) >= 0.5f ? "On" : "Off";
            return setting.Format(ConfigAccess.Default(setting.Path));
        }

        private static string CostLabel(SettingCost cost)
        {
            return cost == SettingCost.Free ? "None" : cost.ToString();
        }

        private void AddToggle(VisualSetting setting, double y)
        {
            bool on = _controller.Value(setting) >= 0.5f;
            int generation = _composerGeneration;
            SingleComposer.AddStaticText(on ? "ON" : "OFF", CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(ControlX, y + 16, 42, 22));

            SingleComposer.AddSwitch(value =>
            {
                if (!IsCurrentGeneration(generation, "ToggleChanged " + setting.Code)) return;
                Log("ENTER ToggleChanged " + setting.Code);
                _ignoreNextConfigRefresh = true;
                _controller.Set(setting, value ? 1f : 0f);
                Log("EXIT ToggleChanged " + setting.Code);
            }, ElementBounds.Fixed(ControlX + 50, y + 13, 58, 28), "switch-" + setting.Code);

            GuiElementSwitch sw = SingleComposer.GetSwitch("switch-" + setting.Code);
            if (sw != null) sw.On = on;
        }

        private void AddSlider(VisualSetting setting, double y)
        {
            int generation = _composerGeneration;
            int steps = Math.Max(1, (int)Math.Round((setting.Max - setting.Min) / setting.Step));
            int value = (int)Math.Round((_controller.Value(setting) - setting.Min) / setting.Step);
            value = Math.Max(0, Math.Min(steps, value));

            SingleComposer.AddSlider(newValue =>
            {
                if (!IsCurrentGeneration(generation, "SliderChanged " + setting.Code)) return false;
                Log("ENTER SliderChanged " + setting.Code);
                float target = setting.Min + newValue * setting.Step;
                _ignoreNextConfigRefresh = true;
                _controller.Set(setting, target);
                Log("EXIT SliderChanged " + setting.Code);
                return true;
            }, ElementBounds.Fixed(ControlX, y + 14, ControlWidth, 24), "slider-" + setting.Code);

            GuiElementSlider slider = SingleComposer.GetSlider("slider-" + setting.Code);
            if (slider != null)
            {
                slider.SetValues(value, 0, steps, 1, "0");
            }
        }

        private void AddDropdown(VisualSetting setting, double y)
        {
            int generation = _composerGeneration;
            string current = _controller.StringValue(setting);
            int selected = Array.FindIndex(setting.Choices, c => string.Equals(c, current, StringComparison.Ordinal));
            if (selected < 0) selected = 0;

            SingleComposer.AddDropDown(setting.Choices, setting.Choices, selected,
                (code, selectedValue) =>
                {
                    if (!selectedValue) return;
                    if (!IsCurrentGeneration(generation, "DropdownChanged " + setting.Code)) return;
                    Log("ENTER DropdownChanged " + setting.Code);
                    _ignoreNextConfigRefresh = true;
                    _controller.SetString(setting, code);
                    Log("EXIT DropdownChanged " + setting.Code);
                },
                ElementBounds.Fixed(ControlX, y + 14, Math.Min(ControlWidth, 220), 24), "dropdown-" + setting.Code);
        }

        private static string ShortDescription(VisualSetting setting)
        {
            return ShortText(setting.Description, 48);
        }

        private static string ShortText(string text, int max)
        {
            text = text ?? "";
            if (text.Length <= max) return text;

            int cut = text.LastIndexOf(' ', max);
            if (cut < 24) cut = max;
            return text.Substring(0, cut) + "...";
        }

        private bool ResetSetting(VisualSetting setting)
        {
            Log("ENTER ResetSetting " + setting.Code);
            _ignoreNextConfigRefresh = true;
            if (_controller.Reset(setting)) ScheduleCompose("reset setting");
            Log("EXIT ResetSetting " + setting.Code);
            return true;
        }

        private void AddDebug()
        {
            double y = BodyY + 4;
            int generation = _composerGeneration;
            SyncDebugSystemToActiveView();

            SingleComposer
                .AddStaticText("Debug System", CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentLeft, y, 150, 22))
                .AddDropDown(_debugSystems.Select(s => s.Label).ToArray(), _debugSystems.Select(s => s.Label).ToArray(),
                    _debugSystemIndex,
                    (code, selected) =>
                    {
                        if (!IsCurrentGeneration(generation, "DebugSystemChanged")) return;
                        OnDebugSystemChanged(code, selected);
                    },
                    ElementBounds.Fixed(ContentLeft + LabelWidth + 8, y - 2, 220, 26), "debug-system");

            y += 42;
            List<DebugView> views = CurrentDebugViews().ToList();
            DebugView current = CurrentDebugView(views);
            int selected = Math.Max(0, views.FindIndex(v => v.Value == current.Value));

            SingleComposer
                .AddStaticText("Debug View", CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentLeft, y, 150, 22))
                .AddDropDown(views.Select(v => v.Value.ToString()).ToArray(), views.Select(v => v.Label).ToArray(),
                    selected,
                    (code, selectedValue) =>
                    {
                        if (!IsCurrentGeneration(generation, "DebugViewChanged")) return;
                        OnDebugViewChanged(code, selectedValue);
                    },
                    ElementBounds.Fixed(ContentLeft + LabelWidth + 8, y - 2, 360, 26), "debug-view");

            y += 46;
            SingleComposer.AddStaticText(ShortText(current.Description, 92), CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(ContentLeft, y - 12, ContentWidth - 40, 18));

            if (current.Value != 0)
            {
                AddDebugBanner(current, y);
                y += 82;
            }

            List<RowItem> diagnostics = _controller.VisibleSettings(SettingTab.Debug, true)
                .Select(RowItem.ForSetting)
                .ToList();

            if (diagnostics.Count > 0)
            {
                diagnostics.Insert(0, RowItem.ForSection("Diagnostics"));
            }

            double diagnosticsY = y + 8;
            double diagnosticsHeight = Math.Max(120, BodyY + BodyHeight - diagnosticsY);
            AddScrollableRows(diagnostics, diagnosticsY, diagnosticsHeight);
        }

        private void AddDebugBanner(DebugView current, double y)
        {
            SingleComposer
                .AddInset(ElementBounds.Fixed(ContentLeft, y, ContentWidth - 34, 62), 2, 0.6f)
                .AddStaticText("DEBUG VIEW ACTIVE", CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentLeft + 14, y + 8, 180, 22))
                .AddStaticText(current.Label, CairoFont.WhiteDetailText(), ElementBounds.Fixed(ContentLeft + 14, y + 31, 330, 20))
                .AddButton("Return to Normal Rendering", () => Guard(_composerGeneration, "ReturnToNormal", OnReturnToNormalClicked),
                    ElementBounds.Fixed(ContentLeft + ContentWidth - 244, y + 16, 210, 28), CairoFont.WhiteSmallText(),
                    EnumButtonStyle.Normal, "normal-rendering");
        }

        private void OnDebugSystemChanged(string code, bool selected)
        {
            if (!selected) return;
            Log("ENTER DebugSystemChanged");
            int index = Array.FindIndex(_debugSystems, s => s.Label == code);
            if (index < 0) return;

            _debugSystemIndex = index;
            _debugOwnerPath = _debugSystems[index].OwnerPath;
            ScheduleCompose("debug system");
            Log("EXIT DebugSystemChanged");
        }

        private void OnDebugViewChanged(string code, bool selected)
        {
            if (!selected) return;
            Log("ENTER DebugViewChanged");
            DebugView view = CurrentDebugViews().FirstOrDefault(v => v.Value.ToString() == code);
            _ignoreNextConfigRefresh = true;
            _controller.SetDebugView(view);
            Log("EXIT DebugViewChanged");
        }

        private IEnumerable<DebugView> CurrentDebugViews()
        {
            DebugSystem system = _debugSystems[_debugSystemIndex];
            return DebugViewRegistry.For(system.OwnerPath).Where(system.Include);
        }

        private DebugView CurrentDebugView(List<DebugView> views)
        {
            int current = (int)Math.Round(ConfigAccess.Get(_controller.Config, _debugOwnerPath));
            return views.FirstOrDefault(v => v.Value == current)
                ?? views.First(v => v.Value == 0);
        }

        private string DebugTooltip(DebugView view)
        {
            return view.DisplayName + "\n\n" + view.Description + "\n\nUnderlying shader mode: " + view.Value;
        }

        private void SyncDebugSystemToActiveView()
        {
            foreach (DebugSystem system in _debugSystems)
            {
                int value = (int)Math.Round(ConfigAccess.Get(_controller.Config, system.OwnerPath));
                if (value == 0) continue;
                DebugView view = DebugViewRegistry.Current(system.OwnerPath, value);
                if (view == null || !system.Include(view)) continue;

                _debugOwnerPath = system.OwnerPath;
                _debugSystemIndex = Array.IndexOf(_debugSystems, system);
                return;
            }
        }

        private bool OnReturnToNormalClicked()
        {
            Log("ENTER ReturnToNormal");
            _ignoreNextConfigRefresh = true;
            _controller.ReturnToNormalRendering();
            ScheduleCompose("return to normal");
            Log("EXIT ReturnToNormal");
            return true;
        }

        private double MeasureRows(List<RowItem> rows)
        {
            return rows.Sum(row => row.IsSection ? HeaderHeight : RowHeight);
        }

        private void OnScrollbarChanged(float value)
        {
            Log("ENTER ScrollbarChanged");
            _currentScroll = ClampScroll(value);
            SaveScroll();
            ScheduleCompose("scrollbar");
            Log("EXIT ScrollbarChanged");
        }

        private void ScrollTo(double value)
        {
            Log("ENTER MouseWheelScroll");
            _currentScroll = ClampScroll(value);
            SaveScroll();
            ScheduleCompose("mouse wheel");
            Log("EXIT MouseWheelScroll");
        }

        private double CurrentScroll()
        {
            double value;
            return _scrollByTab.TryGetValue(_tab, out value) ? value : 0;
        }

        private void SaveScroll()
        {
            _scrollByTab[_tab] = _currentScroll;
        }

        private double ClampScroll(double value)
        {
            return Math.Max(0, Math.Min(Math.Max(0, _currentContentHeight - _currentBodyHeight), value));
        }

        private void ConfigureScrollbar()
        {
            GuiElementScrollbar scrollbar = SingleComposer.GetScrollbar(ScrollbarKey);
            if (scrollbar == null) return;

            scrollbar.SetHeights((float)_currentBodyHeight, (float)Math.Max(_currentBodyHeight, _currentContentHeight));
            scrollbar.SetScrollbarPosition((int)Math.Round(_currentScroll));
        }

        private bool Guard(int generation, string name, System.Func<bool> action)
        {
            if (!IsCurrentGeneration(generation, name)) return false;

            Log("ENTER " + name);
            bool result = action();
            Log("EXIT " + name);
            return result;
        }

        private bool IsCurrentGeneration(int generation, string callback)
        {
            if (generation == _composerGeneration) return true;

            Log("STALE CALLBACK " + callback + " callbackGeneration=" + generation);
            return false;
        }

        private void ScheduleCompose(string reason)
        {
            Log("ScheduleCompose " + reason);
            if (_recomposePending) return;

            _recomposePending = true;
            capi.Event.RegisterCallback(_ =>
            {
                Log("ENTER DeferredCompose " + reason);
                if (!IsOpened())
                {
                    _recomposePending = false;
                    Log("EXIT DeferredCompose skipped closed");
                    return;
                }

                _recomposePending = false;
                ComposeDialog();
                Log("EXIT DeferredCompose " + reason);
            }, RecomposeDelayMs);
        }

        private void Log(string message)
        {
            int sequence = Interlocked.Increment(ref _eventSequence);
            capi.Logger.Notification("[VV UI #" + sequence + "] " + message +
                " tab=" + _tab +
                " composerGen=" + _composerGeneration +
                " thread=" + Thread.CurrentThread.ManagedThreadId +
                " composerNull=" + (SingleComposer == null) +
                " opened=" + IsOpened() +
                " composing=" + _isComposing +
                " pending=" + _recomposePending);
        }

        private sealed class DebugSystem
        {
            public readonly string Label;
            public readonly string OwnerPath;
            public readonly System.Func<DebugView, bool> Include;

            public DebugSystem(string label, string ownerPath, System.Func<DebugView, bool> include)
            {
                Label = label;
                OwnerPath = ownerPath;
                Include = include;
            }
        }

        private sealed class RowItem
        {
            public readonly string Section;
            public readonly VisualSetting Setting;

            public bool IsSection { get { return Section != null; } }

            private RowItem(string section, VisualSetting setting)
            {
                Section = section;
                Setting = setting;
            }

            public static RowItem ForSection(string section)
            {
                return new RowItem(section, null);
            }

            public static RowItem ForSetting(VisualSetting setting)
            {
                return new RowItem(null, setting);
            }
        }
    }
}
