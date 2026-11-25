using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageVisuals.Common.Patching
{
    /// <summary>
    /// The texture slot report: which texture unit every sampler of the terrain
    /// programs is actually pointed at, read back from the driver.
    ///
    /// Two of this mod's worst failures were sampler-unit faults that compiled
    /// cleanly: a sampler declared above vanilla's shifted every unit below it,
    /// and a mod texture bound over a vanilla unit took the frame. Neither is
    /// visible from C# or from glslang. This reads the linked program's active
    /// sampler list and each sampler's current unit, and WARNS when two
    /// samplers of different texture types share a unit - the one state the
    /// driver will silently render wrong.
    ///
    /// It runs twice per shader reload: once right after link, and once a few
    /// seconds later, after the binders have uploaded their units. A mod sampler
    /// the binder has not set yet reads unit 0 in the first report; that is the
    /// unset default, not a collision, and the second report is the one to read.
    ///
    /// Everything here is reflection over OpenTK, which is not a stable API. A
    /// missing overload is logged loudly by name rather than skipped silently,
    /// because a report that quietly prints nothing reads as "no samplers".
    /// </summary>
    public static class RuntimeShaderIntrospector
    {
        public const int SettledReportDelayMs = 8000;

        public static void LogTerrainPrograms(ICoreClientAPI capi, ILogger logger)
        {
            if (capi == null || logger == null) return;

            Report(capi, logger, "after link");
            try
            {
                capi.Event.RegisterCallback(_ => Report(capi, logger, "settled"), SettledReportDelayMs);
            }
            catch (Exception ex)
            {
                logger.Warning("[VintageVisuals] texture slot report: could not schedule the settled report: " + ex.Message);
            }
        }

        private static void Report(ICoreClientAPI capi, ILogger logger, string when)
        {
            try
            {
                LogProgram(capi, logger, EnumShaderProgram.Chunkopaque, "chunkopaque", when);
                LogProgram(capi, logger, EnumShaderProgram.Chunktopsoil, "chunktopsoil", when);
            }
            catch (Exception ex)
            {
                logger.Warning("[VintageVisuals] texture slot report disabled after an unexpected diagnostics error: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void LogProgram(ICoreClientAPI capi, ILogger logger, EnumShaderProgram id, string name, string when)
        {
            IShaderProgram program = capi.Shader.GetProgram((int)id);
            if (program == null)
            {
                logger.Warning("[VintageVisuals] texture slot report: " + name + " program is not loaded.");
                return;
            }

            int handle;
            if (!TryGetProgramHandle(program, out handle))
            {
                logger.Warning("[VintageVisuals] texture slot report: " + name +
                    " exists but its OpenGL program handle is not exposed by the current Vintage Story API shape.");
                return;
            }

            var gl = ReflectionGlApi.TryCreate();
            if (gl == null)
            {
                logger.Warning("[VintageVisuals] texture slot report: OpenTK GL type was not found; " +
                    name + " sampler map cannot be read at runtime.");
                return;
            }

            int maxUnits = gl.GetInteger("MaxTextureImageUnits");
            int activeUniforms = gl.GetProgramInt(handle, "ActiveUniforms");
            logger.Notification("[VintageVisuals] texture slot report (" + when + "): " + name +
                " program=" + handle +
                " linkStatus=" + gl.GetProgramInt(handle, "LinkStatus") +
                " activeUniforms=" + activeUniforms +
                " GL_MAX_TEXTURE_IMAGE_UNITS=" + maxUnits +
                " GL_MAX_COMBINED_TEXTURE_IMAGE_UNITS=" + gl.GetInteger("MaxCombinedTextureImageUnits"));

            if (!gl.HasActiveUniformQuery)
            {
                logger.Warning("[VintageVisuals] texture slot report: no usable GL.GetActiveUniform overload on " +
                    gl.TypeName + "; expected String GetActiveUniform(Int32, Int32, out Int32, out ActiveUniformType). " +
                    "The " + name + " sampler map is NOT being read, which is not the same as having no samplers.");
                return;
            }

            var samplers = new System.Collections.Generic.List<ActiveUniformInfo>();
            for (int i = 0; i < Math.Max(0, activeUniforms); i++)
            {
                ActiveUniformInfo uniform;
                if (!gl.TryGetActiveUniform(handle, i, out uniform)) continue;
                if (uniform.Type.IndexOf("Sampler", StringComparison.OrdinalIgnoreCase) < 0) continue;

                int location = gl.GetUniformLocation(handle, uniform.Name);
                uniform.Unit = location >= 0 ? gl.GetUniformInt(handle, location) : -1;
                samplers.Add(uniform);
            }

            logger.Notification("[VintageVisuals] texture slot report (" + when + "): " + name + " " + samplers.Count +
                " samplers: " + string.Join(", ", samplers.Select(u => u.Name + "=" + u.Unit + " (" + u.Type + ")")));

            if (samplers.Count == 0)
            {
                logger.Warning("[VintageVisuals] texture slot report: " + name +
                    " reported no active samplers. Every terrain program samples at least the block atlas, so the query failed.");
                return;
            }

            foreach (var group in samplers.Where(u => u.Unit >= 0).GroupBy(u => u.Unit))
            {
                if (group.Select(u => u.Type).Distinct().Count() > 1)
                {
                    logger.Warning("[VintageVisuals] texture slot report (" + when + "): " + name + " unit " + group.Key +
                        " is shared by samplers of different types: " + string.Join(", ", group.Select(u => u.Name + " (" + u.Type + ")")) +
                        ". The driver renders this wrong without an error." +
                        (when == "after link" ? " A mod sampler not yet uploaded reads 0 here; check the settled report." : ""));
                }
            }

            foreach (var u in samplers.Where(u => maxUnits > 0 && u.Unit >= maxUnits))
            {
                logger.Warning("[VintageVisuals] texture slot report: " + name + " sampler " + u.Name +
                    " is on unit " + u.Unit + ", beyond GL_MAX_TEXTURE_IMAGE_UNITS=" + maxUnits + ".");
            }
        }

        /// <summary>
        /// For tools/smoketest: which of the queries this report depends on the
        /// given OpenTK GL binding type does not provide. Empty means the report
        /// can read the sampler map on that binding.
        /// </summary>
        public static System.Collections.Generic.List<string> MissingGlQueries(Type glType)
        {
            return new ReflectionGlApi(glType).Missing();
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
            public int Unit;
        }

        private sealed class ReflectionGlApi
        {
            private readonly Type _glType;

            internal ReflectionGlApi(Type glType)
            {
                _glType = glType;
            }

            /// <summary>Names of the GL queries this report needs that the given binding lacks.</summary>
            internal System.Collections.Generic.List<string> Missing()
            {
                var missing = new System.Collections.Generic.List<string>();
                var methods = _glType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                if (!methods.Any(IsActiveUniformMethod)) missing.Add("GetActiveUniform");
                if (!methods.Any(m => m.Name == "GetUniform" && m.GetParameters().Length == 3 &&
                                      m.GetParameters()[0].ParameterType == typeof(int) &&
                                      m.GetParameters()[1].ParameterType == typeof(int) &&
                                      m.GetParameters()[2].ParameterType == typeof(int).MakeByRefType()))
                    missing.Add("GetUniform(int, int, out int)");
                if (!methods.Any(m => m.Name == "GetUniformLocation" && m.GetParameters().Length == 2 &&
                                      m.GetParameters()[0].ParameterType == typeof(int) &&
                                      m.GetParameters()[1].ParameterType == typeof(string)))
                    missing.Add("GetUniformLocation(int, string)");
                foreach (string pname in new[] { "LinkStatus", "ActiveUniforms" })
                {
                    if (!methods.Any(m => m.Name == "GetProgram" && m.GetParameters().Length == 3 &&
                                          m.GetParameters()[0].ParameterType == typeof(int) &&
                                          m.GetParameters()[2].ParameterType == typeof(int).MakeByRefType() &&
                                          m.GetParameters()[1].ParameterType.IsEnum &&
                                          Enum.IsDefined(m.GetParameters()[1].ParameterType, pname)))
                        missing.Add("GetProgram(" + pname + ")");
                }
                foreach (string pname in new[] { "MaxTextureImageUnits", "MaxCombinedTextureImageUnits" })
                {
                    if (!methods.Any(m => m.Name == "GetInteger" && m.GetParameters().Length == 2 &&
                                          m.GetParameters()[1].ParameterType == typeof(int).MakeByRefType() &&
                                          m.GetParameters()[0].ParameterType.IsEnum &&
                                          Enum.IsDefined(m.GetParameters()[0].ParameterType, pname)))
                        missing.Add("GetInteger(" + pname + ")");
                }
                return missing;
            }

            public static ReflectionGlApi TryCreate()
            {
                // OpenTK.Graphics.OpenGL is the binding Vintage Story itself
                // calls; OpenGL4 is a separate class whose entry points may not
                // have been loaded, so it is only a fallback.
                Type[] types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).ToArray();
                Type glType = types.FirstOrDefault(t => t.FullName == "OpenTK.Graphics.OpenGL.GL")
                           ?? types.FirstOrDefault(t => t.FullName == "OpenTK.Graphics.OpenGL4.GL");
                return glType == null ? null : new ReflectionGlApi(glType);
            }

            public string TypeName { get { return _glType.FullName; } }

            public bool HasActiveUniformQuery
            {
                get { return _glType.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(IsActiveUniformMethod); }
            }

            public int GetInteger(string enumName)
            {
                MethodInfo method = _glType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetInteger" &&
                                         m.GetParameters().Length == 2 &&
                                         m.GetParameters()[1].ParameterType == typeof(int).MakeByRefType() &&
                                         m.GetParameters()[0].ParameterType.IsEnum &&
                                         Enum.IsDefined(m.GetParameters()[0].ParameterType, enumName));
                if (method == null) return -1;

                Type enumType = method.GetParameters()[0].ParameterType;
                if (!enumType.IsEnum || !Enum.IsDefined(enumType, enumName)) return -1;
                object enumValue = Enum.Parse(enumType, enumName);
                object[] args = { enumValue, 0 };
                method.Invoke(null, args);
                return (int)args[1];
            }

            public int GetProgramInt(int program, string enumName)
            {
                // OpenTK.Graphics.OpenGL has two such overloads, one per enum
                // type; take the one whose enum actually has this name.
                MethodInfo method = _glType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetProgram" &&
                                         m.GetParameters().Length == 3 &&
                                         m.GetParameters()[0].ParameterType == typeof(int) &&
                                         m.GetParameters()[2].ParameterType == typeof(int).MakeByRefType() &&
                                         m.GetParameters()[1].ParameterType.IsEnum &&
                                         Enum.IsDefined(m.GetParameters()[1].ParameterType, enumName));
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
                    Type uniformType = method.GetParameters()[3].ParameterType.GetElementType();
                    object[] args = { program, index, 0, Enum.ToObject(uniformType, 0) };
                    string name = method.Invoke(null, args) as string;
                    uniform.Name = name;
                    uniform.Type = args[3].ToString();
                    return !string.IsNullOrEmpty(name);
                }
                catch
                {
                    return false;
                }
            }

            /// <summary>
            /// OpenTK 4's managed convenience overload: String GetActiveUniform(
            /// int program, int index, out int size, out ActiveUniformType type).
            /// The StringBuilder overload this used to look for does not exist
            /// in the OpenTK the game ships, so the old report never printed a
            /// single sampler.
            /// </summary>
            private static bool IsActiveUniformMethod(MethodInfo method)
            {
                if (method.Name != "GetActiveUniform" || method.ReturnType != typeof(string)) return false;

                ParameterInfo[] p = method.GetParameters();
                return p.Length == 4 &&
                       p[0].ParameterType == typeof(int) &&
                       p[1].ParameterType == typeof(int) &&
                       p[2].ParameterType == typeof(int).MakeByRefType() &&
                       p[3].ParameterType.IsByRef &&
                       p[3].ParameterType.GetElementType().IsEnum;
            }

            private static Type[] SafeTypes(Assembly assembly)
            {
                try { return assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
            }
        }
    }
}
