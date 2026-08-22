using System;
using Vintagestory.API.MathTools;
using VintageVisuals.Common;
using VintageVisuals.Common.Scene;

namespace VintageVisuals.Atmosphere
{
    /// <summary>
    /// What the shader is actually told, derived once per frame on the CPU.
    ///
    /// The third layer of the split this subsystem is built around:
    ///
    ///   game state  ->  AtmosphereState  ->  AtmosphereInputs  ->  uniforms
    ///   (the game)      (what the air is)    (what to draw)        (GPU)
    ///
    /// <see cref="AtmosphereState"/> may not contain a config-scaled value: a
    /// strength slider states what the player wants, the state describes what
    /// the world is doing, and mixing them is how "off" stops meaning off. This
    /// struct is where the two meet, and it is the only place they do.
    ///
    /// It is also where NORMALISATION happens. Vintage Story's raw units - a
    /// per-block extinction coefficient of 0.00125, a temperature in degrees C,
    /// an altitude in blocks - are not things a shader should have to
    /// understand. Everything below leaves here either as a 0..1 factor or as a
    /// quantity in the one unit the GLSL works in, and every conversion is
    /// documented at the field.
    ///
    /// Pure and free of every game type except plain vector maths, so the whole
    /// derivation can be driven through any sky in tools/smoketest without a
    /// client.
    /// </summary>
    public readonly struct AtmosphereInputs
    {
        // --- Feature strengths ----------------------------------------------
        //
        // One float per feature, and no separate enabled flag. A strength of 0
        // is already the "behave like vanilla" value, an unset GLSL uniform
        // reads as exactly 0, and a second flag would be a second way to say
        // the same thing that could disagree with the first.
        //
        // Every one of these is the product of the player's setting, whatever
        // the world state gates it on, and whatever VisualBudget allowed. The
        // shader gets the answer, never the ingredients.

        public readonly float Aerial;
        public readonly float Horizon;
        public readonly float SunScatter;
        public readonly float HeightAttenuation;
        public readonly float WeatherExtinction;
        public readonly float WeatherTint;
        public readonly float CloudAtmosphere;
        public readonly float CloudEdge;
        public readonly float Godray;
        public readonly float Precipitation;
        public readonly float Moon;
        public readonly float Dapple;

        // --- Normalised world state -----------------------------------------

        /// <summary>Unit vector toward the sun, camera-relative frame.</summary>
        public readonly Vec3f SunDirection;

        /// <summary>The sun's own colour, from the game's calendar. Never modelled here.</summary>
        public readonly Vec3f SunColor;

        /// <summary>0 at the horizon, 1 overhead, 0 once the sun is down.</summary>
        public readonly float SunElevation;

        /// <summary>Unit vector toward the moon.</summary>
        public readonly Vec3f MoonDirection;

        /// <summary>How much the moon is lighting this night, 0..1. Already zero by day.</summary>
        public readonly float MoonLight;

        /// <summary>Rain falling now, 0..1.</summary>
        public readonly float Rain;

        /// <summary>Snow falling now, 0..1.</summary>
        public readonly float Snow;

        /// <summary>0 clear, 1 overcast. The same figure the cloud shadows read.</summary>
        public readonly float Overcast;

        /// <summary>
        /// Broken cloud, 0..1, peaking at half cover.
        ///
        /// DERIVED, and the honest name for what the cloud-edge feature can
        /// actually key on. The game's cloud tiles say how much cloud sits
        /// above a place; nothing in them locates an individual cloud's edge.
        /// What IS knowable is that a sky which is neither clear nor solid has
        /// edges in it somewhere, and that is when sun breaking through
        /// happens. See src/Atmosphere/README.md for the data gap.
        /// </summary>
        public readonly float BrokenCloud;

        /// <summary>
        /// Vanilla's own distance-fog density, per block.
        ///
        /// Passed through unnormalised on purpose. It is an extinction
        /// coefficient and the shader uses it as one, in the same
        /// <c>exp(-sigma * d)</c> the game uses. Normalising it to 0..1 would
        /// destroy the only unit in this struct that has a physical meaning.
        /// </summary>
        public readonly float BaseDensity;

        /// <summary>
        /// How thin the air is where the camera is, 0 sea level, 1 high.
        ///
        /// Normalised against <see cref="AtmosphereInputs.ThinAirBlocks"/>. Vintage Story
        /// models no vertical density profile at all, so this is an
        /// approximation and not a measurement - it is here so the feature has
        /// one number to scale rather than each shader inventing its own
        /// altitude curve.
        /// </summary>
        public readonly float Altitude;

        /// <summary>
        /// 0 fully enclosed, 1 open sky.
        ///
        /// Almost everything here is multiplied by this. An atmosphere is what
        /// lies between the camera and the sky, and in a cave there is none -
        /// vanilla's own fog is doing that work, and adding sun scattering to a
        /// tunnel would be inventing a sun the player cannot see.
        /// </summary>
        public readonly float SkyExposure;

        /// <summary>Which diagnostic to draw instead of shading, 0 for none.</summary>
        public readonly float DebugView;

        /// <summary>
        /// Blocks of altitude over which the air is treated as fully thinned.
        ///
        /// Roughly the height of the tallest terrain Vintage Story generates
        /// above sea level, so a mountain top lands near 1 and ordinary ground
        /// stays near 0. Named because the shader must not carry a second copy
        /// of it and a test pins the normalisation.
        /// </summary>
        public const float ThinAirBlocks = 180f;

        /// <summary>
        /// The most extinction, per block, that every mod-side source together
        /// may add on top of vanilla's.
        ///
        /// An energy budget, not a tuning constant. Vanilla's own default is
        /// 0.00125 per block; this is eight times that, which at the 250-block
        /// clamp takes a distant surface to about 92% obscured on its own. Past
        /// that there is no image left to have an atmosphere in, and the
        /// features stack additively, so without a ceiling a snowstorm in a
        /// humid valley at altitude would each be reasonable alone and white
        /// together.
        /// </summary>
        public const float MaxAddedDensity = 0.01f;

        private AtmosphereInputs(
            float aerial, float horizon, float sunScatter, float heightAttenuation,
            float weatherExtinction, float weatherTint, float cloudAtmosphere, float cloudEdge,
            float godray, float precipitation, float moon, float dapple,
            Vec3f sunDirection, Vec3f sunColor, float sunElevation,
            Vec3f moonDirection, float moonLight,
            float rain, float snow, float overcast, float brokenCloud,
            float baseDensity, float altitude, float skyExposure, float debugView)
        {
            Aerial = aerial;
            Horizon = horizon;
            SunScatter = sunScatter;
            HeightAttenuation = heightAttenuation;
            WeatherExtinction = weatherExtinction;
            WeatherTint = weatherTint;
            CloudAtmosphere = cloudAtmosphere;
            CloudEdge = cloudEdge;
            Godray = godray;
            Precipitation = precipitation;
            Moon = moon;
            Dapple = dapple;
            SunDirection = sunDirection;
            SunColor = sunColor;
            SunElevation = sunElevation;
            MoonDirection = moonDirection;
            MoonLight = moonLight;
            Rain = rain;
            Snow = snow;
            Overcast = overcast;
            BrokenCloud = brokenCloud;
            BaseDensity = baseDensity;
            Altitude = altitude;
            SkyExposure = skyExposure;
            DebugView = debugView;
        }

        /// <summary>
        /// Everything off, and every state value at its no-influence reading.
        ///
        /// What an unbound program, a rolled-back patch group and a disabled
        /// subsystem all have to look like. Every strength is 0, which is also
        /// what an unset GLSL uniform reads as - so the three cases are
        /// indistinguishable by construction rather than by three code paths
        /// that have to agree.
        /// </summary>
        public static AtmosphereInputs Off
        {
            get
            {
                return new AtmosphereInputs(
                    0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
                    new Vec3f(0f, 1f, 0f), new Vec3f(1f, 1f, 1f), 0f,
                    new Vec3f(0f, -1f, 0f), 0f,
                    0f, 0f, 0f, 0f,
                    AtmosphereState.VanillaFogDensity, 0f, 1f, 0f);
            }
        }

        /// <summary>
        /// The one derivation. Pure, so the whole table can be driven in a test.
        /// </summary>
        /// <param name="air">What the world is doing. Never scaled by config.</param>
        /// <param name="config">What the player asked for.</param>
        /// <param name="grants">What arbitration allowed. See VisualBudget.</param>
        public static AtmosphereInputs Derive(AtmosphereState air, AtmosphereConfig config, SceneGrants grants)
        {
            if (config == null || !config.Enabled) return Off;

            float sky = Clamp01(air.SkyExposure);

            // Every feature that describes light travelling through open air is
            // gated on there BEING open air. Extinction is not: vanilla fogs a
            // cave too, and a mod that cleared the air underground would be
            // removing something the game put there.
            float outdoor = sky;

            float elevation = Clamp01(air.SunElevation);
            float day = Clamp01(air.DayLight);

            // Overcast diffuses the sun rather than blocking it, so directional
            // terms fade toward the flat sky as cover rises. Applied ONCE, here,
            // rather than inside each directional feature - three of them doing
            // it independently is how one overcast afternoon gets dimmed thrice.
            float overcast = Clamp01(air.CloudCover);
            float direct = 1f - overcast * Clamp01(config.CloudAtmosphere);

            // Neither clear nor solid. Peaks at half cover and is zero at both
            // ends, which is the most the game's tile data can honestly say
            // about where a cloud's edge is.
            float broken = Clamp01(4f * overcast * (1f - overcast));

            float rain = Clamp01(air.Rain);
            float snow = Clamp01(air.Snow);

            // Rain fog is the one atmospheric claim VisualBudget already
            // arbitrates, because weather owns the haze role and three
            // subsystems used to wash out the same rainy afternoon
            // independently. The grant, not the config value.
            float weatherGrant = Clamp(grants.RainFog, 0f, 1f);

            float altitude = Clamp01(air.HeightAboveSeaLevel / ThinAirBlocks);

            return new AtmosphereInputs(
                aerial: Clamp01(config.AerialPerspective) * outdoor,
                horizon: Clamp01(config.HorizonScattering) * outdoor,
                sunScatter: Clamp01(config.SunScattering) * outdoor * direct * day,
                heightAttenuation: Clamp01(config.HeightAttenuation) * outdoor,
                weatherExtinction: Clamp01(config.WeatherExtinction) * weatherGrant,
                weatherTint: Clamp01(config.WeatherTint),
                cloudAtmosphere: Clamp01(config.CloudAtmosphere) * outdoor,
                cloudEdge: Clamp01(config.CloudEdgeScattering) * outdoor * day,
                // Quality scales the CONTRIBUTION, never the enablement. At
                // strength 0 nothing is contributed whatever the quality says,
                // which is section 29's rule and the reason the two are
                // multiplied here rather than branched on.
                godray: Clamp01(config.Godrays) * GodrayQualityScale(config.GodrayQuality) * outdoor,
                precipitation: Clamp01(config.PrecipitationScattering) * weatherGrant,

                // Subordinate to the game's own night by construction: the moon
                // term carries MoonLight, which the tracker has already scaled
                // by how dark it is. A full moon at noon reaches here as zero.
                moon: Clamp01(config.MoonScattering) * outdoor * Clamp01(air.MoonLight),

                dapple: Clamp01(config.DappleInteraction) * outdoor,

                sunDirection: air.SunDirection ?? new Vec3f(0f, 1f, 0f),
                sunColor: air.SunColor ?? new Vec3f(1f, 1f, 1f),
                sunElevation: elevation,
                moonDirection: air.MoonDirection ?? new Vec3f(0f, -1f, 0f),
                moonLight: Clamp01(air.MoonLight),
                rain: rain,
                snow: snow,
                overcast: overcast,
                brokenCloud: broken,
                baseDensity: air.FogDensity,
                altitude: altitude,
                skyExposure: sky,
                debugView: config.AirDebugView);
        }

        /// <summary>
        /// What a quality tier is worth, as a fraction.
        ///
        /// Deliberately narrow. The godray PASS belongs to Vintage Story and so
        /// does its sample count - that is the player's own graphics setting,
        /// not this mod's. What this mod controls is how much of the frame it
        /// marks as godray source, so "low" is a smaller contribution rather
        /// than a cheaper one. Anything claiming to make vanilla's pass cheaper
        /// from here would be a lie.
        /// </summary>
        private static float GodrayQualityScale(float quality)
        {
            if (float.IsNaN(quality)) return 0f;
            if (quality < 0.5f) return 0.4f;
            if (quality < 1.5f) return 0.7f;
            return 1f;
        }

        private static float Clamp01(float v)
        {
            if (float.IsNaN(v)) return 0f;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        private static float Clamp(float v, float min, float max)
        {
            if (float.IsNaN(v)) return min;
            return v < min ? min : (v > max ? max : v);
        }
    }
}
