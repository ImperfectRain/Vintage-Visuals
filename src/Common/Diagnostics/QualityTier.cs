namespace VintageVisuals.Common.Diagnostics
{
    /// <summary>
    /// The quality tier: one dial for what the mod's features COST.
    ///
    /// "Quality is not enablement" (CLAUDE.md): a tier scales what a feature
    /// costs or contributes and never switches on a feature the player turned
    /// off. A strength of 0 stays 0 at every tier.
    ///
    /// High is the reference: every scale below is exactly 1 at High, so a
    /// player who never touches this dial gets the build as it was before the
    /// dial existed. Low and Medium cut the per-pixel loops that the frame
    /// profiler can attribute; Ultra spends more on the same loops.
    ///
    /// What each tier touches, and nothing else:
    ///   terrain canopy ring radius  (shadow-map taps per terrain pixel)
    ///   terrain detail distance     (how far per-texel relief is evaluated)
    ///   water reflection march      (screen-space steps per water pixel)
    ///   water reflection softness   (extra taps for the subtle blur)
    ///   water ripple layers         (rain ripple scales evaluated)
    /// </summary>
    public static class QualityTier
    {
        public const int Low = 0;
        public const int Medium = 1;
        public const int High = 2;
        public const int Ultra = 3;

        private static int _configured = High;
        private static int _override = -1;

        /// <summary>The tier in effect: a bench override if one is running, else the config.</summary>
        public static int Current { get { return _override >= 0 ? _override : _configured; } }

        public static void SetConfigured(float value)
        {
            int v = (int)(value + 0.5f);
            _configured = v < Low ? Low : (v > Ultra ? Ultra : v);
        }

        /// <summary>Used by <c>.vvbench tiers</c>; never saved. -1 clears it.</summary>
        public static void SetOverride(int tier) { _override = tier; }

        public static string Name(int tier)
        {
            switch (tier)
            {
                case Low: return "Low";
                case Medium: return "Medium";
                case Ultra: return "Ultra";
                default: return "High";
            }
        }

        public static float CanopyRadiusScale
        {
            get { return Pick(0.5f, 0.75f, 1f, 1f); }
        }

        public static float DetailDistanceScale
        {
            get { return Pick(0.5f, 0.75f, 1f, 1.25f); }
        }

        /// <summary>Water screen-space march steps; 0 means sky reflection only.</summary>
        public static float WaterMarchSteps
        {
            get { return Pick(0f, 16f, 24f, 40f); }
        }

        /// <summary>Taps for the reflection softness: 1 is the sharp texel image alone.</summary>
        public static float WaterSoftTaps
        {
            get { return Pick(1f, 3f, 5f, 5f); }
        }

        /// <summary>Rain ripple layers on water.</summary>
        public static float WaterRippleLayers
        {
            get { return Pick(1f, 2f, 2f, 2f); }
        }

        private static float Pick(float low, float medium, float high, float ultra)
        {
            switch (Current)
            {
                case Low: return low;
                case Medium: return medium;
                case Ultra: return ultra;
                default: return high;
            }
        }
    }
}
