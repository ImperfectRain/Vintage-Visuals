using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageVisuals.Common.Rendering
{
    /// <summary>
    /// Exact depth prepass for the opaque and leaf chunk passes (D88).
    ///
    /// WHY. The chunk renderer draws its mesh pools in pool order, never sorted
    /// by distance (<c>ChunkRenderer.RenderOpaque</c> -> <c>MeshDataPoolManager.Render</c>
    /// per pass and atlas). So a leaf drawn behind another leaf is shaded in full
    /// about half the time and then overwritten. The game's own terrain shading is
    /// cheap enough that this never mattered; the mod's terrain material is not,
    /// and every per-fragment cost it has is multiplied by that overdraw. In a
    /// forest that was most of the opaque pass (perf audit D84 to D87).
    ///
    /// WHAT. Before the game's own RenderOpaque runs, this runs the very same
    /// method once more with colour writes off and <c>vv_depthPrepass</c> = 1,
    /// letting only the Opaque (0) and OpaqueNoCull (1) pools draw. The patch
    /// group <c>depthprepass</c> makes that draw cheap: it skips vanilla's
    /// shadow-map taps (they only scale colour) and returns right after vanilla's
    /// own alpha discard, so nothing of the mod's material runs. It writes depth
    /// only where the final fragment will be fully opaque and not reflective.
    /// The real pass then draws those two pools with GL_LEQUAL; every fragment
    /// behind the nearest opaque surface fails the depth test before it is
    /// shaded, and each visible pixel runs the material once.
    ///
    /// WHY IT IS EXACT. The prepass uses the same program, the same vertex shader
    /// binary, the same uniforms and the same draw calls as the real pass, so the
    /// depth it writes is bit-identical (GL's invariance rules for one program).
    /// It writes depth only for fragments the real pass keeps at alpha 1, which
    /// overwrite whatever is behind them under any blend, so rejecting what is
    /// behind them changes nothing. A pool the game draws with blending off
    /// (the leaves) overwrites with every surviving fragment, so there all of
    /// them write prepass depth; blending is read from GL per draw. Partly transparent and reflective fragments
    /// write nothing in the prepass and behave exactly as before. Pools 2
    /// (BlendNoCull), 5 (TopSoil, another program) and 8 (Decor) are not prepassed
    /// and keep the game's GL_LESS. The final depth buffer equals vanilla's, so
    /// entities, water and every later pass see the same depth.
    ///
    /// The one theoretical difference: two DIFFERENT triangles of pools 0/1 at
    /// bit-identical depth (exactly coplanar duplicates). Vanilla's GL_LESS keeps
    /// the first drawn, GL_LEQUAL the last. Such faces z-fight in vanilla anyway.
    ///
    /// SAFETY. Fails closed at every step: missing internals, a missing uniform
    /// (the group rolled back) or any exception switch it off for the session
    /// and the game renders exactly as vanilla. Harmony prefix/postfix only; the
    /// MeshDataPoolManager prefix returns at once outside RenderOpaque.
    /// </summary>
    public sealed class DepthPrepass
    {
        public const string GroupName = "depthprepass";
        public const string PrepassUniform = "vv_depthPrepass";

        private const string ChunkRendererTypeName = "Vintagestory.Client.NoObf.ChunkRenderer";
        private const string RenderOpaqueName = "RenderOpaque";
        private const string PoolsFieldName = "poolsByRenderPass";

        private const int PassOpaque = 0;     // EnumChunkRenderPass.Opaque
        private const int PassOpaqueNoCull = 1; // EnumChunkRenderPass.OpaqueNoCull (leaves, plants)

        private const int GlLess = 0x0201;
        private const int GlLequal = 0x0203;

        private enum Phase { None, Prepass, Main }

        // Harmony patches are static; one client per process makes this safe.
        private static ICoreClientAPI _capi;
        private static ILogger _logger;
        private static Func<bool> _wanted;
        private static MethodInfo _renderOpaque;
        private static FieldInfo _poolsField;
        private static GlDepthApi _gl;
        private static Phase _phase = Phase.None;
        private static bool _busy;
        private static bool _failed;
        private static int _savedDepthFunc = GlLess;
        private static readonly object[] OneArg = new object[1];

        /// <summary>-1 follows the config; 0 forces off, 1 forces on. Set only by '.vvbench prepass'.</summary>
        public static int BenchOverride { get; set; } = -1;

        /// <summary>Whether the prepass ran on the last frame, for the bench report and the log.</summary>
        public static bool RanLastFrame { get; private set; }

        private readonly Harmony _harmony;
        private bool _installed;

        public DepthPrepass(string harmonyId, ICoreClientAPI capi, ILogger logger, Func<bool> wanted)
        {
            _harmony = new Harmony(harmonyId + ".depthprepass");
            _capi = capi;
            _logger = logger;
            _wanted = wanted;
        }

        public bool Install()
        {
            if (_installed) return true;
            try
            {
                Type chunkRenderer = AccessTools.TypeByName(ChunkRendererTypeName);
                if (chunkRenderer == null) return Refuse("cannot find " + ChunkRendererTypeName);

                _renderOpaque = AccessTools.Method(chunkRenderer, RenderOpaqueName, new[] { typeof(float) });
                if (_renderOpaque == null) return Refuse("cannot find " + RenderOpaqueName + "(float)");

                _poolsField = AccessTools.Field(chunkRenderer, PoolsFieldName);
                if (_poolsField == null || _poolsField.FieldType != typeof(MeshDataPoolManager[][]))
                    return Refuse("cannot find " + PoolsFieldName + " as MeshDataPoolManager[][]");

                MethodInfo poolRender = AccessTools.Method(typeof(MeshDataPoolManager), "Render",
                    new[] { typeof(Vec3d), typeof(string), typeof(EnumFrustumCullMode) });
                if (poolRender == null) return Refuse("cannot find MeshDataPoolManager.Render");

                _gl = GlDepthApi.TryCreate();
                if (_gl == null) return Refuse("cannot reach glDepthFunc through OpenTK");

                _harmony.Patch(_renderOpaque,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(DepthPrepass), nameof(RenderOpaquePrefix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(DepthPrepass), nameof(RenderOpaqueFinalizer))));
                _harmony.Patch(poolRender,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(DepthPrepass), nameof(PoolRenderPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(DepthPrepass), nameof(PoolRenderPostfix))));

                _installed = true;
                _logger.Notification("[VintageVisuals] depth prepass: hooks installed (exact; '.vvprepass' toggles, " +
                                     "'.vvbench prepass' measures it).");
                return true;
            }
            catch (Exception ex)
            {
                _failed = true;
                try { _harmony.UnpatchAll(_harmony.Id); } catch { }
                return Refuse("could not install: " + ex.Message);
            }
        }

        public void Uninstall()
        {
            if (!_installed) return;
            try { _harmony.UnpatchAll(_harmony.Id); }
            catch (Exception ex) { _logger.Warning("[VintageVisuals] depth prepass: unpatch failed: " + ex.Message); }
            _installed = false;
            _phase = Phase.None;
        }

        private static bool Refuse(string why)
        {
            _logger?.Warning("[VintageVisuals] depth prepass: " + why +
                             ". The prepass is off; rendering is exactly as before.");
            return false;
        }

        /// <summary>Whether chunkopaque carries the depthprepass group right now.</summary>
        public static bool ShaderReady()
        {
            IShaderProgram program = _capi?.Shader.GetProgramByName("chunkopaque");
            return program != null && program.HasUniform(PrepassUniform);
        }

        /// <summary>Whether this frame should prepass: wanted, not failed, and the shader carries the group.</summary>
        private static bool ShouldRun()
        {
            if (_failed) return false;
            bool wanted = BenchOverride >= 0 ? BenchOverride == 1 : (_wanted != null && _wanted());
            return wanted && ShaderReady();
        }

        public static bool RenderOpaquePrefix(object __instance, float __0)
        {
            if (_busy) return true; // this is the nested prepass call itself
            RanLastFrame = false;
            if (!ShouldRun()) return true;

            _busy = true;
            _phase = Phase.Prepass;
            try
            {
                _capi.Render.GlColorMask(false, false, false, false);
                OneArg[0] = __0;
                _renderOpaque.Invoke(__instance, OneArg);
                RanLastFrame = true;
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            finally
            {
                _capi.Render.GlColorMask(true, true, true, true);
                _phase = _failed ? Phase.None : Phase.Main;
                _busy = false;
            }
            return true;
        }

        public static Exception RenderOpaqueFinalizer(Exception __exception)
        {
            if (!_busy) _phase = Phase.None;
            return __exception;
        }

        /// <summary>Which prepassed pass this pool manager draws, or -1.</summary>
        private static int PrepassedPassOf(MeshDataPoolManager pool, object chunkRenderer)
        {
            var pools = _poolsField.GetValue(chunkRenderer) as MeshDataPoolManager[][];
            if (pools == null) return -1;
            for (int pass = PassOpaque; pass <= PassOpaqueNoCull && pass < pools.Length; pass++)
            {
                MeshDataPoolManager[] perAtlas = pools[pass];
                if (perAtlas == null) continue;
                for (int i = 0; i < perAtlas.Length; i++)
                {
                    if (ReferenceEquals(perAtlas[i], pool)) return pass;
                }
            }
            return -1;
        }

        private static object _chunkRenderer;

        public static bool PoolRenderPrefix(MeshDataPoolManager __instance, out bool __state)
        {
            __state = false;
            if (_phase == Phase.None) return true;

            try
            {
                if (_chunkRenderer == null) _chunkRenderer = FindChunkRenderer();
                if (_chunkRenderer == null) { Fail(new InvalidOperationException("chunk renderer not found")); return _phase != Phase.Prepass; }

                bool prepassed = PrepassedPassOf(__instance, _chunkRenderer) >= 0;
                IShaderProgram shader = _capi.Render.CurrentActiveShader;
                bool hasUniform = shader != null && shader.HasUniform(PrepassUniform);

                if (_phase == Phase.Prepass)
                {
                    if (!prepassed || !hasUniform) return false; // only pools 0 and 1 draw in the prepass
                    // 1: the game draws this pool blended, so only alpha-1 fragments may
                    // write prepass depth. 2: blending is off (the leaf pool), so every
                    // fragment that survives the discard overwrites what is behind it.
                    // Read from GL rather than assumed, so a game change cannot make it lie;
                    // unknown reads as blended, the strict case.
                    float mode = _gl.BlendEnabled(true) ? 1f : 2f;
                    shader.Uniform(PrepassUniform, mode);
                    return true;
                }

                // Main pass: every chunkopaque draw shades normally again.
                if (hasUniform) shader.Uniform(PrepassUniform, 0f);
                if (prepassed && RanLastFrame)
                {
                    _savedDepthFunc = _gl.GetDepthFunc(GlLess);
                    _gl.DepthFunc(GlLequal);
                    __state = true;
                }
                return true;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return _phase != Phase.Prepass;
            }
        }

        public static void PoolRenderPostfix(bool __state)
        {
            if (!__state) return;
            try { _gl.DepthFunc(_savedDepthFunc); }
            catch (Exception ex) { Fail(ex); }
        }

        private static object FindChunkRenderer()
        {
            // ClientMain.chunkRenderer, found by type so a rename of the field does not matter.
            object world = _capi.World;
            if (world == null) return null;
            Type chunkRendererType = _renderOpaque.DeclaringType;
            FieldInfo field = world.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(f => f.FieldType == chunkRendererType);
            return field?.GetValue(world);
        }

        private static void Fail(Exception ex)
        {
            if (_failed) return;
            _failed = true;
            _phase = Phase.None;
            try { _gl?.DepthFunc(GlLess); } catch { }
            _logger?.Error("[VintageVisuals] depth prepass failed and is off for this session; rendering is " +
                           "exactly as before. " + (ex is TargetInvocationException t && t.InnerException != null
                               ? t.InnerException.Message : ex.Message));
            if (ex != null) _logger?.LogException(EnumLogType.Error, ex);
        }

        /// <summary>glDepthFunc and glGetInteger(GL_DEPTH_FUNC) by reflection; the mod does not compile against OpenTK.</summary>
        private sealed class GlDepthApi
        {
            private readonly MethodInfo _depthFunc;
            private readonly Type _depthFuncType;
            private readonly MethodInfo _getInteger;
            private readonly object _depthFuncName;
            private readonly MethodInfo _isEnabled;
            private readonly object _blendCap;
            private readonly object[] _one = new object[1];
            private readonly object[] _two = new object[2];

            private GlDepthApi(MethodInfo depthFunc, MethodInfo getInteger, object depthFuncName,
                               MethodInfo isEnabled, object blendCap)
            {
                _isEnabled = isEnabled;
                _blendCap = blendCap;
                _depthFunc = depthFunc;
                _depthFuncType = depthFunc.GetParameters()[0].ParameterType;
                _getInteger = getInteger;
                _depthFuncName = depthFuncName;
            }

            public static GlDepthApi TryCreate()
            {
                try
                {
                    Type glType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(SafeTypes)
                        .Where(t => t.FullName == "OpenTK.Graphics.OpenGL.GL" ||
                                    t.FullName == "OpenTK.Graphics.OpenGL4.GL")
                        .OrderBy(t => t.FullName == "OpenTK.Graphics.OpenGL.GL" ? 0 : 1)
                        .FirstOrDefault();
                    if (glType == null) return null;

                    MethodInfo[] methods = glType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                    MethodInfo depthFunc = methods.FirstOrDefault(m =>
                        m.Name == "DepthFunc" && m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType.IsEnum);
                    if (depthFunc == null) return null;

                    MethodInfo getInteger = methods.FirstOrDefault(m =>
                        m.Name == "GetInteger" && m.GetParameters().Length == 2 &&
                        m.GetParameters()[0].ParameterType.IsEnum &&
                        m.GetParameters()[1].ParameterType == typeof(int).MakeByRefType());
                    object name = null;
                    if (getInteger != null)
                    {
                        try { name = Enum.Parse(getInteger.GetParameters()[0].ParameterType, "DepthFunc"); }
                        catch { getInteger = null; }
                    }
                    MethodInfo isEnabled = methods.FirstOrDefault(m =>
                        m.Name == "IsEnabled" && m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType.IsEnum && m.ReturnType == typeof(bool));
                    object blend = null;
                    if (isEnabled != null)
                    {
                        try { blend = Enum.Parse(isEnabled.GetParameters()[0].ParameterType, "Blend"); }
                        catch { isEnabled = null; }
                    }
                    return new GlDepthApi(depthFunc, getInteger, name, isEnabled, blend);
                }
                catch
                {
                    return null;
                }
            }

            public void DepthFunc(int func)
            {
                _one[0] = Enum.ToObject(_depthFuncType, func);
                _depthFunc.Invoke(null, _one);
            }

            /// <summary>glIsEnabled(GL_BLEND); the fallback when it cannot be read.</summary>
            public bool BlendEnabled(bool fallback)
            {
                if (_isEnabled == null) return fallback;
                try
                {
                    _one[0] = _blendCap;
                    return (bool)_isEnabled.Invoke(null, _one);
                }
                catch { return fallback; }
            }

            public int GetDepthFunc(int fallback)
            {
                if (_getInteger == null) return fallback;
                _two[0] = _depthFuncName;
                _two[1] = 0;
                _getInteger.Invoke(null, _two);
                int value = (int)_two[1];
                return value >= 0x0200 && value <= 0x0207 ? value : fallback;
            }

            private static Type[] SafeTypes(Assembly assembly)
            {
                try { return assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
            }
        }
    }
}
