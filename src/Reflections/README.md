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
render-stage framebuffer               | samples
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
| RGB | The composed scene source selected at `AfterPostProcessing` |
| A | Linear view depth, in blocks |

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

**The colour source is selected at the render stage.** The first source tried is
`IRenderAPI.CurrentFrameBuffer`, because the framebuffer bound during
`AfterPostProcessing` is the one most likely to be the composed image at that
point in the pipeline. If it has no colour texture, the capture falls back to
`EnumFrameBuffer.Primary`. Depth follows the colour source when possible and
falls back to Primary depth. The first successful capture logs current, primary,
chosen colour, chosen depth, target, viewport and every public framebuffer entry
so a bad View 41 screenshot has IDs to compare instead of another guess.

**PBR debug views freeze the capture.** View 41 is drawn by replacing terrain
with the capture texture. If the capture keeps updating while that view is
active, the next frame captures the diagnostic output instead of the world:
terrain feeds back toward black while sky remains normal because the sky pass is
not replaced by PBR debug. The capture therefore pauses whenever
`PseudoPBR.DebugView` is non-zero and diagnostics inspect the last normal frame.

## Cost

| | |
|---|---|
| Render target | One RGBA16F at half the frame in each axis, so a quarter of the pixels |
| When | Every frame the feature is on, whether or not anything reflective is visible |
| Extra passes | One fullscreen copy |
| Measured | **No.** Nothing here has been profiled |

Off by default (`Reflections.SceneReflections`) for that reason.

## Status

**L2 — implemented, compiles, tested statically, not visually validated.**

### What the march does that nothing measured

Two findings from a source audit, both about measurement rather than tuning, and
both now visible in game rather than argued about:

**The stride constant is not the stride on the rays that matter.**
`VV_SSR_STRIDE` asks for one sample every two capture texels and explains at
length why a uniform screen-space rate is what makes a reflection foreshorten
instead of smear. `VV_SSR_STEPS` now caps the count at 96. The first visible
scene used 24; on a long grazing ray — the ordinary case on a flat reflective
floor, and the only case that carries a reflected tree — that walked broad
intervals through the capture and produced ringed hit/miss bands in views 39,
48 and 51. Raising the cap keeps the march bounded, but lets floor rays stay
much closer to the named stride before the budget clamps. `VV_SSR_THICKNESS`
stays at 0.5 because widening the tolerance would hide the coarse march rather
than fix it.

**One red was four faults.** View 39 paints a miss red whether the ray pointed
back at the camera, started off the captured frame, walked off the edge without
crossing anything, or crossed the right surface and was rejected as too thick.
The last two are opposites: one says the geometry is never found, the other says
it is found and thrown away, and they want opposite fixes. **View 48 separates
them**, and view 50 shows how far behind the surface each refined crossing
landed against the tolerance judging it.

Neither finding has been acted on. Both are stated here so the next run measures
instead of guessing.

### The depth is half float, and the tolerance is half a block

The capture originally packed linear view depth into the alpha of an RGBA8 target
as `linear / zFar`, which made the finest representable difference `zFar / 255`
blocks everywhere. At ordinary Vintage Story far planes, that was coarser than
`VV_SSR_THICKNESS` and forced the hit decision into quantisation noise.

The target is now `Rgba16f`, and `vvscenecapture.fsh` writes linear depth
directly to alpha. The terrain shader reads `texture(vv_reflectScene, uv).a`
directly; there is no `/ zFar` then `* zFar` round trip. Half-float precision is
local rather than global, so debug view 51 reports the local quantum at the
sampled scene depth against the half-block tolerance.

### Two smaller findings, confirmed and not acted on

**Roughness does not coarsen the scene reflection.** `vvPixelReflection`
quantises the reflection direction into `cells` chosen by roughness — and then
uses that quantised direction *only* for `vvReflectionFallback`. The captured
world ray now uses the geometric face normal, deliberately ignoring material
normal flecks after they produced a cone of near-floor hits instead of stable
geometry. A rough surface with a valid scene hit therefore gets the same
captured geometry as a polished one. The documented hierarchy — smooth reflects
sharply, rough reflects coarsely — currently applies to the analytic sky and not
to the captured world, so the two can have visibly different character on the
same surface either side of a hit boundary.

**The luminance ceiling is a compression rule, not an energy rule.** A scene hit
brighter than `envLuma * VV_REFLECT_MAX` (1.2) is scaled down to it. With a local
environment at 0.20 and a sunlit tree at 0.70, the tree arrives at 0.24 — losing
about two thirds of its luminance before PBR sees it. A sunlit surface can
legitimately be several times brighter than the local horizon colour, so this is
bounding the reflection by something that does not bound the world. It may still
be the right visual choice; it is not the conservation rule its placement
suggests.

Both are recorded rather than changed. They sit downstream of the depth problem,
and changing them first would be tuning the visible result before the information
pipeline is trustworthy.

### Reading the march views

Only meaningful once view 39 shows green somewhere; if the bridge is dead these
say nothing.

| View | Shows | What to look for |
|---|---|---|
| 48 | why each ray ended | yellow (found and rejected) versus red (never found) — opposite faults |
| 49 | stride against budget | bright red is a ray walked coarser than `VV_SSR_STRIDE` names |
| 50 | crossing residual | a bright red band at the grazing end is the tolerance absorbing a coarse march |

The bridge is not considered visually proven until debug view 41 shows the
terrain image clearly. The latest runtime evidence showed sky in the capture but
black terrain while view 41 was active; that is consistent with recursive
diagnostic capture, so diagnostics now freeze on the last normal frame before
they inspect the bridge. See `docs/CHECKLIST.md` for the specific scenes still
outstanding.

**Water is not supported.** `chunkliquid.fsh` is in no patch group. It should
wait until the reflection geometry is validated on terrain, or a wrong reflection
appears in two places at once and neither can be trusted to diagnose the other.

**Entities are not supported.** They have no material atlas, so there is no texel
grid to attach a reflection to.
