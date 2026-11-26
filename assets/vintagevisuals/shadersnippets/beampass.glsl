// Vintage Visuals - canopy light shafts from the game's own shadow map (D92).
//
// Used ONLY by the fast beam pass (BeamPassRenderer), never injected into a
// vanilla program, so it may declare samplers freely: the pass is our own
// program and owns every texture unit it binds.
//
// WHY THE SHADOW MAP. The first two beam models (D56, D87) decided where a
// beam stood by projecting each point of air up the sun ray to a canopy plane
// and asking an invented fleck field. At a low sun that projection leaves the
// leaf map's 32-block reach for any air more than a few blocks below the
// crown, so only a thin layer just under the leaves passed every gate: the
// player saw smeared horizontal bands across the trunks rather than shafts.
// The shadow map already knows, for every point near the camera, whether the
// sun reaches it - through the real holes in the real leaves, at any sun
// height, behind any trunk. A shaft is simply lit air with shaded air beside
// it, so marching the view ray through the shadow map draws true shafts with
// no projection and no invented pattern (information ladder: authoritative
// game data over invented simulation).
//
// WHAT STAYS FROM BEFORE. Only air under a crown carries a shaft (the leaf
// map), so open country gets the game's fog and nothing else; the strength,
// forward scattering, humid-air lift, clear-sky and sun-presence gates and the
// visibility dampen are the terrain helpers, unchanged.

uniform sampler2DShadow vv_beamShadowFar;
uniform sampler2DShadow vv_beamShadowNear;
uniform mat4 vv_beamToShadowFar;
uniform mat4 vv_beamToShadowNear;
uniform vec2 vv_beamShadowValid; // x far cascade usable, y near cascade usable; 0 draws no shafts

const int VV_BEAM_STEPS = 20;          // samples along the view ray
const float VV_BEAM_REACH = 32.0;      // blocks of air marched; the leaf map's reach
const float VV_BEAM_SCATTER = 0.07;    // share of the sunlit colour per block of lit air seen
const float VV_BEAM_UNDER_LEAF = 0.5;  // air must be this far below the lowest leaf of its column

// 1 where the sun reaches this camera-relative point, 0 in shadow, -1 where
// neither cascade covers it. Same depth biases as vanilla's own lookup.
float vvBeamSunLit(vec3 rel)
{
    if (vv_beamShadowValid.y > 0.5)
    {
        vec4 n = vv_beamToShadowNear * vec4(rel, 1.0);
        if (n.x > 0.02 && n.x < 0.98 && n.y > 0.02 && n.y < 0.98 && n.z < 0.98)
            return texture(vv_beamShadowNear, vec3(n.xy, n.z - 0.0005));
    }
    if (vv_beamShadowValid.x > 0.5)
    {
        vec4 f = vv_beamToShadowFar * vec4(rel, 1.0);
        if (f.x > 0.02 && f.x < 0.98 && f.y > 0.02 && f.y < 0.98 && f.z < 0.98)
            return texture(vv_beamShadowFar, vec3(f.xy, f.z - 0.0009));
    }
    return -1.0;
}

// How much this point of air may carry a shaft: under a crown, below its
// lowest leaf. Soft at both edges so a crown's outline does not draw a line.
//
// The leaf map is 2-block cells; read hard, a cell edge drew a square step in
// the light. So the four nearest cell centres are blended (bilinear).
vec2 vvBeamCanopySmooth(vec2 xz)
{
    vec2 g = xz * 0.5 - 0.5;
    vec2 i = floor(g);
    vec2 f = g - i;
    vec2 a = vvTerrainCanopyCell((i + vec2(0.5, 0.5)) * 2.0);
    vec2 b = vvTerrainCanopyCell((i + vec2(1.5, 0.5)) * 2.0);
    vec2 c = vvTerrainCanopyCell((i + vec2(0.5, 1.5)) * 2.0);
    vec2 d = vvTerrainCanopyCell((i + vec2(1.5, 1.5)) * 2.0);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float vvBeamUnderCanopy(vec3 p)
{
    vec2 here = vvBeamCanopySmooth(p.xz);
    float crown = smoothstep(VV_TERRAIN_BEAM_CROWN * 0.5, VV_TERRAIN_BEAM_CROWN + 0.15, here.x);
    float below = smoothstep(0.0, 1.5, here.y - VV_BEAM_UNDER_LEAF - p.y);
    return crown * below;
}

// Sunlight scattered toward the camera by the lit air between the eye and
// this camera-relative position, as a share of the sunlit colour, 0 to
// VV_TERRAIN_BEAM_MAX.
float vvBeamShadowInscatter(vec3 cameraRelativePos, float fogDensity)
{
    if (vv_pbrShafts < 0.001 || vv_canopyMapValid < 0.5) return 0.0;
    if (vv_beamShadowValid.x < 0.5 && vv_beamShadowValid.y < 0.5) return 0.0;
    float sun = vvTerrainSunPresence();
    if (sun < 0.01) return 0.0;
    float clearSky = vvTerrainClearSky();
    if (clearSky < 0.004) return 0.0;
    vec3 l = vvTerrainSafeNormalize(lightPosition, vec3(0.0, 1.0, 0.0));
    if (l.y < 0.02) return 0.0;

    vec3 fromEye = -vvTerrainToEye(cameraRelativePos);
    float dist = length(fromEye);
    if (dist < 0.5) return 0.0;
    vec3 dir = fromEye / dist;
    float span = min(dist, VV_BEAM_REACH);
    float ds = span / float(VV_BEAM_STEPS);

    // A fixed per-pixel offset of the samples, averaged by the composite's
    // depth-aware filter, so the steps read as a smooth integral.
    float offset = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
    float lit = 0.0;
    float seen = 0.0;   // gated air marched, lit or not
    for (int i = 0; i < VV_BEAM_STEPS; i++)
    {
        vec3 rel = vv_pbrEye + dir * ((float(i) + offset) * ds);
        float gate = vvBeamUnderCanopy(rel + vv_pbrOrigin);
        if (gate <= 0.0) continue;
        float s = vvBeamSunLit(rel);
        if (s < 0.0) continue;   // no cascade covers it: neither lit nor shaded
        seen += gate;
        lit += s * gate;
    }
    if (lit <= 0.0) return 0.0;

    // A shaft is CONTRAST: lit air with shade around it. Air that is lit
    // all along the ray (noon under a broken canopy) is not a shaft, it is a
    // veil, and adding it lifted every shadowed patch of ground to the same
    // grey (2026-11-26, "shadows look disabled"). So the lit share is weighed
    // by the shade the ray also crossed: 1 - f^2 keeps nearly all of a thin
    // shaft seen through deep shade (looking into one), three quarters of a
    // ray half lit, and nothing of a ray lit throughout. The composite then
    // removes what is left of a veil across the screen (D94).
    float f = lit / max(seen, 1e-4);
    float contrast = 1.0 - f * f;

    float cosView = dot(dir, l);
    float phase = 0.45 + 0.55 * pow(max(cosView, 0.0), 3.0);
    float lowSun = 1.0 - smoothstep(0.15, 0.55, l.y);
    float fog = clamp((fogDensity - 0.00125) / 0.01, 0.0, 1.0);
    float humid = 0.6 + 0.4 * clamp(max(fog, max(clamp(vv_sceneWetness, 0.0, 1.0), lowSun)), 0.0, 1.0);

    // Shafts are strongest at a low sun, but one seen through deep shade
    // still reads at noon; the composite's veil removal keeps that safe.
    float sunAngle = 0.4 + 0.6 * lowSun;

    float level = lit * contrast * sunAngle * ds * VV_BEAM_SCATTER * phase * humid * clearSky * sun
                * clamp(vv_pbrShafts, 0.0, 2.0) / 0.45 * vvSceneVisibilityDampen();
    // A soft shoulder instead of a clip: a long run of lit air approaches
    // the cap without flattening into one milky level.
    return VV_TERRAIN_BEAM_MAX * (1.0 - exp(-max(level, 0.0) / VV_TERRAIN_BEAM_MAX));
}
