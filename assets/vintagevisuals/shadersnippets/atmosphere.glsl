// Vintage Visuals - atmosphere: the air between the camera and the world
//
// Injected at vanilla's applyFog, which every shading program in the game
// declares identically, and which is the single point every fogged fragment
// passes through.
//
// WHAT THIS FILE IS AND IS NOT FOR
// -------------------------------
// Much of Vintage Story's atmosphere is driven from the CPU instead, by writing
// the game's own ambient stack - see src/Atmosphere/README.md and DECISIONS
// D18. That reaches the sky, the water and everything else this mod does not
// patch, which no GLSL here can. Height haze is entirely that; so are fog
// colour, density and floor.
//
// What is left is what the game does not compute at all: air that knows where
// the sun is. Vanilla's fog is
//
//     mix(pixel.rgb, rgbaFog.rgb, fogWeight)
//
// - isotropic, so haze looking into a low sun and haze looking away from it are
// the same grey. In the real thing they are not remotely the same, and the
// difference is most of what makes distance read as distance.
//
// ONE TRANSPORT, NOT TEN EFFECTS
// ------------------------------
// Eleven features land in this file. They are NOT eleven multipliers applied in
// turn - that arrangement loses energy uncontrollably and every one of the
// eleven looks reasonable while it does it. They contribute to two quantities:
//
//     EXTINCTION   how much of the surface's own light survives the trip
//     INSCATTER    what colour the air adds in its place
//
// and the frame is composed once, as participating media:
//
//     out = surface * T + inscatter * (1 - T)
//
// Vanilla's own fogWeight enters as its transmittance, 1 - fogWeight, and the
// mod's extra media multiplies it. Multiplying TRANSMITTANCES is not the same
// mistake as multiplying effects: it is what stacked media actually do, and it
// is why turning every strength to zero returns exactly
// mix(pixel, fogColor, fogWeight) - vanilla, algebraically, not approximately.
// A smoke check pins that identity numerically.
//
// Self-contained, sharing no uniform and no function with weather.glsl or
// pseudopbr.glsl even though all three land in the same file. Each is a
// separate patch group and any of them must be able to roll back without taking
// another's declarations with it.

// --- Feature strengths -------------------------------------------------------
//
// One per feature, already the product of the player's setting, whatever world
// state gates it, and whatever VisualBudget allowed. The shader gets the answer
// and never the ingredients.
//
// 0 is vanilla for every one of them, and 0 is what an unset uniform reads as -
// so an unpatched program, a rolled-back group and a binder that skipped a frame
// all land on vanilla rather than on something.

uniform float vv_atmosAerial;      // 1  aerial perspective: the transport itself
uniform float vv_atmosHorizon;     // 2  horizon scattering
uniform float vv_atmosSunScatter;  // 3  sun-aware scattering
uniform float vv_atmosHeight;      // 4  height attenuation
uniform float vv_atmosWeather;     // 5  weather extinction
uniform float vv_atmosCloud;       // 6  cloud-atmosphere coupling
uniform float vv_atmosCloudEdge;   // 7  cloud-edge forward scattering
uniform float vv_atmosGodray;      // 8  contribution to vanilla's godray pass
uniform float vv_atmosPrecip;      // 9  precipitation scattering
uniform float vv_atmosMoon;        // 10 moonlight scattering
uniform float vv_atmosDapple;      // 11 dapple interaction - FOUNDATION ONLY

uniform float vv_atmosWeatherTint; // how much rain drains colour from the air

// --- Normalised world state --------------------------------------------------
//
// Every one of these is read from Vintage Story and normalised on the CPU. None
// is simulated here and none is a second opinion about something the game
// already knows. See AtmosphereInputs for where each comes from.

uniform vec3  vv_atmosSunDir;      // unit, camera-relative frame
uniform vec3  vv_atmosSunColor;    // the game's own sun colour, never modelled
uniform float vv_atmosSunElevation;// 0 horizon, 1 overhead, 0 once set
uniform vec3  vv_atmosMoonDir;     // unit
uniform float vv_atmosMoonLight;   // 0..1, already zero by day
uniform float vv_atmosRain;        // 0..1 falling now
uniform float vv_atmosSnow;        // 0..1 falling now
uniform float vv_atmosOvercast;    // 0 clear, 1 overcast
uniform float vv_atmosBrokenCloud; // 0..1, peaks at half cover
uniform float vv_atmosDensity;     // vanilla's own extinction, per block
uniform float vv_atmosAltitude;    // 0 sea level, 1 thin air

// Sky exposure is NOT a uniform here. Everything that needed it is already
// multiplied by it in AtmosphereInputs.Derive, on the CPU, once per frame -
// applying it a second time in the fragment shader would gate the same feature
// twice and make a porch twice as vanilla as it should be.

// Which diagnostic to draw instead of shading, 0 for none. See vvAtmosDebug.
uniform float vv_atmosDebug;

// The comparison wipe, in frame pixels. 0 disables it, which is what an unset
// uniform reads as. See pseudopbr.glsl for what it can and cannot split; this
// group declares its own copy because a uniform shared across patch groups
// would couple their rollbacks.
uniform float vv_atmosCompareWipe;

// --- Constants ---------------------------------------------------------------

const float VV_ATMOS_PI = 3.14159265;

// Vanilla's own fog distance clamp, from getFogLevel in every shading program:
// min(250, depth). Anything reasoning about how much haze a distance produces
// has to use the same number or it predicts a gradient the game does not draw.
// Pinned against AtmosphereState.FogDistanceClamp by a smoke check.
const float VV_ATMOS_FOG_CLAMP = 250.0;

// How forward-biased the sun's scattering is.
//
// The g of a Henyey-Greenstein phase function. Real atmospheric aerosol sits
// around 0.6-0.8; this is deliberately below that. The phase function is being
// applied to a fog term vanilla tuned by eye rather than to an actual optical
// depth, so a physical g piles the whole effect into a bright disc a few degrees
// wide around the sun - correct, and it reads as a bug.
const float VV_ATMOS_SUN_G = 0.45;

// Rain droplets are enormous next to aerosol and scatter almost straight ahead.
const float VV_ATMOS_RAIN_G = 0.65;

// Snow is the opposite: large, irregular, and close to isotropic. This is the
// difference the player should be able to SEE between a rainstorm and a
// snowstorm, rather than the same haze in a different tint.
const float VV_ATMOS_SNOW_G = 0.15;

// The moon is a light source of the same shape as the sun and a tiny fraction of
// the brightness, so it gets the same phase function and a hard ceiling.
const float VV_ATMOS_MOON_G = 0.40;

// THE ENERGY BUDGET.
//
// The most the inscattered colour may be brightened, as a fraction, by every
// additive term together. A ceiling and not a hope: the phase functions are
// unbounded as g approaches 1, the terms are summed, and the result multiplies a
// colour that has already been through vanilla's exposure. Without this, a low
// sun through broken cloud over a snowfield is three reasonable terms and one
// white horizon.
const float VV_ATMOS_MAX_GAIN = 0.85;

// The most extinction, per block, every mod-side source together may add on top
// of vanilla's. Vanilla's own default is 0.00125, so this is eight times it -
// about 92% obscured at the 250-block clamp from the mod's contribution alone.
// Mirrors AtmosphereInputs.MaxAddedDensity; a smoke check pins the pair.
const float VV_ATMOS_MAX_DENSITY = 0.01;

// Extinction added by aerial perspective at full strength, per block.
//
// Modest against vanilla's own 0.00125: this deepens the game's distance
// convergence rather than replacing it, and a value that dominated vanilla's
// would be a different look rather than a stronger one.
const float VV_ATMOS_AERIAL_DENSITY = 0.0022;

// Extinction added by rain and by snow at full intensity, before the strength
// slider. Snow is thicker: a blizzard closes the world down in a way rain does
// not, and this is the number that says so.
const float VV_ATMOS_RAIN_DENSITY = 0.004;
const float VV_ATMOS_SNOW_DENSITY = 0.006;

// How much of vanilla's own extinction the air keeps at full altitude.
//
// Restrained on purpose. Vintage Story models no vertical density profile at
// all, so this is an approximation standing in for one - see
// src/Atmosphere/README.md. A third off at a mountain top is enough to read as
// "the air is clearer up here" and little enough that being wrong about it
// costs nothing.
const float VV_ATMOS_THIN_AIR = 0.66;

// --- Geometry ----------------------------------------------------------------

// How far this fragment is, clamped where vanilla clamps.
float vvAtmosDistance(vec3 cameraRelativePos)
{
    return min(VV_ATMOS_FOG_CLAMP, length(cameraRelativePos));
}

// The view ray. Degenerate at the camera, where there is no direction and no air
// either, so it falls out rather than normalising a zero vector.
vec3 vvAtmosViewDir(vec3 cameraRelativePos)
{
    float len = length(cameraRelativePos);
    return len < 1e-4 ? vec3(0.0, 1.0, 0.0) : cameraRelativePos / len;
}

// Henyey-Greenstein, normalised so its isotropic case is 1 rather than 1/4pi.
//
// Normalising here rather than at the call site is deliberate: the raw form
// returns about 0.08 facing the sun at g = 0.45, which looks like the effect is
// off and invites raising the strength until the peak is wrong instead.
float vvAtmosPhase(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    float hg = (1.0 - g2) / (4.0 * VV_ATMOS_PI * pow(max(1e-4, denom), 1.5));

    return hg * 4.0 * VV_ATMOS_PI;
}

// How much of a light source is above the horizon, softened.
//
// A source BELOW the horizon lights the air from underneath the world, and a
// bright band pointing at it is a hole in the ground. A step would pop as the
// sun set; this fades over the last few degrees.
float vvAtmosAboveHorizon(float elevation)
{
    return clamp(elevation * 8.0, 0.0, 1.0);
}

// --- Extinction --------------------------------------------------------------

// What the mod's own media adds to vanilla's, per block.
//
// FEATURES 4, 5 and 9 land here, and they SUM rather than multiply - which is
// what extinction coefficients do. Summing is also what makes the ceiling
// meaningful: three sources each asking for a reasonable amount is exactly the
// case that needs one.
float vvAtmosAddedDensity(void)
{
    float added = 0.0;

    // FEATURE 1: aerial perspective. Distant surfaces converging toward the
    // atmosphere's own radiance, which is what the phrase means and all it
    // means - the DIRECTIONAL half is feature 3, and the two are separate
    // sliders because they are separate phenomena. Vanilla already does some of
    // this; this deepens it.
    //
    // Extinction rather than a blend toward the mod's result, deliberately. A
    // master blend would make every other feature die when this one is zero,
    // and each of the eleven is supposed to be independently switchable.
    added += VV_ATMOS_AERIAL_DENSITY * vv_atmosAerial;

    // FEATURE 5 / 9: weather. Rain and snow are separate coefficients because a
    // blizzard and a downpour do not close the world down at the same rate.
    added += vv_atmosRain * VV_ATMOS_RAIN_DENSITY * vv_atmosWeather;
    added += vv_atmosSnow * VV_ATMOS_SNOW_DENSITY * vv_atmosWeather;

    return min(added, VV_ATMOS_MAX_DENSITY);
}

// FEATURE 4: height attenuation.
//
// A MULTIPLIER on the whole extinction rather than an addition to it, because
// thin air is not another medium in the way - it is less of the medium that is
// already there. Returns 1 at sea level, so zero strength is vanilla.
float vvAtmosHeightFactor(void)
{
    float thin = mix(1.0, VV_ATMOS_THIN_AIR, vv_atmosAltitude);
    return mix(1.0, thin, vv_atmosHeight);
}

// Everything that survives the trip, 0 nothing to 1 all of it.
//
// Vanilla's fogWeight enters as ITS transmittance and the mod's media multiplies
// it, which is what stacked media do. With every strength at zero the added
// density is zero, the height factor is one, and this returns exactly
// 1 - fogWeight.
float vvAtmosTransmittance(float fogWeight, float distance)
{
    float vanilla = 1.0 - clamp(fogWeight, 0.0, 1.0);

    float sigma = vvAtmosAddedDensity();
    float height = vvAtmosHeightFactor();

    // The height factor scales vanilla's own coefficient too, which is the whole
    // point of the feature: at altitude you see further through the air the game
    // put there, not just through the air this mod added.
    //
    // Vanilla's contribution is re-expressed as an optical depth so the factor
    // can apply to it, then recomposed. At height 1 this is an identity.
    float vanillaDepth = -log(max(1e-4, vanilla));
    float extra = sigma * distance;

    return clamp(exp(-(vanillaDepth + extra) * height), 0.0, 1.0);
}

// --- Inscatter ---------------------------------------------------------------

// FEATURE 2: horizon scattering.
//
// Looking along the ground is looking through more air than looking up, so the
// horizon is where the atmosphere's own colour dominates. Derived from vanilla's
// fog colour and the game's sun colour - there is no sky palette in this file,
// and DECISIONS D21 records why the game's own sky texture cannot supply one to
// every shading program.
vec3 vvAtmosHorizonColor(vec3 fogColor, vec3 viewDir)
{
    if (vv_atmosHorizon <= 0.0) return fogColor;

    // 1 along the horizon, 0 straight up or straight down.
    float flatness = 1.0 - abs(viewDir.y);
    flatness = flatness * flatness;

    // Toward the sun's own colour, weighted by how low the sun is. A high sun
    // leaves the horizon vanilla's grey; a low one warms the band it is sitting
    // in, using the colour the game already decided that sun has.
    float lowSun = (1.0 - vv_atmosSunElevation) * vvAtmosAboveHorizon(vv_atmosSunElevation);
    vec3 warmed = fogColor * vv_atmosSunColor;

    return mix(fogColor, warmed, flatness * lowSun * vv_atmosHorizon);
}

// FEATURE 5 tint: rain draining the colour out of the air.
//
// Shifts vanilla's colour rather than replacing it. That colour already carries
// the time of day, the biome and whatever ambient modifiers are loaded, and rain
// does not repaint the sky - it drains it.
vec3 vvAtmosWeatherColor(vec3 fogColor)
{
    float blend = clamp((vv_atmosRain + vv_atmosSnow) * vv_atmosWeatherTint, 0.0, 1.0);
    if (blend <= 0.0) return fogColor;

    float luma = dot(fogColor, vec3(0.2126, 0.7152, 0.0722));
    return mix(fogColor, vec3(luma), blend);
}

// How much brighter the air gets, and from what.
//
// FEATURES 3, 6, 7, 9 and 10 land here. They SUM into one gain which is then
// capped once - not applied one after another, which would let five reasonable
// terms multiply into a white horizon.
float vvAtmosGain(vec3 viewDir)
{
    float gain = 0.0;

    float sunCos = clamp(dot(viewDir, vv_atmosSunDir), -1.0, 1.0);
    float sunUp = vvAtmosAboveHorizon(vv_atmosSunElevation);

    // FEATURE 3: sun-aware scattering. Strongest when the sun is LOW, because
    // that is when its light is travelling through the most air rather than
    // straight down through it. Floored so a high sun does not lose all of it.
    if (vv_atmosSunScatter > 0.0)
    {
        float lowSun = mix(0.35, 1.0, 1.0 - vv_atmosSunElevation);
        gain += max(0.0, vvAtmosPhase(sunCos, VV_ATMOS_SUN_G) - 1.0)
              * lowSun * sunUp * vv_atmosSunScatter;
    }

    // FEATURE 7: cloud-edge forward scattering.
    //
    // FOUNDATION ONLY. The game's cloud tiles say how much cloud is above a
    // place, not where an individual cloud's edge is - so this keys on a sky
    // that is neither clear nor solid, which is when sun breaking through
    // happens, rather than on an edge it cannot locate. See the README.
    if (vv_atmosCloudEdge > 0.0)
    {
        gain += max(0.0, vvAtmosPhase(sunCos, VV_ATMOS_SUN_G) - 1.0)
              * vv_atmosBrokenCloud * sunUp * vv_atmosCloudEdge * 0.5;
    }

    // FEATURE 9: precipitation scattering.
    //
    // The air ITSELF, as opposed to how much it takes - that is feature 5, and
    // it is a separate slider because a storm can reasonably want more of one
    // than the other. Rain scatters hard forward; snow is close to isotropic,
    // which is why a blizzard glows in every direction and a downpour only
    // toward the sun.
    if (vv_atmosPrecip > 0.0)
    {
        float rainGain = max(0.0, vvAtmosPhase(sunCos, VV_ATMOS_RAIN_G) - 1.0) * vv_atmosRain;
        float snowGain = max(0.0, vvAtmosPhase(sunCos, VV_ATMOS_SNOW_G) - 1.0) * vv_atmosSnow;

        gain += (rainGain + snowGain) * sunUp * vv_atmosPrecip;
    }

    // FEATURE 10: moonlight.
    //
    // Subordinate to the game's own night by construction. vv_atmosMoonLight has
    // already been scaled by how dark it is on the CPU, so a full moon at noon
    // arrives here as zero. The extra factor is small because a night this
    // makes brighter is a bug, not a stronger setting.
    if (vv_atmosMoon > 0.0)
    {
        float moonCos = clamp(dot(viewDir, vv_atmosMoonDir), -1.0, 1.0);
        float moonUp = vvAtmosAboveHorizon(vv_atmosMoonDir.y);

        gain += max(0.0, vvAtmosPhase(moonCos, VV_ATMOS_MOON_G) - 1.0)
              * vv_atmosMoonLight * moonUp * vv_atmosMoon * 0.25;
    }

    // FEATURE 6: cloud-atmosphere coupling.
    //
    // Cover diffuses the directional terms rather than blocking them. Applied to
    // the SUM, once, rather than inside each term - and applied here rather than
    // as a second attenuation of the surface, which is what the cloud shadows
    // already do to direct light. Those two are kept apart deliberately: a cloud
    // shadow decides how much sun lands on a block, this decides what the air in
    // between scatters. Collapsing them is how one overcast day gets darkened
    // twice.
    gain *= 1.0 - vv_atmosOvercast * vv_atmosCloud;

    return min(gain, VV_ATMOS_MAX_GAIN);
}

// The colour the air adds, all features folded in.
vec3 vvAtmosInscatter(vec3 fogColor, vec3 viewDir)
{
    vec3 color = vvAtmosWeatherColor(fogColor);
    color = vvAtmosHorizonColor(color, viewDir);

    // Multiplied INTO the colour, not added beside it. Adding lifts the air off
    // whatever it sits on and detaches it from the scene's own exposure;
    // multiplying keeps a grey air grey when the sun is white and warms it when
    // the sun is not.
    vec3 lit = color * vv_atmosSunColor;

    return mix(color, lit, vvAtmosGain(viewDir));
}

// --- Godrays -----------------------------------------------------------------

// FEATURE 8: what this mod contributes to VANILLA'S godray pass.
//
// Vintage Story already has crepuscular rays. godrays.fsh radially blurs the
// frame outward from the sun's screen position, weighted per pixel by the GREEN
// channel of the glow buffer that the shading programs write. So the correct
// integration is not a pass of this mod's own - it is a number written into a
// channel that already exists, on a buffer that already exists, feeding a pass
// the player's own graphics setting already turns on.
//
// Returns what to ADD to vanilla's own godray level, so zero is vanilla exactly.
// The caller is chunkopaque only: it is the one shading program with a godray
// channel at all.
float vvAtmosGodrayLevel(vec3 cameraRelativePos)
{
    if (vv_atmosGodray <= 0.0) return 0.0;

    vec3 viewDir = vvAtmosViewDir(cameraRelativePos);

    float sunCos = clamp(dot(viewDir, vv_atmosSunDir), -1.0, 1.0);
    float sunUp = vvAtmosAboveHorizon(vv_atmosSunElevation);

    // Rays need air to be visible in, so this rides the same media the rest of
    // the file does: thicker air, brighter shafts. That is also what keeps it
    // from firing on a clear day at noon, when there is nothing for light to
    // catch on and vanilla's own level is the right answer.
    float media = clamp((vv_atmosDensity + vvAtmosAddedDensity()) / VV_ATMOS_MAX_DENSITY, 0.0, 1.0);

    float forward = max(0.0, vvAtmosPhase(sunCos, VV_ATMOS_SUN_G) - 1.0);

    return clamp(forward * media * sunUp * vv_atmosGodray * 0.25, 0.0, 1.0);
}

// --- Diagnostics -------------------------------------------------------------
//
// Numbered in THIS shader's own terms, per the project convention: one global
// list stopped making sense as soon as two subsystems wanted the same number.
//
// The list is built to separate four questions that look identical on screen:
// is the GAME giving us the wrong data, are we NORMALISING it wrongly, is the
// SHADER wrong, or is the TUNING wrong? Views 2 and 6..12 answer the first two
// by drawing the inputs raw. Views 3..5 and 13 answer the third. Only then is
// the fourth worth asking.
//
//   1  final atmosphere: the composed result, which is what mode 0 also draws
//   2  raw state: sun elevation red, overcast green, precipitation blue
//   3  aerial perspective: transmittance, white is clear air
//   4  horizon scattering: the horizon colour alone
//   5  sun scattering: its gain alone
//   6  height attenuation: the altitude factor
//   7  weather extinction: added density against its own ceiling
//   8  cloud contribution: overcast red, broken cloud green
//   9  godray contribution
//   10 precipitation scattering: its gain alone
//   11 moon contribution: moonlight red, moon-facing gain green
//   12 dapple interaction - FOUNDATION ONLY, draws its strength flat
//   13 combined transport: transmittance red, total gain green, distance blue
vec4 vvAtmosDebug(int mode, vec4 pixel, float fogWeight, vec3 fogColor, vec3 cameraRelativePos)
{
    vec3 viewDir = vvAtmosViewDir(cameraRelativePos);
    float distance = vvAtmosDistance(cameraRelativePos);
    float t = vvAtmosTransmittance(fogWeight, distance);

    if (mode == 1)
    {
        return vec4(mix(pixel.rgb, vvAtmosInscatter(fogColor, viewDir), 1.0 - t), pixel.a);
    }

    if (mode == 2)
    {
        // The inputs, raw. If this is wrong, nothing downstream can be right,
        // and no amount of tuning the shader is the fix.
        return vec4(vv_atmosSunElevation, vv_atmosOvercast,
                    clamp(vv_atmosRain + vv_atmosSnow, 0.0, 1.0), pixel.a);
    }

    if (mode == 3) return vec4(vec3(t), pixel.a);
    if (mode == 4) return vec4(vvAtmosHorizonColor(fogColor, viewDir), pixel.a);

    if (mode == 5)
    {
        float sunCos = clamp(dot(viewDir, vv_atmosSunDir), -1.0, 1.0);
        float lowSun = mix(0.35, 1.0, 1.0 - vv_atmosSunElevation);
        float g = max(0.0, vvAtmosPhase(sunCos, VV_ATMOS_SUN_G) - 1.0)
                * lowSun * vvAtmosAboveHorizon(vv_atmosSunElevation) * vv_atmosSunScatter;
        return vec4(vec3(clamp(g, 0.0, 1.0)), pixel.a);
    }

    if (mode == 6) return vec4(vec3(vvAtmosHeightFactor()), pixel.a);
    if (mode == 7) return vec4(vec3(vvAtmosAddedDensity() / VV_ATMOS_MAX_DENSITY), pixel.a);
    if (mode == 8) return vec4(vv_atmosOvercast, vv_atmosBrokenCloud, 0.0, pixel.a);
    if (mode == 9) return vec4(vec3(vvAtmosGodrayLevel(cameraRelativePos)), pixel.a);

    if (mode == 10)
    {
        float sunCos = clamp(dot(viewDir, vv_atmosSunDir), -1.0, 1.0);
        float rainGain = max(0.0, vvAtmosPhase(sunCos, VV_ATMOS_RAIN_G) - 1.0) * vv_atmosRain;
        float snowGain = max(0.0, vvAtmosPhase(sunCos, VV_ATMOS_SNOW_G) - 1.0) * vv_atmosSnow;
        float g = (rainGain + snowGain) * vvAtmosAboveHorizon(vv_atmosSunElevation) * vv_atmosPrecip;
        return vec4(vec3(clamp(g, 0.0, 1.0)), pixel.a);
    }

    if (mode == 11)
    {
        float moonCos = clamp(dot(viewDir, vv_atmosMoonDir), -1.0, 1.0);
        float g = max(0.0, vvAtmosPhase(moonCos, VV_ATMOS_MOON_G) - 1.0)
                * vv_atmosMoonLight * vvAtmosAboveHorizon(vv_atmosMoonDir.y);
        return vec4(vv_atmosMoonLight, clamp(g, 0.0, 1.0), 0.0, pixel.a);
    }

    // Flat, and flat is the honest picture: the strength arrives, and nothing
    // reads it. Dapple lives in the pseudopbr patch group and cloud shadows in
    // the weather group, and a function shared across patch groups couples
    // their rollbacks - which this project forbids, because one group rolling
    // back would leave another calling something that no longer exists.
    if (mode == 12) return vec4(vec3(vv_atmosDapple), pixel.a);

    if (mode == 13)
    {
        return vec4(t, vvAtmosGain(viewDir) / VV_ATMOS_MAX_GAIN,
                    distance / VV_ATMOS_FOG_CLAMP, pixel.a);
    }

    return pixel;
}

// --- Composition -------------------------------------------------------------

// The whole atmosphere, composed once.
//
// out = surface * T + inscatter * (1 - T)
//
// With every strength at zero: the added density is zero, the height factor is
// one, so T = 1 - fogWeight; the gain is zero and both colour shifts return
// their input, so inscatter = fogColor. The result is
// mix(pixel, fogColor, fogWeight) - vanilla, exactly.
vec4 vvAtmosphere(vec4 rgbaPixel, float fogWeight, vec3 fogColor, vec3 cameraRelativePos)
{
    // The wipe, before the debug views: a diagnostic drawn over half a frame is
    // harder to read than one drawn over all of it.
    if (vv_atmosCompareWipe > 0.0 && gl_FragCoord.x < vv_atmosCompareWipe)
    {
        return vec4(mix(rgbaPixel.rgb, fogColor, clamp(fogWeight, 0.0, 1.0)), rgbaPixel.a);
    }

    int debug = int(vv_atmosDebug + 0.5);
    if (debug > 0) return vvAtmosDebug(debug, rgbaPixel, fogWeight, fogColor, cameraRelativePos);

    float distance = vvAtmosDistance(cameraRelativePos);
    float t = vvAtmosTransmittance(fogWeight, distance);

    vec3 viewDir = vvAtmosViewDir(cameraRelativePos);
    vec3 inscatter = vvAtmosInscatter(fogColor, viewDir);

    return vec4(rgbaPixel.rgb * t + inscatter * (1.0 - t), rgbaPixel.a);
}

// The anchor line, pasted back. The patch REPLACES vanilla's applyFog signature
// with this whole file, so dropping this would delete the function every fog
// call in the shader goes through.
vec4 applyFog(vec4 rgbaPixel, float fogWeight) {
