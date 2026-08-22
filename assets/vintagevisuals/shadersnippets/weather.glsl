// Vintage Visuals - weather: cloud shadows
//
// Injected into chunkopaque.fsh and chunktopsoil.fsh at vanilla's
// getBrightnessFromShadowMap, which this group wraps: a cloud occludes the sun,
// so it belongs wherever the sun's occlusion is already decided.
//
// Self-contained on purpose. It shares no uniform and no function with
// pseudopbr.glsl even though both land in the same file, because the two are
// separate patch groups and either must be able to roll back without taking
// the other's declarations with it.

// NOTE: this is applied to TERRAIN ONLY, never to the sky.
//
// An earlier version patched sky.fsh with this group's fog, on the reasoning
// that rain
// should thicken the whole scene. It does not work: the sky dome is not
// something you look THROUGH, it is the thing at the far end, and fogging it
// flattens the contrast between cloud and sky into a uniform haze. Clouds
// stopped reading as clouds and became a blanket with the layer's tile seams
// showing through in perspective - with both the classic and the volumetric
// renderer, because neither was the problem. Vanilla already has horizonFog for
// the sky's own weather response.
//
// That fog has since moved out of this group entirely - see atmosphere.yaml -
// but the conclusion travelled with it and still holds: nothing this mod writes
// goes on the sky.

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
// A uniform array rather than a texture. It is 144 vec4s - 576 floats, inside
// the 1024 fragment uniform components OpenGL 3.3 guarantees, with vanilla's own
// use of chunkopaque.fsh leaving room - and adding a second
// sampler to chunkopaque.fsh is the change that has twice cost this project the
// entire world render - not a trade worth making for 256 numbers.
//
// vv_cloudMapValid is 0 when the tile array could not be read, which is also
// what an unpatched or unbound program reads, so the fallback below is what
// happens by default rather than by accident.
#define VV_CLOUD_TILES 24
#define VV_CLOUD_TILE_SIZE 50.0

uniform vec4  vv_cloudTiles[VV_CLOUD_TILES * VV_CLOUD_TILES / 4];
uniform float vv_cloudMapValid;      // 1 when the game's own cloud tiles were read

// Whether to draw the mod's own noise field when the game's tiles could not be
// read. 0 draws NOTHING instead, and 0 is both the default and what an unset
// uniform reads as.
//
// This used to be implicit and it was the most expensive decision in the
// subsystem. A failed read fell through to an invented field that moves
// plausibly and covers plausibly and has no relation whatever to the sky - so
// the failure looked exactly like a working effect that needed tuning, and four
// rounds of debugging went into tuning it. An effect that cannot do its job
// should be absent and say so, not approximate its way into looking like a bug
// in something else.
//
// It is still reachable, as a deliberate choice: turning "clouds from game" off
// asks for the noise field on purpose, for a version where the renderer has
// moved out of reach and stylised shadows beat none.
uniform float vv_cloudFallback;

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
// The true projection runs to infinity as the sun reaches the horizon, so some
// cap is needed or every shadow walks out of the window at exactly the hour
// they would be longest. 500 sits just inside the usable radius: the window is
// 24 tiles across, so 600 blocks from the camera to the edge, less the two-tile
// fade band.
//
// It used to be 320 against a 16-tile window, and it was applied to the wrong
// quantity - see vvCloudCoverage. Between the two, a morning sun at 20 degrees
// threw its shadows 300 blocks when the geometry called for 400, which reads as
// shadows sitting too close under their clouds all morning and all evening.
#define VV_CLOUD_MAX_THROW 500.0

// Tiles of fade at the window edge. Without it the window boundary is a hard
// line across the world that moves with the player.
#define VV_CLOUD_EDGE_FADE 2.0

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
// The window is only 1200 blocks across and follows the player, so a hard
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

    // The horizontal run of the ray from this fragment up to the cloud deck.
    //
    // Draw a line from the sun through a cloud: where it meets the ground is
    // the shadow. Read backwards from the ground, that is climb / tan(elevation)
    // blocks along the sun's azimuth, and the whole of a cloud shadow's
    // behaviour through the day is in that tangent - short and almost under the
    // cloud at noon, lengthening quickly as the sun drops.
    //
    // Written as an explicit run and direction, which matters for the cap. The
    // previous form multiplied the unnormalised toSun.xz by min(climb/sin, cap),
    // so the magnitude came out right but the CAP was being applied to
    // climb/sin(e) - a different and always larger quantity than the
    // climb/tan(e) actually being capped. The effective limit therefore varied
    // with the sun's height, biting hardest exactly when the shadows should
    // have been longest.
    vec2 azimuth = toSun.xz;
    float azimuthLength = length(azimuth);

    vec2 thrown = vec2(0.0);

    if (azimuthLength > 0.0001)
    {
        // Floored so a sun exactly overhead - or exactly at the horizon - does
        // not divide by zero. At 0.05 the run is at most twenty times the
        // climb, and the cap takes over long before that.
        float up = max(0.05, abs(toSun.y));
        float run = climb * azimuthLength / up;

        thrown = (azimuth / azimuthLength) * min(run, VV_CLOUD_MAX_THROW);
    }

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

    // Nothing rather than something plausible. See vv_cloudFallback.
    if (vv_cloudFallback < 0.5) return 0.0;

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
    // No tiles means no data, and a diagnostic that draws NOTHING when there is
    // nothing is indistinguishable from a diagnostic that is not running. That
    // is not a hypothetical: views 1 and 2 both came back blank in exactly the
    // case they exist for, and the only conclusion available from the screen was
    // "the debug options do nothing".
    //
    // So say it. Broad diagonal bars, unmistakably artificial, drawn by every
    // view whenever the game's cloud tiles could not be read. Bars mean the
    // shader is live and the DATA is missing - which sends the reader to the
    // log rather than to the sliders.
    if (vv_cloudMapValid < 0.5)
    {
        float bar = fract((cameraRelativePos.x + cameraRelativePos.z) / 24.0);
        return bar < 0.5 ? 0.35 : 1.0;
    }

    if (mode == 1) return 1.0 - vvCloudCoverage(cameraRelativePos);

    // Straight down. No throw, so this is honest about placement even when the
    // deck height is a guess - which it is, because the game does not expose
    // the altitude it draws its clouds at.
    if (mode == 2) return 1.0 - vvCloudMapRaw(cameraRelativePos.xz);

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

// The anchor line, pasted back RENAMED.
//
// The patch replaces vanilla's getBrightnessFromShadowMap signature with this
// whole file, and this line puts the original function back under the name the
// wrapper above calls. Dropping it would delete vanilla's shadow lookup and
// leave the wrapper calling something that no longer exists.
//
// This used to be applyFog, five lines above. Fog moved to its own group when it
// turned out to need every shading program rather than the two this one
// patches - see atmosphere.yaml. Injecting here instead means the rename and the
// injection are one patch, which is why there is no separate rename entry in
// weather.yaml any more.
float vvVanillaShadowMap() {
