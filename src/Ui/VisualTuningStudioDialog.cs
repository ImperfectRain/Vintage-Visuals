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

        private readonly VisualTuningStudioController _controller;
        private readonly Dictionary<SettingTab, string> _tabNames = new Dictionary<SettingTab, string>
        {
            { SettingTab.Overview, "Overview" },
            { SettingTab.Color, "Color" },
            { SettingTab.Materials, "PBR & Materials" },
            { SettingTab.Weather, "Weather" },
            { SettingTab.Atmosphere, "Atmosphere" },
            { SettingTab.Reflections, "Reflections" },
            { SettingTab.Debug, "Debug" },
        };

        private SettingTab _tab = SettingTab.Overview;
        private bool _advanced;
        private int _scrollFirstSetting;

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

        private void ComposeDialog()
        {
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedSize(980, 650);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            ElementBounds titleBounds = ElementBounds.Fixed(0, 0, 940, 34);

            SingleComposer = capi.Gui.CreateCompo("vintagevisuals-tuning-studio", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Vintage Visuals", OnTitleBarClose)
                .BeginChildElements(bgBounds)
                    .AddStaticText("Vintage Visuals", CairoFont.WhiteMediumText(), titleBounds)
                    .AddButton("Close", OnCloseClicked, ElementBounds.Fixed(850, 0, 80, 28), CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "close")
                    .AddInset(ElementBounds.Fixed(0, 42, 940, 46), 2)
                    .AddInset(ElementBounds.Fixed(0, 98, 940, 500), 2)
                    .AddButton(_advanced ? "Basic" : "Advanced", OnAdvancedClicked, ElementBounds.Fixed(760, 604, 84, 28), CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "advanced")
                    .AddButton("Reset Tab", OnResetTabClicked, ElementBounds.Fixed(850, 604, 90, 28), CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "reset-tab");

            AddTabs();

            if (_tab == SettingTab.Overview) AddOverview();
            else if (_tab == SettingTab.Debug) AddDebug();
            else AddSettingsTab(_tab);

            SingleComposer.EndChildElements().Compose();
        }

        private void AddTabs()
        {
            double x = 10;
            foreach (KeyValuePair<SettingTab, string> pair in _tabNames)
            {
                SettingTab tab = pair.Key;
                string label = pair.Key == _tab ? "[" + pair.Value + "]" : pair.Value;
                double width = tab == SettingTab.Materials ? 146 : 108;

                SingleComposer.AddButton(label, () => SelectTab(tab),
                    ElementBounds.Fixed(x, 50, width, 28), CairoFont.WhiteSmallText(), EnumButtonStyle.Normal,
                    "tab-" + tab);

                x += width + 8;
            }
        }

        private bool SelectTab(SettingTab tab)
        {
            _tab = tab;
            _scrollFirstSetting = 0;
            ComposeDialog();
            return true;
        }

        private bool OnAdvancedClicked()
        {
            _advanced = !_advanced;
            _scrollFirstSetting = 0;
            ComposeDialog();
            return true;
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
            double y = 112;
            AddParagraph("Major systems", "Toggle states below reflect the current in-memory config. Use the buttons to jump to each subsystem.", y);
            y += 54;

            AddOverviewRow("Color grade", "ColorGrade.Enabled", SettingTab.Color, y); y += 42;
            AddOverviewRow("PBR", "PseudoPBR.Enabled", SettingTab.Materials, y); y += 42;
            AddOverviewRow("Weather", "Weather.Enabled", SettingTab.Weather, y); y += 42;
            AddOverviewRow("Atmosphere", "Atmosphere.Enabled", SettingTab.Atmosphere, y); y += 42;
            AddOverviewRow("Scene reflections", "Reflections.SceneReflections", SettingTab.Reflections, y);
        }

        private void AddOverviewRow(string label, string path, SettingTab target, double y)
        {
            bool on = ConfigAccess.Get(_controller.Config, path) >= 0.5f;
            SingleComposer
                .AddStaticText(label, CairoFont.WhiteSmallText(), ElementBounds.Fixed(24, y, 220, 24))
                .AddStaticText(on ? "On" : "Off", CairoFont.WhiteSmallText(), ElementBounds.Fixed(270, y, 80, 24))
                .AddButton("Open", () => SelectTab(target), ElementBounds.Fixed(790, y - 2, 90, 28),
                    CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "open-" + target);
        }

        private void AddSettingsTab(SettingTab tab)
        {
            double y = 112;
            string currentSection = null;
            List<VisualSetting> visible = _controller.VisibleSettings(tab, _advanced).ToList();
            int first = Math.Max(0, Math.Min(_scrollFirstSetting, Math.Max(0, visible.Count - 1)));
            int shown = 0;

            foreach (VisualSetting setting in visible.Skip(first))
            {
                if (!string.Equals(currentSection, setting.Section, StringComparison.Ordinal))
                {
                    currentSection = setting.Section;
                    SingleComposer.AddStaticText(currentSection, CairoFont.WhiteMediumText(), ElementBounds.Fixed(24, y, 420, 24));
                    y += 32;
                }

                AddSettingRow(setting, y);
                y += 72;
                shown++;
                if (y > 570)
                {
                    break;
                }
            }

            AddScrollControls(visible.Count, first, shown);
        }

        private void AddScrollControls(int total, int first, int shown)
        {
            if (total <= shown) return;

            SingleComposer
                .AddButton("Up", OnScrollUpClicked, ElementBounds.Fixed(520, 604, 56, 28),
                    CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "scroll-up")
                .AddButton("Down", OnScrollDownClicked, ElementBounds.Fixed(584, 604, 62, 28),
                    CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "scroll-down")
                .AddStaticText((first + 1) + "-" + Math.Min(total, first + shown) + " of " + total,
                    CairoFont.WhiteSmallText(), ElementBounds.Fixed(650, 608, 100, 24));
        }

        private bool OnScrollUpClicked()
        {
            _scrollFirstSetting = Math.Max(0, _scrollFirstSetting - 3);
            ComposeDialog();
            return true;
        }

        private bool OnScrollDownClicked()
        {
            _scrollFirstSetting += 3;
            ComposeDialog();
            return true;
        }

        private void AddDebug()
        {
            double y = 112;
            AddParagraph("Friendly diagnostics", "Choose named debug views instead of typing shader mode numbers. Return to normal resets all debug view selectors and the compare wipe.", y);
            y += 60;

            AddDebugGroup("PBR", DebugViewRegistry.PbrPath, y); y += 54;
            AddDebugGroup("Entities", DebugViewRegistry.EntityPath, y); y += 54;
            AddDebugGroup("Weather clouds", DebugViewRegistry.CloudPath, y); y += 54;
            AddDebugGroup("Atmosphere", DebugViewRegistry.AtmospherePath, y); y += 70;

            foreach (VisualSetting setting in _controller.VisibleSettings(SettingTab.Debug, true))
            {
                AddSettingRow(setting, y);
                y += 72;
            }

            SingleComposer.AddButton("Return to Normal Rendering", OnReturnToNormalClicked,
                ElementBounds.Fixed(650, 548, 230, 30), CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "normal-rendering");
        }

        private void AddDebugGroup(string label, string ownerPath, double y)
        {
            List<DebugView> views = DebugViewRegistry.For(ownerPath).ToList();
            int current = (int)Math.Round(ConfigAccess.Get(_controller.Config, ownerPath));
            int selected = Math.Max(0, views.FindIndex(v => v.Value == current));

            SingleComposer.AddStaticText(label, CairoFont.WhiteSmallText(), ElementBounds.Fixed(24, y + 4, 160, 24));
            SingleComposer.AddDropDown(
                views.Select(v => v.Value.ToString()).ToArray(),
                views.Select(v => v.Label).ToArray(),
                selected,
                (code, selectedValue) =>
                {
                    if (!selectedValue) return;
                    DebugView view = views.FirstOrDefault(v => v.Value.ToString() == code);
                    if (_controller.SetDebugView(view)) ComposeDialog();
                },
                ElementBounds.Fixed(190, y, 520, 28),
                "debug-" + ownerPath.Replace('.', '-'));
        }

        private bool OnReturnToNormalClicked()
        {
            _controller.ReturnToNormalRendering();
            ComposeDialog();
            return true;
        }

        private void AddSettingRow(VisualSetting setting, double y)
        {
            SingleComposer.AddStaticText(setting.DisplayName, CairoFont.WhiteSmallText(), ElementBounds.Fixed(24, y, 210, 24));
            SingleComposer.AddStaticText(_controller.DisplayValue(setting), CairoFont.WhiteSmallText(), ElementBounds.Fixed(246, y, 100, 24));

            if (setting.Kind == SettingKind.Toggle) AddToggle(setting, y);
            else if (setting.Kind == SettingKind.Dropdown) AddDropdown(setting, y);
            else AddSlider(setting, y);

            SingleComposer.AddStaticText(setting.Description, CairoFont.WhiteSmallText(), ElementBounds.Fixed(24, y + 30, 760, 34));

            if (_controller.IsModified(setting))
            {
                SingleComposer.AddButton("Reset", () => ResetSetting(setting), ElementBounds.Fixed(810, y, 72, 26),
                    CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "reset-" + setting.Code);
            }
        }

        private void AddToggle(VisualSetting setting, double y)
        {
            bool on = _controller.Value(setting) >= 0.5f;
            SingleComposer.AddSwitch(value =>
            {
                if (_controller.Set(setting, value ? 1f : 0f)) ComposeDialog();
            }, ElementBounds.Fixed(360, y, 60, 28), "switch-" + setting.Code);

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
            }, ElementBounds.Fixed(360, y, 330, 28), "slider-" + setting.Code);

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
                ElementBounds.Fixed(360, y, 260, 28), "dropdown-" + setting.Code);
        }

        private bool ResetSetting(VisualSetting setting)
        {
            _controller.Reset(setting);
            ComposeDialog();
            return true;
        }

        private void AddParagraph(string title, string body, double y)
        {
            SingleComposer
                .AddStaticText(title, CairoFont.WhiteMediumText(), ElementBounds.Fixed(24, y, 500, 24))
                .AddStaticText(body, CairoFont.WhiteSmallText(), ElementBounds.Fixed(24, y + 28, 780, 42));
        }
    }
}
