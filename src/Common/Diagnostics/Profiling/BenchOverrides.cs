using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VintageVisuals.Common.Diagnostics.Profiling
{
    /// <summary>
    /// D99. What a running profiler bench holds against the player's config:
    /// patch groups forced in or out. Runtime only, never saved, empty outside
    /// a bench, and cleared by <see cref="Clear"/> on every exit path.
    ///
    /// Forcing a group IN that the player has off is deliberate and bounded:
    /// a compiled-cost run asks what the group's source costs while it does
    /// nothing, and "zero means vanilla" (CLAUDE.md) is what makes a forced-in
    /// group with no uniforms uploaded look like vanilla. It never changes a
    /// config value, so the player's off switch is untouched when it ends.
    /// </summary>
    public static class BenchOverrides
    {
        private static readonly Dictionary<string, bool> _groups = new Dictionary<string, bool>(StringComparer.Ordinal);

        public static bool Any { get { return _groups.Count > 0; } }

        public static void ForceGroup(string group, bool compiledIn) { _groups[group] = compiledIn; }

        public static void Clear() { _groups.Clear(); }

        /// <summary>Null when the bench leaves this group to the config.</summary>
        public static bool? GroupOverride(string group)
        {
            return _groups.TryGetValue(group, out bool v) ? v : (bool?)null;
        }

        /// <summary>Appended to the patch-gating signature so a change reloads the shaders exactly once.</summary>
        public static string Signature
        {
            get
            {
                if (_groups.Count == 0) return "";
                var sb = new StringBuilder("|bench:");
                foreach (var kv in _groups.OrderBy(k => k.Key, StringComparer.Ordinal))
                    sb.Append(kv.Key).Append(kv.Value ? '+' : '-');
                return sb.ToString();
            }
        }
    }
}
