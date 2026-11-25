// Vintage Visuals - the ONE description of a reflection (D81).
//
// Terrain and water used to carry their own copies of the analytic sky and
// the direction cells, held equal by parity checks, and the screen march lived
// in the terrain group alone. This file is what a reflection IS, shared by
// every program that draws one:
//
//   the texel ray      vvReflectCells, vvReflectCell: one direction per
//                      texture pixel, stepped by roughness, no noise - Vintage
//                      Story has no temporal filter to hide stochastic jitter,
//                      and its look is crisp texels
//   how much shows     vvReflectEnvBrdf (the split-sum fit of the reflected
//                      share) and vvReflectMirror (where an environment
//                      reflection is a picture rather than a sheen)
//   the screen march   vvReflectMarch, over an image the CALLER chooses: the
//                      terrain's last-frame capture (linear depth in alpha) or
//                      the water pass's live opaque frame (window depth)
//   the sky            vvReflectSky, the analytic fallback for a ray nothing
//                      on screen answered
//
// It declares no sampler and no uniform. A sampler reaches it only as a
// function parameter, so injecting it can never shift a program's texture
// units (see CLAUDE.md on samplers). Each patch group injects its own copy, as
// with pbrcore.glsl, so each can still roll back to vanilla alone.
//
// Plain 7-bit ASCII only, comments included.

struct VvReflectHit
{
    vec3 color;     // what the ray found, as the image stores it
    float valid;    // 0..1 confidence; 0 is a miss
    float reason;   // VV_REFLECT_* below, for the march debug views
    float travel;   // 0..1 along the marched segment where it landed
};

#define VV_REFLECT_NO_SOURCE   0.0
#define VV_REFLECT_FACING      1.0
#define VV_REFLECT_ORIGIN_OFF  2.0
#define VV_REFLECT_FAR_OFF     3.0
#define VV_REFLECT_NO_CROSSING 4.0
#define VV_REFLECT_TOO_THICK   5.0
#define VV_REFLECT_HIT         6.0
#define VV_REFLECT_SKIPPED     7.0

// ---------------------------------------------------------------------------
// The texel ray
// ---------------------------------------------------------------------------

const float VV_REFLECT_CELLS_SHARP = 16.0;
const float VV_REFLECT_CELLS_ROUGH = 3.0;
const float VV_REFLECT_TWO_PI = 6.28318530718;

float vvReflectCells(float roughness)
{
    return max(2.0, mix(VV_REFLECT_CELLS_SHARP, VV_REFLECT_CELLS_ROUGH, clamp(roughness, 0.0, 1.0)));
}

// The reflection direction snapped to its cell, so the analytic image steps
// in what the texel is looking at.
vec3 vvReflectCell(vec3 r, float cells)
{
    float elevation = (floor((clamp(r.y, -1.0, 1.0) * 0.5 + 0.5) * cells) + 0.5) / cells;
    float azimuth = (floor((atan(r.z, r.x) / VV_REFLECT_TWO_PI + 0.5) * cells * 2.0) + 0.5)
                  / (cells * 2.0);
    float y = elevation * 2.0 - 1.0;
    float theta = (azimuth - 0.5) * VV_REFLECT_TWO_PI;
    float radius = sqrt(max(0.0, 1.0 - y * y));
    return vec3(cos(theta) * radius, y, sin(theta) * radius);
}

// ---------------------------------------------------------------------------
// How much shows
// ---------------------------------------------------------------------------

// The reflected share of the environment, Karis's analytic fit of the
// split-sum table: F0 x A + B, by roughness and view angle. Head-on it is F0
// (4% on stone, 2% on water); toward a grazing view it rises toward 1 on a
// smooth surface and much less on a rough one, which spreads its reflection
// over the whole sky instead of mirroring one direction.
vec3 vvReflectEnvBrdf(vec3 f0, float roughness, float ndotv)
{
    // Scalar form of Karis's c0 = (-1, -0.0275, -0.572, 0.022),
    // c1 = (1, 0.0425, 1.04, -0.04), kept in floats so the smoke test can
    // evaluate the shipped arithmetic. exp(-6.4324 x) is exp2(-9.28 x).
    float r = clamp(roughness, 0.0, 1.0);
    float nv = clamp(ndotv, 0.0, 1.0);
    float rx = 1.0 - r;
    float ry = 0.0425 - 0.0275 * r;
    float rz = 1.04 - 0.572 * r;
    float rw = 0.022 * r - 0.04;
    float a004 = min(rx * rx, exp(-6.4324 * nv)) * rx + ry;
    float scale = rz - 1.04 * a004;
    float bias = rw + 1.04 * a004;
    return clamp(f0 * scale + bias, vec3(0.0), vec3(1.0));
}

// Where an environment reflection reads as a PICTURE. A rough surface still
// reflects (the fit above says how much), but what it reflects is the whole
// sky averaged - and vanilla's ambient light already paints that into the
// diffuse. Adding it again as a sky-coloured sheen is the milky look. So the
// picture fades out over the rough half of the range, and a mirror keeps all
// of it.
const float VV_REFLECT_MIRROR_FULL = 0.30;   // at or below: a full picture
const float VV_REFLECT_MIRROR_NONE = 0.80;   // at or above: nothing extra

float vvReflectMirror(float roughness)
{
    return 1.0 - smoothstep(VV_REFLECT_MIRROR_FULL, VV_REFLECT_MIRROR_NONE, clamp(roughness, 0.0, 1.0));
}

// ---------------------------------------------------------------------------
// The sky: the analytic environment as a COLOUR - sky above, a lifted horizon
// band, dim ground below, the sun's half of the sky a little brighter. No sun
// disc: the direct lobe already draws the sun, and a disc here would count it
// twice. The caller passes its own sun direction and daylight, because the
// programs that carry this file do not all declare the same uniforms.
// ---------------------------------------------------------------------------

const float VV_REFLECT_HORIZON = 0.14;      // half-width of the horizon band in r.y
const float VV_REFLECT_GROUND = 0.34;       // dim fallback below the horizon
const float VV_REFLECT_HORIZON_LIFT = 1.15; // the horizon is the brightest band
const float VV_REFLECT_TOWARD = 0.30;       // the sun's half of the sky, brighter
const float VV_REFLECT_SKY_MAX = 2.0;       // no sky cell brighter than this x environment

vec3 vvReflectSky(vec3 direction, vec3 environment, vec3 sunDir, float dayLight)
{
    float lift = direction.y > VV_REFLECT_HORIZON
        ? 1.0
        : (direction.y < -VV_REFLECT_HORIZON ? VV_REFLECT_GROUND : VV_REFLECT_HORIZON_LIFT);

    vec2 sunAzimuth = sunDir.xz;
    if (dot(sunAzimuth, sunAzimuth) > 1e-6 && dot(direction.xz, direction.xz) > 1e-6)
    {
        float toward = dot(normalize(direction.xz), normalize(sunAzimuth));
        lift *= 1.0 + VV_REFLECT_TOWARD * toward * clamp(dayLight, 0.0, 1.0);
    }
    return environment * clamp(lift, 0.0, VV_REFLECT_SKY_MAX);
}

// ---------------------------------------------------------------------------
// The screen march (D62 on terrain, generalised in D81).
//
// The caller projects the ray's two ends into the image it passes:
// a, b = (u, v, axial linear depth, on-screen 1/0). Every fix carried from the
// legacy march is kept: the march steps uniformly in the IMAGE (fixed world
// steps smeared a trunk down to the viewer's feet), depth is axial so ray and
// image agree (radial against axial drew rings), 1/w is what interpolates
// along the screen, a crossing is bisected before the thickness test, and hits
// fade at the frame edge, the thickness limit and toward the camera. Every
// read is textureLod: this runs in a loop with a data-dependent exit, where
// implicit derivatives are undefined.
//
// depthMode 0: linear depth lives in image.a (the terrain's capture).
// depthMode 1: depthTex holds window depth, linearised with nearFar.
//
// blur (image texels) averages four taps around the hit, so a rough surface
// shows a soft picture and a polished one a sharp one. Deterministic, no noise.
// ---------------------------------------------------------------------------

const int VV_REFLECT_MAX_STEPS = 64;        // loop bound; the caller's budget is below it
const float VV_REFLECT_STRIDE = 2.0;        // image texels per step, when the budget allows
const int VV_REFLECT_REFINE = 5;            // bisection passes on a crossing
const float VV_REFLECT_FACING_FADE = 0.25;  // rays pointing back at the camera fade out
const float VV_REFLECT_START_TEXELS = 2.0;  // start past the texel's own depth
const float VV_REFLECT_EDGE_FADE = 0.04;    // fade hits at the image's edge

float vvReflectSceneDepth(sampler2D image, sampler2D depthTex, float depthMode, vec2 nearFar, vec2 uv)
{
    if (depthMode < 0.5) return textureLod(image, uv, 0.0).a;
    float z = textureLod(depthTex, uv, 0.0).r * 2.0 - 1.0;
    float n = max(nearFar.x, 1e-4);
    float f = max(nearFar.y, n + 1e-3);
    return 2.0 * n * f / max(f + n - z * (f - n), 1e-6);
}

vec3 vvReflectSceneColor(sampler2D image, vec2 uv, float blur, vec2 imageSize)
{
    if (blur < 0.5) return textureLod(image, uv, 0.0).rgb;
    vec2 o = vec2(blur * 0.5) / imageSize;
    vec3 c = textureLod(image, clamp(uv + vec2(-o.x, -o.y), 0.0, 1.0), 0.0).rgb
           + textureLod(image, clamp(uv + vec2( o.x, -o.y), 0.0, 1.0), 0.0).rgb
           + textureLod(image, clamp(uv + vec2(-o.x,  o.y), 0.0, 1.0), 0.0).rgb
           + textureLod(image, clamp(uv + vec2( o.x,  o.y), 0.0, 1.0), 0.0).rgb;
    return c * 0.25;
}

VvReflectHit vvReflectMarch(sampler2D image, sampler2D depthTex, float depthMode, vec2 nearFar,
                            vec4 a, vec4 b, float facing, int maxSteps, float thickness, float blur)
{
    VvReflectHit miss;
    miss.color = vec3(0.0);
    miss.valid = 0.0;
    miss.travel = 0.0;

    miss.reason = VV_REFLECT_FACING;
    if (facing > 1.0 - VV_REFLECT_FACING_FADE) return miss;

    miss.reason = VV_REFLECT_ORIGIN_OFF;
    if (a.w < 0.5 || any(greaterThan(abs(a.xy - 0.5), vec2(0.5)))) return miss;

    miss.reason = VV_REFLECT_FAR_OFF;
    if (b.w < 0.5) return miss;

    vec2 imageSize = max(vec2(1.0), vec2(textureSize(image, 0)));
    vec2 travel = abs(b.xy - a.xy) * imageSize;
    float pixels = max(1.0, max(travel.x, travel.y));
    int budget = min(maxSteps, VV_REFLECT_MAX_STEPS);
    int steps = int(clamp(pixels / VV_REFLECT_STRIDE, 4.0, float(max(budget, 4))));

    float invA = 1.0 / max(1e-4, a.z);
    float invB = 1.0 / max(1e-4, b.z);

    float startF = clamp(VV_REFLECT_START_TEXELS / pixels, 0.0, 0.25);
    float previousF = startF;
    float previousDelta = 1.0 / mix(invA, invB, startF)
                        - vvReflectSceneDepth(image, depthTex, depthMode, nearFar, mix(a.xy, b.xy, startF));

    for (int i = 1; i <= VV_REFLECT_MAX_STEPS; i++)
    {
        if (i > steps) break;

        float f = mix(startF, 1.0, float(i) / float(steps));
        vec2 uv = mix(a.xy, b.xy, f);
        if (any(greaterThan(abs(uv - 0.5), vec2(0.5)))) break;

        miss.reason = VV_REFLECT_NO_CROSSING;
        float delta = 1.0 / mix(invA, invB, f)
                    - vvReflectSceneDepth(image, depthTex, depthMode, nearFar, uv);

        if (previousDelta < 0.0 && delta >= 0.0)
        {
            float lo = previousF;
            float hi = f;
            for (int k = 0; k < VV_REFLECT_REFINE; k++)
            {
                float mid = (lo + hi) * 0.5;
                float midDelta = 1.0 / mix(invA, invB, mid)
                               - vvReflectSceneDepth(image, depthTex, depthMode, nearFar, mix(a.xy, b.xy, mid));
                if (midDelta < 0.0) lo = mid; else hi = mid;
            }

            vec2 hitUv = mix(a.xy, b.xy, hi);
            float thick = 1.0 / mix(invA, invB, hi)
                        - vvReflectSceneDepth(image, depthTex, depthMode, nearFar, hitUv);

            if (thick < thickness)
            {
                VvReflectHit hit;
                hit.color = vvReflectSceneColor(image, hitUv, blur, imageSize);
                hit.reason = VV_REFLECT_HIT;
                hit.travel = hi;
                vec2 edge = min(hitUv, 1.0 - hitUv);
                hit.valid = (1.0 - smoothstep(1.0 - VV_REFLECT_FACING_FADE * 2.0,
                                              1.0 - VV_REFLECT_FACING_FADE, facing))
                          * smoothstep(0.0, VV_REFLECT_EDGE_FADE, min(edge.x, edge.y))
                          * (1.0 - smoothstep(thickness * 0.65, thickness, max(0.0, thick)));
                return hit;
            }

            miss.reason = VV_REFLECT_TOO_THICK;
            return miss;
        }

        previousDelta = delta;
        previousF = f;
    }
    return miss;
}
