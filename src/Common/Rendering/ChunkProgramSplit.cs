using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageVisuals.Common.Rendering
{
    /// <summary>
    /// Split chunk programs (D100).
    ///
    /// WHY. The game draws every chunk pool that is not topsoil with ONE
    /// program, chunkopaque, and the mod's terrain material lives in it. A GPU
    /// allocates registers for a program's heaviest path, and every fragment the
    /// program shades pays that allocation in occupancy, whether it takes that
    /// path or not (D90 measured it: deleting debug code that never ran saved
    /// about 6 ms). So the depth prepass, which runs none of the material, and
    /// the leaves, which take the short flora path, both paid for the block
    /// path's scene march, mirrors and relief. The forest bench (D99) showed it
    /// directly: the leaf prepass cost 1.64 ms on vanilla's program and 3.46 ms
    /// on the patched one, while running the same instructions.
    ///
    /// WHAT. Two twins of the game's own program, compiled from exactly the
    /// source the game compiled (every other group's patches included) plus
    /// one fragment define:
    /// <list type="bullet">
    /// <item>VV_DEPTH_ONLY draws the depth prepass. Every prepass test is a
    ///   constant there, so all of the material after its early return is dead
    ///   code. It needs no uniform copied: everything the prepass reads before
    ///   its return is set by the game inside RenderOpaque, which the swap
    ///   below routes into the twin.</item>
    /// <item>VV_FLORA_ONLY draws the OpaqueNoCull pool (leaves, plants and a few
    ///   blocks) in the main pass. Plants shade exactly as before; the pool's
    ///   few blocks drop the scene march and the mirrors (a visual trade-off,
    ///   D100). Its uniforms are copied from the game's program before each
    ///   draw, because the mod's binders write them there earlier in the
    ///   frame.</item>
    /// </list>
    ///
    /// HOW. The game addresses chunkopaque through one object, so the swap is
    /// made on that object: its program handle and uniform location tables are
    /// pointed at the twin for the duration of the draw and put back after.
    /// Every uniform the game and the pool manager set in that window then
    /// lands in the twin, with no list of names to keep in step with the game.
    /// Depth agrees across programs because terrainsplit.yaml declares
    /// gl_Position invariant in the shared vertex source.
    ///
    /// SAFETY. Fails closed: a missing internal, a twin that will not compile,
    /// or any exception leaves the game's own program drawing everything, which
    /// is the D88 behaviour. Never during a mirror pass. A twin is rebuilt
    /// whenever the game's program changes (a shader reload), and only one twin
    /// is compiled per frame so a reload costs two short hitches, not one long
    /// one.
    /// </summary>
    public static class ChunkProgramSplit
    {
        public const string GroupName = "terrainsplit";
        public const string VertexMarker = "invariant gl_Position";
        public const string DepthDefine = "VV_DEPTH_ONLY";
        public const string FloraDefine = "VV_FLORA_ONLY";

        /// <summary>D99. CpuSpans name of the leaf program's per-frame uniform copy.</summary>
        public const string CopySpanName = "terrainsplit.copy";
        /// <summary>D99. CpuSpans name of compiling a twin (only after a shader reload).</summary>
        public const string BuildSpanName = "terrainsplit.build";

        private const int PassOpaqueNoCull = 1;

        private static ICoreClientAPI _capi;
        private static ILogger _logger;
        private static Func<bool> _wanted;
        private static ChunkSplitGl _gl;
        private static FieldInfo _fProgramId, _fUniforms, _fTextures, _fAttributes, _fPassName, _fShaderFilename;
        private static bool _failed;
        private static Twin _depth, _flora;
        private static int _frame;
        private static int _builtOnFrame = -1;

        // The one swap in flight. Never nested: the prepass swap ends before the
        // main pass begins, and the leaf swap ends with its pool draw.
        private static bool _swapped;
        private static object _swapOwner;
        private static int _savedId;
        private static object _savedUniforms, _savedTextures;
        private static bool _leafBound;

        /// <summary>-1 follows the config; 0 forces off, 1 forces on. Set only by '.vvbench split'.</summary>
        public static int BenchOverride { get; set; } = -1;

        /// <summary>Whether the prepass and the leaf pool drew with their own programs on the last frame.</summary>
        public static bool DepthTwinLastFrame { get; private set; }
        public static bool FloraTwinLastFrame { get; private set; }

        public static void Initialize(ICoreClientAPI capi, ILogger logger, Func<bool> wanted)
        {
            _capi = capi;
            _logger = logger;
            _wanted = wanted;
            try
            {
                Type programBase = HarmonyLib.AccessTools.TypeByName("Vintagestory.Client.NoObf.ShaderProgramBase");
                Type program = HarmonyLib.AccessTools.TypeByName("Vintagestory.Client.NoObf.ShaderProgram");
                if (programBase == null || program == null) { Refuse("cannot find ShaderProgramBase"); return; }
                const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _fProgramId = programBase.GetField("ProgramId", f);
                _fUniforms = programBase.GetField("uniformLocations", f);
                _fTextures = programBase.GetField("textureLocations", f);
                _fAttributes = program.GetField("attributes", f);
                _fPassName = programBase.GetField("PassName", f);
                if (_fPassName != null && _fPassName.FieldType != typeof(string)) _fPassName = null;
                // The game decides a chunk vertex shader's #version from its FILE
                // NAME: Shader.UsesSSBOs() is Filename.StartsWith("chunk"), and with
                // allowSSBOs on, only a shader it says yes to is raised to 430. A
                // twin with no name compiles at 330 and fails on the SSBO block
                // ("unexpected ... at token buffer"), which is how the first
                // install shipped. The name is copied, so this field is required.
                Type shader = HarmonyLib.AccessTools.TypeByName("Vintagestory.Client.NoObf.Shader");
                _fShaderFilename = shader?.GetField("Filename", f);
                if (_fShaderFilename == null || _fShaderFilename.FieldType != typeof(string))
                {
                    Refuse("Shader has no Filename field, so a twin would not get the game's SSBO version");
                    return;
                }
                if (_fProgramId == null || _fProgramId.FieldType != typeof(int) ||
                    _fUniforms == null || _fUniforms.FieldType != typeof(Dictionary<string, int>) ||
                    _fTextures == null || _fTextures.FieldType != typeof(Dictionary<string, int>))
                {
                    Refuse("ShaderProgramBase does not have the fields the split swaps");
                    return;
                }
                string why;
                _gl = ChunkSplitGl.TryCreate(out why);
                if (_gl == null) { Refuse(why); return; }
            }
            catch (Exception ex)
            {
                Refuse(ex.Message);
            }
        }

        public static void Dispose()
        {
            Release(ref _depth);
            Release(ref _flora);
        }

        private static void Refuse(string why)
        {
            _failed = true;
            _logger?.Warning("[VintageVisuals] split chunk programs: " + why +
                             ". The game's own program draws everything, as before D100.");
        }

        private static bool Wanted()
        {
            if (_failed || _gl == null) return false;
            return BenchOverride >= 0 ? BenchOverride == 1 : (_wanted != null && _wanted());
        }

        private static IShaderProgram GameProgram()
        {
            return _capi?.Shader.GetProgram((int)EnumShaderProgram.Chunkopaque);
        }

        /// <summary>Called once per top-level RenderOpaque, before the prepass.</summary>
        public static void BeginFrame()
        {
            _frame++;
            DepthTwinLastFrame = false;
            FloraTwinLastFrame = false;
        }

        // ---- the prepass twin ---------------------------------------------------

        /// <summary>
        /// Points the game's chunkopaque object at the depth twin for the nested
        /// prepass RenderOpaque. False (and nothing changed) when the twin is
        /// unavailable; the prepass then runs on the game's program as in D88.
        /// </summary>
        public static bool EnterDepthTwin()
        {
            if (!Wanted() || _swapped) return false;
            try
            {
                IShaderProgram main = GameProgram();
                Twin twin = Current(ref _depth, main, DepthDefine, "depth");
                if (twin == null) return false;
                SwapIn(main, twin);
                DepthTwinLastFrame = true;
                return true;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return false;
            }
        }

        /// <summary>Puts the game's program back after the prepass. Safe to call when nothing was swapped.</summary>
        public static void ExitTwin()
        {
            try { SwapOut(); }
            catch (Exception ex) { Fail(ex); }
        }

        // ---- the leaf twin ------------------------------------------------------

        /// <summary>
        /// Before a pool draws in the main pass: if it is the OpaqueNoCull pool
        /// and the game's chunkopaque is bound, copy its uniforms into the leaf
        /// twin, bind the twin and swap. <paramref name="pass"/> is the pool's
        /// EnumChunkRenderPass, or -1.
        /// </summary>
        public static void BeforePool(int pass)
        {
            if (pass != PassOpaqueNoCull || _swapped || !Wanted()) return;
            try
            {
                IShaderProgram main = GameProgram();
                if (main == null || !ReferenceEquals(_capi.Render.CurrentActiveShader, main)) return;
                Twin twin = Current(ref _flora, main, FloraDefine, "flora");
                if (twin == null) return;

                int mainId = (int)_fProgramId.GetValue(main);
                using (VintageVisuals.Common.Diagnostics.Profiling.CpuSpans.Measure(CopySpanName))
                {
                    // Arrays (the canopy map, the gust table, the cloud tiles) are
                    // written by the mod's binders once per frame, before
                    // RenderOpaque, so they are copied on the first leaf draw of a
                    // frame only. Everything else, again before each atlas, because
                    // the game sets the texture and alpha test between pools.
                    bool full = twin.CopiedOnFrame != _frame;
                    twin.CopyFrom(mainId, full);
                    twin.CopiedOnFrame = _frame;
                }
                SwapIn(main, twin);
                _gl.UseProgram(twin.ProgramId);
                _leafBound = true;
                FloraTwinLastFrame = true;
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>After any pool draw (a finalizer, so it runs on a throw too).</summary>
        public static void AfterPool()
        {
            if (!_leafBound) return;
            _leafBound = false;
            try
            {
                SwapOut();
                IShaderProgram main = GameProgram();
                if (main != null) _gl.UseProgram((int)_fProgramId.GetValue(main));
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        // ---- swapping -----------------------------------------------------------

        private static void SwapIn(IShaderProgram main, Twin twin)
        {
            _swapOwner = main;
            _savedId = (int)_fProgramId.GetValue(main);
            _savedUniforms = _fUniforms.GetValue(main);
            _savedTextures = _fTextures.GetValue(main);
            _fProgramId.SetValue(main, twin.ProgramId);
            _fUniforms.SetValue(main, twin.Uniforms);
            _fTextures.SetValue(main, twin.Textures);
            _swapped = true;
        }

        private static void SwapOut()
        {
            if (!_swapped) return;
            _swapped = false;
            object owner = _swapOwner;
            _swapOwner = null;
            _fProgramId.SetValue(owner, _savedId);
            _fUniforms.SetValue(owner, _savedUniforms);
            _fTextures.SetValue(owner, _savedTextures);
        }

        private static void Fail(Exception ex)
        {
            if (_failed) return;
            _failed = true;
            try { SwapOut(); } catch { }
            if (_leafBound)
            {
                _leafBound = false;
                try { IShaderProgram main = GameProgram(); if (main != null) _gl.UseProgram((int)_fProgramId.GetValue(main)); } catch { }
            }
            _logger?.Error("[VintageVisuals] split chunk programs failed and are off for this session; the game's own " +
                           "program draws everything, as before D100. " + ex.Message);
            _logger?.LogException(EnumLogType.Error, ex);
        }

        // ---- building -----------------------------------------------------------

        /// <summary>The twin for the game's program as it is now, building it if needed. Null when it cannot be used.</summary>
        private static Twin Current(ref Twin slot, IShaderProgram main, string define, string label)
        {
            if (main == null || main.Disposed || main.VertexShader == null || main.FragmentShader == null) return null;
            int mainId = (int)_fProgramId.GetValue(main);
            string vert = main.VertexShader.Code, frag = main.FragmentShader.Code;

            if (slot != null && slot.Matches(main, mainId, vert, frag))
                return slot.Usable ? slot : null;

            // The game's program changed (a reload) or was never seen.
            Release(ref slot);

            // Only what the groups put there may be split: the invariant
            // declaration (terrainsplit) for exact depth, and the define's
            // consumer (depthprepass or the material) for it to mean anything.
            if (vert == null || frag == null || vert.IndexOf(VertexMarker, StringComparison.Ordinal) < 0) return null;
            if (define == DepthDefine && frag.IndexOf("VV_PREPASS_ON", StringComparison.Ordinal) < 0) return null;
            if (define == FloraDefine && frag.IndexOf("#ifdef VV_FLORA_ONLY", StringComparison.Ordinal) < 0) return null;
            // A normal view (NORMALVIEW) draws its own debug colour after the
            // prepass's early return was skipped; nothing to gain there.
            string prefix = main.FragmentShader.PrefixCode ?? "";
            if (Regex.IsMatch(prefix, @"#define\s+NORMALVIEW\s+[1-9]")) return null;

            // One compile per frame: a reload otherwise compiles both twins at once.
            if (_builtOnFrame == _frame) return null;
            _builtOnFrame = _frame;

            using (VintageVisuals.Common.Diagnostics.Profiling.CpuSpans.Measure(BuildSpanName))
                slot = Twin.Build(main, mainId, vert, frag, define, label);
            return slot.Usable ? slot : null;
        }

        private static void Release(ref Twin twin)
        {
            if (twin == null) return;
            try { twin.Program?.Dispose(); } catch { }
            twin = null;
        }

        private struct UniformCopy
        {
            public int Kind;       // 0 float, 1 int, 2 mat3, 3 mat4, 4 mat4x3
            public int Components;
            public int From, To;   // locations in the game's program and in the twin
        }

        private sealed class Twin
        {
            public IShaderProgram Program;
            public int ProgramId;
            public Dictionary<string, int> Uniforms, Textures;
            public bool Usable;
            public int CopiedOnFrame = -1;
            public string Label;

            private object _source;
            private int _sourceId;
            private string _sourceVert, _sourceFrag;
            private UniformCopy[] _copies = new UniformCopy[0];
            private int _scalarCount;
            private readonly float[] _f = new float[16];
            private readonly int[] _i = new int[4];

            public int ActiveUniforms { get { return _copies.Length; } }

            public bool Matches(IShaderProgram main, int mainId, string vert, string frag)
            {
                return ReferenceEquals(_source, main) && _sourceId == mainId &&
                       ReferenceEquals(_sourceVert, vert) && ReferenceEquals(_sourceFrag, frag);
            }

            public static Twin Build(IShaderProgram main, int mainId, string vert, string frag, string define, string label)
            {
                var twin = new Twin { _source = main, _sourceId = mainId, _sourceVert = vert, _sourceFrag = frag, Label = label };
                IShaderProgram p = null;
                try
                {
                    p = _capi.Shader.NewShaderProgram();
                    // The pass name only labels the game's log lines about this program.
                    try { _fPassName?.SetValue(p, (main.PassName ?? "chunkopaque") + "-" + label); } catch { }
                    p.AssetDomain = main.AssetDomain;
                    p.VertexShader = _capi.Shader.NewShader(EnumShaderType.VertexShader);
                    p.FragmentShader = _capi.Shader.NewShader(EnumShaderType.FragmentShader);
                    p.VertexShader.Code = vert;
                    p.VertexShader.PrefixCode = main.VertexShader.PrefixCode;
                    p.FragmentShader.Code = frag;
                    // The define FIRST: the game inserts the prefix on the line
                    // after #version, and a define is legal above #extension.
                    p.FragmentShader.PrefixCode = "#define " + define + " 1\n" + (main.FragmentShader.PrefixCode ?? "");
                    // Same file names as the game's shaders, so the game picks the
                    // same #version for each stage (see _fShaderFilename).
                    _fShaderFilename.SetValue(p.VertexShader, _fShaderFilename.GetValue(main.VertexShader));
                    _fShaderFilename.SetValue(p.FragmentShader, _fShaderFilename.GetValue(main.FragmentShader));

                    if (_fAttributes != null)
                    {
                        var from = _fAttributes.GetValue(main) as Dictionary<int, string>;
                        var to = _fAttributes.GetValue(p) as Dictionary<int, string>;
                        if (from != null && to != null) foreach (var kv in from) to[kv.Key] = kv.Value;
                    }

                    if (!p.Compile())
                    {
                        _logger.Warning("[VintageVisuals] split chunk programs: the " + label + " twin did not compile " +
                                        "(see the shader error above); that part of the frame keeps the game's program.");
                        try { p.Dispose(); } catch { }
                        return twin;
                    }

                    twin.Program = p;
                    twin.ProgramId = (int)_fProgramId.GetValue(p);
                    twin.Uniforms = (Dictionary<string, int>)_fUniforms.GetValue(p);
                    twin.Textures = (Dictionary<string, int>)_fTextures.GetValue(p);

                    // Every name the game's program can be asked for must exist
                    // in the twin's table too, or the game's next Uniform() call
                    // throws a KeyNotFoundException mid-frame. Same source, same
                    // regex, so they always should; checked rather than assumed.
                    var mainUniforms = (Dictionary<string, int>)_fUniforms.GetValue(main);
                    foreach (string name in mainUniforms.Keys)
                    {
                        if (!twin.Uniforms.ContainsKey(name))
                        {
                            _logger.Warning("[VintageVisuals] split chunk programs: the " + label + " twin lacks uniform '" +
                                            name + "'; it will not be used.");
                            return twin;
                        }
                    }

                    if (define == FloraDefine && !twin.PlanCopies(mainId)) return twin;

                    twin.Usable = true;
                    _logger.Notification("[VintageVisuals] split chunk programs: built the " + label + " twin (program " +
                                         twin.ProgramId + ", " + twin.ActiveUniforms + " uniforms to copy).");
                    return twin;
                }
                catch (Exception ex)
                {
                    _logger.Warning("[VintageVisuals] split chunk programs: the " + label + " twin could not be built: " + ex.Message);
                    if (twin.Program == null && p != null) { try { p.Dispose(); } catch { } }
                    twin.Usable = false;
                    return twin;
                }
            }

            /// <summary>
            /// Lists every active uniform of the twin with its location in both
            /// programs, element by element for arrays. Scalars first, so a
            /// partial copy is a prefix of the list.
            /// </summary>
            private bool PlanCopies(int mainId)
            {
                int count = _gl.GetProgramInt(ProgramId, "ActiveUniforms");
                var scalars = new List<UniformCopy>();
                var arrays = new List<UniformCopy>();
                for (int index = 0; index < count; index++)
                {
                    int size, type;
                    string typeName;
                    string name = _gl.GetActiveUniform(ProgramId, index, out size, out type, out typeName);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("gl_", StringComparison.Ordinal)) continue;

                    UniformCopy shape;
                    if (!Shape(type, typeName, out shape))
                    {
                        _logger.Warning("[VintageVisuals] split chunk programs: uniform '" + name + "' has a type (" +
                                        typeName + ") the copy does not handle; the " + Label + " twin will not be used.");
                        return false;
                    }

                    string baseName = name.EndsWith("[0]", StringComparison.Ordinal) ? name.Substring(0, name.Length - 3) : name;
                    bool isArray = size > 1 || name.EndsWith("[0]", StringComparison.Ordinal);
                    for (int e = 0; e < Math.Max(1, size); e++)
                    {
                        string element = isArray ? baseName + "[" + e.ToString(CultureInfo.InvariantCulture) + "]" : baseName;
                        int to = _gl.GetUniformLocation(ProgramId, element);
                        int from = _gl.GetUniformLocation(mainId, element);
                        // Inactive in the game's program means the game's
                        // compiler proved nothing reads it there; nothing to copy.
                        if (to < 0 || from < 0) continue;
                        UniformCopy c = shape;
                        c.From = from;
                        c.To = to;
                        (isArray ? arrays : scalars).Add(c);
                    }
                }
                _scalarCount = scalars.Count;
                scalars.AddRange(arrays);
                _copies = scalars.ToArray();
                return true;
            }

            private static bool Shape(int type, string typeName, out UniformCopy c)
            {
                c = default(UniformCopy);
                switch (type)
                {
                    case 0x1406: c.Kind = 0; c.Components = 1; return true; // float
                    case 0x8B50: c.Kind = 0; c.Components = 2; return true; // vec2
                    case 0x8B51: c.Kind = 0; c.Components = 3; return true; // vec3
                    case 0x8B52: c.Kind = 0; c.Components = 4; return true; // vec4
                    case 0x1404: case 0x8B56: c.Kind = 1; c.Components = 1; return true; // int, bool
                    case 0x8B53: case 0x8B57: c.Kind = 1; c.Components = 2; return true;
                    case 0x8B54: case 0x8B58: c.Kind = 1; c.Components = 3; return true;
                    case 0x8B55: case 0x8B59: c.Kind = 1; c.Components = 4; return true;
                    case 0x8B5B: c.Kind = 2; c.Components = 9; return true;  // mat3
                    case 0x8B5C: c.Kind = 3; c.Components = 16; return true; // mat4
                    case 0x8B6A: c.Kind = 4; c.Components = 12; return true; // mat4x3
                }
                // Every sampler type: its value is a texture unit, an int.
                if (typeName != null && typeName.IndexOf("Sampler", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    c.Kind = 1; c.Components = 1;
                    return true;
                }
                return false;
            }

            /// <summary>Copies the game's program's current values into the twin; arrays only when <paramref name="full"/>.</summary>
            public void CopyFrom(int mainId, bool full)
            {
                int n = full ? _copies.Length : _scalarCount;
                for (int k = 0; k < n; k++)
                {
                    UniformCopy c = _copies[k];
                    switch (c.Kind)
                    {
                        case 0:
                            _gl.GetUniformF(mainId, c.From, _f);
                            _gl.ProgramUniformF[c.Components](ProgramId, c.To, 1, _f);
                            break;
                        case 1:
                            _gl.GetUniformI(mainId, c.From, _i);
                            _gl.ProgramUniformI[c.Components](ProgramId, c.To, 1, _i);
                            break;
                        case 2:
                            _gl.GetUniformF(mainId, c.From, _f);
                            _gl.ProgramUniformMatrix3(ProgramId, c.To, 1, false, _f);
                            break;
                        case 3:
                            _gl.GetUniformF(mainId, c.From, _f);
                            _gl.ProgramUniformMatrix4(ProgramId, c.To, 1, false, _f);
                            break;
                        case 4:
                            _gl.GetUniformF(mainId, c.From, _f);
                            _gl.ProgramUniformMatrix4x3(ProgramId, c.To, 1, false, _f);
                            break;
                    }
                }
            }
        }

        // ---- the register readout ('.vvprofile shaders') --------------------------

        /// <summary>
        /// One line per chunk program the game and the split use: GL handle,
        /// active uniforms, binary size, and the compiler's own instruction and
        /// register counts where the driver's binary carries them (NVIDIA does,
        /// as assembly comments). Must run on the render thread.
        /// </summary>
        public static string ShaderReport()
        {
            var sb = new StringBuilder();
            if (_gl == null) return "split chunk programs unavailable" + (_failed ? " (refused or failed, see the log)" : "") + "\n";
            sb.Append("split: ").Append(Wanted() ? "on" : "off")
              .Append(", last frame prepass=").Append(DepthTwinLastFrame ? "twin" : "main")
              .Append(" leaves=").Append(FloraTwinLastFrame ? "twin" : "main").Append('\n');
            IShaderProgram main = GameProgram();
            if (main != null && !_swapped) Describe(sb, "chunkopaque", (int)_fProgramId.GetValue(main));
            IShaderProgram topsoil = _capi.Shader.GetProgram((int)EnumShaderProgram.Chunktopsoil);
            if (topsoil != null) Describe(sb, "chunktopsoil", (int)_fProgramId.GetValue(topsoil));
            if (_depth != null && _depth.Usable) Describe(sb, "chunkopaque-depth", _depth.ProgramId);
            else sb.Append("chunkopaque-depth  not built\n");
            if (_flora != null && _flora.Usable) Describe(sb, "chunkopaque-flora", _flora.ProgramId);
            else sb.Append("chunkopaque-flora  not built\n");
            return sb.ToString();
        }

        private static void Describe(StringBuilder sb, string name, int program)
        {
            sb.Append(name.PadRight(19)).Append(" program ").Append(program)
              .Append(", ").Append(_gl.GetProgramInt(program, "ActiveUniforms")).Append(" active uniforms");
            int bytes;
            string text = null;
            try { text = _gl.ProgramBinaryText(program, out bytes); }
            catch (Exception ex) { sb.Append(", binary unreadable: ").Append(ex.Message).Append('\n'); return; }
            if (text == null) { sb.Append(", no binary\n"); return; }
            sb.Append(", binary ").Append((bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture)).Append(" KB");
            // NVIDIA: "# 1234 instructions, 56 R-regs" once per stage.
            var stats = new List<string>();
            foreach (Match m in Regex.Matches(text, @"(\d+)\s+instructions,\s*(\d+)\s+R-regs"))
                stats.Add(m.Groups[1].Value + " instr / " + m.Groups[2].Value + " regs");
            sb.Append(stats.Count > 0 ? ", " + string.Join(" | ", stats) : ", no register counts in this driver's binary").Append('\n');
        }
    }
}
