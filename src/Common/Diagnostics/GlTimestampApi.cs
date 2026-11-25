using System;
using System.Linq;
using System.Reflection;

namespace VintageVisuals.Common.Diagnostics
{
    /// <summary>
    /// GPU timestamp queries (GL_TIMESTAMP via glQueryCounter), reached by
    /// reflection.
    ///
    /// The project deliberately does not compile against OpenTK - Vintage Story
    /// owns that dependency and has moved it between namespaces before - so
    /// this follows the same fail-closed bridge as
    /// TerrainTextureBindInterceptor: if any method cannot be found the
    /// profiler reports "GPU timing unavailable" and measures CPU time only.
    ///
    /// Timestamps rather than GL_TIME_ELAPSED because elapsed-time queries
    /// cannot nest or overlap, and a stage bracket always contains the mod
    /// pass brackets inside it. A timestamp is a single point, so any number
    /// of them can be laid down in any order and differenced afterwards.
    /// </summary>
    internal sealed class GlTimestampApi
    {
        private readonly MethodInfo _genQuery;
        private readonly MethodInfo _deleteQuery;
        private readonly MethodInfo _queryCounter;
        private readonly MethodInfo _getQueryObject;
        private readonly object _timestampTarget;
        private readonly object _resultParam;
        private readonly object _availableParam;

        // Reused argument arrays: these calls happen ~40 times a frame, and
        // allocating a fresh object[] for each one would be garbage the
        // profiler itself adds to the frame it is measuring.
        private readonly object[] _oneArg = new object[1];
        private readonly object[] _counterArgs = new object[2];
        private readonly object[] _getArgs = new object[3];
        private readonly long[] _result = new long[1];

        private GlTimestampApi(MethodInfo genQuery, MethodInfo deleteQuery, MethodInfo queryCounter,
                               MethodInfo getQueryObject, object timestampTarget,
                               object resultParam, object availableParam)
        {
            _genQuery = genQuery;
            _deleteQuery = deleteQuery;
            _queryCounter = queryCounter;
            _getQueryObject = getQueryObject;
            _timestampTarget = timestampTarget;
            _resultParam = resultParam;
            _availableParam = availableParam;
        }

        /// <summary>Why <see cref="TryCreate"/> returned null, for the log.</summary>
        public static string LastError { get; private set; } = "";

        public static GlTimestampApi TryCreate()
        {
            try
            {
                Type glType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeTypes)
                    .Where(t => t.FullName == "OpenTK.Graphics.OpenGL.GL" ||
                                t.FullName == "OpenTK.Graphics.OpenGL4.GL")
                    .OrderBy(t => t.FullName == "OpenTK.Graphics.OpenGL.GL" ? 0 : 1)
                    .FirstOrDefault();
                if (glType == null) { LastError = "no OpenTK GL type loaded"; return null; }

                MethodInfo[] methods = glType.GetMethods(BindingFlags.Public | BindingFlags.Static);

                MethodInfo genQuery = methods.FirstOrDefault(m =>
                    m.Name == "GenQuery" && m.GetParameters().Length == 0 && m.ReturnType == typeof(int));
                MethodInfo deleteQuery = methods.FirstOrDefault(m =>
                    m.Name == "DeleteQuery" && m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(int));
                MethodInfo queryCounter = methods.FirstOrDefault(m =>
                    m.Name == "QueryCounter" && m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == typeof(int) &&
                    m.GetParameters()[1].ParameterType.IsEnum);
                MethodInfo getQueryObject = methods.FirstOrDefault(m =>
                    m.Name == "GetQueryObject" && m.GetParameters().Length == 3 &&
                    m.GetParameters()[0].ParameterType == typeof(int) &&
                    m.GetParameters()[1].ParameterType.IsEnum &&
                    m.GetParameters()[2].ParameterType == typeof(long[]));

                if (genQuery == null || deleteQuery == null || queryCounter == null || getQueryObject == null)
                {
                    LastError = "a query method is missing from " + glType.FullName;
                    return null;
                }

                object timestamp = Enum.Parse(queryCounter.GetParameters()[1].ParameterType, "Timestamp");
                Type paramType = getQueryObject.GetParameters()[1].ParameterType;
                object result = Enum.Parse(paramType, "QueryResult");
                object available = Enum.Parse(paramType, "QueryResultAvailable");

                return new GlTimestampApi(genQuery, deleteQuery, queryCounter, getQueryObject,
                                          timestamp, result, available);
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        public int Create()
        {
            return (int)_genQuery.Invoke(null, null);
        }

        public void Delete(int query)
        {
            _oneArg[0] = query;
            _deleteQuery.Invoke(null, _oneArg);
        }

        /// <summary>Records the GPU clock when the commands before it have completed.</summary>
        public void Stamp(int query)
        {
            _counterArgs[0] = query;
            _counterArgs[1] = _timestampTarget;
            _queryCounter.Invoke(null, _counterArgs);
        }

        /// <summary>
        /// Never waits: a query whose result is not ready yet returns false.
        /// Reading a pending query would stall the CPU until the GPU caught up,
        /// which is exactly the cost the profiler would then misreport.
        /// </summary>
        public bool TryRead(int query, out long nanoseconds)
        {
            nanoseconds = 0;
            _getArgs[0] = query;
            _getArgs[1] = _availableParam;
            _getArgs[2] = _result;
            _result[0] = 0;
            _getQueryObject.Invoke(null, _getArgs);
            if (_result[0] == 0) return false;

            _getArgs[1] = _resultParam;
            _getQueryObject.Invoke(null, _getArgs);
            nanoseconds = _result[0];
            return true;
        }

        private static Type[] SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
            catch { return Type.EmptyTypes; }
        }
    }
}
