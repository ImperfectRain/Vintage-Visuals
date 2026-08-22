using Vintagestory.API.MathTools;

namespace VintageVisuals.Common.Scene
{
    /// <summary>
    /// What the air between the camera and everything else is doing, as one
    /// value, read from Vintage Story's own atmosphere rather than modelled
    /// beside it.
    ///
    /// Separate from <see cref="EnvironmentState"/> on purpose. That struct
    /// answers "what is the world doing" - it is raining, it is autumn, the
    /// player is underground. This one answers "what is the air doing between
    /// here and there", which is a different question with a different owner:
    /// almost every field below is a number the game already computes every
    /// frame and hands to its own shaders. Merging the two would put a dozen
    /// pass-through fields into a struct whose whole purpose is to be the
    /// mod's own reading of the world.
    ///
    /// The same two-kinds rule applies as in EnvironmentState:
    ///
    ///  - SAMPLED: what the game says, read once and otherwise unopinionated.
    ///  - DERIVED: an answer more than one consumer needs and none should own.
    ///
    /// And the same prohibition: NOTHING here may be scaled by a config value.
    /// A haze slider states what the player wants; this states what the air is.
    /// The product belongs in AtmosphereInputs.
    ///
    /// Why this is a CPU struct rather than shader code
    /// ------------------------------------------------
    /// Vanilla evaluates its sky in the fragment shader, through getSkyColorAt
    /// and the "sky" sampler. Both exist in chunkopaque.fsh and in NO other
    /// shading program - not chunktopsoil, not entityanimated, not the particle
    /// shaders, not chunkliquid. An atmosphere built on them would give opaque
    /// terrain one answer and the grass, the animals and the rain in front of
    /// it another, with the seam falling exactly where a hillside meets its own
    /// ground cover.
    ///
    /// So the atmosphere is decided ONCE, here, and uploaded identically to
    /// every program that shades anything. That costs no texture unit, which
    /// matters more than usual: adding a sampler to chunkopaque.fsh has twice
    /// cost this project the entire world render.
    ///
    /// Free of every game type except the API's plain vector maths, so the
    /// rules that read it can be driven through any sky in tools/smoketest
    /// without a client.
    /// </summary>
    public readonly struct AtmosphereState
    {
        // --- Vanilla's fog --------------------------------------------------
        //
        // All four come from IAmbientManager, which blends a stack of named
        // AmbientModifiers into one answer every frame. These are that answer,
        // and they are the same numbers the game uploads as fogColorIn,
        // fogDensityIn and fogMinIn - so anything derived from them agrees with
        // vanilla's own fog by construction rather than by tuning.

        /// <summary>SAMPLED. The colour vanilla fades distant things toward. <c>BlendedFogColor</c>.</summary>
        public readonly Vec3f FogColor;

        /// <summary>
        /// SAMPLED. Vanilla's distance-fog density. <c>BlendedFogDensity</c>.
        ///
        /// Used as <c>1 - 1/exp(distance * density)</c>, so it is per-block
        /// extinction and it is SMALL - the game's default ambient is 0.00125.
        /// </summary>
        public readonly float FogDensity;

        /// <summary>
        /// SAMPLED. A floor added to fog everywhere, after the distance term.
        /// <c>BlendedFogMin</c>. This is what makes being underwater or inside
        /// a fog sphere murky at arm's length.
        /// </summary>
        public readonly float FogMin;

        /// <summary>SAMPLED. Vanilla's own multiplier on how bright fog reads. <c>BlendedFogBrightness</c>.</summary>
        public readonly float FogBrightness;

        // --- Vanilla's height fog -------------------------------------------
        //
        // Vintage Story ALREADY has height fog, and it is not a shader effect
        // this mod would have to add: flatFogDensity and flatFogStart are
        // uniforms every shading program declares, and every one of them
        // computes 1 - 1/exp((worldY - flatFogStart) * flatFogDensity) and
        // takes the max of that and the distance term. The sky and the water
        // get it too, which nothing this mod patches would.
        //
        // So height haze is driven by writing these, not by reimplementing
        // them. See docs/DECISIONS.md D19.

        /// <summary>SAMPLED. Density of vanilla's height-banded fog. <c>BlendedFlatFogDensity</c>. Negative inverts the band.</summary>
        public readonly float FlatFogDensity;

        /// <summary>
        /// SAMPLED. The world height vanilla's height fog is measured from,
        /// already expressed the way the shader wants it.
        /// <c>BlendedFlatFogYPosForShader</c>, which the game documents as
        /// <c>BlendedFlatFogYPos + SeaLevel - MainCamera.TargetPosition.Y</c>.
        /// </summary>
        public readonly float FlatFogYPos;

        // --- The sun --------------------------------------------------------

        /// <summary>
        /// SAMPLED. The sun's colour at the player, normalised.
        /// <c>IClientGameCalendar.SunColor</c>.
        ///
        /// This is vanilla's answer to sun attenuation and the mod does not get
        /// to have a second one. The game already reddens the sun through
        /// thick air near the horizon, per player position, with a per-day
        /// offset (<c>SunsetMod</c>) so that no two sunsets match. A Rayleigh
        /// approximation layered on top would be a second description of the
        /// same sun, disagreeing with the sky disc the player can see.
        /// </summary>
        public readonly Vec3f SunColor;

        /// <summary>SAMPLED. Unit vector toward the sun. <c>SunPositionNormalized</c>.</summary>
        public readonly Vec3f SunDirection;

        /// <summary>
        /// DERIVED. How high the sun is: 0 at the horizon, 1 overhead, 0 once
        /// it is below the horizon.
        ///
        /// <see cref="SunDirection"/>.Y clamped at zero. Trivial, and here
        /// rather than at each consumer because "below the horizon reads as
        /// zero, not as a negative elevation" is a decision, and two consumers
        /// making it separately is how they stop agreeing.
        /// </summary>
        public readonly float SunElevation;

        // --- Ambient --------------------------------------------------------

        /// <summary>SAMPLED. Vanilla's blended ambient colour. <c>BlendedAmbientColor</c>.</summary>
        public readonly Vec3f AmbientColor;

        // --- Where the camera is in the air ---------------------------------

        /// <summary>
        /// SAMPLED. Camera height above sea level, in blocks, signed.
        /// <c>DefaultShaderUniforms.PlayerToSealevelOffset</c>.
        ///
        /// The game's own figure rather than a subtraction done here, because
        /// it is the one the sky shader uses for its earth-curvature bias and
        /// the two disagreeing would put the mod's horizon somewhere the
        /// game's is not.
        /// </summary>
        public readonly float HeightAboveSeaLevel;

        /// <summary>SAMPLED. The far plane, in blocks. <c>DefaultShaderUniforms.ZFar</c>.</summary>
        public readonly float ViewDistance;

        public AtmosphereState(Vec3f fogColor, float fogDensity, float fogMin, float fogBrightness,
                               float flatFogDensity, float flatFogYPos,
                               Vec3f sunColor, Vec3f sunDirection,
                               Vec3f ambientColor,
                               float heightAboveSeaLevel, float viewDistance)
        {
            FogColor = fogColor;
            FogDensity = fogDensity;
            FogMin = fogMin;
            FogBrightness = fogBrightness;
            FlatFogDensity = flatFogDensity;
            FlatFogYPos = flatFogYPos;
            SunColor = sunColor;
            SunDirection = sunDirection;
            SunElevation = sunDirection == null ? 0f : GameMath.Clamp(sunDirection.Y, 0f, 1f);
            AmbientColor = ambientColor;
            HeightAboveSeaLevel = heightAboveSeaLevel;
            ViewDistance = viewDistance;
        }

        /// <summary>
        /// A clear temperate noon at sea level, with vanilla's own default
        /// ambient.
        ///
        /// Every number here is read off AmbientModifier.DefaultAmbient rather
        /// than chosen, so "no influence" in a test means the same thing it
        /// means in a world that has not loaded an ambient modifier yet.
        /// </summary>
        public static AtmosphereState Clear
        {
            get
            {
                return new AtmosphereState(
                    fogColor: new Vec3f(201f / 255f, 211f / 255f, 219f / 255f),
                    fogDensity: 0.00125f,
                    fogMin: 0f,
                    fogBrightness: 1f,
                    flatFogDensity: 0f,
                    flatFogYPos: 0f,
                    sunColor: new Vec3f(1f, 1f, 1f),
                    sunDirection: new Vec3f(0f, 1f, 0f),
                    ambientColor: new Vec3f(1f, 1f, 1f),
                    heightAboveSeaLevel: 0f,
                    viewDistance: 1500f);
            }
        }

        /// <summary>
        /// Vanilla's own default distance-fog density, from
        /// <c>AmbientModifier.DefaultAmbient</c>.
        ///
        /// Named because two places need to know what "vanilla thickness"
        /// is - the ambient bridge, when deciding how much weather may add,
        /// and the tests, when asserting that an idle atmosphere changes
        /// nothing. It is small: 0.00125 per block is about 27% extinction at
        /// 250 blocks, which is where vanilla clamps distance fog.
        /// </summary>
        public const float VanillaFogDensity = 0.00125f;

        /// <summary>
        /// The distance, in blocks, past which vanilla stops accumulating
        /// distance fog.
        ///
        /// Not a choice: every shading program's getFogLevel does
        /// <c>min(250, depth)</c>. Anything here that reasons about how much
        /// haze a distance produces has to use the same clamp or it will
        /// predict a gradient the game does not draw.
        /// </summary>
        public const float FogDistanceClamp = 250f;
    }
}
