// Vintage Visuals - scene reflection bridge on the modular terrain path.
//
// STAGE ONE OF THE REFLECTION RESTORATION, AND DELIBERATELY ONLY THE BRIDGE.
// The legacy reflection (pseudopbr.glsl) was never seen working in game: its
// own debug view 41 was the outstanding proof that the captured frame reaching
// terrain contains the world rather than whatever else was left on the unit.
// This group restores exactly that question and nothing else - the capture
// sampler, the uniforms describing it, and the two views that answer it - so
// the march and the material response are rebuilt on a binding that has been
// seen to work, instead of debugging a reflection and a binding at once.
//
// Its own patch group, because it owns a sampler. It runs after
// pbrterrainmaterial and asserts that group's debug entry point, so it can
// never apply to a terrain program the material path did not patch. The
// sampler is declared below vanilla's last one, on an explicit unit (13) that
// the texture slot report shows is free, and is rebound per terrain draw by
// TerrainTextureBindInterceptor with the active texture unit restored.
//
// Zero is vanilla for every uniform: vv_reflectValid 0 means no capture, and
// both views then return a flat marker colour rather than sampling the unit.
// Outside debug views 41 and 42 this file changes no pixel.

uniform sampler2D vv_reflectScene;
uniform mat4 vv_reflectViewProj;     // the transform that capture was drawn with
uniform vec3 vv_reflectCameraDelta;  // THIS frame's player position minus the capture's
uniform float vv_reflectValid;       // 0 no capture, 1 capture usable
uniform float vv_reflectFar;         // far plane of the captured projection
uniform vec2 vv_reflectFrameSize;    // screen size in pixels, for the capture view

vec4 vvTerrainReflectDebug(vec4 color)
{
    int mode = int(vv_pbrDebugView + 0.5);
    if (mode == 0) return color;

    // 41: the capture itself, read at this fragment's own screen position.
    // It should look like the world, slightly darkened and one frame behind.
    // A texture atlas, a GUI, or flat garbage here means the per-draw bind is
    // not surviving to the terrain draw. Dark blue means no capture at all.
    if (mode == 41)
    {
        if (vv_reflectValid < 0.5) return vec4(0.0, 0.0, 0.35, color.a);

        // The colour alone. Alpha holds linear depth in blocks, and the
        // legacy view mixed it in as blue: past a few blocks that saturated
        // and hid the image this view exists to show (seen in game,
        // 2026-11-22). The capture freezes while any debug view is active,
        // so walking makes this image slide off the geometry - which is the
        // proof it is the capture and not the live frame.
        vec2 screen = gl_FragCoord.xy / max(vec2(1.0), vv_reflectFrameSize);
        vec4 cap = texture(vv_reflectScene, clamp(screen, 0.0, 1.0));
        return vec4(cap.rgb, color.a);
    }

    // 42: how far the player has moved since the capture, per axis, a
    // quarter block to full brightness. The capture freezes while a debug
    // view is active, so this measures movement since leaving normal
    // rendering: black if you have not moved since, brightening as you walk.
    if (mode == 42)
    {
        return vec4(abs(vv_reflectCameraDelta) * 4.0, color.a);
    }

    return color;
}
