using System;

namespace VintageVisuals.Ui
{
    /// <summary>Which tab a setting belongs to. The user-facing grouping, not the C# one.</summary>
    public enum SettingTab
    {
        Overview,
        Color,
        Materials,
        Weather,
        Atmosphere,
        Reflections,
        Debug,
    }

    /// <summary>What widget a setting needs.</summary>
    public enum SettingKind
    {
        Toggle,
        Slider,
        Dropdown,
    }

    /// <summary>
    /// What a setting costs, qualitatively.
    ///
    /// QUALITATIVE ON PURPOSE. This project has never measured a frame time, so
    /// a number here would be invented, and an invented number in a performance
    /// field is worse than no field - it is the one thing a player has no way to
    /// check. These three words are claims the code can actually support: a
    /// feature that allocates a framebuffer and captures every frame costs more
    /// than one that multiplies a uniform, and that ordering is knowable without
    /// a profiler.
    /// </summary>
    public enum SettingCost
    {
        Free,
        Low,
        Medium,
        High,
    }

    /// <summary>
    /// One tunable setting, as the UI needs to present it.
    ///
    /// THIS IS PRESENTATION METADATA AND HOLDS NO VALUES. The value lives in
    /// <see cref="VintageVisuals.Common.VintageVisualsConfig"/> and nowhere
    /// else; this record says only how to find it, what to call it, and what to
    /// tell the player it does. A UI that kept its own copy of a value is the
    /// same defect as a subsystem keeping its own copy of the weather, and this
    /// project has already paid for that one.
    ///
    /// <see cref="Path"/> is a dotted property path into the config object -
    /// "PseudoPBR.NormalStrength" - resolved by reflection at edit time. It is a
    /// string rather than a lambda pair for one reason: a string can be checked.
    /// A smoke check walks every entry in the registry, resolves the path, and
    /// fails on the first one that does not exist, so a renamed config property
    /// breaks the build's tests rather than one silent slider.
    /// </summary>
    public sealed class VisualSetting
    {
        /// <summary>
        /// Stable identifier, and deliberately the SAME code ConfigLib uses.
        ///
        /// The two UIs then describe one setting under one name, which is what
        /// lets a check compare this registry against
        /// assets/vintagevisuals/config/configlib-patches.json rather than
        /// trusting that two hand-maintained lists agree. Where a setting has no
        /// ConfigLib entry the code is still in the same shape, so adding one
        /// later needs no rename.
        /// </summary>
        public string Code { get; }

        /// <summary>Dotted property path into VintageVisualsConfig.</summary>
        public string Path { get; }

        /// <summary>What the player sees. Names the RESULT, not the variable.</summary>
        public string DisplayName { get; }

        /// <summary>
        /// One or two sentences answering "what does this visibly change?".
        ///
        /// Not "what variable does this modify". "Multiplies normal strength" is
        /// a fact about the code; "controls how strongly small surface details
        /// affect the shape and lighting of blocks" is a fact about the screen,
        /// and only one of them helps someone holding a mouse.
        /// </summary>
        public string Description { get; }

        public SettingTab Tab { get; }

        /// <summary>Heading within the tab, e.g. "Foliage". Free text, ordered by registration.</summary>
        public string Section { get; }

        public SettingKind Kind { get; }

        public float Min { get; }
        public float Max { get; }
        public float Step { get; }

        /// <summary>"blocks", "s", or null. Shown after the value.</summary>
        public string Unit { get; }

        /// <summary>Hidden behind the Advanced toggle. Never hidden outright.</summary>
        public bool Advanced { get; }

        public SettingCost Cost { get; }

        /// <summary>
        /// Changing this makes the mod rebuild shaders, so it cannot be dragged
        /// smoothly and the UI says so.
        ///
        /// The UI does NOT perform the reload. VintageVisualsModSystem already
        /// detects a patch-gating change on the config-changed path and
        /// schedules it; a dialog calling ReloadShaders() from a click handler
        /// is precisely the shader-delivery class of bug this project has spent
        /// three rounds on.
        /// </summary>
        public bool RequiresShaderReload { get; }

        /// <summary>A developer diagnostic. Lives on the Debug tab and never in a shipped preset.</summary>
        public bool DebugOnly { get; }

        /// <summary>Labels for a dropdown, index-aligned to the stored value. Null otherwise.</summary>
        public string[] Choices { get; }

        public VisualSetting(string code, string path, string displayName, string description,
                             SettingTab tab, string section, SettingKind kind,
                             float min = 0f, float max = 1f, float step = 0.01f,
                             string unit = null, bool advanced = false,
                             SettingCost cost = SettingCost.Free,
                             bool requiresShaderReload = false, bool debugOnly = false,
                             string[] choices = null)
        {
            Code = code;
            Path = path;
            DisplayName = displayName;
            Description = description;
            Tab = tab;
            Section = section;
            Kind = kind;
            Min = min;
            Max = max;
            Step = step;
            Unit = unit;
            Advanced = advanced;
            Cost = cost;
            RequiresShaderReload = requiresShaderReload;
            DebugOnly = debugOnly;
            Choices = choices;
        }

        /// <summary>
        /// How many decimals to show, derived from the step rather than chosen.
        ///
        /// A step of 1 shows "48", a step of 0.05 shows "0.35", and nothing ever
        /// shows "0.349999994". Deriving it means a retuned step cannot leave a
        /// display precision behind that no longer matches it.
        /// </summary>
        public int DisplayDecimals
        {
            get
            {
                if (Step >= 1f) return 0;
                if (Step >= 0.1f) return 1;
                if (Step >= 0.01f) return 2;
                return 3;
            }
        }

        /// <summary>The value as the player should read it, units included.</summary>
        public string Format(float value)
        {
            string number = value.ToString("F" + DisplayDecimals);
            return Unit == null ? number : number + " " + Unit;
        }
    }
}
