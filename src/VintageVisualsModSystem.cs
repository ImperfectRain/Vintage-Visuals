using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VintageVisuals.ColorGrade;
using VintageVisuals.Common;
using VintageVisuals.Common.Patching;
using VintageVisuals.PseudoPBR;

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
        private const string HarmonyIdValue = "com.imperfectrain.vintagevisuals";

        /// <summary>Shared so subsystems installing their own hooks stay under one id to unpatch.</summary>
        public string HarmonyId { get { return HarmonyIdValue; } }
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

        /// <summary>Guards the one self-inflicted shader reload, so it can never loop.</summary>
        private bool _forcedShaderReload;

        /// <summary>
        /// Whether the last shader load applied the pseudopbr group, so a
        /// change to that flag can be turned into a shader reload rather than
        /// silently waiting for the next one.
        /// </summary>
        private bool _pseudoPbrPatchesApplied;

        /// <summary>Null when ConfigLib is not installed. Optional by design.</summary>
        private ConfigLibBridge _configLibBridge;

        // Uniform upload needs the vanilla program to exist and be compiled.
        // Exactly when that becomes true varies with load order and machine
        // speed, and getting it wrong is silent - an unset GLSL uniform reads
        // as zero, which this mod's shader treats as "disabled". Rather than
        // guess one correct moment, apply repeatedly over a short window and
        // let the one-shot warnings keep the log quiet. Apply() is idempotent
        // and costs a handful of uniform writes, so this is cheap insurance.
        private const int ApplyRetries = 5;
        private const int ApplyRetryIntervalMs = 500;

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
            _patchLoader.LoadInto(ShaderPatcher, IsPatchGroupEnabled);
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            _interceptor = new ShaderSourceInterceptor(HarmonyIdValue, ShaderPatcher, Mod.Logger);
            _interceptor.SetShaderDumpEnabled(ConfigManager.Config.EnableShaderDebugDump);
            ShaderPatchingAvailable = _interceptor.Install();

            RegisterSubsystems();

            api.Input.RegisterHotKey(ReloadHotkeyCode, "Vintage Visuals: reload config",
                GlKeys.V, HotkeyType.GUIOrOtherControls, ctrlPressed: true);
            api.Input.SetHotKeyHandler(ReloadHotkeyCode, OnReloadHotkey);

            ConfigManager.ConfigChanged += ApplyToAllSubsystems;
            api.Event.ReloadShader += OnReloadShader;

            InstallConfigLibBridge(api);

            // Shaders have compiled by the time the texture atlases are ready,
            // so this is the first point at which uniforms can be uploaded and
            // the patch results are worth reporting.
            api.Event.BlockTexturesLoaded += OnShadersReady;
        }

        /// <summary>
        /// Wires up the optional in-game settings GUI.
        ///
        /// ConfigLib is a soft dependency and is deliberately absent from
        /// modinfo.json: a modinfo dependency is hard, and would stop this mod
        /// loading at all for anyone without ConfigLib installed.
        ///
        /// The IsModEnabled check is belt-and-braces rather than load-bearing.
        /// ConfigLibBridge references no ConfigLib types at all - it listens on
        /// the game's own event bus - so with ConfigLib absent the listeners
        /// would simply never fire. The check earns its place by keeping the
        /// log honest about which mode is active, which is the first thing
        /// worth knowing when a slider does not do anything.
        /// </summary>
        private void InstallConfigLibBridge(ICoreClientAPI api)
        {
            if (!api.ModLoader.IsModEnabled(ConfigLibBridge.ConfigLibModId))
            {
                Mod.Logger.VerboseDebug("[VintageVisuals] ConfigLib not installed — settings come from " +
                    "ModConfig/vintagevisuals.json, reloadable with Ctrl+V.");
                return;
            }

            try
            {
                _configLibBridge = new ConfigLibBridge(this);
                _configLibBridge.Install();
                Mod.Logger.VerboseDebug("[VintageVisuals] ConfigLib detected — live settings GUI available (F7). " +
                    "ModConfig/vintagevisuals.json and Ctrl+V keep working alongside it.");
            }
            catch (Exception ex)
            {
                _configLibBridge = null;
                Mod.Logger.Warning("[VintageVisuals] ConfigLib bridge failed to install; falling back to " +
                    "ModConfig/vintagevisuals.json + Ctrl+V. " + ex.Message);
            }
        }

        /// <summary>
        /// Whether a patch group's GLSL should be applied at all.
        ///
        /// Only groups that can cost the player something they cannot switch
        /// off are listed. ColorGrade patches unconditionally: its uniform
        /// defaults to "disabled", and a no-op grading function on final.fsh is
        /// harmless. PseudoPBR patches chunkopaque.fsh, which draws the world —
        /// so if the player turns it off, the honest response is to hand the
        /// compiler vanilla source, not patched source with the effect muted.
        /// </summary>
        private bool IsPatchGroupEnabled(string group)
        {
            if (group == PseudoPbrSubsystem.GroupName)
            {
                return ConfigManager.Config.PseudoPBR.Enabled;
            }

            return true;
        }

        /// <summary>
        /// Reloads shaders when the PseudoPBR flag has been toggled since the
        /// last shader load.
        ///
        /// Every other setting in this mod is a uniform, and uniforms take
        /// effect the moment they are uploaded. This one decides whether GLSL
        /// is injected at all, so it needs the shaders rebuilt — otherwise
        /// unticking the box in the GUI would appear to do nothing until the
        /// next graphics setting change, which is exactly the wrong behaviour
        /// for what is meant to be an escape hatch.
        /// </summary>
        private void ReloadShadersIfPatchGatingChanged()
        {
            bool wanted = ConfigManager.Config.PseudoPBR.Enabled;
            if (wanted == _pseudoPbrPatchesApplied) return;

            _pseudoPbrPatchesApplied = wanted;

            Mod.Logger.Notification("[VintageVisuals] PseudoPBR.Enabled is now " + wanted +
                "; reloading shaders so chunkopaque.fsh is rebuilt " +
                (wanted ? "with the patch." : "from vanilla source."));

            // Re-derive the patch set BEFORE asking for the reload, not from
            // inside the ReloadShader handler. The game recompiles its shaders
            // before that event reaches us, so a gating change made there
            // applies to the reload after the one it asked for — the log showed
            // the compile happening, then "Skipped pseudopbr" arriving 40 lines
            // later. One toggle then took two reloads to take effect, which is
            // exactly the kind of off-by-one that makes an A/B test lie.
            ShaderPatcher.ResetRunState();
            if (_patchLoader != null) _patchLoader.LoadInto(ShaderPatcher, IsPatchGroupEnabled);

            Capi.Event.RegisterCallback(_ => Capi.Shader.ReloadShaders(), 0);
        }

        private void RegisterSubsystems()
        {
            _subsystems.Add(new ColorGradeSubsystem());
            _subsystems.Add(new PseudoPbrSubsystem());

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
            bool anyApplied = false;
            foreach (ShaderPatchGroup group in ShaderPatcher.Groups)
            {
                if (group.PatchedFiles.Count > 0) anyApplied = true;
            }

            // The game compiles its shaders during the pre-mod main-menu
            // bootstrap, before any ModSystem exists and therefore before the
            // interceptor is installed. Its later "reload with mod assets" pass
            // does not route already-cached programs back through LoadShader,
            // so without this the hook is installed correctly and simply never
            // sees the one program we care about.
            //
            // Rather than fight that caching, ask for a reload we control. Once
            // only, and guarded, because ReloadShaders() raises the very event
            // whose handler schedules the re-apply.
            if (ShaderPatchingAvailable && ShaderPatcher.Groups.Count > 0 && !anyApplied && !_forcedShaderReload)
            {
                _forcedShaderReload = true;
                Mod.Logger.Notification("[VintageVisuals] no vanilla shader has passed through the hook yet — " +
                    "the game compiled its shaders before this mod loaded. Forcing one shader reload so the " +
                    "patches reach the running programs.");

                // Deferred a tick: ReloadShaders() re-enters shader loading, and
                // doing that from inside an asset-load event is asking for trouble.
                Capi.Event.RegisterCallback(_ => Capi.Shader.ReloadShaders(), 0);
                return;
            }

            ScheduleApplyRetries();
        }

        /// <summary>
        /// Applies now and again a few times over the next couple of seconds.
        /// See the ApplyRetries comment for why a single shot is not enough.
        /// </summary>
        private void ScheduleApplyRetries()
        {
            ApplyToAllSubsystems();

            for (int attempt = 1; attempt <= ApplyRetries; attempt++)
            {
                bool last = attempt == ApplyRetries;
                Capi.Event.RegisterCallback(_ =>
                {
                    ApplyToAllSubsystems();

                    // The summary is logged HERE rather than when the reload is
                    // requested. The game recompiles its shaders after our
                    // reload handler returns, so a summary taken at request
                    // time always reports "loaded but not applied to any shader
                    // yet" — which it did, 52 times in one session, for groups
                    // that had in fact applied perfectly. A status line that is
                    // wrong in the normal case is worse than none: it sent this
                    // project looking for a patch failure that never happened.
                    if (last) ShaderPatcher.LogSummary();
                }, attempt * ApplyRetryIntervalMs);
            }
        }

        private bool OnReloadShader()
        {
            // Fired as the reload starts, so re-read the patch files from disk
            // first: a developer editing patch YAML expects a shader reload to
            // pick the edits up without restarting the game.
            ShaderPatcher.ResetRunState();
            if (_patchLoader != null) _patchLoader.LoadInto(ShaderPatcher, IsPatchGroupEnabled);

            // GL uniform values are per-program state and are lost when the
            // program is relinked, so they must be re-uploaded — but only once
            // the reload has actually finished. Next tick is soon enough.
            Capi.Event.RegisterCallback(_ => ScheduleApplyRetries(), 0);

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
            ReloadShadersIfPatchGatingChanged();

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
