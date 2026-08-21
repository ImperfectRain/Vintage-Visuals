using Vintagestory.API.MathTools;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Everything outside the material system that the material shader needs,
    /// as one value.
    ///
    /// The split it enforces is between fact and preference. EnvironmentState
    /// says what the world is doing; the config says how much of that the
    /// player wants to see; and this is the product, which is the only form the
    /// shader has any use for. Keeping config-scaled values OUT of the shared
    /// state is what stops "off" quietly meaning "slightly different".
    ///
    /// Every field's zero is its harmless value, matching the rule the GLSL
    /// runs on: an unset uniform reads as zero, so zero has to mean vanilla.
    /// </summary>
    public readonly struct SceneInputs
    {
        /// <summary>
        /// 0 at midnight, 1 at noon.
        ///
        /// Vanilla's own dayLight uniform is declared in chunkopaque.fsh and
        /// not in chunktopsoil.fsh, so the shared snippet reads ours instead
        /// and this is where it comes from.
        /// </summary>
        public readonly float DayLight;

        /// <summary>0 dry, 1 as wet as rain makes it.</summary>
        public readonly float Wetness;

        /// <summary>Sky exposure a surface needs before rain is treated as reaching it.</summary>
        public readonly float RainCover;

        /// <summary>0 still water, 1 rain landing hard in it.</summary>
        public readonly float Ripples;

        /// <summary>Ripple animation clock, already wrapped to 0..1. See EnvironmentTracker.RippleClock.</summary>
        public readonly float RippleTime;

        /// <summary>Slow clock for leaf-speed movement, wrapped to 0..1.</summary>
        public readonly float Breeze;

        /// <summary>0 clear sky, 1 sun fully diffused by cloud.</summary>
        public readonly float Overcast;

        /// <summary>0 open sky, 1 fully boxed in.</summary>
        public readonly float Enclosure;

        /// <summary>0 lit by sky, 1 lit by fire.</summary>
        public readonly float ArtificialLight;

        /// <summary>How much the mod should hold back. See IntentChannel.Restraint.</summary>
        public readonly float Restraint;

        /// <summary>How much this scene needs help being legible.</summary>
        public readonly float Readability;

        /// <summary>How far into autumn, 0..1. Changes how surfaces respond, never how they look.</summary>
        public readonly float Autumn;

        /// <summary>How far into winter, 0..1.</summary>
        public readonly float Winter;

        /// <summary>How much frost changes a surface, on top of vanilla's own frost mask.</summary>
        public readonly float Frost;

        /// <summary>How much snow dusts surfaces the sky can see.</summary>
        public readonly float Snow;

        /// <summary>
        /// Camera world position. The chunk shaders only have camera-relative
        /// coordinates, and a ripple field built on those swims across the
        /// ground as the player walks.
        /// </summary>
        public readonly Vec3f Origin;

        public SceneInputs(float dayLight, float wetness, float rainCover, float ripples,
                           float rippleTime, float breeze, float overcast, Vec3f origin,
                           float enclosure = 0f, float artificialLight = 0f,
                           float restraint = 0f, float readability = 0f,
                           float autumn = 0f, float winter = 0f,
                           float frost = 0f, float snow = 0f)
        {
            Autumn = autumn;
            Winter = winter;
            Frost = frost;
            Snow = snow;
            Enclosure = enclosure;
            ArtificialLight = artificialLight;
            Restraint = restraint;
            Readability = readability;
            RippleTime = rippleTime;
            Breeze = breeze;
            DayLight = dayLight;
            Wetness = wetness;
            RainCover = rainCover;
            Ripples = ripples;
            Overcast = overcast;
            Origin = origin;
        }

        /// <summary>What the shader sees before anything has told it otherwise: vanilla, at noon.</summary>
        public static SceneInputs None
        {
            get { return new SceneInputs(1f, 0f, 0.82f, 0f, 0f, 0f, 0f, new Vec3f()); }
        }
    }
}
