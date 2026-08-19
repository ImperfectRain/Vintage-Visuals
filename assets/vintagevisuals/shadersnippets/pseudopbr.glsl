// Vintage Visuals - pseudo-PBR material response
//
// Injected into chunkopaque.fsh by shaderpatches/pseudopbr.yaml, anchored on
// vanilla's `uniform sampler2D liquidDepth` — the LAST sampler vanilla
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
// waterMurkColor. In game that looked like the world had gone transparent —
// only grass tops survived, because they come from chunktopsoil.fsh, which
// this mod does not patch. Same cause both times it broke; only the water
// colour differed.
//
// So: declared last, after terrainTex, terrainTexLinear, shadowMapFar,
// shadowMapNear, glow, sky and liquidDepth. Vanilla keeps units 0..6 and this
// takes what is left. Anchoring on the last of them is what enforces it — if a
// game update adds an eighth sampler below liquidDepth, the anchor still puts
// us above it, so re-check this when the sampler list changes.
//
// The atlas matches the block texture atlas's layout exactly, so it samples
// with the very same `uv` the diffuse uses. Channels:
//   R,G = tangent-space normal X,Y   B = roughness   A = specular

// The anchor line, pasted back. The patch REPLACES it with this whole file, so
// dropping this would delete the sampler the underwater code depends on — the
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
// system so it can be inspected on its own — see vvDebugView.
uniform float vv_pbrDebugView;

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

// Pure: no enable check. With vv_pbrNormalStrength unset (0) this returns the
// face normal unchanged, so it degrades to vanilla on its own.
vec3 vvPerturbNormal(vec3 faceNormal, vec2 materialUv)
{
    vec2 xy = (texture(vv_materialTex, materialUv).rg * 2.0 - 1.0) * vv_pbrNormalStrength;

    // Z is reconstructed rather than stored, which is what buys the atlas its
    // fourth channel. Scaling xy first and solving for z afterwards keeps the
    // result unit length at any strength, so turning the effect up tilts the
    // normal further instead of denormalising it.
    float z = sqrt(max(1e-4, 1.0 - dot(xy, xy)));

    return normalize(vvTangentFrame(faceNormal) * vec3(xy, z));
}

// Vanilla's directional shading term, lifted from getBrightnessFromNormal so
// the two agree on what "lit" means.
//
// Deliberately WITHOUT that function's second line, `nb = max(nb, normal.y *
// 0.95)`. That term is a sky-bounce fudge stopping block tops being darker than
// their sides, and it saturates every upward-facing surface at 0.95 — included
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

// Adjusts vanilla's per-vertex brightness by that difference.
//
// A difference rather than a replacement, for two reasons. Vanilla computes nb
// per VERTEX and hands it over as a varying, so its absolute value already
// carries whatever normalShadeIntensity and minNormalShade the vertex shader
// chose — values this shader cannot see and should not guess. And a difference
// is exactly zero where the atlas is flat, so every texture this mod failed to
// process, and every gap in the atlas, renders precisely as vanilla.
float vvSurfaceBrightness(float vanillaBrightness, vec3 faceNormal, vec2 materialUv)
{
    if (vv_pbrEnabled < 0.5) return vanillaBrightness;
    return clamp(vanillaBrightness + vvReliefDelta(faceNormal, materialUv), 0.0, 1.0);
}

// Specular highlight from the atlas's roughness and specular channels.
//
// Blinn-Phong rather than a microfacet BRDF. The inputs are derived, not
// measured — roughness comes from local texture variance and the specular mask
// from flood-filled colour regions — so a physically exact evaluation of
// approximate data buys nothing a half-vector power does not, and costs a
// square root and two divisions per fragment on every terrain pixel.
//
// The exponent is what actually reads as material: 4 for a rough surface gives
// a broad soft sheen, 256 for polished metal gives a tight glint. Roughness
// drives it, so the same code makes soil matte and steel sharp.
vec3 vvSpecular(vec3 faceNormal, vec2 materialUv, vec3 cameraRelativePos,
                float shadowBrightness, float fog, float murkiness)
{
    if (vv_pbrEnabled < 0.5) return vec3(0.0);

    vec4 material = texture(vv_materialTex, materialUv);
    float roughness = clamp(material.b, 0.0, 1.0);
    float specularMask = clamp(material.a, 0.0, 1.0);

    vec3 n = vvPerturbNormal(normalize(faceNormal), materialUv);
    vec3 l = normalize(lightPosition);

    // worldPos is camera-relative here — vanilla's own applyReflectiveEffect
    // treats it that way — so the view vector points back from the fragment.
    vec3 v = normalize(-cameraRelativePos);
    vec3 h = normalize(l + v);

    float shininess = mix(256.0, 4.0, roughness);
    float highlight = pow(max(0.0, dot(n, h)), shininess);

    // Everything that should suppress a highlight: facing away from the sun,
    // in shadow, at night, or behind fog. Without the dot(n, l) term a surface
    // lit from behind still catches a rim highlight, which reads as glass.
    float visibility = max(0.0, dot(n, l))
                     * clamp(shadowBrightness, 0.0, 1.0)
                     * clamp(dayLight, 0.0, 1.0)
                     * clamp(1.0 - fog - murkiness, 0.0, 1.0);

    return vec3(min(1.0, highlight * specularMask * visibility * vv_pbrSpecularStrength));
}

// Replaces the output with one layer of the material system.
//
// This exists because the material system is four derived quantities stacked on
// top of each other, and when the composite looks wrong there is otherwise no
// way to tell which layer is at fault. It is also the instrument that finds
// sampler problems: if view 1 shows block textures instead of flat lavender,
// vv_materialTex is not reading the atlas at all.
vec4 vvDebugView(vec4 color, vec3 faceNormal, vec2 materialUv, vec3 cameraRelativePos,
                 float shadowBrightness, float fog, float murkiness)
{
    int mode = int(vv_pbrDebugView + 0.5);
    if (mode <= 0) return color;

    vec4 material = texture(vv_materialTex, materialUv);

    // 1: the tangent-space normal as stored, blue forced flat — the same view
    // the offline preview PNG writes, so the two can be compared directly.
    if (mode == 1) return vec4(material.r, material.g, 1.0, color.a);

    if (mode == 2) return vec4(vec3(material.b), color.a);
    if (mode == 3) return vec4(vec3(material.a), color.a);

    // 4: the relief contribution alone, biased so no change reads as mid grey.
    if (mode == 4) return vec4(vec3(0.5 + vvReliefDelta(faceNormal, materialUv)), color.a);

    if (mode == 5) return vec4(vvSpecular(faceNormal, materialUv, cameraRelativePos,
                                          shadowBrightness, fog, murkiness), color.a);

    // 6: the perturbed normal in WORLD space. Unlike view 1 this shows the
    // tangent frame doing its job — the six block faces should come out as six
    // flat colours, with the relief visible as variation within each.
    if (mode == 6) return vec4(vvPerturbNormal(normalize(faceNormal), materialUv) * 0.5 + 0.5, color.a);

    return color;
}
