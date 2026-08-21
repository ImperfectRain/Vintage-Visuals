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

// Look controls. Each is a uniform rather than a rebuild because the whole
// point is that taste is argued with at runtime, not compiled in.
uniform float vv_pbrRoughnessBias;   // matte <-> gloss, applied to every material
// Daylight, wetness and overcast are NOT declared here. They describe the
// scene rather than this effect, so they live in scene.glsl with one meaning
// shared by every program - see the rule at the top of that file.
// The rest of the look controls are declared in pbrcore.glsl, which this file
// is injected below: they belong to the lobe rather than to the atlas.
uniform float vv_pbrDetailDistance;  // blocks at which relief has faded to nothing
uniform float vv_pbrFoliage;         // light passing through leaves, 0 is vanilla
uniform float vv_pbrCavity;          // small-scale occlusion from the material normal, 0 is vanilla
uniform float vv_pbrDapple;          // sunlight broken up by the canopy above, 0 is vanilla
uniform float vv_weatherRainCover;   // sky exposure a surface needs before rain reaches it
uniform float vv_weatherRipples;     // 0 still water, 1 rain landing in it
uniform float vv_weatherRippleTime;  // ripple clock, pre-wrapped to 0..1 on the CPU

// Camera world position, WRAPPED to VV_ORIGIN_PERIOD on the CPU, so the ripple
// field stays nailed to the ground rather than swimming with the player.
//
// The wrap is not tidiness, it is the whole reason the ripples work. See the
// note above vvRippleSlope.
//
// Duplicated from the weather group's own vv_cloudOrigin on purpose: a uniform
// shared across two patch groups is a dependency between them, and either has
// to be able to roll back without the other losing a declaration it needs.
uniform vec3 vv_pbrOrigin;

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



// ---------------------------------------------------------------------------
// Light through leaves
//
// The strongest single cue that a renderer is doing something modern, and this
// game is full of foliage. A leaf is thin and translucent: light that hits its
// far side does not stop there, it scatters through and leaves the near side
// travelling roughly the way it came. So a canopy with the sun behind it glows,
// and that glow is the effect - not a brighter leaf, a LIT-FROM-BEHIND one.
//
// A real gap rather than a re-tint: vanilla shades leaves with the same opaque
// diffuse it uses for stone. Its own wind deformation moves them without
// touching how they are shaded.
// ---------------------------------------------------------------------------

// Vanilla sets a wind mode on anything that bends in the wind, which in
// practice is exactly the set of things thin enough to transmit light: leaves,
// grass, crops, flowers. chunkopaque.vsh uses this same test for its own
// `bool isLeaves`, so this borrows the game's answer rather than inventing a
// second one that could disagree with it.
bool vvIsFoliage()
{
    return (renderFlags & WindModeBitMask) != 0;
}


// How wet this fragment is.
//
// Two gates, both physical. Rain falls downward, so it pools on up-facing
// surfaces, runs off vertical ones and never touches undersides - squared so
// the falloff is steep rather than linear. And it cannot reach anything the sky
// cannot see, which is what sun exposure measures.
float vvWetness(vec3 faceNormal)
{
    if (vv_sceneWetness < 0.001) return 0.0;

    float facing = clamp(faceNormal.y * 0.5 + 0.5, 0.0, 1.0);

    // A THRESHOLD on sun exposure, not a straight multiply. Vanilla's sun light
    // bleeds sideways under an overhang, so reading it linearly only ever dries
    // out fully enclosed spaces - a porch stays as wet as the lawn beside it.
    // Requiring near-full exposure gets overhangs, canopy and doorways back.
    //
    // This is still a threshold on a soft signal rather than a rain occlusion
    // test. The game has a real one, which is how torches are extinguished, but
    // it answers per block on the CPU and this question is per fragment.
    float exposure = smoothstep(vv_weatherRainCover - 0.12, vv_weatherRainCover + 0.06,
                                clamp(vv_sunExposure, 0.0, 1.0));

    // Foliage does not pool water the way a flat stone does - a leaf sheds it -
    // but it does get thoroughly wet, so the up-facing test is relaxed rather
    // than removed.
    float pooling = vvIsFoliage() ? mix(0.45, 1.0, facing) : facing * facing;

    return clamp(vv_sceneWetness * pooling * exposure, 0.0, 1.0);
}

// How much snow is lying here.
//
// Vanilla places snow BLOCKS and this does not compete with that: it is the
// thin dusting on everything else, gated the same way rain is - it has to fall
// from the sky, so it lands on up-facing surfaces the sky can see.
float vvSnowLayer(vec3 faceNormal)
{
    if (vv_sceneSnow < 0.001) return 0.0;

    float facing = clamp(faceNormal.y, 0.0, 1.0);
    float exposure = smoothstep(vv_weatherRainCover - 0.12, vv_weatherRainCover + 0.06,
                                clamp(vv_sunExposure, 0.0, 1.0));

    // Squared, harder than rain's: rain runs down a slope and snow slides off
    // it, so an angled roof holds much less than a flat one.
    return clamp(vv_sceneSnow * facing * facing * facing * exposure, 0.0, 1.0);
}

// How much frost is on this fragment.
//
// frostAlpha is VANILLA'S, computed in the vertex shader from the block's own
// frostable bit, the local temperature, sunlight and value noise. The mod does
// not decide where frost is - the game already did, per block, and knows which
// blocks can frost at all.
float vvFrostLayer()
{
    if (vv_sceneFrost < 0.001) return 0.0;

    return clamp(frostAlpha, 0.0, 1.0) * clamp(vv_sceneFrost, 0.0, 1.0);
}

// ---------------------------------------------------------------------------
// Rain landing in the water it left
// ---------------------------------------------------------------------------

// How far a ripple tilts the normal, and the shape of the ring.
//
// Measured, not chosen by eye. Transcribing this field into Python and
// sampling it says these give a median tilt of about half a degree - most of
// the ground is between rings and undisturbed - rising to 5 degrees at the
// 90th percentile and around 38 at the crest of a fresh drop. That is what a
// highlight needs in order to break up: an average small enough that still
// water stays still, and a peak large enough to scatter the specular where a
// drop just landed.
const float VV_RIPPLE_DEPTH = 1.2;
const float VV_RIPPLE_BAND = 14.0;
const float VV_RIPPLE_WAVE = 30.0;

// How far a drop may land from the centre of its cell, in cell widths.
//
// Without this every drop sits dead centre and the field is a lattice, which
// is the one thing rain never looks like.
const float VV_RIPPLE_JITTER = 0.5;

// Blocks between repeats of the world coordinate, matching the wrap the CPU
// applies to vv_pbrOrigin. Both octave scales below divide into it exactly, so
// the wrap lands on a cell boundary and never cuts a ring in half.
const float VV_ORIGIN_PERIOD = 4096.0;

// Texels per block in the game's block textures.
//
// Ripples are snapped to this grid so they read as pixels rather than as smooth
// analytic rings. Vintage Story is a pixel-art game and a perfectly round
// anti-aliased ripple sitting on a 32-pixel texture looks like something from a
// different renderer - which it is. Snapping costs nothing and puts the effect
// back in the game's own vocabulary.
//
// Ripples only appear on up-facing surfaces, and a block is one unit across, so
// world XZ quantised to 1/32 IS the top face's texel grid - the ripple lands on
// the texture's own pixels rather than merely near them. 32 divides
// VV_ORIGIN_PERIOD exactly, so the coordinate wrap still lands on a texel
// boundary. A resource pack at another resolution only changes how coarse the
// ripple looks; nothing breaks.
const float VV_RIPPLE_TEXELS = 32.0;

// ---------------------------------------------------------------------------
// Canopy dapple
// ---------------------------------------------------------------------------

// Blocks of canopy assumed above a fragment, from just-under-the-leaves to
// deep under a full crown. Sets how far the pattern slides as the sun moves.
const float VV_DAPPLE_LOW = 2.5;
const float VV_DAPPLE_HIGH = 9.0;

// Blocks across one gap in the leaves.
const float VV_DAPPLE_SCALE = 3.2;

// Where the noise is cut to separate gap from leaf, and how hard that edge is.
//
// MEASURED, not chosen. The two-octave sum here has a standard deviation near
// 0.149, so the field is roughly normal about 0.5 with sd 0.23 - and a
// threshold picked by eye lands somewhere on that curve nobody can predict. The
// first draft of this used 0.56 on the reasoning that "a canopy is mostly leaf",
// which passes only 19% of the area rather than the third it was meant to.
//
//   threshold   mean(gap)   lit
//     0.44        0.355      35%
//     0.48        0.295      29%
//     0.52        0.240      23%
//     0.56        0.191      18%
//
// Re-measure this table if either octave weight changes. Guessing at where a
// threshold sits on a pile of summed noise is the mistake that cost the cloud
// shadows two rounds, once as an invisible effect and once as a world that was
// uniformly slightly darker.
const float VV_DAPPLE_THRESHOLD = 0.44;
const float VV_DAPPLE_SOFT = 0.30;

// The area fraction the threshold above actually passes, subtracted so the
// effect averages to zero. Read it off the table.
//
// Not cosmetic. Three subsystems once washed out the same rainy afternoon by
// each removing a little light, which is what VisualBudget exists to arbitrate.
// Dapple sidesteps that argument entirely by REDISTRIBUTING rather than
// removing: gaps brighten by as much as the shade between them darkens, so the
// mean is unchanged and there is nothing to arbitrate.
//
// Which only holds if this number is right. Against the first draft's 0.56
// threshold it was set to 0.34 by eye, and the true mean was 0.191 - so every
// canopy in the world would have been dimmed by a net 15% by an effect
// documented as mean-preserving, with nothing in the budget accounting for it.
const float VV_DAPPLE_COVER = 0.355;

// How far the pattern may be thrown along the sun's azimuth, in blocks. The
// projection runs away at the horizon like every other one in this mod.
const float VV_DAPPLE_MAX_THROW = 40.0;

// A bit-mixing integer hash, not the usual fract(sin(dot(p, k)) * 43758.5453).
//
// Integer mixing is exact at any coordinate, and it is worth the few extra
// instructions here because everything else about this field turned out to be
// a precision problem and the hash should not be the next one.
uint vvHashU(uint x)
{
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

// Four independent values per cell: where the drop landed (two), when it
// landed, and how big it is.
vec4 vvCellRandom(vec2 cell)
{
    uvec2 c = uvec2(ivec2(cell));
    uint h = vvHashU(c.x * 0x9e3779b9u ^ vvHashU(c.y + 0x85ebca6bu));

    return vec4(float(vvHashU(h)),
                float(vvHashU(h ^ 0x68bc21ebu)),
                float(vvHashU(h ^ 0x02e5be93u)),
                float(vvHashU(h ^ 0x9c8f2d3bu))) / 4294967295.0;
}

// One drop impact per grid cell.
//
// Returned as a SLOPE rather than a height. The lighting reads a normal, and
// building a height field only to difference it costs three more evaluations
// for an answer the slope gives directly.
//
// EVERYTHING here depends on the coordinate arriving pre-wrapped. The first
// version of this did the arithmetic on a raw world position, and Vintage
// Story worlds run to roughly half a million blocks from the origin - at which
// point a float32 holds about sixteen distinct positions inside one ripple
// cell. The rings collapsed into a coarse stamp repeated identically
// everywhere, which is exactly what it looked like. Wrapped, the coordinate
// stays under five thousand and a cell holds two thousand positions.
vec2 vvRippleSlope(vec2 p, float t, float density)
{
    vec2 cell  = floor(p);
    vec2 local = fract(p);

    vec4 rnd = vvCellRandom(cell);

    // Is this cell being rained into at all? This is what makes heavy rain
    // visibly denser rather than merely faster - sampled, it takes the
    // disturbed fraction of the ground from 8% in light rain to 36% in heavy.
    if (rnd.x > density) return vec2(0.0);

    // Where the drop landed, and how far its ring may travel without leaving
    // the cell. A ring clipped by a cell boundary shows the boundary.
    vec2 centre = vec2(0.5) + (rnd.yz - 0.5) * VV_RIPPLE_JITTER;
    float reach = 0.5 - VV_RIPPLE_JITTER * 0.5;

    vec2 offset = local - centre;
    float r = length(offset);
    if (r > reach) return vec2(0.0);

    // How often this cell is hit, as a whole number of drops per wrap of the
    // clock. Whole numbers rather than a continuous rate because the clock is
    // pre-wrapped to 0..1: any other multiplier would jump when it wrapped.
    float rate = 1.0 + floor(rnd.w * 3.0);

    // Phase from the cell's own position hash rather than a fifth random,
    // so neighbouring drops are out of step without another hash.
    float age = fract(t * rate + rnd.y * 0.61 + rnd.z * 0.39);

    // The crest travels out from the impact and the whole ring dies as it
    // goes. Without the decay a drop reads as a permanent bump in the surface
    // rather than as something that happened.
    float front = age * reach;
    float band = exp(-abs(r - front) * VV_RIPPLE_BAND);
    float decay = (1.0 - age) * (1.0 - age);

    return normalize(offset + vec2(1e-5)) * sin((r - front) * VV_RIPPLE_WAVE) * band * decay;
}

// Two scales of drop, at unrelated rates. One grid on its own reads as a grid
// however well the drops inside it are scattered.
//
// Both scales divide VV_ORIGIN_PERIOD exactly - cells of half a block and of
// one block - so the coordinate wrap lands on a cell boundary in both.
//
// The second octave's offset is deliberately NOT a whole number. It used to be
// vec2(31.0, 17.0), and an integer offset on a one-block grid puts its cell
// boundaries exactly on top of the half-block grid's - so the two octaves broke
// at the same lines and reinforced the very lattice the second octave was added
// to hide. A fractional offset slides its boundaries into the middle of the
// other's cells. It is still a constant, so the coordinate wrap is unaffected.
vec2 vvRainRipples(vec2 worldXZ, float t, float density)
{
    // Snapped to the texture's pixel grid before anything samples it, so both
    // octaves land on the same pixels and the result is one coherent pixelated
    // field rather than two smooth ones added together.
    //
    // Space only, never time: the ring still expands smoothly, it is only its
    // EDGE that is made of pixels. Quantising the clock as well would make the
    // drops stutter, which is the opposite of what this is for.
    vec2 p = floor(worldXZ * VV_RIPPLE_TEXELS) / VV_RIPPLE_TEXELS;

    return vvRippleSlope(p * 2.0, t, density)
         + vvRippleSlope(p * 1.0 + vec2(31.43, 17.61), t * 0.5 + 0.37, density) * 0.7;
}

// Perturbs the surface normal with rain landing in standing water.
//
// Gated on wetness AND on the surface facing up, which are not the same test.
// Wetness already asks whether rain can reach the surface; this asks whether
// what reached it stayed there. A vertical face is as wet as the ground beside
// it and has nothing lying on it to be rained into.
vec3 vvRainNormal(vec3 n, vec3 faceNormal, vec3 cameraRelativePos, float wetness, float fade)
{
    if (vv_weatherRipples < 0.001 || wetness < 0.001) return n;

    // Not on foliage. Ripples are rain landing in STANDING water, and a leaf
    // holds none - it sheds. Without this gate a leaf whose normal happened to
    // point upward grew expanding rings on it, which is the wrong effect on the
    // one surface in the game that most obviously cannot puddle.
    if (vvIsFoliage()) return n;

    float pooling = clamp(faceNormal.y, 0.0, 1.0);
    pooling *= pooling;

    float amount = clamp(vv_weatherRipples, 0.0, 1.0) * wetness * pooling * fade;
    if (amount < 0.001) return n;

    // World space, not camera space: ripples belong to the puddle, and a field
    // built on camera-relative coordinates swims across the ground as the
    // player walks.
    vec3 world = cameraRelativePos + vv_pbrOrigin;

    // The clock arrives already wrapped to 0..1. Vanilla's windWaveCounter was
    // the obvious candidate and is the same trap as the world coordinate: it
    // accumulates without bound, and once it passes about ten million a float32
    // cannot separate two phases at all, so every drop in the world lands on
    // the same frame.
    vec2 slope = vvRainRipples(world.xz, vv_weatherRippleTime,
                               clamp(vv_weatherRipples, 0.0, 1.0));

    // fade is vvDetailFade, the same distance falloff the relief uses. Ripples
    // are the highest-frequency thing this shader produces, so they are also
    // the first to alias into sparkle once a cell is smaller than a pixel.
    return normalize(n + vec3(slope.x, 0.0, slope.y) * amount * VV_RIPPLE_DEPTH);
}

// The canopy's gaps, as a field on the ground.
//
// Two octaves and a threshold. Deliberately NOT a third octave: the pattern is
// seen through a moving frame at walking pace, and detail below about a third
// of a block only reads as noise.
//
// Normalised the way the cloud field had to be. A weighted sum of gnoise does
// not span -1..1 - it piles up hard around zero with a standard deviation near
// 0.14 - so a threshold placed on the raw sum sits outside the range the field
// ever visits, and the result is either untouched ground or uniformly darker
// ground. That shipped twice in the cloud shadows before it was measured.
float vvDappleField(vec2 p)
{
    float n = gnoise(p) * 0.66 + gnoise(p * 2.3 + vec2(19.7, 7.3)) * 0.34;
    return clamp(n * 1.55 + 0.5, 0.0, 1.0);
}

// Sunlight broken into moving patches by the leaves overhead.
//
// Returns SIGNED: positive where a gap lets the sun through, negative in the
// shade between. Zero average by construction - see VV_DAPPLE_COVER.
//
// The gate is the whole design, and it is the game's own answer rather than a
// guess. vv_sunExposure is vanilla's per-vertex sun light level: 0 under a
// roof, 1 under open sky, and PARTIAL under a canopy, because leaves absorb
// light on the way down. Partial is therefore an exact statement that something
// leafy is overhead - which is precisely and only where dapple belongs. Open
// ground has nothing above it to break the light; a cellar has no sunlight to
// break. Both ends of the range return zero.
//
// This matters more than the pattern does. An invented field gated to the wrong
// places is what made cloud shadows wrong for four rounds: it looked like
// weather and corresponded to nothing. Here the invention is only the SHAPE of
// the gaps; whether there are gaps at all, and where, comes from the game.
float vvCanopyDapple(vec3 cameraRelativePos, float fade)
{
    if (vv_pbrDapple < 0.001) return 0.0;

    float exposure = clamp(vv_sunExposure, 0.0, 1.0);

    // Rises out of deep shade and falls again as the sky opens up.
    float under = smoothstep(0.12, 0.40, exposure) * smoothstep(0.99, 0.72, exposure);
    if (under < 0.001) return 0.0;

    // No sun, no dapple. Also kills it at night, where a dappled moon would be
    // an effect nobody has ever seen.
    float sun = clamp(vv_sceneDayLight, 0.0, 1.0);
    if (sun < 0.01) return 0.0;

    vec3 toSun = normalize(lightPosition);

    // Where the ray from this fragment up to the leaves crosses the canopy.
    // Deeper shade is read as more canopy overhead, so the pattern slides
    // further under a full crown than under an edge branch.
    float height = mix(VV_DAPPLE_LOW, VV_DAPPLE_HIGH, 1.0 - exposure);

    vec2 azimuth = toSun.xz;
    float azimuthLength = length(azimuth);

    vec3 world = cameraRelativePos + vv_pbrOrigin;
    vec2 at = world.xz;

    float stretch = 1.0;

    if (azimuthLength > 0.0001)
    {
        float up = max(0.12, abs(toSun.y));

        // climb / tan(elevation), written as run and direction so the cap lands
        // on the quantity being capped. The cloud shadows were capped on
        // climb/sin instead and sat too close under their clouds all morning.
        float run = height * azimuthLength / up;
        at += (azimuth / azimuthLength) * min(run, VV_DAPPLE_MAX_THROW);

        // A gap is a hole in a thin layer, so its footprint on the ground is
        // stretched along the azimuth as the sun drops - the reason late
        // afternoon dapple reads as shafts rather than as spots. Capped at four:
        // beyond that it stops looking like light and starts looking like a
        // smear.
        stretch = clamp(1.0 / up, 1.0, 4.0);
    }

    vec2 p = at / VV_DAPPLE_SCALE;

    if (azimuthLength > 0.0001 && stretch > 1.0)
    {
        vec2 dir = azimuth / azimuthLength;
        float along = dot(p, dir);
        p = (p - dir * along) + dir * (along / stretch);
    }

    // Leaves move, so the gaps do. Built from sine and cosine of the clock so
    // it is exactly periodic: vv_sceneClock is pre-wrapped to 0..1 on the CPU,
    // and anything not periodic in it would jump every time it wrapped.
    float phase = vv_sceneClock * 6.2831853;
    p += vec2(sin(phase + p.y * 0.7), cos(phase * 0.83 + p.x * 0.6)) * 0.18;

    float gap = smoothstep(VV_DAPPLE_THRESHOLD, VV_DAPPLE_THRESHOLD + VV_DAPPLE_SOFT,
                           vvDappleField(p));

    return (gap - VV_DAPPLE_COVER) * under * sun * fade * clamp(vv_pbrDapple, 0.0, 2.0);
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

// How much light reaches the eye THROUGH the surface.
//
// The cheap standard model: bend the light direction backwards through the
// surface, then measure how directly the viewer is looking down that bent ray.
// The distortion term is what turns a hard sun disc into the soft wrap real
// foliage has - without it the effect is a mirror-sharp hotspot that reads as
// a bug rather than as light.
float vvTranslucency(vec3 n, vec3 l, vec3 v)
{
    const float distortion = 0.35;
    const float power = 3.0;

    vec3 through = normalize(-l + n * distortion);

    return pow(max(0.0, dot(v, -through)), power);
}

// The colour that comes through, to be added to the lit result.
//
// Tinted by the leaf's own albedo and pushed toward yellow-green, because
// transmitted light has been filtered by chlorophyll on the way out and leaves
// warmer and more saturated than light reflected off the same leaf. Skipping
// that tint is what makes cheap foliage translucency read as grey haze.
vec3 vvFoliageTransmission(vec3 albedo, vec3 n, vec3 l, vec3 v, float shadowBrightness)
{
    if (vv_pbrFoliage < 0.001 || !vvIsFoliage()) return vec3(0.0);

    vec3 tint = albedo * vec3(1.06, 1.12, 0.72);

    // Shadowed leaves do not glow - there is no sun behind them to come
    // through - and daylight scales it for the same reason. Wetness is
    // deliberately absent: a wet leaf transmits no differently, it only
    // reflects more, and that half is already handled.
    return tint
         * vvTranslucency(n, l, v)
         * vv_pbrFoliage
         * clamp(shadowBrightness, 0.0, 1.0)
         * clamp(vv_sceneDayLight, 0.0, 1.0);
}

// ---------------------------------------------------------------------------
// Crevice shading
//
// Occlusion at the scale the material atlas can see, which is the scale nothing
// else in the frame covers. Vanilla ships SSAO, and SSAO works on GEOMETRY: it
// knows a block sits in a corner and darkens the corner. It has no idea that
// the mortar line between two bricks is a groove, because at the depth buffer's
// resolution it is not one.
//
// Worth more in a blocky game than in most. There is very little geometric
// detail to carry form, so what the eye reads as shape has to come from
// shading, and occlusion in the grooves is the strongest cue available.
// ---------------------------------------------------------------------------

// Curvature of the surface, from the material atlas's own normal.
//
// The atlas stores a tangent-space normal whose xy IS the height gradient. The
// divergence of that gradient - how much the surrounding normals lean toward
// this texel rather than away from it - is the surface's curvature: positive in
// a groove, negative on a ridge. That is a real cavity estimate rather than an
// edge detector, and the difference matters: an edge detector darkens ridges
// too, and the result reads as dirt rather than as depth.
//
// Four extra samples, which is why it is behind its own control and behind the
// distance fade. Once a texel is smaller than a pixel the taps are sampling
// noise and the term should already have gone.
float vvCavity(vec2 materialUv, float fade)
{
    if (vv_pbrCavity < 0.001 || fade < 0.001) return 1.0;

    // Not on foliage. This measures curvature in the material normal, and leaf
    // textures are high-contrast by nature - every leaf edge reads as a groove
    // and the canopy comes out looking dirty rather than deep.
    if (vvIsFoliage()) return 1.0;

    vec2 texel = 1.0 / max(vec2(1.0), vec2(textureSize(vv_materialTex, 0)));

    float left  = vvSampleMaterial(materialUv - vec2(texel.x, 0.0)).r;
    float right = vvSampleMaterial(materialUv + vec2(texel.x, 0.0)).r;
    float down  = vvSampleMaterial(materialUv - vec2(0.0, texel.y)).g;
    float up    = vvSampleMaterial(materialUv + vec2(0.0, texel.y)).g;

    // Stored 0..1 around 0.5, so these differences are already signed
    // gradients and no decode is needed.
    float curvature = (left - right) + (down - up);

    // Only the concave half darkens. Brightening ridges as well would be the
    // symmetric thing to do and looks wrong - real cavity occlusion removes
    // light from crevices, it does not add light to bumps.
    float occlusion = clamp(curvature * 2.0, 0.0, 1.0);

    return 1.0 - occlusion * vv_pbrCavity * fade;
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

    // What the world has put on this surface, resolved in one place and in a
    // fixed order: water, then snow, then frost, each displacing the one before
    // rather than stacking on it. Before anything else reads roughness or
    // reflectance, because these are properties OF the surface rather than a
    // layer over the finished shading - a tint at the end is what wetness looks
    // like when it is done wrong.
    float wetness = vvWetness(faceNormal);
    float foliage = vvIsFoliage() ? 1.0 : 0.0;

    VvSurface surface = vvApplyEnvironmentLayers(
        VvSurface(albedo, roughness, specularMask),
        wetness, vvSnowLayer(faceNormal), vvFrostLayer(), foliage);

    albedo = surface.albedo;
    roughness = surface.roughness;
    specularMask = surface.specular;

    // The same normal the relief uses, so the highlight sits on the surface the
    // player can see rather than on one the shading invented.
    vec3 n = vvSurfaceNormal(faceNormal, materialUv, cameraRelativePos);

    // Rain landing in whatever is lying on that surface. After the relief and
    // before anything reads the normal, because a ripple is a disturbance of
    // the water film rather than of the stone under it - and the highlight it
    // breaks up is the whole effect.
    n = vvRainNormal(n, faceNormal, cameraRelativePos, wetness, vvDetailFade(cameraRelativePos));

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

    // Cloud cover diffuses the sun.
    //
    // A clear sky lights the world with a small, very bright source, which is
    // what makes a sharp highlight. An overcast one replaces it with a source
    // the size of the sky: dimmer per unit area, and coming from everywhere.
    // So the direct lobe loses most of its strength and the sky term gains -
    // that redistribution is what an overcast day looks like, and modelling it
    // as "everything gets darker" is the usual way of getting it wrong.
    float overcast = clamp(vv_sceneOvercast, 0.0, 1.0);

    // Everything that should suppress a highlight: shadow, night, fog, water,
    // cloud.
    float visibility = clamp(shadowBrightness, 0.0, 1.0)
                     * clamp(vv_sceneDayLight, 0.0, 1.0)
                     * clamp(1.0 - fog - murkiness, 0.0, 1.0)
                     * vvDetailFade(cameraRelativePos)
                     * mix(1.0, VV_OVERCAST_DIRECT, overcast);

    // The layer resolve already darkened the albedo; this applies the same
    // darkening to the light vanilla computed, which is the half that makes a
    // wet surface read as wet rather than as a differently-coloured dry one.
    vec3 result = litColor.rgb * mix(1.0, mix(VV_LAYER_WET_DARKEN, VV_LAYER_WET_LEAF_DARKEN, foliage),
                                     wetness);

    // Energy conservation. Light reflected specularly is light that did not
    // scatter diffusely, so the diffuse has to give it up - this is what makes
    // metal read as metal rather than as bright plastic, because a metal's
    // diffuse drops to almost nothing and only the highlight remains.
    //
    // Scaled by the strength slider so that 0 is exactly vanilla: a player who
    // turns the effect off must get their old image back, not a darker one.
    result *= mix(vec3(1.0), 1.0 - fresnel, vv_pbrSpecularStrength * specularMask);

    result += specular * ndotl * visibility * vv_pbrSpecularStrength;

    // Occlusion in the grooves, applied to the diffuse and to everything
    // hemispherical, and NOT to the direct lobe.
    //
    // That split is the whole of why this is physical rather than a texture. A
    // crevice is dark because most of the sky cannot see into it; the sun
    // either reaches it or does not, and the normal already decides which.
    // Multiplying the highlight by cavity as well is the usual mistake and
    // leaves polished stone looking dusty.
    // Dampened where the scene is already hard to read. Crevice occlusion
    // removes light, and a cave at night is exactly where a player can least
    // afford more of that - the CPU arbitration cannot see this one because its
    // cost is only knowable per fragment.
    float cavity = mix(1.0, vvCavity(materialUv, vvDetailFade(cameraRelativePos)),
                       vvSceneVisibilityDampen());
    result *= cavity;

    // Ambient is deliberately NOT multiplied by the shadow map: sky light
    // reaches surfaces the sun does not, and killing it in shadow is what makes
    // metal in a doorway look like painted wood. Daylight and fog still apply.
    // Torches, lava and glowing blocks. Fogged like everything else, but not
    // shadowed and not scaled by daylight - see vvBlockLightSpecular.
    result += vvBlockLightSpecular(f0, roughness, n, v, blockLightColor, cameraRelativePos)
            * clamp(1.0 - fog - murkiness, 0.0, 1.0)
            * vv_pbrSpecularStrength
            * cavity;

    result += vvAmbientSpecular(f0, roughness, ndotv, environment)
            * clamp(vv_sceneDayLight, 0.0, 1.0)
            * clamp(1.0 - fog - murkiness, 0.0, 1.0)
            * vv_pbrSpecularStrength
            * mix(1.0, VV_OVERCAST_AMBIENT, overcast)
            * cavity;

    // Light through leaves, last, because it is the one term that is not a
    // reflection off this surface - it is light that went past it. Fogged like
    // everything else so a distant canopy does not glow through the haze.
    result += vvFoliageTransmission(albedo, n, l, v, shadowBrightness)
            * clamp(1.0 - fog - murkiness, 0.0, 1.0);

    // Emission, after everything and dampened by nothing.
    //
    // It is the one term here that is not a response to light arriving - it IS
    // light leaving - so shadow, daylight and the scene's restraint have no
    // business scaling it. Restraint exists to stop the mod removing light the
    // player needs; a light source is the opposite problem. Fog still applies,
    // because a distant forge is genuinely behind more air.
    result += vvEmission(albedo, glowLevel, cameraRelativePos)
            * clamp(1.0 - fog - murkiness, 0.0, 1.0);

    // Sunlight broken up by the leaves overhead.
    //
    // Last, and multiplicative on the whole result, because dapple is not a
    // light of its own - it is a statement about how much of the sun reaches
    // this spot, and everything above has already worked out what the sun does
    // when it arrives. Scaling the finished pixel keeps the surface's own
    // response intact: a wet stone in a sunbeam is a brighter wet stone, not a
    // stone with a bright patch painted over it.
    //
    // Scaled by the shadow term as well, so a fragment vanilla has already put
    // in full shade does not grow sunbeams. Dapple is the sun finding a way
    // through; where there is no sun to find, there is nothing to break up.
    float dapple = vvCanopyDapple(cameraRelativePos, vvDetailFade(cameraRelativePos))
                 * clamp(shadowBrightness, 0.0, 1.0);

    result *= 1.0 + dapple;

    return vec4(result, litColor.a);
}

// Replaces the output with one layer of the material system.
//
// NUMBERED IN THIS FILE'S OWN TERMS, not in a list shared across the mod. The
// numbers here mean material-system layers and nothing else; the entity path
// and any future water or atmosphere pass number their own views from 1 in
// their own terms. One global list stops making sense the moment two
// subsystems both want view 3, and the crowding was already visible at 13.
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

    // 14: emission alone, on black. A forge, a lamp and lava should read; a
    // white marble block should not, which is the check that this is driven by
    // vanilla's glowLevel rather than by pixel brightness.
    if (mode == 14) return vec4(vvEmission(color.rgb, glowLevel, cameraRelativePos), color.a);

    // 15: canopy dapple alone, biased so no effect reads as mid grey. Bright
    // patches are gaps in the leaves, dark is the shade between. It should be
    // flat grey on open ground and flat grey in a cellar - both ends of the
    // sky-exposure gate - and appear only under trees. Walk the edge of a wood
    // and the band should switch on as the canopy closes over.
    if (mode == 15)
    {
        float d = vvCanopyDapple(cameraRelativePos, vvDetailFade(cameraRelativePos));
        return vec4(vec3(0.5 + d), color.a);
    }

    // 12: crevice occlusion alone. White is open surface, dark is a groove.
    // Mortar lines, plank gaps and bark furrows should read; a flat painted
    // texture should stay white, which is also the check that this is finding
    // curvature rather than contrast.
    if (mode == 12) return vec4(vec3(vvCavity(materialUv, vvDetailFade(cameraRelativePos))), color.a);

    // 13: which fragments the foliage path treats as foliage, and how much
    // light it is passing. Black on stone, and brightest on leaves with the sun
    // behind them.
    if (mode == 13)
    {
        vec3 n = vvSurfaceNormal(faceNormal, materialUv, cameraRelativePos);
        return vec4(vvFoliageTransmission(vec3(1.0), n, normalize(lightPosition),
                                          normalize(-cameraRelativePos), shadowBrightness), color.a);
    }

    // 11: the rain ripple field, biased so still water reads as mid grey.
    // Rings should appear and die on up-facing wet ground and nowhere else, so
    // a wall beside a puddle is the check that both gates are working.
    if (mode == 11)
    {
        float wet = vvWetness(faceNormal);
        vec3 still = normalize(faceNormal);
        vec3 rippled = vvRainNormal(still, faceNormal, cameraRelativePos, wet,
                                    vvDetailFade(cameraRelativePos));
        return vec4((rippled - still) * 8.0 + 0.5, color.a);
    }

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
