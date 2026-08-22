# Vintage Visuals - Implementation Plan

The roadmap. What is built, what is next, what is deliberately deferred, what is
still an open question, and what was tried and rejected.

**This file is the plan, not the status.** For what each subsystem currently is
and what is wrong with it, read [STATUS.md](STATUS.md). For how far each has been
proven, read [CHECKLIST.md](CHECKLIST.md). For why the architecture is what it
is, read [DECISIONS.md](DECISIONS.md).

**This file previously contained two contradictory roadmaps** — a ten-system
phase list and a separate four-phase list appended below it — plus a paragraph
congratulating itself on having corrected stale claims, which had itself gone
stale. Both are gone. Historical reasoning that was worth keeping moved to
DECISIONS.md; the rest was superseded.

---

## CURRENT — implemented and in the tree

Levels are as defined in STATUS.md. **L2 means it has never been seen working.**

| System | Level | Where |
|---|---|---|
| Shader patch engine, per-group rollback | L4 | `src/Common/Patching/` |
| Config, live reload, ConfigLib bridge | L4 | `src/Common/` |
| Environment state and scene intent | L4 | `src/Common/Scene/` |
| Colour management, adaptive stack | L4 | `src/ColorGrade/` |
| Material inference and atlas (2 pages) | L4 | `src/PseudoPBR/` |
| Per-texture material resolution | L3 | `src/PseudoPBR/MaterialResolver.cs` |
| Second material atlas | L2 | `src/PseudoPBR/MaterialAtlas2Builder.cs` |
| PBR on terrain | L4 | `chunkopaque`, `chunktopsoil` |
| PBR on entities | L4 | `entityanimated` |
| PBR on particles | L3 | `particlescube`, `particlesquad` |
| Metalness, multi-scatter, specular occlusion, anisotropy | L2 | `pbrcore.glsl` |
| Emission masks | L2 | atlas 2, channel A |
| Wetness, rain ripples | L4 | `src/Weather/` |
| Snow dusting, frost response | L2 | `src/Weather/` |
| Cloud shadows from the game's own tiles | L4 | `src/Weather/CloudTileReader.cs` |
| Sun dapple from vanilla's shadow map | L2 | `pseudopbr.glsl` |
| God-ray shafts into vanilla's own pass | L2 | `pseudopbr.glsl` |
| Scene capture and pixelated reflections | L2 | `src/Reflections/` |
| Atmosphere state read from the game | L2 | `src/Common/Scene/AtmosphereState.cs` |
| Height haze through vanilla's ambient stack | L2 | `src/Atmosphere/` |
| Aerial perspective, one owner for fog | L2 | `atmosphere.glsl`, four programs |

---

## NEXT — highest priority unfinished work

**1. Validate the reflection.** It is the newest and least proven system, and
several passes of correction have been driven entirely by screenshots. The
specific scenes are listed in CHECKLIST.md under Reflections. Until those pass,
nothing should be built on top of it.

**2. Measure something.** No part of this project has ever been profiled. Every
cost claim in the documentation is an argument from operation count. The scene
capture is the obvious first target: it runs every frame whether or not anything
reflective is visible.

**3. Close the L2 backlog in the material system.** Metalness, multi-scatter
compensation, specular occlusion, anisotropic grain and emission masks are all
implemented, tested statically, and never seen. They are cheap to validate
because the debug views for each already exist.

---

## LATER — deliberately deferred, with the reason

| Work | Deferred because |
|---|---|
| Water reflections | The reflection geometry is not yet validated on terrain. Patching `chunkliquid.fsh` now means debugging two surfaces at once, and a wrong reflection in two places is harder to diagnose than in one |
| Parallax / height mapping | The height channel is packed and has no consumer. Needs the reflection work to settle first, as both compete for the same shader budget |
| Entity reflections | Entities have no material atlas, so there is no texel grid to attach a reflection to. Needs a separate decision about entity texture identity |
| Post-processing (bloom, DoF, camera FX) | Blocked on the colour-space problem below. Grading in the wrong space makes every subsequent image operation wrong too |
| SSAO | Vanilla has its own. Any addition must be shown to beat it rather than duplicate it |
| Quality tiers | Premature without measurements |
| Temporal accumulation / TAA | Last, and only if the renderer allows it |
| Local volumetric fog | Vanilla has `fogSpheres`, three of them, fed from the ambient system. Any addition must be shown to beat it rather than duplicate it |

---

## RESEARCH — open questions, not yet answerable

**Colour management space.** Grading operates on vanilla's already-composed,
display-referred output. Exposure, contrast and saturation are being applied to
values that are not linear radiance. This is a correctness problem, it blocks
post-processing, and the fix is not obvious without knowing what vanilla's
pipeline guarantees. See DECISIONS D16.

**`vv_sunExposure`'s numeric range.** Debug view 16 showed it reads ~1 across an
entire outdoor scene. It is still used by wetness, snow and frost as a
sky-exposure gate. Whether those gates are meaningfully firing has not been
measured. The game's rain map (`GetRainMapHeightAt`) is the authoritative
replacement and is not yet plumbed.

**Sub-tile cloud drift.** `windOffsetX`/`windOffsetZ` are read from the game's
cloud renderer but not applied. Cloud shadows are correct at tile granularity
and unaccounted for below it.

**Entity material identity.** Vintage Story keeps separate atlas infrastructure
for entities. Whether the per-texture resolution used for blocks transfers is
unknown.

**Whether the ambient modifier survives.** `IAmbientManager.CurrentModifiers` is
public and writable, and the blend is documented on the interface, but nothing
documents the dictionary's LIFETIME. `AmbientBridge` re-adds its entry every tick
and verifies the blend once, so a stack rebuilt on a world change is handled -
but whether that ever happens has not been observed. The log answers it the first
time anyone runs with height haze on.

**A horizon colour usable by every surface.** `getSkyColorAt` and the `sky`
sampler exist in `chunkopaque.fsh` and in no other shading program, so the
authoritative sky colour is out of reach for grass, entities and particles. A
derivation from `BlendedFogColor`, `SunColor` and sun elevation is the fallback -
DECISIONS D21.

---

## DEPRECATED — tried, rejected, do not reimplement

Each of these looked reasonable and failed for a reason that is not obvious from
the outside. Full reasoning in DECISIONS.md.

| Approach | Why it was rejected |
|---|---|
| **Reimplementing height fog in GLSL** | Vanilla computes it in all seven shading programs from two ambient uniforms, including the sky and the water, which this mod does not patch. A snippet version would stop at the edge of what was patched, and the seam falls where a hillside meets its own grass — D18, D19 |
| **Rain fog inside the weather patch group** | It reached the two terrain shaders and nothing else, so an animal standing in a fogged valley kept crisp edges. Not a tuning problem: no value of FogStrength could reach a program the group did not patch — D23 |
| **A second sun-attenuation model** | `IClientGameCalendar.SunColor` already reddens the sun near the horizon per player position, with a per-day offset. A mod model would light the terrain with a sun of one colour while the player looks at a sun of another — D21 |
| **Procedural sunfleck field** | Vanilla's shadow map already resolves individual leaf gaps and animates them with the game's own wind. A second field is a second description of the same leaves, and the invented one has no access to where the branches are. `vvSunflecks` and nine constants were deleted, not disabled — D7 |
| **`vv_sunExposure` as a canopy gate** | Measured flat at ~1 across a whole forest. It is a lighting result, not a geometric cause — D7 |
| **Edge detection (`4p(1-p)`) as canopy identity** | An edge detector can only outline a shadow, never fill it. Replaced by counting separate occluders — D7 |
| **Reflection as a directional gain on the ambient colour** | A gain is not a reflection. It conserved energy, passed its tests, and produced a stylised highlight rather than an image — D8 |
| **R2 phase-shifted reflection cells** | Structure from a low-discrepancy sequence is a procedural patchwork; neighbouring texels differed because the sequence said so, not because they see different things — D10 |
| **World-space reflection marching** | Steps project to wildly uneven screen distances: rings from shell sampling, then smearing from overshoot — D11 |
| **Radial distance in the depth comparison** | The capture stores axial view-space z. The mismatch is radially symmetric and drew its own rings — D12 |
| **Denominator clamp as a specular energy limit** | Caps the peak at `1e5*roughness^4`, so a smoother surface gets a dimmer highlight. Backwards, and why wet stone never produced a sheen — D14 |
| **Shader transpilers for the Harmony hooks** | `VintagestoryLib` is not a stable API. Prefix/postfix only — D1 |
| **Vegetation wind** | Vanilla already does it, including a high-frequency term and per-class bend counters |
| **Recolouring foliage by season** | Vanilla's `colormapData` already accounts for temperature, rainfall and altitude per block |

---

## Gating rule

A phase does not start until the previous one's milestone is **verified**, and
verified means L4 — seen on screen and surviving a world reload. This rule has
been broken repeatedly by building on L2 work; the reflection corrections are the
direct cost of that.
