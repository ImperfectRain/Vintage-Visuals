using Vintagestory.API.Client;
using VintageVisuals.Common;

namespace VintageVisuals.Water
{
    /// <summary>
    /// The water surface: patch group "water" on vanilla's chunkliquid program.
    ///
    /// Owns one renderer, the uniform binder. The patch group itself is gated
    /// in <c>VintageVisualsModSystem.IsPatchGroupEnabled</c> on
    /// <see cref="WaterConfig.Enabled"/>, so off is vanilla's liquid source and
    /// not this code multiplied by zero.
    ///
    /// Reads the world only through <c>EnvironmentTracker</c> (EnvironmentState
    /// and AtmosphereState), never directly - see CLAUDE.md, "A new subsystem
    /// reads SceneIntent, it does not re-derive the world".
    /// </summary>
    public sealed class WaterSubsystem : IVisualSubsystem
    {
        public const string GroupName = "water";

        /// <summary>The scene reflection and refraction group (D82): it declares the sampler.</summary>
        public const string SceneGroupName = "waterscene";

        /// <summary>D93. Defines VV_WATER_CAUSTICS, so the caustic web compiles in only while Water.Caustics is above 0.</summary>
        public const string CausticsGroupName = "watercaustics";

        private VintageVisualsModSystem _mod;
        private WaterShaderBinder _binder;
        private WaterSceneBindInterceptor _sceneBind;

        public string Name { get { return GroupName; } }

        public void Initialize(VintageVisualsModSystem mod)
        {
            _mod = mod;
            if (mod == null || mod.Capi == null) return;

            // Installed whatever the config says: the hook is inert until the
            // binder publishes a texture, and the binder publishes one only
            // while the waterscene group reached chunkliquid.
            _sceneBind = new WaterSceneBindInterceptor(mod.HarmonyId, mod.Mod.Logger);
            _sceneBind.Install();

            _binder = new WaterShaderBinder(mod.Capi, mod);
            VintageVisuals.Common.Diagnostics.Profiling.ModRenderers.Register(mod.Capi, _binder, EnumRenderStage.Before, "vintagevisuals-water-uniforms");
        }

        public void Apply()
        {
            // Nothing cached: the binder reads config and world on every frame.
        }

        public void Dispose()
        {
            if (_binder != null && _mod != null && _mod.Capi != null)
            {
                VintageVisuals.Common.Diagnostics.Profiling.ModRenderers.Unregister(_mod.Capi, _binder, EnumRenderStage.Before);
            }
            _binder = null;
            if (_sceneBind != null) _sceneBind.Uninstall();
            _sceneBind = null;
        }
    }
}
