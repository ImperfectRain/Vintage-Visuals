// Vintage Visuals - color grading
//
// Injected near the top of final.fsh by shaderpatches/colorgrade.yaml. Defines
// the uniforms and the grading function; the actual call site is appended by
// the same patch file.
//
// Operation order is fixed and deliberate:
//   exposure -> white balance -> tonemap -> contrast -> saturation
// Exposure and white balance are scene-referred operations and belong before
// the tonemap, or the curve's shoulder rolls off highlights that exposure was
// meant to bring back. Contrast and saturation are look operations and belong
// after it, in display space, where their pivot is a meaningful 0.5.

uniform float vv_enabled;
uniform float vv_exposure;
uniform float vv_contrast;
uniform float vv_saturation;
uniform float vv_temperature;
uniform float vv_tonemapStrength;

// Eye adaptation, driven from the CPU. Multiplies vv_exposure rather than
// replacing it, so a manually dialled exposure survives.
uniform float vv_adaptation;

// Rec.709 luma weights, matching the primaries the game renders in.
const vec3 VV_LUMA_WEIGHTS = vec3(0.2126, 0.7152, 0.0722);

// Contrast pivot. 0.5 rather than the more commonly quoted 0.18 because this
// runs *after* the tonemap: 0.18 is mid-grey in a linear scene-referred signal,
// 0.5 is mid-grey in the display-referred signal the tonemap produces.
const float VV_CONTRAST_PIVOT = 0.5;

// ACES fitted approximation by Stephen Hill (@self_shadow), the widely used
// "ACESFitted" curve. Published as HLSL float3x3 literals, which are ROW-major;
// GLSL's mat3 constructor takes COLUMNS. The literals below are therefore the
// transpose of the published matrices, so that VV_ACES_INPUT * color performs
// the same multiplication as the original mul(ACESInputMat, color).
const mat3 VV_ACES_INPUT = mat3(
    0.59719, 0.07600, 0.02840,  // column 0
    0.35458, 0.90834, 0.13383,  // column 1
    0.04823, 0.01566, 0.83777   // column 2
);

const mat3 VV_ACES_OUTPUT = mat3(
     1.60475, -0.10208, -0.00327,  // column 0
    -0.53108,  1.10813, -0.07276,  // column 1
    -0.07367, -0.00605,  1.07602   // column 2
);

vec3 vvRRTAndODTFit(vec3 v)
{
    vec3 a = v * (v + 0.0245786) - 0.000090537;
    vec3 b = v * (0.983729 * v + 0.4329510) + 0.238081;
    return a / b;
}

vec3 vvACESFitted(vec3 color)
{
    color = VV_ACES_INPUT * color;
    color = vvRRTAndODTFit(color);
    color = VV_ACES_OUTPUT * color;
    return clamp(color, 0.0, 1.0);
}

// White balance by direct channel scaling rather than a Planckian-locus
// conversion. The real thing needs a chromatic adaptation matrix and a colour
// temperature in kelvin; this is a two-multiply approximation that is
// monotonic, cheap, and adequate for a look control the player nudges by eye.
// The green coefficient is small but non-zero so the shift does not read as a
// pure magenta/cyan swing.
vec3 vvWhiteBalance(vec3 color, float temperature)
{
    vec3 tint = vec3(
        1.0 + 0.20 * temperature,
        1.0 + 0.02 * temperature,
        1.0 - 0.20 * temperature
    );
    return color * tint;
}

vec4 vvApplyColorGrade(vec4 color)
{
    // vv_enabled is 0 both when the player disabled the subsystem and when the
    // uniforms were never uploaded at all (an unset GLSL uniform reads as 0).
    // Bailing out here means a failure to upload degrades to vanilla output
    // rather than to a black screen, which the player could not diagnose.
    if (vv_enabled < 0.5) return color;

    vec3 graded = max(color.rgb, vec3(0.0));

    // A uniform that was never uploaded reads as 0, which here would mean
    // multiplying the scene by zero - a black screen. Treat non-positive as
    // "no adaptation", the same defensive reasoning as vv_enabled above.
    float adaptation = vv_adaptation > 0.0 ? vv_adaptation : 1.0;

    graded *= vv_exposure * adaptation;
    graded = vvWhiteBalance(graded, vv_temperature);

    // Blend rather than branch: the tonemap can be dialled back to compare
    // against vanilla output without recompiling the shader.
    graded = mix(graded, vvACESFitted(graded), vv_tonemapStrength);

    graded = (graded - VV_CONTRAST_PIVOT) * vv_contrast + VV_CONTRAST_PIVOT;

    float luma = dot(max(graded, vec3(0.0)), VV_LUMA_WEIGHTS);
    graded = mix(vec3(luma), graded, vv_saturation);

    return vec4(clamp(graded, 0.0, 1.0), color.a);
}
