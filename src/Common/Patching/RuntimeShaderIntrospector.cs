using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageVisuals.Common.Patching
{
    public static class RuntimeShaderIntrospector
    {
        private static bool _reported;

        public static void LogTerrainPrograms(ICoreClientAPI capi, ILogger logger)
        {
            if (_reported || capi == null || logger == null) return;
            _reported = true;

            try
            {
                LogProgram(capi, logger, EnumShaderProgram.Chunkopaque, "chunkopaque");
                LogProgram(capi, logger, EnumShaderProgram.Chunktopsoil, "chunktopsoil");
            }
            catch (Exception ex)
            {
                logger.Warning("[VintageVisuals] shader introspection disabled after an unexpected diagnostics error: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void LogProgram(ICoreClientAPI capi, ILogger logger, EnumShaderProgram id, string name)
        {
            IShaderProgram program = capi.Shader.GetProgram((int)id);
            if (program == null)
            {
                logger.Warning("[VintageVisuals] shader introspection: " + name + " program is not loaded.");
                return;
            }

            int handle;
            if (!TryGetProgramHandle(program, out handle))
            {
                logger.Warning("[VintageVisuals] shader introspection: " + name +
                    " exists but its OpenGL program handle is not exposed by the current Vintage Story API shape.");
                return;
            }

            var gl = ReflectionGlApi.TryCreate();
            if (gl == null)
            {
                logger.Warning("[VintageVisuals] shader introspection: OpenTK GL reflection API was not found; " +
                    name + " sampler map cannot be read at runtime.");
                return;
            }

            logger.Notification("[VintageVisuals] shader introspection: " + name +
                " program=" + handle +
                " linkStatus=" + gl.GetProgramInt(handle, "LinkStatus") +
                " validateStatus=" + gl.GetProgramInt(handle, "ValidateStatus") +
                " activeUniforms=" + gl.GetProgramInt(handle, "ActiveUniforms") +
                " GL_MAX_TEXTURE_IMAGE_UNITS=" + gl.GetInteger("MaxTextureImageUnits") +
                " GL_MAX_COMBINED_TEXTURE_IMAGE_UNITS=" + gl.GetInteger("MaxCombinedTextureImageUnits"));

            int activeUniforms = Math.Max(0, gl.GetProgramInt(handle, "ActiveUniforms"));
            for (int i = 0; i < activeUniforms; i++)
            {
                ActiveUniformInfo uniform;
                if (!gl.TryGetActiveUniform(handle, i, out uniform)) continue;
                if (uniform.Type.IndexOf("Sampler", StringComparison.OrdinalIgnoreCase) < 0) continue;

                int location = gl.GetUniformLocation(handle, uniform.Name);
                int unit = location >= 0 ? gl.GetUniformInt(handle, location) : -1;
                logger.Notification("[VintageVisuals] shader introspection: " + name +
                    " sampler " + uniform.Name +
                    " type=" + uniform.Type +
                    " location=" + location +
                    " unit=" + unit);
            }
        }

        private static bool TryGetProgramHandle(IShaderProgram program, out int handle)
        {
            handle = 0;
            Type type = program.GetType();
            foreach (string name in new[] { "ProgramId", "programid", "Handle", "handle", "Id", "Program" })
            {
                PropertyInfo prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && TryConvertInt(prop.GetValue(program, null), out handle)) return handle > 0;

                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && TryConvertInt(field.GetValue(program), out handle)) return handle > 0;
            }

            return false;
        }

        private static bool TryConvertInt(object value, out int result)
        {
            result = 0;
            if (value == null) return false;
            try
            {
                result = Convert.ToInt32(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private struct ActiveUniformInfo
        {
            public string Name;
            public string Type;
        }

        private sealed class ReflectionGlApi
        {
            private readonly Type _glType;

            private ReflectionGlApi(Type glType)
            {
                _glType = glType;
            }

            public static ReflectionGlApi TryCreate()
            {
                Type glType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeTypes)
                    .FirstOrDefault(t => t.FullName == "OpenTK.Graphics.OpenGL.GL" ||
                                         t.FullName == "OpenTK.Graphics.OpenGL4.GL");
                return glType == null ? null : new ReflectionGlApi(glType);
            }

            public int GetInteger(string enumName)
            {
                MethodInfo method = _glType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetInteger" &&
                                         m.GetParameters().Length == 2 &&
                                         m.GetParameters()[1].ParameterType == typeof(int).MakeByRefType());
                if (method == null) return -1;

                Type enumType = method.GetParameters()[0].ParameterType;
                object enumValue = Enum.Parse(enumType, enumName);
                object[] args = { enumValue, 0 };
                method.Invoke(null, args);
                return (int)args[1];
            }

            public int GetProgramInt(int program, string enumName)
            {
                MethodInfo method = _glType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetProgram" &&
                                         m.GetParameters().Length == 3 &&
                                         m.GetParameters()[0].ParameterType == typeof(int) &&
                                         m.GetParameters()[2].ParameterType == typeof(int).MakeByRefType());
                if (method == null) return -1;

                Type enumType = method.GetParameters()[1].ParameterType;
                object enumValue = Enum.Parse(enumType, enumName);
                object[] args = { program, enumValue, 0 };
                method.Invoke(null, args);
                return (int)args[2];
            }

            public int GetUniformLocation(int program, string name)
            {
                MethodInfo method = _glType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetUniformLocation" &&
                                         m.GetParameters().Length == 2 &&
                                         m.GetParameters()[0].ParameterType == typeof(int) &&
                                         m.GetParameters()[1].ParameterType == typeof(string));
                return method == null ? -1 : Convert.ToInt32(method.Invoke(null, new object[] { program, name }));
            }

            public int GetUniformInt(int program, int location)
            {
                MethodInfo method = _glType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetUniform" &&
                                         m.GetParameters().Length == 3 &&
                                         m.GetParameters()[0].ParameterType == typeof(int) &&
                                         m.GetParameters()[1].ParameterType == typeof(int) &&
                                         m.GetParameters()[2].ParameterType == typeof(int).MakeByRefType());
                if (method == null) return -1;

                object[] args = { program, location, 0 };
                method.Invoke(null, args);
                return (int)args[2];
            }

            public bool TryGetActiveUniform(int program, int index, out ActiveUniformInfo uniform)
            {
                uniform = default(ActiveUniformInfo);
                MethodInfo method = _glType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(IsActiveUniformMethod);
                if (method == null) return false;

                try
                {
                    ParameterInfo[] p = method.GetParameters();
                    int typeIndex = FindEnumParameter(p);
                    int nameIndex = FindStringBuilderParameter(p);
                    if (typeIndex < 0 || nameIndex < 0) return false;

                    Type uniformType = ByRefElement(p[typeIndex].ParameterType);
                    if (uniformType == null || !uniformType.IsEnum) return false;

                    object[] args = new object[p.Length];
                    args[0] = program;
                    args[1] = index;
                    args[2] = 256;

                    for (int i = 3; i < args.Length; i++)
                    {
                        Type parameterType = ByRefElement(p[i].ParameterType);
                        if (i == typeIndex) args[i] = Enum.ToObject(uniformType, 0);
                        else if (i == nameIndex) args[i] = new StringBuilder(256);
                        else if (parameterType == typeof(int)) args[i] = 0;
                        else return false;
                    }

                    method.Invoke(null, args);
                    uniform.Name = args[nameIndex].ToString();
                    uniform.Type = args[typeIndex].ToString();
                    return !string.IsNullOrEmpty(uniform.Name);
                }
                catch
                {
                    return false;
                }
            }

            private static bool IsActiveUniformMethod(MethodInfo method)
            {
                if (method.Name != "GetActiveUniform") return false;

                ParameterInfo[] p = method.GetParameters();
                return p.Length >= 7 &&
                       p[0].ParameterType == typeof(int) &&
                       p[1].ParameterType == typeof(int) &&
                       p[2].ParameterType == typeof(int) &&
                       FindEnumParameter(p) >= 0 &&
                       FindStringBuilderParameter(p) >= 0;
            }

            private static int FindEnumParameter(ParameterInfo[] parameters)
            {
                for (int i = 3; i < parameters.Length; i++)
                {
                    Type type = ByRefElement(parameters[i].ParameterType);
                    if (type != null && type.IsEnum) return i;
                }

                return -1;
            }

            private static int FindStringBuilderParameter(ParameterInfo[] parameters)
            {
                for (int i = 3; i < parameters.Length; i++)
                {
                    if (ByRefElement(parameters[i].ParameterType) == typeof(StringBuilder)) return i;
                }

                return -1;
            }

            private static Type ByRefElement(Type type)
            {
                return type != null && type.IsByRef ? type.GetElementType() : type;
            }

            private static Type[] SafeTypes(Assembly assembly)
            {
                try { return assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
            }
        }
    }
}
