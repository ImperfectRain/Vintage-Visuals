using Vintagestory.API.MathTools;

// Namespace: Scene, not Environment. A namespace called Environment inside
// VintageVisuals.Common shadows System.Environment for every file in that
// namespace, which silently broke Environment.NewLine two files away. It is
// also the better name: this layer is what the renderer knows about the scene,
// of which the weather is one part.
namespace VintageVisuals.Common.Scene
{
    /// <summary>
    /// One snapshot of what the world is doing, shared by every subsystem.
    ///
    /// This exists because the alternative was already happening. Before it,
    /// three subsystems each asked the game the same questions on their own
    /// timers: the climate was sampled twice per second in two places, cloud
    /// density was read twice through two copies of the same magic gain, and
    /// daylight was read in three. Nothing was wrong with any one of them, and
    /// that is the problem - two copies of a constant drift apart quietly, and
    /// the symptom is two subsystems disagreeing about whether it is raining.
    ///
    /// Two kinds of field, kept apart deliberately:
    ///
    ///  - SAMPLED: what the game says, normalised at the point of reading and
    ///    otherwise unopinionated.
    ///  - DERIVED: an answer more than one subsystem needs and none of them
    ///    should own. Wetness is the example - Weather computes it, PseudoPBR
    ///    consumes it, and neither is the natural home for it.
    ///
    /// What does NOT belong here is anything scaled by a config value. A
    /// strength slider is a statement about what the player wants, not about
    /// what the world is doing, and mixing the two is how "off" stops meaning
    /// off. Those live in the per-subsystem input structs instead.
    ///
    /// Free of every game type except the API's plain vector maths, so the
    /// rules that read it can be driven through any world state in
    /// tools/smoketest without a client.
    /// </summary>
    public readonly struct EnvironmentState
    {
        // --- Celestial ------------------------------------------------------

        /// <summary>SAMPLED. 0 at midnight, 1 at noon.</summary>
        public readonly float DayLight;

        /// <summary>SAMPLED. How much light the moon is contributing, 0..1.</summary>
        public readonly float MoonLight;

        // --- Sky ------------------------------------------------------------

        /// <summary>
        /// SAMPLED. 0 clear, 1 overcast.
        ///
        /// The ambient manager's blended cloud density, gained once here rather
        /// than once per consumer. It is the game's own density parameter and
        /// not a fraction of sky, so read raw it says "nearly clear" under a
        /// sky full of cloud.
        /// </summary>
        public readonly float CloudCover;

        /// <summary>SAMPLED. Unit vector in world XZ, or the last known heading in still air.</summary>
        public readonly Vec2f WindDirection;

        /// <summary>SAMPLED. Wind magnitude where the player is.</summary>
        public readonly float WindSpeed;

        // --- Precipitation --------------------------------------------------

        /// <summary>SAMPLED. How hard it is precipitating now, 0..1, before any temperature test.</summary>
        public readonly float Precipitation;

        /// <summary>DERIVED. Precipitation that is falling as rain, eased over seconds.</summary>
        public readonly float Rain;

        /// <summary>DERIVED. Precipitation that is falling as snow, eased over seconds.</summary>
        public readonly float Snow;

        /// <summary>DERIVED. How wet surfaces are, eased over a minute. Rain that fell, not rain falling.</summary>
        public readonly float Wetness;

        // --- Place ----------------------------------------------------------

        /// <summary>SAMPLED. Air temperature at the player, in degrees C.</summary>
        public readonly float Temperature;

        /// <summary>
        /// SAMPLED. Worldgen rainfall at the player, 0 arid to 1 rainforest.
        ///
        /// Deliberately not the current rainfall. This answers what KIND of
        /// place this is, and a rainforest in a dry spell is still a
        /// rainforest.
        /// </summary>
        public readonly float Humidity;

        // --- The observer ---------------------------------------------------

        /// <summary>
        /// SAMPLED. 0 fully enclosed, 1 open sky, from vanilla's sunlight level
        /// at the player's head.
        ///
        /// The indoor/outdoor signal, and a soft one on purpose: a porch is
        /// neither, and anything that snapped between two looks as the player
        /// stepped under an awning would be worse than something that leans.
        /// </summary>
        public readonly float SkyExposure;

        /// <summary>SAMPLED. 0 at or above sea level, 1 deep underground.</summary>
        public readonly float Depth;

        /// <summary>SAMPLED. 1 when the camera is submerged.</summary>
        public readonly float Underwater;

        /// <summary>
        /// SAMPLED. Camera world position, WRAPPED to <see cref="CameraPeriod"/>.
        ///
        /// Every chunk shader works in camera-relative coordinates, so any
        /// effect that has to stay nailed to the ground - cloud shadows, rain
        /// ripples - needs this to get back to world space.
        ///
        /// It is wrapped because the unwrapped value is unusable. Vintage Story
        /// worlds run to roughly half a million blocks from the origin, and a
        /// float32 at that magnitude holds about sixteen distinct positions
        /// inside a half-block cell - so a fine-grained field built on it
        /// collapses into a coarse stamp repeated identically everywhere. That
        /// is what the first rain ripples looked like.
        ///
        /// Nothing in this mod needs absolute world position; everything needs
        /// a coordinate that is stable relative to the ground. Wrapping in
        /// double here and adding the camera-relative position in the shader
        /// gives that, with the whole frame continuous. The pattern shifts once
        /// per 4096 blocks travelled, which no one has ever seen happen.
        /// </summary>
        public readonly Vec3f CameraPosition;

        public EnvironmentState(float dayLight, float moonLight, float cloudCover,
                                Vec2f windDirection, float windSpeed,
                                float precipitation, float rain, float snow, float wetness,
                                float temperature, float humidity,
                                float skyExposure, float depth, float underwater,
                                Vec3f cameraPosition)
        {
            DayLight = dayLight;
            MoonLight = moonLight;
            CloudCover = cloudCover;
            WindDirection = windDirection;
            WindSpeed = windSpeed;
            Precipitation = precipitation;
            Rain = rain;
            Snow = snow;
            Wetness = wetness;
            Temperature = temperature;
            Humidity = humidity;
            SkyExposure = skyExposure;
            Depth = depth;
            Underwater = underwater;
            CameraPosition = cameraPosition;
        }

        /// <summary>
        /// A clear temperate noon in the open.
        ///
        /// What every consumer sees before the first sample lands, and what the
        /// tests measure "no influence" against. Every field here is the value
        /// under which a well-behaved subsystem does nothing.
        /// </summary>
        public static EnvironmentState Clear
        {
            get
            {
                return new EnvironmentState(
                    dayLight: 1f, moonLight: 0f, cloudCover: 0f,
                    windDirection: new Vec2f(1f, 0f), windSpeed: 0f,
                    precipitation: 0f, rain: 0f, snow: 0f, wetness: 0f,
                    temperature: TemperateCelsius, humidity: 0.5f,
                    skyExposure: 1f, depth: 0f, underwater: 0f,
                    cameraPosition: new Vec3f());
            }
        }

        /// <summary>Degrees C that counts as neither hot nor cold. Shared so two subsystems cannot disagree.</summary>
        public const float TemperateCelsius = 8f;

        /// <summary>
        /// Blocks between repeats of <see cref="CameraPosition"/>.
        ///
        /// Any shader field built on that coordinate must tile evenly into this
        /// or the wrap cuts one of its cells in half. A smoke check pins this
        /// value against the constant in the GLSL, because the two being
        /// silently different is a seam nobody would trace back to here.
        /// </summary>
        public const float CameraPeriod = 4096f;
    }
}
