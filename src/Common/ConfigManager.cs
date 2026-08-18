using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace VintageVisuals.Common
{
    /// <summary>
    /// Owns the config instance, its file, and the "config changed" signal.
    ///
    /// Backed by the vanilla <c>LoadModConfig</c>/<c>StoreModConfig</c> API,
    /// which is the always-available path and needs no other mod installed.
    ///
    /// <see cref="ConfigLibBridge"/> layers an optional in-game GUI on top by
    /// writing into this same object and calling <see cref="NotifyChanged"/>.
    /// It is an additional writer, not a second store: subsystems only ever see
    /// <see cref="Config"/> plus <see cref="ConfigChanged"/>, and neither knows
    /// nor cares which writer moved the value.
    /// </summary>
    public sealed class ConfigManager
    {
        public const string ConfigFilename = "vintagevisuals.json";

        private readonly ICoreAPI _api;
        private readonly ILogger _logger;

        /// <summary>
        /// Raised after the config is reloaded from disk. Subsystems re-read
        /// <see cref="Config"/> and re-upload their uniforms; nothing caches
        /// config values across this event.
        /// </summary>
        public event Action ConfigChanged;

        public VintageVisualsConfig Config { get; private set; }

        public ConfigManager(ICoreAPI api, ILogger logger)
        {
            _api = api;
            _logger = logger;
            Config = new VintageVisualsConfig();
        }

        /// <summary>
        /// Reads the config file, creating it with defaults if absent. Never
        /// throws: an unreadable config falls back to defaults so a stray comma
        /// cannot stop the game from starting.
        /// </summary>
        public void Load()
        {
            VintageVisualsConfig loaded = null;

            try
            {
                loaded = _api.LoadModConfig<VintageVisualsConfig>(ConfigFilename);
            }
            catch (Exception ex)
            {
                _logger.Error("[VintageVisuals] could not parse ModConfig/" + ConfigFilename +
                              ", falling back to defaults. Fix or delete the file to stop seeing this. " + ex.Message);
            }

            if (loaded == null)
            {
                Config = new VintageVisualsConfig();
                _logger.Notification("[VintageVisuals] no config found, writing defaults to ModConfig/" + ConfigFilename);
                Save();
            }
            else
            {
                Config = loaded;
            }

            foreach (string correction in Config.ClampToValidRanges())
            {
                _logger.Warning("[VintageVisuals] " + correction);
            }
        }

        public void Save()
        {
            try
            {
                _api.StoreModConfig(Config, ConfigFilename);
            }
            catch (Exception ex)
            {
                _logger.Error("[VintageVisuals] could not write ModConfig/" + ConfigFilename + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Re-reads the file and notifies subsystems. Bound to a hotkey so
        /// tuning a look does not need a world reload — the whole point of the
        /// Phase 1 milestone is being able to see a value change take effect.
        /// </summary>
        public void Reload()
        {
            Load();
            NotifyChanged();
            _logger.Notification("[VintageVisuals] config reloaded from ModConfig/" + ConfigFilename);
        }

        /// <summary>
        /// Tells subsystems the in-memory config changed, without re-reading
        /// the file.
        ///
        /// Exists so every writer - the hotkey reload, and the optional
        /// ConfigLib GUI bridge - converges on one notification path. A second
        /// way to push values into the render pipeline is how the two drift out
        /// of sync.
        /// </summary>
        public void NotifyChanged()
        {
            try
            {
                ConfigChanged?.Invoke();
            }
            catch (Exception ex)
            {
                // One misbehaving subsystem must not stop the others from
                // picking up the new values.
                _logger.Error("[VintageVisuals] a subsystem threw while applying the changed config.");
                _logger.LogException(EnumLogType.Error, ex);
            }
        }
    }
}
