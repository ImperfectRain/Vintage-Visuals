using System;

namespace VintageVisuals.Common.Scene
{
    /// <summary>
    /// Turns what the world is doing into what the scene needs.
    ///
    /// Pure and free of game types, like every rule in this project that decides
    /// what the screen looks like, so it can be driven through any world state
    /// in tools/smoketest without a client.
    ///
    /// Every push carries a source and a reason. That is not decoration: when a
    /// scene comes out wrong the only question worth asking is which influence
    /// did it, and this is the only place that can answer.
    /// </summary>
    public static class SceneIntentBuilder
    {
        /// <summary>Blocks below sea level at which enclosure is complete on depth alone.</summary>
        private const float DeepUnderground = 0.75f;

        public static SceneIntent Build(EnvironmentState world)
        {
            var intent = new SceneIntent();

            float sky = Clamp01(world.SkyExposure);
            float enclosed = 1f - sky;
            float dayLight = Clamp01(world.DayLight);
            float night = 1f - dayLight;

            // --- what the air is doing ---------------------------------------
            //
            // A push marked Uncapped is a direct restatement of a fact rather
            // than a side effect: rain does not INFLUENCE wetness, it is what
            // wetness means. Everything else takes the default cap, so no single
            // side effect can own a channel it does not describe.

            intent.Add("rain", IntentChannel.Wetness, world.Rain,
                       "precipitation falling now", SceneIntent.Uncapped);

            intent.Add("rain", IntentChannel.Atmosphere, world.Rain * 0.6f,
                       "rain thickens the air");

            intent.Add("cloud", IntentChannel.Gloom, world.CloudCover * 0.7f * sky,
                       "overcast sky, outdoors");

            intent.Add("snow", IntentChannel.Cold, world.Snow,
                       "snow falling", SceneIntent.Uncapped);

            // Cold is a place as much as a weather. Freezing air reads cold
            // whether or not anything is falling out of it.
            intent.Add("climate", IntentChannel.Cold,
                       Ramp(world.Temperature, 4f, -12f) * 0.7f,
                       "air temperature below freezing");

            // Heat needs the sky. A cellar in a desert is not hot to look at,
            // and the glare that reads as heat is sunlight on sand.
            intent.Add("climate", IntentChannel.Heat,
                       Ramp(world.Temperature, 20f, 34f) * sky * dayLight,
                       "hot, open and sunlit");

            intent.Add("climate", IntentChannel.Atmosphere,
                       Ramp(world.Humidity, 0.55f, 1f) * 0.4f * sky,
                       "humid climate");

            // --- where the player is -----------------------------------------

            intent.Add("sky", IntentChannel.Enclosure, enclosed,
                       "sunlight cannot reach here", SceneIntent.Uncapped);

            intent.Add("depth", IntentChannel.Enclosure, Clamp01(world.Depth) * 0.5f,
                       "below sea level");

            intent.Add("calendar", IntentChannel.Night, night,
                       "sun is down", SceneIntent.Uncapped);

            // Where the sky is not doing the lighting, something else is, and in
            // this game that something is almost always fire.
            intent.Add("sky", IntentChannel.ArtificialLight, enclosed * 0.7f,
                       "enclosed, so local light dominates", SceneIntent.Uncapped);

            intent.Add("calendar", IntentChannel.ArtificialLight, night * sky * 0.3f,
                       "outdoors after dark");

            // --- what the scene needs ----------------------------------------
            //
            // Readability is not a look. It is the channel that says the player
            // may struggle to see, and it exists to set floors under everything
            // that removes light.

            intent.Add("depth", IntentChannel.Readability,
                       Clamp01(world.Depth / DeepUnderground) * 0.6f,
                       "underground");

            intent.Add("sky", IntentChannel.Readability, enclosed * 0.35f,
                       "enclosed space");

            intent.Add("calendar", IntentChannel.Readability, night * 0.45f,
                       "night");

            intent.Add("weather", IntentChannel.Readability,
                       world.Rain * 0.3f + world.Snow * 0.35f,
                       "precipitation obscures");

            // --- restraint ---------------------------------------------------
            //
            // The channel that outranks the others. It rises wherever the scene
            // is ALREADY hard to read, and everything that removes light, colour
            // or contrast is scaled down by it downstream. A visual overhaul
            // that makes a cave unnavigable has not improved the game, and that
            // is a much easier mistake to make than it sounds: every effect here
            // was tuned in daylight on the surface.

            intent.Add("depth", IntentChannel.Restraint,
                       Clamp01(world.Depth / DeepUnderground) * 0.5f,
                       "underground, where light is scarce already");

            intent.Add("calendar", IntentChannel.Restraint, night * 0.4f,
                       "night, where light is scarce already");

            intent.Add("weather", IntentChannel.Restraint,
                       Math.Max(world.Rain, world.Snow) * 0.25f,
                       "precipitation is already costing visibility");

            intent.Add("cloud", IntentChannel.Restraint, world.CloudCover * night * 0.3f,
                       "overcast night");

            return intent;
        }

        /// <summary>
        /// Rises from 0 at <paramref name="from"/> to 1 at <paramref name="to"/>,
        /// in either direction, smoothly.
        ///
        /// Written to take the two ends in the order the caller thinks about
        /// them - "cold below 4 down to -12" reads correctly as Ramp(t, 4, -12) -
        /// because getting a threshold backwards is otherwise invisible.
        /// </summary>
        private static float Ramp(float value, float from, float to)
        {
            if (Math.Abs(to - from) < 1e-4f) return value >= to ? 1f : 0f;

            float t = Clamp01((value - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        private static float Clamp01(float v)
        {
            if (float.IsNaN(v)) return 0f;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }
}
