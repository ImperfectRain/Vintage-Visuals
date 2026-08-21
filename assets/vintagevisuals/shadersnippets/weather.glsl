// Vintage Visuals - weather: fog and cloud shadows
//
// Injected into chunkopaque.fsh and chunktopsoil.fsh immediately before
// vanilla's applyFog, which is the last point before anything reads either of
// the two things this changes.
//
// Self-contained on purpose. It shares no uniform and no function with
// pseudopbr.glsl even though both land in the same file, because the two are
// separate patch groups and either must be able to roll back without taking
// the other's declarations with it.

// NOTE: this is applied to TERRAIN ONLY, never to the sky.
//
// An earlier version patched sky.fsh the same way, on the reasoning that rain
// should thicken the whole scene. It does not work: the sky dome is not
// something you look THROUGH, it is the thing at the far end, and fogging it
// flattens the contrast between cloud and sky into a uniform haze. Clouds
// stopped reading as clouds and became a blanket with the layer's tile seams
// showing through in perspective - with both the classic and the volumetric
// renderer, because neither was the problem. Vanilla already has horizonFog for
// the sky's own weather response.

uniform float vv_weatherRain;        // 0..1 precipitation intensity, smoothed
uniform float vv_weatherFogStrength; // how much rain thickens the air
uniform float vv_weatherFogTint;     // how much rain drains colour from it

uniform float vv_cloudShadowStrength; // already scaled by daylight on the CPU
uniform float vv_cloudCover;         // 0 clear, 1 overcast - the game's own figure
uniform float vv_cloudScale;         // blocks across one cloud cell
uniform float vv_cloudHeight;        // world height the shadow-casting deck sits at
uniform vec2  vv_cloudDrift;         // advanced on the CPU along the real wind
uniform vec3  vv_cloudOrigin;        // camera world position, wrapped on the CPU
// Which diagnostic to draw instead of shading, 0 for none. See vvCloudDebug.
uniform float vv_cloudDebug;

// The game's OWN cloud placement, read off the cloud renderer's tile array on
// the CPU and handed over as a window centred on the player.
//
// A uniform array rather than a texture. It is 64 vec4s, and adding a second
// sampler to chunkopaque.fsh is the change that has twice cost this project the
// entire world render - not a trade worth making for 256 numbers.
//
// vv_cloudMapValid is 0 when the tile array could not be read, which is also
// what an unpatched or unbound program reads, so the fallback below is what
// happens by default rather than by accident.
#define VV_CLOUD_TILES 16
#define VV_CLOUD_TILE_SIZE 50.0

uniform vec4  vv_cloudTiles[VV_CLOUD_TILES * VV_CLOUD_TILES / 4];
uniform float vv_cloudMapValid;      // 0 falls back to the noise field

// CAMERA-RELATIVE XZ of the corner of tile [0,0], not world XZ.
//
// This is the fix for the defect that made the tile path contribute nothing at
// all. The corner used to be handed over in true world coordinates and then
// compared against a position built from vv_cloudOrigin - which is the camera
// position WRAPPED to 4096 blocks, because a float32 cannot hold a Vintage
// Story world coordinate finely enough to be useful. Subtracting an unwrapped
// corner from a wrapped position leaves a residue of whatever multiple of 4096
// the player happened to be past, so the lookup landed thousands of tiles
// outside a sixteen-tile window and every fragment read clear sky. At spawn the
// residue is zero and it works, which is presumably how it survived.
//
// Camera-relative removes the question rather than answering it: both sides are
// small numbers near the camera, there is no wrap to agree about, and float32
// has precision to spare. Renamed from vv_cloudMapOrigin so that a binder still
// uploading the old meaning fails the wiring check instead of silently
// disagreeing about what the number means.
uniform vec2  vv_cloudMapCorner;

// How far a shadow may be thrown sideways from the cloud casting it, in blocks.
//
// The true projection runs to infinity as the sun reaches the horizon, and the
// window is 800 blocks across, so an uncapped throw walks straight out of the
// data and every shadow vanishes at exactly the hour they would be longest.
// Capped and then faded at the window edge instead: shadows shorten rather than
// disappear, and vv_cloudShadowStrength is already scaled by daylight, so the
// hours where the cap bites are the hours the effect is fading out anyway.
#define VV_CLOUD_MAX_THROW 320.0

// Tiles of fade at the window edge. Without it the window boundary is a hard
// line across the world that moves with the player.
#define VV_CLOUD_EDGE_FADE 2.0

// ---------------------------------------------------------------------------
// Fog
// ---------------------------------------------------------------------------

float vvWeatherFogAmount(float fogWeight)
{
    float extra = clamp(vv_weatherRain * vv_weatherFogStrength, 0.0, 1.0);

    // Returns the input untouched when it is not raining, rather than a clamped
    // copy of it. Callers do pass values outside 0..1 and vanilla's mix handles
    // that; quietly clamping them is a behaviour change dressed up as a no-op,
    // and "identity" has to mean identity.
    if (extra <= 0.0) return fogWeight;

    // Added as a fraction of what is LEFT rather than as a sum, so heavy rain
    // approaches full fog without ever exceeding it. A plain addition makes
    // distant terrain pop to solid grey the moment a shower starts.
    return clamp(fogWeight + (1.0 - fogWeight) * extra, 0.0, 1.0);
}

vec3 vvWeatherFogColor(vec3 fogColor)
{
    float blend = clamp(vv_weatherRain * vv_weatherFogTint, 0.0, 1.0);
    if (blend <= 0.0) return fogColor;

    // Shifts vanilla's fog colour rather than replacing it. That colour already
    // tracks time of day, biome and altitude, and a fixed rain grey would fight
    // every sunset it was drawn over.
    float luma = dot(fogColor, vec3(0.2126, 0.7152, 0.0722));
    vec3 overcast = mix(vec3(luma), vec3(luma) * vec3(0.94, 0.97, 1.06), 0.6);

    return mix(fogColor, overcast, blend);
}

// ---------------------------------------------------------------------------
// Cloud shadows
// ---------------------------------------------------------------------------

// Three octaves of vanilla's own gradient noise. Reusing gnoise rather than
// shipping a noise function matters here: it is already compiled into both of
// these shaders, so this costs instructions rather than a texture fetch or a
// second implementation to keep in step.
//
// This is the mod's own field, NOT the tile map vanilla places its clouds from.
// That map is built on the CPU and handed to the renderers as mapData1 and
// mapData2 (see cloudmap.fsh), so it is not reachable from a terrain shader,
// and both cloud renderers read the clouds' positions out of it. What can be
// taken from the game without it is everything except position - how much of
// the sky is covered, which way it is going, and where the sun is - and all
// three are.
float vvCloudField(vec2 p)
{
    float n = gnoise(p) * 0.6 + gnoise(p * 2.3) * 0.3 + gnoise(p * 5.1) * 0.1;

    // Normalised by the deviation this sum actually HAS, not by the range it
    // could theoretically reach. That distinction is why the first version
    // showed nothing.
    //
    // Two-dimensional gnoise peaks near +-0.7 but spends nearly all its time
    // inside +-0.2, and a weighted sum of three octaves is narrower still: call
    // it a standard deviation of 0.14 around zero. Mapping that raw onto 0..1
    // and then thresholding puts every threshold worth using outside the band
    // the field ever visits, so the ground comes out either untouched or
    // uniformly dark depending on which side of the pile the threshold landed.
    // Both failures have now shipped, one after the other.
    return clamp(n * 1.55 + 0.5, 0.0, 1.0);
}

float vvCloudDensity(vec2 worldXZ, vec3 toSun)
{
    vec2 p = worldXZ / max(32.0, vv_cloudScale) - vv_cloudDrift;

    // Stretch the field along the sun's azimuth as the sun drops.
    //
    // A cloud's shadow is its cross-section projected along the ray. With the
    // sun overhead that is the cloud's own footprint; with the sun low it is
    // the same footprint smeared out along the direction the light comes from,
    // which is why late-afternoon cloud shadows are long streaks rather than
    // blobs. Without this the shadows keep their noon shape all day and merely
    // slide sideways, which reads as a texture scrolling under the world.
    vec2 azimuth = toSun.xz;
    float azimuthLength = length(azimuth);
    if (azimuthLength > 0.001)
    {
        azimuth /= azimuthLength;

        // Capped at 3. The true projection goes to infinity at the horizon,
        // and anything past about three times reads as smearing rather than as
        // a long shadow.
        float stretch = clamp(1.0 / max(0.2, abs(toSun.y)), 1.0, 3.0);

        float along = dot(p, azimuth);
        p = (p - azimuth * along) + azimuth * (along / stretch);
    }

    float n = vvCloudField(p);

    // Cover chooses how much of the ground ends up shaded, by moving the
    // threshold through the part of the range the field actually occupies.
    //
    // Both ends stop short of the extremes on purpose. Overcast in vanilla is
    // still a cloud LAYER with thin patches in it, and a clear sky still has
    // the odd cloud in it - a day with no shadows at all anywhere is rarer
    // than either.
    float threshold = mix(0.80, 0.30, clamp(vv_cloudCover, 0.0, 1.0));

    // One-sided and narrow: wide enough not to alias into shimmer at draw
    // distance, narrow enough that a shadow has an edge you can watch cross a
    // field.
    return smoothstep(threshold, threshold + 0.14, n);
}

// Prototype. The vanilla function is renamed to this by the same patch that
// injects this file, and its definition sits below - GLSL needs the
// declaration first.
float vvVanillaShadowMap();

// One tile of the game's cloud map, or clear outside the window.
//
// Outside rather than clamped: a clamped edge smears the last row of tiles to
// the horizon, which reads as a shadow that follows the player.
float vvCloudTile(ivec2 t)
{
    if (t.x < 0 || t.y < 0 || t.x >= VV_CLOUD_TILES || t.y >= VV_CLOUD_TILES) return 0.0;

    int i = t.y * VV_CLOUD_TILES + t.x;
    return vv_cloudTiles[i >> 2][i & 3];
}

// The game's cloud cover at a world position, smoothly interpolated.
//
// Vanilla's clouds are drawn from 50-block tiles and look like it, which is
// part of the art direction. Their SHADOWS looking like it is not: a hard tile
// edge on the ground reads as a bug rather than as a cloud, so the lookup is
// bilinear with a smoothstep weight. The shadow stays where the cloud is and
// stops being square.
float vvCloudMapRaw(vec2 cameraRelativeXZ)
{
    vec2 p = (cameraRelativeXZ - vv_cloudMapCorner) / VV_CLOUD_TILE_SIZE;

    ivec2 t = ivec2(floor(p));
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);

    float a = mix(vvCloudTile(t),                 vvCloudTile(t + ivec2(1, 0)), f.x);
    float b = mix(vvCloudTile(t + ivec2(0, 1)),   vvCloudTile(t + ivec2(1, 1)), f.x);

    return clamp(mix(a, b, f.y), 0.0, 1.0);
}

// The same lookup, faded toward the window edge.
//
// The window is only 800 blocks across and follows the player, so a hard
// boundary is a line of shadow that ends in mid-field and slides along with
// them - which reads as a rendering fault, not as weather. Split from the raw
// lookup so the calibration view can show placement without the fade confusing
// what it is looking at.
float vvCloudMap(vec2 cameraRelativeXZ)
{
    vec2 p = (cameraRelativeXZ - vv_cloudMapCorner) / VV_CLOUD_TILE_SIZE;

    vec2 fromEdge = min(p, vec2(float(VV_CLOUD_TILES)) - p);
    float fade = clamp(min(fromEdge.x, fromEdge.y) / VV_CLOUD_EDGE_FADE, 0.0, 1.0);

    return vvCloudMapRaw(cameraRelativeXZ) * fade;
}

// The raw cloud field over this fragment, 0 in the clear to 1 fully under
// cloud.
//
// Split out from vvCloudShadow so it can be shown on its own. Cloud shadows
// have now failed to appear three times, and each round was spent guessing at
// which of five multiplied terms was zero. This function is deliberately
// independent of every one of them.
float vvCloudCoverage(vec3 cameraRelativePos)
{
    // Walk from the fragment up to the cloud deck along the light direction, so
    // the shadow lands where the cloud actually blocks the light rather than
    // straight above. At a low sun this is a long way sideways, which is
    // exactly what makes cloud shadows read as three-dimensional.
    //
    // lightPosition is vanilla's own light direction - the same vector the
    // terrain shader lights every face with, which is why this needs no notion
    // of its own about where the sun is and why it follows the moon at night
    // without being told. Directionality comes from using the game's answer.
    vec3 toSun = normalize(lightPosition);

    // Y is the one component of vv_cloudOrigin that is NOT wrapped, precisely
    // so that a height can still be compared against the cloud deck.
    float worldY = cameraRelativePos.y + vv_cloudOrigin.y;

    // Floored rather than used raw: an unset uniform reads as 0, and a deck at
    // world height zero is below the ground everywhere, which would collapse
    // the offset to nothing and cast every shadow straight down.
    float deck = max(32.0, vv_cloudHeight);
    float climb = max(0.0, deck - worldY);

    vec2 thrown = toSun.xz * min(climb / max(0.15, abs(toSun.y)), VV_CLOUD_MAX_THROW);

    // The game's own clouds when they can be read, and the mod's noise field
    // when they cannot. Only the first of these can actually line up with the
    // sky; the second exists so that a version which moves the cloud renderer
    // out of reach costs the registration rather than the whole effect.
    //
    // The two work in DIFFERENT SPACES and that is deliberate. The tile window
    // is anchored to the camera, so it is sampled camera-relative and never
    // touches a world coordinate. The noise field has to be anchored to the
    // world or it would slide along the ground with the player, so it takes the
    // wrapped world position - and being periodic already, a wrap it repeats on
    // costs it nothing.
    if (vv_cloudMapValid > 0.5) return vvCloudMap(cameraRelativePos.xz + thrown);

    return vvCloudDensity(cameraRelativePos.xz + vv_cloudOrigin.xz + thrown, toSun);
}

float vvCloudShadow(vec3 cameraRelativePos)
{
    if (vv_cloudShadowStrength < 0.001) return 1.0;

    return 1.0 - vvCloudCoverage(cameraRelativePos) * clamp(vv_cloudShadowStrength, 0.0, 1.0);
}

// ---------------------------------------------------------------------------
// Diagnostics
// ---------------------------------------------------------------------------
//
// Cloud shadows have now failed four times, and every round was spent guessing
// from a screenshot. These views exist to make the next round a measurement.
//
// The question that has never actually been answered is REGISTRATION: whether
// the tile window the CPU hands over describes the piece of sky it is assumed
// to describe. Every other unknown - deck height, throw distance, strength,
// vanilla's own shadow map - is downstream of that, and each one has been
// blamed in turn while the registration went unchecked.
//
//   1  the field the shadow actually uses, thrown along the light. What the
//      effect is doing, with strength and vanilla's shadow out of the way.
//
//   2  CALIBRATION. The tile field straight down, with NO throw toward the
//      light and NO edge fade. Whatever is directly overhead is drawn directly
//      underfoot, so the test is: stand still, look up, look down. If the
//      pattern on the ground matches the clouds in the sky, the window is
//      registered and any remaining error is in the throw. If it matches but is
//      shifted, the corner is wrong by that much. If it looks nothing like the
//      sky, the array is not what this assumes and no amount of tuning the
//      throw will help.
//
//   3  the window itself: the tile grid and its edge, so the sampling geometry
//      can be seen rather than inferred. Every 50-block tile boundary is a dark
//      line, and the outer two tiles are the fade band. The player stands at
//      the centre of the middle tile.
//
// Deliberately independent of vv_cloudShadowStrength, of daylight and of the
// vanilla shadow map, all three of which have been the zero at some point.
float vvCloudDebug(int mode, vec3 cameraRelativePos)
{
    if (mode == 1) return 1.0 - vvCloudCoverage(cameraRelativePos);

    // Straight down. No throw, so this is honest about placement even when the
    // deck height is a guess - which it is, because the game does not expose
    // the altitude it draws its clouds at.
    if (mode == 2)
    {
        if (vv_cloudMapValid < 0.5) return 1.0;
        return 1.0 - vvCloudMapRaw(cameraRelativePos.xz);
    }

    if (mode == 3)
    {
        vec2 p = (cameraRelativePos.xz - vv_cloudMapCorner) / VV_CLOUD_TILE_SIZE;

        if (p.x < 0.0 || p.y < 0.0 || p.x >= float(VV_CLOUD_TILES) || p.y >= float(VV_CLOUD_TILES))
        {
            return 1.0;
        }

        vec2 fromEdge = min(p, vec2(float(VV_CLOUD_TILES)) - p);
        float band = min(fromEdge.x, fromEdge.y) < VV_CLOUD_EDGE_FADE ? 0.55 : 1.0;

        vec2 within = abs(fract(p) - 0.5);
        float line = max(within.x, within.y) > 0.47 ? 0.25 : 1.0;

        return band * line;
    }

    return 1.0;
}

// Replaces vanilla's shadow lookup everywhere it is used.
//
// Wrapping this rather than editing each call site is the whole trick: a cloud
// occludes the sun, so it belongs wherever the sun's occlusion is already
// decided. Every caller - the terrain lighting, the normal shading, this mod's
// own specular - picks it up without any of them being patched, and without
// this group needing to touch a line another group has already rewritten.
float getBrightnessFromShadowMap()
{
    // Debug: the cloud field alone, at full strength, with vanilla's own
    // shadow map taken out of the way. Under cloud the ground goes black and
    // in the clear it stays lit, which is not subtle and is not meant to be.
    //
    // It answers the one question that matters when the effect is invisible:
    // is the GLSL running with sane uniforms and merely too faint, or is it not
    // running at all? Every multiplied term that could be silently zero -
    // strength, daylight, the vanilla shadow - is bypassed here, so if this
    // shows nothing the fault is upstream of the shader and the binder's own
    // log line says where.
    int debug = int(vv_cloudDebug + 0.5);
    if (debug > 0) return vvCloudDebug(debug, worldPos.xyz);

    return vvVanillaShadowMap() * vvCloudShadow(worldPos.xyz);
}

// The anchor line, pasted back. The patch REPLACES vanilla's applyFog signature
// with this whole file, so dropping this would delete the function every fog
// call in the shader goes through.
vec4 applyFog(vec4 rgbaPixel, float fogWeight) {
