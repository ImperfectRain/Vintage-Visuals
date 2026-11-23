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
uniform vec3 vv_pbrOrigin;           // camera world position, wrapped to VV_TERRAIN_ORIGIN_PERIOD on the CPU

// Vanilla's per-vertex sun light level (rgbaLightIn.a), 0 under a roof and 1 in
// the open. This group writes it in the vertex shader of the same program, so
// both halves of the varying apply or roll back together.
in float vv_sunExposure;

uniform float vv_pbrFoliage;   // light passing through leaves; 0 is vanilla
uniform float vv_pbrDapple;        // canopy shade between sunflecks; 0 is vanilla
uniform float vv_pbrCanopyRadius;  // shadow-map ring radius in texels; 0 disables the canopy test
uniform float vv_pbrSunflecks;     // sunflecks inside canopy shade; 0 is vanilla
uniform float vv_pbrMoonFlecks;    // the moon's faint version of them; 0 is none
uniform float vv_pbrShafts;        // beams from real gaps into vanilla's god-ray pass; 0 is vanilla

// The leaf map: for each 2x2-block cell around the player, how much leaf is
// overhead and the world Y of the lowest leaf, read from the world's own blocks
// on the CPU (src/Common/Scene/CanopyMap.cs). A uniform array, not a sampler,
// so it cannot shift vanilla's texture units. Two 12-bit cells per float.
// vv_canopyMapValid 0 means "no map" and every consumer draws vanilla.
uniform vec4 vv_canopyMap[128];
uniform float vv_canopyMapValid;
uniform float vv_canopyPlaneY;   // world Y the fleck pattern is projected from

const float VV_TERRAIN_MIN_ROUGHNESS = 0.18;

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
};

vec3 vvTerrainSafeNormalize(vec3 v, vec3 fallback)
{
    float len2 = dot(v, v);
    if (!(len2 > 1e-12) || len2 > 1e24) return fallback;
    return v * inversesqrt(len2);
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
    return clamp((far - length(cameraRelativePos)) / max(1.0, far - near), 0.0, 1.0);
}

vec3 vvTerrainNormalFromTexel(vec4 primary, vec3 faceNormal, float detailFade)
{
    vec2 xy = (primary.rg * 2.0 - 1.0) * max(0.0, vv_pbrNormalStrength) * detailFade;
    float z = sqrt(max(1e-4, 1.0 - dot(xy, xy)));
    return vvTerrainSafeNormalize(vvTerrainFrame(faceNormal) * vec3(xy, z), faceNormal);
}

float vvTerrainFilteredRoughness(float perceptualRoughness, vec3 n)
{
    if (vv_pbrSpecularAA < 0.5) return perceptualRoughness;
    vec3 dx = dFdx(n);
    vec3 dy = dFdy(n);
    float variance = clamp(max(dot(dx, dx), dot(dy, dy)), 0.0, 1.0);
    return clamp(sqrt(perceptualRoughness * perceptualRoughness + variance * 0.32),
                 VV_TERRAIN_MIN_ROUGHNESS, 1.0);
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
vec2 vvTerrainRippleSlope(vec2 p, float t, float density)
{
    vec2 cell = floor(p);
    vec2 local = fract(p);
    vec4 rnd = vvTerrainCellRandom(cell);
    if (rnd.x > density) return vec2(0.0);

    vec2 centre = vec2(0.5) + (rnd.yz - 0.5) * VV_TERRAIN_RIPPLE_JITTER;
    float reach = 0.5 - VV_TERRAIN_RIPPLE_JITTER * 0.5;
    vec2 offset = local - centre;
    float r = length(offset);
    if (r > reach) return vec2(0.0);

    // Whole drops per wrap of the 0..1 clock, so the wrap cannot jump.
    float rate = 1.0 + floor(rnd.w * 3.0);
    float age = fract(t * rate + rnd.y * 0.61 + rnd.z * 0.39);
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
    return vvTerrainRippleSlope(p * 2.0, t, density)
         + vvTerrainRippleSlope(p + vec2(31.43, 17.61), t * 0.5 + 0.37, density) * 0.7;
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
VvTerrainMaterial vvTerrainApplyWeather(VvTerrainMaterial m, vec3 cameraRelativePos)
{
    m.wetness = vvTerrainWetness(m.faceNormal);
    m.snow = vvTerrainSnow(m.faceNormal);
    m.frost = clamp(vv_sceneFrost, 0.0, 1.0);

    vec2 slope = vvTerrainRippleField(m.faceNormal, cameraRelativePos, m.wetness, m.detailFade);
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
        m.perceptualRoughness = clamp(layered.roughness, VV_TERRAIN_MIN_ROUGHNESS, 1.0);
        m.alphaRoughness = max(VV_TERRAIN_MIN_ROUGHNESS * VV_TERRAIN_MIN_ROUGHNESS,
                               m.perceptualRoughness * m.perceptualRoughness);
        m.specularFactor = clamp(layered.specular, 0.0, 1.0);
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
    m.alphaRoughness = max(VV_TERRAIN_MIN_ROUGHNESS * VV_TERRAIN_MIN_ROUGHNESS,
                           m.perceptualRoughness * m.perceptualRoughness);
    m.specularFactor = clamp(primary.a * max(0.0, vv_pbrSpecularStrength), 0.0, 1.0);
    m.metalness = clamp(secondary.r * max(0.0, vv_pbrMetalResponse), 0.0, 1.0);
    m.height = clamp(secondary.g, 0.0, 1.0);
    m.ao = clamp(secondary.b, 0.18, 1.0);
    m.emissionMask = vv_material2Valid < 0.5 ? 1.0 : clamp(secondary.a, 0.0, 1.0);
    m.detailFade = detailFade;
    m.wetness = 0.0;
    m.snow = 0.0;
    m.frost = 0.0;
    return vvTerrainApplyWeather(m, cameraRelativePos);
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

vec3 vvTerrainBlockSpecular(VvTerrainMaterial m, vec3 viewDir, vec3 blockLight)
{
    float localLight = clamp(max(max(blockLight.r, blockLight.g), blockLight.b), 0.0, 1.0);
    if (localLight <= 0.002 && vv_sceneArtificialLight <= 0.002) return vec3(0.0);

    vec3 l = vvTerrainSafeNormalize(blockLight + vec3(0.08, 0.28, 0.08), vec3(0.0, 1.0, 0.0));
    vec3 h = vvTerrainSafeNormalize(l + viewDir, m.normal);
    float ndotl = max(dot(m.normal, l), 0.0);
    float ndotv = max(dot(m.normal, viewDir), 0.0);
    float ndoth = max(dot(m.normal, h), 0.0);
    float vdoth = max(dot(viewDir, h), 0.0);
    vec3 f0 = mix(vec3(VV_DIELECTRIC_F0) * mix(0.6, 1.8, m.specularFactor),
                  m.baseColor, m.metalness);
    vec3 f = vvFresnelSchlickTerrain(vdoth, f0);
    float d = vvDistributionGGXTerrain(ndoth, max(m.alphaRoughness, 0.08));
    float g = vvGeometrySmithTerrain(ndotv, ndotl, max(m.perceptualRoughness, 0.28));
    vec3 spec = (d * g * f) / max(4.0 * ndotv * ndotl, 0.16);
    return spec * ndotl * max(localLight, vv_sceneArtificialLight * 0.35) * blockLight;
}

vec3 vvTerrainAmbientSpecular(VvTerrainMaterial m, vec3 viewDir, vec3 environmentColor)
{
    vec3 r = reflect(-viewDir, m.normal);
    float skyFacing = clamp(r.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 f0 = mix(vec3(VV_DIELECTRIC_F0) * mix(0.6, 1.8, m.specularFactor),
                  m.baseColor, m.metalness);
    vec3 f = vvFresnelSchlickTerrain(max(dot(m.normal, viewDir), 0.0), f0);
    float roughFade = (1.0 - m.perceptualRoughness) * (1.0 - m.perceptualRoughness);
    float specOcc = mix(m.ao, clamp(pow(max(m.ao, 0.02), 1.0 + roughFade), 0.0, 1.0),
                        clamp(vv_pbrSpecOcclusion, 0.0, 1.0));
    vec3 env = mix(environmentColor * 0.48, environmentColor, skyFacing);
    return env * f * roughFade * specOcc * max(0.0, vv_pbrAmbient);
}

vec3 vvTerrainApplySurfaceLayers(VvTerrainMaterial m, vec3 diffuse)
{
    // Snow and frost displace water, as in the shared layer stack.
    float wet = m.wetness * clamp(1.0 - m.snow - m.frost, 0.0, 1.0);

    diffuse *= mix(1.0, VV_LAYER_WET_DARKEN, wet * (1.0 - m.metalness));
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
// slider, the green that light picks up on the way through, and the distance
// over which the shadow-map count stops resolving canopy structure.
const float VV_TERRAIN_DAPPLE_SHADE = 0.28;
const float VV_TERRAIN_DAPPLE_GREEN = 0.055;
const float VV_TERRAIN_DAPPLE_FADE_START = 22.0;
const float VV_TERRAIN_DAPPLE_FADE_RANGE = 22.0;
// How much of the dapple's full shade a solid crown takes, known only from the
// leaf map. Below 1 so the measured broken shade stays the stronger reading.
const float VV_TERRAIN_LEAF_SHADE = 0.7;
// Sunflecks: the gap grid and the coarser grid gaps cluster on, on the canopy
// plane (both must divide the 4096 world wrap exactly: 4096 / 2.048 = 2000,
// 4096 / 8.192 = 500), the least stretch a low sun may apply (see
// vvTerrainFleckPattern), texels per block, and the distance flecks dissolve
// over - finished before the leaf map's 28-block reach runs out.
const float VV_TERRAIN_FLECK_CELL = 2.048;
const float VV_TERRAIN_FLECK_PERIOD = 2000.0;
const float VV_TERRAIN_FLECK_CLUSTER_CELL = 8.192;
const float VV_TERRAIN_FLECK_CLUSTER_PERIOD = 500.0;
const float VV_TERRAIN_FLECK_MIN_STRETCH = 0.3;
const float VV_TERRAIN_FLECK_TEXELS = 16.0;
// How hard the slider drives a fleck (the default 0.6 reaches a full gap at
// 1.6), and how much richer a sunlit patch is than the shade around it: a
// touch, so it reads as grass in the sun rather than a pale stain (v4 read as
// dry grass), and not the 1.35 that read as neon in v3. The smooth wash v4
// laid under thin crowns is gone: it lifted the shade toward grey. The fleck
// distance is HORIZONTAL (the leaf map's own measure), so flying above a
// forest does not fade it.
const float VV_TERRAIN_FLECK_GAIN = 1.6;
const float VV_TERRAIN_FLECK_SATURATION = 1.08;
// Bounce light at a pool's edge, at most this share of the way to the lit
// colour. Penumbra: field units of rim per block of rise above the floor,
// about half a degree of sun.
const float VV_TERRAIN_FLECK_BOUNCE = 0.07;
// Lit share of the cells outside a cluster: specks and a rare lone gap.
const float VV_TERRAIN_FLECK_SCATTER = 0.12;
// How far the pool threshold wanders, which ragges the outline.
const float VV_TERRAIN_FLECK_RAGGED = 0.36;
// Lobe noise on the pool outline: 0.512-block cells, tiling the 4096-block
// world wrap in 8000 cells; per-texel jitter tiles it in 65536 texels.
const float VV_TERRAIN_FLECK_LOBE_CELL = 0.512;
const float VV_TERRAIN_FLECK_LOBE_PERIOD = 8000.0;
const float VV_TERRAIN_FLECK_TEXEL_PERIOD = 65536.0;
// Cloud cover over which the flecks close: an image of the sun needs the sun.
const float VV_TERRAIN_FLECK_OVERCAST_START = 0.45;
const float VV_TERRAIN_FLECK_OVERCAST_END = 0.65;
// The moon's flecks at full slider and full moon, as a share of the sun's.
const float VV_TERRAIN_MOON_FLECK = 0.45;
// The most a shaft may mark vanilla's god-ray channel, so a beam can never
// hide what is behind it.
const float VV_TERRAIN_SHAFT_MAX = 0.35;
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

    float resolvable = clamp(1.0 - (length(cameraRelativePos) - VV_TERRAIN_DAPPLE_FADE_START)
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
    return VV_TERRAIN_DAPPLE_SHADE * resolvable * fade * under * crown * sun * mix(1.0, VV_OVERCAST_DIRECT, overcast)
         * clamp(vv_pbrDapple, 0.0, 2.0);
}

// Applies the canopy term: darker, and green on the shaded fraction only.
// Torchlight did not come through a leaf, so the local share is spared.
vec3 vvTerrainApplyCanopy(vec3 color, float shaded, float cap)
{
    float canopy = clamp(shaded, 0.0, cap) * (1.0 - vvTerrainLocalLightShare());
    if (canopy <= 0.0) return color;
    float tint = canopy * VV_TERRAIN_DAPPLE_GREEN;
    return color * (1.0 - canopy) * vec3(1.0 - tint, 1.0 + tint * 0.6, 1.0 - tint * 0.7);
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
    int ix = ((c.x % 32) + 32) % 32;
    int iz = ((c.y % 32) + 32) % 32;
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
// height above the point of the step that saw the most (debug view only).
// Zero when there are none, when the map is absent, or when the ray leaves the
// map (which is toroidal, so reading past its edge would return another
// place's leaves).
//
// CONTINUOUS in the fragment's position, because every straight line in a
// fleck has come from a step here: the four nearest cells are blended with a
// smooth weight, the height test is soft, every step is taken and the
// strongest kept (an early return switched steps between neighbouring
// fragments), and the density eases out before the map's edge.
vec2 vvTerrainCanopyOverhead(vec3 cameraRelativePos)
{
    if (vv_canopyMapValid < 0.5) return vec2(0.0);
    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));
    if (l.y < 0.06) return vec2(0.0);

    vec3 world = cameraRelativePos + vv_pbrOrigin;
    vec2 best = vec2(0.0);
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
        float density = mix(mix(a, b, f.x), mix(c, d, f.x), f.y) * (1.0 - smoothstep(20.0, 28.0, reach));
        if (density > best.x) best = vec2(density, p.y - world.y);
    }
    return best;
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

// The offset from a feature on the canopy plane to `src`, with the part along
// the sun azimuth shortened by `stretch` (sin elevation), which lengthens
// everything a low sun projects.
vec2 vvTerrainFleckOffset(vec2 src, vec2 feature, vec2 az, float stretch)
{
    vec2 d = src - feature;
    return d - az * dot(d, az) * (1.0 - stretch);
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

// The sun images through the gaps on the canopy plane.
//
// x: brightness, 0, 0.5 (a speck) or 1.
// y: how close an unlit point is to a pool, 0..1, for the bounce light.
// z: how thin the crown is here, 0..1.
//
// GAPS are soft round kernels summed into one field and thresholded, so
// neighbouring gaps run together into pools the way light through a thin
// crown does. A cluster mask a few cells wide decides where gaps open at all,
// so pools come in groups under thin patches of crown with plain shade
// between them, and inside a group they grow until they merge. Each kernel is
// stretched along the sun's azimuth by 1 / sin(elevation), so a low sun
// draws ellipses pointing away from it. Most gaps are small; a cubic size
// distribution leaves a few large. SPECKS, one in two cells, are a few texels
// across and survive a closed crown. The threshold wanders with a lobe noise
// and a per-texel jitter, so outlines are ragged and pixel-stepped rather
// than circles. There is one hard level: the v5 half-lit rim and the leaf
// bites read in game as blur and as coins with notches, and both are gone.
// Everything sways and flickers with the game's wind.
//
// Nothing reaches past the cells searched. A gap's centre sits within 0.3
// cell of its cell's middle; the 5x5 search finds anything reaching under 2.2
// cells, and a gap reaches at most 0.3 + 0.546 cell / VV_TERRAIN_FLECK_MIN_STRETCH
// plus its sway. Specks reach under 1.2 cells and use the 3x3
// centre of the same search.
vec3 vvTerrainFleckField(vec2 src, vec2 az, float stretch, float density, float broken, float shimmer)
{
    vec2 base = floor(src / VV_TERRAIN_FLECK_CELL);
    // Where the crown is thin, gaps are many and large and run together into
    // pools; where it is closed, only specks. "Thin" is mostly game data:
    // fewer leaf blocks in the column the sun ray crosses, and vanilla's own
    // shadow already broken here (the ring count).
    float noise = vvTerrainFleckNoise(src, VV_TERRAIN_FLECK_CLUSTER_CELL, VV_TERRAIN_FLECK_CLUSTER_PERIOD, vec2(17.0, 5.0));
    float thin = clamp(0.35 * noise + 0.45 * (1.0 - clamp(density, 0.0, 1.0)) + 0.25 * clamp(broken, 0.0, 1.0), 0.0, 1.0);
    // Pools come in groups: a cluster mask a few cells wide gathers the gaps
    // under thin patches of crown and leaves wide plain shade between them,
    // instead of an even scatter of coins (v5, seen in game).
    float dataThin = clamp(0.6 * (1.0 - clamp(density, 0.0, 1.0)) + 0.4 * clamp(broken, 0.0, 1.0), 0.0, 1.0);
    float cluster = smoothstep(0.32, 0.6, noise);
    float open = (VV_TERRAIN_FLECK_SCATTER + cluster) * (0.55 + 0.6 * dataThin);
    stretch = max(stretch, VV_TERRAIN_FLECK_MIN_STRETCH);
    float turn = 6.28318530718 * vv_sceneBreeze;
    float swayAmount = 0.02 + 0.04 * shimmer;

    float field = 0.0;
    float speck = 0.0;
    for (int dy = -2; dy <= 2; dy++)
    for (int dx = -2; dx <= 2; dx++)
    {
        vec2 cell = base + vec2(float(dx), float(dy));
        vec2 key = mod(cell, VV_TERRAIN_FLECK_PERIOD);

        if (abs(dx) <= 1 && abs(dy) <= 1)
        {
            // A speck: a chink a few texels wide. One cell in two, under a
            // closed crown as much as an open one, at half brightness.
            vec4 k = vvTerrainCellRandom(key + vec2(3907.0, 1423.0));
            if (k.x < 0.5)
            {
                vec2 sway = vec2(sin(turn * 5.0 + k.y * 6.28318530718), cos(turn * 3.0 + k.z * 6.28318530718)) * swayAmount;
                vec2 d = vvTerrainFleckOffset(src, (cell + vec2(0.2) + k.yz * 0.6 + sway) * VV_TERRAIN_FLECK_CELL, az, stretch);
                float rs = VV_TERRAIN_FLECK_CELL * mix(0.035, 0.07, k.w);
                speck = max(speck, 0.5 * step(length(d), rs));
            }
        }

        vec4 r = vvTerrainCellRandom(key + vec2(911.0, 3301.0));
        // Faded in and out across the threshold, so a gap opens as the
        // crown thins instead of popping on.
        float present = smoothstep(r.x - 0.06, r.x + 0.06, open);
        if (present <= 0.0) continue;
        vec4 s = vvTerrainCellRandom(key + vec2(5113.0, 77.0));

        vec2 sway = vec2(sin(turn * 3.0 + 6.28318530718 * r.w), cos(turn * 2.0 + 6.28318530718 * r.y)) * swayAmount;
        vec2 centre = (cell + vec2(0.2) + r.yz * 0.6 + sway) * VV_TERRAIN_FLECK_CELL;

        // Mostly small, a few large; inside a cluster they grow until they
        // touch and merge into irregular patches.
        float size = s.x * s.x * s.x;
        float radius = VV_TERRAIN_FLECK_CELL * mix(0.08, 0.39, size) * mix(0.9, 1.4, cluster);
        // Flutter: in wind a gap opens and closes as the leaves around it
        // move. Two fixed rhythms, mixed by the wind's strength.
        float flutter = mix(sin(turn * 5.0 + 6.28318530718 * s.y), sin(turn * 11.0 + 6.28318530718 * s.z), shimmer);
        float weight = present * mix(0.8, 1.0, size) * (1.0 - 0.3 * shimmer * (0.5 + 0.5 * flutter));

        vec2 d = vvTerrainFleckOffset(src, centre, az, stretch);
        float x = length(d) / radius;
        float kernel = max(0.0, 1.0 - x * x);
        field += weight * kernel * kernel;
    }

    // A ragged outline, not a circle: the threshold wanders with a lobe
    // noise a quarter-block across and a per-texel jitter, so the edge is a
    // stepped, bitten line. One hard level - no half-bright rim, which read
    // as blur on the ground (v5, seen in game).
    float lobe = vvTerrainFleckNoise(src, VV_TERRAIN_FLECK_LOBE_CELL, VV_TERRAIN_FLECK_LOBE_PERIOD, vec2(29.0, 11.0));
    float jitter = vvTerrainCellRandom(mod(floor(src * VV_TERRAIN_FLECK_TEXELS), VV_TERRAIN_FLECK_TEXEL_PERIOD) + vec2(613.0, 59.0)).x;
    float threshold = 0.5 + VV_TERRAIN_FLECK_RAGGED * (lobe - 0.5) + 0.5 * VV_TERRAIN_FLECK_RAGGED * (jitter - 0.5);
    float lit = step(threshold, field);
    float level = max(lit, speck);
    float halo = smoothstep(0.12, 0.5, field) * (1.0 - level);
    return vec3(level, halo, thin);
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
vec2 vvTerrainSunfleck(vec3 cameraRelativePos, vec3 faceNormal, vec2 overhead, float shadowed, float facing, float broken)
{
    if (vv_pbrSunflecks < 0.001 || overhead.x <= 0.0) return vec2(0.0);

    float flatDistance = max(abs(cameraRelativePos.x), abs(cameraRelativePos.z));
    float resolvable = clamp(1.0 - (flatDistance - VV_TERRAIN_FLECK_FADE_START) / VV_TERRAIN_FLECK_FADE_RANGE, 0.0, 1.0)
                     * (1.0 - smoothstep(VV_TERRAIN_FLECK_VIEW_LIMIT * 0.75, VV_TERRAIN_FLECK_VIEW_LIMIT, length(cameraRelativePos)));
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
    vec3 field = vvTerrainFleckField(src, az, l.y, overhead.x, broken, vvTerrainFleckShimmer());
    // A blade of grass catches a speck as full light, which is what makes
    // the lit part of a pool read as grass in the sun rather than a stain.
    float pattern = vvTerrainIsFlora() ? min(1.0, field.x * 2.0) : field.x;

    float gate = clamp(shadowed, 0.0, 1.0) * resolvable * smoothstep(0.0, 0.3, overhead.x)
               * clearSky * light * vv_pbrSunflecks * VV_TERRAIN_FLECK_GAIN;
    float fleck = clamp(pattern * clamp(facing, 0.0, 1.0) * gate, 0.0, 1.0);
    float bounce = clamp(field.y * VV_TERRAIN_FLECK_BOUNCE * gate, 0.0, VV_TERRAIN_FLECK_BOUNCE);
    return vec2(fleck, bounce);
}

// A sunlit patch's colour from the shaded colour lifted to vanilla's sunlit
// level: warmer than the shade and a touch richer, at the SAME luminance, so a
// fleck reads as sunlight on the surface rather than a pale stain, and still
// cannot pass vanilla's own sunlit brightness. `golden` 1 near the horizon
// shifts it toward the colour of an evening sun; by night the same patch is
// the moon's, cool instead of warm.
vec3 vvTerrainSunlitTint(vec3 c, float golden, float night)
{
    float y = dot(c, vec3(0.2126, 0.7152, 0.0722));
    vec3 hue = mix(mix(vec3(1.04, 1.0, 0.92), vec3(1.10, 0.98, 0.80), clamp(golden, 0.0, 1.0)),
                   vec3(0.88, 0.95, 1.12), clamp(night, 0.0, 1.0));
    vec3 warm = (vec3(y) + (c - vec3(y)) * VV_TERRAIN_FLECK_SATURATION) * hue;
    return max(warm * (y / max(dot(warm, vec3(0.2126, 0.7152, 0.0722)), 0.0001)), vec3(0.0));
}

// The factor that undoes vanilla's sun shadow at this visibility: its shadow
// takes shadowIntensity / 2 at full occlusion. Bounded, so a fleck can reach
// vanilla's sunlit level and never pass it.
float vvTerrainSunRelight(float sunVisibility)
{
    float vanilla = 1.0 - clamp(shadowIntensity, 0.0, 1.0) * 0.5 * (1.0 - clamp(sunVisibility, 0.0, 1.0));
    return 1.0 / max(vanilla, 0.5);
}

// Canopy shade and sunflecks applied to a sunlit term, the one entry point
// for opaque blocks, the forest floor and plants alike.
vec3 vvTerrainCanopyResolve(vec3 color, vec3 cameraRelativePos, vec3 faceNormal, float fade,
                            float sunVisibility, float cap)
{
    if (vv_pbrDapple < 0.001 && vv_pbrSunflecks < 0.001) return color;
    if (!vvTerrainIsCanopyReceiver()) return color;
    if (length(cameraRelativePos) > VV_TERRAIN_DAPPLE_FADE_START + VV_TERRAIN_DAPPLE_FADE_RANGE) return color;
    if (vvTerrainFleckLight() < 0.01) return color;

    if (sunVisibility < 0.0) sunVisibility = vvTerrainSunVisibility();
    float shadowed = 1.0 - clamp(sunVisibility, 0.0, 1.0);
    if (shadowed < 0.01) return color;

    vec2 overhead = vvTerrainCanopyOverhead(cameraRelativePos);
    // Measured once: the dapple's shade and the flecks' placement both read it.
    float evidence = vvTerrainCanopyEvidence(sunVisibility);
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
    vec2 fleck = vvTerrainSunfleck(cameraRelativePos, faceNormal, overhead, shadowed, facing, evidence);
    if (fleck.x <= 0.0 && fleck.y <= 0.0) return shaded;

    float night = 1.0 - vvTerrainSunPresence();
    float golden = 1.0 - smoothstep(0.08, 0.45, l.y);
    vec3 lit = vvTerrainSunlitTint(color * vvTerrainSunRelight(sunVisibility), golden, night);
    // Bounce: a little of a pool's light lands on the shade around it. Warm
    // because the pool is, and well under the cap - it fills the edge of a
    // pool, it does not light the floor.
    float local = 1.0 - vvTerrainLocalLightShare();
    return mix(shaded, lit, clamp(fleck.x + fleck.y, 0.0, 1.0) * local);
}

// How much this fragment marks vanilla's god-ray channel, 0 to
// VV_TERRAIN_SHAFT_MAX.
//
// Vanilla's god-ray pass blurs that channel radially from the sun's screen
// position, so a beam runs from its source AWAY from the sun on screen. The
// source therefore has to be where the light ENTERS the forest - a leaf
// lit from behind at a thin place in the crown - so the beam falls from the
// crown toward the floor. The pools on the ground would draw beams running
// the wrong way. Only leaves, only where the leaf map says the crown is thin
// (a real gap, not an invented one), only where the sun reaches the leaf,
// strongest looking toward the sun, stronger in humid air - after rain, at
// dawn and dusk, in fog - and gone under cloud. fogDensity is vanilla's
// fogDensityIn, passed in because only chunkopaque declares it.
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

    vec3 world = cameraRelativePos + vv_pbrOrigin;
    float crown = vvTerrainCanopyCell(world.xz).x;
    float gap = 1.0 - smoothstep(0.4, 1.0, crown);
    // The leaves seen when facing the sun are the crown's shaded side, so a
    // shadow-map test on the fragment is near zero exactly where a beam would
    // be seen (v5: view 66 showed no source at noon or at a low sun). A thin
    // leaf-map cell is the evidence that light passes; the leaf's own
    // visibility only raises it, never gates it.
    float reached = mix(0.6, 1.0, vvTerrainSunVisibility());

    // Humid air carries a beam: wet ground after rain, a low sun at either
    // end of the day, and vanilla's own fog density (0.00125 by default,
    // ten times that in fog).
    float lowSun = 1.0 - smoothstep(0.15, 0.55, l.y);
    float fog = clamp((fogDensity - 0.00125) / 0.01, 0.0, 1.0);
    float humid = 0.35 + 0.65 * clamp(max(max(clamp(vv_sceneWetness, 0.0, 1.0), lowSun), fog), 0.0, 1.0);

    float level = clamp(vv_pbrShafts, 0.0, 2.0) * 0.5 * toward * gap * reached * humid * clearSky * sun
                * vvSceneVisibilityDampen();
    return clamp(level, 0.0, VV_TERRAIN_SHAFT_MAX);
}

// ---------------------------------------------------------------------------
// Plant tissue, on the modular terrain path
//
// A port of the legacy patch's flora response, not a second model: the same
// taxonomy, tissue values, pigment and weather optics. Plant tissue never
// receives the opaque material's relief, cavity or atlas specular; it starts
// from vanilla's lit colour and takes only the terms below.
// ---------------------------------------------------------------------------

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
    // is a height up the plant.
    if (flora == VV_TFLORA_HERB || flora == VV_TFLORA_GRASS || flora == VV_TFLORA_CROP ||
        flora == VV_TFLORA_REED || flora == VV_TFLORA_STIFF || flora == VV_TFLORA_THIN)
    {
        float height = clamp(float((renderFlags >> WindDataPosition) & 0x7) / 7.0, 0.0, 1.0);
        base *= mix(0.55, 1.0, height);
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
float vvTerrainTranslucency(vec3 n, vec3 l, vec3 v)
{
    vec3 through = vvTerrainSafeNormalize(l + n * 0.35, l);
    return pow(max(0.0, dot(v, -through)), 3.0);
}

// Distance at which per-leaf detail stops being resolvable.
float vvTerrainFloraLod(vec3 cameraRelativePos)
{
    return clamp(1.0 - (length(cameraRelativePos) - 18.0) / 54.0, 0.0, 1.0);
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

    vec3 v = vvTerrainSafeNormalize(-cameraRelativePos, vec3(0.0, 1.0, 0.0));
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

vec4 vvTerrainFloraShade(vec4 litColor, vec3 baseColor, vec3 faceNormal, vec3 cameraRelativePos,
                         float fogAmount, float murkiness)
{
    vec3 v = vvTerrainSafeNormalize(-cameraRelativePos, vec3(0.0, 1.0, 0.0));
    vec3 n = vvTerrainSafeNormalize(faceNormal, vec3(0.0, 1.0, 0.0));
    if (dot(n, v) < 0.0) n = -n;
    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));

    float lod = vvTerrainFloraLod(cameraRelativePos);
    float sunVisibility = vvTerrainSunVisibility();
    float clearAir = clamp(1.0 - fogAmount - murkiness, 0.0, 1.0);
    float wet = vvTerrainWetness(n);
    float snowFrost = clamp(vvTerrainSnow(n) + vv_sceneFrost, 0.0, 1.0);

    // Vanilla's lit leaf is the base; a wet leaf is a darker leaf.
    vec3 result = litColor.rgb * mix(1.0, VV_LAYER_WET_LEAF_DARKEN, wet);
    result = vvTerrainCanopyResolve(result, cameraRelativePos, n, lod, sunVisibility, 0.55);

    vec3 through = vvTerrainFoliageTransmission(baseColor, faceNormal, cameraRelativePos, sunVisibility)
                 * mix(0.45, 1.0, lod);
    result += (vvTerrainFloraFrontScatter(baseColor, n, l, sunVisibility, lod) + through) * clearAir;
    result += vvTerrainCuticleSpecular(n, l, v, wet, snowFrost, sunVisibility, lod) * clearAir;
    return vec4(clamp(result, 0.0, 8.0), litColor.a);
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

vec4 vvTerrainMaterialPrimary(vec4 litColor, vec3 baseColor, vec3 faceNormal,
                              vec2 materialUv, vec3 cameraRelativePos,
                              float shadowBrightness, float fogAmount,
                              float murkiness, vec3 environmentColor,
                              vec3 blockLight, float glowLevel)
{
    if (vv_pbrEnabled < 0.5) return litColor;
    if (vvTerrainIsFlora())
        return vvTerrainFloraShade(litColor, baseColor, faceNormal, cameraRelativePos,
                                   fogAmount, murkiness);

    VvTerrainMaterial m = vvDecodeTerrainMaterial(litColor, baseColor, faceNormal,
                                                  materialUv, cameraRelativePos);
    vec3 viewDir = vvTerrainSafeNormalize(-cameraRelativePos, m.faceNormal);

    float directionalRelief = vvDirectionalShadeTerrain(m.normal) -
                              vvDirectionalShadeTerrain(m.faceNormal);
    float relief = 1.0 + directionalRelief * 0.32 * m.detailFade;
    float heightCavity = mix(0.92, 1.06, m.height);
    float indirectAo = mix(1.0, m.ao * heightCavity, clamp(vv_pbrCavity, 0.0, 1.0));

    vec3 dielectricF0 = vec3(VV_DIELECTRIC_F0) * mix(0.6, 1.8, m.specularFactor);
    vec3 f0 = mix(dielectricF0, m.baseColor, m.metalness);
    vec3 diffuseEnergy = (vec3(1.0) - f0 * 0.35) * (1.0 - m.metalness);

    vec3 diffuse = litColor.rgb * diffuseEnergy * relief;
    diffuse *= mix(1.0, max(indirectAo, 0.82), 0.35);
    diffuse = vvTerrainApplySurfaceLayers(m, diffuse);
    diffuse = vvTerrainCanopyResolve(diffuse, cameraRelativePos, m.faceNormal, m.detailFade, -1.0, 0.85);

    vec3 directSpec = vvTerrainDirectSpecular(m, viewDir, shadowBrightness, fogAmount, murkiness, materialUv);
    vec3 blockSpec = vvTerrainBlockSpecular(m, viewDir, blockLight);
    vec3 ambientSpec = vvTerrainAmbientSpecular(m, viewDir, environmentColor);

    float energyCap = mix(1.0, 1.18, clamp(vv_pbrEnergyCompensation, 0.0, 1.0));
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
        vec2 overhead = vvTerrainCanopyOverhead(cameraRelativePos);
        vec2 f = vvTerrainSunfleck(cameraRelativePos, faceNormal, overhead, 1.0 - vis, 1.0, vvTerrainCanopyEvidence(vis));
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
    if (mode == 67)
        return vec4(vvTerrainClearSky(), vvTerrainFleckShimmer(),
                    (1.0 - vvTerrainSunPresence()) * clamp(vv_sceneMoonLight, 0.0, 1.0), color.a);
    // 65: the leaf map. Green is leaf density along the sun ray, blue its
    // height (white at 16 blocks); grey means the map is not uploaded.
    if (mode == 65)
    {
        if (vv_canopyMapValid < 0.5) return vec4(vec3(0.5), color.a);
        vec2 overhead = vvTerrainCanopyOverhead(cameraRelativePos);
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
    vec3 viewDir = vvTerrainSafeNormalize(-cameraRelativePos, m.faceNormal);
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
