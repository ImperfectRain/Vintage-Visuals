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
| `WorldReflectionVolume.cs` | Local classified block volume. Builds the 2D atlas used when screen-space reflection has no valid scene hit |
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

## World Volume

Screen-space reflection remains the highest-confidence source, because it sees
the actual rendered frame. It cannot see geometry that is off screen, hidden
behind nearer pixels, or outside the previous capture. The world volume fills
that gap with a bounded local block atlas.

The volume is 128 x 64 x 128 cells around the player, stored as a 2D atlas of
Z slices. The current atlas layout is data driven: 128 x 64 slices arranged as
16 columns by 8 rows. The shader receives the slice size, grid and total atlas
size as uniforms rather than hardcoding them.

Each texel stores one classified voxel. Full opaque cubes store a representative
game block colour multiplied by local block light in RGB, and store the voxel
class in alpha. Cutout foliage stores its actual representative block colour in
RGB rather than a class swatch, because the same scan now also feeds forest
lighting. Other non-full cells store class debug colour in RGB and the same
class id in alpha. The first production pass traces only full opaque cubes for
reflection hits; partial, cutout, transparent, liquid, emissive and unsupported
cells are classified for diagnostics and future coverage.

The same rebuild also uploads a 128 x 128 canopy context texture. RGB is the
current representative colour of leaf blocks in that X/Z column; alpha is a
bounded leaf-density estimate. This is intentionally low frequency. The vanilla
shadow map still owns animated leaf gaps, sunflecks and wind movement; the
canopy texture owns only the broader question "am I under a tree, and what
colour is that tree today?"

The volume rebuilds on initial upload, after player movement beyond 32 horizontal
blocks or 16 vertical blocks, after `BlockChanged` inside the volume, and after
`ChunkDirty` intersects the volume. Rebuild logs include reason, invalidation
count and per-class totals. The atlas is bound only when pixel reflection is
active or when a world-volume debug view is selected.

`vvPixelReflection` resolves sources in this order:

1. screen-space scene capture, when the captured frame has a valid hit;
2. local world volume, when the scene hit is missing and the DDA hits a full
   opaque cube inside range;
3. analytic sky, horizon and ground fallback.

The debug views exposed in the new UI are:

| UI path | Mode | Shows |
|---|---:|---|
| Debug > Debug System: Materials > World reflection proof > World trace result | 53 | why the world DDA ended |
| Debug > Debug System: Materials > World reflection proof > World hit color | 54 | lit representative block colour for the hit cell |
| Debug > Debug System: Materials > World reflection proof > World hit distance | 55 | hit distance in blocks, normalised by range |
| Debug > Debug System: Materials > World reflection proof > World trace steps | 56 | DDA cell budget used |
| Debug > Debug System: Materials > World reflection proof > World voxel class | 57 | first non-empty classified cell crossed by the ray |
| Debug > Debug System: Materials > World reflection proof > Hybrid reflection source | 58 | white screen-space, green world volume, blue analytic fallback |
| Debug > Debug System: Materials > Forest lighting > World canopy density | 59 | leaf-column density from the canopy context texture |
| Debug > Debug System: Materials > Forest lighting > World canopy colour | 60 | seasonal/lit leaf colour stored for the current column |
| Debug > Debug System: Materials > Forest lighting > Forest ambient filter | 61 | low-frequency ambient attenuation/tint used under canopy |
| Debug > Debug System: Materials > Forest lighting > Vegetation LOD | 62 | distance simplification applied to foliage lighting |

## Cost

| | |
|---|---|
| Render target | One RGBA16F at half the frame in each axis, so a quarter of the pixels |
| When | Every frame the feature is on, whether or not anything reflective is visible |
| Extra passes | One fullscreen copy |
| World atlas | 128 x 64 x 128 classified cells packed into a 2048 x 512 RGBA upload when pixel reflection, canopy lighting or world/canopy debug is active |
| Canopy context | 128 x 128 RGBA upload, rebuilt with the world atlas and bound only when forest lighting or modes 59-62 need it |
| World rebuild | Initial upload, player movement threshold, block edits inside the volume, or dirty chunks intersecting the volume |
| Measured | **No.** Nothing here has been profiled |

Off by default (`Reflections.SceneReflections`) for that reason.

## Status

**L2 — implemented, compiles, tested statically, not visually validated.**

Normal terrain reflections now use a hybrid source: scene capture first, local
world volume second, analytic fallback last. The world volume is production code,
but still at L2 because it has not been profiled or validated in a running game
from this environment.

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
brighter than `envLuma * VV_REFLECT_MAX` is scaled down to it. The first ceiling
was 1.2, so with a local environment at 0.20 and a sunlit tree at 0.70, the tree
arrived at 0.24 — losing about two thirds of its luminance before PBR saw it. A
sunlit surface can legitimately be several times brighter than the local horizon
colour, so the ceiling is now 2.0: still a white-metal guard, but no longer a
hard crush against real reflected object colour.

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
