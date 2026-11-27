using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VintageVisuals.Common.Rendering
{
    /// <summary>
    /// D100. The raw GL calls the split chunk programs need, reached through
    /// OpenTK by reflection like every other GL bridge in the mod (the project
    /// does not compile against OpenTK; the game owns that dependency).
    ///
    /// The per-frame calls (read a uniform, write one into another program,
    /// bind a program) are bound once as typed delegates on overloads whose
    /// parameters are all primitives, so the hot path makes no reflection call
    /// and boxes nothing. The rare ones (enumerating a program's uniforms,
    /// reading its binary) use plain reflection.
    ///
    /// Writing goes through glProgramUniform* (GL 4.1), which sets a uniform on
    /// a program WITHOUT binding it, so a copy never disturbs what the game has
    /// bound. Null when any required entry point is missing; the split then
    /// refuses to run and the game's program draws everything as before.
    /// </summary>
    internal sealed class ChunkSplitGl
    {
        public readonly Action<int> UseProgram;
        public readonly Func<int, string, int> GetUniformLocation;
        public readonly Action<int, int, float[]> GetUniformF;
        public readonly Action<int, int, int[]> GetUniformI;
        public readonly Action<int, int, int, float[]>[] ProgramUniformF = new Action<int, int, int, float[]>[5];
        public readonly Action<int, int, int, int[]>[] ProgramUniformI = new Action<int, int, int, int[]>[5];
        public readonly Action<int, int, int, bool, float[]> ProgramUniformMatrix3;
        public readonly Action<int, int, int, bool, float[]> ProgramUniformMatrix4;
        public readonly Action<int, int, int, bool, float[]> ProgramUniformMatrix4x3;

        private readonly MethodInfo _getProgram;
        private readonly Type _programParamType;
        private readonly MethodInfo _getActiveUniform;
        private readonly Type _uniformTypeType;
        private readonly MethodInfo _getProgramBinary;
        private readonly Type _binaryFormatType;

        private ChunkSplitGl(Type gl, MethodInfo[] all)
        {
            UseProgram = Bind<Action<int>>(all, "UseProgram", typeof(int));
            GetUniformLocation = Bind<Func<int, string, int>>(all, "GetUniformLocation", typeof(int), typeof(string));
            GetUniformF = Bind<Action<int, int, float[]>>(all, "GetUniform", typeof(int), typeof(int), typeof(float[]));
            GetUniformI = Bind<Action<int, int, int[]>>(all, "GetUniform", typeof(int), typeof(int), typeof(int[]));
            for (int n = 1; n <= 4; n++)
            {
                ProgramUniformF[n] = Bind<Action<int, int, int, float[]>>(all, "ProgramUniform" + n,
                    typeof(int), typeof(int), typeof(int), typeof(float[]));
                ProgramUniformI[n] = Bind<Action<int, int, int, int[]>>(all, "ProgramUniform" + n,
                    typeof(int), typeof(int), typeof(int), typeof(int[]));
            }
            ProgramUniformMatrix3 = Bind<Action<int, int, int, bool, float[]>>(all, "ProgramUniformMatrix3",
                typeof(int), typeof(int), typeof(int), typeof(bool), typeof(float[]));
            ProgramUniformMatrix4 = Bind<Action<int, int, int, bool, float[]>>(all, "ProgramUniformMatrix4",
                typeof(int), typeof(int), typeof(int), typeof(bool), typeof(float[]));
            ProgramUniformMatrix4x3 = Bind<Action<int, int, int, bool, float[]>>(all, "ProgramUniformMatrix4x3",
                typeof(int), typeof(int), typeof(int), typeof(bool), typeof(float[]));

            _getProgram = all.FirstOrDefault(m => m.Name == "GetProgram" && Params(m, typeof(int), null, typeof(int).MakeByRefType()) &&
                                                  m.GetParameters()[1].ParameterType.IsEnum &&
                                                  Enum.IsDefined(m.GetParameters()[1].ParameterType, "ActiveUniforms"));
            _programParamType = _getProgram?.GetParameters()[1].ParameterType;

            _getActiveUniform = all.FirstOrDefault(m => m.Name == "GetActiveUniform" && m.ReturnType == typeof(string) &&
                                                        m.GetParameters().Length == 4 &&
                                                        m.GetParameters()[0].ParameterType == typeof(int) &&
                                                        m.GetParameters()[1].ParameterType == typeof(int) &&
                                                        m.GetParameters()[2].ParameterType == typeof(int).MakeByRefType() &&
                                                        m.GetParameters()[3].ParameterType.IsByRef &&
                                                        m.GetParameters()[3].ParameterType.GetElementType().IsEnum);
            _uniformTypeType = _getActiveUniform?.GetParameters()[3].ParameterType.GetElementType();

            _getProgramBinary = all.FirstOrDefault(m => m.Name == "GetProgramBinary" && !m.IsGenericMethodDefinition &&
                                                        m.GetParameters().Length == 5 &&
                                                        m.GetParameters()[0].ParameterType == typeof(int) &&
                                                        m.GetParameters()[4].ParameterType == typeof(IntPtr));
            _binaryFormatType = _getProgramBinary?.GetParameters()[3].ParameterType.GetElementType();
        }

        /// <summary>Whether everything the split needs to draw was found. The binary readout is optional.</summary>
        public bool Complete
        {
            get
            {
                return UseProgram != null && GetUniformLocation != null && GetUniformF != null && GetUniformI != null &&
                       ProgramUniformF.Skip(1).All(d => d != null) && ProgramUniformI.Skip(1).All(d => d != null) &&
                       ProgramUniformMatrix3 != null && ProgramUniformMatrix4 != null && ProgramUniformMatrix4x3 != null &&
                       _getProgram != null && _getActiveUniform != null;
            }
        }

        public static ChunkSplitGl TryCreate(out string why)
        {
            why = null;
            try
            {
                Type gl = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeTypes)
                    .Where(t => t.FullName == "OpenTK.Graphics.OpenGL.GL" || t.FullName == "OpenTK.Graphics.OpenGL4.GL")
                    .OrderBy(t => t.FullName == "OpenTK.Graphics.OpenGL.GL" ? 0 : 1)
                    .FirstOrDefault();
                if (gl == null) { why = "OpenTK GL type not found"; return null; }
                var api = new ChunkSplitGl(gl, gl.GetMethods(BindingFlags.Public | BindingFlags.Static));
                if (!api.Complete) { why = "an OpenTK GL entry point the split needs was not found"; return null; }
                return api;
            }
            catch (Exception ex)
            {
                why = ex.Message;
                return null;
            }
        }

        public int GetProgramInt(int program, string name)
        {
            if (_getProgram == null || !Enum.IsDefined(_programParamType, name)) return -1;
            object[] args = { program, Enum.Parse(_programParamType, name), 0 };
            _getProgram.Invoke(null, args);
            return (int)args[2];
        }

        /// <summary>glGetActiveUniform: name, array size and GL type code of one active uniform.</summary>
        public string GetActiveUniform(int program, int index, out int size, out int type, out string typeName)
        {
            object[] args = { program, index, 0, Enum.ToObject(_uniformTypeType, 0) };
            string name = _getActiveUniform.Invoke(null, args) as string;
            size = (int)args[2];
            type = Convert.ToInt32(args[3]);
            typeName = args[3].ToString();
            return name;
        }

        /// <summary>
        /// The program binary's printable text, or null. NVIDIA's binary carries
        /// the compiled assembly with "# N instructions, M R-regs" comments, which
        /// is the one register count a running game can read back.
        /// </summary>
        public string ProgramBinaryText(int program, out int bytes)
        {
            bytes = 0;
            if (_getProgramBinary == null) return null;
            int length = GetProgramInt(program, "ProgramBinaryLength");
            if (length <= 0 || length > 64 * 1024 * 1024) return null;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                object[] args = { program, length, 0, Enum.ToObject(_binaryFormatType, 0), buffer };
                _getProgramBinary.Invoke(null, args);
                bytes = (int)args[2];
                if (bytes <= 0) return null;
                var raw = new byte[bytes];
                Marshal.Copy(buffer, raw, 0, bytes);
                var sb = new StringBuilder(bytes);
                foreach (byte b in raw) sb.Append(b >= 32 && b < 127 || b == 10 ? (char)b : '\n');
                return sb.ToString();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static T Bind<T>(MethodInfo[] all, string name, params Type[] types) where T : class
        {
            MethodInfo m = all.FirstOrDefault(x => x.Name == name && !x.IsGenericMethodDefinition && Params(x, types));
            if (m == null) return null;
            try { return Delegate.CreateDelegate(typeof(T), m) as T; }
            catch { return null; }
        }

        private static bool Params(MethodInfo m, params Type[] types)
        {
            ParameterInfo[] p = m.GetParameters();
            if (p.Length != types.Length) return false;
            for (int i = 0; i < p.Length; i++)
            {
                if (types[i] != null && p[i].ParameterType != types[i]) return false;
            }
            return true;
        }

        private static Type[] SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
        }
    }
}
