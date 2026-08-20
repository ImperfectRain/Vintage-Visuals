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

uniform float vv_cloudShadowStrength;
uniform float vv_cloudCover;         // 0 clear, 1 overcast
uniform float vv_cloudScale;         // blocks across one cloud cell
uniform float vv_cloudDrift;         // advanced on the CPU so clouds move
uniform vec3  vv_cloudOrigin;        // camera world position

// Height the shadow-casting cloud deck sits at, in blocks above the fragment.
// Vanilla's clouds are far higher than this; using their real altitude makes
// the shadow slide almost a kilometre sideways at a low sun, which reads as a
// bug rather than as weather.
const float VV_CLOUD_HEIGHT = 160.0;

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
float vvCloudDensity(vec2 worldXZ)
{
    vec2 p = worldXZ / max(8.0, vv_cloudScale) + vec2(vv_cloudDrift, vv_cloudDrift * 0.6);

    float n = gnoise(p) * 0.6 + gnoise(p * 2.3) * 0.3 + gnoise(p * 5.1) * 0.1;
    n = n * 0.5 + 0.5;

    // Cover moves the threshold rather than scaling the result, so a clear sky
    // has no shadows at all instead of faint ones everywhere, and an overcast
    // one goes fully shaded instead of uniformly grey.
    float threshold = 1.0 - clamp(vv_cloudCover, 0.0, 1.0);

    return smoothstep(threshold - 0.18, threshold + 0.18, n);
}

// Prototype. The vanilla function is renamed to this by the same patch that
// injects this file, and its definition sits below - GLSL needs the
// declaration first.
float vvVanillaShadowMap();

float vvCloudShadow(vec3 cameraRelativePos)
{
    if (vv_cloudShadowStrength < 0.001) return 1.0;

    vec3 world = cameraRelativePos + vv_cloudOrigin;

    // Walk from the fragment up to the cloud deck along the sun direction, so
    // the shadow lands where the cloud actually blocks the light rather than
    // straight above. At a low sun this is a long way sideways, which is
    // exactly what makes cloud shadows read as three-dimensional.
    vec3 toSun = normalize(lightPosition);
    float climb = max(0.0, VV_CLOUD_HEIGHT - world.y);
    vec2 at = world.xz + toSun.xz * (climb / max(0.15, abs(toSun.y)));

    return 1.0 - vvCloudDensity(at) * clamp(vv_cloudShadowStrength, 0.0, 1.0);
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
    return vvVanillaShadowMap() * vvCloudShadow(worldPos.xyz);
}

// The anchor line, pasted back. The patch REPLACES vanilla's applyFog signature
// with this whole file, so dropping this would delete the function every fog
// call in the shader goes through.
vec4 applyFog(vec4 rgbaPixel, float fogWeight) {
