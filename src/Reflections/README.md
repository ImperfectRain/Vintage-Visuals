# Reflections

The render-stage bridge that lets reflective surfaces see the world.

**This subsystem shades nothing.** It has no GLSL of its own and no shader patch
group. It produces a source image and hands it over; the reflection is evaluated
inside PseudoPBR, because that is where the texture grid, normal, roughness and
metalness already live and none of them should be duplicated.

## The problem it solves

`chunkopaque.fsh` is a forward opaque pass. It knows which material texel a
fragment belongs to, but the frame it would sample is the one it is still
drawing. The post-process pass can see the finished scene but has no idea which
texel anything is. Neither pass can produce a pixel-art mirror alone.

So the scene is carried **across a frame** rather than across a pass.

```
frame N                          frame N+1
-------                          ---------
world renders                    terrain pass shades
      |                                |
primary framebuffer                    | samples
      |                                v
AfterPostProcessing  ---> capture ---> reflection ray
                         (half res,         |
                          depth in          v
                          alpha)       material texel
```

## Files

| File | Role |
|---|---|
| `SceneCaptureRenderer.cs` | The capture. Owns the framebuffer, copies the frame, records the transform it was drawn with |
| `ReflectionsSubsystem.cs` | Lifecycle. Starts and stops the capture from config, registers the renderer |
| `../../assets/vintagevisuals/shaders/vvscenecapture.vsh` | Fullscreen quad, clip space, no matrix |
| `../../assets/vintagevisuals/shaders/vvscenecapture.fsh` | Copies scene colour to RGB, linear view depth to alpha |

The consuming code is `vvSceneReflection` in
`../../assets/vintagevisuals/shadersnippets/pseudopbr.glsl`.

## What the capture holds

| Channel | Content |
|---|---|
| RGB | The composed scene, as it was at `AfterPostProcessing` |
| A | Linear view depth, normalised by the far plane |

Packing depth into alpha is why the terrain shader needs **one** new sampler
rather than two. Adding a sampler to `chunkopaque.fsh` has twice cost this
project the entire world render.

## Constraints that are not negotiable

**Depth comes from the depth attachment, never from `gPosition`.** The position
G-buffer lives inside `#if SSAOLEVEL > 0` and does not exist for a player with
SSAO off. A depth buffer always does.

**The capture texture is `ClampToEdge`.** A reflected ray that leaves the screen
must be rejected and fall back to the analytic environment. With `Repeat` it
would sample the far side of the frame and paint unrelated geometry onto a wall.

**The texture is rebound per draw call**, in
`../PseudoPBR/TerrainTextureBindInterceptor.cs`, alongside the material atlases.
A texture unit is global GL state; binding once a frame does not survive to the
chunk draws. This was diagnosed from debug view 41 showing the block atlas where
the captured frame should have been.

**Everything fails safe.** Any failure — shader, framebuffer, engine texture ids
— disables the feature and logs. The shader's `vv_reflectValid` uniform is 0 in
all of those cases, and 0 means the analytic fallback, which is what shipped
before this subsystem existed.

## Cost

| | |
|---|---|
| Render target | One RGBA8 at half the frame in each axis, so a quarter of the pixels |
| When | Every frame the feature is on, whether or not anything reflective is visible |
| Extra passes | One fullscreen copy |
| Measured | **No.** Nothing here has been profiled |

Off by default (`Reflections.SceneReflections`) for that reason.

## Status

**L2 — implemented, compiles, tested statically, not visually validated.**

The bridge is confirmed alive in game: debug view 41 shows the captured world,
and view 39 reports hits. Whether the resulting reflection reads correctly is
unverified. See `docs/CHECKLIST.md` for the specific scenes still outstanding.

**Water is not supported.** `chunkliquid.fsh` is in no patch group. It should
wait until the reflection geometry is validated on terrain, or a wrong reflection
appears in two places at once and neither can be trusted to diagnose the other.

**Entities are not supported.** They have no material atlas, so there is no texel
grid to attach a reflection to.
