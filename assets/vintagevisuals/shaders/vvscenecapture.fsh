#version 330 core

// Scene capture: the reflection system's source image.
//
// One low-resolution RGBA16F texture holding the frame the reflection will
// read: RGB is the composed scene colour, ALPHA is linear view depth in blocks.
// Packing depth into the alpha channel is the whole reason this pass exists -
// it means the terrain shader needs ONE new sampler rather than two, and a
// sampler added to chunkopaque.fsh has twice cost this project the entire world
// render.
//
// Depth comes from the framebuffer's own DEPTH ATTACHMENT, not from the
// gPosition G-buffer. That is deliberate: gPosition is written inside
// `#if SSAOLEVEL > 0`, so it does not exist for a player with SSAO switched
// off, while a depth buffer always does. Reading depth here is what lets the
// reflection work at every quality setting instead of silently vanishing at
// one of them.

uniform sampler2D sceneColor;
uniform sampler2D sceneDepth;

// Camera near and far, needed to turn the depth buffer's non-linear value back
// into a distance. Uploaded from the game's own projection so this cannot drift
// from what the scene was actually rendered with.
uniform float zNear;
uniform float zFar;

in vec2 uv;
out vec4 outColor;

void main()
{
    vec3 scene = texture(sceneColor, uv).rgb;

    // Standard reverse of a perspective depth divide. The buffer stores
    // window-space depth in 0..1; this recovers view-space distance.
    float d = texture(sceneDepth, uv).r * 2.0 - 1.0;
    float linear = (2.0 * zNear * zFar) / (zFar + zNear - d * (zFar - zNear));

    // Stored directly. The target is RGBA16F, so normalising by zFar and then
    // multiplying by zFar in the terrain shader would only throw precision away.
    outColor = vec4(scene, max(0.0, linear));
}
