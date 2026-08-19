// Vintage Visuals - pseudo-PBR surface relief
//
// Injected into chunkopaque.fsh by shaderpatches/pseudopbr.yaml, immediately
// after vanilla's `uniform vec3 lightPosition` — NOT at the top of the file.
// That position is load-bearing: the game expands #include directives before
// this mod ever sees the source, so chunkopaque.fsh arrives as one flat file
// with fogandlight.fsh's uniforms partway down it. Injecting at the top would
// put this code above the `lightPosition` declaration it depends on and fail
// to compile. Anchoring on that declaration puts us in scope and asserts the
// dependency in the same patch.
//
// What this does: modulates vanilla's per-vertex normal brightness with a
// per-fragment term derived from the material atlas, so log grooves, plank
// seams and gravel catch light instead of every block face shading flat.
// There is no second lighting model — the game already shades by normal, and
// this gives it a better one.
//
// The atlas matches the block texture atlas's layout exactly, so it samples
// with the very same `uv` the diffuse uses. Channels:
//   R,G = tangent-space normal X,Y   B = roughness   A = specular
// Only R and G are read here.

// The anchor line, pasted back. Patch 3 in pseudopbr.yaml REPLACES
// `uniform vec3 lightPosition;` with this whole file, so dropping this line
// would delete a vanilla uniform that half of fogandlight.fsh depends on.
uniform vec3 lightPosition;

uniform sampler2D vv_materialTex;

// Master switch AND the "did the CPU side actually bind anything" flag. An
// unset GLSL uniform reads as exactly 0, so a failure to bind lands on the
// same branch as a deliberate disable: vanilla brightness, vanilla output.
// Same defensive shape as vv_enabled in colorgrade.glsl, and it matters more
// here — chunkopaque.fsh draws the world, so this shader's failure mode is
// the difference between a missing effect and a black screen.
uniform float vv_pbrEnabled;

// Global multiplier on top of the per-material NormalStrength already baked
// into the atlas. Lets the player dial the whole effect back without a rebuild.
uniform float vv_pbrNormalStrength;

// Builds a tangent frame for an axis-aligned block face.
//
// A proper renderer would take tangents from the mesh. Chunk geometry does not
// carry them, and does not need to: every face is axis-aligned, so one
// consistent frame per axis is exact rather than approximate. The branch picks
// a reference axis never parallel to the normal, which is the only way this
// can degenerate.
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
    // normal further instead of denormalising it. The floor stops a strength
    // above 1 from driving the square root negative.
    float z = sqrt(max(1e-4, 1.0 - dot(xy, xy)));

    return normalize(vvTangentFrame(faceNormal) * vec3(xy, z));
}

// Vanilla's directional shading term, lifted from getBrightnessFromNormal in
// fogandlight.fsh so the two agree on what "lit" means.
//
// Deliberately WITHOUT that function's second line, `nb = max(nb, normal.y *
// 0.95)`. That term is a sky-bounce fudge so the tops of blocks are not darker
// than their sides, and it saturates every upward-facing surface at 0.95 — if
// it were included here, floors and ground would be the one place relief could
// never show, which is most of what a player looks at. Ambient bounce is not
// what surface relief modulates anyway; the directional term is.
float vvDirectionalShade(vec3 n)
{
    return max(0.0, 0.5 + 0.5 * dot(n, lightPosition));
}

// Adjusts vanilla's brightness by the difference the perturbed normal makes.
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

    vec3 n = normalize(faceNormal);
    float delta = vvDirectionalShade(vvPerturbNormal(n, materialUv)) - vvDirectionalShade(n);

    return clamp(vanillaBrightness + delta, 0.0, 1.0);
}
