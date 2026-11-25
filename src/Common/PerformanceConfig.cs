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

        public void ClampToValidRanges(List<string> corrections)
        {
            Quality = ColorGradeConfig.Clamp(Quality, 0f, 3f, "Performance.Quality", corrections);
        }
    }
}
