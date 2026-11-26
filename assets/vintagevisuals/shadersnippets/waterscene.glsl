// Vintage Visuals - water scene reflection and refraction
// (patch group "waterscene", chunkliquid.fsh, D82)
//
// The water pass runs in the transparent (OIT) framebuffer, which shares the
// opaque pass's depth but writes its own colour targets. So the opaque frame
// is complete and readable while the water draws: vanilla already reads its
// depth as depthTex, and WaterSceneBinder binds its colour as vv_waterScene.
// No copy, no extra pass, no render target, and no frame of lag.
//
// This group only DECLARES: the sampler (below vanilla's last sampler,
// liquidDepth, so no vanilla unit moves), the two matrices the vertex shader
// already receives, the shared reflection core and the trace. water.glsl calls it under
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

// reflect.glsl (D81), the ONE description of a reflection, is injected right
// here by this group's own patch, so the march below is the shared one and
// the group still rolls back alone.
// vintagevisuals-hook: water reflect core

const float VV_WSCENE_START = 0.15;  // blocks: the ray leaves the surface this far before it may hit
const float VV_WSCENE_REACH = 384.0; // blocks: the longest reflected ray, past any view distance a hit is drawn at
const float VV_WSCENE_THICK = 1.5;   // blocks behind a surface a refined crossing may sit and still be that surface
const float VV_WSCENE_WAVE = 0.4;    // share of the wave tilt the traced ray follows (the sky takes all of it)
const float VV_WSCENE_BEND = 1.2;    // bed displacement per unit of wave slope, per block of path (max 2)

// Screen position (0..1) and view depth (blocks, axial: what the depth buffer
// holds) of a point in fWorldPos space, through vanilla's own matrices.
vec3 vvWaterSceneProject(vec3 p)
{
    vec4 c = projectionMatrix * modelViewMatrix * vec4(p, 1.0);
    float w = max(c.w, 1e-4);
    return vec3(c.xy / w * 0.5 + 0.5, c.w);
}

// The opaque depth at a screen position, in blocks. textureLod: this runs
// inside branches, where implicit derivatives are undefined.
float vvWaterSceneDepth(vec2 uv)
{
    return linearDepth(textureLod(depthTex, uv, 0.0).x) * zFar;
}

// The world a water texel's reflected ray finds in the opaque frame. The ray
// is projected once and cut where it leaves the screen or would pass behind
// the eye, so the shared march (reflect.glsl) spends every step on the
// visible stretch: a far shore costs what a near bank does. No noise: the
// ray belongs to its texel, so the answer holds still while the camera does.
// Returns the colour and a confidence; .a 0 is a miss (off screen, into the
// sky, or behind something too thick to be the surface it crossed), and the
// caller keeps its sky.
vec4 vvWaterSceneTrace(vec3 origin, vec3 dir, vec3 toEye)
{
    if (vv_waterSceneOn < 0.5 || vv_waterSceneSteps < 0.5 || vv_waterSceneMix < 0.001) return vec4(0.0);

    vec3 o = origin + dir * VV_WSCENE_START;
    vec3 a = vvWaterSceneProject(o);
    if (a.z <= 0.05) return vec4(0.0);
    vec3 b = vvWaterSceneProject(o + dir);
    float dz = b.z - a.z;
    float reach = VV_WSCENE_REACH;
    if (dz < 0.0) reach = min(reach, (a.z - 0.1) / -dz);
    b = vvWaterSceneProject(o + dir * reach);
    if (b.z <= 0.05) return vec4(0.0);

    // Cut at the screen border: the screen position is linear along the
    // projected segment, 1/depth is what interpolates with it.
    vec2 d = b.xy - a.xy;
    float sMax = 1.0;
    if (d.x > 1e-6) sMax = min(sMax, (1.0 - a.x) / d.x);
    if (d.x < -1e-6) sMax = min(sMax, -a.x / d.x);
    if (d.y > 1e-6) sMax = min(sMax, (1.0 - a.y) / d.y);
    if (d.y < -1e-6) sMax = min(sMax, -a.y / d.y);
    if (sMax <= 0.0) return vec4(0.0);
    vec2 bXY = a.xy + d * (sMax * 0.999);
    float bZ = 1.0 / mix(1.0 / a.z, 1.0 / b.z, sMax * 0.999);

    VvReflectHit hit = vvReflectMarch(vv_waterScene, depthTex, 1.0, vec2(zNear, zFar),
                                      vec4(a.xy, a.z, 1.0), vec4(bXY, bZ, 1.0),
                                      dot(dir, toEye), int(clamp(vv_waterSceneSteps, 1.0, 64.0) + 0.5),
                                      VV_WSCENE_THICK, 0.0);
    return vec4(hit.color, hit.valid);
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
    return vec4(textureLod(vv_waterScene, s.xy, 0.0).rgb, 1.0);
}
