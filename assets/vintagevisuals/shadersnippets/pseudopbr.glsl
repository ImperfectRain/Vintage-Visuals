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

// The SECOND material atlas. Same layout, same slots, same UVs - a different
// four properties.
//
// Declared immediately BELOW the first, which is below every vanilla sampler.
// That ordering is not stylistic. Sampler texture units are assigned at LINK
// time from the program's active sampler list, so a sampler inserted above
// vanilla's shifts every unit below it; doing exactly that once pushed
// liquidDepth off the end, saturated getUnderwaterMurkiness, and turned every
// terrain fragment the colour of water murk. Both atlases are also bound to
// explicit units at bind time rather than trusting the linker, so the two
// defences are independent.
//
//   R = metalness      is this surface a conductor
//   G = height         the surface's own relief, normalised per texture
//   B = baked AO       broad occlusion from that height
//   A = emission mask  WHERE an already-emitting block emits
uniform sampler2D vv_materialTex2;

// 0 when the second atlas is unavailable - not built, not uploaded, rolled
// back, or switched off. Zero is also what an unset uniform reads, so every
// consumer below falls back to the pre-second-atlas behaviour by default rather
// than by remembering to.
uniform float vv_material2Valid;

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
uniform float vv_pbrSpecOcclusion;   // 0 keeps the flat cavity on specular, 1 makes it view-aware
uniform float vv_pbrDapple;          // sunlight broken up by the canopy above, 0 is vanilla
uniform float vv_pbrShafts;          // visible beams through the canopy, 0 is vanilla
uniform float vv_pbrCanopyRadius;    // shadow-map ring radius for the canopy test, in texels; 0 is vanilla
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

// Everything the second atlas knows about one texel, in one place.
//
// A struct rather than four calls to .r/.g/.b/.a scattered through the shader:
// channel assignments are the kind of knowledge that gets duplicated and then
// only half-updated, and a consumer that reads .b thinking it is height is a
// bug no compiler will catch.
struct VvMaterial2
{
    float metalness;
    float height;
    float occlusion;
    float emission;
};

// The neutral reading: not metal, mid height, unoccluded, not emitting.
//
// Every field is the value that makes its consumer behave as though this atlas
// did not exist, so an unavailable page degrades to the previous renderer
// rather than to a half-metallic glowing one. It matches the builder's own
// NeutralTexel exactly, and a test pins the two together.
VvMaterial2 vvNeutralMaterial2()
{
    return VvMaterial2(0.0, 0.5, 1.0, 0.0);
}

VvMaterial2 vvSampleMaterial2(vec2 materialUv)
{
    if (vv_material2Valid < 0.5) return vvNeutralMaterial2();

    vec4 texel = texture(vv_materialTex2, vvSnapToTexel(materialUv));
    return VvMaterial2(texel.r, texel.g, texel.b, texel.a);
}

// The emission mask alone, with its own fallback.
//
// 1 rather than 0 when the second atlas is unavailable, and the difference is
// the whole safety of this channel: the mask is a MULTIPLIER on emission the
// game already granted, so "no data" has to mean "emits everywhere it used to"
// and not "emits nowhere". Reading the neutral material's 0 here would silently
// switch every light source in the world off.
float vvEmissionMask(vec2 materialUv)
{
    return vv_material2Valid > 0.5 ? vvSampleMaterial2(materialUv).emission : 1.0;
}

// Deliberately shares vvSnapToTexel with the first atlas rather than measuring
// its own size. The two pages are built at identical dimensions from identical
// slot rectangles, so the same UV must land on the same texel in both - and
// giving the second page its own snap would let them disagree by a texel the
// moment anything about the layout changed. A test pins the dimensions
// together for the same reason.

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
// Canopy dapple: sunflecks
// ---------------------------------------------------------------------------
//
// What a sunfleck actually is, because the first version of this was built on a
// guess and looked like one.
//
// A gap in a canopy is a PINHOLE. The sun is not a point - it subtends about
// 0.53 degrees, 0.0093 radians - so a small gap does not project its own shape
// onto the ground, it projects an IMAGE OF THE SUN. That is why sunflecks under
// a tall tree are all roughly the same rounded shape whatever the gaps above
// them look like, and why they turn into crescents during a partial eclipse.
// The rule is that a gap smaller than about 0.0093 * height stops mattering as
// a shape and only matters as a hole.
//
// Three consequences, and all three are what makes the effect read:
//
//   1. Flecks are DISCRETE. Not a continuous noise field that is brighter in
//      some places - individual soft-edged spots with dark between them. A
//      threshold on summed noise gives amorphous blobs that slide, which is
//      what the first version did.
//   2. Every fleck has a PENUMBRA about 0.0093 * height across. Under a high
//      canopy that is the same order as the fleck itself, so everything is
//      soft; close under a bush the edges are crisp. Softness and size scale
//      together with the height above.
//   3. They are ELLIPSES on the floor, stretched along the sun's azimuth by
//      1 / sin(elevation), because a circular beam meets a horizontal floor at
//      an angle. This is the whole reason afternoon dapple reads as shafts and
//      midday dapple reads as spots.
//
// Coverage: a closed canopy passes something like 5-20% of the light, and
// sunflecks are a MINORITY of the floor - bright spots on shade, not a mottling
// that is half and half. The model below measures out at 14.5%.
//
// Movement is two separate things on two separate timescales. The sun slides
// the whole pattern over minutes, which the azimuth throw already does. Wind
// makes individual flecks WINK - they open and close semi-independently as
// leaves cross the gaps, over seconds. What it does not do is rotate or scroll
// the pattern coherently, which is exactly what the first version did: it
// displaced the sample point by sin and cos of one phase, so the field
// literally orbited, once every 1.5 seconds.

// ---------------------------------------------------------------------------
// Vanilla's own sun occlusion
//
// The game already renders a directional sun shadow map, and both terrain
// shaders already sample it - getBrightnessFromShadowMap() sits a few hundred
// lines above this one. Two facts about how it is built decide the whole
// design of the dapple system, and both were checked in the game's source
// rather than assumed:
//
//   FOLIAGE IS IN IT. chunkshadowmap.fsh samples the block texture and
//   discards where alpha < 0.02. Leaf blocks are alpha-tested cutouts, so the
//   gaps BETWEEN the leaves in the texture punch real holes in the shadow map.
//   The canopy's shadow already has the shape of the canopy.
//
//   IT ALREADY MOVES WITH THE WIND. chunkshadowmap.vsh calls the same
//   applyVertexWarping() the main pass does, wind mode 3 (Leaves) included. So
//   the holes sway using the game's own wind model, at the game's own phase,
//   coherently across a whole tree - which is exactly the spatial coherence a
//   procedural oscillator cannot fake.
//
// This means the mod has been inventing a pattern to stand in for something
// the game computes correctly. What is genuinely missing is only RESOLUTION:
// whether the shadow map has enough texels per metre to resolve a gap between
// leaves. That is a runtime measurement, and these functions plus the debug
// views at the bottom of this file are the instrument for making it.
//
// NOT THE SAME THING AS shadowBrightness. The value the mod passes around as
// "shadow" is vanilla's getBrightnessFromShadowMap(), whose last line is
// `b = clamp(b + blockBrightness, 0, 1)` - a torch RAISES it. It is a
// brightness, not an occlusion, and using it to ask "does the sun reach here"
// gets the answer wrong next to every light source. These functions read the
// shadow map directly and stop before that step.
// ---------------------------------------------------------------------------

// How much of the sun reaches this fragment, geometrically. 1 = unoccluded,
// 0 = fully blocked.
//
// Deliberately NOT scaled by shadowIntensity: that is a graphics setting
// describing how dark the game chooses to draw a shadow, not a statement about
// where the light goes. A player who has turned shadows down still has a tree
// over their head.
float vvSunVisibility()
{
#if SHADOWQUALITY > 0
    float occlusion = 0.0;

    if (shadowCoordsFar.w > 0.0)
    {
        // texture() on a sampler2DShadow returns 1 where the comparison PASSES,
        // meaning lit. Counting down from 9 turns the taps into a count of
        // shadowed samples, which is what vanilla does and why totalFar reads
        // backwards at first glance.
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

    // The two cascade weights are complementary by construction - the far one
    // subtracts the near one in chunkopaque.vsh - so they sum to at most 1 and
    // this needs no normalising.
    return clamp(1.0 - occlusion, 0.0, 1.0);
#else
    // Shadows off. There is no occlusion data at all, and saying "fully lit"
    // is the only honest answer: anything built on this must degrade to
    // vanilla rather than invent a shadow the player switched off.
    return 1.0;
#endif
}

// How BROKEN the shadow is around this fragment, over a neighbourhood several
// times wider than the PCF kernel.
//
// This is the measurement that could replace vv_sunExposure as the canopy
// signal, and the reason is the architectural rule this whole audit turns on:
// sun exposure is a LIGHTING RESULT and this is a GEOMETRIC CAUSE.
//
// A canopy and a roof produce the same partial sun exposure. They do not
// produce the same shadow map. Under a roof every tap for metres around
// agrees: shadowed. In an open field every tap agrees: lit. Under leaves the
// taps DISAGREE, because the gaps are metres apart or less - that disagreement
// is the signature of a broken occluder, and nothing else in a normal world
// produces it over a wide area.
//
// The honest limitation, stated here so it is not discovered later as a
// surprise: a single straight shadow edge also produces disagreement. A wall's
// edge will read as broken in a band about as wide as the sample radius. That
// is a thin band, against today's failure mode of the entire area under any
// roof, but it is a leak and it is why this is not yet wired to anything.
//
// Returns 0 where the neighbourhood is uniform (lit or shadowed alike) and
// approaches 1 where it is evenly split.
float vvSunShadowBreakup(float radiusTexels)
{
#if SHADOWQUALITY > 0
    if (radiusTexels <= 0.0) return 0.0;

    vec2 texel = vec2(shadowMapWidthInv, shadowMapHeightInv) * radiusTexels;

    // Eight taps on a ring plus the centre. A ring rather than a filled disc
    // because the question is how much the neighbourhood disagrees, not how
    // much of it is shadowed, and a ring samples the widest span per tap.
    const vec2 ring[8] = vec2[8](
        vec2( 1.0,  0.0), vec2( 0.7071,  0.7071),
        vec2( 0.0,  1.0), vec2(-0.7071,  0.7071),
        vec2(-1.0,  0.0), vec2(-0.7071, -0.7071),
        vec2( 0.0, -1.0), vec2( 0.7071, -0.7071));

    float lit = 0.0;
    float taps = 0.0;

#if SHADOWQUALITY > 1
    // The near cascade wherever it applies: it is the higher-resolution of the
    // two, and resolving small gaps is the entire point of this measurement.
    bool useNear = shadowCoordsNear.w > 0.0;
#else
    bool useNear = false;
#endif

    if (useNear)
    {
#if SHADOWQUALITY > 1
        lit += texture(shadowMapNear, vec3(shadowCoordsNear.xy, shadowCoordsNear.z - 0.0005));
        taps += 1.0;
        for (int i = 0; i < 8; i++)
        {
            lit += texture(shadowMapNear,
                           vec3(shadowCoordsNear.xy + ring[i] * texel,
                                shadowCoordsNear.z - 0.0005));
            taps += 1.0;
        }
#endif
    }
    else if (shadowCoordsFar.w > 0.0)
    {
        lit += texture(shadowMapFar, vec3(shadowCoordsFar.xy, shadowCoordsFar.z - 0.0009));
        taps += 1.0;
        for (int i = 0; i < 8; i++)
        {
            lit += texture(shadowMapFar,
                           vec3(shadowCoordsFar.xy + ring[i] * texel,
                                shadowCoordsFar.z - 0.0009));
            taps += 1.0;
        }
    }

    if (taps < 1.0) return 0.0;

    float mean = lit / taps;

    // 4 * p * (1 - p): the variance of a Bernoulli draw, scaled to peak at 1
    // when the taps are evenly split. Zero at both ends, which is the property
    // that makes open field and cave interior both read as "not broken".
    return clamp(4.0 * mean * (1.0 - mean), 0.0, 1.0);
#else
    return 0.0;
#endif
}

// How many separate things the shadow boundary crosses around this fragment.
//
// This replaces disagreement-at-a-radius as the canopy test, because
// disagreement is an EDGE DETECTOR and edges are not what distinguishes a
// canopy. 4p(1-p) is maximal where the taps differ and zero wherever the
// neighbourhood is uniform, so it can only ever draw outlines: a point in the
// middle of a leaf shadow reads zero, and so does a point in the middle of a
// wall shadow. Seen in game as "only an outline around already present
// shadows", which is precisely what the formula guarantees.
//
// What actually separates the two is how many times the shadow CHANGES going
// around a circle. Walk a ring of taps in angular order and total the
// absolute differences - the total variation of the ring:
//
//   uniform, lit or shadowed alike   TV = 0
//   one straight edge across it      TV = 2   (out once, back once)
//   N separate gaps or occluders     TV = 2N
//
// A wall, a terrace lip, a cliff and a roof are all ONE edge, wherever the ring
// is placed. A canopy is many, everywhere. That is a topological property of
// the occluder rather than a photometric one, it does not care how deep the
// shadow is, and it fills regions instead of tracing them, because a ring sat
// entirely inside a gappy shadow still crosses several gaps.
//
// It also degrades correctly rather than lying: a ring deep inside a solid
// shadow with no gap within its radius returns 0, and that IS the right answer.
// Sunflecks only exist where light gets through.
//
// SPLIT IN TWO on purpose. vvCanopyVariation returns the raw count and
// vvCanopyStructure applies the threshold, because when the gate shows nothing
// those two failures look identical on screen and need completely different
// fixes: "there is no broken shadow here" is the measurement working, and "the
// band is above what a real canopy scores" is the band being wrong. Debug view
// 31 shows the raw number for exactly this reason.
float vvCanopyVariation(float radiusTexels, out float mean)
{
    mean = 0.0;
#if SHADOWQUALITY > 0
    if (radiusTexels <= 0.0) return 0.0;

    vec2 texel = vec2(shadowMapWidthInv, shadowMapHeightInv) * radiusTexels;

    // Twelve taps. Eight is enough to count a single edge but too coarse to
    // separate two nearby gaps from one wide one, and the whole measure is a
    // count.
    const int VV_RING_TAPS = 12;
    const float VV_RING_STEP = 6.28318530718 / 12.0;

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

    for (int i = 0; i < VV_RING_TAPS; i++)
    {
        float angle = float(i) * VV_RING_STEP;
        vec2 offset = vec2(cos(angle), sin(angle)) * texel;

        float lit;
        if (useNear)
        {
#if SHADOWQUALITY > 1
            lit = texture(shadowMapNear, vec3(shadowCoordsNear.xy + offset,
                                              shadowCoordsNear.z - 0.0005));
#else
            lit = 1.0;
#endif
        }
        else
        {
            lit = texture(shadowMapFar, vec3(shadowCoordsFar.xy + offset,
                                             shadowCoordsFar.z - 0.0009));
        }

        if (i == 0) first = lit;
        else        variation += abs(lit - previous);

        previous = lit;
        sum += lit;
    }

    // Close the ring. Without this a feature sitting exactly at the seam is
    // counted once instead of twice, and the seam is a fixed screen-space
    // direction, so the error would be systematic rather than noise.
    variation += abs(first - previous);

    mean = sum / float(VV_RING_TAPS);
    return variation;
#else
    return 0.0;
#endif
}

// The count, thresholded into canopy evidence.
float vvCanopyStructure(float radiusTexels)
{
    float mean;
    float variation = vvCanopyVariation(radiusTexels, mean);
    if (variation <= 0.0) return 0.0;

    // One edge is 2. Two features are 4. The band starts above a single edge
    // so a wall contributes nothing, and saturates at three features.
    float features = smoothstep(2.6, 6.0, variation);

    // A ring can register variation while being almost entirely on one side of
    // a boundary - a tangent clip. Requiring the ring to be genuinely mixed as
    // well removes those without touching a real gappy shadow, which is mixed
    // by definition.
    float mixed = clamp(4.0 * mean * (1.0 - mean), 0.0, 1.0);

    return clamp(features * sqrt(mixed), 0.0, 1.0);
}

// Whether a CANOPY is what is blocking the sun here.
//
// Measured, not assumed, and it replaces vv_sunExposure as the dapple gate.
//
// The measurement that settled it: debug view 16 on a birch forest floor at
// midday reads essentially 1 EVERYWHERE - open ground, under the crowns, on
// the terraces. vv_sunExposure carries almost no canopy information at all in
// a real scene, because Vintage Story's sunlight flood fill barely attenuates
// through leaves. So the old gate, `smoothstep(0.97, 0.62, exposure)`, passed
// almost nothing under actual trees and passed its maximum wherever something
// else happened to hold sun light down - a terrace lip, an overhang, a
// doorway. The effect was not weak in the right places, it was firing in the
// wrong ones.
//
// Debug view 25 in the same spot shows vanilla's shadow map resolving
// INDIVIDUAL LEAF SHADOWS. The information was there the whole time.
//
// The discriminator is COUNT, not scale and not depth. A wall, a terrace lip,
// a cliff and a roof are each ONE edge; a canopy is many. See
// vvCanopyStructure for why that is the property worth measuring and why the
// first attempt - disagreement at a small radius - could only draw outlines.
float vvCanopyEvidence()
{
    // Only where the sun is actually blocked. A lit fragment has no shadow to
    // be broken and nothing to redistribute.
    float shadowed = 1.0 - vvSunVisibility();
    if (shadowed < 0.01) return 0.0;

    // Counted, not detected. See vvCanopyStructure: the previous version used
    // 4p(1-p) at a small radius, which is an edge detector and could only ever
    // outline a shadow rather than fill it.
    //
    // The radius is the one number here that is genuinely a guess, and it is a
    // guess about shadow-map scale rather than about the world - how many
    // texels apart the gaps in a canopy land, which depends on the player's
    // shadow quality and on how far away they are standing. Two rounds of
    // guessing it from screenshots is enough, so it is a slider: views 30 and
    // 31 show what it is counting and the player can move it until the area
    // under a crown fills instead of outlining.
    //
    // Zero disables the whole test, which is the correct vanilla behaviour for
    // an unset uniform.
    float structure = vvCanopyStructure(clamp(vv_pbrCanopyRadius, 0.0, 16.0));

    return clamp(structure * shadowed, 0.0, 1.0);
}

// Blocks of canopy assumed above a fragment, from just-under-the-leaves to
// deep under a full crown. Sets both how far the pattern slides as the sun
// moves and, through the penumbra, how soft the flecks are.
const float VV_DAPPLE_LOW = 3.0;
const float VV_DAPPLE_HIGH = 11.0;

// Blocks between one fleck and the next.
//
// Not a whole number on purpose. At 1.0 the fleck grid would land in step with
// the block grid and the texture grid under it, and a pattern that agrees with
// the blocks reads as the blocks glowing rather than as light falling on them.
const float VV_DAPPLE_SCALE = 0.70;

// Fraction of cells with a gap in them, the fleck's radius in cell units, and
// how much of that radius is penumbra rather than core.
//
// MEASURED, not chosen: mean coverage 0.1452 with 13.5% of the area above half.
// That sits inside the 10-25% real sunflecks occupy. The previous version's
// constants were picked by eye and were out by a factor of two in the direction
// that dims the world.
const float VV_DAPPLE_DENSITY = 0.70;
const float VV_DAPPLE_RADIUS = 0.42;
const float VV_DAPPLE_PENUMBRA = 0.50;

// How far a gap's centre may sit from its cell's centre.
const float VV_DAPPLE_JITTER = 0.60;

// The band a fleck's own oscillation is cut at as it winks open and shut. Wide
// enough that flecks spend real time fully open and fully shut rather than
// forever crossfading.
const float VV_DAPPLE_BLINK_LOW = 0.15;
const float VV_DAPPLE_BLINK_HIGH = 0.85;

// The measured mean of the field above. 11% of the area is fleck.
//
// No longer subtracted, and the reason is worth writing down because it is the
// second thing that went wrong here.
//
// The previous version subtracted this to make the effect mean-preserving, so
// that it never became a net dimming VisualBudget had not accounted for. That is
// the right instinct and it does not survive contact with the coverage. At 11%
// lit, holding the mean fixed forces the bright ninth to be enormously brighter
// than the dark eight-ninths - which is physically true, a real sunfleck is
// close to full sun against shade a tenth of it, but a game has nowhere to put
// that range. Pixels went past 1.0, the bloom pass multiplies the whole frame
// rather than thresholding it, and a forest floor came out as white spotlights.
//
// So this is now DARKEN-ONLY: a fleck is where light was not taken away, not
// where light was added. Nothing can exceed vanilla's own brightness, which
// makes blowing out arithmetically impossible rather than merely unlikely, and
// it is also the more honest model - a canopy removes light, it does not make
// any. The cost is that dapple does dim on average, so it is a light-removing
// term and belongs to VisualBudget's accounting like the rest of them.
const float VV_DAPPLE_COVER = 0.1105;

// How green the shade between flecks is.
//
// A leaf transmits only about 5-10% of the visible light that hits it and
// reflects about another 10, but both are strongly wavelength dependent - green
// gets through several times more readily than red or blue, which is why a leaf
// is green in the first place. So light that has been through the canopy is
// green-dominant, and the shade under a tree is not merely darker than open
// ground, it is a different colour. The flecks themselves are sunlight that
// missed every leaf, so they stay neutral.
//
// Small. This is a tint on the shaded fraction only, and the game's own
// colormap already owns what foliage looks like.
const float VV_DAPPLE_GREEN = 0.055;

// How much light the canopy takes out of the shade between flecks, at full
// slider. Flecks themselves are left exactly at vanilla.
const float VV_DAPPLE_SHADE = 0.28;

// Blocks at which flecks begin to dissolve, and over how many blocks.
//
// This is the strobing, and it was aliasing rather than animation. A fleck is
// about half a block across, so past a certain distance several of them land
// inside one pixel and which one gets sampled depends on exactly where that
// pixel falls. Move the camera slightly and the answer changes, every pixel
// independently, and a forest full of that reads as a disco ball. Slowing the
// blink could never have helped, because the blink was not what was moving.
//
// Faded by distance rather than by fwidth on purpose. The derivative is the
// more precise measure, but this field is computed inside a gate that early-outs
// on open ground and indoors - divergent control flow, where a quad's helper
// lanes may have taken the other branch and the derivative is unreliable
// exactly at the edges of the effect. Distance needs no derivative, and it is
// what the relief and the ripples already fade on.
//
// Much nearer than the relief fade: flecks are finer than surface detail and
// stop being resolvable sooner. Real dapple is not readable from fifty metres
// either.
// How brightly a backlit leaf and a lit sunfleck feed vanilla's god-ray
// channel. Small: the radial blur accumulates well over a hundred samples, so a
// source that looks reasonable on its own comes out as a searchlight.
//
// The two are NOT equal partners, and the gap between them is a physical claim
// rather than a taste one.
//
// A shaft exists because air scatters sunlight toward the eye while an occluder
// removes that sunlight from part of the view. The canopy silhouette IS that
// occluder boundary, so backlit foliage is a defensible stand-in for where a
// beam begins. A lit patch of ground is not: it is a RECEIVER. It has already
// absorbed the light; it does not scatter a beam back up to the gap that made
// it. Streaking it toward the sun is a rendering approximation that happens to
// reinforce the shape, and nothing more.
//
// So the ground term is kept deliberately weak - reinforcement, not a second
// source of equal standing - and this note is here so the next person tuning it
// upward knows what they are trading away.
const float VV_SHAFT_LEAF = 0.34;
const float VV_SHAFT_GROUND = 0.10;

const float VV_DAPPLE_FADE_START = 22.0;
const float VV_DAPPLE_FADE_RANGE = 22.0;

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

// The sunfleck field: discrete soft-edged spots, not a noise threshold.
//
// p is in cell space and already compressed along the sun's azimuth, so a fleck
// is a CIRCLE here and comes out as an ellipse stretched along the azimuth once
// that compression is undone. Doing it this way rather than stretching the disc
// directly is what lets the neighbourhood search below stay square.
//
// The 3x3 sweep is not optional. A fleck is jittered off its cell's centre and
// is nearly a cell wide, so it reaches into its neighbours; sampling only the
// cell a fragment falls in would clip every fleck at the cell border and put a
// square grid through the middle of the effect.
float vvSunflecks(vec2 p, float softness)
{
    vec2 base = floor(p);
    float total = 0.0;

    for (int dy = -1; dy <= 1; dy++)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            vec2 cell = base + vec2(float(dx), float(dy));
            vec4 rnd = vvCellRandom(cell);

            // Most of the canopy is leaf. Only some cells have a hole in them.
            if (rnd.x > VV_DAPPLE_DENSITY) continue;

            vec2 centre = cell + 0.5 + (rnd.yz - 0.5) * VV_DAPPLE_JITTER;

            // Each fleck winks on its OWN phase. This is the part that reads as
            // leaves moving: neighbouring flecks are unrelated, so the canopy
            // shimmers rather than sliding as one piece. Periodic in the breeze
            // clock by construction, so its wrap is invisible.
            float swing = 0.5 + 0.5 * sin((vv_sceneBreeze + rnd.w) * 6.2831853);
            float openness = smoothstep(VV_DAPPLE_BLINK_LOW, VV_DAPPLE_BLINK_HIGH, swing);

            // The penumbra. Softness grows with the height of the canopy above,
            // because the sun's half-degree spreads further the further it has
            // to fall - so a fleck under a high crown is nearly all edge and one
            // under a low branch has a hard core.
            float d = length(p - centre) / VV_DAPPLE_RADIUS;
            total += (1.0 - smoothstep(1.0 - softness, 1.0, d)) * openness;
        }
    }

    return min(total, 1.0);
}

// Sunlight broken into moving flecks by the leaves overhead.
//
// Returns how much light the canopy TAKES AWAY, 0..1: nothing inside a fleck,
// most of VV_DAPPLE_SHADE in the shade between. Never adds - see the note on
// VV_DAPPLE_COVER.
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

    // Never on the canopy itself. A sunfleck is light that got PAST the leaves
    // and landed on something below - ground, trunk, undergrowth, a player. On
    // a leaf block it is not dapple at all, and putting it there lit up every
    // tree in the world from the outside, which is the opposite of the effect:
    // the tree should be casting this, not wearing it.
    if (vvIsFoliage()) return 0.0;

    // GEOMETRIC, not photometric. See vvCanopyEvidence: this asks whether the
    // thing blocking the sun here is broken at leaf scale, which is a fact
    // about the occluder. The value it replaced asked how much sky light
    // reached this vertex, which is a fact about the result, and measured
    // ~1 under a real canopy.
    float under = vvCanopyEvidence();
    if (under < 0.001) return 0.0;

    // No sun, no flecks. Also kills it at night, where a dappled moon would be
    // an effect nobody has ever seen.
    float sun = clamp(vv_sceneDayLight, 0.0, 1.0);
    if (sun < 0.01) return 0.0;

    vec3 toSun = normalize(lightPosition);

    // Deeper shade is read as more canopy overhead, so the pattern slides
    // further, and its flecks are softer, under a full crown than under an edge
    // branch.
    // Deeper, more thoroughly broken shadow is read as more canopy overhead, so
    // the pattern slides further and its flecks go softer under a full crown
    // than under an edge branch. Driven by the evidence term now that the
    // exposure it used to read has been shown to be flat.
    float height = mix(VV_DAPPLE_LOW, VV_DAPPLE_HIGH, under);

    vec2 azimuth = toSun.xz;
    float azimuthLength = length(azimuth);

    vec3 world = cameraRelativePos + vv_pbrOrigin;
    vec2 at = world.xz;

    float stretch = 1.0;

    if (azimuthLength > 0.0001)
    {
        float up = max(0.12, abs(toSun.y));

        // height / tan(elevation), written as run and direction so the cap
        // lands on the quantity being capped. The cloud shadows were capped on
        // height/sin instead and sat too close under their clouds all morning.
        float run = height * azimuthLength / up;
        at += (azimuth / azimuthLength) * min(run, VV_DAPPLE_MAX_THROW);

        // 1 / sin(elevation): a round beam meeting the floor at an angle. Capped
        // at three, past which it stops reading as a shaft and starts reading as
        // a smear.
        stretch = clamp(1.0 / up, 1.0, 3.0);
    }

    vec2 p = at / VV_DAPPLE_SCALE;

    // Compressed along the azimuth, so the circular flecks in cell space come
    // out stretched along it in the world.
    if (azimuthLength > 0.0001 && stretch > 1.0)
    {
        vec2 dir = azimuth / azimuthLength;
        float along = dot(p, dir);
        p = (p - dir * along) + dir * (along / stretch);
    }

    // The sun's angular width, spread over the fall from the canopy, as a
    // fraction of a fleck's radius. Clamped so a fleck always keeps some core
    // and never becomes pure gradient.
    float penumbra = clamp(VV_DAPPLE_PENUMBRA * height / VV_DAPPLE_HIGH, 0.25, 0.85);

    // Dissolved with distance before the flecks alias into scintillation. Toward
    // 1 rather than 0, since 1 is "no light removed" - so the effect fades back
    // into vanilla rather than into flat shade.
    float resolvable = clamp(1.0 - (length(cameraRelativePos) - VV_DAPPLE_FADE_START)
                                   / VV_DAPPLE_FADE_RANGE, 0.0, 1.0);
    if (resolvable < 0.004) return 0.0;

    float fleck = mix(1.0, vvSunflecks(p, penumbra), resolvable);

    // How much light the canopy takes away here: none inside a fleck, most of
    // VV_DAPPLE_SHADE between them. Never negative, so the caller can only ever
    // darken - see the note on VV_DAPPLE_COVER for why that is the whole point.
    return (1.0 - fleck) * VV_DAPPLE_SHADE
         * under * sun * fade * clamp(vv_pbrDapple, 0.0, 2.0);
}

// Feeds vanilla's OWN god-ray channel, so the beams are the game's rather than
// a second invented system sitting next to it.
//
// outGlow.g is the source mask for godrays.fsh, which radially blurs the frame
// outward from the sun's screen position and accumulates wherever that mask is
// bright. That is exactly what a shaft is - light streaking away from the sun
// past whatever is occluding it - and terrain barely writes to it: chunkopaque
// only sets it on sky-fading fragments, and chunktopsoil hard-codes zero.
//
// So the beams cost one number per fragment. No marching, no second buffer, no
// depth reads. It also means they inherit the player's own god-ray graphics
// setting: with godrays off, this writes a mask nothing reads and the effect
// simply is not there, which is the correct way for it to degrade.
//
// Two sources, because a canopy produces beams two ways:
//
//   - BACKLIT LEAVES. Looking toward the sun through a crown, the leaf edges
//     around each gap are what the shafts appear to emanate from.
//   - SUNFLECKS, weakly. A lit patch of ground is a receiver rather than a
//     source - it has absorbed the light, not scattered it - so streaking it
//     toward the sun is an approximation that reinforces the shape and is
//     weighted accordingly. See VV_SHAFT_GROUND.
float vvCanopyShaft(vec3 cameraRelativePos)
{
    if (vv_pbrShafts < 0.001) return 0.0;

    float sun = clamp(vv_sceneDayLight, 0.0, 1.0);
    if (sun < 0.01) return 0.0;

    vec3 toSun = normalize(lightPosition);

    // Angular proximity to the sun - NOT, as this comment used to claim, a test
    // of whether the camera is facing it. The distinction was raised in review
    // as a bug and it is not one, but the description was wrong and worth
    // correcting.
    //
    // What this measures is how close a fragment lies to the sun's direction,
    // which for a blur that is radial from the sun's SCREEN position is the
    // quantity that matters: the mask should be strong near the sun and fade
    // away from it. It also subsumes the camera test it was mislabelled as - if
    // the sun is behind the player then no fragment in the frustum has a
    // direction anywhere near toSun, so the mask is zero everywhere without
    // needing to ask about the camera at all.
    //
    // It is also where the effect gets its dependence on the real sun: the
    // beams follow lightPosition, so they swing through the day and lie flat at
    // dawn without being told to.
    float look = dot(normalize(cameraRelativePos), toSun);
    float facing = smoothstep(0.35, 0.95, look);
    if (facing < 0.004) return 0.0;

    float strength = clamp(vv_pbrShafts, 0.0, 2.0) * sun * facing;

    // Leaves lit from behind. Vanilla already draws the transmission; this says
    // that the same fragments are where beams start.
    if (vvIsFoliage()) return strength * VV_SHAFT_LEAF;

    float exposure = clamp(vv_sunExposure, 0.0, 1.0);
    float under = smoothstep(0.12, 0.40, exposure) * smoothstep(0.97, 0.62, exposure);
    if (under < 0.004) return 0.0;

    // A COARSE fleck - one cell, no neighbours, no blink. The radial blur
    // smears this across a hundred-odd samples before anyone sees it, so paying
    // for the full field here would buy detail that is destroyed by the next
    // pass.
    vec2 p = (cameraRelativePos.xz + vv_pbrOrigin.xz) / VV_DAPPLE_SCALE;
    vec2 cell = floor(p);
    vec4 rnd = vvCellRandom(cell);

    if (rnd.x > VV_DAPPLE_DENSITY) return 0.0;

    vec2 centre = cell + 0.5 + (rnd.yz - 0.5) * VV_DAPPLE_JITTER;
    float d = length(p - centre) / VV_DAPPLE_RADIUS;
    float fleck = 1.0 - smoothstep(0.5, 1.0, d);

    return strength * under * fleck * VV_SHAFT_GROUND;
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

// Which way the grain runs, and how sure we are, measured from the texture.
//
// Returns (direction.x, direction.y, coherence) in tangent space. Coherence is
// 0 where the surface has no preferred direction and 1 where it is perfectly
// linear.
//
// THE DIRECTION IS NOT ASSUMED, and that is the whole design. Wood grain is a
// set of roughly parallel lines, and the atlas already stores exactly what is
// needed to find them: its normal's xy IS the height gradient. A gradient points
// ACROSS a line, so the grain runs perpendicular to it.
//
// Averaging gradients directly would be wrong - the two sides of a grain line
// have opposite gradients and cancel. The structure tensor is the standard fix:
// average the OUTER PRODUCT of the gradient with itself, which is invariant to
// sign, then take its principal axis. Its two eigenvalues also give coherence
// for free, as their normalised difference, which is a measurement of how
// fibrous this texel is rather than a guess.
//
// That measurement is what gates the effect. A plank's grain is strongly
// coherent and gets the full anisotropic lobe; stone is mottled noise, its
// eigenvalues are nearly equal, coherence collapses and the surface stays
// isotropic. Nothing has to know which block it is looking at - which matters,
// because the shader has no way to know, and fingerprinting a material from its
// roughness and specular is exactly the kind of inference this file criticises
// elsewhere.
//
// Five taps, and only on the direct lobe.
vec3 vvGrainDirection(vec2 materialUv)
{
    vec2 texel = 1.0 / max(vec2(1.0), vec2(textureSize(vv_materialTex, 0)));

    // The stored normal is centred on 0.5, so these are already signed
    // gradients and need no decode.
    vec2 c = vvSampleMaterial(materialUv).rg - 0.5;
    vec2 l = vvSampleMaterial(materialUv - vec2(texel.x, 0.0)).rg - 0.5;
    vec2 r = vvSampleMaterial(materialUv + vec2(texel.x, 0.0)).rg - 0.5;
    vec2 d = vvSampleMaterial(materialUv - vec2(0.0, texel.y)).rg - 0.5;
    vec2 u = vvSampleMaterial(materialUv + vec2(0.0, texel.y)).rg - 0.5;

    // Structure tensor, summed over the neighbourhood.
    float jxx = c.x * c.x + l.x * l.x + r.x * r.x + d.x * d.x + u.x * u.x;
    float jyy = c.y * c.y + l.y * l.y + r.y * r.y + d.y * d.y + u.y * u.y;
    float jxy = c.x * c.y + l.x * l.y + r.x * r.y + d.x * d.y + u.x * u.y;

    float trace = jxx + jyy;
    if (trace < 1e-6) return vec3(1.0, 0.0, 0.0);

    // Eigenvalues of a symmetric 2x2.
    float diff = jxx - jyy;
    float root = sqrt(max(0.0, diff * diff + 4.0 * jxy * jxy));

    float major = 0.5 * (trace + root);
    float minor = 0.5 * (trace - root);

    // Normalised difference: 0 when the two axes are equally strong (noise),
    // 1 when one dominates completely (a clean line).
    float coherence = clamp((major - minor) / max(1e-6, trace), 0.0, 1.0);

    // Principal eigenvector - the direction the gradient prefers, i.e. ACROSS
    // the grain.
    vec2 across = normalize(vec2(jxy, major - jxx) + vec2(1e-6, 0.0));

    // The grain itself is perpendicular to that.
    return vec3(-across.y, across.x, coherence);
}

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

// How much of the REFLECTED light a groove takes away, as opposed to how much
// of the ambient it takes away. They are not the same number and using one for
// both is the usual shortcut.
//
// Occlusion derived from geometry answers "how much of the hemisphere can this
// texel see", which is the right question for diffuse and for sky light because
// both gather from the whole hemisphere. A reflection does not gather from the
// hemisphere - it gathers from a lobe around the mirror direction, whose width
// is set by roughness and whose position is set by the view. So a polished
// surface in a shallow groove still reflects almost everything, while a rough
// one in the same groove loses nearly all of it. Multiplying both by the same
// cavity leaves polished stone looking dusty, which is exactly what the comment
// above vvCavity warned about while the code did it anyway.
//
// Lagarde's approximation, from the Frostbite course notes. The exponent
// collapses toward 1 as roughness rises, so a rough surface converges on plain
// occlusion and a smooth one escapes it.
//
// vv_pbrSpecOcclusion blends from the previous behaviour rather than from
// nothing: at 0 this returns the flat cavity the shader used before, so turning
// the feature off reproduces the old image exactly rather than an
// unoccluded one.
float vvSpecularOcclusion(float cavity, float ndotv, float roughness)
{
    float occlusion = clamp(cavity, 0.0, 1.0);
    float view = clamp(ndotv, 0.0, 1.0);

    float lobe = clamp(pow(view + occlusion, exp2(-16.0 * clamp(roughness, 0.0, 1.0) - 1.0))
                       - 1.0 + occlusion, 0.0, 1.0);

    // Bounded BELOW by the hemispherical occlusion, and this is not tidying up.
    //
    // Lagarde's expression is an empirical fit, not a derivation, and it
    // inverts where (ndotv + occlusion) falls below 1: at a grazing angle in a
    // deep groove it hands a polished surface LESS reflection than a rough one,
    // which is backwards and was caught by the monotonicity test rather than by
    // reading it. Measured at occlusion 0.3, ndotv 0.4 the smooth answer was
    // 0.203 against 0.300 for rough.
    //
    // Clamping to the plain occlusion says: a reflection is never dimmed by
    // MORE than the ambient is. With only a scalar cavity there is no way to
    // know whether the groove lies along the reflection lobe or across it, and
    // under that ignorance the conservative reading is the right one. It also
    // makes the whole term a relaxation - specular can only ever gain light
    // relative to what shipped before, never lose it - which is what the
    // feature is for and is a far easier property to reason about.
    lobe = clamp(lobe, occlusion, 1.0);

    return mix(occlusion, lobe, clamp(vv_pbrSpecOcclusion, 0.0, 1.0));
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

    // Real metalness when the second atlas has it, the specular-mask stand-in
    // when it does not.
    //
    // Scaled by vv_pbrMetalResponse so the player keeps the same control they
    // had over the stand-in, and so 0 collapses to a fully dielectric world
    // rather than to a different kind of metal.
    VvMaterial2 material2 = vvSampleMaterial2(materialUv);
    float metalness = clamp(material2.metalness, 0.0, 1.0) * clamp(vv_pbrMetalResponse, 0.0, 1.0);

    vec3 f0 = vv_material2Valid > 0.5
        ? vvReflectanceF0FromMetalness(albedo, metalness)
        : vvReflectanceF0(albedo, specularMask);
    vec3 fresnel = vvFresnelSchlick(vdoth, f0);

    // The highlight's shape. Anisotropic where the surface is measurably
    // fibrous, isotropic everywhere else - see vvGrainDirection.
    //
    // Only the DIRECT lobe. The ambient and block-light terms are hemispherical
    // approximations that never had a lobe shape to stretch, and giving them a
    // grain direction would be inventing detail the model does not contain.
    float distribution;

    if (vv_pbrGrain > 0.001)
    {
        vec3 grain = vvGrainDirection(materialUv);

        // Scaled by the material's own reflectance as well as by coherence: a
        // matte surface has no highlight to stretch, so soil and sand stay out
        // of this however striated their texture happens to be.
        float anisotropy = clamp(grain.z, 0.0, 1.0) * clamp(vv_pbrGrain, 0.0, 1.0)
                         * clamp(specularMask * 2.0, 0.0, 1.0);

        mat3 frame = vvTangentFrame(normalize(faceNormal));
        vec3 tangent = normalize(frame * vec3(grain.xy, 0.0));
        vec3 bitangent = cross(n, tangent);

        distribution = vvDistributionGGXAnisotropic(ndoth, dot(tangent, h), dot(bitangent, h),
                                                    roughness, anisotropy);
    }
    else
    {
        distribution = vvDistributionGGX(ndoth, roughness);
    }
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

    // A conductor has essentially no diffuse lobe. Light either reflects off
    // the surface or is absorbed by the free electrons underneath it; almost
    // none re-emerges scattered, which is why a polished metal shows you the
    // room rather than its own colour.
    //
    // This is the second half of metalness and the half that is usually
    // skipped. Raising F0 alone makes metal brighter - a shinier dielectric.
    // Removing the diffuse at the same time is what makes it darker where it is
    // not reflecting anything, and that contrast is the whole read.
    //
    // Only where the atlas actually said so: without the second page metalness
    // is zero and this is exactly 1, so the line costs nothing and changes
    // nothing. Scaled by the specular slider for the same reason the line above
    // it is - a player who turns the effect off gets their old image back.
    result *= mix(1.0, 1.0 - metalness, vv_pbrSpecularStrength);

    // Occlusion in the grooves.
    //
    // Computed here rather than below because the diffuse and the specular now
    // take DIFFERENT amounts of it, and the split has to happen before the lobe
    // is added rather than after. The previous order applied one flat cavity to
    // everything accumulated so far - including the direct lobe, despite the
    // comment here claiming it did not.
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

    // Hemispherical occlusion, on the diffuse only.
    result *= cavity;

    // Reflections lose a different amount - see vvSpecularOcclusion.
    float specularOcclusion = vvSpecularOcclusion(cavity, ndotv, roughness);

    // Energy the single-scatter lobe dropped, returned. A multiplier of at
    // least 1, so this cannot make the highlight dimmer than it was.
    vec3 energy = vvMultiScatterCompensation(f0, roughness, ndotv);

    result += specular * ndotl * visibility * vv_pbrSpecularStrength * specularOcclusion * energy;

    // Sunlight broken up by the leaves overhead.
    //
    // POSITION IN THIS FUNCTION IS THE CONTRACT. It used to sit at the very
    // end, multiplying the finished pixel - after block-light specular, after
    // the sky term, after foliage transmission and after EMISSION. A canopy
    // therefore dimmed a torch and dimmed a glowing forge, which is not
    // something a canopy does. Dapple is a statement about how much of the SUN
    // arrives; nothing below this line is sunlight.
    //
    // What it still scales, and cannot help scaling: the diffuse term, which
    // arrives from vanilla with sky light already mixed into it and no way to
    // separate the two. A canopy does block sky light as well as sun, so that
    // is defensible rather than merely unavoidable, but it is not exact and
    // saying so here is cheaper than rediscovering it.
    //
    // SUBTRACTIVE, never additive. A fleck is where the leaves failed to block
    // the sun, not a light of its own, so the brightest this can leave a pixel
    // is exactly what vanilla lit it to. That is not a stylistic preference:
    // the additive version pushed pixels past 1.0, findbright multiplies the
    // whole frame rather than thresholding it, and a forest floor came back as
    // white spotlights with bloom halos.
    //
    // Scaled by the shadow term as well, so a fragment vanilla has already put
    // in full shade does not get dappled a second time.
    float shaded = vvCanopyDapple(cameraRelativePos, vvDetailFade(cameraRelativePos))
                 * clamp(shadowBrightness, 0.0, 1.0);

    if (shaded > 0.0)
    {
        result *= 1.0 - clamp(shaded, 0.0, 0.85);

        // Green shade.
        //
        // A leaf transmits roughly 5-10% of the visible light that reaches it
        // and reflects about another 10, and both are far higher in green than
        // in red or blue - which is what makes a leaf green to begin with. So
        // light that has been through a canopy is green-dominant: the floor of
        // a wood is not merely darker than the field beside it, it is a
        // different colour, and that is the strongest single cue that you are
        // under trees.
        //
        // On the shaded fraction only, because a fleck is sunlight that missed
        // every leaf on the way down and has no business being tinted. It moves
        // colour between channels rather than adding or removing any.
        float tint = shaded * VV_DAPPLE_GREEN;
        result *= vec3(1.0 - tint, 1.0 + tint * 0.6, 1.0 - tint * 0.7);
    }

    // Ambient is deliberately NOT multiplied by the shadow map: sky light
    // reaches surfaces the sun does not, and killing it in shadow is what makes
    // metal in a doorway look like painted wood. Daylight and fog still apply.
    // Torches, lava and glowing blocks. Fogged like everything else, but not
    // shadowed and not scaled by daylight - see vvBlockLightSpecular.
    result += vvBlockLightSpecular(f0, roughness, n, v, blockLightColor, cameraRelativePos)
            * clamp(1.0 - fog - murkiness, 0.0, 1.0)
            * vv_pbrSpecularStrength
            * specularOcclusion;

    result += vvAmbientSpecular(f0, roughness, ndotv, environment)
            * energy
            * clamp(vv_sceneDayLight, 0.0, 1.0)
            * clamp(1.0 - fog - murkiness, 0.0, 1.0)
            * vv_pbrSpecularStrength
            * mix(1.0, VV_OVERCAST_AMBIENT, overcast)
            * specularOcclusion;

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
    // Emission, restricted to WHERE the texture actually emits.
    //
    // The hierarchy matters and is deliberately one-directional. Vanilla's
    // glowLevel decides whether this block emits at all and how strongly; the
    // mask only redistributes that emission across the texture. A forge should
    // not glow evenly from its stonework and its coals, and until now it did -
    // glowLevel is per-block, so the whole texture lit uniformly.
    //
    // Multiplicative, so it can only ever take emission away from parts of a
    // texture that were already emitting. It cannot grant any: on a block the
    // game does not light, glowLevel is 0 and vvEmission returns black before
    // the mask is even consulted. Without the second atlas the mask reads 0
    // from the neutral material, which would remove all emission - so the
    // fallback is an explicit 1, meaning "emits everywhere", which is exactly
    // the behaviour that shipped before this channel existed.
    float emissionMask = vvEmissionMask(materialUv);

    result += vvEmission(albedo, glowLevel, cameraRelativePos)
            * clamp(1.0 - fog - murkiness, 0.0, 1.0)
            * emissionMask;

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

    // 15: the sunfleck field alone, normalised. White is a fleck - light the
    // canopy did not take - and dark is the shade between. It should be flat
    // WHITE on open ground, flat white in a cellar and flat white on the leaves
    // themselves: all three are outside the gate. Walk the edge of a wood and
    // the flecks should switch on as the canopy closes over.
    if (mode == 15)
    {
        float d = vvCanopyDapple(cameraRelativePos, vvDetailFade(cameraRelativePos));
        return vec4(vec3(1.0 - d / max(0.001, VV_DAPPLE_SHADE)), color.a);
    }

    // 16: vv_sunExposure raw, which is the one number the dapple gate is built
    // on and the one number that has never been measured in game.
    //
    // It is vanilla's per-vertex sun light level, and the gate assumes it reads
    // 1 under open sky and clearly less under a canopy. If leaves in this
    // version absorb only a little, "clearly less" might be 0.9, and a gate
    // tuned for 0.75 would barely fire; if open ground does not quite reach 1,
    // the gate leaks onto every field in the world. Both are one screenshot
    // away from being settled and neither is worth another round of guessing.
    //
    // White is full sun, black is none. Stand in the open, then under a tree,
    // and read off the two values.
    if (mode == 16) return vec4(vec3(clamp(vv_sunExposure, 0.0, 1.0)), color.a);

    // 17: the god-ray source mask this writes into outGlow.g. Bright where a
    // beam starts - backlit leaves and lit flecks, and only while the camera is
    // looking toward the sun. Black everywhere if the player has god-rays
    // switched off in the game's own graphics settings, which is where the
    // effect actually lives.
    if (mode == 17) return vec4(vec3(vvCanopyShaft(cameraRelativePos)), color.a);

    // 18: what the grooves take from the REFLECTION, against what they take
    // from the ambient. Red is the flat cavity, green is the specular
    // occlusion actually applied to the highlight. On a polished surface green
    // should sit well above red; on a rough one the two converge. Equal
    // everywhere means the feature is switched off, which is what 0 does.
    if (mode == 18)
    {
        float cav = mix(1.0, vvCavity(materialUv, vvDetailFade(cameraRelativePos)),
                        vvSceneVisibilityDampen());
        vec4 mat = vvSampleMaterial(materialUv);
        float rough = clamp(mat.b + vv_pbrRoughnessBias, 0.04, 1.0);
        vec3 nn = vvSurfaceNormal(faceNormal, materialUv, cameraRelativePos);
        float nv = max(dot(nn, normalize(-cameraRelativePos)), 1e-4);
        return vec4(cav, vvSpecularOcclusion(cav, nv, rough), 0.0, color.a);
    }

    // ---------------------------------------------------------------------
    // The second material atlas, one channel per view.
    //
    // All four read BLACK when the page is unavailable, because
    // vvSampleMaterial2 returns the neutral material - which is exactly what
    // the lighting sees in that case. A view that showed something plausible
    // while the lighting saw nothing would defeat the point of having it.
    // ---------------------------------------------------------------------

    // 19: metalness. Iron, steel, copper, gold and anvils should read white;
    // stone, wood, soil and leaves black. Anything dark reading as metal means
    // the classification is coming from pixels rather than from
    // EnumBlockMaterial, which it must never be.
    if (mode == 19) return vec4(vec3(vvSampleMaterial2(materialUv).metalness), color.a);

    // 20: height, mid grey where flat. Should look like the texture's own
    // relief - mortar recessed, cobbles raised - and must line up exactly with
    // the normal in view 1, since both derive from the same luminance.
    if (mode == 20) return vec4(vec3(vvSampleMaterial2(materialUv).height), color.a);

    // 21: baked AO. White is open, dark is recessed. Broader and smoother than
    // view 12's crevice term, which is the point of having both - this is the
    // shape of the surface, that is the grain of it.
    if (mode == 21) return vec4(vec3(vvSampleMaterial2(materialUv).occlusion), color.a);

    // 22: the emission mask. BLACK on every block the game does not light, no
    // matter how bright its texture - that is the check that vanilla's
    // glowLevel is the only thing granting emission. On a forge or a lantern
    // the hot part should read and the casing should not.
    if (mode == 22) return vec4(vec3(vvSampleMaterial2(materialUv).emission), color.a);

    // 23: what metalness does to reflectance. The F0 the shader actually uses -
    // grey means dielectric, a coloured surface means the highlight will be
    // tinted by the albedo. Copper should read orange here and steel white.
    if (mode == 23)
    {
        vec4 mat = vvSampleMaterial(materialUv);
        VvMaterial2 m2 = vvSampleMaterial2(materialUv);
        float metal = clamp(m2.metalness, 0.0, 1.0) * clamp(vv_pbrMetalResponse, 0.0, 1.0);

        return vec4(vv_material2Valid > 0.5
            ? vvReflectanceF0FromMetalness(color.rgb, metal)
            : vvReflectanceF0(color.rgb, mat.a), color.a);
    }

    // 24: the grain the anisotropic highlight is following. Red and green are
    // the direction, blue is coherence - how confident the measurement is.
    //
    // Planks and log sides should show a strong, steady direction along the
    // grain, and a log's end should show it curving with the rings. Stone,
    // soil and sand should be nearly black in blue: their texture is noise, the
    // two eigenvalues are equal, and the surface stays isotropic without
    // anything having to know it is stone.
    if (mode == 24)
    {
        vec3 grain = vvGrainDirection(materialUv);
        return vec4(grain.xy * 0.5 + 0.5, grain.z, color.a);
    }

    // ---- Canopy audit instrument, views 25-28 ----------------------------
    //
    // These four exist to answer one question that cannot be answered by
    // reading code: is vanilla's shadow map fine enough to resolve the gaps in
    // a canopy? If it is, the dapple system should be modulating it rather than
    // replacing it, and most of the procedural machinery below becomes
    // sub-texel detail instead of the whole effect.
    //
    // Read them in this order, standing on a forest floor at mid-morning:
    //
    //   25 next to 16 - what the game knows about the sun, against what the
    //                   mod has been using as a stand-in for it.
    //   26            - whether leaf shadows are resolved or averaged away.
    //   27            - whether 26 is telling apart leaves from walls.
    //
    // 25: vanilla's OWN sun visibility, raw. White where the sun reaches,
    // black where geometry blocks it, grey in the PCF penumbra.
    //
    // This is the comparison that decides the architecture. Compare it with
    // view 16 in the same spot: 16 is vv_sunExposure, a per-vertex flood-filled
    // sky light value that knows nothing about where the sun is and is
    // identical at noon and midnight. 25 is a real directional occlusion test
    // and moves as the sun moves. If 25 shows leaf-shaped shadows under a tree,
    // the game already resolves the canopy and the mod should stop inventing
    // one.
    //
    // Unlike the shadowBrightness the rest of this file passes around, this is
    // not raised by torchlight - see vvSunVisibility.
    if (mode == 25) return vec4(vec3(vvSunVisibility()), color.a);

    // 26: the same thing at three neighbourhood radii, as a false-colour ramp.
    // Red is a 2-texel ring, green 6, blue 16.
    //
    // What to look for: an open field and a cave interior are both BLACK, since
    // a uniform neighbourhood has nothing to disagree about. A forest floor
    // should light up, and WHICH channel lights up says at what scale the
    // shadow is broken - red means the shadow map is resolving individual leaf
    // gaps, blue-only means it is only catching whole-crown structure and the
    // fine detail has been filtered away.
    //
    // If red stays dark everywhere under trees, the shadow map cannot resolve
    // canopy gaps at this quality setting and the procedural flecks have a job
    // to do. That is the finding this view exists to produce.
    if (mode == 26)
    {
        return vec4(vvSunShadowBreakup(2.0),
                    vvSunShadowBreakup(6.0),
                    vvSunShadowBreakup(16.0), color.a);
    }

    // 27: the candidate canopy mask - broken shadow at a mid radius, shown
    // only where the fragment is actually in shadow.
    //
    // This is the proposed replacement for the vv_sunExposure gate, and this
    // view is how its failure modes get found before it is trusted with
    // anything. Walk it past all of: a tree, a wall, a cliff overhang, a
    // doorway, a cave mouth.
    //
    // Expected to be bright under foliage and dark under solid occluders. The
    // KNOWN leak is a bright band along any straight shadow edge, roughly as
    // wide as the sample ring, because one edge also makes taps disagree. How
    // wide that band actually is, and whether it reads as a defect in motion,
    // is a runtime question.
    if (mode == 27)
    {
        float visibility = vvSunVisibility();
        return vec4(vec3(vvSunShadowBreakup(6.0) * (1.0 - visibility)), color.a);
    }

    // 28: the two motion sources side by side. Red is vv_sceneBreeze, the mod's
    // own wind clock that currently drives the flecks; green is vanilla's sun
    // direction projected onto this surface.
    //
    // The point of this one is negative: the canopy in the shadow map is ALREADY
    // wind-animated by the game's own applyVertexWarping, at the game's phase
    // and coherently across each tree. If the shadow map resolves gaps at all,
    // the mod's separate breeze clock is a second, disagreeing animation of the
    // same leaves, and the correct fix is to delete it rather than tune it.
    // 29: the gate the dapple system now actually uses.
    //
    // Fine-radius shadow breakup where the sun is blocked - see
    // vvCanopyEvidence. This is the replacement for view 16, and comparing the
    // two is the whole argument: 16 is white across an entire forest scene and
    // says nothing, this should be bright under crowns and dark on open ground,
    // in caves, and under solid roofs.
    //
    // Its known defect is visible here too: a straight shadow edge leaves a
    // band about two texels wide. Walk a wall, a terrace lip and a doorway. If
    // those bands are wide or crawl as the camera moves, the fine radius is
    // still too coarse for this shadow resolution and the next move is to
    // require agreement across two radii rather than trusting one.
    if (mode == 29) return vec4(vec3(vvCanopyEvidence()), color.a);

    // 30: the structure count at three ring radii. Red 3 texels, green 5
    // (what the gate uses), blue 9.
    //
    // This exists because the ring radius is the one number in the canopy test
    // that is a guess. It is a guess about shadow map scale, not about the
    // world, and this view settles it: whichever channel FILLS the area under a
    // crown - rather than tracing its edges - is the right radius. If red fills
    // and green only outlines, the gaps are finer than assumed and the radius
    // should come down; if only blue fills, it should go up.
    //
    // A wall, a terrace and a roof should stay dark in ALL THREE, because a
    // single edge scores 2 in the total variation at every radius and the band
    // starts above that. That is the check that this is counting occluders
    // rather than finding boundaries.
    // 31: the RAW count at the gate's own radius, banded into false colour so a
    // screenshot answers the question without needing a number read off it.
    //
    //   black   variation below 1     nothing is crossing the ring at all
    //   red     1 to 3                ONE edge - a wall, a terrace, a roof
    //   green   3 to 5                two features
    //   blue    above 5               three or more, which is a canopy
    //
    // This separates the two failures that look identical in view 29. If the
    // ground under a crown is BLACK, the shadow there really is solid and there
    // is nothing to break up - move the radius, or the sun is too low and the
    // shadows have merged. If it is RED or GREEN, the structure is there and
    // the threshold band is what is wrong.
    if (mode == 31)
    {
        float mean;
        float v = vvCanopyVariation(clamp(vv_pbrCanopyRadius, 0.0, 16.0), mean);

        if (v < 1.0) return vec4(0.0, 0.0, 0.0, color.a);
        if (v < 3.0) return vec4(1.0, 0.0, 0.0, color.a);
        if (v < 5.0) return vec4(0.0, 1.0, 0.0, color.a);
        return vec4(0.0, 0.4, 1.0, color.a);
    }

    if (mode == 30)
    {
        float r = clamp(vv_pbrCanopyRadius, 0.0, 16.0);
        return vec4(vvCanopyStructure(max(1.0, r * 0.6)),
                    vvCanopyStructure(r),
                    vvCanopyStructure(min(16.0, r * 1.8)), color.a);
    }

    if (mode == 28)
    {
        return vec4(clamp(vv_sceneBreeze, 0.0, 1.0),
                    clamp(dot(faceNormal, normalize(lightPosition)) * 0.5 + 0.5, 0.0, 1.0),
                    0.0, color.a);
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
