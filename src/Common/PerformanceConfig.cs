using System.Collections.Generic;

namespace VintageVisuals.Common
{
    /// <summary>
    /// Measuring and paying for the mod's effects. See
    /// src/Common/Diagnostics/QualityTier.cs for exactly what a tier touches.
    /// </summary>
    public class PerformanceConfig
    {
        /// <summary>
        /// 0 Low, 1 Medium, 2 High, 3 Ultra. High is the build as it was before
        /// tiers existed. A tier scales cost, never enables a feature that is off.
        /// </summary>
        public float Quality { get; set; } = 2f;

        /// <summary>
        /// Shows the frame-time readout (also toggled with .vvperf). Registers
        /// the profiler's markers only while shown, so off costs nothing.
        /// </summary>
        public bool ShowFrameTimes { get; set; } = false;

        /// <summary>
        /// Exact depth prepass for the opaque and leaf chunk passes (D88): each
        /// visible pixel runs the terrain material once instead of once per
        /// overlapping leaf layer. Gates the <c>depthprepass</c> patch group,
        /// so off is vanilla source. Also toggled with .vvprepass.
        /// </summary>
        public bool DepthPrepass { get; set; } = true;

        /// <summary>D99. Seconds each profiler run records (.vvbench groups / sweep / suite).</summary>
        public float ProfilerSeconds { get; set; } = 6f;

        /// <summary>D99. Interleaved rounds per profiler plan (ABBA order); 3 gives a usable spread.</summary>
        public float ProfilerRounds { get; set; } = 3f;

        /// <summary>D99. Adds the mod's CPU time per system to the frame-time readout.</summary>
        public bool ProfilerCpuInReadout { get; set; } = true;

        /// <summary>D99. Which system the panel's "Sweep system" button sweeps: an index into the systems that declare sweeps.</summary>
        public float ProfilerSweepSystem { get; set; } = 0f;

        public void ClampToValidRanges(List<string> corrections)
        {
            Quality = ColorGradeConfig.Clamp(Quality, 0f, 3f, "Performance.Quality", corrections);
            ProfilerSeconds = ColorGradeConfig.Clamp(ProfilerSeconds, 3f, 30f, "Performance.ProfilerSeconds", corrections);
            ProfilerRounds = ColorGradeConfig.Clamp(ProfilerRounds, 1f, 6f, "Performance.ProfilerRounds", corrections);
            ProfilerSweepSystem = ColorGradeConfig.Clamp(ProfilerSweepSystem, 0f, 64f, "Performance.ProfilerSweepSystem", corrections);
        }
    }
}
