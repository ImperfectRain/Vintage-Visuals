using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VintageVisuals.Common.Rendering;

namespace VintageVisuals.Common.Diagnostics
{
    /// <summary>
    /// GPU and CPU time per chunk pool inside the opaque stage.
    ///
    /// WHY. The opaque stage is one figure in the profiler, and it holds the
    /// game's terrain, the mod's terrain material, entities and the depth
    /// prepass together. Every optimisation since D84 was judged by comparing
    /// that one figure with a feature on and off. This splits it: the time the
    /// chunk renderer spends in each render pass's pools (Opaque, TopSoil,
    /// BlendNoCull, OpaqueNoCull, Decor), and the depth prepass on its own.
    /// Whatever remains of the opaque figure is entities and other renderers.
    ///
    /// HOW. Harmony prefix and finalizer on <c>ChunkRenderer.RenderOpaque</c>
    /// (the whole chunk draw, outermost call only, so the prepass's nested call
    /// is inside it) and prefix and postfix on <c>MeshDataPoolManager.Render</c>.
    /// A pass's first pool call stamps its start, every pool call's end stamps
    /// its end, so a pass drawn as one call per atlas reads as one span. Pool to
    /// pass is found by reference in the chunk renderer's own
    /// <c>poolsByRenderPass</c>, never assumed from call order.
    ///
    /// COST. Installed on the profiler's first use, never before. While
    /// installed and nobody is measuring, each hook is one static null check.
    /// While measuring, about 30 timestamp queries a frame.
    ///
    /// SAFETY. Reflection-guarded, prefix/postfix/finalizer only, never skips
    /// the original, and any exception switches the timers off for the session.
    /// It changes no GL state.
    /// </summary>
    internal sealed class ChunkPassTimers
    {
        private const string ChunkRendererTypeName = "Vintagestory.Client.NoObf.ChunkRenderer";
        private const string PoolsFieldName = "poolsByRenderPass";

        private static ILogger _logger;
        private static FieldInfo _poolsField;
        private static object _chunkRenderer;
        private static int _depth;
        private static bool _failed;

        private readonly Harmony _harmony = new Harmony("vintagevisuals.chunkpasstimers");
        private bool _installed;

        public ChunkPassTimers(ILogger logger)
        {
            _logger = logger;
        }

        public void Install()
        {
            if (_installed || _failed) return;
            try
            {
                Type chunkRenderer = AccessTools.TypeByName(ChunkRendererTypeName);
                MethodInfo renderOpaque = chunkRenderer == null ? null
                    : AccessTools.Method(chunkRenderer, "RenderOpaque", new[] { typeof(float) });
                _poolsField = chunkRenderer == null ? null : AccessTools.Field(chunkRenderer, PoolsFieldName);
                MethodInfo poolRender = AccessTools.Method(typeof(MeshDataPoolManager), "Render",
                    new[] { typeof(Vec3d), typeof(string), typeof(EnumFrustumCullMode) });

                if (renderOpaque == null || poolRender == null || _poolsField == null ||
                    _poolsField.FieldType != typeof(MeshDataPoolManager[][]))
                {
                    _failed = true;
                    _logger.Warning("[VintageVisuals] profiler: chunk renderer internals not found; " +
                                    "the opaque stage is timed as one figure.");
                    return;
                }

                // First: the chunk span must open before the depth prepass's own
                // prefix runs the nested prepass draw.
                _harmony.Patch(renderOpaque,
                    prefix: new HarmonyMethod(typeof(ChunkPassTimers), nameof(RenderOpaquePrefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(ChunkPassTimers), nameof(RenderOpaqueFinalizer)));
                _harmony.Patch(poolRender,
                    prefix: new HarmonyMethod(typeof(ChunkPassTimers), nameof(PoolPrefix)) { priority = Priority.Last },
                    postfix: new HarmonyMethod(typeof(ChunkPassTimers), nameof(PoolPostfix)) { priority = Priority.Last });
                _installed = true;
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        public void Uninstall()
        {
            if (!_installed) return;
            try { _harmony.UnpatchAll(_harmony.Id); } catch { }
            _installed = false;
            _depth = 0;
        }

        private static void Fail(Exception ex)
        {
            if (_failed) return;
            _failed = true;
            _logger?.Warning("[VintageVisuals] profiler: chunk pass timers off for this session (" + ex.Message + ").");
        }

        public static void RenderOpaquePrefix(object __instance)
        {
            _depth++;
            if (_depth != 1 || _failed) return;
            _chunkRenderer = __instance;
            FrameProfiler p = FrameProfiler.Active;
            if (p == null) return;
            try { p.StampCode(p.ChunksStart, true); }
            catch (Exception ex) { Fail(ex); }
        }

        public static Exception RenderOpaqueFinalizer(Exception __exception)
        {
            if (_depth == 1 && !_failed)
            {
                FrameProfiler p = FrameProfiler.Active;
                if (p != null)
                {
                    try { p.StampCode(p.ChunksEnd, false); }
                    catch (Exception ex) { Fail(ex); }
                }
            }
            if (_depth > 0) _depth--;
            return __exception;
        }

        /// <summary>The render pass this pool draws for, by reference, or -1.</summary>
        private static int PassOf(MeshDataPoolManager pool)
        {
            var pools = _poolsField.GetValue(_chunkRenderer) as MeshDataPoolManager[][];
            if (pools == null) return -1;
            int n = Math.Min(pools.Length, FrameProfiler.ChunkPassCount);
            for (int pass = 0; pass < n; pass++)
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

        public static void PoolPrefix(MeshDataPoolManager __instance, out int __state)
        {
            __state = -1;
            if (_depth == 0 || _failed || _chunkRenderer == null) return;
            FrameProfiler p = FrameProfiler.Active;
            if (p == null) return;
            try
            {
                int pass = PassOf(__instance);
                if (pass < 0) return;
                if (DepthPrepass.InPrepass)
                {
                    // Only the Opaque and OpaqueNoCull pools draw in the prepass; the
                    // rest are skipped by its prefix and must not stretch the span.
                    if (pass > 1) return;
                    p.StampCode(p.PrepassStart, true);
                    __state = 100 + pass;
                }
                else
                {
                    p.StampCode(p.PassStart(pass), true);
                    __state = pass;
                }
            }
            catch (Exception ex) { Fail(ex); }
        }

        public static void PoolPostfix(int __state)
        {
            if (__state < 0 || _failed) return;
            FrameProfiler p = FrameProfiler.Active;
            if (p == null) return;
            try { p.StampCode(__state >= 100 ? p.PrepassEnd : p.PassEnd(__state), false); }
            catch (Exception ex) { Fail(ex); }
        }
    }
}
