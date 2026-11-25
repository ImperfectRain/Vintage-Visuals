namespace VintageVisuals.Common.Diagnostics
{
    /// <summary>
    /// D78. The leaf mode a running <c>.vvbench leaves</c> holds, so one bench
    /// can compare no leaves, v15's 20-block reach and the current reach on the
    /// same scene. Runtime only, never saved; Normal outside a bench. It never
    /// switches leaves ON that the player turned off (quality is not
    /// enablement): with the slider at 0 every run has none.
    /// </summary>
    public static class LeafBench
    {
        public const int Normal = 0;
        public const int Off = 1;
        public const int Legacy = 2;

        public static int Mode { get; set; } = Normal;

        /// <summary>Leaves flying and lying, written by the leaf renderer each frame it runs, for the bench report.</summary>
        public static int Flying { get; set; }
        public static int Litter { get; set; }

        /// <summary>Seconds each run waits before recording, so the leaves of the new reach are falling in numbers.</summary>
        public const float WarmSeconds = 15f;

        public static string Name(int mode)
        {
            switch (mode)
            {
                case Off: return "no falling leaves";
                case Legacy: return "old 20-block reach";
                default: return "new reach (quality " + QualityTier.Name(QualityTier.Current) + ")";
            }
        }
    }
}
