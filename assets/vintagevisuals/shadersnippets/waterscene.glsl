// Vintage Visuals - water scene reflection and refraction
// (patch group "waterscene", chunkliquid.fsh, D80)
//
// The water pass runs in the transparent (OIT) framebuffer, which shares the
// opaque pass's depth but writes its own colour targets. So the opaque frame
// is complete and readable while the water draws: vanilla already reads its
// depth as depthTex, and WaterSceneBinder binds its colour as vv_waterScene.
// No copy, no extra pass, no render target, and no frame of lag.
//
// This group only DECLARES: the sampler (below vanilla's last sampler,
// liquidDepth, so no vanilla unit moves), the two matrices the vertex shader
// already receives, and the trace. water.glsl calls it under
// #ifdef VV_WATER_SCENE, so with this group off the water is exactly D79.
//
// ZERO IS THE SKY ONLY. vv_waterSceneOn is 1 only while the bind has a colour
// texture this frame; unset it reads 0, the trace misses, and the water keeps
// its analytic sky. Refraction needs it too.

uniform sampler2D vv_waterScene;    // the opaque frame's colour (Primary, attachment 0)
uniform mat4 projectionMatrix;      // vanilla's, set for the vertex shader
uniform mat4 modelViewMatrix;       // vanilla's: fWorldPos -> view
uniform float vv_waterSceneOn;      // 1 while vv_waterScene holds the frame
uniform float vv_waterSceneSteps;   // quality: march steps, 0 no trace
uniform float vv_waterSceneMix;     // scene reflection strength, 0 sky only
uniform float vv_waterRefraction;   // how far the bed wobbles through the waves, 0 none

#define VV_WATER_SCENE 1

const float VV_WSCENE_START = 0.2;   // blocks: first sample along the reflected ray
const float VV_WSCENE_RANGE = 48.0;  // blocks: last sample
const float VV_WSCENE_THICK = 0.5;   // blocks of tolerance behind a surface, plus the step
const float VV_WSCENE_EDGE = 0.06;   // share of the screen a hit fades over at its border
const float VV_WSCENE_BEND = 1.2;    // bed displacement per unit of wave slope, per block of path (max 2)

// Screen position (0..1) and view depth (blocks) of a point in fWorldPos space.
vec3 vvWaterSceneProject(vec3 p)
{
    vec4 c = projectionMatrix * modelViewMatrix * vec4(p, 1.0);
    float w = max(c.w, 1e-4);
    return vec3(c.xy / w * 0.5 + 0.5, c.w);
}

// The opaque depth at a screen position, in blocks.
float vvWaterSceneDepth(vec2 uv)
{
    return linearDepth(texture(depthTex, uv).x) * zFar;
}

// March a reflected ray across the opaque frame. Steps grow geometrically, so
// near detail is sampled finely and the far end coarsely; a crossing is
// refined by bisection. Returns the colour found and a confidence (0 = miss:
// off screen, into the sky, or passing behind something too thick to be the
// surface it crossed).
vec4 vvWaterSceneTrace(vec3 origin, vec3 dir)
{
    if (vv_waterSceneOn < 0.5 || vv_waterSceneSteps < 0.5 || vv_waterSceneMix < 0.001) return vec4(0.0);
    int steps = int(clamp(vv_waterSceneSteps, 1.0, 64.0) + 0.5);
    float grow = pow(VV_WSCENE_RANGE / VV_WSCENE_START, 1.0 / float(steps));
    float t = VV_WSCENE_START;
    float prevT = 0.0;
    for (int i = 0; i < 64; i++)
    {
        if (i >= steps) break;
        vec3 s = vvWaterSceneProject(origin + dir * t);
        if (s.z <= 0.05 || s.x < 0.0 || s.y < 0.0 || s.x > 1.0 || s.y > 1.0) return vec4(0.0);
        float behind = s.z - vvWaterSceneDepth(s.xy);
        if (behind > 0.0)
        {
            if (behind > VV_WSCENE_THICK + (t - prevT)) return vec4(0.0);
            float a = prevT, b = t;
            for (int j = 0; j < 5; j++)
            {
                float m = 0.5 * (a + b);
                vec3 sm = vvWaterSceneProject(origin + dir * m);
                if (sm.z - vvWaterSceneDepth(sm.xy) > 0.0) b = m; else a = m;
            }
            vec3 hit = vvWaterSceneProject(origin + dir * b);
            vec2 border = min(hit.xy, 1.0 - hit.xy);
            float edge = smoothstep(0.0, VV_WSCENE_EDGE, min(border.x, border.y));
            float reach = 1.0 - smoothstep(VV_WSCENE_RANGE * 0.6, VV_WSCENE_RANGE, b);
            return vec4(texture(vv_waterScene, hit.xy).rgb, edge * reach);
        }
        prevT = t;
        t *= grow;
    }
    return vec4(0.0);
}

// The bed seen through a texel, bent by its wave normal: the bed point moved
// along the slope, in proportion to the water in front of it (to 2 blocks).
// Returns the colour and whether the bent sample is really behind the water
// (a = 1); otherwise the straight view is kept (a = 0).
vec4 vvWaterSceneRefract(vec3 surface, vec3 bedPoint, vec3 n, float path)
{
    vec3 bent = bedPoint + vec3(n.x, 0.0, n.z) * (VV_WSCENE_BEND * min(path, 2.0));
    vec3 s = vvWaterSceneProject(bent);
    vec3 own = vvWaterSceneProject(surface);
    if (s.x < 0.0 || s.y < 0.0 || s.x > 1.0 || s.y > 1.0) return vec4(0.0);
    if (vvWaterSceneDepth(s.xy) <= own.z) return vec4(0.0);
    return vec4(texture(vv_waterScene, s.xy).rgb, 1.0);
}
