// Vintage Visuals - pseudo-PBR material response
//
// Injected into chunkopaque.fsh by shaderpatches/pseudopbr.yaml, anchored on
// vanilla's `uniform sampler2D liquidDepth`  -  the LAST sampler vanilla
// declares. That position is the single most load-bearing decision in this
// file, and it was learned the hard way.
//
// WHY IT MUST GO AFTER EVERY VANILLA SAMPLER
//
// Declaring `vv_materialTex` earlier in the file shifted vanilla's remaining
// samplers by one when the program linked: glow took sky's unit, sky took
// liquidDepth's, and liquidDepth took a unit nothing was ever bound to. A
// liquidDepth that reads nothing makes getUnderwaterMurkiness() saturate to 1,
// and applyUnderwaterEffects then mixes EVERYTHING this shader draws to
// waterMurkColor. In game that looked like the world had gone transparent  - 
// only grass tops survived, because they come from chunktopsoil.fsh, which
// this mod does not patch. Same cause both times it broke; only the water
// colour differed.
//
// So: declared last, after terrainTex, terrainTexLinear, shadowMapFar,
// shadowMapNear, glow, sky and liquidDepth. Vanilla keeps units 0..6 and this
// takes what is left. Anchoring on the last of them is what enforces it  -  if a
// game update adds an eighth sampler below liquidDepth, the anchor still puts
// us above it, so re-check this when the sampler list changes.
//
// The atlas matches the block texture atlas's layout exactly, so it samples
// with the very same `uv` the diffuse uses. Channels:
//   R,G = tangent-space normal X,Y   B = roughness   A = specular

// The anchor line, pasted back. The patch REPLACES it with this whole file, so
// dropping this would delete the sampler the underwater code depends on  -  the
// exact failure this layout exists to prevent.
uniform sampler2D liquidDepth;

uniform sampler2D vv_materialTex;

// Master switch AND the "did the CPU side actually bind anything" flag. An
// unset GLSL uniform reads as exactly 0, so a failure to bind lands on the
// same branch as a deliberate disable: vanilla shading, vanilla output.
uniform float vv_pbrEnabled;

// Global multipliers on top of the per-material values baked into the atlas.
uniform float vv_pbrNormalStrength;
uniform float vv_pbrSpecularStrength;

// 0 renders normally. 1..6 replace the output with one layer of the material
// system so it can be inspected on its own  -  see vvDebugView.
uniform float vv_pbrDebugView;

// Daylight strength, uploaded by this mod rather than read from vanilla's
// `dayLight`. chunkopaque.fsh declares that uniform and chunktopsoil.fsh does
// not, and this snippet is injected into both - depending on a uniform that
// exists in one shader and not the other would make the shared code compile in
// one place and fail in the other. Ours exists wherever we put it.
uniform float vv_pbrDayLight;

// Look controls. Each is a uniform rather than a rebuild because the whole
// point is that taste is argued with at runtime, not compiled in.
uniform float vv_pbrRoughnessBias;   // matte <-> gloss, applied to every material
uniform float vv_pbrMetalResponse;   // how metallic the reflective materials read
uniform float vv_pbrAmbient;         // sky/environment reflection strength
uniform float vv_pbrSpecularAA;      // geometric specular antialiasing strength
uniform float vv_pbrDetailDistance;  // blocks at which relief has faded to nothing
uniform float vv_pbrBlockLight;      // strength of highlights from torches, lava and glowing blocks
uniform float vv_pbrBlockLightDir;   // 0 treats block light as ambient, 1 fully trusts the gradient
uniform float vv_weatherWetness;     // 0 dry, 1 as wet as rain makes it

// Sky exposure, added to the vertex shader by the weather patch: vanilla's own
// per-vertex sun light level, which is 0 under a roof and 1 in the open. Rain
// cannot reach a surface the sky cannot see, and this is the only signal in
// either chunk shader that knows the difference.
in float vv_sunExposure;

// Builds a tangent frame for an axis-aligned block face.
//
// A proper renderer would take tangents from the mesh. Chunk geometry carries
// none, and does not need to: every face is axis-aligned, so one consistent
// frame per axis is exact rather than approximate. The branch picks a reference
// axis never parallel to the normal, which is the only way this degenerates.
mat3 vvTangentFrame(vec3 n)
{
    vec3 reference = abs(n.y) > 0.99 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
    vec3 tangent = normalize(cross(reference, n));
    vec3 bitangent = cross(n, tangent);
    return mat3(tangent, bitangent, n);
}

// Snaps a coordinate to the centre of its texel.
//
// The atlas is uploaded with nearest filtering, so in principle this is
// redundant. In practice the filter mode is set through the game's texture API
// on a texture this mod does not fully own, and one round of "still looks
// muddy" was spent unable to tell from a screenshot whether the setting had
// taken. Snapping in the shader makes the sampling nearest by construction, at
// the cost of one textureSize and a floor, and removes the question.
//
// It matters because Vintage Story is pixel art: the diffuse steps sharply
// from texel to texel, and a normal that interpolates smoothly across the same
// span makes light roll over a surface whose colour does not, which reads as
// wet clay rather than as stone.
vec2 vvSnapToTexel(vec2 materialUv)
{
    vec2 size = vec2(textureSize(vv_materialTex, 0));
    return (floor(materialUv * size) + 0.5) / size;
}

vec4 vvSampleMaterial(vec2 materialUv)
{
    return texture(vv_materialTex, vvSnapToTexel(materialUv));
}

// Pure: no enable check. With vv_pbrNormalStrength unset (0) this returns the
// face normal unchanged, so it degrades to vanilla on its own.
vec3 vvPerturbNormal(vec3 faceNormal, vec2 materialUv)
{
    vec2 xy = (vvSampleMaterial(materialUv).rg * 2.0 - 1.0) * vv_pbrNormalStrength;

    // Z is reconstructed rather than stored, which is what buys the atlas its
    // fourth channel. Scaling xy first and solving for z afterwards keeps the
    // result unit length at any strength, so turning the effect up tilts the
    // normal further instead of denormalising it.
    float z = sqrt(max(1e-4, 1.0 - dot(xy, xy)));

    return normalize(vvTangentFrame(faceNormal) * vec3(xy, z));
}

// How much of the material response survives at this distance.
//
// The atlas is sampled with nearest filtering and carries no mipmaps, so at
// range one screen pixel covers many texels and picks one of them essentially
// at random - which crawls and shimmers as the camera moves. Surface relief is
// a close-up detail anyway: past a few blocks the eye reads silhouette and
// colour, not grain. Fading it out costs one length() and removes the whole
// aliasing problem instead of managing it.
//
// Distances are in blocks, measured from the camera. Full detail is held to a
// third of the fade distance, so one slider moves both ends together and the
// ramp keeps its shape.
float vvDetailFade(vec3 cameraRelativePos)
{
    float far = max(4.0, vv_pbrDetailDistance);
    float near = far * 0.33;

    return clamp((far - length(cameraRelativePos)) / max(1.0, far - near), 0.0, 1.0);
}

// Vanilla's directional shading term, lifted from getBrightnessFromNormal so
// the two agree on what "lit" means.
//
// Deliberately WITHOUT that function's second line, `nb = max(nb, normal.y *
// 0.95)`. That term is a sky-bounce fudge stopping block tops being darker than
// their sides, and it saturates every upward-facing surface at 0.95  -  included
// here, floors and ground would be the one place relief could never show, which
// is most of what a player looks at.
float vvDirectionalShade(vec3 n)
{
    return max(0.0, 0.5 + 0.5 * dot(n, lightPosition));
}

// How much the perturbed normal changes vanilla's directional shading.
float vvReliefDelta(vec3 faceNormal, vec2 materialUv)
{
    vec3 n = normalize(faceNormal);
    return vvDirectionalShade(vvPerturbNormal(n, materialUv)) - vvDirectionalShade(n);
}

// The perturbed normal, gated and distance-faded, for shaders that hand their
// normal straight to the lighting function instead of a precomputed brightness.
// chunktopsoil.fsh is one: it still calls applyFogAndShadowWithNormal, so the
// relief can go in through the normal itself rather than through a delta.
vec3 vvSurfaceNormal(vec3 faceNormal, vec2 materialUv, vec3 cameraRelativePos)
{
    if (vv_pbrEnabled < 0.5) return faceNormal;

    vec3 n = normalize(faceNormal);
    return normalize(mix(n, vvPerturbNormal(n, materialUv), vvDetailFade(cameraRelativePos)));
}

// ---------------------------------------------------------------------------
// Rain
//
// A wet surface is not the dry one with water drawn on top. Three things
// change, and all three are already inputs to the microfacet model:
//
//   roughness  collapses  - water fills the microscopic pits that scatter light
//   specular   rises      - a smooth water film is far more reflective than
//                           stone, which is what makes wet ground glare
//   albedo     darkens    - light entering the film scatters inside it and less
//                           of it comes back out
//
// Miss the darkening and wet stone reads as polished stone. It is the least
// obvious of the three and the one that sells it.
// ---------------------------------------------------------------------------

const float VV_WET_ROUGHNESS = 0.08;
const float VV_WET_SPECULAR = 0.60;
const float VV_WET_DARKEN = 0.72;

// How wet this fragment is.
//
// Two gates, both physical. Rain falls downward, so it pools on up-facing
// surfaces, runs off vertical ones and never touches undersides - squared so
// the falloff is steep rather than linear. And it cannot reach anything the sky
// cannot see, which is what sun exposure measures.
float vvWetness(vec3 faceNormal)
{
    if (vv_weatherWetness < 0.001) return 0.0;

    float facing = clamp(faceNormal.y * 0.5 + 0.5, 0.0, 1.0);

    return clamp(vv_weatherWetness * facing * facing * clamp(vv_sunExposure, 0.0, 1.0), 0.0, 1.0);
}

// Adjusts vanilla's per-vertex brightness by that difference.
//
// A difference rather than a replacement, for two reasons. Vanilla computes nb
// per VERTEX and hands it over as a varying, so its absolute value already
// carries whatever normalShadeIntensity and minNormalShade the vertex shader
// chose  -  values this shader cannot see and should not guess. And a difference
// is exactly zero where the atlas is flat, so every texture this mod failed to
// process, and every gap in the atlas, renders precisely as vanilla.
float vvSurfaceBrightness(float vanillaBrightness, vec3 faceNormal, vec2 materialUv,
                          vec3 cameraRelativePos)
{
    if (vv_pbrEnabled < 0.5) return vanillaBrightness;

    float delta = vvReliefDelta(faceNormal, materialUv) * vvDetailFade(cameraRelativePos);
    return clamp(vanillaBrightness + delta, 0.0, 1.0);
}

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

vec4 vvApplyPbr(vec4 litColor, vec3 albedo, vec3 faceNormal, vec2 materialUv,
                vec3 cameraRelativePos, float shadowBrightness, float fog, float murkiness,
                vec3 environment, vec3 blockLightColor)
{
    if (vv_pbrEnabled < 0.5) return litColor;

    vec4 material = vvSampleMaterial(materialUv);

    // Floored rather than clamped to 0: a perfectly smooth surface makes the
    // GGX denominator collapse to a single pixel-wide highlight that aliases
    // into sparkle as the camera moves.
    float roughness = clamp(material.b + vv_pbrRoughnessBias, 0.04, 1.0);
    float specularMask = clamp(material.a, 0.0, 1.0);

    // Rain, before anything else reads these. Wetness is a property of the
    // surface, not a layer over the finished shading, so it belongs here where
    // roughness and reflectance are decided rather than at the end where it
    // would be a tint.
    float wetness = vvWetness(faceNormal);
    roughness = mix(roughness, VV_WET_ROUGHNESS, wetness);
    specularMask = max(specularMask, mix(specularMask, VV_WET_SPECULAR, wetness));
    albedo *= mix(1.0, VV_WET_DARKEN, wetness);

    // The same normal the relief uses, so the highlight sits on the surface the
    // player can see rather than on one the shading invented.
    vec3 n = vvSurfaceNormal(faceNormal, materialUv, cameraRelativePos);
    vec3 l = normalize(lightPosition);

    // worldPos is camera-relative here - vanilla's own applyReflectiveEffect
    // treats it that way - so the view vector points back from the fragment.
    vec3 v = normalize(-cameraRelativePos);
    vec3 h = normalize(l + v);

    float ndotl = max(dot(n, l), 0.0);
    float ndotv = max(dot(n, v), 1e-4);
    float ndoth = max(dot(n, h), 0.0);
    float vdoth = max(dot(v, h), 0.0);

    // Widened by however much the normal varies inside this pixel. Everything
    // downstream uses the filtered value, so the lobe, the geometry term and
    // the ambient ceiling all agree on how rough the surface is at this scale.
    roughness = vvFilteredRoughness(roughness, n);

    vec3 f0 = vvReflectanceF0(albedo, specularMask);
    vec3 fresnel = vvFresnelSchlick(vdoth, f0);

    float distribution = vvDistributionGGX(ndoth, roughness);
    float geometry = vvGeometrySmith(ndotv, ndotl, roughness);

    // The standard Cook-Torrance denominator. One factor of NdotL cancels
    // against the NdotL in the reflectance equation, which is why the term
    // below is multiplied by ndotl exactly once.
    vec3 specular = (distribution * geometry * fresnel) / max(1e-4, 4.0 * ndotv * ndotl);

    // Everything that should suppress a highlight: shadow, night, fog, water.
    float visibility = clamp(shadowBrightness, 0.0, 1.0)
                     * clamp(vv_pbrDayLight, 0.0, 1.0)
                     * clamp(1.0 - fog - murkiness, 0.0, 1.0)
                     * vvDetailFade(cameraRelativePos);

    vec3 result = litColor.rgb * mix(1.0, VV_WET_DARKEN, wetness);

    // Energy conservation. Light reflected specularly is light that did not
    // scatter diffusely, so the diffuse has to give it up - this is what makes
    // metal read as metal rather than as bright plastic, because a metal's
    // diffuse drops to almost nothing and only the highlight remains.
    //
    // Scaled by the strength slider so that 0 is exactly vanilla: a player who
    // turns the effect off must get their old image back, not a darker one.
    result *= mix(vec3(1.0), 1.0 - fresnel, vv_pbrSpecularStrength * specularMask);

    result += specular * ndotl * visibility * vv_pbrSpecularStrength;

    // Ambient is deliberately NOT multiplied by the shadow map: sky light
    // reaches surfaces the sun does not, and killing it in shadow is what makes
    // metal in a doorway look like painted wood. Daylight and fog still apply.
    // Torches, lava and glowing blocks. Fogged like everything else, but not
    // shadowed and not scaled by daylight - see vvBlockLightSpecular.
    result += vvBlockLightSpecular(f0, roughness, n, v, blockLightColor, cameraRelativePos)
            * clamp(1.0 - fog - murkiness, 0.0, 1.0)
            * vv_pbrSpecularStrength;

    result += vvAmbientSpecular(f0, roughness, ndotv, environment)
            * clamp(vv_pbrDayLight, 0.0, 1.0)
            * clamp(1.0 - fog - murkiness, 0.0, 1.0)
            * vv_pbrSpecularStrength;

    return vec4(result, litColor.a);
}

// Replaces the output with one layer of the material system.
//
// This exists because the material system is four derived quantities stacked on
// top of each other, and when the composite looks wrong there is otherwise no
// way to tell which layer is at fault. It is also the instrument that finds
// sampler problems: if view 1 shows block textures instead of flat lavender,
// vv_materialTex is not reading the atlas at all.
vec4 vvDebugView(vec4 color, vec3 faceNormal, vec2 materialUv, vec3 cameraRelativePos,
                 float shadowBrightness, float fog, float murkiness, vec3 environment,
                 vec3 blockLightColor)
{
    int mode = int(vv_pbrDebugView + 0.5);
    if (mode <= 0) return color;

    vec4 material = vvSampleMaterial(materialUv);

    // 1: the tangent-space normal as stored, blue forced flat  -  the same view
    // the offline preview PNG writes, so the two can be compared directly.
    if (mode == 1) return vec4(material.r, material.g, 1.0, color.a);

    if (mode == 2) return vec4(vec3(material.b), color.a);
    if (mode == 3) return vec4(vec3(material.a), color.a);

    // 4: the relief contribution alone, biased so no change reads as mid grey.
    if (mode == 4) return vec4(vec3(0.5 + vvReliefDelta(faceNormal, materialUv)), color.a);

    // 5: the specular contribution alone, on black.
    if (mode == 5)
    {
        vec4 lobe = vvApplyPbr(vec4(0.0, 0.0, 0.0, color.a), vec3(1.0), faceNormal, materialUv,
                               cameraRelativePos, shadowBrightness, fog, murkiness, environment,
                               blockLightColor);
        return vec4(lobe.rgb, color.a);
    }

    // 6: the perturbed normal in WORLD space. Unlike view 1 this shows the
    // tangent frame doing its job  -  the six block faces should come out as six
    // flat colours, with the relief visible as variation within each.
    if (mode == 6) return vec4(vvPerturbNormal(normalize(faceNormal), materialUv) * 0.5 + 0.5, color.a);

    // 7: reflectance at normal incidence. Grey means dielectric, a coloured
    // surface means the shader is treating it as metal - the fastest way to see
    // whether the specular-mask stand-in for metalness is behaving.
    if (mode == 7) return vec4(vvReflectanceF0(color.rgb, material.a), color.a);

    // 8: the roughness the shading model actually uses - the stored value plus
    // the bias slider plus whatever specular antialiasing widened it by. Where
    // this is much brighter than view 2, the surface was sparkling and is now
    // being held down.
    if (mode == 8)
    {
        float shaded = vvFilteredRoughness(clamp(material.b + vv_pbrRoughnessBias, 0.04, 1.0),
                                           vvSurfaceNormal(faceNormal, materialUv, cameraRelativePos));
        return vec4(vec3(shaded), color.a);
    }

    // 10: wetness. White is soaked, black is dry - shows both gates at once,
    // so an overhang should read black while the ground beside it is white.
    if (mode == 10) return vec4(vec3(vvWetness(faceNormal)), color.a);

    // 9: the estimated block-light direction, as a colour. Flat means the
    // gradient found nothing and the light is being treated as ambient;
    // variation across a wall means a torch is being located.
    if (mode == 9)
    {
        vec3 n = vvSurfaceNormal(faceNormal, materialUv, cameraRelativePos);
        vec3 l = vvBlockLightDirection(n, cameraRelativePos, dot(blockLightColor, VV_PBR_LUMA));
        return vec4(l * 0.5 + 0.5, color.a);
    }

    return color;
}
