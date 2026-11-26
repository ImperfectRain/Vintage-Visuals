// Vintage Visuals - water surface (patch group "water", chunkliquid.fsh)
//
// Builds on what vanilla's water already does rather than replacing it:
// vanilla keeps its texture, flow, foam, vertex waves, droplets and lava
// branch, and this file runs on the finished water colour, just before the
// game's own fog. Lava is left alone entirely.
//
// TEXEL-LOCKED, like the terrain reflections. Every input that varies across
// the surface is evaluated at the centre of the water texel the fragment falls
// in (1/32 block on the face plane, the water texture's own pixel): the wave
// normal, the view vector, the reflection direction, the depth behind, the
// ripple. So the reflection, the
// depth colour and the glints are one colour per texture pixel, and the sky
// is looked up in direction CELLS as on terrain (vvTerrainReflectCell), so a
// wave steps through the reflected sky instead of smearing it.
//
// SOFTNESS (vv_waterSoft) is a blur in TEXEL space, not screen space: a
// texel's reflection is averaged with the reflections seen VV_WATER_SOFT_RADIUS
// texels away in four directions (the waves barely change between adjacent
// texels, so the immediate neighbours would blur nothing).
// Each texel stays one flat colour; neighbouring texels just differ by less.
//
// Order of the layers, as planned: the sky and horizon reflection, then the
// depth colouring, then the rain ripples, then the sun and moon glints, then
// the caustics the waves focus onto what lies beneath (D77), then the
// shoreline: a soft edge and a lapping foam line (D79). The world reflected
// in the water and the bed bent through the waves come from the "waterscene"
// group (waterscene.glsl, D82), called under #ifdef VV_WATER_SCENE: that group
// declares the sampler, so with it off this file is exactly the sky-only water.
//
// ZERO IS VANILLA. vv_water is the master and an unset uniform reads 0, so a
// program the binder never reached returns vanilla's colour untouched, and
// vvWaterVanillaSpecularKeep() returns 1. Every strength is zero-is-off on
// its own.
//
// Float precision: vv_waterOrigin arrives wrapped to 4096 blocks on the CPU
// (EnvironmentState.CameraPeriod), and every pattern here tiles that period
// exactly - the texel grid, the ripple cells, and the waves (whole cycles per
// 4096 blocks on both axes) - so the wrap never shows as a seam.

uniform float vv_water;            // master: 0 vanilla, 1 on
uniform vec3  vv_waterOrigin;      // camera world position, wrapped on the CPU
uniform vec3  vv_waterEye;         // the eye, relative to the player origin fWorldPos is measured from
uniform vec3  vv_waterSunDir;      // unit, toward the sun (AtmosphereState.SunDirection)
uniform vec3  vv_waterSunColor;    // the game's sun colour, already reddened at the horizon
uniform vec3  vv_waterMoonDir;     // unit, toward the moon
uniform float vv_waterSky;         // sky and horizon reflection strength
uniform float vv_waterSoft;        // texel-space blur of the reflection, 0 sharp
uniform float vv_waterSoftTaps;    // quality: 1 sharp only, 3 or 5 taps
uniform float vv_waterDepthTint;   // colour and opacity gained with depth
uniform float vv_waterClarity;     // how clear the shallows are
uniform float vv_waterRipples;     // rain falling x strength, 0 none
uniform float vv_waterRippleTime;  // 0..1 clock, whole drops per wrap
uniform float vv_waterRippleEpoch; // whole wraps of that clock so far, mod 4096
uniform float vv_waterRippleLayers;// quality: 1 or 2 ripple scales
uniform float vv_waterOwnDrops;    // 1 when this mod's rain rings replace vanilla's droplet rings
uniform float vv_waterGlints;      // sun and moon highlight strength
uniform float vv_waterWaves;       // texel wave relief strength
uniform float vv_waterCaustics;    // sunlight the waves focus onto the bed, 0 none
uniform float vv_waterCausticWaves;// quality: 3 or 6 focusing waves
uniform float vv_waterShore;      // shoreline soft edge and foam line, 0 none
uniform float vv_waterDebug;       // debug views, 0 off

// Whether anything below needs the bed point behind the texel.
#ifdef VV_WATER_SCENE
#define VV_WATER_REFRACTS (vv_waterRefraction > 0.001)
#else
#define VV_WATER_REFRACTS false
#endif

const float VV_WATER_TEXELS = 32.0;          // water texels per block: the water texture is 32x32
const float VV_WATER_F0 = 0.02;              // water's reflectance at normal incidence
const float VV_WATER_ROUGHNESS = 0.10;       // perceptual roughness for the glint lobe
const float VV_WATER_DEEP_PATH = 32.0;       // blocks: depth tint saturates well before this
const float VV_WATER_SKY_CAVE = 0.15;
const float VV_WATER_SOFT_RADIUS = 3.0;      // texels between a texel and each blur neighbour
// A pond's near-horizontal reflections meet its own banks and trees, not the
// sky: below VV_WATER_BANK_HIGH of elevation the reflected sky gives way to the
// surroundings, lit by the same sky at a typical ground albedo. DATA GAP: the
// banks are not looked up, only stood in for, until water has a scene trace.
const float VV_WATER_BANK_LOW = 0.02;
const float VV_WATER_BANK_HIGH = 0.18;
const float VV_WATER_BANK_SHADE = 0.42;      // the surroundings, relative to the sky lighting them
const float VV_WATER_GLINT_ALPHA = 0.6;      // most a glint may add to the water's opacity
const float VV_WATER_GLINT_PEAK = 1.2;       // a glint's luminance approaches this, never clips to it
const float VV_WATER_GLINT_FACETS = 0.62;    // share of texels with a soft facet; the rest glitter hard
const float VV_WATER_GLINT_HARD = 1.6;
const float VV_WATER_GLINT_SOFT = 0.3;
const float VV_WATER_GLINT_TWINKLE = 3.0;    // facet re-deals per unit of vanilla's wave counter
const float VV_WATER_GLINT_COVER_START = 0.45; // sky cover where the sun's path starts to fade
const float VV_WATER_GLINT_COVER_END = 0.9;
const float VV_WATER_RING_SHARE = 0.9;       // reflected share a full ring crest reaches, at any view angle
const float VV_WATER_SPEC_KEEP = 0.25;       // vanilla's broad sun specular kept at full glints

// Caustics (D77). The bed under open water is lit through a moving lens: the
// short focusing waves below, refracted into the water (index 1.33), gather
// the sun into bright lines that sharpen with depth up to their focus and then
// soften, because the sun is a disc and not a point. DATA GAP: the bed's own
// colour is not visible to this shader (no scene-colour sampler, by design),
// so the focused light lands on an assumed albedo; and the shadow is the one
// at the SURFACE, which is where the light enters.
const float VV_WATER_ETA = 0.75188;          // 1 / 1.33, air into water
const float VV_WATER_CAUSTIC_RELIEF = 0.07;  // blocks of height on the six focusing waves
const float VV_WATER_CAUSTIC_FOCUS = 2.0;    // blocks: the depth the lens settles at
const float VV_WATER_CAUSTIC_WARP = 0.3;     // blocks the web's cells are bent by
const float VV_WATER_CAUSTIC_SHARP = 0.05;   // how soft a focus line is right under the surface
const float VV_WATER_CAUSTIC_SPREAD = 0.05;  // ... and how much softer per block of depth
const float VV_WATER_CAUSTIC_PEAK = 1.0;     // a line approaches this gain, never clips to it
const float VV_WATER_CAUSTIC_ALBEDO = 0.35;  // the bed it lands on (DATA GAP above)
const float VV_WATER_CAUSTIC_DEEP = 5.0;     // blocks: faded out by this depth, from 0.3 of it
const float VV_WATER_CAUSTIC_FAR = 0.035;    // blocks of bed per pixel where the web is gone (about 30 blocks away)

// Shoreline (D79). Where the water behind a texel is only a sliver deep, the
// surface thins out over two texels so the bank meets it softly, and a foam
// line laps in and out: its reach swings with vanilla's wave counter, in a
// phase that wanders along the shore, and it breaks up per texel, dense at the
// edge and sparse further out. Lit by vanilla's own vertex light and shadow.
const float VV_WATER_SHORE_SOFT = 0.06;      // blocks of depth the edge thins over
const float VV_WATER_SHORE_FOAM = 0.30;      // blocks of depth the foam reaches at the top of a lap
const float VV_WATER_SHORE_ALPHA = 0.8;      // a foam texel's opacity
const float VV_WATER_SHORE_ALBEDO = 0.85;
const float VV_WATER_SHORE_KEEP = 0.35;      // vanilla's depth-edge foam kept while the foam line is on
const float VV_WATER_SHORE_FAR = 0.12;       // blocks of surface per pixel where the line is gone

// Rivers and far water (D79). Vanilla's flow vector turns the waves into
// streaks along the current (their slope across it is kept, along it is
// reduced: a linear map of the slope, so the waves still tile the period) and
// quickens the chop and the glitter; still water in still air goes calmer.
// DATA GAP: the flow vector's scale is the game's and undocumented; it is
// treated as full flow by VV_WATER_FLOW_FULL.
const float VV_WATER_FLOW_FULL = 0.5;
const float VV_WATER_FLOW_STREAK = 0.65;     // share of the along-flow slope removed in full flow
const float VV_WATER_STILL_CALM = 0.6;       // wave height on still water in still air
// Far swells: two long slow waves (about 11 and 12 blocks) that only come in
// with distance, so open water keeps its texture where the short waves are
// sub-pixel. Whole cycles per 4096 blocks.
const float VV_WATER_SWELL_NEAR = 20.0;      // blocks: no swell nearer than this
const float VV_WATER_SWELL_FAR = 60.0;       // blocks: full swell from here

// Absorption per block of path, red first: what makes deep water blue-green
// and shallow water clear. Scaled by vv_waterDepthTint.
const vec3 VV_WATER_ABSORB = vec3(0.42, 0.10, 0.065);
// What the water body scatters back toward the eye, relative to the light on it.
const vec3 VV_WATER_SCATTER = vec3(0.035, 0.115, 0.135);

// --- shared with terrain ---------------------------------------------------
// The sky cells and the analytic horizon are the terrain's (pbrterrainmaterial
// vvTerrainReflectionFallback / vvTerrainReflectCell), and so are the ripples
// (vvTerrainRippleSlope / vvTerrainRainRipples). They are copied rather than
// shared because each patch group must roll back alone; tools/smoketest
// compares the bodies so the two cannot drift.

const float VV_WATER_REFLECT_CELLS = 16.0;   // VV_TREFLECT_CELLS_SHARP
const float VV_WATER_HORIZON = 0.14;         // VV_TREFLECT_HORIZON
const float VV_WATER_GROUND = 0.34;          // VV_TREFLECT_GROUND
const float VV_WATER_HORIZON_LIFT = 1.15;    // VV_TREFLECT_HORIZON_LIFT
const float VV_WATER_TOWARD = 0.30;          // VV_TREFLECT_TOWARD
const float VV_WATER_MAX = 2.0;              // VV_TREFLECT_MAX
const float VV_WATER_TWO_PI = 6.28318530718;

const float VV_WATER_RIPPLE_DEPTH = 1.2;     // VV_TERRAIN_RIPPLE_DEPTH
const float VV_WATER_RIPPLE_BAND = 14.0;     // VV_TERRAIN_RIPPLE_BAND
const float VV_WATER_RIPPLE_WAVE = 30.0;     // VV_TERRAIN_RIPPLE_WAVE
const float VV_WATER_RIPPLE_JITTER = 0.5;    // VV_TERRAIN_RIPPLE_JITTER
const float VV_WATER_RIPPLE_TEXELS = 32.0;   // VV_TERRAIN_RIPPLE_TEXELS

uint vvWaterHashU(uint x)
{
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

vec4 vvWaterCellRandom(vec2 cell)
{
    uvec2 c = uvec2(ivec2(cell));
    uint h = vvWaterHashU(c.x * 0x9e3779b9u ^ vvWaterHashU(c.y + 0x85ebca6bu));
    return vec4(float(vvWaterHashU(h)),
                float(vvWaterHashU(h ^ 0x68bc21ebu)),
                float(vvWaterHashU(h ^ 0x02e5be93u)),
                float(vvWaterHashU(h ^ 0x9c8f2d3bu))) / 4294967295.0;
}

vec2 vvWaterRippleSlope(vec2 p, float t, float epoch, float density)
{
    vec2 cell = floor(p);
    vec2 local = fract(p);
    vec4 rnd = vvWaterCellRandom(cell);

    // Whole drops per wrap of the 0..1 clock, so the wrap cannot jump. The
    // drop COUNT runs on across wraps (epoch = wraps so far, whole), and each
    // drop draws its own chance to fall and its own spot in the cell: rain
    // lands somewhere new every time, and the density decides how many cells
    // are struck rather than which ones.
    float rate = 1.0 + floor(rnd.w * 3.0);
    float phase = t * rate + rnd.y * 0.61 + rnd.z * 0.39;
    float age = fract(phase);
    float drop = floor(phase) + epoch * rate;
    vec4 hit = vvWaterCellRandom(vec2(drop, floor(rnd.x * 1048576.0)));
    if (hit.x > density) return vec2(0.0);

    vec2 centre = vec2(0.5) + (hit.yz - 0.5) * VV_WATER_RIPPLE_JITTER;
    float reach = 0.5 - VV_WATER_RIPPLE_JITTER * 0.5;
    vec2 offset = local - centre;
    float r = length(offset);
    if (r > reach) return vec2(0.0);

    float front = age * reach;
    float band = exp(-abs(r - front) * VV_WATER_RIPPLE_BAND);
    float decay = (1.0 - age) * (1.0 - age);

    vec2 dir = offset / max(r, 1e-5);
    return dir * sin((r - front) * VV_WATER_RIPPLE_WAVE) * band * decay;
}

vec2 vvWaterRainRipples(vec2 worldXZ, float t, float density)
{
    vec2 p = floor(worldXZ * VV_WATER_RIPPLE_TEXELS) / VV_WATER_RIPPLE_TEXELS;
    vec2 slope = vvWaterRippleSlope(p * 2.0, t, vv_waterRippleEpoch, density);
    if (vv_waterRippleLayers > 1.5)
    {
        // The slow layer runs at half the clock. Its time and drop count are
        // rebuilt from the epoch so both stay continuous when the clock wraps.
        float u = mod(vv_waterRippleEpoch, 2.0) * 0.5 + t * 0.5 + 0.37;
        slope += vvWaterRippleSlope(p + vec2(31.43, 17.61), fract(u),
                                    floor(vv_waterRippleEpoch * 0.5) + floor(u), density) * 0.7;
    }
    return slope;
}

vec3 vvWaterReflectCell(vec3 r, float cells)
{
    float elevation = (floor((clamp(r.y, -1.0, 1.0) * 0.5 + 0.5) * cells) + 0.5) / cells;
    float azimuth = (floor((atan(r.z, r.x) / VV_WATER_TWO_PI + 0.5) * cells * 2.0) + 0.5)
                  / (cells * 2.0);
    float y = elevation * 2.0 - 1.0;
    float theta = (azimuth - 0.5) * VV_WATER_TWO_PI;
    float radius = sqrt(max(0.0, 1.0 - y * y));
    return vec3(cos(theta) * radius, y, sin(theta) * radius);
}

vec3 vvWaterReflectionFallback(vec3 direction, vec3 environment)
{
    float lift = direction.y > VV_WATER_HORIZON
        ? 1.0
        : (direction.y < -VV_WATER_HORIZON ? VV_WATER_GROUND : VV_WATER_HORIZON_LIFT);

    vec2 sunAzimuth = vv_waterSunDir.xz;
    if (dot(sunAzimuth, sunAzimuth) > 1e-6 && dot(direction.xz, direction.xz) > 1e-6)
    {
        float toward = dot(normalize(direction.xz), normalize(sunAzimuth));
        lift *= 1.0 + VV_WATER_TOWARD * toward * clamp(vv_sceneDayLight, 0.0, 1.0);
    }
    return environment * clamp(lift, 0.0, VV_WATER_MAX);
}

// --- water's own -------------------------------------------------------------

// Small irregular chop on top of the long waves: the analytic gradient of
// value noise on half-block cells, two octaves drifting different ways. It
// breaks up the regular pattern four sine waves make on their own. The cell
// index is wrapped to the 4096-block period (8192 and 16384 cells), so the
// pattern tiles the CPU's origin wrap like everything else here.
vec3 vvWaterValueNoise(vec2 p, float period)
{
    vec2 i = floor(p);
    vec2 f = p - i;
    vec2 u = f * f * (3.0 - 2.0 * f);
    vec2 du = 6.0 * f * (1.0 - f);
    float a = vvWaterCellRandom(mod(i, period)).x;
    float b = vvWaterCellRandom(mod(i + vec2(1.0, 0.0), period)).x;
    float c = vvWaterCellRandom(mod(i + vec2(0.0, 1.0), period)).x;
    float d = vvWaterCellRandom(mod(i + vec2(1.0, 1.0), period)).x;
    float value = a + (b - a) * u.x + (c - a) * u.y + (a - b - c + d) * u.x * u.y;
    vec2 grad = du * vec2(b - a + (a - b - c + d) * u.y, c - a + (a - b - c + d) * u.x);
    return vec3(value, grad);
}

vec2 vvWaterChop(vec2 world, float t)
{
    vec2 g = vvWaterValueNoise(world * 2.0 + vec2(t * 0.35, t * 0.21), 8192.0).yz * 2.0 * 0.020
           + vvWaterValueNoise(world * 4.0 - vec2(t * 0.28, -t * 0.47), 16384.0).yz * 4.0 * 0.008;
    return g;
}

// Wave slope at a world XZ: four travelling waves with WHOLE cycles per 4096
// blocks on both axes (integer wave numbers below), so the CPU's origin wrap
// lands on a phase boundary of every one of them. Wind sets the height; it
// cannot turn them, because a rotated wave stops tiling the period.
vec2 vvWaterWaveSlope(vec2 world, float t, float flowAmount)
{
    const float K = VV_WATER_TWO_PI / 4096.0;
    const vec2 k0 = vec2(900.0, 420.0) * K;     // ~4.1 blocks
    const vec2 k1 = vec2(-610.0, 1200.0) * K;   // ~3.0 blocks
    const vec2 k2 = vec2(1700.0, -1300.0) * K;  // ~1.9 blocks
    const vec2 k3 = vec2(380.0, -2300.0) * K;   // ~1.8 blocks

    vec2 s = k0 * cos(dot(k0, world) - t * 1.10) * 0.030
           + k1 * cos(dot(k1, world) - t * 1.35) * 0.024
           + k2 * cos(dot(k2, world) - t * 1.90) * 0.014
           + k3 * cos(dot(k3, world) - t * 2.05) * 0.012;
    float wind = clamp(vv_sceneWind, 0.0, 1.0);
    float height = 0.45 + 1.1 * wind;
    height *= mix(1.0, VV_WATER_STILL_CALM, (1.0 - flowAmount) * (1.0 - wind));
    return (s + vvWaterChop(world, t * (1.0 + 2.0 * flowAmount))) * height;
}

// The long swells for far water, as a slope.
vec2 vvWaterSwellSlope(vec2 world, float t)
{
    const float K = VV_WATER_TWO_PI / 4096.0;
    const vec2 s0 = vec2(300.0, 170.0) * K;     // ~11.9 blocks
    const vec2 s1 = vec2(-150.0, 330.0) * K;    // ~11.3 blocks
    return s0 * cos(dot(s0, world) - t * 0.45) * 0.10
         + s1 * cos(dot(s1, world) - t * 0.38) * 0.09;
}

// Streaks along a current: the slope across the flow is kept, along it is
// cut, so the normals vary mostly across the stream.
vec2 vvWaterStreak(vec2 slope, vec2 flowDir, float flowAmount)
{
    return slope - flowDir * dot(slope, flowDir) * (VV_WATER_FLOW_STREAK * flowAmount);
}

// One focusing wave's curvature. A travelling sine of unit height has the
// Hessian -k k^T sin(phase); returned as (xx, xz, zz). Whole cycles per 4096
// blocks, like the surface waves, so the origin wrap is a phase boundary.
vec3 vvWaterCausticWave(vec2 world, float t, vec2 cycles, float speed)
{
    vec2 k = cycles * (VV_WATER_TWO_PI / 4096.0);
    float s = sin(dot(k, world) - t * speed);
    return -vec3(k.x * k.x, k.x * k.y, k.y * k.y) * s;
}

// How much the waves gather the light reaching a floor `depth` blocks down,
// as a gain over flat water: 0 where no light gathers, rising on focus lines.
// Refraction displaces a ray by -depth (1 - 1/1.33) grad(height), so the floor
// sees the surface through J = I - f H, and light collects where det J -> 0.
// Light spread thin (det J > 1) is not taken from the bed: this pass can only
// add to what lies behind it, and the gain keeps that addition on the lines.
float vvWaterCausticGain(vec2 world, float t, float depth, float waves)
{
    // A slow drift bends the lattice the sines alone would draw into the
    // irregular cells of a real caustic web. Whole cycles per 4096 blocks too.
    const float K = VV_WATER_TWO_PI / 4096.0;
    world += VV_WATER_CAUSTIC_WARP * vec2(sin(dot(vec2(1300.0, -700.0) * K, world) - t * 0.7),
                                          sin(dot(vec2(600.0, 1400.0) * K, world) + t * 0.6));

    // The first three run 60 degrees apart, so Low still has no favoured direction.
    vec3 h = vvWaterCausticWave(world, t, vec2(3860.0, 540.0), 1.30)
           + vvWaterCausticWave(world, t, vec2(1650.0, 4080.0), 1.55)
           + vvWaterCausticWave(world, t, vec2(-2110.0, 2800.0), 1.85);
    float relief = VV_WATER_CAUSTIC_RELIEF * 1.41421;
    if (waves > 4.5)
    {
        h += vvWaterCausticWave(world, t, vec2(2600.0, 2030.0), 1.40)
           + vvWaterCausticWave(world, t, vec2(-570.0, 4660.0), 1.70)
           + vvWaterCausticWave(world, t, vec2(-3750.0, 1670.0), 1.15);
        relief = VV_WATER_CAUSTIC_RELIEF;   // twice the waves: the same curvature spread
    }
    // Calm water still focuses a little; wind sharpens it.
    relief *= 0.7 + 0.6 * clamp(vv_sceneWind, 0.0, 1.0);

    // The lens reaches its focus around VV_WATER_CAUSTIC_FOCUS blocks down and
    // holds there, softening, instead of folding into noise deeper still.
    float lensDepth = VV_WATER_CAUSTIC_FOCUS * (1.0 - exp(-depth / (0.45 * VV_WATER_CAUSTIC_FOCUS)));
    float f = lensDepth * (1.0 - VV_WATER_ETA) * relief;
    float jxx = 1.0 - f * h.x;
    float jzz = 1.0 - f * h.z;
    float jxz = -f * h.y;
    float det = jxx * jzz - jxz * jxz;
    float soft = VV_WATER_CAUSTIC_SHARP + VV_WATER_CAUSTIC_SPREAD * depth;
    float gain = max(0.0, sqrt(1.0 + soft * soft) / sqrt(det * det + soft * soft) - 1.0);
    return gain * VV_WATER_CAUSTIC_PEAK / (VV_WATER_CAUSTIC_PEAK + gain);
}

// The normal of one water texel: the face, tilted by the waves (top faces at
// full height, falling water a little) and by the rain ripples (top faces in
// the open only).
//
// look: xy the flow direction (unit, or zero), z how much it flows (0..1),
// w how far the texel is in swell terms (0 near, 1 far).
vec3 vvWaterTexelNormal(vec3 faceNormal, vec3 world, vec2 flowOffset, vec4 look, float rippleAmount, out float ring)
{
    ring = 0.0;
    float top = clamp(faceNormal.y, 0.0, 1.0);
    vec2 slope = vvWaterWaveSlope(world.xz - flowOffset, waterWaveCounter, look.z);
    if (look.w > 0.001) slope += vvWaterSwellSlope(world.xz - flowOffset, waterWaveCounter) * look.w;
    slope = vvWaterStreak(slope, look.xy, look.z)
          * clamp(vv_waterWaves, 0.0, 2.0) * mix(0.25, 1.0, top);

    if (rippleAmount > 0.001)
    {
        vec2 rain = vvWaterRainRipples(world.xz, vv_waterRippleTime, clamp(vv_waterRipples, 0.0, 1.0))
                  * (VV_WATER_RIPPLE_DEPTH * rippleAmount);
        slope += rain;
        ring = clamp(length(rain), 0.0, 1.0);
    }

    vec3 n = faceNormal + vec3(slope.x, 0.0, slope.y) * top
           + vec3(0.0, slope.x + slope.y, 0.0) * (1.0 - top) * 0.5;
    return normalize(n);
}

// Which way is a texel step along the face: XZ on a top face, the horizontal
// and Y on a side face.
void vvWaterFaceAxes(vec3 faceNormal, out vec3 a, out vec3 b)
{
    if (abs(faceNormal.y) > 0.5) { a = vec3(1.0, 0.0, 0.0); b = vec3(0.0, 0.0, 1.0); }
    else if (abs(faceNormal.x) > 0.5) { a = vec3(0.0, 0.0, 1.0); b = vec3(0.0, 1.0, 0.0); }
    else { a = vec3(1.0, 0.0, 0.0); b = vec3(0.0, 1.0, 0.0); }
}

// The analytic sky as one texel sees it along r.
vec3 vvWaterSkyImage(vec3 r, vec3 environment)
{
    vec3 cell = vvWaterReflectCell(r, VV_WATER_REFLECT_CELLS);
    vec3 sky = vvWaterReflectionFallback(cell, environment);

    // Never brighter than the sky itself: the terrain fallback's horizon and
    // toward-sun lift suit a rough surface, and on a mirror they are what
    // turned a pond white.
    float skyLuma = dot(sky, vec3(0.2126, 0.7152, 0.0722));
    float cap = dot(environment, vec3(0.2126, 0.7152, 0.0722));
    if (skyLuma > cap && skyLuma > 1e-5) sky *= cap / skyLuma;

    float open = smoothstep(VV_WATER_BANK_LOW, VV_WATER_BANK_HIGH, cell.y);
    return mix(environment * VV_WATER_BANK_SHADE, sky, open);
}

float vvWaterLuma(vec3 c)
{
    return dot(c, vec3(0.2126, 0.7152, 0.0722));
}

// Vanilla's own sun highlight, kept smaller as the glints take over so the
// sun is not drawn twice. 1 (vanilla) whenever this group is not live.
float vvWaterVanillaSpecularKeep()
{
    if (vv_water < 0.5) return 1.0;
    return mix(1.0, VV_WATER_SPEC_KEEP, clamp(vv_waterGlints, 0.0, 1.0));
}

// Vanilla's droplet rings on water (dropletnoise) sit on a fixed grid and
// replay in the same cells forever, beside rings that land somewhere new: two
// rain systems that disagree on one surface. While this mod's rings are on they
// replace vanilla's, so water and land show the same drops (D75). 1 (vanilla)
// whenever this group is not live or the ring slider is 0.
// Vanilla's own depth-edge foam steps back while the foam line is on, so the
// shore is not foamed twice. 1 (vanilla) whenever this group is not live or
// the shoreline slider is 0.
float vvWaterVanillaFoamKeep()
{
    if (vv_water < 0.5) return 1.0;
    return mix(1.0, VV_WATER_SHORE_KEEP, clamp(vv_waterShore, 0.0, 1.0));
}

float vvWaterVanillaDropletKeep()
{
    if (vv_water < 0.5) return 1.0;
    return 1.0 - clamp(vv_waterOwnDrops, 0.0, 1.0);
}

// One directional glint (GGX, the same lobe terrain uses), for one light.
vec3 vvWaterGlint(vec3 n, vec3 v, vec3 l, vec3 lightColor)
{
    float ndotl = dot(n, l);
    float ndotv = dot(n, v);
    if (ndotl <= 0.0 || ndotv <= 0.0) return vec3(0.0);
    vec3 h = normalize(l + v);
    float d = vvDistributionGGX(max(dot(n, h), 0.0), VV_WATER_ROUGHNESS);
    float g = vvGeometrySmith(ndotv, ndotl, VV_WATER_ROUGHNESS);
    vec3 f = vvFresnelSchlick(max(dot(v, h), 0.0), vec3(VV_WATER_F0));
    return lightColor * (d * g * f * ndotl / max(1e-4, 4.0 * ndotv * ndotl));
}

// Everything above, on the finished vanilla water colour. Lava never comes here.
//
// The OIT pass blends this colour over the world behind by its alpha, so the
// water is composed the way light actually splits at a surface: a share F
// (Fresnel) is reflected and arrives as the reflection colour; the rest
// passes into the water, where the water body (vanilla's colour, deepened by
// the path length behind) is what it meets. Out alpha is F plus the body's
// opacity over the remaining (1 - F).
vec4 vvWaterSurface(vec4 color, vec3 pos, vec3 faceNormal, float shadowBright,
                    int skyOpen, vec2 flowOffset, vec2 flow, vec3 environment)
{
    // Derivatives FIRST, in uniform control flow: everything below branches.
    vec3 dpx = dFdx(pos);
    vec3 dpy = dFdy(pos);
    float ownLinear = linearDepth(gl_FragCoord.z);
    float dlx = dFdx(ownLinear);
    float dly = dFdy(ownLinear);
    // The bed behind this pixel, and how it slopes, for the caustics: light
    // falls on a face by N.L, so a wall or a slope under the water takes less.
    float behindPixel = linearDepth(texture(depthTex, gl_FragCoord.xy / frameSize).x);
    vec3 bedPixel = vv_waterEye + (pos - vv_waterEye) * (behindPixel / max(ownLinear, 1e-6));
    vec3 bedDx = dFdx(bedPixel);
    vec3 bedDy = dFdy(bedPixel);

    if (vv_water < 0.5) return color;

    // Seen from below (camera underwater, looking up): leave vanilla's.
    // fWorldPos is measured from the PLAYER, not the eye (the camera offset
    // lives in the view matrix), so the view vector starts at vv_waterEye.
    // From the feet, water at standing height is seen exactly edge-on and
    // reflects the whole sky (2026-11-25). 0 is the previous build.
    vec3 vFrag = normalize(vv_waterEye - pos);
    if (dot(vFrag, faceNormal) <= 0.0) return color;

    // --- the texel ---------------------------------------------------------
    vec3 world = pos + vv_waterOrigin;
    vec3 axisMask = step(vec3(0.5), abs(faceNormal));
    vec3 snapped = (floor(world * VV_WATER_TEXELS) + 0.5) / VV_WATER_TEXELS;
    vec3 centreWorld = mix(snapped, world, axisMask);
    vec3 centre = centreWorld - vv_waterOrigin;
    vec3 delta = centre - pos;

    // Screen offset to the texel centre, least squares on the two screen
    // derivatives (the surface is a plane within a pixel). Singular when the
    // face is seen edge-on, where a texel is sub-pixel anyway.
    vec2 screenStep = vec2(0.0);
    float a11 = dot(dpx, dpx), a12 = dot(dpx, dpy), a22 = dot(dpy, dpy);
    float det = a11 * a22 - a12 * a12;
    if (abs(det) > 1e-14)
    {
        float b1 = dot(dpx, delta), b2 = dot(dpy, delta);
        screenStep = vec2(a22 * b1 - a12 * b2, a11 * b2 - a12 * b1) / det;
        screenStep = clamp(screenStep, vec2(-64.0), vec2(64.0));
    }

    vec3 v = normalize(vv_waterEye - centre);
    float top = clamp(faceNormal.y, 0.0, 1.0);
    float skyFactor = skyOpen > 0 ? 1.0 : VV_WATER_SKY_CAVE;
    float rippleAmount = skyOpen > 0 ? clamp(vv_waterRipples, 0.0, 1.0) * top : 0.0;

    // Rivers and far water (D79): vanilla's flow vector, and the distance.
    float flowLen = length(flow);
    float flowAmount = smoothstep(0.0, VV_WATER_FLOW_FULL, flowLen);
    vec2 flowDir = flowLen > 1e-4 ? flow / flowLen : vec2(0.0);
    float far = smoothstep(VV_WATER_SWELL_NEAR, VV_WATER_SWELL_FAR, length(vv_waterEye - centre));
    vec4 look = vec4(flowDir, flowAmount, far);

    float ring;
    vec3 n = vvWaterTexelNormal(faceNormal, centreWorld, flowOffset, look, rippleAmount, ring);

    // --- 1. sky and horizon reflection, texel-locked ------------------------
    vec3 r = reflect(-v, n);
    if (r.y < 0.0 && top > 0.5) r = normalize(vec3(r.x, -r.y * 0.25, r.z));
    vec3 image = vvWaterSkyImage(r, environment);

    int taps = int(clamp(vv_waterSoftTaps, 1.0, 5.0) + 0.5);
    float soft = clamp(vv_waterSoft, 0.0, 1.0);
    if (soft > 0.001 && taps > 1)
    {
        vec3 ax, bx;
        vvWaterFaceAxes(faceNormal, ax, bx);
        float step1 = VV_WATER_SOFT_RADIUS / VV_WATER_TEXELS;
        vec3 sum = image;
        float count = 1.0;
        for (int i = 1; i < 5; i++)
        {
            if (i >= taps) break;
            vec3 dir = (i == 1) ? ax : (i == 2) ? -ax : (i == 3) ? bx : -bx;
            float ringTap;
            vec3 nb = vvWaterTexelNormal(faceNormal, centreWorld + dir * step1, flowOffset, look, rippleAmount, ringTap);
            vec3 rb = reflect(-v, nb);
            if (rb.y < 0.0 && top > 0.5) rb = normalize(vec3(rb.x, -rb.y * 0.25, rb.z));
            sum += vvWaterSkyImage(rb, environment);
            count += 1.0;
        }
        image = mix(image, sum / count, soft);
    }
    image *= skyFactor;

    // --- 1b. the world in the water (D82) ---------------------------------
    // The texel's own reflected ray, marched across the opaque frame: banks,
    // trees and the far shore replace the sky where the ray finds them, and
    // the sky (with its bank stand-in) stays where it does not.
    // Not traced where the reflection could not show anyway (a share below
    // one part in 256). Under a roof it still runs: what it finds is real
    // content, not sky, so it is mixed in AFTER skyFactor dims the sky.
    float ndotv = clamp(dot(n, v), 0.0, 1.0);
    float fresnel = VV_WATER_F0 + (1.0 - VV_WATER_F0) * pow(1.0 - ndotv, 5.0);
    float reflectShare = clamp(fresnel * clamp(vv_waterSky, 0.0, 1.0), 0.0, 1.0);

    vec4 sceneHit = vec4(0.0);
#ifdef VV_WATER_SCENE
    // The traced ray follows only part of the wave tilt. The sky is smooth, so
    // a fully tilted ray only moves its gradient; a bank or a tree is a few
    // degrees tall, and the full tilt scatters each texel's ray clean off it,
    // leaving a reflection of noise. Still one ray per texel, from its own
    // normal, so it holds still and wobbles with the waves.
    if (top > 0.5 && reflectShare > 0.004)
    {
        vec3 calm = normalize(mix(faceNormal, n, VV_WSCENE_WAVE));
        vec3 rs = reflect(-v, calm);
        if (rs.y > 0.0) sceneHit = vvWaterSceneTrace(centre, rs, v);
    }
    image = mix(image, sceneHit.rgb, sceneHit.a * clamp(vv_waterSceneMix, 0.0, 1.0));
#endif

    // A ring's crest is steep: it throws sky light back over its whole circle,
    // not only where Fresnel is already high, so a drop reads looking down into
    // the water as well as across it, the way vanilla's droplets brighten the
    // water they hit. Scaled by the ripple strength alone (it is in ring), so
    // the reflection slider at 0 does not take the rain with it.
    reflectShare = max(reflectShare, VV_WATER_RING_SHARE * ring);

    // The sun has to be up and its disc visible for the glints and the
    // caustics alike: the game's own cloud tiles (sky cover) hide it, as well
    // as the player's overcast softening. In game a grey sky still threw a
    // white sun path (2026-11-25).
    float sunUp = smoothstep(-0.02, 0.08, vv_waterSunDir.y) * clamp(vv_sceneDayLight * 1.5, 0.0, 1.0);
    float clearSky = (1.0 - 0.85 * clamp(vv_sceneOvercast, 0.0, 1.0))
                   * (1.0 - smoothstep(VV_WATER_GLINT_COVER_START, VV_WATER_GLINT_COVER_END,
                                       clamp(vv_sceneSkyCover, 0.0, 1.0)));

    // --- 2. depth colouring: the path behind this texel ----------------------
    vec3 body = color.rgb;
    float bodyAlpha = clamp(color.a, 0.0, 1.0);
    float path = 0.0;
    float depthTint = clamp(vv_waterDepthTint, 0.0, 1.0);
    float caustics = clamp(vv_waterCaustics, 0.0, 1.0);
    vec3 causticLight = vec3(0.0);
    vec3 bedNormal = vec3(0.0, 1.0, 0.0);
    vec3 bedPoint = centre;
    float shoreGap = VV_WATER_DEEP_PATH;
    float shore = clamp(vv_waterShore, 0.0, 1.0);
    if (depthTint > 0.001 || vv_waterClarity > 0.001 || caustics > 0.001 || shore > 0.001 || VV_WATER_REFRACTS || vv_waterDebug > 0.5)
    {
        vec2 uv = (gl_FragCoord.xy + screenStep) / frameSize;
        float behind = linearDepth(texture(depthTex, clamp(uv, vec2(0.0), vec2(1.0))).x);
        float own = ownLinear + dlx * screenStep.x + dly * screenStep.y;
        path = clamp((behind - own) * zFar, 0.0, VV_WATER_DEEP_PATH);

        vec3 transmit = exp(-VV_WATER_ABSORB * path * max(depthTint, 0.05) * 2.0);
        float absorbed = 1.0 - dot(transmit, vec3(0.3333));

        // The light reaching the body: the vertex light vanilla lit the
        // water with (rgba), the shadow, and the sky.
        vec3 light = rgba.rgb * mix(0.55, 1.0, shadowBright);
        vec3 deep = VV_WATER_SCATTER * light * 3.0 + environment * 0.08 * skyFactor;

        body = mix(body, deep, absorbed * depthTint);
        float clear = clamp(vv_waterClarity, 0.0, 1.0) * (1.0 - smoothstep(0.25, 2.0, path));
        bodyAlpha = mix(bodyAlpha * (1.0 - 0.55 * clear), max(bodyAlpha, 0.92), absorbed * depthTint);

        // The bed behind this texel. Points on one view ray scale with view
        // depth, so it is the texel centre pushed along the ray by behind /
        // own. Its normal comes from the bed's screen slope behind each pixel;
        // a block edge between two pixels is a jump, not a slope, and there
        // the bed counts as flat rather than drawing a seam along every block.
        vec3 bed = vv_waterEye + (centre - vv_waterEye) * (behind / max(own, 1e-6));
        bedPoint = bed;
        vec3 bedCross = cross(bedDx, bedDy);
        float bedReach = 9.0 * (behindPixel / max(ownLinear, 1e-6)) * (behindPixel / max(ownLinear, 1e-6));
        bool jump = dot(bedDx, bedDx) > bedReach * a11 || dot(bedDy, bedDy) > bedReach * a22;
        bedNormal = (!jump && dot(bedCross, bedCross) > 1e-12) ? normalize(bedCross) : vec3(0.0, 1.0, 0.0);
        if (dot(bedNormal, vv_waterEye - bed) < 0.0) bedNormal = -bedNormal;
        // How far the texel is from what lies behind it, measured across that
        // surface: the depth over a floor, the distance out from a bank wall.
        // Unlike the view path it does not change with the angle of view.
        shoreGap = abs(dot(centre - bed, bedNormal));

        // --- 5. caustics on the bed seen through this texel ----------------
        // Top faces in the open, under a sun that is up and visible, only.
        float sunLight = sunUp * clearSky * shadowBright;
        if (caustics > 0.001 && skyOpen > 0 && top > 0.5 && sunLight > 0.001 && own > 1e-6)
        {
            float depth = max(centre.y - bed.y, 0.0);
            // Where the sunlight that reaches this bed point entered the
            // water: back up the refracted sun ray to the surface.
            vec3 down = refract(-vv_waterSunDir, vec3(0.0, 1.0, 0.0), VV_WATER_ETA);
            float sunPath = depth / max(-down.y, 0.2);
            vec2 bedTexel = (floor((bed.xz + vv_waterOrigin.xz) * VV_WATER_TEXELS) + 0.5) / VV_WATER_TEXELS;
            vec2 entry = bedTexel - down.xz * sunPath - flowOffset;
            float gain = vvWaterCausticGain(entry, waterWaveCounter, depth, vv_waterCausticWaves);
            // The game lights the bed as if no water were above it, and the
            // view path's colour is already the water body's job, so the light
            // on its way down only loses a little red: enough to keep the lines
            // the colour of sunlight in water, not of a second tint.
            vec3 downTransmit = exp(-VV_WATER_ABSORB * sunPath * depthTint * 0.5);
            float fade = 1.0 - smoothstep(VV_WATER_CAUSTIC_DEEP * 0.3, VV_WATER_CAUSTIC_DEEP, depth);
            // Seen through the water: the longer the view path, the less of
            // the bed's light comes back, even where the water is kept clear.
            fade *= dot(exp(-VV_WATER_ABSORB * path * 2.0), vec3(0.3333));
            // The face the light lands on.
            float facing = clamp(dot(bedNormal, -down), 0.0, 1.0);
            fade *= facing;
            // A line is a few bed texels wide; once a texel is smaller than a
            // pixel the web only shimmers, so it gives way with distance.
            float footprint = sqrt(max(a11, a22)) * (behind / own);
            fade *= 1.0 - smoothstep(VV_WATER_CAUSTIC_FAR * 0.5, VV_WATER_CAUSTIC_FAR, footprint);
            causticLight = vv_waterSunColor * downTransmit * (gain * fade * sunLight * caustics);
        }
    }

    // --- 3./4. glints from the sun and the moon -----------------------------
    vec3 glint = vec3(0.0);
    float glints = clamp(vv_waterGlints, 0.0, 1.0);
    if (glints > 0.001 && skyOpen > 0)
    {
        float lit = mix(0.08, 1.0, shadowBright * shadowBright);
        glint += vvWaterGlint(n, v, vv_waterSunDir, vv_waterSunColor) * sunUp * clearSky * lit;
        glint += vvWaterGlint(n, v, vv_waterMoonDir, vec3(0.55, 0.62, 0.80))
               * clamp(vv_sceneMoonLight, 0.0, 1.0) * clearSky * 0.6;
        // Glitter, not a blob: the texel normals vary too little for a GGX
        // lobe to break up on its own, so each texel carries a facet that
        // twinkles with the waves - some catch the sun hard, the rest softly.
        vec2 facetCell = floor(centreWorld.xz * VV_WATER_TEXELS);
        float twinkle = mod(floor(waterWaveCounter * VV_WATER_GLINT_TWINKLE * (1.0 + 2.0 * flowAmount)), 4096.0);
        float facet = vvWaterCellRandom(facetCell + vec2(twinkle * 7.0, twinkle * 3.0)).x;
        glint *= glints * (facet > VV_WATER_GLINT_FACETS ? VV_WATER_GLINT_HARD : VV_WATER_GLINT_SOFT);

        // A shoulder instead of a clip: bright glints approach the peak
        // rather than flattening into one white shape.
        float gl = vvWaterLuma(glint);
        glint *= VV_WATER_GLINT_PEAK / (VV_WATER_GLINT_PEAK + gl);
    }

    // --- compose ------------------------------------------------------------
    float outAlpha = reflectShare + bodyAlpha * (1.0 - reflectShare);
    vec3 premult = image * reflectShare + body * bodyAlpha * (1.0 - reflectShare) + glint;
    float glintAlpha = clamp(vvWaterLuma(glint), 0.0, 1.0);
    outAlpha = clamp(outAlpha + min(glintAlpha, VV_WATER_GLINT_ALPHA) * (1.0 - outAlpha), 0.0, 1.0);
    // Caustics are light on the BED, seen through what the water lets by:
    // they add to the colour and leave the opacity alone, so they can never
    // hide what is beneath. Zero is exactly the composition above.
    premult += causticLight * (VV_WATER_CAUSTIC_ALBEDO * (1.0 - outAlpha));

    // --- 6. shoreline (D79) ---------------------------------------------------
    // How far the texel is from the bank or bed behind it (shoreGap). A water
    // block stands most of a block above its floor, so in practice the line
    // runs where the surface meets a bank wall or a raised bed. Top faces in
    // clear view only; far away the depth buffer cannot tell a sliver from a step.
    float foam = 0.0;
    if (shore > 0.001 && top > 0.5)
    {
        float shoreDepth = shoreGap;
        float shoreFade = 1.0 - smoothstep(VV_WATER_SHORE_FAR * 0.5, VV_WATER_SHORE_FAR, sqrt(max(a11, a22)));
        // The edge thins: what the water lays over the bed fades toward the
        // waterline, colour and opacity together.
        float edge = mix(1.0, smoothstep(0.0, VV_WATER_SHORE_SOFT, shoreDepth), shore * shoreFade);
        premult *= edge;
        outAlpha *= edge;

        // The lap: reach swings with the waves, its phase wandering along the
        // shore on 2-block value noise; each texel foams while the lap covers
        // its own threshold, so the line advances and breaks up per texel.
        float phase = vvWaterValueNoise(centreWorld.xz * 0.5, 2048.0).x * VV_WATER_TWO_PI;
        float lap = 0.5 + 0.5 * sin(waterWaveCounter * 0.9 + phase);
        float reach = VV_WATER_SHORE_FOAM * (0.45 + 0.55 * lap) * (0.8 + 0.4 * clamp(vv_sceneWind, 0.0, 1.0));
        float threshold = vvWaterCellRandom(floor(centreWorld.xz * VV_WATER_TEXELS) + vec2(5003.0, 7019.0)).x;
        float cover = 1.0 - shoreDepth / max(reach, 1e-4);
        foam = (cover > threshold * 0.85 + 0.1 ? 1.0 : 0.0) * shore * shoreFade * VV_WATER_SHORE_ALPHA;
        vec3 foamColor = rgba.rgb * mix(0.55, 1.0, shadowBright) * VV_WATER_SHORE_ALBEDO;
        premult = foamColor * foam + premult * (1.0 - foam);
        outAlpha = foam + outAlpha * (1.0 - foam);
    }
#ifdef VV_WATER_SCENE
    // --- 7. the bed bent through the waves (D82) --------------------------
    // The water shows its own copy of what lies behind, sampled where the wave
    // slope bends the view, and covers the straight view by the same share:
    // what passes is (1 - outAlpha), split between the bent copy (k) and the
    // straight one (1 - k). A sample that would land in front of the water
    // keeps the straight view.
    float refraction = clamp(vv_waterRefraction, 0.0, 1.0);
    if (refraction > 0.001 && top > 0.5 && vv_waterSceneOn > 0.5 && path > 0.0)
    {
        vec4 bentBed = vvWaterSceneRefract(centre, bedPoint, n, path);
        float k = refraction * bentBed.a;
        premult += bentBed.rgb * ((1.0 - outAlpha) * k);
        outAlpha = mix(outAlpha, 1.0, k);
    }
#endif

    vec4 result = vec4(premult / max(outAlpha, 1e-3), outAlpha);

    // --- debug views (Water.DebugView) -------------------------------------
    int mode = int(vv_waterDebug + 0.5);
    if (mode == 1) return vec4(n * 0.5 + 0.5, 1.0);                       // texel normal
    if (mode == 2) return vec4(image, 1.0);                                 // reflected sky image
    if (mode == 3) return vec4(vec3(path / 8.0), 1.0);                      // path behind, 8 blocks = white
    if (mode == 4) return vec4(vec3(reflectShare * 4.0), 1.0);              // reflected share, x4
    if (mode == 5) return vec4(glint, 1.0);                                 // glints alone
    if (mode == 6) return vec4(fract(centreWorld.xz * VV_WATER_TEXELS / 2.0) * 0.8 + 0.1, top, 1.0); // texel grid
    if (mode == 7) return vec4(causticLight, 1.0);                          // caustic light reaching the bed
    if (mode == 8) return vec4(foam, flowAmount, far, 1.0);                 // foam line, flow, far swell
    if (mode == 9) return vec4(sceneHit.rgb * sceneHit.a, 1.0);             // the world found by the reflected ray
    return result;
}
