using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VintageVisuals.PseudoPBR;

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

        // Rows are two lines. The first names the setting and says what it does
        // (or why it cannot); the second gives the control most of the width,
        // with the current value beside it and Reset in a fixed slot after that,
        // so neither moves when a row becomes modified.
        private const double RowLeft = 0;
        private const double RowHeight = 56;
        private const double HeaderHeight = 26;
        private const double RowGap = 10;
        private const double RowRightPadding = 8;
        private const double LabelWidth = 190;
        private const double InfoWidth = 8;
        private const double ValueWidth = 86;
        private const double ResetWidth = 54;
        private const double LabelX = RowLeft + 18;
        private const double InfoX = LabelX + LabelWidth + InfoWidth;
        private const double InfoTextWidth = ContentWidth - InfoX - RowRightPadding;
        private const double ControlX = LabelX;
        private const double ControlWidth = ContentWidth - ControlX - ValueWidth - ResetWidth - 2 * RowGap - RowRightPadding;
        private const double ValueX = ControlX + ControlWidth + RowGap;
        private const double ResetX = ValueX + ValueWidth + RowGap;
        private const double LineOneY = 4;
        private const double LineTwoY = 27;
        private const int InfoChars = 52;
        private const double WheelStep = 48;
        private const string ScrollbarKey = "settings-scrollbar";
        private const string AdvancedSwitchKey = "advanced-switch";

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
        private double _currentBodyY = BodyY;
        private ElementBounds _scrollContentBounds;
        private ElementBounds _viewportBounds;
        private ElementBounds _scrollbarBounds;
        private int _eventSequence;
        private int _composerGeneration;

        /// <summary>
        /// Non-zero while THIS dialog is inside a controller call.
        ///
        /// Replaces a one-shot "ignore the next refresh" flag. ConfigManager
        /// raises ConfigChanged synchronously, so the notification caused by our
        /// own edit arrives while this counter is up and nowhere else. The flag
        /// it replaces was armed BEFORE the write, and the controller sends no
        /// notification for a no-op or clamped-away change - which left it armed
        /// to swallow the next real ConfigLib or disk reload instead.
        /// </summary>
        private int _localEditDepth;

        /// <summary>Set while the dialog itself moves the scrollbar, so its echo is not taken as a user drag.</summary>
        private bool _syncingScrollbar;

        /// <summary>The live widgets of each composed setting row, by setting code.</summary>
        private readonly Dictionary<string, RowWidgets> _rows = new Dictionary<string, RowWidgets>(StringComparer.Ordinal);

        /// <summary>Dropdown keys in the current composer, for wheel ownership.</summary>
        private readonly List<string> _dropdownKeys = new List<string>();

        /// <summary>Which rows, in which states, the current composer was built for.</summary>
        private string _composedLayout;

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

            if (_localEditDepth > 0)
            {
                // Our own edit, reported synchronously. The edit path updates
                // the rows itself once the controller returns.
                Log("EXIT RefreshFromConfig inside local edit");
                return;
            }

            if (CanRefreshInPlace())
            {
                RefreshRows();
                Log("EXIT RefreshFromConfig updated rows in place");
                return;
            }

            ComposeDialog();
            Log("EXIT RefreshFromConfig recomposed");
        }

        /// <summary>
        /// The one wheel handler for the dialog.
        ///
        /// The native path is NOT called first. GuiComposer.OnMouseWheel offers
        /// the event to every interactive element whether or not the pointer is
        /// over it, so the scrollbar scrolled the list from anywhere and then
        /// this method scrolled it again; a slider under the pointer, even one
        /// clipped out of view, took the wheel as a value change instead.
        ///
        /// So: an open dropdown owns the wheel outright. Otherwise, inside the
        /// settings viewport or over its scrollbar, the wheel scrolls the list -
        /// over labels, gaps and idle controls alike - and is consumed even when
        /// the list cannot move, so it never falls through to a control. Elsewhere
        /// inside the dialog it is consumed and does nothing. Outside the dialog
        /// it is left for the game.
        /// </summary>
        public override void OnMouseWheel(MouseWheelEventArgs args)
        {
            if (SingleComposer == null) return;

            GuiElementDropDown open = OpenDropDown();
            if (open != null)
            {
                open.listMenu.OnMouseWheel(capi, args);
                args.SetHandled();
                return;
            }

            int mouseX = capi.Input.MouseX;
            int mouseY = capi.Input.MouseY;
            if (!SingleComposer.Bounds.PointInside(mouseX, mouseY)) return;

            if (_scrollContentBounds != null && PointerInScrollRegion(mouseX, mouseY))
            {
                double delta = args.deltaPrecise != 0 ? args.deltaPrecise : args.delta;
                if (Math.Abs(delta) > 0.001 && MaxScroll() > 0) ScrollTo(_currentScroll - delta * WheelStep);
            }

            args.SetHandled();
        }

        private bool PointerInScrollRegion(int mouseX, int mouseY)
        {
            return (_viewportBounds != null && _viewportBounds.PointInside(mouseX, mouseY)) ||
                   (_scrollbarBounds != null && _scrollbarBounds.PointInside(mouseX, mouseY));
        }

        private GuiElementDropDown OpenDropDown()
        {
            foreach (string key in _dropdownKeys)
            {
                GuiElementDropDown dropdown = SingleComposer.GetDropDown(key);
                if (dropdown != null && dropdown.listMenu != null && dropdown.listMenu.IsOpened) return dropdown;
            }
            return null;
        }

        public override void Dispose()
        {
            Log("ENTER Dispose");
            base.Dispose();
            Log("EXIT Dispose");
        }

        private void ComposeDialog()
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
            _scrollContentBounds = null;
            _viewportBounds = null;
            _scrollbarBounds = null;
            _rows.Clear();
            _dropdownKeys.Clear();
            _composedLayout = LayoutSignature();

            SingleComposer = capi.Gui.CreateCompo("vintagevisuals-tuning-studio", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Vintage Visuals", OnTitleBarClose)
                .BeginChildElements(bgBounds)
                    .AddStaticText(PageTitle(), CairoFont.WhiteMediumText(), ElementBounds.Fixed(ContentLeft, HeaderY, 380, 26))
                    .AddInset(ElementBounds.Fixed(DialogPadding, 42, SidebarWidth, 506), 2)
                    .AddInset(ElementBounds.Fixed(ContentLeft - 10, BodyY - 8, ContentWidth + 20, BodyHeight + 16), 2)
                    .AddSwitch(on => Guard(generation, "AdvancedChanged", () => OnAdvancedChanged(on)),
                        ElementBounds.Fixed(ContentLeft, FooterY + 2, 30, 30), AdvancedSwitchKey, 24)
                    .AddStaticText("Show advanced and unavailable settings", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(ContentLeft + 36, FooterY + 4, 320, 24))
                    .AddButton("Reset Tab", () => Guard(generation, "ResetTab", OnResetTabClicked), ElementBounds.Fixed(DialogWidth - 136, 548, 96, 28),
                        CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "reset-tab");

            AddTabs(generation);

            if (_tab == SettingTab.Overview) AddOverview();
            else if (_tab == SettingTab.Debug) AddDebug();
            else AddSettingsTab(_tab);

            SingleComposer.EndChildElements().Compose();

            GuiElementSwitch advanced = SingleComposer.GetSwitch(AdvancedSwitchKey);
            if (advanced != null) advanced.On = _advanced;

            ConfigureScrollbar();

            // Reset visibility and every other live state come from one place,
            // the same one a later config change goes through.
            RefreshRows();
            Log("EXIT ComposeDialog generation " + generation);
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
            ComposeDialog();
            Log("EXIT SelectTab " + tab);
        }

        private bool OnAdvancedChanged(bool on)
        {
            if (_advanced == on) return true;

            SaveScroll();
            _advanced = on;
            ComposeDialog();
            return true;
        }

        private bool OnResetTabClicked()
        {
            Log("ENTER ResetTab");
            LocalEdit(() => _controller.ResetTab(_tab, _advanced));
            ComposeDialog();
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
                "Surface Relief " + ValueByCode("pbr_normalstrength") + " · Reflections " + StateByCode("reflections_scene"),
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
                "Scene reflections " + StateByCode("reflections_scene") + " · Pixel strength " + StateByCode("pbr_pixelreflect"),
                "Open screen-space reflections, edge fading, stride and debug controls.", y);
        }

        private void AddOverviewRow(string label, string path, SettingTab target, string summary, string description, double y)
        {
            bool on = ConfigAccess.Get(_controller.Config, path) >= 0.5f;
            VisualSetting master = VisualSettingRegistry.All.FirstOrDefault(v => v.Path == path);
            bool unavailable = master != null && Availability(master).State == AvailabilityState.Unavailable;
            int generation = _composerGeneration;

            SingleComposer
                .AddInset(ElementBounds.Fixed(ContentLeft, y, ContentWidth - 34, 68), 2, 0.42f)
                .AddStaticText(label, CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentLeft + 14, y + 8, 230, 20))
                .AddStaticText(unavailable ? "Not in this build" : on ? "● On" : "○ Off", CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentLeft + 256, y + 8, 160, 20))
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

        /// <summary>The value, or "not in this build" when nothing reads it - never a live-looking value for a dead control.</summary>
        private string StateByCode(string code)
        {
            VisualSetting setting = VisualSettingRegistry.ByCode(code);
            if (setting == null) return "n/a";
            if (Availability(setting).State == AvailabilityState.Unavailable) return "not in this build";
            return _controller.DisplayValue(setting);
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

        /// <summary>
        /// The settings a tab shows. Rows whose code nothing in this build reads
        /// are left out of normal tuning and shown, disabled with the reason,
        /// only under Advanced.
        /// </summary>
        private IEnumerable<VisualSetting> ShownSettings(SettingTab tab)
        {
            return _controller.VisibleSettings(tab, _advanced)
                .Where(s => _advanced || Availability(s).State != AvailabilityState.Unavailable);
        }

        private IEnumerable<RowItem> RowsFor(SettingTab tab)
        {
            string currentSection = null;
            foreach (VisualSetting setting in ShownSettings(tab))
            {
                if (!string.Equals(currentSection, setting.Section, StringComparison.Ordinal))
                {
                    currentSection = setting.Section;
                    yield return RowItem.ForSection(currentSection);
                }
                yield return RowItem.ForSetting(setting);
            }
        }

        private SettingAvailability Availability(VisualSetting setting)
        {
            return SettingAvailabilityRules.Evaluate(setting, _controller.Config);
        }

        /// <summary>
        /// Everything that decides which rows exist and which widgets they hold.
        /// While it is unchanged, a config change can be shown by updating the
        /// existing widgets; when it changes, the dialog has to be recomposed.
        /// </summary>
        private string LayoutSignature()
        {
            var text = new System.Text.StringBuilder();
            text.Append((int)_tab).Append(_advanced ? 'A' : '-');
            if (_tab == SettingTab.Overview || _tab == SettingTab.Debug) return text.ToString();

            foreach (VisualSetting setting in ShownSettings(_tab))
            {
                text.Append('|').Append(setting.Code).Append(':').Append((int)Availability(setting).State);
            }
            return text.ToString();
        }

        private bool CanRefreshInPlace()
        {
            if (SingleComposer == null) return false;
            if (_tab == SettingTab.Overview || _tab == SettingTab.Debug) return false;
            return string.Equals(_composedLayout, LayoutSignature(), StringComparison.Ordinal);
        }

        private void AddScrollableRows(List<RowItem> rows, double bodyY, double bodyHeight)
        {
            _currentBodyHeight = bodyHeight;

            ElementBounds bodyBounds = ElementBounds.Fixed(ContentLeft, bodyY, ContentWidth, bodyHeight);
            ElementBounds clipBounds = ElementBounds.Fixed(0, 0, ContentWidth, bodyHeight);
            ElementBounds scrollbarBounds = ElementBounds.Fixed(ContentLeft + ContentWidth + 6, bodyY, 16, bodyHeight);
            _currentContentHeight = MeasureRows(rows);
            _currentScroll = ClampScroll(_currentScroll);
            SaveScroll();
            _currentBodyY = bodyY;
            _viewportBounds = bodyBounds;
            _scrollbarBounds = scrollbarBounds;
            _scrollContentBounds = ElementBounds.Fixed(0, -_currentScroll, ContentWidth,
                Math.Max(bodyHeight, _currentContentHeight));

            SingleComposer.BeginChildElements(bodyBounds);
            SingleComposer.BeginClip(clipBounds);
            SingleComposer.BeginChildElements(_scrollContentBounds);

            double y = 0;
            foreach (RowItem row in rows)
            {
                if (row.IsSection)
                {
                    AddSection(row.Section, y);
                    y += HeaderHeight;
                }
                else
                {
                    AddSettingRow(row.Setting, y);
                    y += RowHeight;
                }
            }

            SingleComposer.EndChildElements();
            SingleComposer.EndClip();
            SingleComposer.EndChildElements();

            if (_currentContentHeight > bodyHeight)
            {
                SingleComposer.AddVerticalScrollbar(OnScrollbarChanged, scrollbarBounds, ScrollbarKey);
            }
        }

        // EVERYTHING INSIDE THE SCROLL CHILD IS AN INTERACTIVE ELEMENT.
        //
        // A static element (AddStaticText, AddInset) is painted once into the
        // composer's static texture at compose time. Moving the scroll child's
        // bounds afterwards moves the interactive elements and leaves every
        // static one where it was painted - and the clip is a scissor applied at
        // interactive render time, so static rows below the viewport were
        // painted over the footer. Dynamic text renders from its bounds every
        // frame, inside the scissor, so it scrolls and clips with its row.

        private void AddSection(string label, double y)
        {
            SingleComposer.AddDynamicText(label, SectionFont(), ElementBounds.Fixed(RowLeft, y + 4, ContentWidth - 20, 20),
                "section-" + y.ToString("0", CultureInfo.InvariantCulture));
        }

        private void AddSettingRow(VisualSetting setting, double y)
        {
            SettingAvailability availability = Availability(setting);
            bool working = availability.HasWorkingControl;
            var widgets = new RowWidgets(setting, availability.State);
            _rows[setting.Code] = widgets;

            CairoFont nameFont = working ? CairoFont.WhiteSmallText() : DisabledFont(CairoFont.WhiteSmallText());
            CairoFont infoFont = availability.State == AvailabilityState.Conditional
                ? CairoFont.WhiteDetailText().WithColor(GuiStyle.ActiveButtonTextColor)
                : working ? CairoFont.WhiteDetailText() : DisabledFont(CairoFont.WhiteDetailText());

            string info = availability.State == AvailabilityState.Conditional
                ? ShortText(availability.Reason, InfoChars)
                : ShortDescription(setting);

            SingleComposer
                .AddDynamicText(setting.DisplayName, nameFont, ElementBounds.Fixed(LabelX, y + LineOneY, LabelWidth, 22),
                    "name-" + setting.Code)
                .AddDynamicText(info, infoFont, ElementBounds.Fixed(InfoX, y + LineOneY + 3, InfoTextWidth, 18),
                    "info-" + setting.Code);

            // Reset always exists and always sits in the same slot; it is shown
            // only while the row differs from its default. Unavailable rows get
            // none, since resetting a value nothing reads is not an action.
            if (availability.State != AvailabilityState.Unavailable)
            {
                SingleComposer.AddButton("Reset", () => ResetSetting(setting), ElementBounds.Fixed(ResetX, y + LineTwoY, ResetWidth, 24),
                    CairoFont.WhiteSmallText(), EnumButtonStyle.Small, widgets.ResetKey);
                widgets.HasReset = true;
            }

            if (!working)
            {
                // No control at all: a disabled-looking slider that still moves
                // is exactly the thing this row is trying not to be.
                SingleComposer
                    .AddDynamicText(availability.Reason, DisabledFont(CairoFont.WhiteDetailText()),
                        ElementBounds.Fixed(ControlX, y + LineTwoY + 4, ControlWidth, 18), "reason-" + setting.Code)
                    .AddDynamicText(RowValueText(setting), DisabledFont(CairoFont.WhiteSmallText()),
                        ElementBounds.Fixed(ValueX, y + LineTwoY + 2, ValueWidth, 22), widgets.ValueKey);
                widgets.HasValue = true;
            }
            else if (setting.Kind == SettingKind.Toggle) AddToggle(setting, widgets, y);
            else if (setting.Kind == SettingKind.Dropdown) AddDropdown(setting, widgets, y);
            else AddSlider(setting, widgets, y);
        }

        private static CairoFont DisabledFont(CairoFont font)
        {
            return font.WithColor(GuiStyle.DisabledTextColor);
        }

        private static CairoFont SectionFont()
        {
            return CairoFont.WhiteSmallText().WithColor(GuiStyle.ActiveButtonTextColor);
        }

        /// <summary>The value column, read from the config as it is NOW - after clamping.</summary>
        private string RowValueText(VisualSetting setting)
        {
            if (setting.Kind == SettingKind.Toggle) return _controller.Value(setting) >= 0.5f ? "ON" : "OFF";
            return _controller.DisplayValue(setting);
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

        private void AddToggle(VisualSetting setting, RowWidgets widgets, double y)
        {
            bool on = _controller.Value(setting) >= 0.5f;
            int generation = _composerGeneration;

            SingleComposer.AddSwitch(value =>
            {
                if (!IsCurrentGeneration(generation, "ToggleChanged " + setting.Code)) return;
                Log("ENTER ToggleChanged " + setting.Code);
                LocalEdit(() => _controller.Set(setting, value ? 1f : 0f));
                AfterLocalEdit(true);
                Log("EXIT ToggleChanged " + setting.Code);
            }, ElementBounds.Fixed(ControlX, y + LineTwoY - 2, 30, 30), widgets.ControlKey, 24);

            // The toggle's ON/OFF lives in the value column like every other
            // row's value, so there is one place to read state from.
            SingleComposer.AddDynamicText(on ? "ON" : "OFF", CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(ValueX, y + LineTwoY + 2, ValueWidth, 22), widgets.ValueKey);
            widgets.HasValue = true;

            GuiElementSwitch sw = SingleComposer.GetSwitch(widgets.ControlKey);
            if (sw != null) sw.On = on;
        }

        private void AddSlider(VisualSetting setting, RowWidgets widgets, double y)
        {
            int generation = _composerGeneration;
            int steps = SliderSteps(setting);

            SingleComposer.AddSlider(newValue =>
            {
                if (!IsCurrentGeneration(generation, "SliderChanged " + setting.Code)) return false;
                Log("ENTER SliderChanged " + setting.Code);
                float target = setting.Min + newValue * setting.Step;
                LocalEdit(() => _controller.Set(setting, target));

                // Never recompose from a slider: the composer must outlive the
                // drag. No slider is the parent of another row, so a slider
                // edit cannot change which rows exist.
                AfterLocalEdit(false);
                Log("EXIT SliderChanged " + setting.Code);
                return true;
            }, ElementBounds.Fixed(ControlX, y + LineTwoY, ControlWidth, 24), widgets.ControlKey);

            SingleComposer.AddDynamicText(_controller.DisplayValue(setting), CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(ValueX, y + LineTwoY + 2, ValueWidth, 22), widgets.ValueKey);
            widgets.HasValue = true;

            GuiElementSlider slider = SingleComposer.GetSlider(widgets.ControlKey);
            if (slider != null)
            {
                slider.SetValues(SliderIndex(setting), 0, steps, 1, "");

                // The slider counts in steps, not in the setting's units. Left to
                // itself its tooltip would print the step index.
                slider.OnSliderTooltip = index => setting.Format(setting.Min + index * setting.Step);
            }
        }

        private static int SliderSteps(VisualSetting setting)
        {
            return Math.Max(1, (int)Math.Round((setting.Max - setting.Min) / setting.Step));
        }

        private int SliderIndex(VisualSetting setting)
        {
            int index = (int)Math.Round((_controller.Value(setting) - setting.Min) / setting.Step);
            return Math.Max(0, Math.Min(SliderSteps(setting), index));
        }

        private void AddDropdown(VisualSetting setting, RowWidgets widgets, double y)
        {
            int generation = _composerGeneration;

            SingleComposer.AddDropDown(setting.Choices, setting.Choices, DropdownIndex(setting),
                (code, selectedValue) =>
                {
                    if (!selectedValue) return;
                    if (!IsCurrentGeneration(generation, "DropdownChanged " + setting.Code)) return;
                    Log("ENTER DropdownChanged " + setting.Code);
                    LocalEdit(() => _controller.SetString(setting, code));
                    AfterLocalEdit(true);
                    Log("EXIT DropdownChanged " + setting.Code);
                },
                ElementBounds.Fixed(ControlX, y + LineTwoY, Math.Min(ControlWidth, 240), 24), widgets.ControlKey);
            _dropdownKeys.Add(widgets.ControlKey);
        }

        private int DropdownIndex(VisualSetting setting)
        {
            string current = _controller.StringValue(setting);
            int selected = Array.FindIndex(setting.Choices, c => string.Equals(c, current, StringComparison.Ordinal));
            return selected < 0 ? 0 : selected;
        }

        private static string ShortDescription(VisualSetting setting)
        {
            return ShortText(setting.Description, InfoChars);
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
            if (!_controller.IsModified(setting))
            {
                // A Reset that is hidden can still hold keyboard focus.
                Log("EXIT ResetSetting unmodified " + setting.Code);
                return true;
            }

            LocalEdit(() => _controller.Reset(setting));
            AfterLocalEdit(true);
            Log("EXIT ResetSetting " + setting.Code);
            return true;
        }

        /// <summary>
        /// Runs one controller mutation with this dialog marked as its source.
        ///
        /// The scope is the call itself, not "the next notification": whatever
        /// the controller decides - write, clamp to the same value, reject - the
        /// counter is back to zero when it returns, so nothing stays armed to
        /// swallow a later external change.
        /// </summary>
        private void LocalEdit(Action edit)
        {
            _localEditDepth++;
            try
            {
                edit();
            }
            finally
            {
                _localEditDepth--;
            }
        }

        private void LocalEdit(Func<bool> edit)
        {
            LocalEdit(() => { edit(); });
        }

        private void LocalEdit(Func<int> edit)
        {
            LocalEdit(() => { edit(); });
        }

        /// <summary>
        /// Shows the result of a local edit. The rows are re-read from the
        /// config, so they show the value that survived clamping rather than the
        /// one the widget asked for. If the edit changed which rows exist - a
        /// master switch, a reset of one - the dialog is recomposed, unless the
        /// caller is a slider mid-drag.
        /// </summary>
        private void AfterLocalEdit(bool mayRecompose)
        {
            if (mayRecompose && !CanRefreshInPlace())
            {
                SaveScroll();
                ComposeDialog();
                return;
            }

            RefreshRows();
        }

        /// <summary>
        /// Updates every composed row from the effective config value: the
        /// slider thumb, the value text, the toggle state and label, the
        /// dropdown selection and whether Reset is shown, together.
        /// </summary>
        private void RefreshRows()
        {
            if (SingleComposer == null) return;

            foreach (RowWidgets widgets in _rows.Values)
            {
                UpdateRow(widgets);
            }
        }

        private void UpdateRow(RowWidgets widgets)
        {
            VisualSetting setting = widgets.Setting;

            if (widgets.HasValue)
            {
                GuiElementDynamicText value = SingleComposer.GetDynamicText(widgets.ValueKey);
                if (value != null) value.SetNewText(RowValueText(setting));
            }

            if (widgets.HasReset)
            {
                GuiElementTextButton reset = SingleComposer.GetButton(widgets.ResetKey);
                if (reset != null)
                {
                    bool modified = _controller.IsModified(setting);
                    reset.Visible = modified;
                    reset.Enabled = modified;
                }
            }

            if (widgets.State != AvailabilityState.Active && widgets.State != AvailabilityState.Conditional) return;

            if (setting.Kind == SettingKind.Slider)
            {
                GuiElementSlider slider = SingleComposer.GetSlider(widgets.ControlKey);
                int index = SliderIndex(setting);

                // Only when they disagree: during a drag the thumb already sits
                // where the config does, and re-seating it would fight the mouse.
                if (slider != null && slider.GetValue() != index) slider.SetValues(index, 0, SliderSteps(setting), 1, "");
            }
            else if (setting.Kind == SettingKind.Toggle)
            {
                GuiElementSwitch sw = SingleComposer.GetSwitch(widgets.ControlKey);
                if (sw != null) sw.On = _controller.Value(setting) >= 0.5f;
            }
            else if (setting.Kind == SettingKind.Dropdown)
            {
                GuiElementDropDown dropdown = SingleComposer.GetDropDown(widgets.ControlKey);
                int index = DropdownIndex(setting);
                if (dropdown != null && dropdown.listMenu != null && dropdown.listMenu.SelectedIndex != index)
                {
                    dropdown.SetSelectedIndex(index);
                }
            }
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
            _dropdownKeys.Add("debug-system");

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
            _dropdownKeys.Add("debug-view");

            y += 46;
            SingleComposer.AddStaticText(ShortText(current.Description, 92), CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(ContentLeft, y - 12, ContentWidth - 40, 18));
            if (PbrTerrainDebugLimited())
            {
                SingleComposer.AddStaticText("Safe terrain path: material and secondary atlas masks are available.",
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(ContentLeft, y + 10, ContentWidth - 40, 18));
                y += 28;
            }

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
            ComposeDialog();
            Log("EXIT DebugSystemChanged");
        }

        private void OnDebugViewChanged(string code, bool selected)
        {
            if (!selected) return;
            Log("ENTER DebugViewChanged");
            int requested;
            if (int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out requested) &&
                !IsDebugViewAvailable(requested))
            {
                Log("EXIT DebugViewChanged blocked unavailable terrain debug view " + requested);
                ComposeDialog();
                return;
            }

            DebugView view = CurrentDebugViews().FirstOrDefault(v => v.Value.ToString() == code);
            LocalEdit(() => _controller.SetDebugView(view));

            // The active-view banner and its Return button depend on the view,
            // so the page is rebuilt rather than left describing the old one.
            ComposeDialog();
            Log("EXIT DebugViewChanged");
        }

        private bool PbrTerrainDebugLimited()
        {
            return _debugOwnerPath == DebugViewRegistry.PbrPath &&
                   !PseudoPbrSubsystem.TerrainShaderPatchesEnabled;
        }

        private IEnumerable<DebugView> CurrentDebugViews()
        {
            DebugSystem system = _debugSystems[_debugSystemIndex];
            return DebugViewRegistry.For(system.OwnerPath)
                .Where(system.Include)
                .Where(v => system.OwnerPath != DebugViewRegistry.PbrPath || IsDebugViewAvailable(v.Value));
        }

        private bool IsDebugViewAvailable(int value)
        {
            if (_debugOwnerPath != DebugViewRegistry.PbrPath) return true;
            if (FullTerrainDebugAvailable()) return true;
            if (!PrimaryTerrainMaterialDebugAvailable()) return value == 0;

            return value == 0 || value == 1 || value == 2 || value == 3 ||
                   value == 4 || value == 5 || value == 6 || value == 7 ||
                   value == 8 || value == 10 || value == 12 || value == 14 ||
                   value == 19 || value == 20 || value == 21 || value == 22 ||
                   value == 23 || value == 33 || value == 52;
        }

        private static bool FullTerrainDebugAvailable()
        {
            return PseudoPbrSubsystem.TerrainShaderPatchesEnabled;
        }

        private static bool PrimaryTerrainMaterialDebugAvailable()
        {
            return PseudoPbrSubsystem.TerrainMaterialPrimaryPatchEnabled;
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
            LocalEdit(() => _controller.ReturnToNormalRendering());
            ComposeDialog();
            Log("EXIT ReturnToNormal");
            return true;
        }

        private double MeasureRows(List<RowItem> rows)
        {
            return rows.Sum(row => row.IsSection ? HeaderHeight : RowHeight);
        }

        private void OnScrollbarChanged(float value)
        {
            // SetHeights fires this callback with the handle's OLD position, so
            // while the dialog itself is moving the scrollbar the echo is ignored
            // rather than allowed to drag the list back.
            if (_syncingScrollbar) return;

            Log("ENTER ScrollbarChanged");
            _currentScroll = ClampScroll(value);
            SaveScroll();
            MoveScrollContent();
            Log("EXIT ScrollbarChanged");
        }

        private void ScrollTo(double value)
        {
            Log("ENTER MouseWheelScroll");
            _currentScroll = ClampScroll(value);
            SaveScroll();
            MoveScrollContent();
            ConfigureScrollbar();
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

        private double MaxScroll()
        {
            return Math.Max(0, _currentContentHeight - _currentBodyHeight);
        }

        private double ClampScroll(double value)
        {
            return Math.Max(0, Math.Min(MaxScroll(), value));
        }

        /// <summary>
        /// Puts the scrollbar handle where the list is.
        ///
        /// Through CurrentYPosition, which takes content units. The previous
        /// SetScrollbarPosition takes HANDLE PIXELS and then reports a scroll of
        /// zero to the callback, so every wheel tick and every recompose sent the
        /// list back to the top while the handle went somewhere else.
        /// </summary>
        private void ConfigureScrollbar()
        {
            GuiElementScrollbar scrollbar = SingleComposer.GetScrollbar(ScrollbarKey);
            if (scrollbar == null) return;

            _syncingScrollbar = true;
            try
            {
                scrollbar.SetHeights((float)_currentBodyHeight, (float)Math.Max(_currentBodyHeight, _currentContentHeight));
                scrollbar.CurrentYPosition = (float)_currentScroll;
            }
            finally
            {
                _syncingScrollbar = false;
            }
        }

        private void MoveScrollContent()
        {
            if (_scrollContentBounds == null) return;

            _scrollContentBounds.fixedY = -_currentScroll;
            _scrollContentBounds.CalcWorldBounds();
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

        private void Log(string message)
        {
            int sequence = Interlocked.Increment(ref _eventSequence);
            capi.Logger.Notification("[VV UI #" + sequence + "] " + message +
                " tab=" + _tab +
                " composerGen=" + _composerGeneration +
                " thread=" + Thread.CurrentThread.ManagedThreadId +
                " composerNull=" + (SingleComposer == null) +
                " opened=" + IsOpened() +
                " scroll=" + _currentScroll.ToString("0.##", CultureInfo.InvariantCulture));
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

        /// <summary>The keys of one composed row's live widgets, so a config change can update them in place.</summary>
        private sealed class RowWidgets
        {
            public readonly VisualSetting Setting;
            public readonly AvailabilityState State;
            public readonly string ValueKey;
            public readonly string ResetKey;
            public readonly string ControlKey;
            public bool HasValue;
            public bool HasReset;

            public RowWidgets(VisualSetting setting, AvailabilityState state)
            {
                Setting = setting;
                State = state;
                ValueKey = "value-" + setting.Code;
                ResetKey = "reset-" + setting.Code;
                ControlKey = (setting.Kind == SettingKind.Toggle ? "switch-" :
                              setting.Kind == SettingKind.Dropdown ? "dropdown-" : "slider-") + setting.Code;
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
