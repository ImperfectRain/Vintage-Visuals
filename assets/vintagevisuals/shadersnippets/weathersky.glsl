// Vintage Visuals - weather fog, sky shader only
//
// The same fog treatment as weather.glsl, without the cloud shadows: the sky
// has no surface for a cloud to fall on, and sky.fsh has neither worldPos nor
// a shadow-map lookup to wrap.
//
// A separate file rather than a shared one because the two are injected into
// different programs and only overlap by coincidence. Sharing would mean sky.fsh
// carrying declarations for a feature it cannot have.

uniform float vv_weatherRain;
uniform float vv_weatherFogStrength;
uniform float vv_weatherFogTint;

float vvWeatherFogAmount(float fogWeight)
{
    float extra = clamp(vv_weatherRain * vv_weatherFogStrength, 0.0, 1.0);

    return clamp(fogWeight + (1.0 - fogWeight) * extra, 0.0, 1.0);
}

vec3 vvWeatherFogColor(vec3 fogColor)
{
    float luma = dot(fogColor, vec3(0.2126, 0.7152, 0.0722));
    vec3 overcast = mix(vec3(luma), vec3(luma) * vec3(0.94, 0.97, 1.06), 0.6);

    return mix(fogColor, overcast, clamp(vv_weatherRain * vv_weatherFogTint, 0.0, 1.0));
}

// The anchor line, pasted back. The patch REPLACES vanilla's applyFog signature
// with this whole file, so dropping this would delete the function every fog
// call in the shader goes through.
vec4 applyFog(vec4 rgbaPixel, float fogWeight) {
