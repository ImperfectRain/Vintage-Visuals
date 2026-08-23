using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using VintageVisuals.Common;

namespace VintageVisuals.Ui
{
    /// <summary>
    /// Reads and writes one config property named by a dotted path.
    ///
    /// WHY REFLECTION RATHER THAN A PAIR OF LAMBDAS PER SETTING. Roughly seventy
    /// settings would mean a hundred and forty closures, all of them the same
    /// two lines, and none of them checkable: a lambda that reads the wrong
    /// field compiles perfectly and is invisible until someone drags the wrong
    /// slider in a forest. A path is a string, and a string can be resolved and
    /// verified by a test - which is exactly what
    /// tools/smoketest does for every entry in the registry, in both directions.
    ///
    /// The cost is one dictionary lookup and one reflection call per edit, on a
    /// path that runs when a human moves a mouse. That is not a hot path.
    /// </summary>
    public static class ConfigAccess
    {
        private static readonly Dictionary<string, PropertyInfo[]> _cache =
            new Dictionary<string, PropertyInfo[]>(StringComparer.Ordinal);

        /// <summary>
        /// Resolves a path to its chain of properties, or null if any hop is
        /// missing. Never throws: a bad path is a bug for a test to report, not
        /// an exception into a GUI render.
        /// </summary>
        public static PropertyInfo[] Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            PropertyInfo[] cached;
            lock (_cache)
            {
                if (_cache.TryGetValue(path, out cached)) return cached;
            }

            string[] hops = path.Split('.');
            var chain = new PropertyInfo[hops.Length];
            Type current = typeof(VintageVisualsConfig);

            for (int i = 0; i < hops.Length; i++)
            {
                PropertyInfo prop = current.GetProperty(hops[i],
                    BindingFlags.Public | BindingFlags.Instance);

                if (prop == null) { chain = null; break; }

                chain[i] = prop;
                current = prop.PropertyType;
            }

            lock (_cache) { _cache[path] = chain; }
            return chain;
        }

        /// <summary>The object that owns the final property, or null if the path is broken.</summary>
        private static object Owner(VintageVisualsConfig config, PropertyInfo[] chain)
        {
            object node = config;
            for (int i = 0; i < chain.Length - 1; i++)
            {
                node = chain[i].GetValue(node);
                if (node == null) return null;
            }
            return node;
        }

        /// <summary>
        /// The current value as a float. Bools come back as 0 or 1 so one widget
        /// path can carry both, which is what lets the reset and modified-from-
        /// default logic stay in one place instead of two.
        /// </summary>
        public static float Get(VintageVisualsConfig config, string path)
        {
            PropertyInfo[] chain = Resolve(path);
            if (chain == null || config == null) return 0f;

            object owner = Owner(config, chain);
            if (owner == null) return 0f;

            object value = chain[chain.Length - 1].GetValue(owner);
            if (value is bool b) return b ? 1f : 0f;
            if (value is float f) return f;
            if (value is int i) return i;
            if (value is double d) return (float)d;
            return 0f;
        }

        /// <summary>
        /// Writes the value back, converting to the property's own type.
        ///
        /// It does NOT clamp. Clamping belongs to the config's own
        /// ClampToValidRanges, which is the one place that knows every limit and
        /// the one place the JSON path, the ConfigLib path and this path all
        /// pass through. A second clamp here would be a second opinion about a
        /// range, and two opinions about a range is how "off" stops meaning off.
        /// </summary>
        public static bool Set(VintageVisualsConfig config, string path, float value)
        {
            PropertyInfo[] chain = Resolve(path);
            if (chain == null || config == null) return false;

            object owner = Owner(config, chain);
            if (owner == null) return false;

            PropertyInfo target = chain[chain.Length - 1];
            if (!target.CanWrite) return false;

            try
            {
                if (target.PropertyType == typeof(bool)) target.SetValue(owner, value >= 0.5f);
                else if (target.PropertyType == typeof(float)) target.SetValue(owner, value);
                else if (target.PropertyType == typeof(int)) target.SetValue(owner, (int)Math.Round(value));
                else if (target.PropertyType == typeof(double)) target.SetValue(owner, (double)value);
                else if (target.PropertyType == typeof(string)) target.SetValue(owner, value.ToString(CultureInfo.InvariantCulture));
                else return false;
            }
            catch { return false; }

            return true;
        }

        /// <summary>
        /// The current value of a string property, or null if the path is not
        /// one.
        ///
        /// Kept apart from <see cref="Get"/> rather than squeezed through a
        /// float, because a choice is not a number that happens to be spelled
        /// out. AdaptiveGrade.Style is stored as "Filmic", the shader reads it
        /// as "Filmic", and turning it into index 1 on the way through the UI
        /// would invent an ordering that nothing else in the mod shares - so
        /// inserting a style later would silently repoint every saved config.
        /// </summary>
        public static string GetString(VintageVisualsConfig config, string path)
        {
            PropertyInfo[] chain = Resolve(path);
            if (chain == null || config == null) return null;

            object owner = Owner(config, chain);
            if (owner == null) return null;

            return chain[chain.Length - 1].GetValue(owner) as string;
        }

        /// <summary>Writes a string property. Returns false if the path is not one.</summary>
        public static bool SetString(VintageVisualsConfig config, string path, string value)
        {
            PropertyInfo[] chain = Resolve(path);
            if (chain == null || config == null) return false;

            PropertyInfo target = chain[chain.Length - 1];
            if (target.PropertyType != typeof(string) || !target.CanWrite) return false;

            object owner = Owner(config, chain);
            if (owner == null) return false;

            try { target.SetValue(owner, value); }
            catch { return false; }

            return true;
        }

        /// <summary>The shipped default for a string property.</summary>
        public static string DefaultString(string path)
        {
            return GetString(DefaultConfig, path);
        }

        /// <summary>Whether a path names a real, writable property of the shipped config.</summary>
        public static bool Exists(string path)
        {
            PropertyInfo[] chain = Resolve(path);
            return chain != null && chain[chain.Length - 1].CanWrite;
        }

        /// <summary>The CLR type at the end of the path, or null.</summary>
        public static Type TypeOf(string path)
        {
            PropertyInfo[] chain = Resolve(path);
            return chain == null ? null : chain[chain.Length - 1].PropertyType;
        }

        /// <summary>
        /// The shipped default for this path.
        ///
        /// Read from a freshly constructed config rather than from a table,
        /// because the constructor IS the definition of "default" - every other
        /// answer is a copy that can drift from it. Reset, the modified-from-
        /// default marker and the preset "Custom" test all come through here, so
        /// there is exactly one source of truth for what default means.
        /// </summary>
        public static float Default(string path)
        {
            return Get(DefaultConfig, path);
        }

        private static readonly VintageVisualsConfig DefaultConfig = new VintageVisualsConfig();

        /// <summary>Whether the live value differs from the shipped default.</summary>
        public static bool IsModified(VintageVisualsConfig config, string path)
        {
            if (TypeOf(path) == typeof(string))
            {
                return !string.Equals(GetString(config, path), DefaultString(path), StringComparison.Ordinal);
            }

            return Math.Abs(Get(config, path) - Default(path)) > 1e-6f;
        }

        /// <summary>Restores one path to its shipped default, whatever its type.</summary>
        public static bool ResetToDefault(VintageVisualsConfig config, string path)
        {
            if (TypeOf(path) == typeof(string)) return SetString(config, path, DefaultString(path));
            return Set(config, path, Default(path));
        }
    }
}
