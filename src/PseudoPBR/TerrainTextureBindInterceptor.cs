using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VintageVisuals.Reflections;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Binds the matching material atlas page whenever the game binds a block
    /// texture atlas page.
    ///
    /// This exists because of a question the shader cannot answer. Our atlas is
    /// sampled with the diffuse's own <c>uv</c>, which only works while both
    /// atlases share a layout — and once the game needs more than one page it
    /// rebinds <c>terrainTex</c> per draw call, with <c>uv</c> saying nothing
    /// about which page a fragment belongs to. There is no room left in the
    /// vertex format to tell us (VertexFlags is fully packed), and a renderer
    /// running once per frame cannot see individual draw calls.
    ///
    /// The one moment the answer exists is the instant vanilla selects a page.
    /// So we hook exactly that: when something binds a texture id we recognise
    /// as a block atlas page, we bind our derived page for it alongside, into
    /// the same program that is about to draw with it.
    ///
    /// <see cref="ShaderProgramBase"/> is <c>VintagestoryLib</c> internals and
    /// therefore not a stable API. Resolution is by name at runtime and by
    /// method shape, the patch is a postfix rather than a transpiler, and a
    /// failure to install downgrades the subsystem to single-page rather than
    /// throwing — the same contract as ShaderSourceInterceptor.
    /// </summary>
    public sealed class TerrainTextureBindInterceptor
    {
        private const string ShaderProgramBaseTypeName = "Vintagestory.Client.NoObf.ShaderProgramBase";
        private const string BindMethodName = "BindTexture2D";

        /// <summary>Vanilla's block atlas sampler. The name we are watching for.</summary>
        private const string TerrainSampler = "terrainTex";

        // Static because Harmony patches must be. There is one client and one
        // instance of this mod per process, which is what makes that safe.
        private static readonly Dictionary<int, int> MaterialPageByAtlasTexture = new Dictionary<int, int>();
        
        /// <summary>Second material page per atlas texture. May be empty; see SetPages.</summary>
        private static readonly Dictionary<int, int> SecondPageByAtlasTexture = new Dictionary<int, int>();

        /// <summary>
        /// The captured scene texture, rebound alongside the material atlases.
        ///
        /// It has to be rebound HERE, per draw call, for exactly the reason the
        /// atlases do. A texture unit is global GL state, not per-program: the
        /// binder sets it once at the start of the frame, and anything the game
        /// binds afterwards - GUI, held item, another atlas page - replaces
        /// whatever was on that unit before the terrain chunks are drawn.
        ///
        /// This was diagnosed from debug view 41, which showed the block atlas
        /// and pieces of unrelated textures where the captured frame should have
        /// been, while view 39 reported valid hits: the reflection was sampling
        /// whatever happened to be on unit 13 at draw time and finding alpha
        /// values that passed the depth test by chance. A per-frame bind and a
        /// per-draw bind look identical in every static test and completely
        /// different on screen.
        /// </summary>
        private static volatile int _captureTextureId;
        private static volatile int _worldReflectionTextureId;
        private static ILogger _logger;
        private static volatile bool _active;

        /// <summary>
        /// Guards against the postfix seeing its own BindTexture2D call. Thread
        /// static rather than a plain field: rendering is one thread today, and
        /// a mod that assumes so is a mod that breaks quietly if that changes.
        /// </summary>
        [ThreadStatic]
        private static bool _reentrant;

        private readonly Harmony _harmony;
        private readonly ILogger _instanceLogger;
        private bool _installed;

        public TerrainTextureBindInterceptor(string harmonyId, ILogger logger)
        {
            _harmony = new Harmony(harmonyId + ".terrainbind");
            _instanceLogger = logger;
            _logger = logger;
        }

        public bool Installed
        {
            get { return _installed; }
        }

        /// <summary>
        /// Installs the hook. Returns false — having logged why — if the game's
        /// internals no longer look the way this expects. The caller must then
        /// restrict itself to a single-page atlas.
        /// </summary>
        public bool Install()
        {
            if (_installed) return true;

            Type type = AccessTools.TypeByName(ShaderProgramBaseTypeName);
            if (type == null)
            {
                _instanceLogger.Warning("[VintageVisuals] pseudopbr: cannot find " + ShaderProgramBaseTypeName +
                    ". Multi-page block atlases are not supported on this build; surface relief will only run " +
                    "on a single-page atlas. Nothing else is affected.");
                return false;
            }

            List<MethodInfo> targets = FindBindMethods(type);
            if (targets.Count == 0)
            {
                _instanceLogger.Warning("[VintageVisuals] pseudopbr: cannot find a suitable " + BindMethodName +
                    "(string, int, ..) on " + ShaderProgramBaseTypeName + ". Multi-page block atlases are not " +
                    "supported on this build; surface relief will only run on a single-page atlas.");
                return false;
            }

            try
            {
                var postfix = new HarmonyMethod(
                    AccessTools.Method(typeof(TerrainTextureBindInterceptor), nameof(BindTexture2DPostfix)));

                foreach (MethodInfo target in targets) _harmony.Patch(target, postfix: postfix);

                _installed = true;
                _instanceLogger.Notification("[VintageVisuals] pseudopbr: terrain texture bind hook installed on " +
                    targets.Count + " " + BindMethodName + " overload(s); multi-page block atlases supported.");
                return true;
            }
            catch (Exception ex)
            {
                _instanceLogger.Warning("[VintageVisuals] pseudopbr: could not install the terrain texture bind " +
                    "hook, so surface relief is limited to a single-page atlas. " + ex.Message);
                return false;
            }
        }

        public void Uninstall()
        {
            SetPages(null);

            if (!_installed) return;

            try
            {
                _harmony.UnpatchAll(_harmony.Id);
            }
            catch (Exception ex)
            {
                _instanceLogger.Warning("[VintageVisuals] pseudopbr: failed to remove the terrain bind hook: " +
                                        ex.Message);
            }

            _installed = false;
        }

        /// <summary>
        /// Publishes the vanilla-atlas-texture to material-atlas-texture map.
        /// Passing null deactivates the hook without unpatching it, which is
        /// what "the subsystem is switched off" should cost.
        /// </summary>
        /// <summary>
        /// The captured scene texture to rebind per draw, or 0 for none.
        ///
        /// 0 is the safe value and means the shader's validity uniform decides:
        /// nothing is bound, the reflection falls back to the analytic sky.
        /// </summary>
        public static void SetSceneCapture(int textureId)
        {
            _captureTextureId = textureId;
        }

        /// <summary>
        /// The debug-only world block-volume atlas to rebind per terrain draw,
        /// or 0 when the proof views are inactive.
        /// </summary>
        public static void SetWorldReflection(int textureId)
        {
            _worldReflectionTextureId = textureId;
        }

        public static void SetPages(Dictionary<int, int> materialPageByAtlasTexture)
        {
            SetPages(materialPageByAtlasTexture, null);
        }

        /// <summary>
        /// Both material pages for the same atlas texture.
        ///
        /// The second map is optional and is allowed to be null or short: the
        /// second atlas is a strict addition, so a draw call that has a first
        /// page and no second one must still bind the first and shade exactly
        /// as it did before this page existed.
        /// </summary>
        public static void SetPages(Dictionary<int, int> materialPageByAtlasTexture,
                                    Dictionary<int, int> secondPageByAtlasTexture)
        {
            lock (SecondPageByAtlasTexture)
            {
                SecondPageByAtlasTexture.Clear();

                if (secondPageByAtlasTexture != null)
                {
                    foreach (KeyValuePair<int, int> entry in secondPageByAtlasTexture)
                    {
                        SecondPageByAtlasTexture[entry.Key] = entry.Value;
                    }
                }
            }

            lock (MaterialPageByAtlasTexture)
            {
                MaterialPageByAtlasTexture.Clear();

                if (materialPageByAtlasTexture == null)
                {
                    _active = false;
                    return;
                }

                foreach (KeyValuePair<int, int> entry in materialPageByAtlasTexture)
                {
                    MaterialPageByAtlasTexture[entry.Key] = entry.Value;
                }

                _active = MaterialPageByAtlasTexture.Count > 0;
            }
        }

        /// <summary>
        /// Finds BindTexture2D by shape. Every overload taking (string, int, ..)
        /// is patched, because which one the chunk renderer uses is not
        /// something this mod should have an opinion about.
        /// </summary>
        private static List<MethodInfo> FindBindMethods(Type type)
        {
            return type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m =>
                    m.Name == BindMethodName &&
                    m.GetParameters().Length >= 2 &&
                    m.GetParameters()[0].ParameterType == typeof(string) &&
                    m.GetParameters()[1].ParameterType == typeof(int))
                .ToList();
        }

        /// <summary>
        /// Runs after the game has bound a texture to a sampler. <c>__0</c> and
        /// <c>__1</c> are Harmony's positional accessors for the sampler name
        /// and texture id — positional rather than named so a parameter rename
        /// cannot break the injection.
        /// </summary>
        public static void BindTexture2DPostfix(object __instance, string __0, int __1)
        {
            // Ordered cheapest first: this runs on every texture bind in the
            // game, so the common case must be a volatile read and a return.
            if (!_active) return;
            if (_reentrant) return;
            if (!string.Equals(__0, TerrainSampler, StringComparison.Ordinal)) return;

            int materialTextureId;
            lock (MaterialPageByAtlasTexture)
            {
                if (!MaterialPageByAtlasTexture.TryGetValue(__1, out materialTextureId)) return;
            }

            if (materialTextureId == 0) return;

            var program = __instance as IShaderProgram;

            // HasUniform is also the filter that keeps this to the shader we
            // patched: no other program declares vv_materialTex, so the sky,
            // the GUI and everything else that binds a terrain texture falls
            // out here without needing to be named.
            if (program == null || !program.HasUniform(PbrShaderBinder.SamplerUniform)) return;

            _reentrant = true;
            try
            {
                program.BindTexture2D(PbrShaderBinder.SamplerUniform, materialTextureId,
                                      MaterialAtlasTexture.TextureUnit);

                // The second page, when there is one. Guarded on the uniform
                // rather than on our own bookkeeping: if the group that
                // declares vv_materialTex2 rolled back, the program genuinely
                // does not have it and binding would be writing to a name that
                // is not there.
                int secondTextureId;
                lock (SecondPageByAtlasTexture)
                {
                    SecondPageByAtlasTexture.TryGetValue(__1, out secondTextureId);
                }

                if (secondTextureId != 0 && program.HasUniform(PbrShaderBinder.SecondSamplerUniform))
                {
                    program.BindTexture2D(PbrShaderBinder.SecondSamplerUniform, secondTextureId,
                                          MaterialAtlasTexture.SecondTextureUnit);
                }

                // The captured scene, on the same per-draw footing. See the
                // note on _captureTextureId: bound once a frame it does not
                // survive to the chunk draws, and the reflection then samples
                // whatever texture the unit is holding instead.
                int captureId = _captureTextureId;

                if (captureId != 0 && program.HasUniform(PbrShaderBinder.ReflectSceneUniform))
                {
                    program.BindTexture2D(PbrShaderBinder.ReflectSceneUniform, captureId,
                                          SceneCaptureRenderer.TextureUnit);
                }

                int worldReflectionId = _worldReflectionTextureId;

                if (worldReflectionId != 0 && program.HasUniform(PbrShaderBinder.ReflectWorldUniform))
                {
                    program.BindTexture2D(PbrShaderBinder.ReflectWorldUniform, worldReflectionId,
                                          WorldReflectionVolume.TextureUnit);
                }
            }
            catch (Exception ex)
            {
                // The caller is the game's renderer, mid-frame. Throwing here
                // would take the client down over a cosmetic mod.
                _active = false;

                if (_logger != null)
                {
                    _logger.Error("[VintageVisuals] pseudopbr: binding the material atlas page failed; " +
                                  "surface relief is disabled for this session. Rendering is otherwise unaffected.");
                    _logger.LogException(EnumLogType.Error, ex);
                }
            }
            finally
            {
                _reentrant = false;
            }
        }
    }
}
