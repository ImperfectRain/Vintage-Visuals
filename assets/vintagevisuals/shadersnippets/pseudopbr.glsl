// Vintage Visuals - pseudo-PBR surface relief
//
// Injected near the top of chunkopaque.fsh by shaderpatches/pseudopbr.yaml.
// Defines the material sampler and one function; the call site is a single
// token patch in the same file.
//
// What this does: replaces the flat per-face block normal with one perturbed
// by the derived material atlas, then hands that to vanilla's OWN lighting
// call. There is no second lighting model here and no new light maths - the
// game already shades by normal, so giving it a better normal is enough to
// make log grooves, plank seams and gravel read as relief.
//
// The atlas is laid out to match the block texture atlas exactly, so it is
// sampled with the very same `uv` the diffuse uses. Channels:
//   R,G = tangent-space normal X,Y   B = roughness   A = specular
// Only R and G are read here. B and A are populated and waiting for a
// specular term, which needs a light direction this shader does not have yet.

uniform sampler2D vv_materialTex;

// Master switch AND the "did the CPU side actually bind anything" flag. An
// unset GLSL uniform reads as exactly 0, so a failure to upload lands on the
// same branch as a deliberate disable: vanilla normals, vanilla output. The
// same defensive shape as vv_enabled in colorgrade.glsl, and for the same
// reason - a broken bind must degrade to vanilla, never to a black world.
uniform float vv_pbrEnabled;

// Global multiplier on top of the per-material NormalStrength already baked
// into the atlas. Lets the player dial the whole effect back without a rebuild.
uniform float vv_pbrNormalStrength;

// Builds a tangent frame for an axis-aligned block face.
//
// A proper renderer would take tangents from the mesh. Chunk geometry does not
// carry them, but it does not need to: every face is axis-aligned, so one
// consistent frame per axis is exact rather than approximate. The branch picks
// a reference axis that is never parallel to the normal, which is the only way
// this can degenerate.
mat3 vvTangentFrame(vec3 n)
{
    vec3 reference = abs(n.y) > 0.99 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
    vec3 tangent = normalize(cross(reference, n));
    vec3 bitangent = cross(n, tangent);
    return mat3(tangent, bitangent, n);
}

vec3 vvPerturbNormal(vec3 faceNormal, vec2 materialUv)
{
    if (vv_pbrEnabled < 0.5) return faceNormal;

    vec2 xy = (texture(vv_materialTex, materialUv).rg * 2.0 - 1.0) * vv_pbrNormalStrength;

    // Z is reconstructed rather than stored, which is what buys the atlas its
    // fourth channel. Scaling xy first and solving for z afterwards keeps the
    // result unit length at any strength, so turning the effect up tilts the
    // normal further instead of denormalising it. The floor stops a strength
    // above 1 from driving the square root negative.
    float z = sqrt(max(1e-4, 1.0 - dot(xy, xy)));

    return normalize(vvTangentFrame(normalize(faceNormal)) * vec3(xy, z));
}
