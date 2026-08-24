using System;
using System.Collections.Generic;
using System.Linq;
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
        private const double NavWidth = 150;
        private const double ContentX = 168;
        private const double ContentWidth = 650;
        private const double BodyY = 94;
        private const double BodyHeight = 440;
        private const double RowHeight = 44;
        private const double HeaderHeight = 26;
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

        public VisualTuningStudioDialog(ICoreClientAPI capi, VisualTuningStudioController controller)
            : base(capi)
        {
            _controller = controller;
            ComposeDialog();
        }

        public override string ToggleKeyCombinationCode { get { return HotkeyCode; } }
        public override EnumDialogType DialogType { get { return EnumDialogType.Dialog; } }

        public void RefreshFromConfig()
        {
            if (!IsOpened()) return;
            ComposeDialog();
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

        private void ComposeDialog()
        {
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedSize(DialogWidth, DialogHeight);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            _currentScroll = CurrentScroll();

            SingleComposer = capi.Gui.CreateCompo("vintagevisuals-tuning-studio", dialogBounds)
                .PremultipliedAlpha(false)
                .AddShadedDialogBG(bgBounds, true, 4, 0.68f)
                .AddDialogTitleBar("Vintage Visuals", OnTitleBarClose)
                .BeginChildElements(bgBounds)
                    .AddStaticText(PageTitle(), CairoFont.WhiteMediumText(), ElementBounds.Fixed(ContentX, 44, 380, 26))
                    .AddButton("Close", OnCloseClicked, ElementBounds.Fixed(760, 42, 64, 26),
                        CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "close")
                    .AddInset(ElementBounds.Fixed(10, 42, NavWidth, 528), 2)
                    .AddInset(ElementBounds.Fixed(ContentX - 8, BodyY - 8, ContentWidth + 26, BodyHeight + 16), 2)
                    .AddStaticText("Advanced Settings", CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentX, 552, 150, 24))
                    .AddSwitch(value =>
                    {
                        _advanced = value;
                        ComposeDialog();
                    }, ElementBounds.Fixed(ContentX + 150, 548, 58, 28), "advanced")
                    .AddButton("Reset Tab", OnResetTabClicked, ElementBounds.Fixed(724, 548, 96, 28),
                        CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "reset-tab");

            GuiElementSwitch adv = SingleComposer.GetSwitch("advanced");
            if (adv != null) adv.On = _advanced;

            AddTabs();

            if (_tab == SettingTab.Overview) AddOverview();
            else if (_tab == SettingTab.Debug) AddDebug();
            else AddSettingsTab(_tab);

            SingleComposer.EndChildElements().Compose();
            ConfigureScrollbar();
        }

        private void AddTabs()
        {
            foreach (GuiTab tab in _tabs) tab.Active = tab.DataInt == (int)_tab;

            SingleComposer.AddVerticalTabs(_tabs, ElementBounds.Fixed(18, 58, 130, 332),
                (index, tab) => SelectTab((SettingTab)tab.DataInt), "studio-tabs");

            SingleComposer.AddStaticText("Presets", CairoFont.WhiteDetailText(), ElementBounds.Fixed(28, 424, 90, 20));
            SingleComposer.AddStaticText("Coming later", CairoFont.WhiteDetailText(), ElementBounds.Fixed(28, 446, 100, 20));
        }

        private string PageTitle()
        {
            if (_tab == SettingTab.Materials) return "PBR & Materials";
            return _tabs.First(t => t.DataInt == (int)_tab).Name;
        }

        private void SelectTab(SettingTab tab)
        {
            SaveScroll();
            _tab = tab;
            ComposeDialog();
        }

        private bool OnResetTabClicked()
        {
            _controller.ResetTab(_tab, _advanced);
            ComposeDialog();
            return true;
        }

        private bool OnCloseClicked()
        {
            TryClose();
            return true;
        }

        private void OnTitleBarClose()
        {
            TryClose();
        }

        private void AddOverview()
        {
            double y = BodyY + 6;
            SingleComposer.AddStaticText("Systems", CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentX, y, 180, 22));
            y += 34;

            AddOverviewRow("COLOR & EXPOSURE", "ColorGrade.Enabled", SettingTab.Color,
                "Exposure " + ValueByCode("colorgrade_exposure") + " · Eye adaptation " + OnOff("AdaptiveExposure.Enabled"), y);
            y += 58;

            AddOverviewRow("MATERIALS", "PseudoPBR.Enabled", SettingTab.Materials,
                "Surface Relief " + ValueByCode("pbr_normalstrength") + " · Reflections " + OnOff("Reflections.SceneReflections"), y);
            y += 58;

            AddOverviewRow("WEATHER", "Weather.Enabled", SettingTab.Weather,
                "Wetness " + ValueByCode("weather_wetness") + " · Cloud Shadows " + ValueByCode("weather_cloudshadow"), y);
            y += 58;

            AddOverviewRow("ATMOSPHERE", "Atmosphere.Enabled", SettingTab.Atmosphere,
                "Scattering " + ValueByCode("atmosphere_scattering") + " · Ground Haze " + ValueByCode("atmosphere_groundhaze"), y);
            y += 58;

            AddOverviewRow("REFLECTIONS", "Reflections.SceneReflections", SettingTab.Reflections,
                "Scene reflections " + OnOff("Reflections.SceneReflections") + " · Pixel strength " + ValueByCode("pbr_pixelreflect"), y);
        }

        private void AddOverviewRow(string label, string path, SettingTab target, string summary, double y)
        {
            bool on = ConfigAccess.Get(_controller.Config, path) >= 0.5f;
            string status = (on ? "● On" : "○ Off") + "   >";
            string text = label.PadRight(28) + status + "\n" + summary;

            SingleComposer.AddButton(text, () =>
            {
                SelectTab(target);
                return true;
            }, ElementBounds.Fixed(ContentX, y, ContentWidth - 28, 46), CairoFont.WhiteSmallText(),
                EnumButtonStyle.Normal, "overview-" + target);
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

            ElementBounds clipBounds = ElementBounds.Fixed(ContentX, bodyY, ContentWidth, bodyHeight);
            ElementBounds scrollbarBounds = ElementBounds.Fixed(ContentX + ContentWidth + 6, bodyY, 16, bodyHeight);

            _currentContentHeight = MeasureRows(rows);
            _currentScroll = ClampScroll(_currentScroll);
            SaveScroll();

            SingleComposer.BeginClip(clipBounds);

            double y = bodyY - _currentScroll;
            foreach (RowItem row in rows)
            {
                if (row.IsSection)
                {
                    AddSection(row.Section, y, bodyY, bodyHeight);
                    y += HeaderHeight;
                }
                else
                {
                    AddSettingRow(row.Setting, y, bodyY, bodyHeight);
                    y += RowHeight;
                }
            }

            SingleComposer.EndClip();

            if (_currentContentHeight > BodyHeight)
            {
                SingleComposer.AddVerticalScrollbar(OnScrollbarChanged, scrollbarBounds, ScrollbarKey);
            }
        }

        private void AddSection(string label, double y, double bodyY, double bodyHeight)
        {
            if (y < bodyY - HeaderHeight || y > bodyY + bodyHeight) return;

            SingleComposer
                .AddStaticText(label, CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentX, y + 4, 210, 20))
                .AddInset(ElementBounds.Fixed(ContentX + 150, y + 13, ContentWidth - 180, 1), 1, 0.45f);
        }

        private void AddSettingRow(VisualSetting setting, double y, double bodyY, double bodyHeight)
        {
            if (y < bodyY - RowHeight || y > bodyY + bodyHeight) return;

            bool modified = _controller.IsModified(setting);
            string marker = modified ? "●" : "";
            string resetText = modified ? "↺" : " ";

            SingleComposer
                .AddStaticText(marker, CairoFont.WhiteDetailText(), ElementBounds.Fixed(ContentX, y + 12, 14, 20))
                .AddStaticText(setting.DisplayName, CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentX + 18, y + 10, 178, 22))
                .AddButton("ⓘ", () => true, ElementBounds.Fixed(ContentX + 196, y + 7, 24, 24),
                    CairoFont.WhiteSmallText(), EnumButtonStyle.Small, "info-" + setting.Code)
                .AddHoverText(Tooltip(setting), CairoFont.WhiteDetailText(), 320,
                    ElementBounds.Fixed(ContentX + 196, y + 7, 24, 24), "tip-" + setting.Code)
                .AddStaticText(_controller.DisplayValue(setting), CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(ContentX + 226, y + 10, 96, 22))
                .AddButton(resetText, () => ResetSetting(setting), ElementBounds.Fixed(ContentX + 602, y + 7, 28, 24),
                    CairoFont.WhiteSmallText(), EnumButtonStyle.Small, "reset-" + setting.Code)
                .AddHoverText("Reset to default (" + DefaultValue(setting) + ")", CairoFont.WhiteDetailText(), 220,
                    ElementBounds.Fixed(ContentX + 602, y + 7, 28, 24), "reset-tip-" + setting.Code);

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
            SingleComposer.AddStaticText(on ? "ON" : "OFF", CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(ContentX + 336, y + 10, 42, 22));

            SingleComposer.AddSwitch(value =>
            {
                if (_controller.Set(setting, value ? 1f : 0f)) ComposeDialog();
            }, ElementBounds.Fixed(ContentX + 386, y + 7, 58, 28), "switch-" + setting.Code);

            GuiElementSwitch sw = SingleComposer.GetSwitch("switch-" + setting.Code);
            if (sw != null) sw.On = on;
        }

        private void AddSlider(VisualSetting setting, double y)
        {
            int steps = Math.Max(1, (int)Math.Round((setting.Max - setting.Min) / setting.Step));
            int value = (int)Math.Round((_controller.Value(setting) - setting.Min) / setting.Step);
            value = Math.Max(0, Math.Min(steps, value));

            SingleComposer.AddSlider(newValue =>
            {
                float target = setting.Min + newValue * setting.Step;
                if (_controller.Set(setting, target)) ComposeDialog();
                return true;
            }, ElementBounds.Fixed(ContentX + 336, y + 8, 248, 24), "slider-" + setting.Code);

            GuiElementSlider slider = SingleComposer.GetSlider("slider-" + setting.Code);
            if (slider != null)
            {
                slider.SetValues(value, 0, steps, 1, "0");
            }
        }

        private void AddDropdown(VisualSetting setting, double y)
        {
            string current = _controller.StringValue(setting);
            int selected = Array.FindIndex(setting.Choices, c => string.Equals(c, current, StringComparison.Ordinal));
            if (selected < 0) selected = 0;

            SingleComposer.AddDropDown(setting.Choices, setting.Choices, selected,
                (code, selectedValue) =>
                {
                    if (!selectedValue) return;
                    if (_controller.SetString(setting, code)) ComposeDialog();
                },
                ElementBounds.Fixed(ContentX + 336, y + 8, 210, 24), "dropdown-" + setting.Code);
        }

        private bool ResetSetting(VisualSetting setting)
        {
            if (_controller.Reset(setting)) ComposeDialog();
            return true;
        }

        private void AddDebug()
        {
            double y = BodyY + 4;
            SyncDebugSystemToActiveView();

            SingleComposer
                .AddStaticText("Debug System", CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentX, y, 150, 22))
                .AddDropDown(_debugSystems.Select(s => s.Label).ToArray(), _debugSystems.Select(s => s.Label).ToArray(),
                    _debugSystemIndex, OnDebugSystemChanged, ElementBounds.Fixed(ContentX + 160, y - 2, 220, 26), "debug-system");

            y += 42;
            List<DebugView> views = CurrentDebugViews().ToList();
            DebugView current = CurrentDebugView(views);
            int selected = Math.Max(0, views.FindIndex(v => v.Value == current.Value));

            SingleComposer
                .AddStaticText("Debug View", CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentX, y, 150, 22))
                .AddDropDown(views.Select(v => v.Value.ToString()).ToArray(), views.Select(v => v.Label).ToArray(),
                    selected, OnDebugViewChanged, ElementBounds.Fixed(ContentX + 160, y - 2, 360, 26), "debug-view")
                .AddButton("ⓘ", () => true, ElementBounds.Fixed(ContentX + 532, y - 2, 24, 24),
                    CairoFont.WhiteSmallText(), EnumButtonStyle.Small, "debug-info")
                .AddHoverText(DebugTooltip(current), CairoFont.WhiteDetailText(), 340,
                    ElementBounds.Fixed(ContentX + 532, y - 2, 24, 24), "debug-tip");

            y += 46;
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
                .AddInset(ElementBounds.Fixed(ContentX, y, ContentWidth - 28, 62), 2, 0.6f)
                .AddStaticText("DEBUG VIEW ACTIVE", CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentX + 14, y + 8, 180, 22))
                .AddStaticText(current.Label, CairoFont.WhiteDetailText(), ElementBounds.Fixed(ContentX + 14, y + 31, 330, 20))
                .AddButton("Return to Normal Rendering", OnReturnToNormalClicked,
                    ElementBounds.Fixed(ContentX + 390, y + 16, 210, 28), CairoFont.WhiteSmallText(),
                    EnumButtonStyle.Normal, "normal-rendering");
        }

        private void OnDebugSystemChanged(string code, bool selected)
        {
            if (!selected) return;
            int index = Array.FindIndex(_debugSystems, s => s.Label == code);
            if (index < 0) return;

            _debugSystemIndex = index;
            _debugOwnerPath = _debugSystems[index].OwnerPath;
            ComposeDialog();
        }

        private void OnDebugViewChanged(string code, bool selected)
        {
            if (!selected) return;
            DebugView view = CurrentDebugViews().FirstOrDefault(v => v.Value.ToString() == code);
            if (_controller.SetDebugView(view)) ComposeDialog();
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
            _controller.ReturnToNormalRendering();
            ComposeDialog();
            return true;
        }

        private double MeasureRows(List<RowItem> rows)
        {
            return rows.Sum(row => row.IsSection ? HeaderHeight : RowHeight);
        }

        private void OnScrollbarChanged(float value)
        {
            _currentScroll = ClampScroll(value);
            SaveScroll();
            ComposeDialog();
        }

        private void ScrollTo(double value)
        {
            _currentScroll = ClampScroll(value);
            SaveScroll();
            ComposeDialog();
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
