using Vintagestory.API.MathTools;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Everything the weather tells the material shader, as one value.
    ///
    /// The material system models how surfaces respond to light; the weather
    /// system decides what the weather is doing. Neither owns both halves, so
    /// these cross the boundary as plain numbers rather than as a reference to
    /// the weather subsystem - which also means the binder keeps working when
    /// there is no weather subsystem at all.
    ///
    /// Every field's zero is its harmless value, matching the rule the GLSL
    /// runs on: an unset uniform reads as zero, so zero has to mean vanilla.
    /// </summary>
    public readonly struct WeatherInputs
    {
        /// <summary>0 dry, 1 as wet as rain makes it.</summary>
        public readonly float Wetness;

        /// <summary>Sky exposure a surface needs before rain is treated as reaching it.</summary>
        public readonly float RainCover;

        /// <summary>0 still water, 1 rain landing hard in it.</summary>
        public readonly float Ripples;

        /// <summary>0 clear sky, 1 sun fully diffused by cloud.</summary>
        public readonly float Overcast;

        /// <summary>
        /// Camera world position. The chunk shaders only have camera-relative
        /// coordinates, and a ripple field built on those swims across the
        /// ground as the player walks.
        /// </summary>
        public readonly Vec3f Origin;

        public WeatherInputs(float wetness, float rainCover, float ripples, float overcast, Vec3f origin)
        {
            Wetness = wetness;
            RainCover = rainCover;
            Ripples = ripples;
            Overcast = overcast;
            Origin = origin;
        }

        /// <summary>What the shader sees when there is no weather subsystem: vanilla.</summary>
        public static WeatherInputs None
        {
            get { return new WeatherInputs(0f, 0.82f, 0f, 0f, new Vec3f()); }
        }
    }
}
