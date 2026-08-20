// Vintage Visuals - the microfacet lighting core
//
// The one evaluation of Cook-Torrance in this mod, injected into every program
// that shades a surface: both chunk shaders and the entity shader. It used to
// live inside pseudopbr.glsl, which was fine while terrain was the only thing
// lit by it and became three copies to keep in step the moment it was not.
//
// Deliberately knows nothing about WHERE its inputs come from. Terrain reads
// roughness and a specular mask out of a derived atlas; entities have no such
// atlas and use a default material; a future water surface will supply its own.
// All three want the same lobe, and the difference between them is which
// material reaches it - not how light behaves once it does.
//
// Injected by each group separately rather than shared at runtime: a group must
// be able to roll back to vanilla without taking another group's declarations
// with it, and two groups sharing one injection cannot do that.
//
// The anchor is `uniform vec3 lightPosition;`, which all three target shaders
// declare and this file needs. Replacement content is literal, never a regex
// template, so the anchor is pasted back below or the declaration would be
// deleted along with the match.

uniform vec3 lightPosition; // vintagevisuals: anchor, asserted and pasted back

// The look controls this core reads. Declared here rather than left to the
// file below because GLSL needs a declaration before its use and this is
// injected first - and because these belong to the lobe, not to whichever
// material system happens to be feeding it.
uniform float vv_pbrMetalResponse;   // how metallic the reflective materials read
uniform float vv_pbrAmbient;         // sky/environment reflection strength
uniform float vv_pbrSpecularAA;      // geometric specular antialiasing strength
uniform float vv_pbrBlockLight;      // strength of highlights from torches, lava and glowing blocks
uniform float vv_pbrBlockLightDir;   // 0 treats block light as ambient, 1 fully trusts the gradient

// What cloud cover does to the two halves of the specular response. The direct
// lobe keeps about a third of its strength under full overcast and the sky term
// gains half again, so the light is redistributed rather than removed.
const float VV_OVERCAST_DIRECT = 0.35;
const float VV_OVERCAST_AMBIENT = 1.5;

// ---------------------------------------------------------------------------
// Cook-Torrance microfacet shading
//
// Applied on top of vanilla's lit colour rather than replacing it. Vanilla
// already computes a diffuse term from block light, sun light and the shadow
// map, and it is the term the whole game is balanced around; throwing it away
// for a from-scratch lighting model would mean re-deriving light colour,
// ambient and time of day from uniforms this shader only partly has. So the
// diffuse stays vanilla's, and this adds what vanilla has no notion of: a
// microfacet specular lobe whose shape comes from the derived roughness, and
// the energy that lobe takes back out of the diffuse.
// ---------------------------------------------------------------------------

const float VV_PI = 3.14159265359;

// GGX / Trowbridge-Reitz normal distribution. This is the term that makes
// roughness read as material: it concentrates the highlight into a tight core
// with a wide tail, which is what separates polished metal from worn stone far
// more convincingly than a Blinn-Phong exponent does.
float vvDistributionGGX(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / max(1e-5, VV_PI * d * d);
}

// Smith geometry term with the Schlick-GGX approximation, using the k for
// direct lighting rather than IBL. Accounts for microfacets shadowing each
// other, which is what stops rough surfaces blowing out at grazing angles.
float vvGeometrySmith(float NdotV, float NdotL, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    float gv = NdotV / (NdotV * (1.0 - k) + k);
    float gl = NdotL / (NdotL * (1.0 - k) + k);
    return gv * gl;
}

vec3 vvFresnelSchlick(float VdotH, vec3 f0)
{
    return f0 + (1.0 - f0) * pow(clamp(1.0 - VdotH, 0.0, 1.0), 5.0);
}

// Reflectance at normal incidence.
//
// Dielectrics reflect about 4% of light, white; metals reflect their own
// albedo, which is why copper has an orange highlight and steel a white one.
// That distinction needs metalness, and metalness is not in the atlas - there
// are five values worth storing and four channels.
//
// The specular mask stands in for it, squared so the interpolation stays near
// the dielectric end until a surface is genuinely reflective. That works
// because of how the material table happens to be shaped: among the materials
// chunkopaque actually draws, SpecularScale tracks metalness closely (Metal
// 1.00/1.00, Ore 0.85/0.35, Stone 0.25/0, Soil 0.05/0). The materials where
// the two diverge - water, glass - are drawn by chunkliquid and
// chunktransparent, not by this shader; ice is the one exception here, and its
// albedo is near-white, so tinting by it is very close to not tinting at all.
//
// If this ever reads wrong, the fix is a second atlas carrying real metalness,
// not a cleverer curve.
vec3 vvReflectanceF0(vec3 albedo, float specularMask)
{
    return mix(vec3(0.04), albedo, specularMask * specularMask * clamp(vv_pbrMetalResponse, 0.0, 1.0));
}

// Geometric specular antialiasing, after Kaplanyan et al. 2016 and the
// simplified kernel of Tokuyoshi and Kaplanyan 2019.
//
// This is the fix for sparkle, and it is aimed squarely at what this mod does
// to a surface. The normals here are DERIVED from texture detail, so they are
// far higher frequency than a hand-authored map - and a normal that changes
// faster than one screen pixel makes the specular lobe flicker as the camera
// moves, because each pixel samples a different microfacet orientation every
// frame. Rough stone with a tight highlight is exactly the worst case.
//
// The standard answer is not to smooth the normal - that loses the detail the
// mod exists to add - but to widen the lobe by however much the normal varies
// inside the pixel, measured from its screen-space derivative. A surface whose
// normals scatter within one pixel IS rougher at that scale; this makes the
// shading model agree with that.
//
// Kernel clamped because the derivative estimate degrades badly at grazing
// angles and silhouettes, which is the known weakness of the screen-space form.
const float VV_SPECULAR_AA_VARIANCE = 0.25;
const float VV_SPECULAR_AA_CLAMP = 0.18;

float vvFilteredRoughness(float roughness, vec3 n)
{
    if (vv_pbrSpecularAA < 0.001) return roughness;

    vec3 dndx = dFdx(n);
    vec3 dndy = dFdy(n);

    float variance = VV_SPECULAR_AA_VARIANCE * (dot(dndx, dndx) + dot(dndy, dndy));
    float kernel = min(2.0 * variance * vv_pbrSpecularAA, VV_SPECULAR_AA_CLAMP);

    // Widening happens in alpha (roughness squared), which is the space the
    // NDF actually integrates over - adding to roughness directly would widen
    // smooth surfaces far more than rough ones.
    return clamp(sqrt(roughness * roughness + kernel), 0.0, 1.0);
}

// Ambient specular from the sky.
//
// Without this, a metal block in shade or indoors has no highlight at all and
// reads as dark plastic, because the only light this shader knows about is the
// sun. There is no reflection probe to sample, so vanilla's own fog colour
// stands in for the sky - it is already the colour the horizon is being blended
// toward, which makes it a better environment estimate than any constant.
//
// Schlick with a roughness-aware ceiling (Karis): a rough surface must not get
// a mirror-bright rim, which is what plain Fresnel would give it.
vec3 vvAmbientSpecular(vec3 f0, float roughness, float ndotv, vec3 environment)
{
    vec3 fresnel = f0 + (max(vec3(1.0 - roughness), f0) - f0) * pow(clamp(1.0 - ndotv, 0.0, 1.0), 5.0);

    return environment * fresnel * max(0.0, vv_pbrAmbient);
}

// ---------------------------------------------------------------------------
// Block light: torches, lava, lanterns, glowing ore
//
// Vanilla bakes all of it into one per-vertex colour, `blockLight`. That gives
// this shader an intensity and a hue but no position, and a specular highlight
// needs a direction - which is why, until now, a metal wall beside a lit torch
// had no highlight at all while the same wall in sunlight did. Underground,
// where every light is a block light, the material system did nothing.
//
// The direction is recoverable without the CPU ever telling us where the lights
// are. Block light is a scalar field over the surface, and the gradient of that
// field points toward whatever is emitting it. Both pieces are already to hand:
// screen-space derivatives of the light and of the world position.
// ---------------------------------------------------------------------------

const vec3 VV_PBR_LUMA = vec3(0.2126, 0.7152, 0.0722);

// How far a unit of light gradient tilts the light direction off the normal.
// Tuned so a torch a couple of blocks away reads as coming from the side
// rather than from directly overhead.
const float VV_BLOCKLIGHT_TILT = 6.0;

/// Estimated direction toward the block light illuminating this fragment.
//
// Mikkelsen's surface-gradient construction: solve for the world-space gradient
// of the light field that is consistent with both screen derivatives, which
// lands it in the tangent plane by construction. Then tilt the normal toward
// it, by an amount that grows with the gradient's magnitude - a steep falloff
// means the source is close and therefore off to one side, a flat one means it
// is distant or ambient.
//
// Degenerate cases fall back to the normal, which is exactly "treat this light
// as ambient" and is the right answer when the field is uniform.
vec3 vvBlockLightDirection(vec3 n, vec3 cameraRelativePos, float intensity)
{
    vec3 dpdx = dFdx(cameraRelativePos);
    vec3 dpdy = dFdy(cameraRelativePos);

    vec3 r1 = cross(dpdy, n);
    vec3 r2 = cross(n, dpdx);

    float det = dot(dpdx, r1);
    if (abs(det) < 1e-8) return n;

    vec3 gradient = (r1 * dFdx(intensity) + r2 * dFdy(intensity)) / det;

    float magnitude = length(gradient);
    if (magnitude < 1e-4) return n;

    float tilt = clamp(magnitude * VV_BLOCKLIGHT_TILT, 0.0, 4.0) * clamp(vv_pbrBlockLightDir, 0.0, 1.0);

    return normalize(n + (gradient / magnitude) * tilt);
}

// A microfacet highlight from block light, in the light's own colour.
//
// Deliberately NOT scaled by the shadow map or by daylight. Block light is
// independent of both - a torch burns in a cave at midnight - and gating it on
// either would remove the highlight from precisely the places this exists to
// light.
vec3 vvBlockLightSpecular(vec3 f0, float roughness, vec3 n, vec3 v,
                          vec3 blockLightColor, vec3 cameraRelativePos)
{
    if (vv_pbrBlockLight < 0.001) return vec3(0.0);

    float intensity = dot(blockLightColor, VV_PBR_LUMA);
    if (intensity < 0.004) return vec3(0.0);

    vec3 l = vvBlockLightDirection(n, cameraRelativePos, intensity);
    vec3 h = normalize(l + v);

    float ndotl = max(dot(n, l), 0.0);
    float ndotv = max(dot(n, v), 1e-4);
    float ndoth = max(dot(n, h), 0.0);
    float vdoth = max(dot(v, h), 0.0);

    vec3 fresnel = vvFresnelSchlick(vdoth, f0);
    float distribution = vvDistributionGGX(ndoth, roughness);
    float geometry = vvGeometrySmith(ndotv, ndotl, roughness);

    vec3 specular = (distribution * geometry * fresnel) / max(1e-4, 4.0 * ndotv * ndotl);

    return blockLightColor * specular * ndotl * max(0.0, vv_pbrBlockLight);
}
