using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VintageVisuals.ColorGrade;
using VintageVisuals.Common;
using VintageVisuals.Common.Patching;

namespace VintageVisuals
{
    /// <summary>
    /// Mod entrypoint. Owns the config, the shader patch pipeline and the list
    /// of subsystems; contains no visual logic of its own.
    ///
    /// Startup order matters and is dictated by the ModSystem lifecycle:
    ///   StartPre        config (no assets needed yet)
    ///   AssetsLoaded    read the shader patch YAML — the first stage where
    ///                   api.Assets is safe to touch
    ///   StartClientSide install the shader hook and subsystems, before the
    ///                   game compiles its shaders
    /// </summary>
    public class VintageVisualsModSystem : ModSystem
    {
        private const string HarmonyId = "com.imperfectrain.vintagevisuals";
        private const string ReloadHotkeyCode = "vintagevisuals_reloadconfig";

        public ICoreClientAPI Capi { get; private set; }
        public ConfigManager ConfigManager { get; private set; }
        public ShaderPatcher ShaderPatcher { get; private set; }

        /// <summary>
        /// False when the shader source hook could not be installed. Subsystems
        /// must check this before reporting themselves active — without it
        /// their GLSL never reaches the game.
        /// </summary>
        public bool ShaderPatchingAvailable { get; private set; }

        private ShaderPatchLoader _patchLoader;
        private ShaderSourceInterceptor _interceptor;
        private readonly List<IVisualSubsystem> _subsystems = new List<IVisualSubsystem>();

        /// <summary>Client-only: this mod draws things, and the server draws nothing.</summary>
        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        public override void StartPre(ICoreAPI api)
        {
            Capi = api as ICoreClientAPI;
            if (Capi == null) return;

            ConfigManager = new ConfigManager(api, Mod.Logger);
            ConfigManager.Load();

            ShaderPatcher = new ShaderPatcher(Mod.Logger);
        }

        public override void AssetsLoaded(ICoreAPI api)
        {
            if (Capi == null) return;

            _patchLoader = new ShaderPatchLoader(Capi, Mod.Logger, Mod.Info.ModID);
            _patchLoader.LoadInto(ShaderPatcher);
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            _interceptor = new ShaderSourceInterceptor(HarmonyId, ShaderPatcher, Mod.Logger);
            _interceptor.SetShaderDumpEnabled(ConfigManager.Config.EnableShaderDebugDump);
            ShaderPatchingAvailable = _interceptor.Install();

            RegisterSubsystems();

            api.Input.RegisterHotKey(ReloadHotkeyCode, "Vintage Visuals: reload config",
                GlKeys.V, HotkeyType.GUIOrOtherControls, ctrlPressed: true);
            api.Input.SetHotKeyHandler(ReloadHotkeyCode, OnReloadHotkey);

            ConfigManager.ConfigChanged += ApplyToAllSubsystems;
            api.Event.ReloadShader += OnReloadShader;

            // Shaders have compiled by the time the texture atlases are ready,
            // so this is the first point at which uniforms can be uploaded and
            // the patch results are worth reporting.
            api.Event.BlockTexturesLoaded += OnShadersReady;
        }

        private void RegisterSubsystems()
        {
            _subsystems.Add(new ColorGradeSubsystem());

            // Phases 2-4 land here: Weather, Reflections, PseudoPBR. Each is
            // independently toggleable by design, so registration order carries
            // no meaning beyond log ordering.

            foreach (IVisualSubsystem subsystem in _subsystems)
            {
                try
                {
                    subsystem.Initialize(this);
                }
                catch (Exception ex)
                {
                    Mod.Logger.Error("[VintageVisuals] subsystem '" + subsystem.Name + "' failed to initialise " +
                                     "and will be inactive. The rest of the mod is unaffected.");
                    Mod.Logger.LogException(EnumLogType.Error, ex);
                }
            }
        }

        private void OnShadersReady()
        {
            ShaderPatcher.LogSummary();

            if (ShaderPatchingAvailable && ShaderPatcher.Groups.Count > 0)
            {
                bool anyApplied = false;
                foreach (ShaderPatchGroup group in ShaderPatcher.Groups)
                {
                    if (group.PatchedFiles.Count > 0) anyApplied = true;
                }

                if (!anyApplied)
                {
                    // Most likely cause: the game compiled its shaders before
                    // this mod installed the hook. Saying so beats leaving the
                    // user to guess why nothing looks different.
                    Mod.Logger.Warning("[VintageVisuals] the shader hook is installed but no vanilla shader has " +
                        "passed through it yet. If nothing looks different, reload shaders from the graphics " +
                        "settings menu; if that fixes it, this mod is loading too late and that is a bug worth reporting.");
                }
            }

            ApplyToAllSubsystems();
        }

        private bool OnReloadShader()
        {
            // Fired as the reload starts, so re-read the patch files from disk
            // first: a developer editing patch YAML expects a shader reload to
            // pick the edits up without restarting the game.
            ShaderPatcher.ResetRunState();
            if (_patchLoader != null) _patchLoader.LoadInto(ShaderPatcher);

            // GL uniform values are per-program state and are lost when the
            // program is relinked, so they must be re-uploaded — but only once
            // the reload has actually finished. Next tick is soon enough.
            Capi.Event.RegisterCallback(_ =>
            {
                ShaderPatcher.LogSummary();
                ApplyToAllSubsystems();
            }, 0);

            return true;
        }

        private bool OnReloadHotkey(KeyCombination combination)
        {
            ConfigManager.Reload();
            _interceptor.SetShaderDumpEnabled(ConfigManager.Config.EnableShaderDebugDump);
            return true;
        }

        private void ApplyToAllSubsystems()
        {
            foreach (IVisualSubsystem subsystem in _subsystems)
            {
                try
                {
                    subsystem.Apply();
                }
                catch (Exception ex)
                {
                    Mod.Logger.Error("[VintageVisuals] subsystem '" + subsystem.Name + "' threw while applying " +
                                     "config; other subsystems are unaffected.");
                    Mod.Logger.LogException(EnumLogType.Error, ex);
                }
            }
        }

        public override void Dispose()
        {
            foreach (IVisualSubsystem subsystem in _subsystems)
            {
                try
                {
                    subsystem.Dispose();
                }
                catch (Exception ex)
                {
                    Mod.Logger.Warning("[VintageVisuals] subsystem '" + subsystem.Name + "' threw on dispose: " + ex.Message);
                }
            }

            _subsystems.Clear();

            if (_interceptor != null) _interceptor.Uninstall();

            if (ConfigManager != null) ConfigManager.ConfigChanged -= ApplyToAllSubsystems;
            if (Capi != null)
            {
                Capi.Event.ReloadShader -= OnReloadShader;
                Capi.Event.BlockTexturesLoaded -= OnShadersReady;
            }

            Capi = null;
        }
    }
}
