using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VintageVisuals.Common.Diagnostics;
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

        // THE PANEL SIZES ITSELF TO THE SCREEN. The fixed 860x610 panel showed
        // about six rows at 1440p (in-game audit, 2026-11-27). The size is
        // taken from the frame in GUI units at compose time, between these
        // bounds, and every column below derives from it.
        private const double MinDialogWidth = 860;
        private const double MaxDialogWidth = 1200;
        private const double MinDialogHeight = 610;
        private const double MaxDialogHeight = 1000;

        private const double DialogPadding = 10;
        private const double SidebarWidth = 196;
        private const double SidebarPadding = 18;
        private const double ContentGap = 24;
        private const double ContentLeft = DialogPadding + SidebarWidth + ContentGap;
        private const double ContentRightPadding = 30;
        private const double HeaderY = 44;
        private const double BodyY = 88;
        private const double FooterGap = 62;
        private const double DetailsHeight = 56;
        private const double TabTop = 58;
        private const double TabStep = 32;
        private const double SearchWidth = 230;

        private double DialogWidth = MinDialogWidth;
        private double DialogHeight = MinDialogHeight;
        private double ContentWidth { get { return DialogWidth - ContentLeft - ContentRightPadding - 10; } }
        private double FooterY { get { return DialogHeight - FooterGap; } }
        private double DetailsY { get { return FooterY - DetailsHeight - 8; } }
        private double BodyHeight { get { return DetailsY - BodyY - 14; } }

        // Rows are two lines. The first names the setting and gives the first
        // sentence of its description; the second gives the control most of the
        // width, with the current value beside it and Reset in a fixed slot
        // after that, so neither moves when a row becomes modified. The whole
        // description is in the details strip under the list.
        private const double RowLeft = 0;
        private const double RowHeight = 54;
        private const double HeaderHeight = 28;
        private const double NoteHeight = 22;
        private const double ActionHeight = 38;
        private const double RowGap = 10;
        private const double RowRightPadding = 8;
        private const double LabelWidth = 200;
        private const double InfoWidth = 8;
        private const double ValueWidth = 86;
        private const double ResetWidth = 54;
        private const double LabelX = RowLeft + 18;
        private const double InfoX = LabelX + LabelWidth + InfoWidth;
        private double InfoTextWidth { get { return ContentWidth - InfoX - RowRightPadding; } }
        private const double ControlX = LabelX;
        private double ControlWidth { get { return ContentWidth - ControlX - ValueWidth - ResetWidth - 2 * RowGap - RowRightPadding; } }
        private double ValueX { get { return ControlX + ControlWidth + RowGap; } }
        private double ResetX { get { return ValueX + ValueWidth + RowGap; } }
        private const double LineOneY = 4;
        private const double LineTwoY = 26;

        /// <summary>Characters of detail text that fit in a width, from the font's average advance.</summary>
        private static int CharsFor(double width) { return Math.Max(24, (int)(width / 6.3)); }
        private int InfoChars { get { return CharsFor(InfoTextWidth); } }

        /// <summary>
        /// One wheel notch scrolls a row and a half. 48 was under one row, so the
        /// list crawled (in-game audit, 2026-11-27).
        /// </summary>
        private const double WheelStep = 84;

        private const string ScrollbarKey = "settings-scrollbar";
        private const string AdvancedSwitchKey = "advanced-switch";
        private const string RowsContainerKey = "settings-rows";
        private const string SearchKey = "settings-search";
        private const string DetailsKey = "settings-details";
        private const string NotInThisBuild = "Not in this build";

        // Three text colours with three meanings. Orange used to mean both "where
        // to look" and "this is off", and section headings were orange too, so
        // nothing stood out (in-game audit, 2026-11-27).
        private static readonly double[] HeadingColor = { 0.91, 0.80, 0.58, 1.0 };
        private static readonly double[] HintColor = { 0.62, 0.80, 0.96, 1.0 };
        private static readonly double[] WarningColor = { 1.0, 0.66, 0.34, 1.0 };

        private readonly VisualTuningStudioController _controller;
        private readonly SavedLooks _looks;
        private readonly Dictionary<SettingTab, double> _scrollByTab = new Dictionary<SettingTab, double>();

        private readonly GuiTab[] _tabs =
        {
            new GuiTab { DataInt = (int)SettingTab.Overview, Name = "Overview" },
            new GuiTab { DataInt = (int)SettingTab.Color, Name = "Colour" },
            new GuiTab { DataInt = (int)SettingTab.Light, Name = "Light" },
            new GuiTab { DataInt = (int)SettingTab.Materials, Name = "Surfaces" },
            new GuiTab { DataInt = (int)SettingTab.Nature, Name = "Nature" },
            new GuiTab { DataInt = (int)SettingTab.Weather, Name = "Weather" },
            new GuiTab { DataInt = (int)SettingTab.Atmosphere, Name = "Atmosphere" },
            new GuiTab { DataInt = (int)SettingTab.Water, Name = "Water" },
            new GuiTab { DataInt = (int)SettingTab.Reflections, Name = "Reflections" },
            new GuiTab { DataInt = (int)SettingTab.Performance, Name = "Performance" },
            new GuiTab { DataInt = (int)SettingTab.Debug, Name = "Debug" },
        };

        private readonly DebugSystem[] _debugSystems =
        {
            new DebugSystem("Surfaces", DebugViewRegistry.PbrPath,
                v => v.Group != "Pixel reflection" && v.Group != "Scene reflection bridge" && v.Group != "Reflection march"),
            new DebugSystem("Reflections", DebugViewRegistry.PbrPath,
                v => v.Value == 0 || v.Group == "Pixel reflection" || v.Group == "Scene reflection bridge" || v.Group == "Reflection march"),
            new DebugSystem("Water", DebugViewRegistry.WaterPath, v => true),
            new DebugSystem("Creatures", DebugViewRegistry.EntityPath, v => true),
            new DebugSystem("Cloud shadows", DebugViewRegistry.CloudPath, v => true),
            new DebugSystem("Atmosphere", DebugViewRegistry.AtmospherePath, v => true),
        };

        private SettingTab _tab = SettingTab.Overview;
        private bool _advanced;
        private string _debugOwnerPath = DebugViewRegistry.PbrPath;
        private int _debugSystemIndex = 0;
        private double _currentScroll;
        private double _currentContentHeight;
        private double _currentBodyHeight = 400;
        private double _currentBodyY = BodyY;
        private ElementBounds _scrollContentBounds;
        private ElementBounds _viewportBounds;
        private ElementBounds _scrollbarBounds;

        /// <summary>Text in the search box. Non-empty means the list shows matches from every tab.</summary>
        private string _search = "";
        private bool _restoringSearch;
        private bool _searchComposeQueued;

        /// <summary>The Saved looks slot the Save and Load buttons act on, 0-based.</summary>
        private int _lookSlot;

        /// <summary>One line of feedback for the details strip after an action (a look saved, a preset applied).</summary>
        private string _status;

        /// <summary>The setting the pointer is over, whose full text the details strip shows.</summary>
        private VisualSetting _hovered;

        /// <summary>Where each composed setting row sits in the scroll content, for the details strip.</summary>
        private readonly List<RowSpan> _rowSpans = new List<RowSpan>();

        /// <summary>
        /// The native scroll container every row element lives in. Not on the
        /// composer: the container paints its children's static parts (slider
        /// tracks, switch backgrounds, dropdown boxes, button faces) into its OWN
        /// texture and draws it at its bounds inside the clip, so they move and
        /// clip with the rows. Elements added to the composer directly are
        /// painted into the composer's static texture once and stay there.
        /// </summary>
        private GuiElementContainer _rowsContainer;
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
            _looks = new SavedLooks(capi);
            _advanced = false;
            ComposeDialog();
        }

        public override string ToggleKeyCombinationCode { get { return HotkeyCode; } }
        public override EnumDialogType DialogType { get { return EnumDialogType.Dialog; } }

        public override void OnGuiOpened()
        {
            Log("ENTER OnGuiOpened");
            base.OnGuiOpened();
            // The window may have been resized since the panel was composed.
            if (MeasureDialog()) ComposeDialog();
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
            // A bounds is only usable once it is attached to the composed tree:
            // PointInside walks ParentBounds, and a bounds for a scrollbar that
            // was never added (the list fits) has none.
            return IsAttachedAndInside(_viewportBounds, mouseX, mouseY) ||
                   IsAttachedAndInside(_scrollbarBounds, mouseX, mouseY);
        }

        private static bool IsAttachedAndInside(ElementBounds bounds, int mouseX, int mouseY)
        {
            return bounds != null && bounds.ParentBounds != null && bounds.PointInside(mouseX, mouseY);
        }

        /// <summary>
        /// Tracks which row the pointer is over, for the details strip. Read-only:
        /// it changes one text element and nothing else.
        /// </summary>
        public override void OnMouseMove(MouseEvent args)
        {
            base.OnMouseMove(args);
            if (SingleComposer == null) return;

            VisualSetting over = null;
            int mouseX = capi.Input.MouseX;
            int mouseY = capi.Input.MouseY;
            if (_rowSpans.Count > 0 && IsAttachedAndInside(_viewportBounds, mouseX, mouseY))
            {
                double scale = Math.Max(0.01, GuiElement.scaled(1));
                double contentY = (mouseY - _viewportBounds.absY) / scale + _currentScroll;
                foreach (RowSpan span in _rowSpans)
                {
                    if (contentY >= span.Top && contentY < span.Top + span.Height) { over = span.Setting; break; }
                }
            }

            if (over == _hovered) return;
            _hovered = over;
            if (over != null) _status = null;
            UpdateDetails();
        }

        private GuiElementDropDown OpenDropDown()
        {
            foreach (string key in _dropdownKeys)
            {
                GuiElementDropDown dropdown = SingleComposer.GetDropDown(key);
                if (dropdown != null && dropdown.listMenu != null && dropdown.listMenu.IsOpened) return dropdown;
            }
            foreach (RowWidgets widgets in _rows.Values)
            {
                GuiElementDropDown dropdown = widgets.Dropdown;
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

        /// <summary>Sizes the panel to the current frame. True when the size changed.</summary>
        private bool MeasureDialog()
        {
            double scale = Math.Max(0.25, GuiElement.scaled(1));
            double screenW = capi.Render.FrameWidth / scale;
            double screenH = capi.Render.FrameHeight / scale;

            double width = Clamp(Math.Round(screenW * 0.62), MinDialogWidth, Math.Max(MinDialogWidth, Math.Min(MaxDialogWidth, screenW - 40)));
            double height = Clamp(Math.Round(screenH * 0.86), MinDialogHeight, Math.Max(MinDialogHeight, Math.Min(MaxDialogHeight, screenH - 40)));

            bool changed = Math.Abs(width - DialogWidth) > 0.5 || Math.Abs(height - DialogHeight) > 0.5;
            DialogWidth = width;
            DialogHeight = height;
            return changed;
        }

        private static double Clamp(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }

        private bool Searching { get { return !string.IsNullOrWhiteSpace(_search); } }

        private void ComposeDialog()
        {
            _composerGeneration++;
            int generation = _composerGeneration;
            Log("ENTER ComposeDialog generation " + generation);

            MeasureDialog();

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedSize(DialogWidth, DialogHeight);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            _currentScroll = CurrentScroll();
            _scrollContentBounds = null;
            _viewportBounds = null;
            _scrollbarBounds = null;
            _rowsContainer = null;
            _rows.Clear();
            _rowSpans.Clear();
            _dropdownKeys.Clear();
            _hovered = null;
            _composedLayout = LayoutSignature();

            double footerRight = DialogWidth - 40;
            double resetWidth = 100;
            double compareWidth = 150;

            SingleComposer = capi.Gui.CreateCompo("vintagevisuals-tuning-studio", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Vintage Visuals - Visual Tuning", OnTitleBarClose)
                .BeginChildElements(bgBounds)
                    .AddStaticText(PageTitle(), CairoFont.WhiteMediumText(), ElementBounds.Fixed(ContentLeft, HeaderY, ContentWidth - SearchWidth - 20, 26))
                    .AddInset(ElementBounds.Fixed(DialogPadding, 42, SidebarWidth, FooterY - 42), 2)
                    .AddInset(ElementBounds.Fixed(ContentLeft - 10, BodyY - 8, ContentWidth + 20, BodyHeight + 16), 2)
                    .AddInset(ElementBounds.Fixed(ContentLeft - 10, DetailsY - 4, ContentWidth + 20, DetailsHeight + 8), 2, 0.5f)
                    .AddDynamicText(DetailsText(), CairoFont.WhiteDetailText(),
                        ElementBounds.Fixed(ContentLeft, DetailsY, ContentWidth, DetailsHeight), DetailsKey)
                    .AddTextInput(ElementBounds.Fixed(ContentLeft + ContentWidth - SearchWidth, HeaderY - 4, SearchWidth, 30),
                        text => OnSearchChanged(generation, text), CairoFont.TextInput(), SearchKey)
                    .AddSwitch(on => Guard(generation, "AdvancedChanged", () => OnAdvancedChanged(on)),
                        ElementBounds.Fixed(ContentLeft, FooterY + 2, 30, 30), AdvancedSwitchKey, 24)
                    .AddStaticText("Show advanced and unavailable settings", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(ContentLeft + 36, FooterY + 6, 300, 24))
                    .AddButton(_controller.CompareActive ? "Compare: on" : "Compare: off",
                        () => Guard(generation, "Compare", OnCompareClicked),
                        ElementBounds.Fixed(footerRight - resetWidth - 12 - compareWidth, FooterY, compareWidth, 28),
                        CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "compare");

            if (!Searching && _tab != SettingTab.Overview)
            {
                SingleComposer.AddButton("Reset Tab", () => Guard(generation, "ResetTab", OnResetTabClicked),
                    ElementBounds.Fixed(footerRight - resetWidth, FooterY, resetWidth, 28),
                    CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "reset-tab");
            }

            AddTabs(generation);
            AddSavedLooks(generation);

            if (Searching) AddSettingsTab(_tab);
            else if (_tab == SettingTab.Overview) AddOverview();
            else if (_tab == SettingTab.Debug) AddDebug();
            else AddSettingsTab(_tab);

            SingleComposer.EndChildElements().Compose();

            GuiElementSwitch advanced = SingleComposer.GetSwitch(AdvancedSwitchKey);
            if (advanced != null) advanced.On = _advanced;

            RestoreSearchBox();
            ConfigureScrollbar();

            // Reset visibility and every other live state come from one place,
            // the same one a later config change goes through.
            RefreshRows();
            Log("EXIT ComposeDialog generation " + generation);
        }

        private void AddTabs(int generation)
        {
            foreach (GuiTab tab in _tabs) tab.Active = !Searching && tab.DataInt == (int)_tab;

            double tabX = DialogPadding + SidebarPadding;
            double tabWidth = SidebarWidth - 2 * SidebarPadding;
            double y = TabTop;
            foreach (GuiTab tab in _tabs)
            {
                bool active = tab.Active;
                CairoFont font = active
                    ? CairoFont.WhiteSmallText().WithColor(GuiStyle.ActiveButtonTextColor)
                    : CairoFont.WhiteSmallText();

                SingleComposer.AddButton(tab.Name, () => Guard(generation, "SelectTab", () =>
                {
                    SelectTab((SettingTab)tab.DataInt);
                    return true;
                }), ElementBounds.Fixed(tabX, y, tabWidth, 26), font, EnumButtonStyle.Normal, "tab-" + tab.DataInt);

                y += TabStep;
            }
        }

        /// <summary>
        /// Saved looks, at the foot of the sidebar: pick a slot, then Save or
        /// Load. A look is every tuning value (never a debug view), stored in its
        /// own file and put back through the controller like any other edit.
        /// </summary>
        private void AddSavedLooks(int generation)
        {
            double x = DialogPadding + SidebarPadding;
            double width = SidebarWidth - 2 * SidebarPadding;
            double y = FooterY - 104;
            if (y < TabTop + _tabs.Length * TabStep + 4) return;

            SingleComposer.AddStaticText("Saved looks", CairoFont.WhiteSmallText().WithColor(HeadingColor),
                ElementBounds.Fixed(x, y, width, 20));

            double slotWidth = (width - 2 * 6) / SavedLooks.SlotCount;
            for (int i = 0; i < SavedLooks.SlotCount; i++)
            {
                int slot = i;
                bool selected = slot == _lookSlot;
                bool filled = _looks.Get(slot) != null;
                CairoFont font = selected ? CairoFont.WhiteSmallText().WithColor(GuiStyle.ActiveButtonTextColor) : CairoFont.WhiteSmallText();
                SingleComposer.AddButton((slot + 1) + (filled ? "" : " -"), () => Guard(generation, "LookSlot", () =>
                {
                    _lookSlot = slot;
                    SavedLook look = _looks.Get(slot);
                    _status = look == null ? "Look " + (slot + 1) + " is empty. Save puts your current settings in it."
                                           : "Look " + (slot + 1) + " was saved " + look.SavedAt + ". Load puts it back.";
                    ComposeDialog();
                    return true;
                }), ElementBounds.Fixed(x + i * (slotWidth + 6), y + 24, slotWidth, 26), font, EnumButtonStyle.Normal, "look-slot-" + slot);
            }

            double half = (width - 6) / 2;
            SingleComposer
                .AddButton("Save", () => Guard(generation, "LookSave", OnSaveLookClicked),
                    ElementBounds.Fixed(x, y + 58, half, 26), CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "look-save")
                .AddButton("Load", () => Guard(generation, "LookLoad", OnLoadLookClicked),
                    ElementBounds.Fixed(x + half + 6, y + 58, half, 26), CairoFont.WhiteSmallText(), EnumButtonStyle.Normal, "look-load");
        }

        private bool OnSaveLookClicked()
        {
            bool ok = _looks.Save(_lookSlot, _controller.CaptureLook());
            _status = ok ? "Saved your current settings as look " + (_lookSlot + 1) + "."
                         : "Could not save look " + (_lookSlot + 1) + "; see the log.";
            ComposeDialog();
            return true;
        }

        private bool OnLoadLookClicked()
        {
            SavedLook look = _looks.Get(_lookSlot);
            if (look == null)
            {
                _status = "Look " + (_lookSlot + 1) + " is empty. Save puts your current settings in it.";
                UpdateDetails();
                return true;
            }
            int changed = 0;
            LocalEdit(() => { changed = _controller.ApplyLook(look.Values); });
            _status = "Loaded look " + (_lookSlot + 1) + " (" + changed + " settings changed).";
            ComposeDialog();
            return true;
        }

        private bool OnCompareClicked()
        {
            LocalEdit(() => _controller.ToggleCompare());
            _status = _controller.CompareActive
                ? "Compare is on: the left half of the screen is vanilla, the right half Vintage Visuals."
                : "Compare is off.";
            ComposeDialog();
            return true;
        }

        private string PageTitle()
        {
            if (Searching) return "Search results";
            if (_tab == SettingTab.Color) return "Colour and exposure";
            if (_tab == SettingTab.Light) return "Sunlight and moonlight";
            if (_tab == SettingTab.Materials) return "Surfaces and materials";
            if (_tab == SettingTab.Nature) return "Plants and wildlife";
            return TabName(_tab);
        }

        private string TabName(SettingTab tab)
        {
            GuiTab found = _tabs.FirstOrDefault(t => t.DataInt == (int)tab);
            return found == null ? tab.ToString() : found.Name;
        }

        private void SelectTab(SettingTab tab)
        {
            Log("ENTER SelectTab " + tab);
            // A click on the current tab is a no-op, unless a search is
            // showing: then it is how the player goes back to that tab.
            if (_tab == tab)
            {
                if (!Searching)
                {
                    Log("EXIT SelectTab unchanged " + tab);
                    return;
                }
            }

            SaveScroll();
            _search = "";
            _status = null;
            _tab = tab;
            ComposeDialog();
            Log("EXIT SelectTab " + tab);
        }

        // --- search ------------------------------------------------------------

        /// <summary>
        /// A keystroke in the search box. The list is rebuilt on the next frame,
        /// not inside the text box's own key handler: recomposing there would
        /// dispose the element that is still handling the key. Several quick
        /// keystrokes coalesce into one rebuild.
        /// </summary>
        private void OnSearchChanged(int generation, string text)
        {
            if (_restoringSearch || !IsCurrentGeneration(generation, "SearchChanged")) return;
            text = text ?? "";
            if (string.Equals(text, _search, StringComparison.Ordinal)) return;

            if (!Searching) SaveScroll();
            _search = text;
            if (_searchComposeQueued) return;
            _searchComposeQueued = true;
            capi.Event.EnqueueMainThreadTask(() =>
            {
                _searchComposeQueued = false;
                if (!IsOpened()) return;
                Log("ENTER SearchRebuild");
                ComposeDialog();
                Log("EXIT SearchRebuild");
            }, "vintagevisuals-studio-search");
        }

        /// <summary>Puts the typed text back in the new composer's search box and keeps the caret there.</summary>
        private void RestoreSearchBox()
        {
            GuiElementTextInput input = SingleComposer.GetTextInput(SearchKey);
            if (input == null) return;
            input.SetPlaceHolderText("Search settings...");
            if (!Searching && string.IsNullOrEmpty(_search)) return;

            _restoringSearch = true;
            try
            {
                input.SetValue(_search);
            }
            finally
            {
                _restoringSearch = false;
            }
            SingleComposer.FocusElement(input.TabIndex);
        }

        private static bool Matches(VisualSetting setting, string tabName, string query)
        {
            foreach (string word in query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                bool hit = Contains(setting.DisplayName, word) || Contains(setting.Description, word) ||
                           Contains(setting.Section, word) || Contains(tabName, word);
                if (!hit) return false;
            }
            return true;
        }

        private static bool Contains(string text, string word)
        {
            return text != null && text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // --- overview ---------------------------------------------------------

        // Overview cards, two to a row. Each line is one line: static text wraps
        // past its bounds instead of clipping, so every string is cut to fit
        // (OverviewChars) and the card is tall enough for three lines.
        private const double OverviewCardGap = 8;
        private const double OverviewButtonWidth = 70;
        private double OverviewCardWidth { get { return (ContentWidth - 20 - OverviewCardGap) / 2; } }
        private double OverviewTextWidth { get { return OverviewCardWidth - OverviewButtonWidth - 30; } }
        private int OverviewChars { get { return CharsFor(OverviewTextWidth); } }

        private void AddOverview()
        {
            double rows = 5;
            double cardHeight = Math.Min(78, (BodyHeight - (rows - 1) * OverviewCardGap - 4) / rows);
            double step = cardHeight + OverviewCardGap;
            double left = ContentLeft;
            double right = ContentLeft + OverviewCardWidth + OverviewCardGap;
            double y = BodyY + 2;

            AddOverviewRow("COLOUR", "ColorGrade.Enabled", SettingTab.Color,
                "Exposure " + ValueByCode("colorgrade_exposure") + " · Eye adaptation " + OnOff("AdaptiveExposure.Enabled"),
                "Open exposure, tonemap, colour style and grading controls.", left, y, cardHeight);
            AddOverviewRow("LIGHT", "PseudoPBR.Enabled", SettingTab.Light,
                "Sunflecks " + ValueByCode("pbr_sunflecks") + " · Sun shafts " + ValueByCode("pbr_shafts"),
                "Open sunflecks, canopy shade, shafts and moonlight.", right, y, cardHeight);
            y += step;

            AddOverviewRow("SURFACES", "PseudoPBR.Enabled", SettingTab.Materials,
                "Surface relief " + ValueByCode("pbr_normalstrength") + " · Polish or wear " + ValueByCode("pbr_roughnessbias"),
                "Open relief, roughness, specular and glow controls.", left, y, cardHeight);
            AddOverviewRow("NATURE", "PseudoPBR.Enabled", SettingTab.Nature,
                "Light through leaves " + ValueByCode("pbr_foliage") + " · Falling leaves " + ValueByCode("pbr_leaves"),
                "Open wind, plant shading, fireflies, pollen and leaves.", right, y, cardHeight);
            y += step;

            AddOverviewRow("WEATHER", "Weather.Enabled", SettingTab.Weather,
                "Wet surfaces " + ValueByCode("weather_wetness") + " · Cloud shadows " + ValueByCode("weather_cloudshadow"),
                "Open wetness, ripples, rain fog and cloud shadow controls.", left, y, cardHeight);
            AddOverviewRow("ATMOSPHERE", "Atmosphere.Enabled", SettingTab.Atmosphere,
                "Distance haze " + ValueByCode("atmosphere_aerial") + " · Valley haze " + ValueByCode("atmosphere_heighthaze"),
                "Open distance haze, sunlit air, mist and weather in the air.", right, y, cardHeight);
            y += step;

            AddOverviewRow("WATER", "Water.Enabled", SettingTab.Water,
                "Caustics " + ValueByCode("water_caustics") + " · Reflection " + ValueByCode("water_sky"),
                "Open waves, reflections, depth colour, shores and caustics.", left, y, cardHeight);
            bool reflectionsUnavailable = StateByCode("reflections_scene") == NotInThisBuild;
            AddOverviewRow("REFLECTIONS", "PseudoPBR.Enabled", SettingTab.Reflections,
                reflectionsUnavailable
                    ? "Needs Surface materials, which draws the reflections."
                    : "Block reflections " + StateByCode("pbr_pixelreflect") + " · World on blocks " + StateByCode("reflections_scene"),
                "Open block reflection controls.", right, y, cardHeight);
            y += step;

            AddOverviewRow("PERFORMANCE", null, SettingTab.Performance,
                "Quality " + ValueByCode("perf_quality") + " · Depth prepass " + OnOff("Performance.DepthPrepass"),
                "Open the quality tier, the frame-time readout and benches.", left, y, cardHeight);
            AddOverviewRow("DEBUG", null, SettingTab.Debug,
                _controller.CompareActive ? "Compare with vanilla is on" : "Normal rendering",
                "Open debug views, the compare wipe and reports.", right, y, cardHeight);
        }

        private void AddOverviewRow(string label, string path, SettingTab target, string summary, string description,
                                    double x, double y, double height)
        {
            bool on = path == null || ConfigAccess.Get(_controller.Config, path) >= 0.5f;
            VisualSetting master = path == null ? null : VisualSettingRegistry.All.FirstOrDefault(v => v.Path == path);
            bool unavailable = master != null && Availability(master).State == AvailabilityState.Unavailable;
            int generation = _composerGeneration;
            double textX = x + 12;
            string state = path == null ? "" : unavailable ? NotInThisBuild : on ? "● On" : "○ Off";
            double line = Math.Max(16, (height - 10) / 3);

            SingleComposer
                .AddInset(ElementBounds.Fixed(x, y, OverviewCardWidth, height), 2, 0.42f)
                .AddStaticText(label, CairoFont.WhiteSmallText().WithColor(HeadingColor), ElementBounds.Fixed(textX, y + 6, 140, 20))
                .AddStaticText(state, on && !unavailable ? CairoFont.WhiteSmallText() : CairoFont.WhiteSmallText().WithColor(WarningColor),
                    ElementBounds.Fixed(textX + 140, y + 6, OverviewTextWidth - 140, 20))
                .AddStaticText(ShortText(summary, OverviewChars), CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(textX, y + 6 + line, OverviewTextWidth, 18))
                .AddStaticText(ShortText(description, OverviewChars), CairoFont.WhiteDetailText().WithColor(GuiStyle.DisabledTextColor),
                    ElementBounds.Fixed(textX, y + 6 + 2 * line, OverviewTextWidth, 18))
                .AddButton("Open  >", () =>
                {
                    if (!IsCurrentGeneration(generation, "OverviewRow")) return false;
                    SelectTab(target);
                    return true;
                }, ElementBounds.Fixed(x + OverviewCardWidth - OverviewButtonWidth - 10, y + (height - 28) / 2, OverviewButtonWidth, 28),
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
            if (Availability(setting).State == AvailabilityState.Unavailable) return NotInThisBuild;
            return _controller.DisplayValue(setting);
        }

        private string ValueByCode(string code)
        {
            VisualSetting setting = VisualSettingRegistry.ByCode(code);
            return setting == null ? "n/a" : _controller.DisplayValue(setting);
        }

        // --- the details strip ------------------------------------------------

        private void UpdateDetails()
        {
            GuiElementDynamicText details = SingleComposer == null ? null : SingleComposer.GetDynamicText(DetailsKey);
            if (details != null) details.SetNewText(DetailsText());
        }

        private string DetailsText()
        {
            int max = CharsFor(ContentWidth) * 3;
            if (_hovered != null) return ShortText(Tooltip(_hovered), max);
            if (!string.IsNullOrEmpty(_status)) return ShortText(_status, max);
            if (Searching) return "Settings from every tab whose name or description contains \"" + _search.Trim() +
                                  "\". Clear the box or pick a tab to go back.";
            if (_tab == SettingTab.Overview)
                return "Point at any setting to read all about it here. Search finds a setting on any tab; " +
                       "Compare splits the screen with vanilla; Saved looks keeps up to three of your own setups.";
            if (_tab == SettingTab.Debug)
                return "Debug views replace the picture with one ingredient of it. Return to Normal Rendering puts everything back.";
            return "Point at a setting to read what it does, where to look for it and what it costs.";
        }

        // --- settings tabs ----------------------------------------------------

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

        /// <summary>Search: matching settings from every tab, advanced ones included, in sidebar order.</summary>
        private IEnumerable<VisualSetting> SearchResults()
        {
            string query = _search.Trim();
            foreach (GuiTab tab in _tabs)
            {
                SettingTab id = (SettingTab)tab.DataInt;
                foreach (VisualSetting setting in _controller.VisibleSettings(id, true))
                {
                    if (!_advanced && Availability(setting).State == AvailabilityState.Unavailable) continue;
                    if (Matches(setting, tab.Name, query)) yield return setting;
                }
            }
        }

        private IEnumerable<RowItem> RowsFor(SettingTab tab)
        {
            if (Searching)
            {
                string heading = null;
                bool found = false;
                foreach (VisualSetting setting in SearchResults())
                {
                    found = true;
                    string next = TabName(setting.Tab) + "  /  " + setting.Section;
                    if (!string.Equals(heading, next, StringComparison.Ordinal))
                    {
                        heading = next;
                        yield return RowItem.ForSection(heading);
                    }
                    yield return RowItem.ForSetting(setting);
                }
                if (!found)
                {
                    yield return RowItem.ForNote("No setting matches \"" + _search.Trim() + "\".");
                    yield return RowItem.ForNote(_advanced ? "Try a shorter word." : "Try a shorter word, or tick Show advanced below.");
                }
                yield break;
            }

            foreach (RowItem extra in TabHeaderRows(tab)) yield return extra;

            string currentSection = null;
            bool any = false;
            foreach (VisualSetting setting in ShownSettings(tab))
            {
                any = true;
                if (!string.Equals(currentSection, setting.Section, StringComparison.Ordinal))
                {
                    currentSection = setting.Section;
                    yield return RowItem.ForSection(currentSection);
                }
                yield return RowItem.ForSetting(setting);
            }
            // A tab whose every row is unavailable used to open completely
            // blank (Reflections, seen in game), which reads as a broken
            // dialog rather than as "nothing here works yet".
            if (!any && !_advanced)
            {
                // Plain text, not a section header: the orange header style
                // read as an error (in-game audit, 2026-11-23).
                yield return RowItem.ForNote(EmptyTabLine1);
                yield return RowItem.ForNote(EmptyTabLine2);
            }

            foreach (RowItem extra in TabFooterRows(tab)) yield return extra;
        }

        public const string EmptyTabLine1 = "Nothing on this tab changes the picture in this build.";
        public const string EmptyTabLine2 = "Tick Show advanced below to see why.";

        /// <summary>Rows a tab shows above its settings: the Light tab's quick looks.</summary>
        private IEnumerable<RowItem> TabHeaderRows(SettingTab tab)
        {
            if (tab != SettingTab.Light) yield break;

            // The forest light presets, on the tab whose sliders they move. In
            // the list rather than the footer: in the footer they overlapped
            // Reset Tab (in-game audit, 2026-11-27).
            yield return RowItem.ForSection("Quick looks");
            yield return RowItem.ForAction("Sunlight under trees, all at once:",
                new ActionButton("Subtle", () => OnPresetClicked("Subtle")),
                new ActionButton(VisualTuningStudioController.DefaultPreset, () => OnPresetClicked(VisualTuningStudioController.DefaultPreset)),
                new ActionButton("Cinematic", () => OnPresetClicked("Cinematic")));
        }

        /// <summary>Rows a tab shows below its settings: the Performance tab's benches and the last result.</summary>
        private IEnumerable<RowItem> TabFooterRows(SettingTab tab)
        {
            if (tab != SettingTab.Performance) yield break;

            foreach (RowItem row in ProfilerRows()) yield return row;

            yield return RowItem.ForSection("Benches");
            yield return RowItem.ForAction("Closes this panel, then measures:",
                new ActionButton("10 s bench", () => RunBench(".vvbench")),
                new ActionButton("Feature costs", () => RunBench(".vvbench features")),
                new ActionButton("Tiers", () => RunBench(".vvbench tiers")));

            string report = PerformanceTools.LastBenchReport;
            if (string.IsNullOrEmpty(report))
            {
                yield return RowItem.ForNote("No bench has run this session. Results also go to the chat and to client-main.log.");
                yield break;
            }

            yield return RowItem.ForNote("Last bench, " + PerformanceTools.LastBenchAt + ":");
            int shown = 0;
            foreach (string raw in report.Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (line.StartsWith("full detail", StringComparison.Ordinal)) break;
                yield return RowItem.ForNote(ShortText(line.Trim(), CharsFor(ContentWidth - 40)));
                if (++shown >= 18) break;
            }
        }

        /// <summary>
        /// D99. The line every row's details end with: which profiled system
        /// owns the setting and the command that measures it, so the panel and
        /// the profiler stay in step without a second list.
        /// </summary>
        private static string ProfilerLine(VisualSetting setting)
        {
            if (setting.DebugOnly) return "";
            var owner = VintageVisuals.Common.Diagnostics.Profiling.ProfileRegistry.Shared.ForSettingPath(setting.Path);
            if (setting.Kind == SettingKind.Dropdown) return owner == null ? "" : " Profiled under " + owner.DisplayName + ".";
            return " Measure: .vvbench sweep " + setting.Code + (owner != null ? " (" + owner.DisplayName + ")." : ".");
        }

        /// <summary>D99. The profiler's buttons and status, above the older benches.</summary>
        private IEnumerable<RowItem> ProfilerRows()
        {
            yield return RowItem.ForSection("Profiler");
            yield return RowItem.ForAction("Shader cost, compiled out vs in:",
                new ActionButton("All groups", () => RunBench(".vvbench groups all")),
                new ActionButton("Active only", () => RunBench(".vvbench groups active")));
            yield return RowItem.ForAction("Cost curves and everything at once:",
                new ActionButton("Sweep system", () => RunSweepSystem()),
                new ActionButton("Quick suite", () => RunBench(".vvbench suite quick")),
                new ActionButton("Full suite", () => RunBench(".vvbench suite")));
            yield return RowItem.ForAction("Coverage and control:",
                new ActionButton("Coverage", () => RunCommand(".vvprofile coverage")),
                new ActionButton("CPU now", () => RunCommand(".vvprofile cpu")),
                new ActionButton("Stop bench", () => RunCommand(".vvbench stop")));

            var perf = VintageVisuals.Common.Diagnostics.PerformanceTools.Current;
            string status = perf?.ProfilerStatus;
            yield return RowItem.ForNote(status != null ? "Running " + status + "."
                : "Results go to VintagestoryData/vv-bench/ as CSV, with your game settings and GPU clock beside every run. " +
                  "Add @name to any bench command to run it in a scene saved with .vvscene save name.");
        }

        private bool RunSweepSystem()
        {
            string id = VisualSettingRegistry.SweepSystemId(_controller.Config.Performance.ProfilerSweepSystem);
            if (id == null) return true;
            return RunBench(".vvbench sweep " + id);
        }

        /// <summary>A command that only reports in chat: the panel stays open.</summary>
        private bool RunCommand(string command)
        {
            capi.TriggerChatMessage(command);
            return true;
        }

        private bool RunBench(string command)
        {
            Log("ENTER RunBench " + command);
            TryClose();
            capi.TriggerChatMessage(command);
            Log("EXIT RunBench " + command);
            return true;
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
            text.Append((int)_tab).Append(_advanced ? 'A' : '-').Append('/').Append(_search ?? "");
            if (!Searching && (_tab == SettingTab.Overview || _tab == SettingTab.Debug)) return text.ToString();

            foreach (VisualSetting setting in Searching ? SearchResults() : ShownSettings(_tab))
            {
                text.Append('|').Append(setting.Code).Append(':').Append((int)Availability(setting).State);
            }
            return text.ToString();
        }

        private bool CanRefreshInPlace()
        {
            if (SingleComposer == null) return false;
            if (!Searching && (_tab == SettingTab.Overview || _tab == SettingTab.Debug)) return false;
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
            _scrollContentBounds = ElementBounds.Fixed(0, -_currentScroll, ContentWidth,
                Math.Max(bodyHeight, _currentContentHeight));

            SingleComposer.BeginChildElements(bodyBounds);
            SingleComposer.BeginClip(clipBounds);
            SingleComposer.AddContainer(_scrollContentBounds, RowsContainerKey);
            _rowsContainer = SingleComposer.GetContainer(RowsContainerKey);

            double y = 0;
            foreach (RowItem row in rows)
            {
                if (row.IsSection)
                {
                    AddSection(row.Section, y, row.IsNote);
                    y += row.IsNote ? NoteHeight : HeaderHeight;
                }
                else if (row.IsAction)
                {
                    AddActionRow(row, y);
                    y += ActionHeight;
                }
                else
                {
                    AddSettingRow(row.Setting, y);
                    _rowSpans.Add(new RowSpan(y, RowHeight, row.Setting));
                    y += RowHeight;
                }
            }

            SingleComposer.EndClip();
            SingleComposer.EndChildElements();

            if (_currentContentHeight > bodyHeight)
            {
                SingleComposer.AddVerticalScrollbar(OnScrollbarChanged, scrollbarBounds, ScrollbarKey);
                _scrollbarBounds = scrollbarBounds;
            }
        }

        // EVERY ROW ELEMENT GOES INTO THE SCROLL CONTAINER, never onto the composer.
        //
        // The composer paints the static part of each element it owns (text,
        // slider track, switch background, dropdown box) into one texture at
        // compose time, at the position the element had then, and does not
        // scissor it. Moving the scroll child afterwards moved only the
        // interactive parts: slider tracks and dropdown boxes stayed behind,
        // over the title bar, the footer and outside the dialog. The container
        // owns its own texture and draws it at its current bounds, inside the
        // clip, so the whole row moves and clips together.

        private void AddRowElement(GuiElement element)
        {
            _rowsContainer.Add(element);
        }

        private void AddSection(string label, double y, bool note = false)
        {
            AddRowElement(new GuiElementDynamicText(capi, label, note ? CairoFont.WhiteSmallText() : SectionFont(),
                ElementBounds.Fixed(RowLeft, y + (note ? 2 : 8), ContentWidth - 20, 20)));
        }

        /// <summary>A label and a few buttons on one line: the Light presets, the benches.</summary>
        private void AddActionRow(RowItem row, double y)
        {
            int generation = _composerGeneration;
            AddRowElement(new GuiElementDynamicText(capi, row.Section ?? "", CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(LabelX, y + 9, LabelWidth + 60, 20)));

            double x = InfoX + 60;
            double width = Math.Min(130, (ContentWidth - x - RowRightPadding - 8 * row.Actions.Length) / Math.Max(1, row.Actions.Length));
            foreach (ActionButton action in row.Actions)
            {
                ActionButton captured = action;
                CairoFont font = CairoFont.WhiteSmallText();
                var button = new GuiElementTextButton(capi, captured.Label, font,
                    font.Clone().WithColor(GuiStyle.ActiveButtonTextColor),
                    () => IsCurrentGeneration(generation, "Action " + captured.Label) && captured.Run(),
                    ElementBounds.Fixed(x, y + 4, width, 28), EnumButtonStyle.Normal);
                button.SetOrientation(font.Orientation);
                AddRowElement(button);
                x += width + 8;
            }
        }

        private void AddSettingRow(VisualSetting setting, double y)
        {
            SettingAvailability availability = Availability(setting);
            bool working = availability.HasWorkingControl;
            var widgets = new RowWidgets(setting, availability.State, working);
            _rows[setting.Code] = widgets;

            CairoFont nameFont = working ? CairoFont.WhiteSmallText() : DisabledFont(CairoFont.WhiteSmallText());
            CairoFont infoFont = working ? CairoFont.WhiteDetailText() : DisabledFont(CairoFont.WhiteDetailText());

            // The first sentence of the description, always - the "where to look"
            // note and the rest of the text are in the details strip.
            string info = ShortDescription(setting);
            string name = setting.Kind == SettingKind.Slider || !setting.RequiresShaderReload ? setting.DisplayName : setting.DisplayName + " *";

            AddRowElement(new GuiElementDynamicText(capi, name, nameFont,
                ElementBounds.Fixed(LabelX, y + LineOneY, LabelWidth, 22)));
            AddRowElement(new GuiElementDynamicText(capi, info, infoFont,
                ElementBounds.Fixed(InfoX, y + LineOneY + 3, InfoTextWidth, 18)));

            // Reset always exists and always sits in the same slot; it is shown
            // only while the row differs from its default AND has a working
            // control. A greyed-out row under a switched-off parent showed Reset
            // (in-game audit, 2026-11-27), and unavailable rows get none, since
            // resetting a value nothing reads is not an action.
            if (availability.State != AvailabilityState.Unavailable)
            {
                CairoFont resetFont = CairoFont.WhiteSmallText();
                var reset = new GuiElementTextButton(capi, "Reset", resetFont,
                    resetFont.Clone().WithColor(GuiStyle.ActiveButtonTextColor),
                    () => ResetSetting(setting), ElementBounds.Fixed(ResetX, y + LineTwoY, ResetWidth, 24), EnumButtonStyle.Small);
                reset.SetOrientation(resetFont.Orientation);
                AddRowElement(reset);
                widgets.Reset = reset;
            }

            if (!working)
            {
                // No control at all: a disabled-looking slider that still moves
                // is exactly the thing this row is trying not to be. A switched-off
                // parent is something the player can fix, so it reads as a warning;
                // "not in this build" is not, so it stays grey.
                CairoFont reasonFont = availability.State == AvailabilityState.ParentOff
                    ? CairoFont.WhiteDetailText().WithColor(WarningColor)
                    : DisabledFont(CairoFont.WhiteDetailText());
                AddRowElement(new GuiElementDynamicText(capi, availability.Reason, reasonFont,
                    ElementBounds.Fixed(ControlX, y + LineTwoY + 4, ControlWidth, 18)));
                AddValueText(widgets, DisabledFont(CairoFont.WhiteSmallText()), y);
            }
            else if (setting.Kind == SettingKind.Toggle) AddToggle(setting, widgets, y);
            else if (setting.Kind == SettingKind.Dropdown) AddDropdown(setting, widgets, y);
            else if (setting.Kind == SettingKind.Choice) AddChoice(setting, widgets, y);
            else AddSlider(setting, widgets, y);
        }

        private void AddValueText(RowWidgets widgets, CairoFont font, double y)
        {
            widgets.Value = new GuiElementDynamicText(capi, RowValueText(widgets.Setting), font,
                ElementBounds.Fixed(ValueX, y + LineTwoY + 2, ValueWidth, 22));
            AddRowElement(widgets.Value);
        }

        private static CairoFont DisabledFont(CairoFont font)
        {
            return font.WithColor(GuiStyle.DisabledTextColor);
        }

        private static CairoFont SectionFont()
        {
            return CairoFont.WhiteSmallText().WithColor(HeadingColor).WithWeight(FontWeight.Bold);
        }

        /// <summary>The value column, read from the config as it is NOW - after clamping.</summary>
        private string RowValueText(VisualSetting setting)
        {
            if (setting.Kind == SettingKind.Toggle) return _controller.Value(setting) >= 0.5f ? "ON" : "OFF";
            return _controller.DisplayValue(setting);
        }

        private void AddToggle(VisualSetting setting, RowWidgets widgets, double y)
        {
            bool on = _controller.Value(setting) >= 0.5f;
            int generation = _composerGeneration;

            widgets.Switch = new GuiElementSwitch(capi, value =>
            {
                if (!IsCurrentGeneration(generation, "ToggleChanged " + setting.Code)) return;
                Log("ENTER ToggleChanged " + setting.Code);
                LocalEdit(() => _controller.Set(setting, value ? 1f : 0f));
                AfterLocalEdit(true);
                Log("EXIT ToggleChanged " + setting.Code);
            }, ElementBounds.Fixed(ControlX, y + LineTwoY - 2, 30, 30), 24);
            widgets.Switch.On = on;
            AddRowElement(widgets.Switch);

            // The toggle's ON/OFF lives in the value column like every other
            // row's value, so there is one place to read state from.
            AddValueText(widgets, CairoFont.WhiteSmallText(), y);
        }

        private void AddSlider(VisualSetting setting, RowWidgets widgets, double y)
        {
            int generation = _composerGeneration;
            int steps = SliderSteps(setting);

            var slider = new StudioSlider(capi, newValue =>
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
            }, ElementBounds.Fixed(ControlX, y + LineTwoY, ControlWidth, 22), _viewportBounds);

            slider.SetValues(SliderIndex(setting), 0, steps, 1, "");

            // The slider counts in steps, not in the setting's units. Left to
            // itself its tooltip would print the step index.
            slider.OnSliderTooltip = index => setting.Format(setting.Min + index * setting.Step);
            AddRowElement(slider);
            widgets.Slider = slider;

            // Where the shipped default sits on the track, so "back to normal"
            // can be found by eye as well as by Reset.
            float range = setting.Max - setting.Min;
            float fraction = range > 0 ? (ConfigAccess.Default(setting.Path) - setting.Min) / range : 0f;
            AddRowElement(new DefaultMark(capi, ElementBounds.Fixed(ControlX, y + LineTwoY + 22, ControlWidth, 5),
                Math.Max(0f, Math.Min(1f, fraction))));

            AddValueText(widgets, CairoFont.WhiteSmallText(), y);
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

            widgets.Dropdown = new GuiElementDropDown(capi, setting.Choices, setting.Choices, DropdownIndex(setting),
                (code, selectedValue) =>
                {
                    if (!selectedValue) return;
                    if (!IsCurrentGeneration(generation, "DropdownChanged " + setting.Code)) return;
                    Log("ENTER DropdownChanged " + setting.Code);
                    LocalEdit(() => _controller.SetString(setting, code));
                    AfterLocalEdit(true);
                    Log("EXIT DropdownChanged " + setting.Code);
                },
                ElementBounds.Fixed(ControlX, y + LineTwoY, Math.Min(ControlWidth, 240), 24),
                CairoFont.WhiteSmallText(), false);
            AddRowElement(widgets.Dropdown);
        }

        /// <summary>A numeric setting chosen by name: a quality tier, not a slider reading "2".</summary>
        private void AddChoice(VisualSetting setting, RowWidgets widgets, double y)
        {
            int generation = _composerGeneration;
            string[] values = Enumerable.Range(0, setting.Choices.Length).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();

            widgets.Dropdown = new GuiElementDropDown(capi, values, setting.Choices, DropdownIndex(setting),
                (code, selectedValue) =>
                {
                    if (!selectedValue) return;
                    if (!IsCurrentGeneration(generation, "ChoiceChanged " + setting.Code)) return;
                    int index;
                    if (!int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) return;
                    Log("ENTER ChoiceChanged " + setting.Code);
                    LocalEdit(() => _controller.Set(setting, setting.Min + index * setting.Step));
                    AfterLocalEdit(true);
                    Log("EXIT ChoiceChanged " + setting.Code);
                },
                ElementBounds.Fixed(ControlX, y + LineTwoY, Math.Min(ControlWidth, 240), 24),
                CairoFont.WhiteSmallText(), false);
            AddRowElement(widgets.Dropdown);
        }

        private int DropdownIndex(VisualSetting setting)
        {
            if (setting.Kind == SettingKind.Choice)
            {
                int index = (int)Math.Round((_controller.Value(setting) - setting.Min) / setting.Step);
                return Math.Max(0, Math.Min(setting.Choices.Length - 1, index));
            }
            string current = _controller.StringValue(setting);
            int selected = Array.FindIndex(setting.Choices, c => string.Equals(c, current, StringComparison.Ordinal));
            return selected < 0 ? 0 : selected;
        }

        private string ShortDescription(VisualSetting setting)
        {
            return ShortText(setting.Summary, InfoChars);
        }

        private static string ShortText(string text, int max)
        {
            text = text ?? "";
            if (text.Length <= max) return text;

            int cut = text.LastIndexOf(' ', max);
            if (cut < 24) cut = max;
            return text.Substring(0, cut) + "...";
        }

        /// <summary>The full story of one setting, for the details strip.</summary>
        private string Tooltip(VisualSetting setting)
        {
            SettingAvailability availability = Availability(setting);
            string text = setting.DisplayName + ": " + setting.Description;

            if (availability.State == AvailabilityState.Conditional && availability.Reason != null)
                text += "  Where to look: " + availability.Reason;
            else if (!availability.HasWorkingControl && availability.Reason != null)
                text += "  " + availability.Reason;

            text += "  Default " + DefaultValue(setting) + ".";
            if (setting.Cost != SettingCost.Free) text += " Cost: " + CostLabel(setting.Cost) + ".";
            if (setting.RequiresShaderReload) text += " Changing it rebuilds shaders (a short pause).";
            text += ProfilerLine(setting);
            return text;
        }

        private string DefaultValue(VisualSetting setting)
        {
            if (setting.Kind == SettingKind.Dropdown) return ConfigAccess.DefaultString(setting.Path) ?? "";
            if (setting.Kind == SettingKind.Toggle) return ConfigAccess.Default(setting.Path) >= 0.5f ? "on" : "off";
            return setting.Format(ConfigAccess.Default(setting.Path));
        }

        private static string CostLabel(SettingCost cost)
        {
            return cost == SettingCost.Free ? "none" : cost.ToString().ToLowerInvariant();
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

        private bool OnAdvancedChanged(bool on)
        {
            if (_advanced == on) return true;

            SaveScroll();
            _advanced = on;
            ComposeDialog();
            return true;
        }

        private bool OnPresetClicked(string name)
        {
            Log("ENTER Preset " + name);
            int changed = 0;
            LocalEdit(() => { changed = _controller.ApplyPreset(name); });
            _status = changed == 0 ? "The sunlight sliders already match " + name + "."
                                   : "Applied the " + name + " sunlight look (" + changed + " sliders moved).";
            ComposeDialog();
            Log("EXIT Preset " + name);
            return true;
        }

        private bool OnResetTabClicked()
        {
            Log("ENTER ResetTab");
            int changed = 0;
            LocalEdit(() => { changed = _controller.ResetTab(_tab, _advanced); });
            _status = changed == 0 ? "Everything on this tab is already at its default."
                                   : "Reset " + changed + " settings on this tab to their defaults.";
            ComposeDialog();
            Log("EXIT ResetTab");
            return true;
        }

        private void OnTitleBarClose()
        {
            Log("ENTER TitleBarClose");
            TryClose();
            Log("EXIT TitleBarClose");
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

            if (widgets.Value != null) widgets.Value.SetNewText(RowValueText(setting));

            if (widgets.Reset != null)
            {
                bool modified = widgets.Working && _controller.IsModified(setting);
                widgets.Reset.Visible = modified;
                widgets.Reset.Enabled = modified;
            }

            if (widgets.Slider != null)
            {
                int index = SliderIndex(setting);

                // Only when they disagree: during a drag the thumb already sits
                // where the config does, and re-seating it would fight the mouse.
                if (widgets.Slider.GetValue() != index) widgets.Slider.SetValues(index, 0, SliderSteps(setting), 1, "");
            }

            if (widgets.Switch != null) widgets.Switch.On = _controller.Value(setting) >= 0.5f;

            if (widgets.Dropdown != null && widgets.Dropdown.listMenu != null)
            {
                int index = DropdownIndex(setting);
                if (widgets.Dropdown.listMenu.SelectedIndex != index) widgets.Dropdown.SetSelectedIndex(index);
            }
        }

        // --- debug ------------------------------------------------------------

        private void AddDebug()
        {
            double y = BodyY + 4;
            int generation = _composerGeneration;
            SyncDebugSystemToActiveView();

            // System and view on ONE line. Stacked, they and the banner took
            // about 40% of the page (in-game audit, 2026-11-27).
            double systemWidth = 170;
            double viewX = ContentLeft + 96 + systemWidth + 18;
            double viewWidth = ContentLeft + ContentWidth - 30 - (viewX + 80);

            SingleComposer
                .AddStaticText("Debug System", CairoFont.WhiteSmallText(), ElementBounds.Fixed(ContentLeft, y + 3, 96, 22))
                .AddDropDown(_debugSystems.Select(s => s.Label).ToArray(), _debugSystems.Select(s => s.Label).ToArray(),
                    _debugSystemIndex,
                    (code, selected) =>
                    {
                        if (!IsCurrentGeneration(generation, "DebugSystemChanged")) return;
                        OnDebugSystemChanged(code, selected);
                    },
                    ElementBounds.Fixed(ContentLeft + 96, y, systemWidth, 26), "debug-system");
            _dropdownKeys.Add("debug-system");

            List<DebugView> views = CurrentDebugViews().ToList();
            DebugView current = CurrentDebugView(views);
            int selected = Math.Max(0, views.FindIndex(v => v.Value == current.Value));

            SingleComposer
                .AddStaticText("Debug View", CairoFont.WhiteSmallText(), ElementBounds.Fixed(viewX, y + 3, 80, 22))
                .AddDropDown(views.Select(v => v.Value.ToString()).ToArray(), views.Select(v => v.Label).ToArray(),
                    selected,
                    (code, selectedValue) =>
                    {
                        if (!IsCurrentGeneration(generation, "DebugViewChanged")) return;
                        OnDebugViewChanged(code, selectedValue);
                    },
                    ElementBounds.Fixed(viewX + 80, y, Math.Max(160, viewWidth), 26), "debug-view");
            _dropdownKeys.Add("debug-view");

            y += 34;
            SingleComposer.AddStaticText(ShortText(current.Description, CharsFor(ContentWidth - 40)), CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(ContentLeft, y, ContentWidth - 40, 18));
            y += 22;
            if (PbrTerrainDebugLimited())
            {
                SingleComposer.AddStaticText("Safe terrain path: material and secondary atlas masks are available.",
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(ContentLeft, y, ContentWidth - 40, 18));
                y += 22;
            }

            if (current.Value != 0)
            {
                AddDebugBanner(current, y);
                y += 48;
            }

            List<RowItem> diagnostics = _controller.VisibleSettings(SettingTab.Debug, true)
                .Select(RowItem.ForSetting)
                .ToList();

            if (diagnostics.Count > 0)
            {
                diagnostics.Insert(0, RowItem.ForSection("Diagnostics"));
            }

            double diagnosticsY = y + 4;
            double diagnosticsHeight = Math.Max(120, BodyY + BodyHeight - diagnosticsY);
            AddScrollableRows(diagnostics, diagnosticsY, diagnosticsHeight);
        }

        private void AddDebugBanner(DebugView current, double y)
        {
            SingleComposer
                .AddInset(ElementBounds.Fixed(ContentLeft, y, ContentWidth - 34, 40), 2, 0.6f)
                .AddStaticText("DEBUG VIEW ACTIVE", CairoFont.WhiteSmallText().WithColor(WarningColor), ElementBounds.Fixed(ContentLeft + 12, y + 10, 160, 22))
                .AddStaticText(ShortText(current.Label, CharsFor(ContentWidth - 440)), CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(ContentLeft + 170, y + 12, ContentWidth - 440, 20))
                .AddButton("Return to Normal Rendering", () => Guard(_composerGeneration, "ReturnToNormal", OnReturnToNormalClicked),
                    ElementBounds.Fixed(ContentLeft + ContentWidth - 254, y + 6, 214, 28), CairoFont.WhiteSmallText(),
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
            if (value == 0) return true;

            // Which views exist is read from the registry, which tools/smoketest
            // ties to the mode arms of each terrain shader. A hand-kept list
            // here drifted from the shader as soon as a view was added.
            DebugView view = DebugViewRegistry.Current(DebugViewRegistry.PbrPath, value);
            if (view == null) return false;
            if (FullTerrainDebugAvailable()) return view.InLegacyTerrain;
            if (!PrimaryTerrainMaterialDebugAvailable()) return false;
            if (view.RequiresSceneCapture &&
                !(PseudoPbrSubsystem.TerrainReflectPatchEnabled && _controller.Config.Reflections.SceneReflections))
                return false;
            return view.InModularTerrain;
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

        // --- scrolling --------------------------------------------------------

        private double MeasureRows(List<RowItem> rows)
        {
            return rows.Sum(row => row.IsSection ? (row.IsNote ? NoteHeight : HeaderHeight)
                                 : row.IsAction ? ActionHeight : RowHeight) + 8;
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
            if (Searching) return 0;
            double value;
            return _scrollByTab.TryGetValue(_tab, out value) ? value : 0;
        }

        private void SaveScroll()
        {
            // Search results are not a place to come back to.
            if (Searching) return;
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
            capi.Logger.VerboseDebug("[VV UI #" + sequence + "] " + message +
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

            /// <summary>Whether the row has a control. A row without one never shows Reset.</summary>
            public readonly bool Working;

            // Held directly: row elements live in the scroll container, not in
            // the composer's keyed element table.
            public GuiElementDynamicText Value;
            public GuiElementTextButton Reset;
            public GuiElementSlider Slider;
            public GuiElementSwitch Switch;
            public GuiElementDropDown Dropdown;

            public RowWidgets(VisualSetting setting, AvailabilityState state, bool working)
            {
                Setting = setting;
                State = state;
                Working = working;
            }
        }

        private readonly struct RowSpan
        {
            public readonly double Top;
            public readonly double Height;
            public readonly VisualSetting Setting;

            public RowSpan(double top, double height, VisualSetting setting)
            {
                Top = top;
                Height = height;
                Setting = setting;
            }
        }

        private sealed class ActionButton
        {
            public readonly string Label;
            public readonly Func<bool> Run;

            public ActionButton(string label, Func<bool> run)
            {
                Label = label;
                Run = run;
            }
        }

        private sealed class RowItem
        {
            public readonly string Section;
            public readonly VisualSetting Setting;
            public readonly ActionButton[] Actions;

            public readonly bool IsNote;

            public bool IsAction { get { return Actions != null; } }
            public bool IsSection { get { return Section != null && Actions == null; } }

            private RowItem(string section, VisualSetting setting, bool note = false, ActionButton[] actions = null)
            {
                Section = section;
                Setting = setting;
                IsNote = note;
                Actions = actions;
            }

            public static RowItem ForNote(string text)
            {
                return new RowItem(text, null, true);
            }

            public static RowItem ForSection(string section)
            {
                return new RowItem(section, null);
            }

            public static RowItem ForSetting(VisualSetting setting)
            {
                return new RowItem(null, setting);
            }

            public static RowItem ForAction(string label, params ActionButton[] actions)
            {
                return new RowItem(label, null, false, actions);
            }
        }

        /// <summary>
        /// The native slider, with two faults fixed for rows that scroll.
        ///
        /// THE VALUE BUBBLE ONLY WHILE DRAGGING. The native slider draws its
        /// value bubble while it believes the pointer is over it, and learns that
        /// only from mouse moves - which the scroll container stops forwarding
        /// as soon as an earlier row handles one. So a bubble stayed on screen
        /// after a drag ("0.70" over Sun and moon glints, in-game audit,
        /// 2026-11-27). The value column already shows the value; the bubble is
        /// kept for the drag, where it is next to the pointer.
        ///
        /// THE THUMB CLIPS WITH ITS TRACK. The thumb is taller than the track,
        /// so when the track had scrolled just out of the list the thumb's foot
        /// still showed inside it, a fragment with no row. While the track is
        /// partly outside the list, the thumb is clipped to the track.
        /// </summary>
        private sealed class StudioSlider : GuiElementSlider
        {
            private static readonly FieldInfo HoverField =
                typeof(GuiElementSlider).GetField("mouseOnSlider", BindingFlags.Instance | BindingFlags.NonPublic);

            private readonly ElementBounds _viewport;

            public StudioSlider(ICoreClientAPI capi, ActionConsumable<int> onNewSliderValue, ElementBounds bounds, ElementBounds viewport)
                : base(capi, onNewSliderValue, bounds)
            {
                _viewport = viewport;
            }

            public override void RenderInteractiveElements(float deltaTime)
            {
                try { HoverField?.SetValue(this, false); } catch { }

                bool partly = _viewport != null && _viewport.ParentBounds != null &&
                              (Bounds.renderY < _viewport.renderY ||
                               Bounds.renderY + Bounds.OuterHeight > _viewport.renderY + _viewport.OuterHeight);
                if (partly) api.Render.PushScissor(Bounds, true);
                base.RenderInteractiveElements(deltaTime);
                if (partly) api.Render.PopScissor();
            }
        }

        /// <summary>A small mark under a slider's track at the shipped default.</summary>
        private sealed class DefaultMark : GuiElement
        {
            private readonly float _fraction;

            public DefaultMark(ICoreClientAPI capi, ElementBounds bounds, float fraction) : base(capi, bounds)
            {
                _fraction = fraction;
            }

            public override void ComposeElements(Context ctx, ImageSurface surface)
            {
                Bounds.CalcWorldBounds();
                // Matches GuiElementSlider's own thumb placement: 4 px padding,
                // a 15 px thumb, the centre of the thumb at this fraction.
                double padding = scaled(4);
                double handle = scaled(15);
                double travel = Bounds.InnerWidth - 2 * padding - handle / 2;
                double x = Bounds.drawX + travel * _fraction + handle / 2 + 2;
                double top = Bounds.drawY;
                double height = Math.Max(2, Bounds.InnerHeight);

                ctx.SetSourceRGBA(1, 1, 1, 0.55);
                ctx.MoveTo(x, top);
                ctx.LineTo(x + height, top + height);
                ctx.LineTo(x - height, top + height);
                ctx.ClosePath();
                ctx.Fill();
            }
        }
    }
}
