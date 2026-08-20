namespace VintageVisuals.ColorGrade
{
    /// <summary>
    /// What the world is doing right now, in the terms grading cares about.
    ///
    /// Deliberately free of every game type, like <c>WetnessTracker</c> and
    /// <c>AdaptiveExposure</c>. The grading rules are the part worth being sure
    /// about - they decide what the whole screen looks like - and a struct of
    /// plain floats can be driven through every combination in a test without a
    /// client, a world, or a GPU.
    ///
    /// Everything here is normalised at the point it is sampled rather than
    /// inside the rules, so the rules never have to know that sun light happens
    /// to run 0..22 or that sea level happens to be 110.
    /// </summary>
    public readonly struct GradeContext
    {
        /// <summary>0 at midnight, 1 at noon. The game's own daylight strength.</summary>
        public readonly float DayLight;

        /// <summary>0..1 precipitation falling now.</summary>
        public readonly float Rain;

        /// <summary>0 clear, 1 overcast.</summary>
        public readonly float CloudCover;

        /// <summary>
        /// 0 fully enclosed, 1 open sky, from vanilla's sunlight level at the
        /// player's head.
        ///
        /// This is the indoor/outdoor signal, and it is a soft one on purpose:
        /// a porch is neither, and grading that snapped between two looks as
        /// the player stepped under an awning would be worse than one that
        /// leans.
        /// </summary>
        public readonly float SkyExposure;

        /// <summary>0 at or above sea level, 1 deep underground.</summary>
        public readonly float Depth;

        /// <summary>Air temperature at the player, in degrees C.</summary>
        public readonly float Temperature;

        /// <summary>Climate rainfall at the player, 0 arid to 1 rainforest.</summary>
        public readonly float Rainfall;

        /// <summary>1 when the camera is submerged, 0 otherwise. Eased like everything else.</summary>
        public readonly float Underwater;

        public GradeContext(float dayLight, float rain, float cloudCover, float skyExposure,
                            float depth, float temperature, float rainfall, float underwater)
        {
            DayLight = dayLight;
            Rain = rain;
            CloudCover = cloudCover;
            SkyExposure = skyExposure;
            Depth = depth;
            Temperature = temperature;
            Rainfall = rainfall;
            Underwater = underwater;
        }

        /// <summary>
        /// A clear temperate noon in the open: the context under which every
        /// influence contributes nothing and the player's own settings are what
        /// reaches the screen.
        /// </summary>
        public static GradeContext Neutral
        {
            get { return new GradeContext(1f, 0f, 0f, 1f, 0f, GradeStack.TemperateCelsius, 0.5f, 0f); }
        }
    }
}
