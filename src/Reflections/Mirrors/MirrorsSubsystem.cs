using System;
using Vintagestory.API.Client;
using VintageVisuals.Common;
using VintageVisuals.PseudoPBR;

namespace VintageVisuals.Reflections.Mirrors
{
    /// <summary>
    /// Player-marked mirror surfaces (D97, experimental prototype).
    ///
    /// Owns the three parts and nothing else: the store (which surfaces are
    /// mirrors, per world, plus the player's mirror settings), the selector
    /// (the hotkeys and '.vvmirror'), and the renderer (the mirror cameras).
    /// The terrain shader half is the pbrterrainmirror patch group, which
    /// exists in source only while this world has a mirror and mirrors are on,
    /// so the cost of the feature in a world without mirrors is one dictionary
    /// lookup per frame and no GLSL at all.
    ///
    /// Settings live in their own file (vintagevisuals-mirrors.json) rather than
    /// the mod's config while this is a prototype: nothing in the F7 panel,
    /// the tuning studio or the presets needs to know it exists yet.
    /// </summary>
    public sealed class MirrorsSubsystem : IVisualSubsystem
    {
        public const string SubsystemName = "mirrors";
        public const string GroupName = "pbrterrainmirror";

        private VintageVisualsModSystem _mod;
        private ICoreClientAPI _capi;
        private MirrorStore _store;
        private MirrorSelector _selector;
        private MirrorRenderer _renderer;
        private bool _registered;
        private bool _worldTried;

        public string Name => SubsystemName;

        /// <summary>Whether the terrain shader should carry the mirror group: mirrors on, and at least one in this world.</summary>
        public bool ShaderWanted => _store != null && _store.Settings.Enabled && _store.HasMirrors;

        public void Initialize(VintageVisualsModSystem mod)
        {
            _mod = mod;
            _capi = mod.Capi;
            _store = new MirrorStore(_capi);
            _store.LoadSettings();

            MirrorRenderer.ModSystemCapi = _capi;
            _renderer = new MirrorRenderer(_capi, _store, CaptureValid, mod.HarmonyId);
            _capi.Event.RegisterRenderer(_renderer, EnumRenderStage.Opaque, "vintagevisuals-mirrors");
            _registered = true;

            _selector = new MirrorSelector(_capi, _store, () => _renderer.Stats(), OnSettingsChanged);
            _selector.Register();

            _store.Changed += OnStoreChanged;
            _capi.Event.ReloadShader += OnShadersReloaded;
            _capi.Event.LevelFinalize += TryLoadWorld;
            TryLoadWorld();
        }

        public void Apply()
        {
            if (_renderer == null) return;
            // Without a terrain shader that reads the atlas, a pass is a picture
            // nobody sees.
            _renderer.Allowed = _mod.ConfigManager.Config.PseudoPBR.Enabled
                             && PseudoPbrSubsystem.TerrainMaterialPrimaryPatchEnabled;
            TryLoadWorld();
        }

        private void TryLoadWorld()
        {
            if (_store == null || _store.WorldLoaded) return;
            if (_store.TryLoadWorld()) _mod.RequestPatchGatingCheck();
            else if (!_worldTried) _worldTried = true;
        }

        private bool CaptureValid()
        {
            SceneCaptureRenderer c = _mod.Reflections?.Capture;
            return c != null && c.HasCapture && c.TextureId != 0;
        }

        private void OnStoreChanged()
        {
            // The first mirror placed, or the last removed, decides whether the
            // shader group exists; the mod reloads shaders when that flips.
            _mod.RequestPatchGatingCheck();
        }

        private void OnSettingsChanged()
        {
            _mod.RequestPatchGatingCheck();
        }

        private bool OnShadersReloaded()
        {
            _renderer?.OnShadersReloaded();
            return true;
        }

        public void Dispose()
        {
            if (_capi != null)
            {
                _capi.Event.ReloadShader -= OnShadersReloaded;
                _capi.Event.LevelFinalize -= TryLoadWorld;
                if (_registered && _renderer != null) _capi.Event.UnregisterRenderer(_renderer, EnumRenderStage.Opaque);
            }
            if (_store != null) _store.Changed -= OnStoreChanged;
            _renderer?.Dispose();
            _renderer = null;
            _registered = false;
        }
    }
}
