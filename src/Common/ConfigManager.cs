using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace VintageVisuals.Common
{
    /// <summary>
    /// Owns the config instance, its file, and the "config changed" signal.
    ///
    /// Backed by the vanilla <c>LoadModConfig</c>/<c>StoreModConfig</c> API
    /// rather than ConfigLib. ConfigLib gives a nicer in-game GUI, but making
    /// it a hard dependency means this mod cannot load without it, and the
    /// plan's own module-boundary rule is that subsystems degrade gracefully.
    /// A ConfigLib bridge can be layered on top of this later without changing
    /// how subsystems read values — they only ever see
    /// <see cref="Config"/> plus <see cref="ConfigChanged"/>.
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

            try
            {
                ConfigChanged?.Invoke();
            }
            catch (Exception ex)
            {
                // One misbehaving subsystem must not stop the others from
                // picking up the new values.
                _logger.Error("[VintageVisuals] a subsystem threw while applying the reloaded config.");
                _logger.LogException(EnumLogType.Error, ex);
            }

            _logger.Notification("[VintageVisuals] config reloaded from ModConfig/" + ConfigFilename);
        }
    }
}
