using System;
using System.Collections.Generic;
using Vintagestory.API.Client;

namespace VintageVisuals.Ui
{
    /// <summary>One saved look: every tuning value, by setting code, and when it was saved.</summary>
    public sealed class SavedLook
    {
        public string SavedAt { get; set; } = "";
        public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>The file behind the panel's Saved looks slots.</summary>
    public sealed class SavedLooksFile
    {
        public int Version { get; set; } = 1;
        public List<SavedLook> Slots { get; set; } = new List<SavedLook>();
    }

    /// <summary>
    /// The player's own saved looks: three slots in ModConfig/vintagevisuals-looks.json.
    ///
    /// A FILE OF ITS OWN, not part of the mod config. A look is a copy of the
    /// config's tuning values; keeping it inside the config would make loading
    /// the config also load a second, older copy of itself. The values go back
    /// through VisualTuningStudioController.ApplyLook, which clamps them and
    /// notifies once, exactly like any other edit - the file never reaches the
    /// renderer by any other route.
    /// </summary>
    public sealed class SavedLooks
    {
        public const string FileName = "vintagevisuals-looks.json";
        public const int SlotCount = 3;

        private readonly ICoreClientAPI _capi;
        private SavedLooksFile _file;

        public SavedLooks(ICoreClientAPI capi)
        {
            _capi = capi;
        }

        private SavedLooksFile File
        {
            get
            {
                if (_file != null) return _file;
                try { _file = _capi.LoadModConfig<SavedLooksFile>(FileName); }
                catch (Exception ex)
                {
                    _capi.Logger.Warning("[VintageVisuals] could not read " + FileName + ": " + ex.Message);
                }
                if (_file == null) _file = new SavedLooksFile();
                while (_file.Slots.Count < SlotCount) _file.Slots.Add(null);
                return _file;
            }
        }

        /// <summary>The look in a slot (0-based), or null if the slot is empty.</summary>
        public SavedLook Get(int slot)
        {
            if (slot < 0 || slot >= SlotCount) return null;
            SavedLook look = File.Slots[slot];
            return look != null && look.Values != null && look.Values.Count > 0 ? look : null;
        }

        public bool Save(int slot, Dictionary<string, string> values)
        {
            if (slot < 0 || slot >= SlotCount || values == null) return false;
            File.Slots[slot] = new SavedLook
            {
                SavedAt = DateTime.Now.ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                Values = new Dictionary<string, string>(values, StringComparer.Ordinal),
            };
            try
            {
                _capi.StoreModConfig(File, FileName);
                return true;
            }
            catch (Exception ex)
            {
                _capi.Logger.Warning("[VintageVisuals] could not write " + FileName + ": " + ex.Message);
                return false;
            }
        }
    }
}
