// Vintage Visuals - modular terrain material PBR restoration.
//
// This is the new production terrain material path, not the old pseudopbr
// monolith. The core material renderer is allowed to use the primary and
// secondary material atlases. Scene/world reflection resources remain absent
// from this module until their sampler map has been runtime validated again.
//
// The same group injects scene.glsl (at vanilla's rgbaFog input) and pbrcore.glsl
// (at vanilla's lightPosition uniform) above this file, exactly as the entity and
// particle groups do. Their declarations - the scene lanes, the shared look
// controls, the emission model and the environment layers - are therefore NOT
// repeated here: GLSL rejects a second declaration of the same uniform, and a
// second copy of a formula is how two programs stop agreeing.

uniform sampler2D vv_materialTex;
uniform sampler2D vv_materialTex2;
uniform float vv_material2Valid;

uniform float vv_pbrEnabled;
uniform float vv_pbrDebugView;
uniform float vv_pbrNormalStrength;
uniform float vv_pbrRoughnessBias;
uniform float vv_pbrSpecularStrength;
uniform float vv_pbrDetailDistance;
uniform float vv_pbrCavity;
uniform float vv_pbrSpecOcclusion;

// Weather on terrain. Every zero is the harmless value: no ripples, and a rain
// cover threshold of 0 lets rain reach anything the sky can see at all.
uniform float vv_weatherRainCover;   // sky exposure a surface needs before rain reaches it
uniform float vv_weatherRipples;     // 0 still water, 1 rain landing in it
uniform float vv_weatherRippleTime;  // ripple clock, pre-wrapped to 0..1 on the CPU
uniform float vv_weatherRippleEpoch; // whole wraps of that clock so far, mod 4096
uniform vec3 vv_pbrOrigin;           // camera world position, wrapped to VV_TERRAIN_ORIGIN_PERIOD on the CPU
uniform vec3 vv_pbrEye;              // the eye relative to the player origin positions are measured from (D74); 0 is the feet, as before
uniform float vv_pbrTorchFalloff;    // steepens the game's torch falloff on blocks (D70); 0 is vanilla
uniform float vv_pbrCreviceWater;    // water follows the texture's relief; 0 is the even film from before
uniform float vv_weatherRain;        // how hard rain is falling, a fact; 0 means no streams

// Vanilla's per-vertex sun light level (rgbaLightIn.a), 0 under a roof and 1 in
// the open. This group writes it in the vertex shader of the same program, so
// both halves of the varying apply or roll back together.
in float vv_sunExposure;
// The block light alone, per vertex, from rgbaLightIn.rgb (D67b; see
// pbrterrainmaterial.yaml for why vanilla's `blockLight` is not it). Zero
// where no torch reaches, which is "no torch effect".
in vec3 vv_blockLight;

vec3 vvTerrainTorchLight()
{
    return clamp(vv_blockLight, vec3(0.0), vec3(2.0));
}

uniform float vv_pbrFoliage;   // light passing through leaves; 0 is vanilla
uniform float vv_pbrDapple;        // canopy shade between sunflecks; 0 is vanilla
uniform float vv_pbrCanopyRadius;  // shadow-map ring radius in texels; 0 disables the canopy test
uniform float vv_pbrSunflecks;     // sunflecks inside canopy shade; 0 is vanilla
uniform float vv_pbrMoonFlecks;    // the moon's faint version of them; 0 is none
uniform float vv_pbrShafts;        // beams from real gaps into vanilla's god-ray pass; 0 is vanilla
// v14 forest pass. Every strength is 0 for vanilla, and so is an unset uniform.
uniform float vv_pbrGusts;         // travelling gusts on plants and in the flecks; 0 is vanilla
uniform vec2 vv_pbrWindDir;        // the game's wind direction in world XZ; 0 reads as +X, harmless at 0 gusts
uniform float vv_pbrContactShade;  // shade at the base of ground plants; 0 is vanilla
uniform float vv_pbrDamp;          // damp and moss on sheltered rough faces; 0 is vanilla
uniform float vv_pbrHumidity;      // the climate's rainfall where the player is, 0 arid to 1 lush; 0 means no damp
// Vanilla's own uniform, the origin worldPos is relative to. Its vertex shader
// bends a plant by worldPos.y + playerpos.y; the plant-height terms below read
// the same sum, so they agree with the bend exactly.
uniform vec3 playerpos;

// The leaf map: for each 2x2-block cell around the player, how much leaf is
// overhead and the world Y of the lowest leaf, read from the world's own blocks
// on the CPU (src/Common/Scene/CanopyMap.cs). A uniform array, not a sampler,
// so it cannot shift vanilla's texture units. Two 12-bit cells per float.
// vv_canopyMapValid 0 means "no map" and every consumer draws vanilla.
uniform vec4 vv_canopyMap[128];
// The gust field's lattice values around the camera, one 16 x 16 window of
// cells per copy (copy A in 0..63, copy B in 64..127), computed on the CPU
// with the shader's own hash (src/PseudoPBR/GustField.cs). The same numbers
// the hash would give, read instead of computed: every plant fragment paid
// about 24 integer hashes for the two copies (D85). Outside the window, and
// whenever the table is not valid, the hash runs as before.
uniform vec4 vv_gustTable[128];
uniform vec4 vv_gustBase;        // xy copy A's first cell, zw copy B's; whole numbers
uniform float vv_gustTableValid; // 1 when the table matches this frame's clock and wind; 0 hashes
uniform float vv_canopyMapValid;
uniform float vv_canopyPlaneY;   // world Y the fleck pattern is projected from

// Pixel reflection: how far the ambient highlight is replaced by a discrete,
// texel-locked image of what each texel faces. 0 is the flat sky colour this
// path used before (exactly), 1 the full image. No sampler of its own: the
// analytic sky, horizon and ground need none.
uniform float vv_pbrPixelReflect;

// The scene capture is OPTIONAL and belongs to another group
// (pbrterrainreflect), which owns its sampler and declares it below vanilla's
// last one, directly above main - after this file. So this file only ever
// sees the march through a prototype, and only when that group has inserted
// the define below on the line after this marker. If that group is absent or
// rolls back, the define goes with it and every reflection here is the
// analytic environment alone.
// vintagevisuals-hook: terrain scene capture define

// The hit and its reason codes are the shared core's (reflect.glsl, D81),
// which this group injects directly above the marker.

#ifdef VV_TERRAIN_SCENE_CAPTURE
VvReflectHit vvTerrainSceneMarch(vec3 origin, vec3 r, vec3 v, float blur);
#endif

const float VV_TERRAIN_MIN_ROUGHNESS = 0.18;
// The floor for the reflection alone: still water, a polished metal, glass.
const float VV_TERRAIN_MIRROR_ROUGHNESS = 0.04;

// How much of vanilla's lit colour a metal keeps as its body (D63): a polished
// one less, a rough one more. Never 1, so a metal still reads darker and more
// saturated than the painted block it would otherwise be.
const float VV_TERRAIN_METAL_BODY_SMOOTH = 0.30; // D83: the reflection carries a polished metal
const float VV_TERRAIN_METAL_BODY_ROUGH = 0.85;

// Flora taxonomy, read from vanilla's own wind modes in renderFlags. Same
// numbering and the same values as the legacy patch (pseudopbr.glsl), which
// was seen working in a world as debug view 44. Modes 6 and 12 are liquid, not
// plant, and are mapped out here once.
#define VV_TFLORA_NONE      0
#define VV_TFLORA_HERB      1
#define VV_TFLORA_GRASS     2
#define VV_TFLORA_LEAVES    3
#define VV_TFLORA_CROP      4
#define VV_TFLORA_REED      5
#define VV_TFLORA_STIFF     7
#define VV_TFLORA_FRUIT     8
#define VV_TFLORA_BUSH      9
#define VV_TFLORA_VINE     10
#define VV_TFLORA_AQUATIC  11
#define VV_TFLORA_THIN     13

int vvTerrainFloraClass()
{
    if ((renderFlags & WindModeBitMask) == 0) return VV_TFLORA_NONE;

    int mode = (renderFlags >> WindModePosition) & 0xF;
    if (mode == 6 || mode == 12) return VV_TFLORA_NONE;
    return mode;
}

// Plant tissue never takes the opaque material (relief, cavity, atlas
// specular): it has its own path, vvTerrainFloraShade.
bool vvTerrainIsFlora()
{
    return vvTerrainFloraClass() != VV_TFLORA_NONE;
}

struct VvTerrainMaterial
{
    vec3 baseColor;
    vec3 faceNormal;
    vec3 normal;
    float perceptualRoughness;
    float alphaRoughness;
    float specularFactor;
    float metalness;
    float height;
    float ao;
    float emissionMask;
    float detailFade;
    float wetness;
    float snow;
    float frost;
    float holdsWater; // 1 where the region carries the crevice maps (D66, D67)
    float jointPool;  // baked: how deep a joint this texel is, 0 off the joint network (D67)
    float flowAmount; // baked: how much rain a wall path gathers through this texel (D67)
    float flowPhase;  // baked: how far along its path, for drops sliding down it (D67)
    float pool;     // standing water filling this texel (top faces, D65)
    float stream;   // water running down this texel (side faces, D65)
    // What the REFLECTION sees (phase 1, D81). The direct lobe keeps the
    // 0.18 floor, because a sharper GGX peak on a 16-pixel texture is a
    // single flickering pixel. A reflection has no such problem - it is a
    // picture - and the floor was what kept still water, wet stone and
    // polished blocks from ever showing a clear one.
    float reflectRoughness;
};

vec3 vvTerrainSafeNormalize(vec3 v, vec3 fallback)
{
    float len2 = dot(v, v);
    if (!(len2 > 1e-12) || len2 > 1e24) return fallback;
    return v * inversesqrt(len2);
}

// From a point to the EYE. Positions here have the player's feet at the
// origin; the eye height lives only in the view matrix, so -p looks from the
// feet and a floor at standing height reads edge-on (D74). Every view vector
// on terrain starts here.
vec3 vvTerrainToEye(vec3 p)
{
    return vv_pbrEye - p;
}

// How far a point is from the EYE, for every distance fade (detail, dapple,
// flecks, rims, reflection reach). Fog is the exception and keeps vanilla's
// own feet-origin distance, because the two must agree (reflection phase 0).
float vvTerrainEyeDistance(vec3 p)
{
    return length(vvTerrainToEye(p));
}

vec2 vvTerrainSnap(vec2 materialUv)
{
    vec2 size = max(vec2(1.0), vec2(textureSize(vv_materialTex, 0)));
    return (floor(materialUv * size) + vec2(0.5)) / size;
}

vec4 vvTerrainPrimaryTexel(vec2 materialUv)
{
    return texture(vv_materialTex, vvTerrainSnap(materialUv));
}

vec4 vvTerrainSecondaryTexel(vec2 materialUv)
{
    if (vv_material2Valid < 0.5) return vec4(0.0, 0.5, 1.0, 1.0);
    return texture(vv_materialTex2, vvTerrainSnap(materialUv));
}

mat3 vvTerrainFrame(vec3 n)
{
    vec3 reference = abs(n.y) > 0.99 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
    vec3 tangent = vvTerrainSafeNormalize(cross(reference, n), vec3(1.0, 0.0, 0.0));
    vec3 bitangent = cross(n, tangent);
    return mat3(tangent, bitangent, n);
}

float vvTerrainDetailFade(vec3 cameraRelativePos)
{
    float far = max(4.0, vv_pbrDetailDistance);
    float near = far * 0.33;
    return clamp((far - vvTerrainEyeDistance(cameraRelativePos)) / max(1.0, far - near), 0.0, 1.0);
}

vec3 vvTerrainNormalFromTexel(vec4 primary, vec3 faceNormal, float detailFade)
{
    vec2 xy = (primary.rg * 2.0 - 1.0) * max(0.0, vv_pbrNormalStrength) * detailFade;
    float z = sqrt(max(1e-4, 1.0 - dot(xy, xy)));
    return vvTerrainSafeNormalize(vvTerrainFrame(faceNormal) * vec3(xy, z), faceNormal);
}

// Normal variance across the pixel widens the lobe, for the highlight and
// the reflection alike; only the floor differs between them.
float vvTerrainFilteredRoughnessFloor(float perceptualRoughness, vec3 n, float floorValue)
{
    if (vv_pbrSpecularAA < 0.5) return perceptualRoughness;
    vec3 dx = dFdx(n);
    vec3 dy = dFdy(n);
    float variance = clamp(max(dot(dx, dx), dot(dy, dy)), 0.0, 1.0);
    return clamp(sqrt(perceptualRoughness * perceptualRoughness + variance * 0.32),
                 floorValue, 1.0);
}

float vvTerrainFilteredRoughness(float perceptualRoughness, vec3 n)
{
    return vvTerrainFilteredRoughnessFloor(perceptualRoughness, n, VV_TERRAIN_MIN_ROUGHNESS);
}

// --- Weather on terrain -------------------------------------------------------
//
// Ported from the legacy terrain shader. Rain falls downward and cannot reach
// what the sky cannot see; snow likewise, and slides off slopes harder than rain
// runs off them. The exposure test is a THRESHOLD on vanilla's soft sun light,
// because read linearly it only dries fully enclosed rooms and leaves a porch
// as wet as the lawn beside it.

const float VV_TERRAIN_RIPPLE_DEPTH = 1.2;
const float VV_TERRAIN_RIPPLE_BAND = 14.0;
const float VV_TERRAIN_RIPPLE_WAVE = 30.0;
const float VV_TERRAIN_RIPPLE_JITTER = 0.5;
// Must divide the CPU wrap period (EnvironmentState.CameraPeriod) exactly, so
// the coordinate wrap lands on a ripple cell and texel boundary.
const float VV_TERRAIN_ORIGIN_PERIOD = 4096.0;
const float VV_TERRAIN_RIPPLE_TEXELS = 32.0;

float vvTerrainSkyExposure()
{
    return smoothstep(vv_weatherRainCover - 0.12, vv_weatherRainCover + 0.06,
                      clamp(vv_sunExposure, 0.0, 1.0));
}

// How much rain a plant HOLDS rather than sheds, which is a fact about its
// shape: a cupped flower keeps water a grass blade runs straight off. Same
// values as the legacy patch. Aquatic flora is already in water.
float vvTerrainFloraPooling()
{
    int flora = vvTerrainFloraClass();
    if (flora == VV_TFLORA_NONE)    return 1.0;
    if (flora == VV_TFLORA_AQUATIC) return 0.0;
    if (flora == VV_TFLORA_FRUIT)   return 0.55;
    if (flora == VV_TFLORA_HERB)    return 0.95;
    if (flora == VV_TFLORA_GRASS)   return 0.70;
    return 0.85;
}

float vvTerrainWetness(vec3 faceNormal)
{
    if (vv_sceneWetness < 0.001) return 0.0;
    float facing = clamp(faceNormal.y * 0.5 + 0.5, 0.0, 1.0);
    // A plant is not a flat surface, so the up-facing test is relaxed for it
    // rather than squared, and how much stays is the plant's own pooling.
    if (vvTerrainIsFlora())
        return clamp(vv_sceneWetness * mix(0.45, 1.0, facing) * vvTerrainFloraPooling()
                     * vvTerrainSkyExposure(), 0.0, 1.0);
    return clamp(vv_sceneWetness * facing * facing * vvTerrainSkyExposure(), 0.0, 1.0);
}

float vvTerrainSnow(vec3 faceNormal)
{
    if (vv_sceneSnow < 0.001) return 0.0;
    float facing = clamp(faceNormal.y, 0.0, 1.0);
    return clamp(vv_sceneSnow * facing * facing * facing * vvTerrainSkyExposure(), 0.0, 1.0);
}

// Integer hash: exact at any coordinate, which matters because every earlier
// ripple defect was a float32 precision problem rather than a bad hash.
uint vvTerrainHashU(uint x)
{
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

vec4 vvTerrainCellRandom(vec2 cell)
{
    uvec2 c = uvec2(ivec2(cell));
    uint h = vvTerrainHashU(c.x * 0x9e3779b9u ^ vvTerrainHashU(c.y + 0x85ebca6bu));
    return vec4(float(vvTerrainHashU(h)),
                float(vvTerrainHashU(h ^ 0x68bc21ebu)),
                float(vvTerrainHashU(h ^ 0x02e5be93u)),
                float(vvTerrainHashU(h ^ 0x9c8f2d3bu))) / 4294967295.0;
}

// One drop impact per cell, returned as a slope. The coordinate MUST arrive
// pre-wrapped: on a raw world position a float32 holds about sixteen positions
// per ripple cell half a million blocks out.
vec2 vvTerrainRippleSlope(vec2 p, float t, float epoch, float density)
{
    vec2 cell = floor(p);
    vec2 local = fract(p);
    vec4 rnd = vvTerrainCellRandom(cell);

    // Whole drops per wrap of the 0..1 clock, so the wrap cannot jump. The
    // drop COUNT runs on across wraps (epoch = wraps so far, whole), and each
    // drop draws its own chance to fall and its own spot in the cell: rain
    // lands somewhere new every time, and the density decides how many cells
    // are struck rather than which ones.
    float rate = 1.0 + floor(rnd.w * 3.0);
    float phase = t * rate + rnd.y * 0.61 + rnd.z * 0.39;
    float age = fract(phase);
    float drop = floor(phase) + epoch * rate;
    vec4 hit = vvTerrainCellRandom(vec2(drop, floor(rnd.x * 1048576.0)));
    if (hit.x > density) return vec2(0.0);

    vec2 centre = vec2(0.5) + (hit.yz - 0.5) * VV_TERRAIN_RIPPLE_JITTER;
    float reach = 0.5 - VV_TERRAIN_RIPPLE_JITTER * 0.5;
    vec2 offset = local - centre;
    float r = length(offset);
    if (r > reach) return vec2(0.0);

    float front = age * reach;
    float band = exp(-abs(r - front) * VV_TERRAIN_RIPPLE_BAND);
    float decay = (1.0 - age) * (1.0 - age);

    vec2 dir = offset / max(r, 1e-5);
    return dir * sin((r - front) * VV_TERRAIN_RIPPLE_WAVE) * band * decay;
}

// Two scales at unrelated rates, the second offset by a non-integer so its cell
// boundaries do not sit on the first's. Snapped to the texture's pixel grid in
// space only, never in time.
vec2 vvTerrainRainRipples(vec2 worldXZ, float t, float density)
{
    vec2 p = floor(worldXZ * VV_TERRAIN_RIPPLE_TEXELS) / VV_TERRAIN_RIPPLE_TEXELS;
    // The slow layer runs at half the clock. Its time and drop count are
    // rebuilt from the epoch so both stay continuous when the clock wraps.
    float u = mod(vv_weatherRippleEpoch, 2.0) * 0.5 + t * 0.5 + 0.37;
    return vvTerrainRippleSlope(p * 2.0, t, vv_weatherRippleEpoch, density)
         + vvTerrainRippleSlope(p + vec2(31.43, 17.61), fract(u),
                                floor(vv_weatherRippleEpoch * 0.5) + floor(u), density) * 0.7;
}

// Rain landing in standing water: only where it is wet AND the face points up,
// faded with the relief so the finest thing this shader draws does not alias.
vec2 vvTerrainRippleField(vec3 faceNormal, vec3 cameraRelativePos, float wetness, float fade)
{
    if (vv_weatherRipples < 0.001 || wetness < 0.001) return vec2(0.0);
    float pooling = clamp(faceNormal.y, 0.0, 1.0);
    float amount = clamp(vv_weatherRipples, 0.0, 1.0) * wetness * pooling * pooling * fade;
    if (amount < 0.001) return vec2(0.0);

    vec3 world = cameraRelativePos + vv_pbrOrigin;
    return vvTerrainRainRipples(world.xz, vv_weatherRippleTime, clamp(vv_weatherRipples, 0.0, 1.0)) * amount;
}

// Applies the weather to a decoded material: the ripple normal, then the shared
// environment layer stack from pbrcore.glsl for roughness and specular (wet is
// glossier, snow and frost are rough). Albedo is darkened later, in
// vvTerrainApplySurfaceLayers, on the lit colour.
// ---------------------------------------------------------------------------
// Water in crevices (D64, D67)
//
// Rain follows each block's own structure instead of lying as one even film.
//
// D67: the STRUCTURE is measured once, on the CPU, when the atlas is built
// (src/PseudoPBR/CreviceMaps.cs, a port of tools/pbrgen/crevice.py, where it
// was tuned and approved on the game's own stone textures). v17 to v19 asked
// the shader to find crevices by comparing a texel with its four neighbours,
// and on Vintage Story's stone the only height there is the texture's painted
// luminance, which is mostly speckle: every version read every speck as a
// dip. A joint is a long CONNECTED dark run several texels across, which a
// fragment cannot afford to see and a load-time pass can. The second atlas
// now carries, for a material that holds water:
//
//   jointPool   the joint network (long connected dark runs; specks and
//               chisel marks are rejected by length) and how deep each texel
//               of it is
//   height      each stone as a pillow, rounded within three texels of a
//               joint, flat on top
//   flowAmount  for a wall: where the rain landing on the face goes. Water
//               takes the lowest of the three texels below it on the stone
//               shape, so it slides off a bump's shoulder and gathers in the
//               channels between stones; under a ledge it runs sideways along
//               the joint until it finds a way down. The paths that collect
//               the most become the visible streams.
//   flowPhase   how far along its path each texel is, plus the path's own
//               offset, so drops slide ALONG the path, bends included.
//
// ONLY ON MATERIALS THAT HOLD WATER (D66): the game's own EnumBlockMaterial
// decides, and the flag bit in red says this region carries the maps. Soil,
// grass, sand, gravel, wood and leaves keep the even wet film.
//
// TOP FACES pool, from the joint map alone, once the ground is a little wet
// (the shared wetness, which lingers after rain and dries on its own clock).
// A pooled texel is open water: flat, a mirror, its crevice occlusion lifted
// because the water surface sits above the crevice, darker and a touch cool,
// and the only place ripples land. The stones keep a thin damp film.
//
// SIDE FACES stream, only while rain is FALLING (vv_weatherRain, a fact) on a
// wall open to the sky, and only where the texture is upright on the face
// (image rows run down the wall), because that is the orientation the paths
// were traced in. The joints are wet; a path is a wet film by how much it
// carries, and drops run along it.
//
// Pixel art by construction: every input is per material texel and drops
// step whole texels. DATA GAP: the game simulates no water on a block face;
// where the joints and paths are is the texture's, where a drop is at a given
// moment is ours. Cost: no extra texture read for the water (the maps arrive
// with the texel already read), two derivatives on a wall; nothing when dry
// or with the slider at 0. Not measured.
// ---------------------------------------------------------------------------

const float VV_TERRAIN_POOL_FILM = 0.50;      // damp left on the stones around the water (D83)
const float VV_TERRAIN_POOL_DARKEN = 0.62;    // standing water over the wet darkening
const float VV_TERRAIN_STREAM_BASE = 0.50;    // the film a full path keeps between drops
const float VV_TERRAIN_STREAM_JOINT = 0.60;   // how wet a wall's joints are in rain
const float VV_TERRAIN_STREAM_DROP = 0.30;    // share of a path's cycle a drop covers (~3 texels)
const float VV_TERRAIN_STREAM_SPEED = 2.0;    // path cycles per 1.5 s scene clock: integer, so the wrap is seamless
const float VV_TERRAIN_STREAM_UPRIGHT = 0.85; // how close to image-down the wall's fall line must be

float vvTerrainHeightAt(vec2 materialUv)
{
    return vvTerrainSecondaryTexel(materialUv).g;
}

// Where a drop is on its path: 1 inside a drop, 0 between. Periodic in the
// scene clock (VV_TERRAIN_STREAM_SPEED is a whole number of cycles per wrap),
// and the block's own seed keeps neighbouring blocks out of step.
float vvTerrainStreamDrop(float flowPhase, float blockSeed)
{
    float phase = fract(flowPhase - vv_sceneClock * VV_TERRAIN_STREAM_SPEED + blockSeed);
    return 1.0 - step(VV_TERRAIN_STREAM_DROP, phase);
}

// x: standing water in this texel (top faces), y: running water (side faces).
vec2 vvTerrainCreviceWater(VvTerrainMaterial m, vec2 materialUv, vec3 cameraRelativePos)
{
    float amount = clamp(vv_pbrCreviceWater, 0.0, 1.0);
    if (amount < 0.001 || m.wetness < 0.001 || vv_material2Valid < 0.5 || m.detailFade <= 0.0)
        return vec2(0.0);
    if (m.holdsWater < 0.5) return vec2(0.0);

    float up = m.faceNormal.y;
    if (up > 0.7)
    {
        // m.wetness already holds the sky exposure, so a sheltered top stays dry.
        float pool = m.jointPool * smoothstep(0.08, 0.45, clamp(m.wetness, 0.0, 1.0));
        return vec2(pool * amount * m.detailFade, 0.0);
    }

    float rain = clamp(vv_weatherRain, 0.0, 1.0);
    if (abs(up) > 0.3 || rain < 0.001) return vec2(0.0);

    // Which way is down, in material texels: the least-squares UV step per
    // block of falling world Y, from screen derivatives.
    vec2 size = max(vec2(1.0), vec2(textureSize(vv_materialTex, 0)));
    vec2 uvdx = dFdx(materialUv) * size;
    vec2 uvdy = dFdy(materialUv) * size;
    float ydx = dFdx(cameraRelativePos.y);
    float ydy = dFdy(cameraRelativePos.y);
    float g2 = ydx * ydx + ydy * ydy;
    if (g2 < 1e-10) return vec2(0.0);
    vec2 downTexels = -(uvdx * ydx + uvdy * ydy) / g2;
    float perBlock = length(downTexels);
    if (perBlock < 1.0) return vec2(0.0);
    // The paths were traced with image rows running DOWN. On a texture turned
    // sideways on this face they would run across the wall, so only the joints
    // get wet there.
    float upright = step(VV_TERRAIN_STREAM_UPRIGHT, downTexels.y / perBlock);

    vec3 cell = floor(cameraRelativePos + vv_pbrOrigin - m.faceNormal * 0.01);
    float blockSeed = vvTerrainCellRandom(vec2(cell.x * 7.0 + cell.y * 13.0, cell.z * 5.0 + cell.y)).x;
    float drop = vvTerrainStreamDrop(m.flowPhase, blockSeed);
    float path = m.flowAmount * upright;
    float joint = step(0.001, m.jointPool) * VV_TERRAIN_STREAM_JOINT;
    float stream = max(path * (VV_TERRAIN_STREAM_BASE + (1.0 - VV_TERRAIN_STREAM_BASE) * drop), joint);
    if (stream < 0.01) return vec2(0.0);
    return vec2(0.0, stream * rain * amount * clamp(vvTerrainSkyExposure() * 2.0, 0.0, 1.0) * m.detailFade);
}

// Occlusion from the stone shape: the joint floor (shape 0) keeps
// VV_TERRAIN_SHAPE_AO of the light, a stone's flat top (shape above about
// 0.6) all of it. Only removes light, like the plain layout's baked AO.
const float VV_TERRAIN_SHAPE_AO = 0.55;

float vvTerrainShapeOcclusion(float shape)
{
    return mix(VV_TERRAIN_SHAPE_AO, 1.0, smoothstep(0.0, 0.6, shape));
}

VvTerrainMaterial vvTerrainApplyWeather(VvTerrainMaterial m, vec3 cameraRelativePos, vec2 materialUv)
{
    m.wetness = vvTerrainWetness(m.faceNormal);

    // Crevices redistribute the water before anything reads the wetness:
    // pooled texels are fully wet and flat, the rest of a top face keeps a
    // film, and a running channel is wet whatever the wall's own film is.
    vec2 crevice = vvTerrainCreviceWater(m, materialUv, cameraRelativePos);
    float amount = clamp(vv_pbrCreviceWater, 0.0, 1.0);
    if (m.faceNormal.y > 0.7 && amount > 0.001 && vv_material2Valid > 0.5)
    {
        m.wetness = mix(m.wetness * mix(1.0, VV_TERRAIN_POOL_FILM, amount), 1.0, crevice.x);
        m.normal = vvTerrainSafeNormalize(mix(m.normal, m.faceNormal, crevice.x), m.faceNormal);
    }
    m.wetness = max(m.wetness, crevice.y);
    m.pool = crevice.x;
    m.stream = crevice.y;
    // The water's surface sits ABOVE the crevice it fills, so the crevice no
    // longer shadows what that surface reflects: v17 kept the joint's baked
    // occlusion, and specular occlusion then took the reflection away from
    // exactly the texels holding water.
    m.ao = mix(m.ao, 1.0, max(m.pool, m.stream * 0.6));

    m.snow = vvTerrainSnow(m.faceNormal);
    m.frost = clamp(vv_sceneFrost, 0.0, 1.0);

    // Running water is a film over the wall's grain: it keeps half of it.
    // (Pools were flattened above, fully.)
    float streamFlat = m.stream * 0.5 * (1.0 - clamp(m.snow + m.frost, 0.0, 1.0));
    if (streamFlat > 0.001)
    {
        m.normal = vvTerrainSafeNormalize(mix(m.normal, m.faceNormal, streamFlat), m.faceNormal);
    }

    // Ripples land only on open water when crevices are on.
    float rippleWater = mix(m.wetness, crevice.x, step(0.001, amount) * step(0.5, vv_material2Valid));
    vec2 slope = vvTerrainRippleField(m.faceNormal, cameraRelativePos, rippleWater, m.detailFade);
    if (dot(slope, slope) > 0.0)
    {
        m.normal = vvTerrainSafeNormalize(m.normal + vec3(slope.x, 0.0, slope.y) * VV_TERRAIN_RIPPLE_DEPTH,
                                          m.normal);
    }

    if (m.wetness + m.snow + m.frost > 0.001)
    {
        VvSurface base;
        base.albedo = m.baseColor;
        base.roughness = m.perceptualRoughness;
        base.specular = m.specularFactor;
        VvSurface layered = vvApplyEnvironmentLayers(base, m.wetness, m.snow, m.frost, 0.0);
        // The same layers over the reflection's own roughness: a water film
        // smooths what it reflects, snow and frost roughen it.
        base.roughness = m.reflectRoughness;
        m.reflectRoughness = clamp(vvApplyEnvironmentLayers(base, m.wetness, m.snow, m.frost, 0.0).roughness,
                                   VV_TERRAIN_MIRROR_ROUGHNESS, 1.0);
        m.perceptualRoughness = clamp(layered.roughness, VV_TERRAIN_MIN_ROUGHNESS, 1.0);
        m.alphaRoughness = max(VV_TERRAIN_MIN_ROUGHNESS * VV_TERRAIN_MIN_ROUGHNESS,
                               m.perceptualRoughness * m.perceptualRoughness);
        m.specularFactor = clamp(layered.specular, 0.0, 1.0);
    }
    // Open water is a mirror whatever the stone under it was.
    float water = max(m.pool, m.stream) * (1.0 - clamp(m.snow + m.frost, 0.0, 1.0));
    if (water > 0.001)
    {
        m.perceptualRoughness = mix(m.perceptualRoughness, VV_TERRAIN_MIN_ROUGHNESS, water);
        m.alphaRoughness = max(VV_TERRAIN_MIN_ROUGHNESS * VV_TERRAIN_MIN_ROUGHNESS,
                               m.perceptualRoughness * m.perceptualRoughness);
        m.specularFactor = max(m.specularFactor, 0.8 * water);
        m.reflectRoughness = mix(m.reflectRoughness, VV_TERRAIN_MIRROR_ROUGHNESS, water);
    }
    return m;
}

VvTerrainMaterial vvDecodeTerrainMaterial(vec4 litColor, vec3 baseColor,
                                          vec3 faceNormal, vec2 materialUv,
                                          vec3 cameraRelativePos)
{
    vec4 primary = vvTerrainPrimaryTexel(materialUv);
    vec4 secondary = vvTerrainSecondaryTexel(materialUv);
    float detailFade = vvTerrainDetailFade(cameraRelativePos);

    VvTerrainMaterial m;
    m.baseColor = clamp(baseColor, 0.0, 8.0);
    m.faceNormal = vvTerrainSafeNormalize(faceNormal, vec3(0.0, 1.0, 0.0));
    m.normal = vvTerrainNormalFromTexel(primary, m.faceNormal, detailFade);
    m.perceptualRoughness = clamp(primary.b + vv_pbrRoughnessBias, VV_TERRAIN_MIN_ROUGHNESS, 1.0);
    m.perceptualRoughness = vvTerrainFilteredRoughness(m.perceptualRoughness, m.normal);
    m.reflectRoughness = vvTerrainFilteredRoughnessFloor(
        clamp(primary.b + vv_pbrRoughnessBias, VV_TERRAIN_MIRROR_ROUGHNESS, 1.0),
        m.normal, VV_TERRAIN_MIRROR_ROUGHNESS);
    m.alphaRoughness = max(VV_TERRAIN_MIN_ROUGHNESS * VV_TERRAIN_MIN_ROUGHNESS,
                           m.perceptualRoughness * m.perceptualRoughness);
    m.specularFactor = clamp(primary.a * max(0.0, vv_pbrSpecularStrength), 0.0, 1.0);
    // Red is metalness in the top seven bits and the hold-water flag in the
    // lowest (D66, MaterialAtlas2Builder.PackMetalWater); nearest-sampled, so
    // the byte comes back exact.
    float redByte = floor(secondary.r * 255.0 + 0.5);
    float metalLevel = floor(redByte * 0.5);
    m.metalness = clamp(metalLevel / 127.0 * max(0.0, vv_pbrMetalResponse), 0.0, 1.0);
    m.holdsWater = redByte - metalLevel * 2.0;
    m.holdsWater = vv_material2Valid < 0.5 ? 0.0 : m.holdsWater;
    m.height = clamp(secondary.g, 0.0, 1.0);
    m.ao = clamp(secondary.b, 0.18, 1.0);
    m.emissionMask = vv_material2Valid < 0.5 ? 1.0 : clamp(secondary.a, 0.0, 1.0);
    m.jointPool = 0.0;
    m.flowAmount = 0.0;
    m.flowPhase = 0.0;
    if (m.holdsWater > 0.5)
    {
        // The crevice layout (D67, MaterialAtlas2Builder): green is the stone
        // shape, blue the joint depth in its high nibble and the gathered
        // flow in its low, alpha the flow phase. AO comes from the shape -
        // the joints between stones, not the painted specks on them.
        float blueByte = floor(secondary.b * 255.0 + 0.5);
        float poolLevel = floor(blueByte / 16.0);
        m.jointPool = poolLevel / 15.0;
        m.flowAmount = (blueByte - poolLevel * 16.0) / 15.0;
        m.flowPhase = secondary.a;
        m.ao = vvTerrainShapeOcclusion(m.height);
        m.emissionMask = 0.0;
    }
    m.detailFade = detailFade;
    m.wetness = 0.0;
    m.snow = 0.0;
    m.frost = 0.0;
    m.pool = 0.0;
    m.stream = 0.0;
    return vvTerrainApplyWeather(m, cameraRelativePos, materialUv);
}

float vvDistributionGGXTerrain(float ndoth, float alphaRoughness)
{
    float a2 = alphaRoughness * alphaRoughness;
    float d = ndoth * ndoth * (a2 - 1.0) + 1.0;
    return a2 / max(VV_PI * d * d, 1e-4);
}

float vvGeometrySchlickGGXTerrain(float ndotx, float perceptualRoughness)
{
    float r = perceptualRoughness + 1.0;
    float k = (r * r) * 0.125;
    return ndotx / max(ndotx * (1.0 - k) + k, 1e-4);
}

float vvGeometrySmithTerrain(float ndotv, float ndotl, float perceptualRoughness)
{
    return vvGeometrySchlickGGXTerrain(ndotv, perceptualRoughness) *
           vvGeometrySchlickGGXTerrain(ndotl, perceptualRoughness);
}

vec3 vvFresnelSchlickTerrain(float cosTheta, vec3 f0)
{
    float f = pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
    return f0 + (1.0 - f0) * f;
}

float vvDirectionalShadeTerrain(vec3 n)
{
    return max(0.0, 0.5 + 0.5 * dot(n, lightPosition));
}

// Which way the grain runs, and how sure we are, measured from the texture.
//
// Ported from the legacy terrain shader unchanged in method: the atlas normal's
// xy IS the height gradient, the structure tensor of five taps gives its
// principal axis without the two sides of a grain line cancelling, and the
// normalised eigenvalue difference is a measured coherence - 1 on a clean plank
// line, near 0 on mottled stone. That coherence is what gates the effect, so
// nothing has to know which block it is looking at.
//
// Returns (direction.x, direction.y, coherence) in tangent space. Reads only
// the primary atlas that is already bound: no new sampler, no new unit.
vec3 vvTerrainGrainDirection(vec2 materialUv)
{
    vec2 texel = 1.0 / max(vec2(1.0), vec2(textureSize(vv_materialTex, 0)));

    vec2 c = vvTerrainPrimaryTexel(materialUv).rg - 0.5;
    vec2 l = vvTerrainPrimaryTexel(materialUv - vec2(texel.x, 0.0)).rg - 0.5;
    vec2 r = vvTerrainPrimaryTexel(materialUv + vec2(texel.x, 0.0)).rg - 0.5;
    vec2 d = vvTerrainPrimaryTexel(materialUv - vec2(0.0, texel.y)).rg - 0.5;
    vec2 u = vvTerrainPrimaryTexel(materialUv + vec2(0.0, texel.y)).rg - 0.5;

    float jxx = c.x * c.x + l.x * l.x + r.x * r.x + d.x * d.x + u.x * u.x;
    float jyy = c.y * c.y + l.y * l.y + r.y * r.y + d.y * d.y + u.y * u.y;
    float jxy = c.x * c.y + l.x * l.y + r.x * r.y + d.x * d.y + u.x * u.y;

    float trace = jxx + jyy;
    if (trace < 1e-6) return vec3(1.0, 0.0, 0.0);

    float diff = jxx - jyy;
    float root = sqrt(max(0.0, diff * diff + 4.0 * jxy * jxy));
    float major = 0.5 * (trace + root);
    float minor = 0.5 * (trace - root);
    float coherence = clamp((major - minor) / max(1e-6, trace), 0.0, 1.0);

    vec2 across = vvTerrainSafeNormalize(vec3(jxy, major - jxx, 0.0), vec3(1.0, 0.0, 0.0)).xy;
    return vec3(-across.y, across.x, coherence);
}

// How much this texel's highlight is stretched along its grain: measured
// coherence x the Wood grain slider x the texel's own reflectance, so a matte
// surface has no highlight to stretch however striated its texture is. Exactly
// 0 when the slider is 0, which keeps the isotropic lobe bit-for-bit.
float vvTerrainGrainAnisotropy(VvTerrainMaterial m, vec3 grain)
{
    if (vv_pbrGrain <= 0.001) return 0.0;
    return clamp(grain.z, 0.0, 1.0) * clamp(vv_pbrGrain, 0.0, 1.0)
         * clamp(m.specularFactor * 2.0, 0.0, 1.0);
}

vec3 vvTerrainDirectSpecular(VvTerrainMaterial m, vec3 viewDir, float shadowBrightness,
                             float fogAmount, float murkiness, vec2 materialUv)
{
    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));
    vec3 h = vvTerrainSafeNormalize(l + viewDir, m.normal);
    float ndotl = max(dot(m.normal, l), 0.0);
    float ndotv = max(dot(m.normal, viewDir), 0.0);
    float ndoth = max(dot(m.normal, h), 0.0);
    float vdoth = max(dot(viewDir, h), 0.0);

    vec3 dielectricF0 = vec3(VV_DIELECTRIC_F0) * mix(0.6, 1.8, m.specularFactor);
    vec3 f0 = mix(dielectricF0, m.baseColor, m.metalness);
    vec3 f = vvFresnelSchlickTerrain(vdoth, f0);
    float d = vvDistributionGGXTerrain(ndoth, m.alphaRoughness);

    // Only the DIRECT lobe takes the grain: the ambient and block-light terms
    // are hemispherical approximations with no lobe shape to stretch.
    if (vv_pbrGrain > 0.001)
    {
        vec3 grain = vvTerrainGrainDirection(materialUv);
        float anisotropy = vvTerrainGrainAnisotropy(m, grain);
        if (anisotropy > 0.001)
        {
            vec3 tangent = vvTerrainSafeNormalize(vvTerrainFrame(m.faceNormal) * vec3(grain.xy, 0.0),
                                                  vec3(1.0, 0.0, 0.0));
            vec3 bitangent = cross(m.normal, tangent);
            d = vvDistributionGGXAnisotropic(ndoth, dot(tangent, h), dot(bitangent, h),
                                             m.perceptualRoughness, anisotropy);
        }
    }

    float g = vvGeometrySmithTerrain(ndotv, ndotl, m.perceptualRoughness);
    vec3 spec = (d * g * f) / max(4.0 * ndotv * ndotl, 0.08);

    float sunAvailable = clamp(vv_sceneDayLight * (1.0 - vv_sceneOvercast * 0.72), 0.0, 1.0);
    float visibility = clamp(shadowBrightness, 0.0, 1.0) * sunAvailable;
    visibility *= 1.0 - clamp(fogAmount * 0.55 + murkiness * 2.0, 0.0, 0.92);
    return spec * ndotl * visibility;
}

// Highlights from torches, lanterns, lava and every other block light (D64).
//
// This path used to aim every block-light highlight a fixed way (the light's
// colour vector nudged upward), so a torch's glint sat in the same place on
// every block whatever side the torch was on, and the Block light sliders did
// not reach terrain at all. It now uses the shared core's model, the one the
// creatures and particles already use: the direction toward the light is
// recovered from how vanilla's own block light falls off across the surface
// (vvBlockLightDirection, Mikkelsen's surface gradient), so a highlight slides
// across a wet floor as you walk past a torch and sits on the side facing it.
// Never gated on the sun or the shadow map: a torch burns at midnight in a
// cave. vv_pbrBlockLight 0 is no highlight; vv_pbrBlockLightDir 0 treats the
// light as coming from straight off the surface.
//
// The direction is taken before any branch, because it is built from screen
// derivatives, which need every fragment of the quad to reach them.
vec3 vvTerrainBlockSpecular(VvTerrainMaterial m, vec3 viewDir, vec3 blockLight, vec3 cameraRelativePos)
{
    float intensity = dot(blockLight, VV_PBR_LUMA);
    vec3 l = vvBlockLightDirection(m.faceNormal, cameraRelativePos, intensity);
    if (vv_pbrBlockLight < 0.001 || intensity < 0.004) return vec3(0.0);

    vec3 h = vvTerrainSafeNormalize(l + viewDir, m.normal);
    float ndotl = max(dot(m.normal, l), 0.0);
    float ndotv = max(dot(m.normal, viewDir), 1e-4);
    float ndoth = max(dot(m.normal, h), 0.0);
    float vdoth = max(dot(viewDir, h), 0.0);
    vec3 f0 = mix(vec3(VV_DIELECTRIC_F0) * mix(0.6, 1.8, m.specularFactor),
                  m.baseColor, m.metalness);

    vec3 f = vvFresnelSchlick(vdoth, f0);
    float d = vvDistributionGGX(ndoth, m.perceptualRoughness);
    float g = vvGeometrySmith(ndotv, ndotl, m.perceptualRoughness);
    vec3 spec = (d * g * f) / max(4.0 * ndotv * ndotl, 0.08);
    return blockLight * spec * ndotl * max(0.0, vv_pbrBlockLight);
}

// Torchlight relief (D65): block light shading the per-pixel normal in the
// DIFFUSE, not only in the highlight.
//
// Vanilla lights a block face with one block-light value per vertex, so a
// torch brightens a cobble face evenly and its relief is lit only by the sun
// term above, which is dark at night and underground - exactly where a torch
// matters. v17 shaded the per-pixel normal in the torch HIGHLIGHT alone, which
// is a narrow lobe on rough stone and next to invisible. This adds the change
// in N.L between the texel's own normal and the flat face toward the same
// recovered light direction (vvBlockLightDirection), in the torch's own
// light on the texture's colour: texels tilted toward the torch brighten,
// texels tilted away darken, and a flat texel is untouched, so the face's
// average stays vanilla's. Zero at vv_pbrBlockLight 0; bounded to a share of
// what vanilla lit so it cannot invent light vanilla did not have.
// Cost: one derivative-based direction per fragment, no texture read.
const float VV_TERRAIN_TORCH_RELIEF = 1.5;
const float VV_TERRAIN_TORCH_RELIEF_CAP = 0.6;
// D69, after D68 in game (2026-11-25): with the torch read correctly, a wall
// torch turned every joint in a small room near-black, ceiling included, and a
// smooth ashlar block got thick black bars. Darkening is now bounded harder
// than brightening - relief may take at most CAP_DARK of what vanilla lit -
// and the joint (hollow) term fades to HOLLOW_FAR of itself where the light
// has no clear direction, which is everywhere but close to the flame.
const float VV_TERRAIN_TORCH_RELIEF_CAP_DARK = 0.35;
// D66, after view 74 showed the D65 term as black-and-white salt and pepper
// on a cobble wall beside a torch (in game, 2026-11-25). Two causes: the
// recovered direction tilts up to ~70 degrees off the face for a torch
// mounted ON the wall, so N.L swung from 0 to 1 between neighbouring texels;
// and the per-texel normal is the texture's luminance gradient, mostly grain.
// The torch now sees a gentler normal (NORMAL of the way from flat to the
// texel's) from a bounded direction (TILT of the way from the face normal to
// the recovered one), and the coherent part of the relief comes from the
// baked occlusion - a texel below its two-texel neighbourhood (a joint, the
// gap between cobbles) gets less torchlight, HOLLOW of what it lacks.
const float VV_TERRAIN_TORCH_TILT = 0.6;
const float VV_TERRAIN_TORCH_NORMAL = 0.6;
const float VV_TERRAIN_TORCH_HOLLOW = 0.4;
const float VV_TERRAIN_TORCH_HOLLOW_FAR = 0.3;  // share of the joint shading kept where the light is diffuse
const float VV_TERRAIN_TORCH_DIRECT = 3.0;      // how fast the light's tilt off the face makes it directional

// D67: on a region carrying the crevice maps the torch reads the STONE SHAPE,
// not the texture's luminance normal: each stone a pillow that turns toward
// the flame and away from it, the joints between them in shadow. That is the
// relief the approved preview showed (tools/pbrgen, crevice-torch-proposal).
// The plain layout keeps the D66 behaviour. Four reads of the second atlas,
// only where block light reaches a water-holding texel.
const float VV_TERRAIN_TORCH_SHAPE = 1.6;     // tilt per unit of shape slope per texel
const float VV_TERRAIN_TORCH_SHAPE_SOFTEN = 0.8; // of the way from flat to the shape normal (D69: 1.0 drew bars on ashlar)

vec3 vvTerrainTorchNormal(VvTerrainMaterial m, vec2 materialUv)
{
    if (m.holdsWater < 0.5) return m.normal;
    vec2 texel = 1.0 / max(vec2(1.0), vec2(textureSize(vv_materialTex, 0)));
    float gx = 0.5 * (vvTerrainHeightAt(materialUv + vec2(texel.x, 0.0)) - vvTerrainHeightAt(materialUv - vec2(texel.x, 0.0)));
    float gy = 0.5 * (vvTerrainHeightAt(materialUv + vec2(0.0, texel.y)) - vvTerrainHeightAt(materialUv - vec2(0.0, texel.y)));
    // Same convention as the primary atlas normal (PbrMapGenerator: -gx, +gy).
    vec2 xy = vec2(-gx, gy) * VV_TERRAIN_TORCH_SHAPE;
    return vvTerrainSafeNormalize(vvTerrainFrame(m.faceNormal) * vec3(xy, 1.0), m.faceNormal);
}

// How much more or less torchlight this texel takes than the flat face, as a
// share of it: 0 on a flat, unoccluded texel.
float vvTerrainTorchShade(VvTerrainMaterial m, vec3 toTorch, vec3 reliefNormal)
{
    vec3 l = vvTerrainSafeNormalize(mix(m.faceNormal, toTorch, VV_TERRAIN_TORCH_TILT), m.faceNormal);
    // The softening is for the luminance normal, which is mostly grain (D66).
    // The stone shape is coherent by construction and is used as it is.
    float soften = m.holdsWater > 0.5 ? VV_TERRAIN_TORCH_SHAPE_SOFTEN : VV_TERRAIN_TORCH_NORMAL;
    vec3 n = vvTerrainSafeNormalize(mix(m.faceNormal, reliefNormal, soften), m.faceNormal);
    float delta = max(dot(n, l), 0.0) - max(dot(m.faceNormal, l), 0.0);
    float hollow = mix(1.0, m.ao, VV_TERRAIN_TORCH_HOLLOW) - 1.0;
    float direct = clamp((1.0 - dot(toTorch, m.faceNormal)) * VV_TERRAIN_TORCH_DIRECT, 0.0, 1.0);
    hollow *= mix(VV_TERRAIN_TORCH_HOLLOW_FAR, 1.0, direct);
    return delta + hollow;
}

vec3 vvTerrainBlockRelief(VvTerrainMaterial m, vec3 litColor, vec3 blockLight, vec3 cameraRelativePos,
                          vec2 materialUv)
{
    float intensity = dot(blockLight, VV_PBR_LUMA);
    vec3 toTorch = vvBlockLightDirection(m.faceNormal, cameraRelativePos, intensity);
    float strength = clamp(vv_pbrBlockLight, 0.0, 1.0) * VV_TERRAIN_TORCH_RELIEF * m.detailFade;
    if (strength < 0.001 || intensity < 0.004) return vec3(0.0);

    float shade = vvTerrainTorchShade(m, toTorch, vvTerrainTorchNormal(m, materialUv));
    vec3 relief = m.baseColor * blockLight * (shade * strength * (1.0 - m.metalness));
    return clamp(relief, -litColor * VV_TERRAIN_TORCH_RELIEF_CAP_DARK, litColor * VV_TERRAIN_TORCH_RELIEF_CAP);
}

// ---------------------------------------------------------------------------
// Pixel reflection on the modular path (reflections stage two, D62).
//
// Ported from the legacy patch (pseudopbr.glsl, vvPixelReflection and
// vvSceneReflection), whose method stands: the reflection is the IMAGE the
// ambient highlight is lit by, not a term of its own, so Fresnel, metalness,
// roughness, specular occlusion and wetness decide how much of it shows,
// exactly as they decided how much of the flat sky colour showed (D13).
//
// It is pixel art by construction rather than by rounding afterwards: the ray
// leaves from the centre of the material texel, so every fragment of one
// texel marches the identical ray and lands on the identical colour; and the
// analytic environment is looked up in direction CELLS, fewer of them on a
// rougher surface, so a rough face sees broad patches and never a blur.
//
// What is different here is the budget. The legacy march ran on every opaque
// fragment. This one runs only where its answer can reach the image: a texel
// whose ambient highlight is too weak to show a reflection (most soil, grass,
// rough stone and dry wood - Fresnel times the roughness fade) takes the
// analytic image alone, and the march is skipped past VV_TREFLECT_MARCH_FAR
// blocks. Wet stone, metal, polished and glazed blocks are where it runs.
// ---------------------------------------------------------------------------

// The sky, the direction cells, the weight fit and the march are the shared
// core's (reflect.glsl, D81), so terrain and water cannot drift apart.
const float VV_TREFLECT_MAX = 2.0;           // capture colour ceiling vs. the environment

// How far the scene hit is softened by roughness, in capture texels (the
// capture is half resolution): none on a mirror, this many from the middle
// of the range up. Four taps, deterministic.
const float VV_TREFLECT_BLUR_TEXELS = 5.0;
const float VV_TREFLECT_BLUR_SHARP = 0.06;   // at or below: a crisp picture
const float VV_TREFLECT_BLUR_SOFT = 0.55;    // at or above: the full blur

// The march is worth its cost only where the reflected image can change the
// pixel visibly. This is the luminance of the reflection's own weight -
// strength x Fresnel x roughness fade x specular occlusion x the ambient
// slider - below which a texel keeps the analytic image. 0.025 is about one
// 8-bit step on a mid-grey reflection; a dry granite block sits near 0.01, a
// wet one near 0.06, a polished metal far above.
const float VV_TREFLECT_MARCH_WEIGHT = 0.025;
const float VV_TREFLECT_MARCH_NEAR = 32.0;   // full scene reflection inside this
const float VV_TREFLECT_MARCH_FAR = 48.0;    // none past this: the march's own range

// Feedback guard, as legacy: the capture is last frame's finished image, so a
// reflective floor filling the screen can reflect its own brightened copy.
const float VV_TREFLECT_FEEDBACK_KEEP = 0.35;
const float VV_TREFLECT_FEEDBACK_BRIGHT_START = 1.10;
const float VV_TREFLECT_FEEDBACK_BRIGHT_END = 1.90;
const float VV_TREFLECT_FEEDBACK_HUE_START = 0.18;
const float VV_TREFLECT_FEEDBACK_HUE_END = 0.42;

float vvTerrainLuma(vec3 c)
{
    return dot(c, vec3(0.2126, 0.7152, 0.0722));
}

// The world position of the centre of this fragment's material texel, from
// the Jacobian of position against UV that screen derivatives give. Falls back
// to the fragment itself where that is singular (a face seen edge-on), where
// the texel is sub-pixel anyway.
vec3 vvTerrainTexelCentrePos(vec3 cameraRelativePos, vec2 materialUv)
{
    vec2 duv = vvTerrainSnap(materialUv) - materialUv;
    vec2 uvdx = dFdx(materialUv);
    vec2 uvdy = dFdy(materialUv);

    float det = uvdx.x * uvdy.y - uvdx.y * uvdy.x;
    if (abs(det) < 1e-12) return cameraRelativePos;

    float a = (duv.x * uvdy.y - duv.y * uvdy.x) / det;
    float b = (uvdx.x * duv.y - uvdx.y * duv.x) / det;
    return cameraRelativePos + dFdx(cameraRelativePos) * a + dFdy(cameraRelativePos) * b;
}

// How much of the reflected image can reach the pixel (phase 1, D81): the
// split-sum share of the environment at the REFLECTION roughness, times where
// that reads as a picture, times specular occlusion and the slider. Shared so
// the march gate and the shading can never disagree about what is visible.
//
// It replaced F x (1 - r)^2, which was two-thirds of Fresnel at the 0.18
// floor every texel had, so still water and wet stone reflected at most a
// fifth of what they should.
//
// The slider is read RELATIVE to its shipped default, so the default is the
// physical answer here. The same uniform scales the entity and particle sky
// highlight, which keep the meaning they were tuned with; one slider, one
// look per program, and 0 still means none anywhere.
const float VV_TERRAIN_AMBIENT_NEUTRAL = 0.35; // = PseudoPBR.AmbientSpecular's default

float vvTerrainReflectSlider()
{
    return max(0.0, vv_pbrAmbient) / VV_TERRAIN_AMBIENT_NEUTRAL;
}

vec3 vvTerrainAmbientWeight(VvTerrainMaterial m, vec3 viewDir)
{
    vec3 f0 = mix(vec3(VV_DIELECTRIC_F0) * mix(0.6, 1.8, m.specularFactor),
                  m.baseColor, m.metalness);
    float ndv = max(dot(m.normal, viewDir), 0.0);
    float rr = m.reflectRoughness;
    vec3 env = vvReflectEnvBrdf(f0, rr, ndv);
    float mirror = vvReflectMirror(rr);
    float roughFade = (1.0 - rr) * (1.0 - rr);
    float specOcc = mix(m.ao, clamp(pow(max(m.ao, 0.02), 1.0 + roughFade), 0.0, 1.0),
                        clamp(vv_pbrSpecOcclusion, 0.0, 1.0));
    return env * mirror * specOcc * vvTerrainReflectSlider();
}

// The part of vvTerrainAmbientWeight that a glancing view adds over the
// head-on value: zero facing the surface, rising toward grazing. Diffuse gives
// this up, so the reflected and diffuse energy never add up past what arrived.
// The slider is clamped to 1 here: a stronger reflection slider is a choice of
// look, and must not drive the diffuse below zero.
vec3 vvTerrainGrazingShare(VvTerrainMaterial m, vec3 viewDir, vec3 f0)
{
    float ndv = max(dot(m.normal, viewDir), 0.0);
    float rr = m.reflectRoughness;
    float one = 1.0;
    vec3 env = vvReflectEnvBrdf(f0, rr, ndv);
    vec3 envHeadOn = vvReflectEnvBrdf(f0, rr, one);
    float mirror = vvReflectMirror(rr);
    float roughFade = (1.0 - rr) * (1.0 - rr);
    float specOcc = mix(m.ao, clamp(pow(max(m.ao, 0.02), 1.0 + roughFade), 0.0, 1.0),
                        clamp(vv_pbrSpecOcclusion, 0.0, 1.0));
    return max(env - envHeadOn, vec3(0.0)) * mirror * specOcc * min(vvTerrainReflectSlider(), 1.0);
}

// The scene lookup, or a miss saying why there was none. The march itself
// lives in pbrterrainreflect, which owns the capture.
VvReflectHit vvTerrainSceneLookup(vec3 origin, vec3 r, vec3 v, bool worthIt, float blur)
{
    VvReflectHit miss;
    miss.color = vec3(0.0);
    miss.valid = 0.0;
    miss.travel = 0.0;
    miss.reason = VV_REFLECT_NO_SOURCE;
#ifdef VV_TERRAIN_SCENE_CAPTURE
    if (!worthIt) { miss.reason = VV_REFLECT_SKIPPED; return miss; }
    return vvTerrainSceneMarch(origin, r, v, blur);
#else
    return miss;
#endif
}

struct VvTerrainReflection
{
    vec3 image;      // what the texel sees: scene where it can, analytic where not
    vec3 direction;  // the texel's reflection direction (unquantised)
    float cells;     // direction cells across elevation
    float scene;     // 0..1, how much of the image is the captured scene
    float reason;    // VV_REFLECT_* of the march
};

// Strength 0 returns the flat environment the path used before, so the slider
// at 0 is the previous build bit for bit.
VvTerrainReflection vvTerrainPixelReflection(VvTerrainMaterial m, vec3 cameraRelativePos,
                                             vec2 materialUv, vec3 environment,
                                             vec3 surfaceColor, vec3 viewDir, vec3 blockLight)
{
    VvTerrainReflection o;
    vec3 rFlat = reflect(-viewDir, m.normal);
    o.image = mix(environment * 0.48, environment, clamp(rFlat.y * 0.5 + 0.5, 0.0, 1.0));
    o.direction = rFlat;
    o.cells = vvReflectCells(m.reflectRoughness);
    o.scene = 0.0;
    o.reason = VV_REFLECT_SKIPPED;

    float strength = clamp(vv_pbrPixelReflect, 0.0, 1.0);
    if (strength < 0.001) return o;

    // The view vector is the TEXEL's, which is what makes the reflection one
    // colour per texture pixel rather than a gradient inside it.
    vec3 origin = vvTerrainTexelCentrePos(cameraRelativePos, materialUv);
    vec3 v = vvTerrainSafeNormalize(vvTerrainToEye(origin), viewDir);
    vec3 r = vvTerrainSafeNormalize(reflect(-v, m.normal), m.normal);
    o.direction = r;

    vec3 fallback = vvReflectSky(vvReflectCell(r, o.cells), environment, lightPosition, vv_sceneDayLight);

    // The scene is traced from the block FACE normal, not the material one:
    // it is geometry, and normal-map grain would steer rays into the floor.
    vec3 sceneR = vvTerrainSafeNormalize(reflect(-v, m.faceNormal), m.faceNormal);
    float distance = vvTerrainEyeDistance(cameraRelativePos);
    float reach = 1.0 - smoothstep(VV_TREFLECT_MARCH_NEAR, VV_TREFLECT_MARCH_FAR, distance);
    float weight = vvTerrainLuma(vvTerrainAmbientWeight(m, viewDir)) * strength;
    bool worthIt = reach > 0.0 && weight >= VV_TREFLECT_MARCH_WEIGHT;

    float blur = VV_TREFLECT_BLUR_TEXELS
               * smoothstep(VV_TREFLECT_BLUR_SHARP, VV_TREFLECT_BLUR_SOFT, m.reflectRoughness);
    VvReflectHit hit = vvTerrainSceneLookup(origin, sceneR, v, worthIt, blur);
    o.reason = hit.reason;

    // The capture is the finished, graded frame: cap it by LUMINANCE against
    // the environment (keeping its hue), so a bright sky in the capture cannot
    // push a metal past what the ambient term could ever have produced.
    vec3 sceneColor = hit.color;
    float sceneLuma = vvTerrainLuma(sceneColor);
    float envLuma = vvTerrainLuma(environment);
    // The ceiling follows the brighter of the sky and the local block light:
    // by night or underground the sky colour is near black, and capping a
    // reflected torch or forge against it erased exactly the reflection a wet
    // cave floor should show (D64).
    float ceiling = max(envLuma, vvTerrainLuma(blockLight)) * VV_TREFLECT_MAX;
    // A soft knee, not a wall (phase 0): below the ceiling a hit keeps its
    // full brightness, above it the excess is compressed and can never pass
    // twice the ceiling. The hard clamp took a sunlit tree down by half; the
    // knee still stops bloom in the capture from whitening a metal.
    if (sceneLuma > ceiling && ceiling > 1e-5)
    {
        float over = (sceneLuma - ceiling) / ceiling;
        float kneed = ceiling * (1.0 + over / (1.0 + over));
        sceneColor *= kneed / sceneLuma;
        sceneLuma = kneed;
    }

    float surfaceLuma = vvTerrainLuma(surfaceColor);
    float hueMatch = 1.0 - smoothstep(VV_TREFLECT_FEEDBACK_HUE_START, VV_TREFLECT_FEEDBACK_HUE_END,
                                      length(sceneColor / max(sceneLuma, 1e-4)
                                           - surfaceColor / max(surfaceLuma, 1e-4)));
    float overBright = smoothstep(envLuma * VV_TREFLECT_FEEDBACK_BRIGHT_START,
                                  envLuma * VV_TREFLECT_FEEDBACK_BRIGHT_END, sceneLuma);
    float confidence = clamp(hit.valid, 0.0, 1.0) * reach;
    confidence *= mix(1.0, VV_TREFLECT_FEEDBACK_KEEP, hueMatch * overBright * confidence);

    o.scene = confidence;
    o.image = mix(o.image, mix(fallback, sceneColor, confidence), strength);
    return o;
}

vec3 vvTerrainAmbientSpecular(VvTerrainMaterial m, vec3 viewDir, vec3 reflectedImage)
{
    return reflectedImage * vvTerrainAmbientWeight(m, viewDir);
}

vec3 vvTerrainApplySurfaceLayers(VvTerrainMaterial m, vec3 diffuse)
{
    // Snow and frost displace water, as in the shared layer stack.
    float wet = m.wetness * clamp(1.0 - m.snow - m.frost, 0.0, 1.0);

    diffuse *= mix(1.0, VV_LAYER_WET_DARKEN, wet * (1.0 - m.metalness));
    // Standing and running water darken further than a damp film, and lean a
    // touch cool: the colour of the sky it now shows instead of the stone.
    float water = max(m.pool, m.stream) * clamp(1.0 - m.snow - m.frost, 0.0, 1.0) * (1.0 - m.metalness);
    diffuse *= mix(vec3(1.0), vec3(0.94, 0.97, 1.0) * VV_TERRAIN_POOL_DARKEN, water);
    diffuse = mix(diffuse, vec3(dot(diffuse, vec3(0.299, 0.587, 0.114))) * 1.22, m.snow * 0.7);
    diffuse = mix(diffuse, diffuse * vec3(0.86, 0.92, 1.0), m.frost * 0.45);
    return diffuse;
}

// Emission on terrain, through the ONE emission model in pbrcore.glsl.
//
// The atlas mask can only remove emission from a block vanilla already lights
// (glowLevel is the game's own answer to "does this glow"), and every slider
// reaches it: Emissive strength 0 makes both of these return exactly 0. The
// first restoration checkpoint added glowLevel * mask to the bloom with no
// slider in the loop, so glowing blocks bloomed at up to twice vanilla's level
// and nothing could turn it down.
// ---------------------------------------------------------------------------
// Sun visibility and the canopy, on the modular terrain path
//
// A port of the legacy patch's canopy model, not a second one. Everything here
// reads vanilla's own shadow map, which resolves individual leaf gaps, so the
// pattern on the forest floor is the real canopy's; nothing below invents one.
// ---------------------------------------------------------------------------

const float VV_TERRAIN_SUN_PRESENCE_DAWN = 0.18;
// How much light the canopy takes from the shade between flecks at full
// slider, the colour of what lights that shade instead, and the distance
// over which the shadow-map count stops resolving canopy structure.
const float VV_TERRAIN_DAPPLE_SHADE = 0.28;
// How much deeper canopy shade is under a sun near the horizon: the floor of
// a forest at golden hour is dark, which is what makes its flecks and beams
// read (the player's reference photographs). Still bounded by the caller's
// cap, so the shade cannot pass it however low the sun.
const float VV_TERRAIN_DAPPLE_LOW_SUN = 1.6;
// v9: the shade between flecks is lit by the SKY, not by the sun that cast
// it, so it is cooler and less saturated than the sunlit floor. Vanilla's
// shadow only halves the sunlit colour, keeping the sun's warmth, and v8 then
// pushed it green: on the autumn palette the shade read murky olive and the
// flecks, the same hue only brighter, read as yellow paint rather than light
// (in-game screenshot, 13:00, 2026-11-23). SKY cools the shaded fraction;
// MUTE moves it toward its own luminance, as a broad sky fill does. Both
// scale with the canopy term, so they vanish with it - at zero slider, under
// overcast and in the open - and the result is clamped to the colour vanilla
// lit, so no channel is ever brighter than it was.
const float VV_TERRAIN_DAPPLE_SKY = 0.10;
const float VV_TERRAIN_DAPPLE_MUTE = 0.8;
const float VV_TERRAIN_DAPPLE_FADE_START = 22.0;
const float VV_TERRAIN_DAPPLE_FADE_RANGE = 22.0;
// How much of the dapple's full shade a solid crown takes, known only from the
// leaf map. Below 1 so the measured broken shade stays the stronger reading.
const float VV_TERRAIN_LEAF_SHADE = 0.7;
// Sunflecks: the field's coarse octave and the regional grid thin patches
// gather on, on the canopy plane (both must divide the 4096 world wrap
// exactly: 4096 / 1.024 = 4000, 4096 / 8.192 = 500; the half and double
// cells divide it too), the least stretch a low sun may apply, texels per
// block, and the distance flecks dissolve over - finished before the leaf
// map's 28-block reach runs out.
const float VV_TERRAIN_FLECK_CELL = 1.024;
const float VV_TERRAIN_FLECK_PERIOD = 4000.0;
const float VV_TERRAIN_FLECK_CLUSTER_CELL = 8.192;
const float VV_TERRAIN_FLECK_CLUSTER_PERIOD = 500.0;
const float VV_TERRAIN_FLECK_MIN_STRETCH = 0.3;
const float VV_TERRAIN_FLECK_TEXELS = 16.0;
// The lit share of the floor under a closed and under an open crown.
const float VV_TERRAIN_FLECK_COVER_CLOSED = 0.04;
const float VV_TERRAIN_FLECK_COVER_OPEN = 0.55;
// How far the summed noise is expanded before the cut, so the lit share
// tracks the coverage asked for; how far the field is warped, in blocks;
// how much of a coarse cell per unit of lost elevation a streak reaches.
const float VV_TERRAIN_FLECK_CONTRAST = 2.2;
const float VV_TERRAIN_FLECK_WARP = 0.4;
const float VV_TERRAIN_FLECK_STREAK = 0.35;
// How hard the slider drives a fleck (the default 0.6 reaches a full gap at
// 1.6), and how much richer a sunlit patch is than the shade around it: a
// touch, so it reads as grass in the sun rather than a pale stain (v4 read as
// dry grass), and not the 1.35 that read as neon in v3. The smooth wash v4
// laid under thin crowns is gone: it lifted the shade toward grey. The fleck
// distance is HORIZONTAL (the leaf map's own measure), so flying above a
// forest does not fade it.
const float VV_TERRAIN_FLECK_GAIN = 1.6;
const float VV_TERRAIN_FLECK_SATURATION = 1.0;
// Bounce light at a lit patch's edge, at most this share of the way to the
// lit colour.
const float VV_TERRAIN_FLECK_BOUNCE = 0.07;
// How far the threshold wanders per texel, which steps the edge.
const float VV_TERRAIN_FLECK_RAGGED = 0.03;
// The fine octave: quarter-block cells, tiling the 4096-block world wrap in
// 16000 cells; per-texel jitter tiles it in 65536 texels.
const float VV_TERRAIN_FLECK_LOBE_CELL = 0.256;
const float VV_TERRAIN_FLECK_LOBE_PERIOD = 16000.0;
const float VV_TERRAIN_FLECK_TEXEL_PERIOD = 65536.0;
// Cloud cover over which the flecks close: an image of the sun needs the sun.
const float VV_TERRAIN_FLECK_OVERCAST_START = 0.45;
const float VV_TERRAIN_FLECK_OVERCAST_END = 0.65;
// The moon's flecks at full slider and full moon, as a share of the sun's.
const float VV_TERRAIN_MOON_FLECK = 0.45;
// The most a shaft may mark vanilla's god-ray channel, so a beam can never
// hide what is behind it.
const float VV_TERRAIN_SHAFT_MAX = 0.4;
// v10: how hard the slider drives a shaft source before the cap. Sources are
// only the texels lining a hole in a leaf (v7), a sparse lace that vanilla's
// radial blur averages with the dark leaf around it, so at the v8 strength
// (a few hundredths per texel) no ray survived the blur (in game, v9: "not
// seeing any rays"). Sparse sources can take a high cap without washing the
// forest out; whole leaf faces could not (v6).
// v13: back down to 1 under a cap of 0.4. The air march (v11) now draws the
// beams; at 2.5 / 0.8 the radial blur of the sparse rim sources hatched the
// whole view near a low sun with fine lines (in game, v12).
const float VV_TERRAIN_SHAFT_GAIN = 1.0;
// v11 beams in the air. The god-ray channel above is blurred toward the
// sun's SCREEN position, so it can only draw rays while the sun is on or near
// the screen - in game that meant a low sun straight ahead, and nothing at
// midday (v10: "during mid-day I really can't notice anything"). Real shafts
// are light scattered by the air inside each beam, visible from the side at
// any sun height. The beam march integrates that along the view ray: REACH
// blocks of air in STEPS samples, SCATTER the light a block of full beam adds
// (as a share of the sunlit colour), MAX the most any fragment takes.
// v12: v11 took 12 full fleck-field evaluations per step budget (about 500
// hashes a fragment) on every overlapping leaf layer and dropped the game
// below 30 fps looking at the beams; and at the default slider they were
// hard to see. Eight steps over 20 blocks, a two-read beam pattern
// (vvTerrainBeamDensity), and a stronger scatter.
const float VV_TERRAIN_BEAM_REACH = 20.0;
const int VV_TERRAIN_BEAM_STEPS = 8;
const float VV_TERRAIN_BEAM_SCATTER = 0.2;
const float VV_TERRAIN_BEAM_MAX = 0.32;
// v13 gates: the least leaf density that makes a column shaded air a beam
// can show in, and the density of a crown that blocks a beam passing through
// it on the way down.
const float VV_TERRAIN_BEAM_CROWN = 0.3;
const float VV_TERRAIN_BEAM_BLOCK = 0.45;
// Past this distance a leaf texel is under a pixel, and the hole-rim test
// that places shaft sources would sparkle.
const float VV_TERRAIN_SHAFT_RIM_FAR = 32.0;
// v10 fleck brightness. A gap's image is as bright as the gap is wide: a
// patch that only just clears the cut is a small gap's dim, soft image, the
// heart of a big one is the full sun. DIM is the faintest lit patch as a
// share of a full fleck; CORE_WIDE and CORE_NARROW are how far above the cut
// (in the field's 0..1 units) a patch reaches full brightness outside and
// inside a beam group; BEAM_CELL is the grid beam groups gather on
// (4096 / 4.096 = 1000 cells); LEVELS steps the ramp, so a patch has a few
// flat tones with pixel-art edges rather than a blur (the v5 defect).
const float VV_TERRAIN_FLECK_DIM = 0.3;
const float VV_TERRAIN_FLECK_CORE_WIDE = 0.4;
const float VV_TERRAIN_FLECK_CORE_NARROW = 0.1;
const float VV_TERRAIN_FLECK_BEAM_CELL = 4.096;
const float VV_TERRAIN_FLECK_BEAM_PERIOD = 1000.0;
const float VV_TERRAIN_FLECK_LEVELS = 3.0;
// v10: the faint bloom a fleck adds to vanilla's glow channel at full
// brightness against the deepest shade. Vanilla's own sunlit-topsoil glow is
// 0.1 x shadowIntensity; this stays under it.
const float VV_TERRAIN_FLECK_GLOW = 0.08;
// The fleck's share of vanilla's glow channel at this fragment, set by
// vvTerrainCanopyResolve and added at the call site. 0 means none.
float vv_fleckGlow = 0.0;
// Vanilla's normal shade (nb) at this fragment, set by the call site before
// the material runs; see vvTerrainSunRelight. 1 means none, the old formula.
float vv_vanillaNormalShade = 1.0;
const float VV_TERRAIN_FLECK_FADE_START = 18.0;
const float VV_TERRAIN_FLECK_FADE_RANGE = 8.0;
// Past this straight-line distance a block texel is under a pixel and a
// stepped fleck would shimmer, whatever the horizontal distance says.
const float VV_TERRAIN_FLECK_VIEW_LIMIT = 40.0;

// How much of the SUN reaches this fragment, geometrically: 1 unoccluded, 0
// fully blocked.
//
// Read from the shadow map directly rather than from vanilla's brightness `b`,
// for two reasons that were both defects on this path. `b` bottoms out at
// 1 - shadowIntensity/2, not at 0, so a leaf in full shade read as half lit;
// and `b` already has blockBrightness added, so a torch under a tree read as
// sunlight. Not scaled by shadowIntensity either: that is how dark the game
// draws a shadow, not where the light goes.
float vvTerrainSunVisibility()
{
#if SHADOWQUALITY > 0
    float occlusion = 0.0;
    if (shadowCoordsFar.w > 0.0)
    {
        float total = 9.0;
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                total -= texture(shadowMapFar,
                                 vec3(shadowCoordsFar.xy + vec2(float(x) * shadowMapWidthInv,
                                                                float(y) * shadowMapHeightInv),
                                      shadowCoordsFar.z - 0.0009));
        occlusion += (total / 9.0) * shadowCoordsFar.w;
    }
#if SHADOWQUALITY > 1
    if (shadowCoordsNear.w > 0.0)
    {
        float total = 9.0;
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                total -= texture(shadowMapNear,
                                 vec3(shadowCoordsNear.xy + vec2(float(x) * shadowMapWidthInv,
                                                                 float(y) * shadowMapHeightInv),
                                      shadowCoordsNear.z - 0.0005));
        occlusion += (total / 9.0) * shadowCoordsNear.w;
    }
#endif
    // The cascade weights are complementary by construction in the vertex
    // shader, so they sum to at most 1.
    return clamp(1.0 - occlusion, 0.0, 1.0);
#else
    // Shadows off: no occlusion data, and "fully lit" is the only answer that
    // degrades to vanilla rather than inventing a shadow.
    return 1.0;
#endif
}

// A gate, not a dimmer. Transmission and dapple peak at a LOW sun, so they
// must not be scaled by how high it is; this only closes after sunset.
float vvTerrainSunPresence()
{
    return smoothstep(0.0, VV_TERRAIN_SUN_PRESENCE_DAWN, clamp(vv_sceneDayLight, 0.0, 1.0));
}

// How much of the light here is local (torch, glow, point light) rather than
// sun. Vanilla computes it per vertex; a canopy blocks the sun, not a lantern.
float vvTerrainLocalLightShare()
{
#if SHADOWQUALITY > 0
    return clamp(blockBrightness, 0.0, 1.0);
#else
    return 0.0;
#endif
}

// The canopy is the occluder, so it never receives its own dapple; aquatic
// flora is attenuated by water, which vanilla already owns.
bool vvTerrainIsCanopyReceiver()
{
    int flora = vvTerrainFloraClass();
    return flora != VV_TFLORA_LEAVES && flora != VV_TFLORA_AQUATIC;
}

// How broken the shadow is on a ring of twelve shadow-map taps: one edge counts
// 2, so a wall scores 2 and a canopy scores many. Split from the threshold so
// debug view 31 can show the raw count when the gate shows nothing.
float vvTerrainCanopyVariation(float radiusTexels, out float mean)
{
    mean = 0.0;
#if SHADOWQUALITY > 0
    if (radiusTexels <= 0.0) return 0.0;
    vec2 texel = vec2(shadowMapWidthInv, shadowMapHeightInv) * radiusTexels;
#if SHADOWQUALITY > 1
    bool useNear = shadowCoordsNear.w > 0.0;
#else
    bool useNear = false;
#endif
    if (!useNear && shadowCoordsFar.w <= 0.0) return 0.0;

    float first = 0.0;
    float previous = 0.0;
    float variation = 0.0;
    float sum = 0.0;
    for (int i = 0; i < 12; i++)
    {
        float angle = float(i) * (6.28318530718 / 12.0);
        vec2 offset = vec2(cos(angle), sin(angle)) * texel;
        float lit;
#if SHADOWQUALITY > 1
        if (useNear)
            lit = texture(shadowMapNear, vec3(shadowCoordsNear.xy + offset, shadowCoordsNear.z - 0.0005));
        else
#endif
            lit = texture(shadowMapFar, vec3(shadowCoordsFar.xy + offset, shadowCoordsFar.z - 0.0009));

        if (i == 0) first = lit;
        else        variation += abs(lit - previous);
        previous = lit;
        sum += lit;
    }
    // Close the ring, or a feature at the seam is counted once.
    variation += abs(first - previous);
    mean = sum / 12.0;
    return variation;
#else
    return 0.0;
#endif
}

// The count thresholded: above a single edge, saturating at three features,
// and only on a ring that is genuinely mixed (a tangent clip is not a canopy).
float vvTerrainCanopyStructure(float radiusTexels)
{
    float mean;
    float variation = vvTerrainCanopyVariation(radiusTexels, mean);
    if (variation <= 0.0) return 0.0;
    float features = smoothstep(2.6, 6.0, variation);
    float mixed = clamp(4.0 * mean * (1.0 - mean), 0.0, 1.0);
    return clamp(features * sqrt(mixed), 0.0, 1.0);
}

// Whether a CANOPY is what blocks the sun here: shadowed, and broken at leaf
// scale overhead. A radius of zero disables the test.
float vvTerrainCanopyEvidence(float sunVisibility)
{
    float shadowed = 1.0 - clamp(sunVisibility, 0.0, 1.0);
    if (shadowed < 0.01) return 0.0;
    return clamp(vvTerrainCanopyStructure(clamp(vv_pbrCanopyRadius, 0.0, 16.0)) * shadowed, 0.0, 1.0);
}

// How much light the canopy takes away here, 0..1. A SUBTRACTION only: a lit
// gap is already at vanilla's full sun with no headroom above it, and the
// additive version once blew the forest floor out through the bloom pass. The
// flecks get their contrast by being the only thing that did not get darker.
//
// Pass a sun visibility or evidence below zero to have it measured here,
// after the cheap early-outs, so a fragment the dapple cannot reach costs no
// shadow taps. The resolve measures the evidence once and shares it with the
// flecks, which place their pools on it.
float vvTerrainCanopyDapple(vec3 cameraRelativePos, float fade, float sunVisibility, float leafOverhead, float evidence)
{
    if (vv_pbrDapple < 0.001) return 0.0;
    if (!vvTerrainIsCanopyReceiver()) return 0.0;

    float resolvable = clamp(1.0 - (vvTerrainEyeDistance(cameraRelativePos) - VV_TERRAIN_DAPPLE_FADE_START)
                                   / VV_TERRAIN_DAPPLE_FADE_RANGE, 0.0, 1.0);
    if (resolvable * fade < 0.004) return 0.0;

    float sun = vvTerrainSunPresence();
    if (sun < 0.01) return 0.0;

    if (sunVisibility < 0.0) sunVisibility = vvTerrainSunVisibility();
    // Two kinds of evidence that foliage casts this shadow: the shadow map
    // broken at leaf scale (the ring count), and leaves on the leaf map along
    // the sun ray. Under a solid crown only the second exists.
    if (evidence < 0.0) evidence = vvTerrainCanopyEvidence(sunVisibility);
    float under = max(evidence,
                      clamp(leafOverhead, 0.0, 1.0) * (1.0 - clamp(sunVisibility, 0.0, 1.0)) * VV_TERRAIN_LEAF_SHADE);
    if (under < 0.001) return 0.0;

    // Graded by how thick the crown is overhead: a thin crown passes more
    // skylight between its leaves than a closed one, so its shade is lighter.
    // Without this the shade was one flat green from the trunk to the edge of
    // the clearing (in-game audit, 2026-11-23).
    float crown = mix(0.3, 1.0, smoothstep(0.1, 0.85, clamp(leafOverhead, 0.0, 1.0)));

    // Canopy shade is a shadow of the DIRECT sun, so it fades as the sky
    // diffuses it, exactly as the flecks do. Without this an overcast forest
    // took the weather's darkening and a full canopy shade on top of it.
    float overcast = clamp(vv_sceneOvercast, 0.0, 1.0);
    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));
    float lowSun = 1.0 - smoothstep(0.15, 0.5, l.y);
    return VV_TERRAIN_DAPPLE_SHADE * resolvable * fade * under * crown * sun * mix(1.0, VV_OVERCAST_DIRECT, overcast)
         * mix(1.0, VV_TERRAIN_DAPPLE_LOW_SUN, lowSun) * clamp(vv_pbrDapple, 0.0, 2.0);
}

// Applies the canopy term: darker, and on the shaded fraction only, cooler
// and muted toward skylight. Torchlight did not come from the sky, so the
// local share is spared.
vec3 vvTerrainApplyCanopy(vec3 color, float shaded, float cap)
{
    float canopy = clamp(shaded, 0.0, cap) * (1.0 - vvTerrainLocalLightShare());
    if (canopy <= 0.0) return color;
    vec3 dark = color * (1.0 - canopy);
    float y = dot(dark, vec3(0.2126, 0.7152, 0.0722));
    dark = mix(dark, vec3(y), clamp(canopy * VV_TERRAIN_DAPPLE_MUTE, 0.0, 0.5));
    float tint = canopy * VV_TERRAIN_DAPPLE_SKY;
    return min(dark * vec3(1.0 - tint, 1.0 - tint * 0.4, 1.0 + tint * 0.3), color);
}

// ---------------------------------------------------------------------------
// Sunflecks inside canopy shade
//
// The GATE is game data: the fragment is in the sun's shadow (shadow map) AND
// the sun ray from it passes through leaf blocks (leaf map). The PATTERN is
// invented - DATA GAP: Vintage Story does not resolve the gaps inside a crown,
// so where a fleck lands is a stable hash on the canopy plane, not a real gap.
// It is projected along the real sun direction, so it slides as the sun moves,
// stretches into ovals when the sun is low, and sways with the game's wind.
//
// A fleck only RESTORES what the shadow took: it lifts the fragment back to
// vanilla's own sunlit level and never above, so it cannot blow out through
// bloom or hide anything a player needs to see.
// ---------------------------------------------------------------------------

// x: leaf density 0..1 (0 no leaves), y: world Y of the lowest leaf.
vec2 vvTerrainCanopyCell(vec2 worldXZ)
{
    ivec2 c = ivec2(floor(worldXZ / 2.0));
    // Wrapped with a mask, not %: GLSL leaves % undefined for a negative
    // operand, and an integer divide is many instructions on most GPUs while
    // this runs dozens of times per fragment under a crown. Identical result
    // to the positive modulo for every int (two's complement).
    int ix = c.x & 31;
    int iz = c.y & 31;
    int i = iz * 32 + ix;
    float cellPair = vv_canopyMap[i >> 3][(i >> 1) & 3];
    float code = (i & 1) == 0 ? mod(cellPair, 4096.0) : floor(cellPair / 4096.0);
    return vec2(floor(code / 512.0) / 7.0, mod(code, 512.0));
}

// How much of one cell's crown a sun ray at height rayY passes through, as a
// density 0..1, SOFT in height: the crown is taken to start at its lowest
// leaf and run about as deep as its leaf count, and to sit above the
// receiver. A hard test here flipped whole cells on and off as the ray's
// step crossed a leaf's height, which drew square, axis-aligned flecks
// under a low sun (seen in game, v4).
float vvTerrainCanopyLeafAt(vec2 worldXZ, float rayY, float floorY)
{
    vec2 cell = vvTerrainCanopyCell(worldXZ);
    if (cell.x <= 0.0) return 0.0;
    float top = cell.y + max(2.0, cell.x * 5.0);
    float inCrown = smoothstep(cell.y - 1.5, cell.y + 0.5, rayY) * (1.0 - smoothstep(top, top + 2.0, rayY));
    float above = smoothstep(floorY + 0.5, floorY + 2.5, cell.y);
    return cell.x * inCrown * above;
}

// The leaves the sun ray from this point passes through: x density, y the
// height above the point of the step that saw the most (debug view only),
// z the same density BEFORE the fade toward the map's edge. That fade is
// measured from the camera, so it changes as the player moves; z is for
// anything thresholded (the fleck cut), where a moving input pops edges -
// the v9 "spots jitter while flying" (in game, 2026-11-23).
// Zero when there are none, when the map is absent, or when the ray leaves the
// map (which is toroidal, so reading past its edge would return another
// place's leaves).
//
// CONTINUOUS in the fragment's position, because every straight line in a
// fleck has come from a step here: the four nearest cells are blended with a
// smooth weight, the height test is soft, every step is taken and the
// strongest kept (an early return switched steps between neighbouring
// fragments), and the density eases out before the map's edge.
vec3 vvTerrainCanopyOverhead(vec3 cameraRelativePos)
{
    if (vv_canopyMapValid < 0.5) return vec3(0.0);
    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));
    if (l.y < 0.06) return vec3(0.0);

    vec3 world = cameraRelativePos + vv_pbrOrigin;
    vec2 best = vec2(0.0);
    float raw = 0.0;
    for (int k = 1; k <= 10; k++)
    {
        float t = float(k) * 2.0 / l.y;
        vec3 rel = cameraRelativePos + l * t;
        float reach = max(abs(rel.x), abs(rel.z));
        if (reach > 28.0) break;
        vec3 p = world + l * t;

        vec2 g = p.xz / 2.0 - 0.5;
        vec2 f = fract(g);
        f = f * f * (3.0 - 2.0 * f);
        vec2 o = (floor(g) + 0.5) * 2.0;
        float a = vvTerrainCanopyLeafAt(o, p.y, world.y);
        float b = vvTerrainCanopyLeafAt(o + vec2(2.0, 0.0), p.y, world.y);
        float c = vvTerrainCanopyLeafAt(o + vec2(0.0, 2.0), p.y, world.y);
        float d = vvTerrainCanopyLeafAt(o + vec2(2.0, 2.0), p.y, world.y);
        float leaves = mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
        float density = leaves * (1.0 - smoothstep(20.0, 28.0, reach));
        if (density > best.x) best = vec2(density, p.y - world.y);
        raw = max(raw, leaves);
    }
    return vec3(best, raw);
}

// Value noise on the canopy plane, 0..1, on a grid of `cell` blocks that
// tiles the world wrap in `period` cells.
float vvTerrainFleckNoise(vec2 src, float cell, float period, vec2 key)
{
    vec2 g = src / cell;
    vec2 i = floor(g);
    vec2 f = fract(g);
    f = f * f * (3.0 - 2.0 * f);
    float a = vvTerrainCellRandom(mod(i, period) + key).x;
    float b = vvTerrainCellRandom(mod(i + vec2(1.0, 0.0), period) + key).x;
    float c = vvTerrainCellRandom(mod(i + vec2(0.0, 1.0), period) + key).x;
    float d = vvTerrainCellRandom(mod(i + vec2(1.0, 1.0), period) + key).x;
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// How far the sun is diffused, 1 a clear sky and 0 a solid one, from the
// game's own cloud tiles. A sunfleck is an image of the sun's disc; once the
// disc is gone behind cloud there is nothing to image, so the flecks close
// between 45% and 65% cover. Cover 0 - also an unset uniform - is clear.
float vvTerrainClearSky()
{
    return 1.0 - smoothstep(VV_TERRAIN_FLECK_OVERCAST_START, VV_TERRAIN_FLECK_OVERCAST_END,
                            clamp(vv_sceneSkyCover, 0.0, 1.0));
}

// Leaf flutter in the game's wind, as a 0..1 strength: 0 is still air. The
// wobble runs on whole turns of the 26-second breeze clock, so a change of
// wind changes the mix of two fixed rhythms rather than the speed of one,
// which would jump every phase at once.
float vvTerrainFleckShimmer()
{
    return clamp(vv_sceneWind, 0.0, 1.0);
}

// v14 travelling gusts. The game gives one wind per player (speed and
// direction, EnvironmentState); a gust is a patch of stronger wind carried
// DOWNWIND across the land at the breeze clock's pace, which is how a gust
// crosses a meadow or a crown. FOUNDATION ONLY in the sense of rule 10: the
// game has no gust field, so where a gust is drawn is invented; its direction,
// its speed and whether there is wind at all are the game's.
//
// The field is value noise on a 16.384-block grid that tiles the 4096-block
// world wrap (250 cells), sampled twice and translated along the wind by
// TRAVEL blocks per turn of the breeze clock. Each copy fades out at the
// instant its own clock wraps and the other is at full weight, so the wrap of
// vv_sceneBreeze never shows (the two-phase flow-map construction). The grid
// is never rotated toward the wind: a rotated grid does not tile the wrap.
// The sum is re-expanded by the weights' norm before the cut, so a gust's
// coverage does not sag at the crossfade (the summed-noise rule).
const float VV_TERRAIN_GUST_CELL = 16.384;
const float VV_TERRAIN_GUST_PERIOD = 250.0;
const float VV_TERRAIN_GUST_TRAVEL = 96.0;
const float VV_TERRAIN_GUST_CONTRAST = 1.6;
// How much a full gust brightens the tip of a bending plant (the paler
// undersides and the cuticle turned toward the sky), and how much shimmer it
// adds to the flecks under a crown it crosses.
const float VV_TERRAIN_GUST_SHEEN = 0.14;
const float VV_TERRAIN_GUST_FLUTTER = 0.6;

vec2 vvTerrainWindDir()
{
    return dot(vv_pbrWindDir, vv_pbrWindDir) > 1e-4 ? normalize(vv_pbrWindDir) : vec2(1.0, 0.0);
}

const int VV_TERRAIN_GUST_TABLE = 16;   // cells per side of each copy's window

// One lattice value of one copy, from the table. `local` is the cell
// relative to that copy's window and must be inside it.
float vvTerrainGustLattice(ivec2 local, int copy)
{
    int k = local.y * VV_TERRAIN_GUST_TABLE + local.x;
    return vv_gustTable[copy * 64 + (k >> 2)][k & 3];
}

float vvTerrainGustNoise(vec2 worldXZ, float phase, vec2 key, int copy)
{
    vec2 p = worldXZ - vvTerrainWindDir() * (VV_TERRAIN_GUST_TRAVEL * (phase - 0.5));
    if (vv_gustTableValid > 0.5)
    {
        // Exactly vvTerrainFleckNoise's arithmetic, with the four corner
        // values read rather than hashed.
        vec2 g = p / VV_TERRAIN_GUST_CELL;
        vec2 i = floor(g);
        vec2 base = copy == 0 ? vv_gustBase.xy : vv_gustBase.zw;
        ivec2 local = ivec2(i - base);
        if (all(greaterThanEqual(local, ivec2(0))) && all(lessThan(local, ivec2(VV_TERRAIN_GUST_TABLE - 1))))
        {
            vec2 f = fract(g);
            f = f * f * (3.0 - 2.0 * f);
            float a = vvTerrainGustLattice(local, copy);
            float b = vvTerrainGustLattice(local + ivec2(1, 0), copy);
            float c = vvTerrainGustLattice(local + ivec2(0, 1), copy);
            float d = vvTerrainGustLattice(local + ivec2(1, 1), copy);
            return mix(mix(a, b, f.x), mix(c, d, f.x), f.y) - 0.5;
        }
    }
    return vvTerrainFleckNoise(p, VV_TERRAIN_GUST_CELL, VV_TERRAIN_GUST_PERIOD, key) - 0.5;
}

// 0 calm to 1 the heart of a gust, at a world position.
float vvTerrainGust(vec2 worldXZ)
{
    if (vv_pbrGusts < 0.001) return 0.0;
    float wind = smoothstep(0.08, 0.5, clamp(vv_sceneWind, 0.0, 1.0));
    if (wind <= 0.0) return 0.0;
    float a = fract(vv_sceneBreeze);
    float b = fract(vv_sceneBreeze + 0.5);
    float wa = 1.0 - abs(2.0 * a - 1.0);
    float wb = 1.0 - wa;
    float n = vvTerrainGustNoise(worldXZ, a, vec2(71.0, 23.0), 0) * wa
            + vvTerrainGustNoise(worldXZ, b, vec2(79.0, 47.0), 1) * wb;
    n = 0.5 + n * VV_TERRAIN_GUST_CONTRAST * inversesqrt(max(wa * wa + wb * wb, 0.5));
    return smoothstep(0.55, 0.8, n) * wind * clamp(vv_pbrGusts, 0.0, 1.0);
}

// The flecks' flutter at a place on the canopy plane: the game's wind, plus
// any gust crossing the crown there. Still air holds still whatever the gusts
// slider says, because a gust needs wind.
float vvTerrainFleckShimmerAt(vec2 src)
{
    return clamp(vv_sceneWind + VV_TERRAIN_GUST_FLUTTER * vvTerrainGust(src), 0.0, 1.0);
}

// The lit share of the floor asked of the fleck field for a crown this thin,
// 0 closed to 1 open. Kept away from both ends: a closed crown still has
// holes, and an open one is still a crown.
float vvTerrainFleckCoverage(float thin)
{
    return mix(VV_TERRAIN_FLECK_COVER_CLOSED, VV_TERRAIN_FLECK_COVER_OPEN, clamp(thin, 0.0, 1.0));
}

// The sun images through the gaps on the canopy plane.
//
// x: brightness, 0 off, VV_TERRAIN_FLECK_DIM to 1 on (v10; was 0 or 1).
// y: how close an unlit point is to lit ground, 0..1, for the bounce light.
// z: the lit share asked of the field here (its coverage), 0..1.
//
// ONE CONTINUOUS FIELD cut at a threshold, not a scatter of separate gaps
// (v7, from the player's reference photographs). Under a real crown, light
// and shade weave into one pattern: under a thin crown about half the floor
// is lit and the shade breaks into islands, under a closed crown it is shade
// with small holes, and in between the lit parts link into a network. A
// threshold on one field does all three by moving one number, so the
// threshold IS the crown: coverage runs from VV_TERRAIN_FLECK_COVER_CLOSED to
// VV_TERRAIN_FLECK_COVER_OPEN with how thin the crown is, read from the leaf
// map along the sun ray and vanilla's own broken shadow, with a slow regional
// noise so thin patches come in groups.
//
// The field is three octaves of value noise (one, a half and a quarter
// block) under a gentle domain warp, which keeps the value-noise lattice from
// drawing axis-aligned edges. The two coarse octaves are averaged along the
// sun's azimuth over a reach that grows as the sun drops, so a low sun draws
// streaks pointing away from it, and the warp and the fine octave sway with
// the game's wind. Summed noise piles up around its middle, so the sum is
// expanded by VV_TERRAIN_FLECK_CONTRAST before the cut - measured so the lit
// share matches the coverage asked for (see tools/smoketest) - and the
// streak average, which narrows the sum further, is expanded a little more.
// One hard level, texel-stepped by the caller, with a small per-texel jitter
// on the threshold so the edge is a pixel-art line rather than a contour.
//
// Every grid tiles the 4096-block world wrap exactly. There is no cell
// search, so nothing can be clipped by one.
vec3 vvTerrainFleckField(vec2 src, vec2 az, float stretch, float density, float shimmer)
{
    stretch = max(stretch, VV_TERRAIN_FLECK_MIN_STRETCH);
    float turn = 6.28318530718 * vv_sceneBreeze;

    float region = vvTerrainFleckNoise(src, VV_TERRAIN_FLECK_CLUSTER_CELL, VV_TERRAIN_FLECK_CLUSTER_PERIOD, vec2(17.0, 5.0));
    // v10: the crown's thinness from the leaf map alone. v7-v9 also read
    // vanilla's broken shadow here, but the shadow map is fitted around the
    // camera and resampled as it moves, and a hard cut turns every small
    // change of its input into edges popping - the spots jittered while
    // flying (in game, v9). Everything that sets the cut is now anchored to
    // the world.
    float thin = clamp(0.7 * (1.0 - clamp(density, 0.0, 1.0)) + 0.3 * region, 0.0, 1.0);
    // Wind opens and closes the crown a little as a whole, on whole turns
    // of the breeze clock; still air holds it.
    float breathe = 1.0 - 0.15 * shimmer * (0.5 + 0.5 * sin(turn * 5.0 + 6.28318530718 * region));
    float coverage = vvTerrainFleckCoverage(thin) * breathe;

    vec2 sway = vec2(sin(turn * 3.0), cos(turn * 2.0)) * (0.02 + 0.06 * shimmer);
    vec2 warp = vec2(vvTerrainFleckNoise(src + sway, VV_TERRAIN_FLECK_CELL * 2.0, VV_TERRAIN_FLECK_PERIOD * 0.5, vec2(41.0, 7.0)),
                     vvTerrainFleckNoise(src + sway, VV_TERRAIN_FLECK_CELL * 2.0, VV_TERRAIN_FLECK_PERIOD * 0.5, vec2(43.0, 13.0))) - 0.5;
    vec2 p = src + VV_TERRAIN_FLECK_WARP * warp;

    float reach = VV_TERRAIN_FLECK_CELL * (1.0 / stretch - 1.0) * VV_TERRAIN_FLECK_STREAK;
    float n = 0.0;
    for (int t = -1; t <= 1; t++)
    {
        vec2 q = p + az * (float(t) * reach);
        float w = t == 0 ? 0.5 : 0.25;
        n += w * (0.55 * vvTerrainFleckNoise(q, VV_TERRAIN_FLECK_CELL, VV_TERRAIN_FLECK_PERIOD, vec2(23.0, 3.0))
                + 0.45 * vvTerrainFleckNoise(q, VV_TERRAIN_FLECK_CELL * 0.5, VV_TERRAIN_FLECK_PERIOD * 2.0, vec2(31.0, 19.0)));
    }
    n = 0.78 * n + 0.22 * vvTerrainFleckNoise(p + 2.0 * sway, VV_TERRAIN_FLECK_LOBE_CELL, VV_TERRAIN_FLECK_LOBE_PERIOD, vec2(29.0, 11.0));

    float streak = clamp(reach / VV_TERRAIN_FLECK_CELL, 0.0, 1.0);
    float u = clamp(0.5 + (n - 0.5) * VV_TERRAIN_FLECK_CONTRAST * (1.0 + 0.6 * streak), 0.0, 1.0);
    float jitter = vvTerrainCellRandom(mod(floor(src * VV_TERRAIN_FLECK_TEXELS), VV_TERRAIN_FLECK_TEXEL_PERIOD) + vec2(613.0, 59.0)).x;
    float threshold = 1.0 - coverage + VV_TERRAIN_FLECK_RAGGED * (jitter - 0.5);
    float level = step(threshold, u);
    float halo = smoothstep(threshold - 0.12, threshold, u) * (1.0 - level);

    // v10 brightness: most patches are soft dapples, the hearts of wide gaps
    // are full sun, and in a beam group most of each patch is - the bright
    // spots the shafts land on. Stepped into a few flat tones.
    float beams = smoothstep(0.5, 0.8, vvTerrainFleckNoise(src, VV_TERRAIN_FLECK_BEAM_CELL, VV_TERRAIN_FLECK_BEAM_PERIOD, vec2(53.0, 37.0)));
    float core = mix(VV_TERRAIN_FLECK_CORE_WIDE, VV_TERRAIN_FLECK_CORE_NARROW, beams);
    float depth = floor(smoothstep(threshold, threshold + core, u) * VV_TERRAIN_FLECK_LEVELS + 0.5) / VV_TERRAIN_FLECK_LEVELS;
    float bright = mix(VV_TERRAIN_FLECK_DIM, 1.0, depth);
    return vec3(level * bright, halo, coverage);
}

// How strongly the light source that makes flecks is up: the sun by day,
// the moon at night at VV_TERRAIN_MOON_FLECK of the sun's share and only as
// bright as the game says the moon is.
float vvTerrainFleckLight()
{
    float sun = vvTerrainSunPresence();
    float moon = (1.0 - sun) * clamp(vv_sceneMoonLight, 0.0, 1.0) * clamp(vv_pbrMoonFlecks, 0.0, 1.0) * VV_TERRAIN_MOON_FLECK;
    return max(sun, moon);
}

// How much of the direct light this fragment gets back, and how much bounce
// light from a pool beside it: x fleck 0..1, y bounce 0..1.
//
// Evaluated once per block texel (1/16 block), so a fleck has the stepped
// edge of the game's own pixel art. Only the axes along the face are snapped:
// snapping across it would flip a face lying on a texel boundary between two
// texels and speckle it.
//
// The light is the sun by day and the moon by night - the same occluder, the
// same gaps, through vanilla's own lightPosition and shadow map. The moon's
// share is faint and capped (VV_TERRAIN_MOON_FLECK).
vec2 vvTerrainSunfleck(vec3 cameraRelativePos, vec3 faceNormal, vec3 overhead, float shadowed, float facing)
{
    if (vv_pbrSunflecks < 0.001 || overhead.x <= 0.0) return vec2(0.0);

    float flatDistance = max(abs(cameraRelativePos.x), abs(cameraRelativePos.z));
    float resolvable = clamp(1.0 - (flatDistance - VV_TERRAIN_FLECK_FADE_START) / VV_TERRAIN_FLECK_FADE_RANGE, 0.0, 1.0)
                     * (1.0 - smoothstep(VV_TERRAIN_FLECK_VIEW_LIMIT * 0.75, VV_TERRAIN_FLECK_VIEW_LIMIT, vvTerrainEyeDistance(cameraRelativePos)));
    if (resolvable < 0.004) return vec2(0.0);

    float clearSky = vvTerrainClearSky();
    float light = vvTerrainFleckLight();
    if (clearSky * light < 0.004) return vec2(0.0);

    vec3 world = cameraRelativePos + vv_pbrOrigin;
    // A plant card crosses texel boundaries everywhere, so it snaps on all
    // three axes.
    vec3 across = vvTerrainIsFlora() ? vec3(0.0) : step(vec3(0.5), abs(faceNormal));
    vec3 texel = floor(world * VV_TERRAIN_FLECK_TEXELS);
    world = mix((texel + 0.5) / VV_TERRAIN_FLECK_TEXELS, world, across);

    // Projected from one canopy plane for the whole scene, not from each
    // cell's own lowest leaf: a per-cell height shifts the pattern at every
    // cell boundary, which cut flecks in two.
    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));
    float rise = max(vv_canopyPlaneY - world.y, 1.0);
    vec2 src = world.xz + l.xz * (rise / max(l.y, 0.06));
    vec2 az = length(l.xz) > 0.001 ? normalize(l.xz) : vec2(1.0, 0.0);
    vec3 field = vvTerrainFleckField(src, az, l.y, overhead.z, vvTerrainFleckShimmerAt(src));
    float pattern = field.x;

    float gate = clamp(shadowed, 0.0, 1.0) * resolvable * smoothstep(0.0, 0.12, overhead.x)
               * clearSky * light * vv_pbrSunflecks * VV_TERRAIN_FLECK_GAIN;
    float fleck = clamp(pattern * clamp(facing, 0.0, 1.0) * gate, 0.0, 1.0);
    float bounce = clamp(field.y * VV_TERRAIN_FLECK_BOUNCE * gate, 0.0, VV_TERRAIN_FLECK_BOUNCE);
    return vec2(fleck, bounce);
}

// A sunlit patch's colour from the shaded colour lifted to vanilla's sunlit
// level: plain sunlight at noon, only a hair warmer than the shade, at the SAME
// luminance (v7: the v5/v6 tint read as yellow discs painted on the ground,
// where the reference photographs show plain sunlight), and still
// cannot pass vanilla's own sunlit brightness. `golden` 1 near the horizon
// shifts it toward the colour of an evening sun; by night the same patch is
// the moon's, cool instead of warm.
vec3 vvTerrainSunlitTint(vec3 c, float golden, float night)
{
    float y = dot(c, vec3(0.2126, 0.7152, 0.0722));
    vec3 hue = mix(mix(vec3(1.02, 1.0, 0.96), vec3(1.08, 0.99, 0.86), clamp(golden, 0.0, 1.0)),
                   vec3(0.88, 0.95, 1.12), clamp(night, 0.0, 1.0));
    vec3 warm = (vec3(y) + (c - vec3(y)) * VV_TERRAIN_FLECK_SATURATION) * hue;
    return max(warm * (y / max(dot(warm, vec3(0.2126, 0.7152, 0.0722)), 0.0001)), vec3(0.0));
}

// The factor that undoes vanilla's sun shadow at this visibility, so a fleck
// reaches vanilla's sunlit level and never passes it.
//
// Vanilla does not apply its shadow alone: it takes min(shadow, nb), where nb
// is its normal shade (a per-vertex value on plants in chunkopaque, and
// getBrightnessFromNormal on topsoil). A sunlit fragment therefore sits at
// nb, not at 1, and where nb is below 1 a shadow darkens it only from nb
// down. v7 divided by the shadow alone, which lifted a plant with nb 0.6 to
// 1.7x its own sunlit level: the flecks were brighter than the open grass
// beside them, and on the autumn palette that read as a saturated yellow
// (measured from the v7 screenshots: fleck luma 81-107, sunlit grass 61-64).
// vv_vanillaNormalShade carries vanilla's nb in from the call site; it
// defaults to 1, which is the old formula.
float vvTerrainSunRelight(float sunVisibility)
{
    float normalShade = clamp(vv_vanillaNormalShade, 0.05, 1.0);
    float shadow = 1.0 - clamp(shadowIntensity, 0.0, 1.0) * 0.5 * (1.0 - clamp(sunVisibility, 0.0, 1.0));
    float shaded = min(shadow, normalShade);
    return normalShade / max(shaded, 0.05);
}

// Canopy shade and sunflecks applied to a sunlit term, the one entry point
// for opaque blocks, the forest floor and plants alike.
// Share of the canopy shade cap a vertical block face takes (D63).
const float VV_TERRAIN_CANOPY_WALL = 0.45;

vec3 vvTerrainCanopyResolve(vec3 color, vec3 cameraRelativePos, vec3 faceNormal, float fade,
                            float sunVisibility, float cap)
{
    if (vv_pbrDapple < 0.001 && vv_pbrSunflecks < 0.001) return color;
    if (!vvTerrainIsCanopyReceiver()) return color;
    if (vvTerrainEyeDistance(cameraRelativePos) > VV_TERRAIN_DAPPLE_FADE_START + VV_TERRAIN_DAPPLE_FADE_RANGE) return color;
    if (vvTerrainFleckLight() < 0.01) return color;

    if (sunVisibility < 0.0) sunVisibility = vvTerrainSunVisibility();
    float shadowed = 1.0 - clamp(sunVisibility, 0.0, 1.0);
    if (shadowed < 0.01) return color;

    vec3 overhead = vvTerrainCanopyOverhead(cameraRelativePos);
    // Measured once: the dapple's shade and the flecks' placement both read it.
    float evidence = vvTerrainCanopyEvidence(sunVisibility);
    // A wall or a trunk sees most of its sky toward the horizon, not up
    // through the crown, and vanilla has already darkened it for facing
    // away from the light. Under its own crown a trunk read the full 0.85
    // cap and went near black (seen in game 2026-11-24, D63), so a block's
    // vertical faces take at most VV_TERRAIN_CANOPY_WALL of the cap.
    if (!vvTerrainIsFlora())
        cap *= mix(VV_TERRAIN_CANOPY_WALL, 1.0, clamp(faceNormal.y, 0.0, 1.0));
    vec3 shaded = vvTerrainApplyCanopy(color, vvTerrainCanopyDapple(cameraRelativePos, fade, sunVisibility, overhead.x, evidence), cap);

    // A face turned away from the light gets no fleck. A plant's card is
    // two-sided, so it takes the light on either face, falling off to about
    // half edge-on: blades turned to the sun catch a fleck, the rest less.
    // A wall, a trunk or a rock face turned to the sun takes one as the
    // ground does; the projection from the canopy plane already shears it
    // up the face.
    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));
    float facing = vvTerrainIsFlora() ? mix(0.55, 1.0, abs(dot(faceNormal, l)))
                                      : smoothstep(0.0, 0.25, dot(faceNormal, l));
    vec2 fleck = vvTerrainSunfleck(cameraRelativePos, faceNormal, overhead, shadowed, facing);
    if (fleck.x <= 0.0 && fleck.y <= 0.0) return shaded;

    float night = 1.0 - vvTerrainSunPresence();
    float golden = 1.0 - smoothstep(0.08, 0.45, l.y);
    vec3 lit = vvTerrainSunlitTint(color * vvTerrainSunRelight(sunVisibility), golden, night);
    // Bounce: a little of a pool's light lands on the shade around it. Warm
    // because the pool is, and well under the cap - it fills the edge of a
    // pool, it does not light the floor.
    float local = 1.0 - vvTerrainLocalLightShare();
    float amount = clamp(fleck.x + fleck.y, 0.0, 1.0) * local;
    // A faint bloom that scales with how much the fleck stands out from the
    // shade around it: none on a patch barely lighter than its shade, the
    // most on a full-sun spot in deep shade.
    float litY = dot(lit, vec3(0.2126, 0.7152, 0.0722));
    float contrast = clamp(1.0 - dot(shaded, vec3(0.2126, 0.7152, 0.0722)) / max(litY, 0.001), 0.0, 1.0);
    vv_fleckGlow = clamp(fleck.x, 0.0, 1.0) * local * contrast * VV_TERRAIN_FLECK_GLOW;
    return mix(shaded, lit, amount);
}

// How much of this leaf texel lines a hole in the leaf texture, 0..1: the
// share of its four neighbouring texels that are cut away. A leaf's interior
// reads 0. Read from vanilla's own terrainTex at vanilla's own uv, so it
// needs no sampler of ours, and at the base level so the answer is the
// texture's, not a mip's blur of it.
float vvTerrainLeafHoleRim()
{
    vec2 px = 1.0 / max(vec2(1.0), vec2(textureSize(terrainTex, 0)));
    float holes = step(textureLod(terrainTex, uv + vec2(px.x, 0.0), 0.0).a, 0.5)
                + step(textureLod(terrainTex, uv - vec2(px.x, 0.0), 0.0).a, 0.5)
                + step(textureLod(terrainTex, uv + vec2(0.0, px.y), 0.0).a, 0.5)
                + step(textureLod(terrainTex, uv - vec2(0.0, px.y), 0.0).a, 0.5);
    return holes * 0.25;
}

// v14: the rim is read at most once per fragment. The backlit rim light and
// the shaft sources both want it, and it is four texture reads.
float vv_leafRim = -1.0;
float vvTerrainLeafRim()
{
    if (vv_leafRim < 0.0) vv_leafRim = vvTerrainLeafHoleRim();
    return vv_leafRim;
}

// How much this fragment marks vanilla's god-ray channel, 0 to
// VV_TERRAIN_SHAFT_MAX.
//
// Vanilla's god-ray pass blurs that channel radially from the sun's screen
// position. Vanilla terrain writes ZERO to it; the beams come from the sky
// showing through the holes cut in the leaf textures, which is what gives
// real shafts their comb of thin rays with dark gaps between them (the
// player's reference photographs). v5 and v6 marked whole leaf faces at thin
// places in the crown, and those broad bright patches filled the dark gaps
// in, drawing one soft wedge instead of rays (seen in game at 16:30, v6).
//
// v7 keeps a leaf dark in the channel and marks only the texels that LINE a
// hole, and v8 only where the fleck field is open below (see `lands`) - a backlit leaf edge, the bright rim around each chink of sky - so
// every source sits on a real gap and adds to the comb instead of washing
// it out. Only leaves, only where the leaf map says the crown is thin (a
// hole in a closed crown shows more leaves, not sky), strongest looking
// toward the sun, stronger in humid air - after rain, at dawn and dusk, in
// fog - and gone under cloud. Faded out by VV_TERRAIN_SHAFT_RIM_FAR, where a
// texel is under a pixel and a rim test would sparkle. fogDensity is
// vanilla's fogDensityIn, passed in because only chunkopaque declares it.
float vvTerrainShaftLevel(vec3 cameraRelativePos, float fogDensity)
{
    if (vv_pbrShafts < 0.001) return 0.0;
    if (vvTerrainFloraClass() != VV_TFLORA_LEAVES) return 0.0;
    float sun = vvTerrainSunPresence();
    if (sun < 0.01 || vv_canopyMapValid < 0.5) return 0.0;

    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));
    float look = dot(vvTerrainSafeNormalize(cameraRelativePos, -l), l);
    float toward = smoothstep(0.2, 0.9, look);
    if (toward < 0.004) return 0.0;

    float clearSky = vvTerrainClearSky();
    if (clearSky < 0.004) return 0.0;

    float near = 1.0 - smoothstep(VV_TERRAIN_SHAFT_RIM_FAR * 0.5, VV_TERRAIN_SHAFT_RIM_FAR, vvTerrainEyeDistance(cameraRelativePos));
    if (near < 0.004) return 0.0;
    float rim = vvTerrainLeafRim();
    if (rim <= 0.0) return 0.0;

    vec3 world = cameraRelativePos + vv_pbrOrigin;
    float crown = vvTerrainCanopyCell(world.xz).x;
    float gap = 1.0 - smoothstep(0.6, 1.0, crown);
    if (gap <= 0.0) return 0.0;

    // A beam is the air between a gap and the fleck it lands on, so it
    // starts only where the fleck field is open. The sun ray through this
    // leaf meets the canopy plane at the same `src` the flecks below it are
    // projected from, so a leaf marks a beam exactly when the floor that ray
    // reaches is lit: every beam lands on a lit patch, and the beams come in
    // the flecks' groups instead of a haze of sources across every thin
    // crown. Placement uses the leaf map's cell density (vanilla's broken
    // shadow is not known at a leaf), so beams are a subset of the patches.
    float rise = vv_canopyPlaneY - world.y;
    vec2 src = world.xz + l.xz * (rise / max(l.y, 0.06));
    vec2 az = length(l.xz) > 0.001 ? normalize(l.xz) : vec2(1.0, 0.0);
    vec3 field = vvTerrainFleckField(src, az, l.y, crown, vvTerrainFleckShimmerAt(src));
    // v10: only over the BRIGHT patches - a beam is the air a wide gap
    // lights, and the dim dapples are the small gaps that make none.
    float lands = smoothstep(VV_TERRAIN_FLECK_DIM + 0.25, 1.0, field.x);
    if (lands <= 0.0) return 0.0;

    // Humid air carries a beam: wet ground after rain, a low sun at either
    // end of the day, and vanilla's own fog density (0.00125 by default,
    // ten times that in fog).
    float lowSun = 1.0 - smoothstep(0.15, 0.55, l.y);
    float fog = clamp((fogDensity - 0.00125) / 0.01, 0.0, 1.0);
    float humid = 0.6 + 0.4 * clamp(max(max(clamp(vv_sceneWetness, 0.0, 1.0), lowSun), fog), 0.0, 1.0);

    float level = clamp(vv_pbrShafts, 0.0, 2.0) * VV_TERRAIN_SHAFT_GAIN * toward * rim * near * gap * lands * humid * clearSky * sun
                * vvSceneVisibilityDampen();
    return clamp(level, 0.0, VV_TERRAIN_SHAFT_MAX);
}

// Sunlight scattered toward the camera by the air inside the beams between
// the camera and this fragment, as a share of the sunlit colour, 0 to
// VV_TERRAIN_BEAM_MAX.
//
// A beam IS the column of air above a bright sunfleck, along the sun: the
// light that makes the fleck crosses that air on its way down. So a point in
// the air is in a beam exactly when the sun ray through it reaches the canopy
// plane at a `src` where the fleck field's bright patches are - the same
// projection the flecks and the leaf-rim sources use, and the two layers of
// the same field that place the bright patches (vvTerrainBeamDensity), so
// every beam ends on a bright fleck and starts at a gap in the crown above.
//
// Visible from the side at any sun height, brightest looking toward the sun
// (forward scattering), strongest in humid air, scaled by how strongly the
// sun is up and how clear the sky is, and held back by the visibility
// dampen. Only air under the canopy plane and under leaves takes a beam;
// open air above the crown is just daylight. World-anchored except for where
// the samples fall, which a per-pixel offset spreads so the beams do not
// band. v12 cost: eight steps, each one leaf-map read and at most two noise
// reads (one outside beam groups); measured by the player at v11 as below
// 30 fps with the full field, v12 not yet measured. Beams in front of open sky are not drawn: the sky is not a
// terrain fragment (DATA GAP).
// Where a beam stands on the canopy plane, 0..1, from the two layers of the
// fleck field that decide where its BRIGHT patches are: the beam-group noise
// (same grid and key as the field's) and its coarse octave (same key), cut
// high. Two noise reads, against about eleven for the whole field, so the
// march can afford it at every step; the field's warp, streak taps and fine
// octave move a patch's outline by a fraction of a block and are left out,
// so a beam lands on the heart of a bright fleck rather than tracing its
// edge. Zero outside beam groups after one read.
float vvTerrainBeamDensity(vec2 src)
{
    float group = smoothstep(0.5, 0.8, vvTerrainFleckNoise(src, VV_TERRAIN_FLECK_BEAM_CELL, VV_TERRAIN_FLECK_BEAM_PERIOD, vec2(53.0, 37.0)));
    if (group <= 0.0) return 0.0;
    float n = vvTerrainFleckNoise(src, VV_TERRAIN_FLECK_CELL, VV_TERRAIN_FLECK_PERIOD, vec2(23.0, 3.0));
    return group * smoothstep(0.55, 0.8, n);
}

float vvTerrainBeamInscatter(vec3 cameraRelativePos, float fogDensity)
{
    if (vv_pbrShafts < 0.001 || vv_canopyMapValid < 0.5) return 0.0;
    float sun = vvTerrainSunPresence();
    if (sun < 0.01) return 0.0;
    float clearSky = vvTerrainClearSky();
    if (clearSky < 0.004) return 0.0;
    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));
    if (l.y < 0.06) return 0.0;

    vec3 fromEye = -vvTerrainToEye(cameraRelativePos);   // the view ray starts at the eye
    float dist = length(fromEye);
    if (dist < 0.5) return 0.0;
    vec3 dir = fromEye / dist;
    float span = min(dist, VV_TERRAIN_BEAM_REACH);
    float ds = span / float(VV_TERRAIN_BEAM_STEPS);
    // v13: nothing to march where no crown stands along the marched span -
    // open country, where v12 still paid for every sample and drew beams over
    // scrub in full sun. Three leaf-map reads, no noise.
    vec2 eyeXz = vv_pbrOrigin.xz + vv_pbrEye.xz;
    float crowns = max(max(vvTerrainCanopyCell(eyeXz).x,
                           vvTerrainCanopyCell(eyeXz + dir.xz * (span * 0.5)).x),
                       vvTerrainCanopyCell(eyeXz + dir.xz * span).x);
    if (crowns < VV_TERRAIN_BEAM_CROWN) return 0.0;
    // Interleaved gradient noise: a fixed per-pixel offset of the samples,
    // so twelve steps read as a smooth integral rather than bands.
    float offset = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
    float beam = 0.0;
    for (int i = 0; i < VV_TERRAIN_BEAM_STEPS; i++)
    {
        vec3 rel = vv_pbrEye + dir * ((float(i) + offset) * ds);
        vec3 p = rel + vv_pbrOrigin;
        float rise = vv_canopyPlaneY - p.y;
        if (rise < 0.5) continue;
        // v13: a beam is only a beam in SHADED air - under a crown, below its
        // lowest leaf. In open sunlight all the air is lit and there is no
        // shaft to see; v12 drew them over meadows and inside crowns.
        vec2 here = vvTerrainCanopyCell(p.xz);
        if (here.x < VV_TERRAIN_BEAM_CROWN || p.y > here.y - 0.5) continue;
        // v13: and only if no crown stands between this air and the gap. The
        // sun ray halfway up to the canopy plane must not pass through another
        // tree's leaves; at a low sun v12's beams ran through whole crowns as
        // horizontal slabs.
        vec3 mid = p + l * (0.5 * rise / l.y);
        vec2 midCell = vvTerrainCanopyCell(mid.xz);
        if (midCell.x >= VV_TERRAIN_BEAM_BLOCK && mid.y > midCell.y - 0.5
            && mid.y < midCell.y + max(2.0, midCell.x * 5.0)) continue;
        vec2 srcRel = rel.xz + l.xz * (rise / l.y);
        if (max(abs(srcRel.x), abs(srcRel.y)) > 28.0) continue;
        vec2 src = p.xz + l.xz * (rise / l.y);
        float crown = vvTerrainCanopyCell(src).x;
        if (crown < 0.15) continue;
        beam += vvTerrainBeamDensity(src) * smoothstep(0.15, 0.4, crown);
    }
    if (beam <= 0.0) return 0.0;

    // Forward scattering: a beam seen from the side is faint but there, and
    // looking toward the sun it is several times brighter.
    float cosView = dot(dir, l);
    float phase = 0.45 + 0.55 * pow(max(cosView, 0.0), 3.0);
    float lowSun = 1.0 - smoothstep(0.15, 0.55, l.y);
    float fog = clamp((fogDensity - 0.00125) / 0.01, 0.0, 1.0);
    float humid = 0.6 + 0.4 * clamp(max(fog, max(clamp(vv_sceneWetness, 0.0, 1.0), lowSun)), 0.0, 1.0);

    float level = beam * ds * VV_TERRAIN_BEAM_SCATTER * phase * humid * clearSky * sun
                * clamp(vv_pbrShafts, 0.0, 2.0) / 0.45 * vvSceneVisibilityDampen();
    return clamp(level, 0.0, VV_TERRAIN_BEAM_MAX);
}

// The colour beams add: the sun's own colour, warmer as it drops.
vec3 vvTerrainBeamColor()
{
    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));
    float golden = 1.0 - smoothstep(0.08, 0.45, l.y);
    return mix(vec3(1.0, 0.97, 0.9), vec3(1.0, 0.82, 0.58), golden);
}

// ---------------------------------------------------------------------------
// Plant tissue, on the modular terrain path
//
// A port of the legacy patch's flora response, not a second model: the same
// taxonomy, tissue values, pigment and weather optics. Plant tissue never
// receives the opaque material's relief, cavity or atlas specular; it starts
// from vanilla's lit colour and takes only the terms below.
// ---------------------------------------------------------------------------

// v14: how far up a plant this fragment is, in blocks: the block's windData
// (how many blocks of the same plant are below it) plus the height inside the
// block, the same two terms vanilla's applyVertexWarping bends the plant by.
// Read from vanilla's own worldPos + playerpos so it agrees with the bend.
float vvTerrainPlantHeight(vec3 cameraRelativePos)
{
    float within = fract(cameraRelativePos.y + playerpos.y);
    return float((renderFlags >> WindDataPosition) & 0x7) + within;
}

// Ground flora rooted in the block below, whose height up the plant means
// root to tip. Leaves, vines, fruit and water plants are not.
bool vvTerrainIsRootedFlora(int flora)
{
    return flora == VV_TFLORA_HERB || flora == VV_TFLORA_GRASS || flora == VV_TFLORA_CROP ||
           flora == VV_TFLORA_REED || flora == VV_TFLORA_STIFF || flora == VV_TFLORA_THIN;
}

// 0 at the root to 1 a block up and beyond, continuous across the block
// boundaries of a tall plant (its total height is not known here).
float vvTerrainPlantTip(vec3 cameraRelativePos)
{
    return smoothstep(0.0, 1.0, vvTerrainPlantHeight(cameraRelativePos));
}

// v14 contact shade: the darkening at the foot of a plant, where the stems
// crowd together and the sky sees least of them - the occlusion vanilla gives
// a block's vertices and not a plant's. A multiplier on vanilla's lit colour,
// 1 everywhere above REACH blocks, never brighter. Ambient occlusion, so it
// holds in any light.
const float VV_TERRAIN_CONTACT_REACH = 0.45;
const float VV_TERRAIN_CONTACT_DEPTH = 0.45;
float vvTerrainContactShade(vec3 cameraRelativePos)
{
    if (vv_pbrContactShade < 0.001) return 1.0;
    if (!vvTerrainIsRootedFlora(vvTerrainFloraClass())) return 1.0;
    float foot = 1.0 - smoothstep(0.0, VV_TERRAIN_CONTACT_REACH, vvTerrainPlantHeight(cameraRelativePos));
    return 1.0 - VV_TERRAIN_CONTACT_DEPTH * clamp(vv_pbrContactShade, 0.0, 1.0) * foot;
}

float vvTerrainFloraTissue()
{
    int flora = vvTerrainFloraClass();
    if (flora == VV_TFLORA_NONE) return 0.0;
    if (flora == VV_TFLORA_FRUIT) return 0.08;

    float base;
    if      (flora == VV_TFLORA_GRASS)   base = 1.00;
    else if (flora == VV_TFLORA_HERB)    base = 0.95;
    else if (flora == VV_TFLORA_THIN)    base = 0.95;
    else if (flora == VV_TFLORA_CROP)    base = 0.85;
    else if (flora == VV_TFLORA_REED)    base = 0.72;
    else if (flora == VV_TFLORA_LEAVES)  base = 0.78;
    else if (flora == VV_TFLORA_BUSH)    base = 0.68;
    else if (flora == VV_TFLORA_VINE)    base = 0.74;
    else if (flora == VV_TFLORA_AQUATIC) base = 0.62;
    else                                 base = 0.55;

    // Root-to-tip gradient, only for the bending ground flora whose wind data
    // is a height up the plant. v14: up to v13 this read windData / 7 alone,
    // which is 0 for every single-block plant - the commonest grass sat at
    // its root value, 55%, from root to tip. The height now includes the
    // position inside the block, as vanilla's bend does.
    if (vvTerrainIsRootedFlora(flora))
    {
        base *= mix(0.55, 1.0, vvTerrainPlantTip(worldPos.xyz));
    }
    return base;
}

vec3 vvTerrainFloraPigment(vec3 albedo)
{
    vec3 base = max(albedo, vec3(0.02));
    float luma = max(0.001, dot(base, vec3(0.299, 0.587, 0.114)));
    vec3 chroma = clamp(base / luma, vec3(0.25), vec3(2.6));

    float greenLead = clamp((base.g - max(base.r, base.b)) / luma, 0.0, 1.0);
    float warmLead = clamp((max(base.r, base.g) - base.b) / luma, 0.0, 1.0);
    float fruit = vvTerrainFloraClass() == VV_TFLORA_FRUIT ? 1.0 : 0.0;

    vec3 pigment = chroma;
    pigment = mix(pigment, pigment * vec3(1.04, 1.14, 0.70), greenLead * (1.0 - fruit) * 0.65);
    pigment = mix(pigment, pigment * vec3(1.14, 1.02, 0.70), warmLead * 0.25);
    pigment = mix(pigment, chroma, fruit);
    return clamp(pigment * luma, vec3(0.0), vec3(1.8));
}

// l points to the sun and v to the viewer, so a backlit leaf has l roughly
// opposite v. Bending l THROUGH the surface and asking how directly the
// viewer looks back down it is the legacy form, sign included.
// v14: plus a broad diffuse share. The lobe alone lit a leaf only when the
// viewer looked nearly down the sun ray through it; a real leaf lit from
// behind glows over a wide cone too, which is why a canopy seen from below is
// green and bright even with the sun well off to one side. DIFFUSE is that
// share at a sun straight behind the leaf.
const float VV_TERRAIN_TRANSMIT_DIFFUSE = 0.2;
float vvTerrainTranslucency(vec3 n, vec3 l, vec3 v)
{
    vec3 through = vvTerrainSafeNormalize(l + n * 0.35, l);
    return pow(max(0.0, dot(v, -through)), 3.0) + VV_TERRAIN_TRANSMIT_DIFFUSE * max(0.0, -dot(n, l));
}

// Distance at which per-leaf detail stops being resolvable.
float vvTerrainFloraLod(vec3 cameraRelativePos)
{
    return clamp(1.0 - (vvTerrainEyeDistance(cameraRelativePos) - 18.0) / 54.0, 0.0, 1.0);
}

// Snow and frost are a surface over the tissue and stop the beam; water barely
// does. Same weights as the legacy patch.
float vvTerrainFloraWeatherOptics(float wet, float snow, float frost)
{
    return mix(1.0, 0.92, wet) * mix(1.0, 0.10, snow) * mix(1.0, 0.45, frost);
}

vec3 vvTerrainFoliageTransmission(vec3 albedo, vec3 faceNormal, vec3 cameraRelativePos,
                                  float sunVisibility)
{
    if (vv_pbrFoliage < 0.001) return vec3(0.0);

    float tissue = vvTerrainFloraTissue();
    if (tissue < 0.001) return vec3(0.0);

    vec3 v = vvTerrainSafeNormalize(vvTerrainToEye(cameraRelativePos), vec3(0.0, 1.0, 0.0));
    vec3 n = vvTerrainSafeNormalize(faceNormal, vec3(0.0, 1.0, 0.0));
    // Cross quads are seen from both sides; shade the side facing the camera.
    if (dot(n, v) < 0.0) n = -n;
    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));

    float weather = vvTerrainFloraWeatherOptics(vvTerrainWetness(n), vvTerrainSnow(n),
                                                clamp(vv_sceneFrost, 0.0, 1.0));
    // Transmission needs a beam; an overcast sky has none.
    float overcast = clamp(vv_sceneOvercast, 0.0, 1.0);

    return vvTerrainFloraPigment(albedo)
         * vvTerrainTranslucency(n, l, v)
         * tissue
         * clamp(sunVisibility, 0.0, 1.0)
         * weather
         * vv_pbrFoliage
         * vvTerrainSunPresence()
         * mix(1.0, VV_OVERCAST_DIRECT, overcast);
}

// Sunlight scattered back out of the lit side of the leaf, a wrapped diffuse.
// Small, and it never darkens: vanilla already lights the leaf.
vec3 vvTerrainFloraFrontScatter(vec3 albedo, vec3 n, vec3 l, float sunVisibility, float lod)
{
    if (vv_pbrFoliage < 0.001 || sunVisibility <= 0.0) return vec3(0.0);
    float wrapDiffuse = clamp((dot(n, l) + 0.45) / 1.45, 0.0, 1.0);
    float overcast = clamp(vv_sceneOvercast, 0.0, 1.0);
    return albedo * wrapDiffuse * 0.10 * lod
         * clamp(sunVisibility, 0.0, 1.0)
         * vvTerrainSunPresence()
         * mix(1.0, VV_OVERCAST_DIRECT, overcast)
         * vv_pbrFoliage;
}

// The waxy cuticle's sun highlight. Rough and dim when dry, tighter when wet,
// matte under snow or frost. Grass is matter than a leaf and fruit glossier.
vec3 vvTerrainCuticleSpecular(vec3 n, vec3 l, vec3 v, float wet, float snowFrost,
                              float sunVisibility, float lod)
{
    if (vv_pbrSpecularStrength < 0.001) return vec3(0.0);

    vec3 h = vvTerrainSafeNormalize(l + v, n);
    float ndotlFront = max(dot(n, l), 0.0);
    float ndotlBack = max(dot(-n, l), 0.0);
    float ndotv = max(dot(n, v), 1e-4);
    float nl = max(ndotlFront, 0.04);

    float roughness = mix(0.54, 0.40, wet);
    roughness = mix(roughness, 0.78, clamp(snowFrost, 0.0, 1.0));
    roughness = vvTerrainFilteredRoughness(roughness, n);

    vec3 f0 = vec3(mix(0.028, 0.052, wet));
    vec3 f = vvFresnelSchlickTerrain(max(dot(v, h), 0.0), f0);
    float d = vvDistributionGGXTerrain(max(dot(n, h), 0.0), roughness * roughness);
    float g = vvGeometrySmithTerrain(ndotv, nl, roughness);
    vec3 spec = (d * g * f) / max(4.0 * ndotv * nl, 0.08);

    int flora = vvTerrainFloraClass();
    float classSpec = flora == VV_TFLORA_GRASS ? 0.45 : flora == VV_TFLORA_FRUIT ? 1.25 : 1.0;
    float sunAvailable = clamp(vv_sceneDayLight * (1.0 - vv_sceneOvercast * 0.72), 0.0, 1.0);

    return spec * max(ndotlFront, ndotlBack * 0.35)
         * clamp(sunVisibility, 0.0, 1.0) * sunAvailable
         * vv_pbrSpecularStrength * classSpec * mix(0.18, 1.0, lod);
}

// v14 backlit rim light: the bright edge around every chink of sky in a
// backlit crown, where the sun scatters through the thin margin of each leaf.
// Only on the texels lining a hole in the leaf texture (the rim the shaft
// sources already find, read once), only looking toward the sun, only in
// direct sun and within RIM_FAR, where a texel is still bigger than a pixel.
// Lit where the shadow map says so, or where the leaf map says the crown is
// thin: a backlit leaf facing the camera is shadowed by its own block, which
// is not the light it shows. Rides the foliage slider: it is light through
// the leaf, like the transmission beside it.
const float VV_TERRAIN_RIM_GAIN = 0.45;
const float VV_TERRAIN_RIM_FAR = 24.0;
vec3 vvTerrainBacklitRim(vec3 albedo, vec3 cameraRelativePos, vec3 l, float sunVisibility)
{
    if (!(vv_pbrFoliage >= 0.001)) return vec3(0.0);
    int flora = vvTerrainFloraClass();
    if (flora != VV_TFLORA_LEAVES && flora != VV_TFLORA_BUSH && flora != VV_TFLORA_VINE &&
        flora != VV_TFLORA_GRASS && flora != VV_TFLORA_HERB) return vec3(0.0);
    float sun = vvTerrainSunPresence();
    if (sun < 0.01) return vec3(0.0);
    float back = smoothstep(0.35, 0.9, dot(vvTerrainSafeNormalize(cameraRelativePos, -l), l));
    if (back < 0.004) return vec3(0.0);
    float near = 1.0 - smoothstep(VV_TERRAIN_RIM_FAR * 0.5, VV_TERRAIN_RIM_FAR, vvTerrainEyeDistance(cameraRelativePos));
    if (near < 0.004) return vec3(0.0);
    float thin = 0.0;
    if (vv_canopyMapValid > 0.5)
        thin = 0.6 * (1.0 - smoothstep(0.5, 1.0, vvTerrainCanopyCell((cameraRelativePos + vv_pbrOrigin).xz).x));
    float lit = max(clamp(sunVisibility, 0.0, 1.0), thin) * vvTerrainClearSky();
    if (lit < 0.004) return vec3(0.0);
    float rim = vvTerrainLeafRim();
    if (rim <= 0.0) return vec3(0.0);
    vec3 glow = mix(vvTerrainFloraPigment(albedo), vec3(1.0, 0.95, 0.82), 0.45);
    return glow * (rim * back * near * lit * sun * VV_TERRAIN_RIM_GAIN * clamp(vv_pbrFoliage, 0.0, 2.0));
}

// v14 gust sheen on a plant a gust is crossing: brighter toward the tip and a
// touch paler (the undersides and the cuticle turned up as the plant bends),
// most in sunlight. A multiplier of 1 in calm air, at 0 gusts and at night's
// floor of 0.3 of the effect.
vec3 vvTerrainGustSheen(vec3 color, vec3 cameraRelativePos, float sunVisibility)
{
    int flora = vvTerrainFloraClass();
    if (flora == VV_TFLORA_STIFF || flora == VV_TFLORA_AQUATIC || flora == VV_TFLORA_FRUIT) return color;
    float gust = vvTerrainGust((cameraRelativePos + vv_pbrOrigin).xz);
    if (gust <= 0.0) return color;
    float tip = vvTerrainIsRootedFlora(flora) ? vvTerrainPlantTip(cameraRelativePos) : 1.0;
    float light = mix(0.3, 1.0, clamp(sunVisibility, 0.0, 1.0) * vvTerrainSunPresence());
    float k = gust * mix(0.4, 1.0, tip) * light;
    float y = dot(color, vec3(0.299, 0.587, 0.114));
    return mix(color, vec3(y), 0.12 * k) * (1.0 + VV_TERRAIN_GUST_SHEEN * k);
}

vec4 vvTerrainFloraShade(vec4 litColor, vec3 baseColor, vec3 faceNormal, vec3 cameraRelativePos,
                         float fogAmount, float murkiness)
{
    vec3 v = vvTerrainSafeNormalize(vvTerrainToEye(cameraRelativePos), vec3(0.0, 1.0, 0.0));
    vec3 n = vvTerrainSafeNormalize(faceNormal, vec3(0.0, 1.0, 0.0));
    if (dot(n, v) < 0.0) n = -n;
    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));

    float lod = vvTerrainFloraLod(cameraRelativePos);
    float sunVisibility = vvTerrainSunVisibility();
    float clearAir = clamp(1.0 - fogAmount - murkiness, 0.0, 1.0);
    float wet = vvTerrainWetness(n);
    float snowFrost = clamp(vvTerrainSnow(n) + vv_sceneFrost, 0.0, 1.0);

    // Vanilla's lit leaf is the base; a wet leaf is a darker leaf, and the
    // foot of a plant a shadier one.
    vec3 result = litColor.rgb * mix(1.0, VV_LAYER_WET_LEAF_DARKEN, wet) * vvTerrainContactShade(cameraRelativePos);
    result = vvTerrainGustSheen(result, cameraRelativePos, sunVisibility);
    result = vvTerrainCanopyResolve(result, cameraRelativePos, n, lod, sunVisibility, 0.55);

    vec3 through = vvTerrainFoliageTransmission(baseColor, faceNormal, cameraRelativePos, sunVisibility)
                 * mix(0.45, 1.0, lod);
    result += (vvTerrainFloraFrontScatter(baseColor, n, l, sunVisibility, lod) + through) * clearAir;
    result += vvTerrainBacklitRim(baseColor, cameraRelativePos, l, sunVisibility) * clearAir;
    result += vvTerrainCuticleSpecular(n, l, v, wet, snowFrost, sunVisibility, lod) * clearAir;
    return vec4(clamp(result, 0.0, 8.0), litColor.a);
}

// v14 damp and moss on sheltered faces. Where a rough stone, wood or soil
// face sits under a crown, or in the half-light a few blocks into cover, in a
// climate that rains, it holds moisture and greens over: a little darker and
// a little greener, in pixel-art patches on the face's own texel grid.
// DATA GAP: the game simulates no persistent moisture and no moss growth on
// ordinary blocks, so where it lands is inferred from shelter and the
// climate's rainfall, both the game's. The leaf map only reaches about 28
// blocks, so its share fades out by then and the light-level share carries on.
// Never on metal or polished faces, displaced by snow and frost, never
// brighter overall.
const float VV_TERRAIN_DAMP_DARKEN = 0.84;
const vec3 VV_TERRAIN_MOSS_TINT = vec3(0.86, 1.0, 0.72);
const float VV_TERRAIN_MOSS_SHARE = 0.6;
// Patch grid: half-block cells, 8000 of them across the 4096-block wrap.
const float VV_TERRAIN_MOSS_CELL = 0.512;
const float VV_TERRAIN_MOSS_PERIOD = 8000.0;
float vvTerrainDamp(VvTerrainMaterial m, vec3 cameraRelativePos)
{
    if (vv_pbrDamp < 0.001 || vv_pbrHumidity <= 0.0) return 0.0;
    if (m.metalness > 0.2 || m.perceptualRoughness < 0.45) return 0.0;
    vec3 n = m.faceNormal;
    if (n.y < -0.5) return 0.0;

    vec3 world = cameraRelativePos + vv_pbrOrigin;
    float shelter = 0.0;
    if (vv_canopyMapValid > 0.5)
    {
        vec2 cell = vvTerrainCanopyCell(world.xz);
        float reach = max(abs(cameraRelativePos.x), abs(cameraRelativePos.z));
        if (cell.y > world.y + 0.5) shelter = cell.x * (1.0 - smoothstep(20.0, 28.0, reach));
    }
    float sky = clamp(vv_sunExposure, 0.0, 1.0);
    shelter = max(shelter, smoothstep(0.25, 0.45, sky) * (1.0 - smoothstep(0.7, 0.92, sky)));
    if (shelter <= 0.0) return 0.0;

    float side = n.y > 0.5 ? 0.6 : 1.0;
    float north = 1.0 + 0.3 * clamp(-n.z, 0.0, 1.0);
    float humid = mix(0.35, 1.0, clamp(vv_pbrHumidity, 0.0, 1.0));
    vec3 p = floor((world - n * 0.01) * 16.0) / 16.0 + 1.0 / 32.0;
    vec2 q = abs(n.y) > 0.5 ? p.xz : (abs(n.x) > 0.5 ? p.zy : p.xy);
    float patches = smoothstep(0.35, 0.7, vvTerrainFleckNoise(q, VV_TERRAIN_MOSS_CELL, VV_TERRAIN_MOSS_PERIOD, vec2(97.0, 61.0)));
    float layer = clamp(1.0 - m.snow - m.frost, 0.0, 1.0);
    return clamp(clamp(vv_pbrDamp, 0.0, 1.0) * shelter * side * north * humid * mix(0.45, 1.0, patches) * layer, 0.0, 1.0);
}

vec3 vvTerrainApplyDamp(VvTerrainMaterial m, vec3 diffuse, vec3 cameraRelativePos)
{
    float damp = vvTerrainDamp(m, cameraRelativePos);
    if (damp <= 0.0) return diffuse;
    vec3 luma = vec3(0.299, 0.587, 0.114);
    vec3 moss = diffuse * VV_TERRAIN_MOSS_TINT;
    moss *= dot(diffuse, luma) / max(dot(moss, luma), 1e-4);
    diffuse = mix(diffuse, moss, damp * VV_TERRAIN_MOSS_SHARE);
    return diffuse * mix(1.0, VV_TERRAIN_DAMP_DARKEN, damp);
}

vec3 vvTerrainEmission(VvTerrainMaterial m, float glowLevel, vec3 cameraRelativePos)
{
    if (vv_pbrEnabled < 0.5 || glowLevel <= 0.0) return vec3(0.0);
    return vvEmission(m.baseColor, glowLevel, cameraRelativePos) * m.emissionMask;
}

float vvTerrainEmissiveGlow(float glowLevel, vec2 materialUv)
{
    if (vv_pbrEnabled < 0.5 || glowLevel <= 0.0) return 0.0;
    return vvEmissiveGlow(glowLevel) * vvTerrainSecondaryTexel(materialUv).a;
}

// Torch light falloff (D70, `pbr_torchfalloff`). Vanilla lights a block by
// max(sun, block light) per vertex, and one torch reaches about 14 blocks, so
// a few torches light a small room nearly evenly and nothing pools. This
// steepens that falloff from the game's own per-vertex block light
// (vv_blockLight): bright enough to be right by a flame (FULL) keeps all of
// its light, and below that the light falls as a higher power of itself, so
// the dim edge of a torch's reach gets darker while the flame's surroundings
// do not change. It never adds light. FLOOR keeps the ratio from blowing up
// near darkness, MIN bounds it, and it fades out where daylight reaches,
// where vanilla's brightness is the sun's, not the torch's.
// Zero at vv_pbrTorchFalloff 0, and an unset uniform reads as vanilla.
// Blocks only: creatures and items keep vanilla light. No texture read.
const float VV_TERRAIN_FALLOFF_FULL = 0.85;
const float VV_TERRAIN_FALLOFF_POWER = 2.0;   // extra exponent at the slider's top
const float VV_TERRAIN_FALLOFF_FLOOR = 0.08;
const float VV_TERRAIN_FALLOFF_MIN = 0.4;

float vvTerrainTorchFalloff(vec3 torch)
{
    float amount = clamp(vv_pbrTorchFalloff, 0.0, 1.0);
    if (amount < 0.001) return 1.0;
    float b = (torch.r + torch.g + torch.b) / 3.0;
    if (b <= 0.0 || b >= VV_TERRAIN_FALLOFF_FULL) return 1.0;
    float steep = VV_TERRAIN_FALLOFF_FULL * pow(b / VV_TERRAIN_FALLOFF_FULL, 1.0 + VV_TERRAIN_FALLOFF_POWER * amount);
    float ratio = (steep + VV_TERRAIN_FALLOFF_FLOOR) / (b + VV_TERRAIN_FALLOFF_FLOOR);
    float daylight = clamp(vv_sunExposure, 0.0, 1.0) * vvTerrainSunPresence();
    return mix(1.0, max(ratio, VV_TERRAIN_FALLOFF_MIN), 1.0 - daylight);
}

vec4 vvTerrainMaterialPrimary(vec4 litColor, vec3 baseColor, vec3 faceNormal,
                              vec2 materialUv, vec3 cameraRelativePos,
                              float shadowBrightness, float fogAmount,
                              float murkiness, vec3 environmentColor,
                              vec3 blockLight, float glowLevel)
{
    if (vv_pbrEnabled < 0.5) return litColor;
    // Vanilla's blockLight is the sun and block colours mixed (or unwritten
    // with dynamic lights off); every torch term below wants the torch alone.
    blockLight = vvTerrainTorchLight();  // not vanilla's sun-mixed colour
    litColor.rgb *= vvTerrainTorchFalloff(blockLight);
    if (vvTerrainIsFlora())
        return vvTerrainFloraShade(litColor, baseColor, faceNormal, cameraRelativePos,
                                   fogAmount, murkiness);

    VvTerrainMaterial m = vvDecodeTerrainMaterial(litColor, baseColor, faceNormal,
                                                  materialUv, cameraRelativePos);
    vec3 viewDir = vvTerrainSafeNormalize(vvTerrainToEye(cameraRelativePos), m.faceNormal);

    float directionalRelief = vvDirectionalShadeTerrain(m.normal) -
                              vvDirectionalShadeTerrain(m.faceNormal);
    float relief = 1.0 + directionalRelief * 0.32 * m.detailFade;
    float heightCavity = mix(0.92, 1.06, m.height);
    float indirectAo = mix(1.0, m.ao * heightCavity, clamp(vv_pbrCavity, 0.0, 1.0));

    vec3 dielectricF0 = vec3(VV_DIELECTRIC_F0) * mix(0.6, 1.8, m.specularFactor);
    vec3 f0 = mix(dielectricF0, m.baseColor, m.metalness);
    // A metal has no diffuse, and on this path its body colour has to come
    // from somewhere: the ambient highlight alone, scaled by the roughness
    // fade and the ambient slider, left a rough gold block near black (seen
    // in game 2026-11-24, D63). Vanilla's lit colour IS the texture's albedo
    // under the scene's light, and a metal's albedo is its reflectance, so
    // the metal keeps that as its body - less of it the smoother it is,
    // because a polished metal shows its reflection instead.
    // Diffuse gives up what Fresnel adds at a glancing view (reflection
    // phase 0). Head-on it keeps the old (1 - 0.35 F0), so vanilla's look is
    // unchanged facing a block; toward grazing, the reflected share rises to
    // F(view) and the diffuse loses exactly that rise, so a wet floor at a low
    // angle no longer shows its diffuse AND a near-full reflection on top.
    vec3 grazingShare = vvTerrainGrazingShare(m, viewDir, f0);
    vec3 diffuseEnergy = max(vec3(1.0) - f0 * 0.35 - grazingShare, vec3(0.0)) * (1.0 - m.metalness)
                       + vec3(m.metalness * mix(VV_TERRAIN_METAL_BODY_SMOOTH, VV_TERRAIN_METAL_BODY_ROUGH,
                                                m.perceptualRoughness));

    vec3 diffuse = litColor.rgb * diffuseEnergy * relief;
    diffuse *= mix(1.0, max(indirectAo, 0.82), 0.35);
    diffuse = vvTerrainApplySurfaceLayers(m, diffuse);
    diffuse = vvTerrainApplyDamp(m, diffuse, cameraRelativePos);
    diffuse = vvTerrainCanopyResolve(diffuse, cameraRelativePos, m.faceNormal, m.detailFade, -1.0, 0.85);
    diffuse = max(diffuse + vvTerrainBlockRelief(m, litColor.rgb, blockLight, cameraRelativePos, materialUv), vec3(0.0));

    vec3 directSpec = vvTerrainDirectSpecular(m, viewDir, shadowBrightness, fogAmount, murkiness, materialUv);
    vec3 blockSpec = vvTerrainBlockSpecular(m, viewDir, blockLight, cameraRelativePos);
    VvTerrainReflection reflection = vvTerrainPixelReflection(m, cameraRelativePos, materialUv,
                                                              environmentColor, litColor.rgb, viewDir, blockLight);
    vec3 ambientSpec = vvTerrainAmbientSpecular(m, viewDir, reflection.image);

    // Multiple-scattering compensation is real only where the lobe is wide and
    // the surface coloured: rough metals. It used to lift every highlight and
    // reflection by up to 18%, a polished wet floor included (phase 0).
    float energyCap = 1.0 + 0.18 * clamp(vv_pbrEnergyCompensation, 0.0, 1.0)
                          * m.metalness * m.perceptualRoughness;
    vec3 color = diffuse + (directSpec + blockSpec + ambientSpec) * energyCap;
    color += vvTerrainEmission(m, glowLevel, cameraRelativePos);
    return vec4(clamp(color, 0.0, 8.0), litColor.a);
}

vec3 vvTerrainPrimaryGrid(vec2 materialUv)
{
    vec2 size = max(vec2(1.0), vec2(textureSize(vv_materialTex, 0)));
    vec2 cell = fract(materialUv * size);
    float line = 1.0 - smoothstep(0.0, 0.08, min(min(cell.x, cell.y), min(1.0 - cell.x, 1.0 - cell.y)));
    float footprint = max(length(dFdx(materialUv) * size), length(dFdy(materialUv) * size));
    vec3 base = footprint <= 1.0 ? vec3(0.0, 0.65, 0.25)
              : footprint <= 2.0 ? vec3(0.9, 0.75, 0.05)
              : vec3(0.9, 0.1, 0.05);
    return mix(base, vec3(1.0), line * 0.65);
}

vec4 vvTerrainMaterialPrimaryDebug(vec4 color, vec3 baseColor, vec3 faceNormal,
                                   vec2 materialUv, vec3 cameraRelativePos,
                                   float shadowBrightness, float fogAmount,
                                   float murkiness, vec3 environmentColor,
                                   vec3 blockLight, float glowLevel)
{
    int mode = int(vv_pbrDebugView + 0.5);
    if (mode == 0) return color;
    blockLight = vvTerrainTorchLight(); // as the shading reads it

    // Shading path: which code is actually shading each terrain fragment.
    // Blue is plant tissue on the foliage path (vanilla plus transmission only),
    // orange is a block shaded without the second material atlas, green is
    // the full modular material. Read this before blaming a material view.
    if (mode == 63)
    {
        float luma = clamp(dot(color.rgb, vec3(0.299, 0.587, 0.114)), 0.0, 1.0);
        vec3 path = vvTerrainIsFlora() ? vec3(0.10, 0.40, 1.00)
                  : vv_material2Valid < 0.5 ? vec3(1.00, 0.55, 0.05)
                  : vec3(0.10, 0.85, 0.25);
        return vec4(path * mix(0.45, 1.0, luma), color.a);
    }

    if (mode == 13)
        return vec4(vvTerrainFoliageTransmission(vec3(1.0), faceNormal, cameraRelativePos, vvTerrainSunVisibility()), color.a);
    if (mode == 44)
    {
        int flora = vvTerrainFloraClass();
        if (flora == VV_TFLORA_NONE)    return vec4(vec3(0.0), color.a);
        if (flora == VV_TFLORA_LEAVES)  return vec4(1.0, 0.1, 0.1, color.a);
        if (flora == VV_TFLORA_GRASS)   return vec4(0.1, 1.0, 0.1, color.a);
        if (flora == VV_TFLORA_HERB)    return vec4(1.0, 0.1, 1.0, color.a);
        if (flora == VV_TFLORA_CROP)    return vec4(1.0, 1.0, 0.1, color.a);
        if (flora == VV_TFLORA_REED)    return vec4(0.1, 1.0, 1.0, color.a);
        if (flora == VV_TFLORA_FRUIT)   return vec4(1.0, 0.5, 0.0, color.a);
        if (flora == VV_TFLORA_BUSH)    return vec4(0.1, 0.1, 1.0, color.a);
        if (flora == VV_TFLORA_VINE)    return vec4(0.5, 0.0, 1.0, color.a);
        if (flora == VV_TFLORA_AQUATIC) return vec4(0.0, 0.6, 0.6, color.a);
        return vec4(vec3(0.45), color.a);
    }

    // Canopy views, for flora and opaque blocks alike: both receive dapple.
    // 15 is white where nothing is taken; 25 is the shadow map's own answer;
    // 29 is the gate; 30 and 31 are the ring count the gate is built on.
    if (mode == 15)
    {
        float fade = vvTerrainIsFlora() ? vvTerrainFloraLod(cameraRelativePos) : vvTerrainDetailFade(cameraRelativePos);
        float d = vvTerrainCanopyDapple(cameraRelativePos, fade, -1.0, vvTerrainCanopyOverhead(cameraRelativePos).x, -1.0);
        return vec4(vec3(1.0 - d / VV_TERRAIN_DAPPLE_SHADE), color.a);
    }
    if (mode == 25) return vec4(vec3(vvTerrainSunVisibility()), color.a);
    if (mode == 29) return vec4(vec3(vvTerrainCanopyEvidence(vvTerrainSunVisibility())), color.a);
    if (mode == 30)
    {
        float r = clamp(vv_pbrCanopyRadius, 0.0, 16.0);
        return vec4(vvTerrainCanopyStructure(max(1.0, r * 0.6)),
                    vvTerrainCanopyStructure(r),
                    vvTerrainCanopyStructure(min(16.0, r * 1.8)), color.a);
    }
    if (mode == 31)
    {
        float mean;
        float count = vvTerrainCanopyVariation(clamp(vv_pbrCanopyRadius, 0.0, 16.0), mean);
        if (count < 1.0) return vec4(0.0, 0.0, 0.0, color.a);
        if (count < 3.0) return vec4(1.0, 0.0, 0.0, color.a);
        if (count < 5.0) return vec4(0.0, 1.0, 0.0, color.a);
        return vec4(0.0, 0.4, 1.0, color.a);
    }
    // 64: sunflecks. White where a fleck lands, orange its bounce light, dim
    // red where leaves are overhead along the sun ray but no fleck, black
    // elsewhere.
    if (mode == 64)
    {
        // The canopy itself never takes flecks; dark blue says so, so the
        // view does not show flecks on leaves that normal rendering skips.
        if (!vvTerrainIsCanopyReceiver()) return vec4(0.0, 0.0, 0.25, color.a);
        float vis = vvTerrainSunVisibility();
        vec3 overhead = vvTerrainCanopyOverhead(cameraRelativePos);
        vec2 f = vvTerrainSunfleck(cameraRelativePos, faceNormal, overhead, 1.0 - vis, 1.0);
        vec3 base = mix(vec3(overhead.x > 0.0 ? 0.35 : 0.0, 0.0, 0.0), vec3(1.0, 0.55, 0.1), clamp(f.y / VV_TERRAIN_FLECK_BOUNCE, 0.0, 1.0));
        return vec4(mix(base, vec3(1.0), f.x), color.a);
    }
    // 66: sun shaft sources. Yellow where a leaf marks vanilla's god-ray
    // channel, scaled to the cap; blue on leaves that could but do not.
    // Measured at vanilla's default fog, so fog's boost is not shown.
    if (mode == 66)
    {
        if (vvTerrainFloraClass() != VV_TFLORA_LEAVES) return vec4(vec3(0.0), color.a);
        float s = vvTerrainShaftLevel(cameraRelativePos, 0.00125) / VV_TERRAIN_SHAFT_MAX;
        return vec4(mix(vec3(0.0, 0.0, 0.35), vec3(1.0, 0.9, 0.2), s), color.a);
    }
    // 67: the light the flecks run on. Red is the clear-sky gate (black means
    // too cloudy for flecks), green the wind shimmer, blue the moon's share.
    // 68: beams in the air - the light the beam march adds, white at its cap.
    if (mode == 68)
        return vec4(vec3(vvTerrainBeamInscatter(cameraRelativePos, 0.00125) / VV_TERRAIN_BEAM_MAX), color.a);
    if (mode == 67)
        return vec4(vvTerrainClearSky(), vvTerrainFleckShimmer(),
                    (1.0 - vvTerrainSunPresence()) * clamp(vv_sceneMoonLight, 0.0, 1.0), color.a);
    // v14 views. 69: gusts - white in a gust's heart, blue the wind
    // direction's x and z where calm. 71: plant foot - red the contact
    // shade taken, green the tip height. 72: backlit rim - white the rim
    // light added, at twice its cap.
    if (mode == 69)
    {
        vec3 world = cameraRelativePos + vv_pbrOrigin;
        vec2 d = vvTerrainWindDir();
        return vec4(mix(vec3(0.0, 0.25 + 0.25 * d.x, 0.25 + 0.25 * d.y), vec3(1.0), vvTerrainGust(world.xz)), color.a);
    }
    if (mode == 71)
    {
        if (!vvTerrainIsRootedFlora(vvTerrainFloraClass())) return vec4(vec3(0.0), color.a);
        return vec4((1.0 - vvTerrainContactShade(cameraRelativePos)) / VV_TERRAIN_CONTACT_DEPTH,
                    vvTerrainPlantTip(cameraRelativePos), 0.0, color.a);
    }
    if (mode == 72)
    {
        vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));
        vec3 rim = vvTerrainBacklitRim(vec3(1.0), cameraRelativePos, l, vvTerrainSunVisibility());
        return vec4(vec3(clamp(dot(rim, vec3(0.333)) / VV_TERRAIN_RIM_GAIN * 0.5, 0.0, 1.0)), color.a);
    }
    // 65: the leaf map. Green is leaf density along the sun ray, blue its
    // height (white at 16 blocks); grey means the map is not uploaded.
    if (mode == 65)
    {
        if (vv_canopyMapValid < 0.5) return vec4(vec3(0.5), color.a);
        vec3 overhead = vvTerrainCanopyOverhead(cameraRelativePos);
        return vec4(0.0, overhead.x, clamp(overhead.y / 16.0, 0.0, 1.0), color.a);
    }
    // 47: optical role. Red is the canopy (the occluder), blue a surface that
    // does not take dapple, green one that does.
    if (mode == 47)
    {
        if (vvTerrainFloraClass() == VV_TFLORA_LEAVES) return vec4(1.0, 0.0, 0.0, color.a);
        if (!vvTerrainIsCanopyReceiver()) return vec4(0.0, 0.0, 1.0, color.a);
        return vec4(0.0, 1.0, 0.0, color.a);
    }

    if (vvTerrainIsFlora()) return color;

    VvTerrainMaterial m = vvDecodeTerrainMaterial(color, baseColor, faceNormal,
                                                  materialUv, cameraRelativePos);
    vec3 viewDir = vvTerrainSafeNormalize(vvTerrainToEye(cameraRelativePos), m.faceNormal);
    vec3 f0 = mix(vec3(VV_DIELECTRIC_F0) * mix(0.6, 1.8, m.specularFactor),
                  m.baseColor, m.metalness);

    if (mode == 1) return vec4(m.normal * 0.5 + 0.5, color.a);
    if (mode == 2) return vec4(vec3(m.perceptualRoughness), color.a);
    if (mode == 3) return vec4(vec3(m.specularFactor), color.a);
    if (mode == 4) return vec4(vec3(0.5 + 0.5 * (vvDirectionalShadeTerrain(m.normal) - vvDirectionalShadeTerrain(m.faceNormal))), color.a);
    if (mode == 5) return vec4(vvTerrainDirectSpecular(m, viewDir, shadowBrightness, fogAmount, murkiness, materialUv), color.a);
    if (mode == 7) return vec4(f0, color.a);
    if (mode == 8) return vec4(vec3(m.alphaRoughness), color.a);
    if (mode == 10) return vec4(vec3(m.wetness), color.a);
    if (mode == 11)
    {
        vec2 slope = vvTerrainRippleField(m.faceNormal, cameraRelativePos, m.wetness, m.detailFade);
        return vec4(slope * 0.5 + 0.5, m.wetness, color.a);
    }
    if (mode == 16) return vec4(vec3(clamp(vv_sunExposure, 0.0, 1.0)), color.a);
    // 70: damp - green where a face takes damp and moss, by how much.
    if (mode == 70) return vec4(0.0, vvTerrainDamp(m, cameraRelativePos), 0.0, color.a);
    if (mode == 12) return vec4(vec3(m.ao * mix(0.92, 1.06, m.height)), color.a);
    if (mode == 14) return vec4(vvTerrainEmission(m, glowLevel, cameraRelativePos), color.a);
    if (mode == 19) return vec4(vec3(m.metalness), color.a);
    if (mode == 20) return vec4(vec3(m.height), color.a);
    if (mode == 21) return vec4(vec3(m.ao), color.a);
    if (mode == 22) return vec4(vec3(m.emissionMask), color.a);
    if (mode == 24)
    {
        vec3 grain = vvTerrainGrainDirection(materialUv);
        return vec4(grain.xy * 0.5 + 0.5, grain.z * clamp(m.specularFactor * 2.0, 0.0, 1.0), color.a);
    }
    if (mode == 33) return vec4(vvTerrainPrimaryGrid(materialUv), color.a);

    // 9: the recovered block-light direction, as a colour. Flat means the
    // light is being treated as coming straight off the surface; a gradient
    // across a wall means a torch is being located.
    if (mode == 9)
    {
        vec3 l = vvBlockLightDirection(m.faceNormal, cameraRelativePos, dot(blockLight, VV_PBR_LUMA));
        return vec4(l * 0.5 + 0.5, color.a);
    }

    // 73 the crevice maps (D67), static, whatever the weather: grey the
    // stone shape, blue the joint network by depth, cyan the rain paths on a
    // wall by how much they gather. Black where the material holds no
    // water. Speckle here means the load-time maps are wrong; a clean net of
    // joints around grey pillows means they are right.
    if (mode == 73)
    {
        if (m.holdsWater < 0.5) return vec4(vec3(0.0), color.a);
        vec3 view = vec3(0.15 + 0.5 * m.height);
        view = mix(view, vec3(0.10, 0.35, 1.00), m.jointPool);
        if (abs(m.faceNormal.y) < 0.3)
            view = mix(view, vec3(0.20, 1.00, 0.95), m.flowAmount * 0.85);
        return vec4(view, color.a);
    }

    // 75 water in crevices, live (D65): grey the damp film, blue standing
    // water on a top face, cyan water running down a wall, by how much.
    if (mode == 75)
    {
        vec3 film = vec3(clamp(m.wetness, 0.0, 1.0) * 0.35);
        vec3 view = mix(film, vec3(0.10, 0.35, 1.00), clamp(m.pool, 0.0, 1.0));
        view = mix(view, vec3(0.20, 1.00, 0.95), clamp(m.stream, 0.0, 1.0));
        return vec4(view, color.a);
    }

    // 74 torchlight on the normals (D65, D66): the terrain lit by block light
    // ALONE, as untextured clay, with exactly the relief the shading adds
    // (vvTerrainTorchShade) and the torch highlight on top in its own
    // colour. Joints dark and stone faces turned to the flame bright is the
    // relief being shaded; a flat even face would mean it is not. Black where no block light reaches. Stand by a torch at night.
    if (mode == 74)
    {
        float intensity = dot(blockLight, VV_PBR_LUMA);
        vec3 toTorch = vvBlockLightDirection(m.faceNormal, cameraRelativePos, intensity);
        vec3 v = vvTerrainSafeNormalize(vvTerrainToEye(cameraRelativePos), m.faceNormal);
        float shade = vvTerrainTorchShade(m, toTorch, vvTerrainTorchNormal(m, materialUv));
        vec3 clay = vec3(clamp(intensity * (0.55 + 1.2 * shade), 0.0, 1.0));
        vec3 glint = vvTerrainBlockSpecular(m, v, blockLight, cameraRelativePos);
        return vec4(clamp(clay + glint, 0.0, 1.0), color.a);
    }

    // Pixel reflection views (D62). 32 the direction each texel reflects,
    // 34 the image it sees, 35 how much of that reaches the pixel (x4),
    // 36 where the analytic sky stands in (white) for the captured scene
    // (black), 37 how coarse roughness made the direction cells, 39 the
    // scene's validity (green real reflection, red a miss, dark blue the
    // march was skipped as not worth its cost, blue no capture), 48 why
    // the march ended.
    if (mode == 32 || mode == 34 || mode == 35 || mode == 36 || mode == 37 ||
        mode == 39 || mode == 48)
    {
        VvTerrainReflection rf = vvTerrainPixelReflection(m, cameraRelativePos, materialUv,
                                                          environmentColor, color.rgb, viewDir, blockLight);
        if (mode == 32) return vec4(rf.direction * 0.5 + 0.5, color.a);
        if (mode == 34) return vec4(rf.image, color.a);
        if (mode == 35) return vec4(vvTerrainAmbientSpecular(m, viewDir, rf.image) * 4.0, color.a);
        if (mode == 36) return vec4(vec3(1.0 - rf.scene), color.a);
        if (mode == 37) return vec4(vec3((VV_REFLECT_CELLS_SHARP - rf.cells) /
                                         (VV_REFLECT_CELLS_SHARP - VV_REFLECT_CELLS_ROUGH)), color.a);
        if (mode == 39)
        {
            if (rf.reason == VV_REFLECT_NO_SOURCE) return vec4(0.0, 0.0, 0.6, color.a);
            if (rf.reason == VV_REFLECT_SKIPPED) return vec4(0.0, 0.0, 0.18, color.a);
            return vec4(mix(vec3(0.85, 0.08, 0.05), vec3(0.1, 0.9, 0.15), rf.scene), color.a);
        }
        // 48: green hit, yellow found a surface and rejected it as too thick,
        // red never crossed anything, orange the far end unusable, dark red
        // the origin off the captured frame, magenta pointing back at the
        // camera, dark blue skipped as not worth a march, blue no capture.
        float why = rf.reason;
        vec3 c = why == VV_REFLECT_HIT        ? vec3(0.1, 0.9, 0.15)
               : why == VV_REFLECT_TOO_THICK  ? vec3(0.95, 0.85, 0.1)
               : why == VV_REFLECT_NO_CROSSING? vec3(0.9, 0.1, 0.05)
               : why == VV_REFLECT_FAR_OFF    ? vec3(1.0, 0.5, 0.05)
               : why == VV_REFLECT_ORIGIN_OFF ? vec3(0.45, 0.02, 0.02)
               : why == VV_REFLECT_FACING     ? vec3(0.9, 0.1, 0.9)
               : why == VV_REFLECT_SKIPPED    ? vec3(0.0, 0.0, 0.18)
               :                             vec3(0.0, 0.0, 0.6);
        return vec4(c, color.a);
    }
    if (mode == 52)
    {
        vec2 size = max(vec2(1.0), vec2(textureSize(vv_materialTex, 0)));
        float footprint = max(length(dFdx(materialUv) * size), length(dFdy(materialUv) * size));
        float nearOne = 1.0 - smoothstep(0.25, 1.0, abs(footprint - 1.0));
        vec3 colorRamp = footprint < 1.0
            ? mix(vec3(0.0, 0.25, 0.8), vec3(0.0, 0.8, 0.2), footprint)
            : mix(vec3(0.0, 0.8, 0.2), vec3(0.95, 0.1, 0.05), clamp((footprint - 1.0) / 3.0, 0.0, 1.0));
        return vec4(mix(colorRamp, vec3(1.0, 0.85, 0.05), nearOne * 0.55), color.a);
    }

    return color;
}
