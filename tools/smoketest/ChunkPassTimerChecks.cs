using System;
using System.IO;
using System.Text.RegularExpressions;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// The chunk pass timers split the opaque stage into the game's own pools
    /// and the depth prepass. They are only evidence if the prepass span sits
    /// INSIDE the chunk span (the chunk prefix must run before the prepass's
    /// prefix draws the nested prepass), if they never change what is drawn
    /// (no prefix may skip the original), and if code markers are never
    /// registered as renderers (they would stamp at render order 0 of the
    /// opaque stage and report a span that is not the pool's).
    /// </summary>
    public static class ChunkPassTimerChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            string timers = File.ReadAllText(Path.Combine(repo, "src/Common/Diagnostics/ChunkPassTimers.cs"));
            string profiler = File.ReadAllText(Path.Combine(repo, "src/Common/Diagnostics/FrameProfiler.cs"));

            check("chunk timers: the chunk span opens before the depth prepass draws",
                Regex.IsMatch(timers, @"nameof\(RenderOpaquePrefix\)\)\s*\{\s*priority\s*=\s*Priority\.First\s*\}"), "");

            check("chunk timers: no hook can skip the game's draw",
                !Regex.IsMatch(timers, @"public static bool \w+Prefix\(") &&
                Regex.IsMatch(timers, @"public static void RenderOpaquePrefix\(") &&
                Regex.IsMatch(timers, @"public static void PoolPrefix\("), "");

            check("chunk timers: code markers are never registered as renderers",
                Regex.Matches(profiler, @"if \(m\.CodeOnly\) continue;").Count == 1 &&
                Regex.Matches(profiler, @"if \(!m\.CodeOnly\) _capi\.Event\.UnregisterRenderer").Count == 2, "");

            check("chunk timers: only pools 0 and 1 stretch the prepass span",
                timers.Contains("if (pass > 1) return;"), "");
        }
    }
}
