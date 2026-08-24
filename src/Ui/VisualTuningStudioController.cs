using System;
using System.Collections.Generic;
using System.Linq;
using VintageVisuals.Common;

namespace VintageVisuals.Ui
{
    /// <summary>
    /// The live-editing boundary for the native tuning dialog.
    ///
    /// Widgets do not know about subsystems, uniforms, shader reloads or files.
    /// They ask this controller to mutate one config path, and this controller
    /// calls the same ConfigManager notification path every other writer uses.
    /// </summary>
    public sealed class VisualTuningStudioController
    {
        private readonly Func<VintageVisualsConfig> _config;
        private readonly Action _notifyChanged;

        public VisualTuningStudioController(Func<VintageVisualsConfig> config, Action notifyChanged)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _notifyChanged = notifyChanged ?? throw new ArgumentNullException(nameof(notifyChanged));
        }

        public VintageVisualsConfig Config { get { return _config(); } }

        public float Value(VisualSetting setting)
        {
            return ConfigAccess.Get(Config, setting.Path);
        }

        public string StringValue(VisualSetting setting)
        {
            return ConfigAccess.GetString(Config, setting.Path);
        }

        public string DisplayValue(VisualSetting setting)
        {
            if (setting.Kind == SettingKind.Dropdown) return StringValue(setting) ?? "";
            if (setting.Kind == SettingKind.Toggle) return Value(setting) >= 0.5f ? "On" : "Off";
            return setting.Format(Value(setting));
        }

        public bool IsModified(VisualSetting setting)
        {
            return ConfigAccess.IsModified(Config, setting.Path);
        }

        public bool Set(VisualSetting setting, float value)
        {
            VintageVisualsConfig config = Config;
            float before = ConfigAccess.Get(config, setting.Path);
            if (!ConfigAccess.Set(config, setting.Path, value)) return false;

            config.ClampToValidRanges();
            float after = ConfigAccess.Get(config, setting.Path);
            if (Math.Abs(after - before) <= 1e-6f) return false;

            _notifyChanged();
            return true;
        }

        public bool SetString(VisualSetting setting, string value)
        {
            VintageVisualsConfig config = Config;
            string before = ConfigAccess.GetString(config, setting.Path);
            if (!ConfigAccess.SetString(config, setting.Path, value)) return false;

            config.ClampToValidRanges();
            string after = ConfigAccess.GetString(config, setting.Path);
            if (string.Equals(before, after, StringComparison.Ordinal)) return false;

            _notifyChanged();
            return true;
        }

        public bool Reset(VisualSetting setting)
        {
            if (!IsModified(setting)) return false;

            if (!ConfigAccess.ResetToDefault(Config, setting.Path)) return false;
            Config.ClampToValidRanges();
            _notifyChanged();
            return true;
        }

        public int ResetTab(SettingTab tab, bool advanced)
        {
            int changed = 0;
            foreach (VisualSetting setting in VisibleSettings(tab, advanced))
            {
                if (!IsModified(setting)) continue;
                if (ConfigAccess.ResetToDefault(Config, setting.Path)) changed++;
            }

            if (changed == 0) return 0;

            Config.ClampToValidRanges();
            _notifyChanged();
            return changed;
        }

        public IEnumerable<VisualSetting> VisibleSettings(SettingTab tab, bool advanced)
        {
            return VisualSettingRegistry.ForTab(tab)
                .Where(s => advanced || !s.Advanced)
                .Where(s => !s.DebugOnly || tab == SettingTab.Debug);
        }

        public bool SetDebugView(DebugView view)
        {
            if (view == null) return false;

            VintageVisualsConfig config = Config;
            float before = ConfigAccess.Get(config, view.OwnerPath);
            if (!ConfigAccess.Set(config, view.OwnerPath, view.Value)) return false;

            config.ClampToValidRanges();
            float after = ConfigAccess.Get(config, view.OwnerPath);
            if (Math.Abs(after - before) <= 1e-6f) return false;

            _notifyChanged();
            return true;
        }

        public bool ReturnToNormalRendering()
        {
            bool changed = false;

            foreach (string owner in DebugViewRegistry.All.Select(v => v.OwnerPath).Distinct())
            {
                if (Math.Abs(ConfigAccess.Get(Config, owner)) <= 1e-6f) continue;
                ConfigAccess.Set(Config, owner, 0f);
                changed = true;
            }

            VisualSetting wipe = VisualSettingRegistry.ByCode("compare_wipe");
            if (wipe != null && Math.Abs(ConfigAccess.Get(Config, wipe.Path)) > 1e-6f)
            {
                ConfigAccess.Set(Config, wipe.Path, 0f);
                changed = true;
            }

            if (!changed) return false;

            Config.ClampToValidRanges();
            _notifyChanged();
            return true;
        }

        /// <summary>Refresh is intentionally read-only; ConfigLib can move values while the dialog is open.</summary>
        public IReadOnlyDictionary<string, string> Snapshot(IEnumerable<VisualSetting> settings)
        {
            return settings.ToDictionary(s => s.Code, DisplayValue, StringComparer.Ordinal);
        }
    }
}
