// Vintage Visuals - atmosphere: the air between the camera and the world
//
// Injected at vanilla's applyFog, which every shading program in the game
// declares identically, and which is the single point every fogged fragment
// passes through.
//
// WHAT THIS FILE IS AND IS NOT FOR
// -------------------------------
// Almost all of Vintage Story's atmosphere is driven from the CPU instead, by
// writing the game's own ambient stack - see src/Atmosphere/README.md and
// DECISIONS D18. That reaches the sky, the water and everything else this mod
// does not patch, which no GLSL here can.
//
// What is left is the one thing vanilla genuinely does not have: fog that knows
// where the sun is. Vanilla's is an isotropic mix toward a single colour -
//
//     mix(pixel.rgb, rgbaFog.rgb, fogWeight)
//
// - so haze looking into a low sun and haze looking away from it are the same
// grey. In the real thing they are not remotely the same, and the difference is
// most of what makes distance read as distance.
//
// So this file adds a directional in-scattering term and NOTHING ELSE. It does
// not add its own distance falloff, its own height band, or its own desaturation
// with depth: vanilla already has the first two and the third would be a second
// subsystem quietly removing colour, which is what VisualBudget exists to stop.
//
// Self-contained, sharing no uniform and no function with weather.glsl or
// pseudopbr.glsl even though all three land in the same file. Each is a separate
// patch group and any of them must be able to roll back without taking another's
// declarations with it.

// Strength of the whole effect. 0 is vanilla exactly, and 0 is what an unset
// uniform reads as - so an unpatched program, a rolled-back group and a binder
// that skipped a frame all land on vanilla rather than on something.
uniform float vv_atmosAerial;

// Unit vector toward the sun, in the same camera-relative frame the vertex
// shaders work in.
//
// Uploaded rather than taken from vanilla's lightPosition on purpose. That
// uniform is declared in chunkopaque and chunktopsoil and NOT in the particle
// shaders, so reading it would make this file compile in some programs and not
// others - and the whole point of anchoring on applyFog is that every program
// gets the same answer.
//
// Unset it reads as (0,0,0), which makes the phase term a constant. Harmless,
// because vv_atmosAerial is then zero as well.
uniform vec3 vv_atmosSunDir;

// The sun's own colour, from the game's calendar. Not modelled here: Vintage
// Story already reddens the sun near the horizon per player position, with a
// per-day offset so no two sunsets match. See DECISIONS D21.
uniform vec3 vv_atmosSunColor;

// 0 at the horizon, 1 overhead, 0 once the sun is down.
//
// The effect is scaled by this rather than by daylight. Forward scattering is
// strongest exactly when the sun is low and the light is travelling through the
// most air - but a sun BELOW the horizon is lighting the haze from underneath
// the world, and a bright band pointing at it would be a hole in the ground.
uniform float vv_atmosSunElevation;

// --- Weather visibility ------------------------------------------------------
//
// This used to live in weather.glsl, patched into the two terrain shaders only.
// That is why an animal standing in a fogged valley kept crisp edges while the
// hillside behind it went soft: the fog was thickened for terrain and for
// nothing else. It is the same arithmetic, moved to where every shading program
// sees it.

uniform float vv_atmosRain;         // 0..1 precipitation intensity, smoothed
uniform float vv_atmosFogStrength;  // how much rain thickens the air
uniform float vv_atmosFogTint;      // how much rain drains colour from it

// Which diagnostic to draw instead of shading, 0 for none. See vvAtmosDebug.
uniform float vv_atmosDebug;

// How forward-biased the scattering is.
//
// The g of a Henyey-Greenstein phase function. Real atmospheric aerosol sits
// around 0.6-0.8; this is deliberately below that. The phase function is being
// applied to a fog term that vanilla already tuned by eye rather than to an
// actual optical depth, so a physical g piles the whole effect into a bright
// disc a few degrees wide around the sun - correct, and it reads as a bug.
const float VV_ATMOS_ANISOTROPY = 0.45;

// The most the in-scattering may brighten the fog, as a fraction.
//
// A cap rather than a hope. The phase function is unbounded as g approaches 1
// and the term multiplies a colour that has already been through vanilla's own
// exposure, so without this a low sun can push the horizon past white and take
// the sky's gradient with it.
const float VV_ATMOS_MAX_GAIN = 0.85;

// Vanilla's own fog distance clamp, from getFogLevel in every shading program:
// min(250, depth). Anything here that reasons about how much haze a distance
// produces has to use the same number or it predicts a gradient the game does
// not draw. Pinned against AtmosphereState.FogDistanceClamp by a smoke check.
const float VV_ATMOS_FOG_CLAMP = 250.0;

const float VV_ATMOS_PI = 3.14159265;

// How much air is between the camera and this fragment, 0..1.
//
// Derived from DISTANCE rather than from fogWeight. fogWeight is a mix factor:
// it saturates, it has fogMin added into it, and underwater it is pinned near 1
// by murkiness - so using it would make the in-scattering strongest exactly
// where there is no sky to scatter.
float vvAtmosDepth(vec3 cameraRelativePos)
{
    float d = min(VV_ATMOS_FOG_CLAMP, length(cameraRelativePos));
    return clamp(d / VV_ATMOS_FOG_CLAMP, 0.0, 1.0);
}

// Henyey-Greenstein, normalised so that its isotropic case is 1 rather than
// 1/4pi.
//
// Normalising here rather than at the call site is deliberate: the un-normalised
// form returns about 0.08 at g=0.45 facing the sun, which looks like the effect
// is off and invites tuning the strength up until the peak is wrong instead.
float vvAtmosPhase(float cosTheta)
{
    float g = VV_ATMOS_ANISOTROPY;
    float g2 = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    float hg = (1.0 - g2) / (4.0 * VV_ATMOS_PI * pow(max(1e-4, denom), 1.5));

    return hg * 4.0 * VV_ATMOS_PI;
}

// The fog colour this fragment should fade toward.
//
// Vanilla's colour, shifted toward the sun's where the view ray points at the
// sun and the sun is low. It is a SHIFT and not a replacement: rgbaFog.rgb
// already carries the time of day, the weather and whatever ambient modifiers
// are loaded, and none of that is this file's to overrule.
vec3 vvAtmosScatter(vec3 fogColor, vec3 cameraRelativePos)
{
    if (vv_atmosAerial <= 0.0) return fogColor;

    // Degenerate at the camera, where there is no view direction and no air
    // either. Falls out rather than normalising a zero vector.
    float len = length(cameraRelativePos);
    if (len < 1e-4) return fogColor;

    vec3 viewDir = cameraRelativePos / len;

    float phase = vvAtmosPhase(clamp(dot(viewDir, vv_atmosSunDir), -1.0, 1.0));

    // Three things have to be true at once for a bright haze: the ray points
    // near the sun, the sun is low enough for the light to be travelling
    // through air rather than down through it, and there is enough distance for
    // anything to have scattered.
    //
    // The elevation term is inverted - LOW sun scatters more - but floored, so
    // a sun on the horizon does not get the whole effect and a sun straight
    // overhead does not lose all of it.
    float lowSun = mix(0.35, 1.0, 1.0 - vv_atmosSunElevation);
    float up = step(0.0, vv_atmosSunElevation) * min(1.0, vv_atmosSunElevation * 8.0);

    float gain = clamp(phase - 1.0, 0.0, 4.0)
               * lowSun * up
               * vvAtmosDepth(cameraRelativePos)
               * vv_atmosAerial;

    gain = min(gain, VV_ATMOS_MAX_GAIN);

    // Toward the sun's colour multiplied INTO the fog, not added beside it.
    // Adding would lift the fog off any surface it sits on and detach the haze
    // from the scene's own exposure; multiplying keeps a grey fog grey when the
    // sun is white and warms it when the sun is not.
    vec3 lit = fogColor * vv_atmosSunColor;

    return mix(fogColor, lit, gain);
}

// --- Weather visibility ------------------------------------------------------

float vvAtmosFogAmount(float fogWeight)
{
    float extra = clamp(vv_atmosRain * vv_atmosFogStrength, 0.0, 1.0);
    if (extra <= 0.0) return fogWeight;

    // Screen blend rather than addition. Fog that is already thick approaches
    // full without ever exceeding it; a plain addition makes heavy rain at
    // distance a flat wall of fog colour with no depth left in it.
    return clamp(fogWeight + (1.0 - fogWeight) * extra, 0.0, 1.0);
}

vec3 vvAtmosFogColor(vec3 fogColor)
{
    float blend = clamp(vv_atmosRain * vv_atmosFogTint, 0.0, 1.0);
    if (blend <= 0.0) return fogColor;

    // Shifts vanilla's colour rather than replacing it. That colour already
    // carries the time of day and the ambient stack, and rain does not repaint
    // the sky, it drains it.
    float luma = dot(fogColor, vec3(0.2126, 0.7152, 0.0722));
    vec3 overcast = vec3(luma);

    return mix(fogColor, overcast, blend);
}

// --- Diagnostics -------------------------------------------------------------
//
// Numbered in THIS shader's own terms, per the project convention: one global
// list stopped making sense as soon as two subsystems wanted the same number.
//
//   1  distance, as the fraction of vanilla's own 250-block fog clamp
//   2  the phase function alone, facing the sun is white
//   3  the final in-scattering gain
//   4  the fog colour actually used, in-scattering included
//   5  the fog WEIGHT actually used, weather included
//   6  the sun direction, remapped so nothing is negative
//   7  the sun's own colour, flat
//   8  the rain term, so a slider that does nothing is visible as black
vec4 vvAtmosDebug(int mode, vec4 pixel, float fogWeight, vec3 fogColor, vec3 cameraRelativePos)
{
    if (mode == 1) return vec4(vec3(vvAtmosDepth(cameraRelativePos)), pixel.a);

    float len = length(cameraRelativePos);
    vec3 viewDir = len < 1e-4 ? vec3(0.0, 1.0, 0.0) : cameraRelativePos / len;

    if (mode == 2)
    {
        float phase = vvAtmosPhase(clamp(dot(viewDir, vv_atmosSunDir), -1.0, 1.0));
        return vec4(vec3(clamp(phase - 1.0, 0.0, 1.0)), pixel.a);
    }

    if (mode == 3)
    {
        vec3 shifted = vvAtmosScatter(fogColor, cameraRelativePos);
        // The gain is not kept as a scalar anywhere, so it is recovered as the
        // distance between the shifted colour and the original. That is what
        // the eye is being asked about anyway.
        return vec4(vec3(clamp(length(shifted - fogColor) * 3.0, 0.0, 1.0)), pixel.a);
    }

    if (mode == 4) return vec4(vvAtmosScatter(vvAtmosFogColor(fogColor), cameraRelativePos), pixel.a);
    if (mode == 5) return vec4(vec3(vvAtmosFogAmount(fogWeight)), pixel.a);
    if (mode == 6) return vec4(vv_atmosSunDir * 0.5 + 0.5, pixel.a);
    if (mode == 7) return vec4(vv_atmosSunColor, pixel.a);
    if (mode == 8) return vec4(vec3(clamp(vv_atmosRain, 0.0, 1.0)), pixel.a);

    return pixel;
}

// Everything, in the order vanilla would have done it.
//
// The weather tint is applied BEFORE the in-scattering, not after. Rain drains
// the colour out of the air; the sun then lights whatever colour is left. Doing
// it the other way round lets a rainy sunset stay saturated orange, which is the
// one weather in which the haze should be least colourful.
vec4 vvAtmosphere(vec4 rgbaPixel, float fogWeight, vec3 fogColor, vec3 cameraRelativePos)
{
    int debug = int(vv_atmosDebug + 0.5);
    if (debug > 0) return vvAtmosDebug(debug, rgbaPixel, fogWeight, fogColor, cameraRelativePos);

    vec3 color = vvAtmosScatter(vvAtmosFogColor(fogColor), cameraRelativePos);
    float amount = vvAtmosFogAmount(fogWeight);

    return vec4(mix(rgbaPixel.rgb, color, amount), rgbaPixel.a);
}

// The anchor line, pasted back. The patch REPLACES vanilla's applyFog signature
// with this whole file, so dropping this would delete the function every fog
// call in the shader goes through.
vec4 applyFog(vec4 rgbaPixel, float fogWeight) {
