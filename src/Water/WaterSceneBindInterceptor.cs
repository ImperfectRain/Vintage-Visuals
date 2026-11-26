using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageVisuals.Water
{
    /// <summary>
    /// Binds the finished opaque frame to chunkliquid's <c>vv_waterScene</c>
    /// at the moment vanilla binds the opaque depth to the same program
    /// (patch group "waterscene", D82).
    ///
    /// Why at that moment: a texture unit is global GL state, so a bind made
    /// at <see cref="EnumRenderStage.Before"/> does not survive to the OIT
    /// pass - the GUI, the held item and every other draw in between rebind
    /// units freely. <c>ChunkRenderer.RenderOIT</c> sets
    /// <c>chunkliquid.DepthTex2D</c>, which calls
    /// <c>BindTexture2D("depthTex", id, 1)</c>, immediately before drawing
    /// the water, so a postfix on that bind is the one place the colour is
    /// guaranteed to be on its unit when the water draws. The same lesson as
    /// the terrain capture (TerrainTextureBindInterceptor): a per-frame bind
    /// and a per-draw bind look identical in every static test.
    ///
    /// Why it is safe to read: the water draws into the Transparent
    /// framebuffer, whose colour targets are its own (accum, reveal, glow) and
    /// whose depth is Primary's. Primary's colour attachment 0 is therefore
    /// complete and not being written while the water samples it - no copy,
    /// no feedback loop, no frame of lag.
    ///
    /// Filtered on HasUniform(vv_waterScene), which exists only while the
    /// waterscene group is applied, so no other program is touched. The
    /// active texture unit is saved and restored around the bind, and any
    /// failure switches the hook off for the session rather than throwing
    /// into the game's renderer. Reflection-resolved, postfix only, like every
    /// hook on VintagestoryLib internals.
    /// </summary>
    public sealed class WaterSceneBindInterceptor
    {
        private const string ShaderProgramBaseTypeName = "Vintagestory.Client.NoObf.ShaderProgramBase";
        private const string BindMethodName = "BindTexture2D";

        /// <summary>Vanilla's opaque-depth sampler in chunkliquid: the bind we follow.</summary>
        public const string DepthSampler = "depthTex";

        /// <summary>Our sampler, declared by waterscene.glsl below liquidDepth.</summary>
        public const string SceneSampler = "vv_waterScene";

        /// <summary>
        /// chunkliquid's vanilla units are 0..4 (terrainTex, depthTex,
        /// shadowMapFar, shadowMapNear, liquidDepth). The opaque programs use
        /// 11..15 for the canopy, world volume, capture and material atlases
        /// (WorldReflectionVolume, SceneCaptureRenderer, MaterialAtlasTexture);
        /// 10 is free everywhere, so no unit ever means two textures, even if
        /// the world volume later reaches the water too.
        /// </summary>
        public const int TextureUnit = 10;

        private static readonly Lazy<GlActiveTexture> Gl = new Lazy<GlActiveTexture>(GlActiveTexture.TryCreate);

        // Static because Harmony patches must be; one client per process.
        private static volatile int _sceneTextureId;
        private static volatile bool _active;
        private static volatile int _bindsThisSession;
        private static ILogger _logger;

        [ThreadStatic]
        private static bool _reentrant;

        private readonly Harmony _harmony;
        private bool _installed;

        public WaterSceneBindInterceptor(string harmonyId, ILogger logger)
        {
            _harmony = new Harmony(harmonyId + ".waterscene");
            _logger = logger;
        }

        public bool Installed { get { return _installed; } }

        /// <summary>How many times the colour has been bound; 0 means the hook never fired.</summary>
        public static int BindCount { get { return _bindsThisSession; } }

        /// <summary>
        /// The opaque frame's colour texture, or 0 for none. 0 is the safe
        /// value: nothing is bound and vv_waterSceneOn stays 0, so the water
        /// keeps its sky.
        /// </summary>
        public static void SetSceneTexture(int textureId)
        {
            _sceneTextureId = textureId;
        }

        public bool Install()
        {
            if (_installed) return true;

            Type type = AccessTools.TypeByName(ShaderProgramBaseTypeName);
            List<MethodInfo> targets = type == null
                ? new List<MethodInfo>()
                : type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                      .Where(m => m.Name == BindMethodName &&
                                  m.GetParameters().Length >= 2 &&
                                  m.GetParameters()[0].ParameterType == typeof(string) &&
                                  m.GetParameters()[1].ParameterType == typeof(int))
                      .ToList();

            if (targets.Count == 0 || Gl.Value == null)
            {
                _logger.Warning("[VintageVisuals] water: cannot hook " + ShaderProgramBaseTypeName + "." +
                    BindMethodName + (Gl.Value == null ? " (no OpenGL active-texture access)" : "") +
                    ", so the water reflects the sky only. Nothing else is affected.");
                return false;
            }

            try
            {
                var postfix = new HarmonyMethod(
                    AccessTools.Method(typeof(WaterSceneBindInterceptor), nameof(BindTexture2DPostfix)));
                foreach (MethodInfo target in targets) _harmony.Patch(target, postfix: postfix);
                _installed = true;
                _active = true;
                _logger.Notification("[VintageVisuals] water: scene bind hook installed on " + targets.Count +
                    " " + BindMethodName + " overload(s); unit " + TextureUnit + ".");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning("[VintageVisuals] water: could not install the scene bind hook, so the water " +
                    "reflects the sky only. " + ex.Message);
                return false;
            }
        }

        public void Uninstall()
        {
            SetSceneTexture(0);
            _active = false;
            if (!_installed) return;
            try { _harmony.UnpatchAll(_harmony.Id); }
            catch (Exception ex)
            {
                _logger.Warning("[VintageVisuals] water: failed to remove the scene bind hook: " + ex.Message);
            }
            _installed = false;
        }

        /// <summary>
        /// Runs after every texture bind in the game, so the common case is a
        /// volatile read, a string compare and a return.
        /// </summary>
        public static void BindTexture2DPostfix(object __instance, string __0, int __1)
        {
            if (!_active || _reentrant) return;
            if (!string.Equals(__0, DepthSampler, StringComparison.Ordinal)) return;

            int sceneId = _sceneTextureId;
            if (sceneId == 0) return;

            var program = __instance as IShaderProgram;
            if (program == null || !program.HasUniform(SceneSampler)) return;

            GlActiveTexture gl = Gl.Value;
            int previous;
            if (gl == null || !gl.TryGet(out previous))
            {
                _active = false;
                if (_logger != null) _logger.Warning("[VintageVisuals] water: the active texture unit could " +
                    "not be read, so the scene reflection is off for this session rather than risking the " +
                    "render state.");
                return;
            }

            _reentrant = true;
            try
            {
                program.BindTexture2D(SceneSampler, sceneId, TextureUnit);
                _bindsThisSession++;
            }
            catch (Exception ex)
            {
                _active = false;
                if (_logger != null)
                {
                    _logger.Error("[VintageVisuals] water: binding the scene colour failed; the water reflects " +
                                  "the sky only for this session. Rendering is otherwise unaffected.");
                    _logger.LogException(EnumLogType.Error, ex);
                }
            }
            finally
            {
                gl.TrySet(previous);
                _reentrant = false;
            }
        }

        /// <summary>
        /// glGetInteger(GL_ACTIVE_TEXTURE) and glActiveTexture through
        /// reflection: the project does not compile against OpenTK (see
        /// TerrainTextureBindInterceptor, which carries the same bridge; kept
        /// separate so either hook can be removed alone).
        /// </summary>
        private sealed class GlActiveTexture
        {
            private readonly MethodInfo _getInteger;
            private readonly MethodInfo _activeTexture;
            private readonly object _getName;
            private readonly Type _unitType;

            private GlActiveTexture(MethodInfo getInteger, MethodInfo activeTexture, object getName, Type unitType)
            {
                _getInteger = getInteger;
                _activeTexture = activeTexture;
                _getName = getName;
                _unitType = unitType;
            }

            public static GlActiveTexture TryCreate()
            {
                try
                {
                    Type glType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(SafeTypes)
                        .FirstOrDefault(t => t.FullName == "OpenTK.Graphics.OpenGL.GL" ||
                                             t.FullName == "OpenTK.Graphics.OpenGL4.GL");
                    if (glType == null) return null;

                    MethodInfo getInteger = glType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "GetInteger" && m.GetParameters().Length == 2 &&
                                             m.GetParameters()[1].ParameterType == typeof(int).MakeByRefType());
                    MethodInfo activeTexture = glType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "ActiveTexture" && m.GetParameters().Length == 1 &&
                                             m.GetParameters()[0].ParameterType.IsEnum);
                    if (getInteger == null || activeTexture == null) return null;

                    object getName = Enum.Parse(getInteger.GetParameters()[0].ParameterType, "ActiveTexture");
                    return new GlActiveTexture(getInteger, activeTexture, getName,
                                               activeTexture.GetParameters()[0].ParameterType);
                }
                catch
                {
                    return null;
                }
            }

            public bool TryGet(out int unit)
            {
                unit = 0;
                try
                {
                    object[] args = { _getName, 0 };
                    _getInteger.Invoke(null, args);
                    unit = (int)args[1];
                    return unit > 0;
                }
                catch { return false; }
            }

            public void TrySet(int unit)
            {
                try { _activeTexture.Invoke(null, new[] { Enum.ToObject(_unitType, unit) }); }
                catch { }
            }

            private static Type[] SafeTypes(Assembly assembly)
            {
                try { return assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
            }
        }
    }
}
