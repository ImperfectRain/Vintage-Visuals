namespace VintageVisuals.Common.Rendering
{
    /// <summary>
    /// Shader specialisation (D90): code that only some settings need is
    /// compiled in only while those settings ask for it, by gating a patch
    /// group that adds a #define, never by a uniform branch alone. A uniform
    /// branch skips the work at run time but the compiler still allocates
    /// registers for the largest path, so dead code can cost occupancy.
    ///
    /// First cut: the 75 terrain debug views (group pbrterraindebug).
    /// Every decision here feeds the mod's patch-gating signature, so a change
    /// reloads the shaders once.
    /// </summary>
    public static class ShaderSpecialisation
    {
        /// <summary>-1 follows the debug view; 0 forces the views out, 1 in. Set only by '.vvbench debugcode'.</summary>
        public static int DebugCodeBenchOverride { get; set; } = -1;

        /// <summary>
        /// D91. Whether the terrain's own beam march can be compiled out because
        /// the fast beam pass (D87) draws the beams. Never during '.vvbench beams'
        /// (its runs switch between the two without reloading), never while a
        /// debug view is selected (view 68 reads the march), and never once the
        /// pass has failed, so the terrain takes the beams back.
        /// </summary>
        public static bool BeamsInPass(bool pbrEnabled, float sunShafts, bool fastBeams, bool passFailed, float debugView)
        {
            return !BeamBenchHoldsMarch && !BeamCodeBenchHoldsMarch && pbrEnabled && sunShafts >= 0.001f && fastBeams && !passFailed &&
                   !DebugCodeWanted(debugView);
        }

        /// <summary>True during the "compiled in" runs of '.vvbench beamcode' (D91).</summary>
        public static bool BeamCodeBenchHoldsMarch { get; set; }

        /// <summary>True only during '.vvbench beams', which needs the terrain's own march in source.</summary>
        public static bool BeamBenchHoldsMarch { get; set; }

        /// <summary>Whether the terrain debug views should be compiled in right now.</summary>
        public static bool DebugCodeWanted(float debugView)
        {
            if (DebugCodeBenchOverride >= 0) return DebugCodeBenchOverride == 1;
            return (int)(debugView + 0.5f) != 0;
        }
    }
}
