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

float vvTerrainWetness(vec3 faceNormal)
{
    if (vv_sceneWetness < 0.001) return 0.0;
    float facing = clamp(faceNormal.y * 0.5 + 0.5, 0.0, 1.0);
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
// Light through leaves, on the modular terrain path
//
// A port of the legacy patch's foliage transmission, not a second model: the
// same taxonomy, tissue values, pigment and weather optics. Plant tissue gets
// ONLY this additive term on top of vanilla's lit colour; it never receives
// the opaque material's relief or cavity, which is why flora was left alone
// until now. Canopy dapple is a separate feature and is not part of this.
//
// Sun visibility comes from vanilla's own shadow-map brightness `b`, rescaled
// by shadowIntensity so fully shadowed reads 0. It is sun-only, so a torch
// under a tree cannot make a leaf glow toward the sun.
// ---------------------------------------------------------------------------

const float VV_TERRAIN_SUN_PRESENCE_DAWN = 0.18;

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

float vvTerrainFloraSunVisibility(float shadowBrightness)
{
    return clamp(1.0 - (1.0 - clamp(shadowBrightness, 0.0, 1.0)) / max(shadowIntensity, 0.05), 0.0, 1.0);
}

vec3 vvTerrainFoliageTransmission(vec3 albedo, vec3 faceNormal, vec3 cameraRelativePos,
                                  float shadowBrightness)
{
    if (vv_pbrFoliage < 0.001) return vec3(0.0);

    float tissue = vvTerrainFloraTissue();
    if (tissue < 0.001) return vec3(0.0);

    vec3 v = vvTerrainSafeNormalize(-cameraRelativePos, vec3(0.0, 1.0, 0.0));
    vec3 n = vvTerrainSafeNormalize(faceNormal, vec3(0.0, 1.0, 0.0));
    // Cross quads are seen from both sides; shade the side facing the camera.
    if (dot(n, v) < 0.0) n = -n;
    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));

    // Snow and frost are a surface over the tissue and stop the beam; water
    // barely does. Same weights as the legacy patch.
    float wet = vvTerrainWetness(n);
    float snow = vvTerrainSnow(n);
    float frost = clamp(vv_sceneFrost, 0.0, 1.0);
    float weather = mix(1.0, 0.92, wet) * mix(1.0, 0.10, snow) * mix(1.0, 0.45, frost);

    // A gate, not a dimmer: transmission peaks when the sun is low and behind
    // the leaves, so it must not be scaled by "how high is the sun".
    float sunPresence = smoothstep(0.0, VV_TERRAIN_SUN_PRESENCE_DAWN, clamp(vv_sceneDayLight, 0.0, 1.0));
    // Transmission needs a beam; an overcast sky has none.
    float overcast = clamp(vv_sceneOvercast, 0.0, 1.0);

    return vvTerrainFloraPigment(albedo)
         * vvTerrainTranslucency(n, l, v)
         * tissue
         * vvTerrainFloraSunVisibility(shadowBrightness)
         * weather
         * vv_pbrFoliage
         * sunPresence
         * mix(1.0, VV_OVERCAST_DIRECT, overcast);
}

vec4 vvTerrainFloraShade(vec4 litColor, vec3 baseColor, vec3 faceNormal, vec3 cameraRelativePos,
                         float shadowBrightness, float fogAmount, float murkiness)
{
    vec3 through = vvTerrainFoliageTransmission(baseColor, faceNormal, cameraRelativePos, shadowBrightness)
                 * clamp(1.0 - fogAmount - murkiness, 0.0, 1.0);
    return vec4(clamp(litColor.rgb + through, 0.0, 8.0), litColor.a);
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
                                   shadowBrightness, fogAmount, murkiness);

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
        return vec4(vvTerrainFoliageTransmission(vec3(1.0), faceNormal, cameraRelativePos, shadowBrightness), color.a);
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
