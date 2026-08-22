# Status

The single tracker for every feature and idea in Vintage Visuals: what is
finished, what is half-finished, what is planned, and what was tried and
abandoned.

**This file is updated in the same commit as the work it describes.** A feature
that changed state and did not move here is a feature that will be forgotten or
re-litigated. See [WORKFLOW.md](WORKFLOW.md).

**This file is the authoritative snapshot of current state.** Sections 1-8 are
that snapshot. Sections 9 onward are an investigation log kept for the reasoning,
and a record of what was rejected - they are history, not status.

| Question | Read |
|---|---|
| What does the code do? | the code, and sections 1-8 below |
| How far has it been PROVEN? | [CHECKLIST.md](CHECKLIST.md) |
| Why is it built this way? | [DECISIONS.md](DECISIONS.md) |
| What is next, deferred or rejected? | [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) |
| How does the data flow, and what falls back to what? | [ARCHITECTURE.md](ARCHITECTURE.md) |
| Does a feature belong at all? | [VISUAL-LANGUAGE.md](VISUAL-LANGUAGE.md) |
| What scene proves it? | [VISUAL-TESTS.md](VISUAL-TESTS.md) |

---

## How to read the levels

Nothing that touches GLSL or the render pipeline can be verified by building, so
every row carries the level it has actually reached:

| | |
|---|---|
| **L0** | proposed — an idea with no design behind it yet |
| **L1** | parses — YAML/JSON checked, GLSL read by eye |
| **L2** | compiles — `dotnet build`, plus `tools/verifypatches` against the game's own dumped shaders in every define combination |
| **L3** | loads — the game starts, the log shows the group applied |
| **L4** | renders — seen on screen, and survives a world reload |
| **—** | not started |

**Only L4 closes anything.** A row at L2 has never been seen working.

Status marks: `[x]` done · `[~]` partial or unconfirmed · `[ ]` not started ·
`[-]` deliberately abandoned.

---

## 1. Rendering core

| | Feature | Level | Notes |
|---|---|---|---|
| `[x]` | YAML shader patch engine, token/regex/start/end kinds | L4 | whitespace-insensitive matching |
| `[x]` | Per-group rollback on any anchor failure | L4 | a failed group costs its subsystem, never the frame |
| `[x]` | Patch gating by config — skips the patch, not the effect | L4 | `IsPatchGroupEnabled` + shader reload |
| `[x]` | Harmony hook on `ShaderRegistry.LoadShader` | L4 | reflection-guarded, logs rather than throws |
| `[x]` | Config with live reload (<kbd>Ctrl</kbd>+<kbd>V</kbd>) | L4 | |
| `[x]` | ConfigLib bridge over the event bus | L3 | 44 settings; zero coupling, no declared dependency |
| `[x]` | ASCII guard on shipped GLSL | L4 | load-time refusal + smoke scan |
| `[x]` | `tools/smoketest` — 269 checks, no game needed | L4 | |
| `[x]` | `tools/verifypatches` — every group vs the game's own shaders | L4 | 48/24/6 define combinations |
| `[x]` | `VINTAGE_VISUALS_DUMP` writes the merged source | L4 | how the cloud-shadow wrapper was cleared as a suspect |
| `[ ]` | Quality tiers (potato → ultra) driving subsystem settings | — | build before the expensive systems land, not after |
| `[ ]` | GPU/VRAM detection suggesting a tier | — | suggest, never force |
| `[ ]` | Rendering debug HUD (per-system state, material under cursor) | — | debug views exist; a HUD does not |
| `[x]` | **Visual language rules** (information ladder, hierarchy, budgets, blacklist) | — | [VISUAL-LANGUAGE.md](VISUAL-LANGUAGE.md) |
| `[x]` | **Visual test matrix and acceptance criteria** | — | [VISUAL-TESTS.md](VISUAL-TESTS.md). Makes L4 repeatable |
| `[x]` | **Effect provenance report** (`WriteSceneReport`) | L2 | who removed what light, and why, in one file |
| `[ ]` | Compatibility diagnostics when another mod patches the same shader | — | per-group rollback already survives it; the player is not told why |
| `[ ]` | Material authoring overrides (`materials/*.json`) and a material API | — | inference is the right default and will never read every modded texture correctly |

## 2. Scene understanding

| | Feature | Level | Notes |
|---|---|---|---|
| `[x]` | `EnvironmentState` — one shared worldview | L2 | sampled vs derived kept apart; config-scaled values excluded by rule |
| `[x]` | `EnvironmentTracker` — the only file that asks the game | L2 | runs whether or not a subsystem is enabled |
| `[x]` | Camera origin wrapped to 4096 blocks in double | L2 | raw world coords are unusable in float32 |
| `[x]` | Wrapped animation clock | L2 | unbounded clocks collapse phase in float32 |
| `[x]` | Material identity from `EnumBlockMaterial` | L3 | 14090 blocks classified, 0 fallbacks; covers modded blocks |
| `[x]` | Derived normal / roughness / specular / metal atlas | L4 | cached to disk, multi-page |
| `[x]` | Multi-page atlas via Harmony bind hook | L4 | degrades to vanilla with a log line if the hook fails |
| `[~]` | `CloudTileReader` — the game's own cloud placement | L2 | reflection into `VintagestoryLib`; **unverified**, see §9 |
| `[ ]` | Persistent `MaterialDefinition` per block, incl. subsurface | — | today's atlas is four channels; the idea is a full record |
| `[x]` | **Emissive materials** from vanilla's own `glowLevel` | L2 | hot rather than bright, dimmer in daylight, position-seeded flicker, feeds vanilla's bloom |
| `[ ]` | Camera state (velocity, FOV, underwater depth) as shared state | — | needed by motion effects and underwater |
| `[x]` | **Scene intent channels** between state and subsystems | L2 | named 0..1 vocabulary (wetness, cold, heat, haze, readability...) so ten subsystems share one reading. See [INSPIRATION.md](INSPIRATION.md) §1 |
| `[x]` | **Per-contribution caps and contribution records** | L2 | `GradeStack` clamps only the final result, so a saturated grade cannot say which of nine influences did it. §2 |
| `[x]` | **Stack budgets** so one idea cannot apply twice | L2 | *a real defect today*: in rain, grading, overcast and fog all press the same direction with no budget. §3 |
| `[x]` | **Gameplay readability constraint** | L2 | effects must yield to playability: caves navigable, rain fog not hiding a drifter, storms not also doing something clever. §4 |
| `[x]` | Shared scene vocabulary snippet (`scene.glsl`) | L2 | §5 |
| `[ ]` | Receiver masks (may this pixel receive this effect) | — | **deliberately not built.** Nothing would consume it until reflections or emissive land, and a contract with no consumer gets designed for the wrong shape. §6 |
| `[x]` | Effect authorities and dampening | L2 | ColorGrade contrast, crevice occlusion, rain fog and overcast all own contrast today with no arbitration. Also covers other shader mods. §6 |
| `[x]` | Per-shader debug enums | L2 | §7 |
| `[~]` | Style targets (None/Filmic/Muted/Vivid/Cold/Warm) | L2 | the reachable half of §9. Image-derived styles still open: decoding needs SkiaSharp, and without a framebuffer readback to compare against, aiming at a measured target is open loop |
| `[ ]` | Detected-versus-effective state with suppression | — | **deliberately not built.** There is no override layer for it to protect. §8 |

## 3. Colour management

| | Feature | Level | Notes |
|---|---|---|---|
| `[x]` | Exposure, contrast, saturation, white balance | L4 | confirmed on 1.22.7 |
| `[~]` | ACES filmic tonemap | L2 | **ships at 0.0.** Never confirmed that vanilla's output is still scene-referred where we grade it |
| `[~]` | Eye adaptation (`AdaptiveExposure`) | L2 | 19 model checks; never seen in game |
| `[~]` | Adaptive grading: time of day, weather, biome, indoors, depth, underwater | L2 | 34 rule checks; never seen in game |
| `[~]` | `vv_tint` per-channel gain | L2 | scene-referred, applied before the tonemap |
| `[ ]` | Exposure responding to fire, lava, lightning, explosions | — | today it reads block light only |
| `[ ]` | Split-toning (separate shadow and highlight tint) | — | would make golden hour much more convincing |

## 4. Material and lighting

| | Feature | Level | Notes |
|---|---|---|---|
| `[x]` | Surface relief in `chunkopaque.fsh` | L4 | |
| `[~]` | **Foliage translucency** | L2 | light through leaves, grass and crops; foliage identified from vanilla's own wind-mode bits |
| `[~]` | Surface relief in `chunktopsoil.fsh` (forest floor) | L2 | anchors confirmed against the real shader |
| `[x]` | Cook-Torrance: GGX + Smith-Schlick + Schlick Fresnel | L4 | energy-conserving |
| `[~]` | Geometric specular antialiasing | L2 | roughness widened in alpha from screen-space normal derivatives |
| `[~]` | Sky/ambient specular from `rgbaFog` | L2 | |
| `[~]` | Block-light specular with recovered direction | L2 | Mikkelsen surface-gradient of the light field |
| `[x]` | Per-layer debug views (0–43) | L3 | 1-24 material layers, 25-31 the canopy audit, 32-37 pixel reflection, 38-43 the scene bridge. Slider range, config clamp and shader modes are pinned to each other by `tools/smoketest` |
| `[x]` | Offline `tools/pbrgen` prototype + parity fixture | L4 | 31 Python tests; smoketest asserts the C# port agrees |
| `[~]` | **Lighting reach: entities** (`pbrentity` group) | L2 | mobs, animals and players get the same lobe. Default material, not a derived atlas - see below |
| `[ ]` | Lighting reach: held items | — | `helditem.fsh` has no `worldPos`, `blockLight` or `rgbaFog`; much thinner, much less to gain |
| `[x]` | One shared lighting snippet (`pbrcore.glsl`) | L2 | injected into all three programs; a smoke check asserts Cook-Torrance is defined exactly once |
| `[~]` | Crevice shading from the material normal's curvature | L2 | divergence of the stored gradient - a real cavity estimate, not an edge detector |
| `[ ]` | Screen-space contact shadows | — | **blocked.** Needs the depth buffer, which is being written during the opaque pass, not readable |
| `[ ]` | Separate local contrast from albedo level before the Sobel pass | — | "dark is deep" is the known weakness; belongs in `tools/pbrgen` first |
| `[~]` | **Metalness from the second atlas** | L2 | real `f0` from metalness rather than the specular-mask stand-in |
| `[~]` | **Multi-scatter GGX compensation** | L2 | Kulla-Conty with the Karis analytic fit; only ever adds, only to rough surfaces |
| `[~]` | **Specular occlusion** | L2 | Lagarde's fit, clamped below by the plain occlusion where it inverts at grazing angles |
| `[~]` | **Anisotropic highlight along measured grain** | L2 | structure tensor on the material normal; collapses exactly to the isotropic lobe at zero |
| `[~]` | **Emission masks** | L2 | atlas 2 channel A; multiplicative, so it can only ever remove emission from a block vanilla already lights |
| `[~]` | **Baked AO** | L2 | atlas 2 channel B |
| `[~]` | Specular lobe energy limit (`VV_GGX_MIN_ALPHA`) | L2 | display limit, not physical - see DECISIONS D14 |
| `[~]` | Height channel | L2 | packed in atlas 2 channel G, **no consumer** - parallax deferred |
| `[ ]` | Parallax / relief mapping | — | speculative; may fight the art direction. Height is already packed |

## 5. Weather

| | Feature | Level | Notes |
|---|---|---|---|
| `[x]` | Wetness model, temperature-gated, asymmetric drying | L4 | confirmed in game |
| `[x]` | Wet surface response (roughness, specular, albedo darkening) | L4 | rides the PBR path; owns no shader patch |
| `[x]` | Sky-exposure varying so rain cannot reach covered ground | L4 | lives in `pseudopbr.yaml` — a varying belongs to one group |
| `[~]` | Rain fog (terrain only, never the sky) | L2 | |
| `[~]` | Cloud shadows from the game's own cloud tiles | L2 | **reported broken three times**; see §9 |
| `[~]` | Rain ripples in standing water | L2 | field scatter and phase spread measured |
| `[~]` | Overcast light response | L2 | direct lobe down, sky term up — redistribution, not darkening |
| `[x]` | Snow as a derived state (`SnowTargetFor`) | L2 | tracked but nothing consumes it yet |
| `[~]` | **Snow film as a material response** | L2 | thin dusting on sky-exposed up-faces, cubed against the normal. Real accumulated depth is still vanilla's snow blocks |
| `[x]` | **Frost as an environmental layer** | L2 | built on vanilla's own `frostAlpha`/`fragFrostAlpha`. Vanilla tints; this adds the material body |
| `[ ]` | Rain streaks running down vertical wet faces | — | cut from the ripple commit on purpose, unverifiable blind |
| `[ ]` | Puddles pooling in depressions | — | needs a height signal the terrain shader does not have |
| `[ ]` | Weather transitions as first-class state | — | today each effect eases independently |
| `[-]` | Volumetric cloud shaping (`cloudshape` group) | — | **abandoned.** `octave()` is only reachable from `warp()`, which early-returns unless a perception effect is active. The slider did nothing |
| `[-]` | Cloud density scaling | — | **abandoned.** Unset uniform reads 0, and 0 meant *no clouds at all* |
| `[-]` | Fogging the sky dome (`weathersky` group) | — | **abandoned.** Flattens cloud against sky into a uniform haze in both renderers |

## 6. Atmosphere

`src/Atmosphere/` is now a subsystem rather than a rain modifier inside Weather.
See `src/Atmosphere/README.md` and DECISIONS D18-D21.

The audit that opened this work changed what there is to build. **Vintage Story
already has most of an atmosphere, and it is reachable from the CPU**:
`IAmbientManager` blends a public, writable stack of `AmbientModifier`s into the
fog the game renders, and that result reaches *every* shading program - including
the sky, the water, the entities and the particles, none of which this mod
patches and none of which any GLSL it could inject would reach.

So the rule here is sharper than the project's usual one: if a value can be
expressed as an ambient modifier, it belongs there and not in a shader, because
that is the only way to get an answer consistent across the frame. Four of the
five features below turned out to be that kind of value.

| | Feature | Level | Notes |
|---|---|---|---|
| `[x]` | Atmosphere state read from the game | **L2** | `AtmosphereState`, sampled every frame in `EnvironmentTracker`. Fog, height fog, sun colour and direction, ambient colour, camera height, far plane - all read, none modelled |
| `[x]` | Height haze | **L2** | Written into vanilla's own `flatFogDensity`/`flatFogStart`, which every shading program already computes. **Defaults to 0**: the band's height is the one number no test can settle |
| `[ ]` | Aerial perspective / directional in-scattering | — | The one genuine gap. Vanilla's fog is an isotropic mix toward a single colour with no sun-relative term. This is where GLSL is justified |
| `[ ]` | Sky scattering and horizon colouration | — | Vanilla owns the sky's own horizon (`horizonFog`, `getFogAmountForSky`) and patching the sky was already tried and rejected - D6. What is left is terrain taking the horizon's colour at distance, which is part of aerial perspective |
| `[ ]` | Sun and moon attenuation | — | **Vanilla already does this.** `IClientGameCalendar.SunColor` reddens the sun near the horizon, per player position, with a per-day `SunsetMod` so no two sunsets match. Sampled into `AtmosphereState`; a second model would contradict the sun disc the player can see |
| `[ ]` | Weather visibility | — | Currently rain fog inside the weather group's `applyFog` patch, so it reaches terrain only - entities in a fogged valley keep crisp edges. Belongs in the ambient stack for the same reason height haze does |
| `[ ]` | Cloud attenuation of direct sunlight | — | partly done as the overcast term; belongs here |
| `[ ]` | Godray interaction | — | vanilla has `GODRAYS`; untouched |

## 7. Water and reflections

`src/Reflections/` holds the scene-capture bridge. See `src/Reflections/README.md`
and DECISIONS D8-D13.

| | Feature | Level | Notes |
|---|---|---|---|
| `[x]` | Scene capture (previous frame, half res, depth in alpha) | **L2** | `SceneCaptureRenderer`. Confirmed alive in game via debug view 41; the reflection it feeds is not validated |
| `[x]` | Screen-space march, crossing detection, bisection | **L2** | Uniform sampling in the image. Two earlier world-space marches produced rings and smears - D11 |
| `[x]` | Texel-quantised reflection | **L2** | Ray starts at the texel centre, so one colour per texel is structural - D10 |
| `[x]` | Analytic sky/horizon/ground fallback | **L2** | Used where the ray leaves the screen, hits nothing, or points at the camera |
| `[x]` | Reflection through the existing BRDF | **L2** | Substituted into `vvAmbientSpecular`, so Fresnel, metalness, specular occlusion and wetness all apply unchanged - D13 |
| `[x]` | Wet surfaces reflect more strongly | **L2** | No reflection-specific code: `vvApplyEnvironmentLayers` lowers roughness and raises the specular mask, and both feed the reflection |
| `[ ]` | Water reflections | — | `chunkliquid.fsh` is in NO patch group. Deliberately deferred until the geometry is validated on terrain |
| `[ ]` | Refraction | — | |
| `[ ]` | Animated wave and micro-normals | — | vanilla animates water already; check before rebuilding |
| `[ ]` | Depth colouration | — | |
| `[ ]` | Rain disturbance of the water surface | — | the ripple field already exists and should be reused |
| `[ ]` | Underwater: absorption, attenuation, caustics, distortion | — | |

**Known limitations of the reflection, all current:**

- One frame stale. Camera movement is reprojected; world movement (leaves, mobs,
  the player) cannot be and lags.
- Screen-space limits apply and are not bugs: nothing behind the camera, off the
  frame, or hidden behind nearer geometry can be reflected.
- Depth is packed into an 8-bit alpha, so precision at distance is poor.
- Entities are unsupported - no material atlas, so no texel grid.
- Never profiled.

## 8. Not yet started elsewhere

| | Feature | Notes |
|---|---|---|
| `[x]` | ~~**Emissive materials**~~ | **built (L2).** See §4 |
| `[-]` | ~~**Vegetation wind**~~ | **abandoned.** Vanilla already does it, including a high-frequency term and per-class bend counters |
| `[x]` | ~~**Foliage translucency**~~ | **built (L2).** See §4 |
| `[~]` | Seasonal state (framework) | `GetSeason`/`GetSeasonRel`, hemisphere-aware, blended. Drives material response only - **vanilla owns seasonal colour** and does it per block with climate and altitude. Autumn is published and has no consumer yet |
| `[x]` | ~~Foliage excluded from the puddle and cavity paths~~ | **fixed.** Both gate on `vvIsFoliage()` now |
| `[x]` | ~~Wet-foliage response distinct from wet stone~~ | **fixed.** Foliage, creatures and particles hold water; stone films over |
| `[ ]` | SSAO | broad-scale, kept distinct from contact shadows and crevice shading |
| `[ ]` | Bloom | driven by emissive intensity, not brightness. Off / subtle / cinematic |
| `[ ]` | Depth of field | |
| `[ ]` | Camera effects | vignette, grain, chromatic aberration, lens dirt — **all default off**; mandatory ones are what make a mod feel like a generic shader pack |
| `[ ]` | Environmental camera effects | underwater distortion, heat shimmer, rain on the lens, screen frost |
| `[ ]` | Event camera effects | damage flash, explosion distortion |
| `[ ]` | Temporal accumulation / TAA | last, and only if the renderer allows it |
| `[ ]` | Dynamic resolution | |

---

# Investigation log

**Everything below is history.** It records how the current design was reached
and what was rejected on the way. It is NOT a statement of current state - for
that, read sections 1-8 above, or `CHECKLIST.md` for how far each thing has been
proven.

Kept because this project has twice rediscovered an approach it had already
rejected. A rejected idea that vanishes from the record is one someone will find
attractive again.

## 8a. External audit, checked

An outside review of head `63ab331`. Every claim below was checked against the
source rather than taken on trust; two did not survive that. Ranked by what it
costs to be wrong about them.

### Confirmed, and blocking

1. **Colour grading runs in the wrong signal domain.** VERIFIED from the dumped
   `final.fsh`: vanilla applies `gammaLevel` and then
   `color.rgb = pow(color.rgb, vec3(1.0 / extraGamma))` at lines 1351-1356,
   *inside* its own `main()`. Our group renames that `main()` and appends a new
   one, so the entire grade - exposure, white balance, ACES - runs on a
   **display-referred** signal. ACES expects scene-referred linear input. The
   contrast pivot at 0.5 was already reasoned for display space, so the stack is
   internally inconsistent as well as misplaced. `TonemapStrength` ships at 0, so
   nothing is visibly broken today; the design intent is what is wrong. **Do not
   build more grading until the insertion point moves or the stack is rewritten
   for display space.**
2. **`vv_sunExposure`'s range is still assumed.** Everything in the dapple gate
   hangs off it and it has never been read. Debug view 16 exists precisely to
   settle it; treat it as a prerequisite, not a nicety.
3. **`CloudTileReader` registration is still unproven.** Transposition and grid
   offset both remain possible. The optics downstream are now defensible, which
   makes it *more* important not to keep tuning them against an unverified
   mapping.

### Confirmed, worth doing

4. **Ground sunflecks are a receiver, not a source.** Feeding them into the
   god-ray mask is a rendering approximation, not light transport - a lit patch
   of ground does not scatter a shaft toward the camera. The canopy silhouette
   is the physically defensible source. Keep the flecks as low-weight artistic
   reinforcement and say so rather than implying it is physical.
5. **Conservation invariants should be tested.** Effects sort cleanly into
   light-adding (emission), light-redistributing (transmission, reflection) and
   light-removing (cloud shadow, crevice, dapple, attenuation), and each class
   has an invariant a static test can check. This fits `VisualBudget` exactly and
   would have caught the additive-dapple blowout before it shipped.
6. **Monotonicity invariants likewise** - more optical depth must never transmit
   more light, a distance fade must decrease, roughness must stay non-negative,
   foliage transmission must peak with the light behind the leaf.
7. **"Snow film as a material response" overstates what exists.** What is
   implemented is a thin film. Accumulation, thickness, edge buildup and melting
   are not. Rename to snow *film* and reserve the other name.
8. **Vanilla's signals are semantic, not radiometric.** `cloudOpaqueness`,
   `glowLevel`, `sunExposure`, `frostAlpha` and `windMode` are game abstractions;
   the cloud-opacity mistake was exactly this error and it will recur. Rule:
   take vanilla's data as authoritative *meaning*, then apply a physically
   motivated transform - never assume the number is itself physical.
9. **The tracker's checkboxes read as maturity.** A large L2 surface looks done
   at a glance. Levels need to be visible per row, not only defined at the top.

### Acted on in this pass

- The god-ray comment that mislabelled its own gate is corrected (10 below).
- `VV_SHAFT_GROUND` dropped from 0.22 to 0.10 and reframed: a lit patch of
  ground is a receiver, not a source, so it reinforces the shape rather than
  standing as a second physical origin (4 above).
- Conservation invariants are now enforced by `tools/smoketest`
  (`ConservationChecks`): every effect is declared ADDING, REMOVING or
  REDISTRIBUTING, and the source is checked against its declaration - including
  at the CALL SITE, since the sunfleck blowout lived in `result *= 1.0 + dapple`
  rather than inside the function. Verified to bite by restoring that exact
  line (5 above).
- Snow renamed from "material transformation" to "film" where it overstated
  what exists (7 above).

### Checked and disputed

10. **"The god-ray facing test should use camera forward, not fragment
    direction."** Partially wrong. `dot(normalize(cameraRelativePos), toSun)` is
    not a camera-orientation test - it is an *angular proximity to the sun*
    test - and for a blur that is radial from the sun's screen position that is
    the more appropriate quantity, not a mismatch. It also subsumes the camera
    test: if the sun is behind the player, no fragment in the frustum has a
    direction near `toSun`, so the mask is zero everywhere anyway. The audit's
    "one side of the screen passes and the other fails" is true and is exactly
    what radial falloff around the sun should do.
    What IS wrong is the comment above it, which describes the line as a
    camera-facing test. Fixed there rather than in the code.
11. **"Ripple density should follow precipitation rate rather than amplitude."**
    Right in principle, mostly already true, and worth splitting in two.
    `vv_weatherRipples` is driven by `world.Rain * RippleStrength`, and
    `vvRippleSlope` uses it as `if (rnd.x > density) return` - so it gates which
    CELLS carry a drop at all. **Spatial** impact density therefore already
    follows the rain rate rather than merely fading opacity, which is the half
    the audit was worried about.
    What does not follow it is **temporal** rate: `rate = 1 + floor(rnd.w * 3)`
    is a per-cell constant from the hash, so a cell that is active is hit just as
    often in drizzle as in a downpour. That is a real and small gap - fold the
    rain into `rate` - but not the rewrite the finding implies.

## 8b. Material identity

Added after an architecture review proposed a material-ID channel on the
geometry. The investigation changed the answer.

- [x] **Material resolved per TEXTURE, not per block.** `MaterialProfiles.For(
      block.BlockMaterial)` ran once per block, so a wooden gate's iron
      strapping got wood's roughness and a metalness of zero. `MaterialResolver`
      ranks asset path > slot name > block material. L2.
- [x] **Whole-segment matching**, tested against `blackstone`, `driftwood` and
      `metalworking`. A false positive is worse than no change.
- [x] **Chiseled blocks confirmed to need nothing.** The mesher textures each
      voxel face with its source block's texture, and the atlas is keyed by UV,
      so per-voxel material already resolves for free. This was expected to be
      the hardest case.
- [ ] **A material-ID vertex attribute is NOT available.** Investigated and
      ruled out, recorded so it is not re-proposed:
      `renderFlags` is completely full (0-7 glow, 8-10 zoffset, 11 reflective,
      12 lod0, 13-24 normal, 25-28 windmode, 29-31 winddata - no free bits);
      `colormapData` has spare bits at 6-7 and 13-15 but the chunk tesselator
      that writes them lives in `VintagestoryLib`, out of reach of shader
      patching; and a new attribute would mean changing the VAO layout the game
      builds per chunk.
- [ ] **Entities still resolve per-entity, not per-mesh-element.** A creature's
      fur, eyes, horns and metal armour share one classification. The same
      per-texture trick does not apply, because entity textures are one sheet
      per creature rather than one region per substance. Genuinely open.
- [ ] **Single-texture composites remain unsolved and are the honest limit.**
      Where a block draws two substances into ONE texture - iron painted into a
      wood plank image - there is no authored per-texel evidence, and inferring
      it from pixels is the thing this project refuses to do. No effect beats a
      wrong one.

## 8c. Canopy audit: what the game already knows

A source audit of the dapple/sunfleck system against Vintage Story's own
renderer. Nothing about dapple was changed - the point was to find out whether
the effect should exist in its current form at all, and the answer is no.

### The finding

**Vanilla already renders the canopy's shadow, with the canopy's shape, moving
in the canopy's wind.** The mod has been inventing a pattern to stand in for
something the game computes correctly.

Two lines of the game's own source decide this, both checked in
`reference/game-shaders/` rather than assumed:

| Where | What it says | Why it matters |
|---|---|---|
| `chunkshadowmap.fsh` | `outColor = texture(tex2d, uv); if (outColor.a < 0.02) discard;` | The shadow pass is alpha-tested against the block texture. Leaf blocks are cutout textures, so the gaps between leaves punch **real holes** in the shadow map. Foliage is in it, at texture resolution |
| `chunkshadowmap.vsh` | `worldPos = applyVertexWarping(renderFlags, worldPos);` | The shadow geometry is wind-warped by the **same function as the main pass**, wind mode 3 (Leaves) included. The holes already sway, at the game's phase, coherently across a whole tree |

The second is the stronger of the two. Spatial coherence is the thing a
procedural oscillator cannot fake and the thing the mod has failed at three
times; the game has it for free, because it is animating the actual leaves.

### What each signal actually is

| Signal | Source | Class | What it really means |
|---|---|---|---|
| `vv_sunExposure` | `rgbaLightIn.a`, per vertex | **CORRELATED** | Flood-filled sky light level. Direction-independent - identical at noon and midnight. Partial under a canopy, and equally partial under a roof edge, an overhang, a doorway, a pit |
| `shadowMapNear/Far` + `shadowCoords` | vanilla shadow pass | **AUTHORITATIVE** | Real directional sun occlusion, PCF-filtered, foliage included, wind-animated |
| `shadowBrightness` (the mod's `b`) | `getBrightnessFromShadowMap()` | **DERIVED, contaminated** | Its last line is `b = clamp(b + blockBrightness, 0, 1)`. A torch RAISES it. It is a brightness, not an occlusion |
| `lightPosition` | vanilla uniform | **AUTHORITATIVE** | Normalised direction to the current light. Present in both terrain shaders |
| `sunPosition` | vanilla uniform | **AUTHORITATIVE** | Sun specifically - but `chunkopaque.fsh` only. Not in `chunktopsoil.fsh`, so a shared snippet cannot use it |
| `renderFlags & WindModeBitMask` | vertex flags | **AUTHORITATIVE for "wind-affected"**, correlated for "foliage" | Vanilla's own `isLeaves` test is exactly this. It also catches grass, vines, fruit and seaweed |
| `vv_sceneBreeze` | mod clock | **PROCEDURAL** | A second animation of leaves the game is already animating |
| `ClimateCondition.ForestDensity` | game API | **CORRELATED** | A biome prior. Not plumbed, and correctly so - a dense-forest biome contains clearings |

The architectural error is one line of reasoning in `vvCanopyDapple`'s comment:
"Partial is therefore an exact statement that something leafy is overhead."
It is not. Partial sun exposure is a **lighting result**; leaves blocking the
sun is a **geometric cause**. The flood fill produces partial values at any
gradient - beside a roof edge, under an overhang, in a doorway, at a cave
mouth. This is why dapple appears under buildings and cliffs.

### Second defect, independent of the mask

Dapple is applied as `result *= 1.0 - shaded` at the very END of `vvApplyPbr`,
after ambient specular, block-light specular, foliage transmission **and
emission**. So the canopy currently dims a torch and a glowing forge. Direct
sunlight is the only term it has any business touching.

### What was built

An instrument, not a fix. `vvSunVisibility()` reads the shadow map directly and
stops before `+ blockBrightness`, giving pure geometric sun occlusion;
`vvSunShadowBreakup(radius)` measures how much a neighbourhood of shadow taps
disagrees, as `4p(1-p)`. Debug views 25-28 expose both.

Breakup is the candidate replacement for the `vv_sunExposure` gate, and it is a
geometric cause rather than a lighting result: under a roof every tap for metres
agrees (shadowed), in a field every tap agrees (lit), under leaves they
**disagree**, because the gaps are metres apart or less. Its known leak is a
bright band along any straight shadow edge, roughly as wide as the sample ring -
a thin band, against today's failure of the entire area under any roof.

### MEASURED IN GAME - the shadow map resolves the canopy

Birch woodland, midday, shadow quality medium, 480 block render. Debug views
16, 25, 26 and 27 captured from one spot without moving.

| View | What it showed |
|---|---|
| **16** `vv_sunExposure` | **White essentially everywhere** - open ground, forest floor, terraces alike. Carries almost no canopy information in a real scene |
| **25** vanilla sun visibility | **Individual leaf shadows, fully resolved.** Per-leaf detail in the crowns and leaf-shaped shadow on the ground |
| **26** breakup at 2/6/16 texels | Tree crowns white - all three radii firing. Terrain shadow edges cyan/blue - the 6 and 16 texel rings, with little red |
| **27** breakup(6) x shadowed | Fires on leaf silhouettes AND streaks along every terrace shadow edge |

Three conclusions, in order of consequence.

**The game already resolves canopy gaps.** View 25 settles Phase 4: the
procedural fleck generator is not needed as the primary pattern, and the shadow
it should be modulating was in the frame the whole time.

**vv_sunExposure was worse than ambiguous - it was flat.** The audit predicted
it could not tell a canopy from a roof. The measurement says it does not even
distinguish a canopy from open sky: it reads ~1 under the crowns. So the old
gate, `smoothstep(0.97, 0.62, exposure)`, passed nearly nothing under real trees
and passed its maximum wherever something else held sun light down - a terrace
lip, an overhang, a doorway. The effect was not weak in the right places. It was
firing in the wrong ones, which is exactly what was reported and was
misdiagnosed twice as a tuning problem.

**The discriminator is scale, and view 26 shows it working.** A canopy breaks
the shadow at every scale; a wall or a terrace is one straight edge, which the
wide rings cross and the 2-texel ring only disagrees within about two texels of.
Crowns came out white, terrain edges came out cyan. View 27 fires on those
terrain edges precisely because radius 6 was the wrong choice - it is now
`vvCanopyEvidence()` at radius 2 instead, exposed as view 29.

### What changed

- The gate is `vvCanopyEvidence()`: fine-radius shadow breakup where the sun is
  blocked. Geometric cause, not lighting result. `vv_sunExposure` no longer
  gates dapple, and canopy height is driven by the evidence term too.
- Dapple was applied at the END of `vvApplyPbr`, multiplying the finished pixel
  - after block-light specular, the sky term, foliage transmission and
  **emission**. A canopy dimmed a torch and dimmed a glowing forge. It now sits
  immediately after the sun specular lobe and before all four. Nothing caught
  this: the value was right, the sign was right, it was bounded, it conserved,
  and it was in the wrong place, so `CheckDappleTouchesSunlightOnly` pins
  position rather than value.
- The diffuse term is still scaled, knowingly: vanilla hands it over with sky
  light already mixed in and no way to separate them. A canopy does block sky
  light, so it is defensible rather than merely unavoidable.

### The first gate was an edge detector, and only drew outlines

Reported from view 29: "the visible effect seems to only be an outline around
already present shadows." Correct, and guaranteed by the formula rather than by
tuning.

`4p(1-p)` over a ring of shadow taps is maximal where the taps DISAGREE and
zero wherever the neighbourhood is uniform. It is an edge detector. A point in
the middle of a leaf shadow reads zero; so does a point in the middle of a wall
shadow. What distinguishes a canopy is the DENSITY of edges over an area, and
disagreement measured at a point cannot see density - it can only ever trace a
boundary.

The correct property is a count, and it is topological rather than photometric.
Walk a ring of taps in angular order and total the absolute differences:

| Occluder | Total variation around the ring |
|---|---|
| Uniform, lit or shadowed alike | 0 |
| One straight edge, wherever the ring sits | 2 |
| N separate gaps | 2N |

A wall, a terrace lip, a cliff and a roof are each **one** edge. A canopy is
many, everywhere. Thresholding above 2 rejects every solid occluder by
construction rather than by picking a radius that happens to work, and it fills
regions instead of outlining them, because a ring sitting between two leaf gaps
still crosses both.

`vvCanopyStructure(radius)` is 12 taps on a closed ring - closed because a
feature at the seam would otherwise be counted once instead of twice, and the
seam is a fixed screen direction, so that error would be systematic rather than
noise. Banded at 2.6..6.0, so one edge scores nothing and three features
saturate. Multiplied by the ring's own mixedness to reject tangent clips.

It degrades honestly in one respect worth stating: a ring deep inside a solid
shadow with no gap within its radius returns 0. That IS the right answer -
sunflecks only exist where light gets through - but it means dapple will not
tint deep closed-canopy shade, and that is a look question still to be settled.

The ring radius is now the one genuine guess in the system, and it is a guess
about shadow map scale rather than about the world. Debug view 30 shows the
count at 3, 5 and 9 texels so it can be read off rather than argued about:
whichever channel FILLS the area under a crown is the right radius.

### The ring radius is now a slider, and the count is visible raw

Two rounds of guessing the radius from screenshots was enough. It is not a fact
about the world - it is a fact about the SHADOW MAP, namely how many texels
apart canopy gaps land, which depends on the player's shadow quality setting and
on how far they are standing. This code cannot know either, so
`PseudoPBR.CanopyRadius` exposes it and views 30 and 31 show what it counts.

`vvCanopyVariation` is split out from `vvCanopyStructure` for a specific
reason: when the gate shows nothing, "there is no broken shadow here" and "the
threshold band is above what a real canopy scores" look **identical** on screen
and need opposite fixes. View 31 bands the raw count into false colour - black
below 1, red for one edge, green for two features, blue for three or more - so
one screenshot separates them.

### Wetness has the same defect dapple had, and the game has the real answer

Raised by the question "why can't we use the game's own raindrop data?" The
answer is that we can, and `vvWetness` currently does not.

It gates on `smoothstep(vv_weatherRainCover - 0.12, ..., vv_sunExposure)`, and
its own comment admits it: "This is still a threshold on a soft signal rather
than a rain occlusion test. The game has a real one." Debug view 16 has since
shown that soft signal reads **~1 across an entire outdoor scene**, so with
`RainCoverThreshold` at 0.82 essentially everything outdoors passes as fully
rain-exposed. Porches, overhangs and doorways included. Same failure as the
dapple gate, same cause, not yet fixed.

The authoritative data, confirmed in the API source rather than assumed:

| API | What it is |
|---|---|
| `IBlockAccessor.GetRainMapHeightAt(x, z)` | "The topmost non-rain-permeable position at given x/z coordinate. **This map is always updated after placing/removing blocks**" |
| `IMapChunk.RainHeightMap` | The raw `ushort[]` behind it, per chunk column - the bulk-read path, far cheaper than per-column calls |
| `Block.RainPermeable` | The per-block flag the map is built from |

This is what the game itself uses to place rain splash particles, to decide
whether a torch is extinguished, and to accumulate snow. It is AUTHORITATIVE by
the ladder's definition: it directly describes the phenomenon.

One property makes it especially valuable here: leaves are rain-permeable, so
under a tree the rain map height is the GROUND, not the canopy. It therefore
separates "under a solid roof" from "under a canopy" exactly, which is the
discrimination the sun-exposure threshold cannot make at all.

Proposed shape, following `CloudTileReader`, which already does this for cloud
tiles and works:

- A window of columns around the player, read in bulk from `RainHeightMap`.
- Uploaded as heights RELATIVE to a base, packed to 8 bits, four per float,
  as a `Uniforms4` array - not a sampler. Adding a sampler to `chunkopaque.fsh`
  has twice cost the entire world render, and the project rule is to prefer a
  uniform array below roughly a thousand values.
- The fragment compares its own world Y against the height for its column.
  Above it: rain reaches. Below: it does not, by the game's own definition.
- Wrapping and registration follow the cloud reader's camera-relative pattern,
  because true world XZ against a wrapped origin is exactly the bug that made
  cloud shadows land nowhere for two rounds.

A 32x32 window is 1024 bytes, so 64 vec4s - the same order as the cloud window.
That covers 32 blocks, which is the near field where wetness detail is visible.

This also unblocks the ripples, which currently ride the same flat gate.

### The invented pattern is gone. Vanilla's shadow IS the fleck field

Radius measured in game: 6 texels is the smallest value that fills under a
crown rather than outlining it. That is now the default.

The observation that closed the design: "the red shaded areas seem to be areas
where the sun already hits... this feature should be aiming at letting rays of
light break through the foliage itself, and spot the ground in a tree's
shadow."

That is exactly right, and it exposed the last piece of the old architecture
still fighting the new one. A procedural fleck field was still generating the
pattern, gated by a term that had itself become the real canopy structure - two
descriptions of the same leaves, one of which has no access to where the
branches actually are. The invented one had to lose.

**Vanilla already renders the sunflecks.** View 25 proved the shadow map
resolves individual leaf gaps; the game draws those lit gaps onto the forest
floor every frame, with the real canopy's shape, at the real sun angle, moving
in the real wind. There was never a pattern to invent.

The model is now one measurement split by which side of the shadow test a
fragment fell on:

| | |
|---|---|
| broken **and shadowed** | canopy shade - deepened, and tinted green |
| broken **and lit** | a sunfleck - where a beam starts |

`vvCanopyEvidence` and `vvCanopySunfleck` are exact complements.

What is left for the mod is the part vanilla does not do:

- **Contrast.** Vanilla's shadow bottoms out at `1 - shadowIntensity/2`, about
  half brightness; a real forest floor is far darker than its sunflecks.
  Deepening the shade *between* the gaps, and leaving the gaps at exactly what
  vanilla lit them to, widens the difference. The flecks get brighter by being
  the only thing that did not get darker.
- **Colour.** Light that reached the shade came through leaves; light that
  reached a fleck missed every one of them. Only the first is green.

Darken-only is therefore not a safety compromise in this model - it is the only
operator with anywhere to go. A lit gap is already at full sun in vanilla's own
frame and has no headroom above it.

The godray shafts had the same flat `vv_sunExposure` gate and their own coarse
cell pattern, so beams started wherever sun light happened to dip. They now
start at `vvCanopySunfleck()` - ground the game actually lit, with broken canopy
around it - which is the definition of where a shaft comes from, and it means
the beams line up with the light on the floor because they are keyed to the same
fragments.

`vvSunflecks` and nine tuning constants are **deleted**, not disabled. Every
physical property the generator reproduced by hand - discrete rounded spots,
penumbra scaling with canopy height, elongation by 1/sin(elevation), jitter -
the real shadow has for free, because it is cast by real geometry at the real
sun angle. That is the whole argument for the rewrite.

Cost to watch: with both dapple and shafts on, a terrain fragment can take two
ring evaluations plus two visibility evaluations. Each is gated behind its own
strength slider, so 0 costs nothing, but this has not been profiled.

### Still open

The fine radius leaves a band about two texels wide along any straight shadow
edge. View 29 is where that shows. If those bands read as wide or crawl with the
camera, the next move is requiring agreement across two radii rather than
trusting one.

The flecks have not yet been demoted. They are still generating the pattern
where the gate opens, rather than filling in only what the shadow map cannot
resolve. That is the next stage and it is now unblocked.

### The intended architecture, once measured

```
vanilla shadow map        ->  macro canopy shadow      (authoritative, wind-animated)
        x
procedural sunflecks      ->  sub-texel breakup only   (what the map cannot resolve)
        =
direct sunlight term only ->  never ambient, block light, emission or fog
```

## 8d. Pixelated scene reflection

Reflective surfaces read the ACTUAL WORLD, reduced to one colour per material
texel. The analytic sky is now the fallback, not the model.

### The bridge

The problem the previous pass identified: the terrain shader knows the texture
grid but cannot see the scene - `chunkopaque.fsh` is a forward opaque pass and
the frame it would sample is the one it is still drawing. The post pass can see
the scene but has no idea which texel a fragment belongs to.

**The scene is carried across a FRAME instead of across a pass.** At
`AfterPostProcessing`, `SceneCaptureRenderer` copies the composed frame into a
half-resolution RGBA target - RGB the scene, **alpha linear view depth**. The
next frame's terrain pass samples it. Both the image and the grid are then in one
place, which is what makes a pixel-art mirror possible at all.

Everything needed turned out to be public API, which is why the previous pass's
"engine-internal texture IDs" blocker was wrong:

| Need | API |
|---|---|
| Scene colour | `IRenderAPI.FrameBuffers[(int)EnumFrameBuffer.Primary].ColorTextureIds[0]` |
| Depth | the same `FrameBufferRef.DepthTextureId` |
| Own target | `IRenderAPI.CreateFrameBuffer(FramebufferAttrs)` |
| Capture transform | `CurrentProjectionMatrix` x `CameraMatrixOriginf` |

**Depth comes from the depth attachment, never from `gPosition`.** That is what
resolves the SSAO problem: `outGPosition` is written inside `#if SSAOLEVEL > 0`
and does not exist for a player with SSAO off, while a depth buffer always does.
The feature is therefore independent of the SSAO setting - option C of the
brief's section 29, and the smallest safe change.

### One colour per texel, still by construction

The ray starts at `vvTexelCentrePos`, so every fragment inside a texture pixel
marches the **identical ray** and lands on the identical sample. Discreteness is
not a rounding step applied afterwards - it falls out of the geometry. Layer 2 of
the brief's section 40 needs no separate solution because Layer 1 was built at
the right origin.

### The march

Eight fixed steps, geometrically spaced, no refinement pass. The destination is
one colour for a whole texture pixel, so precision beyond "which surface did I
hit" buys nothing displayable - which is the entire reason this is far cheaper
than conventional screen-space reflection. A hit is accepted only if the captured
depth at that screen position matches the ray point's distance within
`VV_SSR_TOLERANCE`; anything else is a miss.

The camera moved between capture and use, so points are shifted by
`vv_reflectCameraDelta` before projection. Without it the reflection would slide
across every surface as the player walks - the crawl the pixel grid exists to
prevent.

### What falls back, and when

Rays that leave the screen, hit nothing, are occluded, or point back toward the
camera return no confidence and the analytic `vvReflectionFallback` shows
instead. The capture texture is allocated **ClampToEdge** so a stray read cannot
wrap to the far side of the frame and paint unrelated geometry onto a wall.

**Debug view 39 is the traffic light**: green means this pixel genuinely reflects
the world, red means fallback, blue means no capture at all.

### Off by default

It costs a framebuffer and one full-screen copy per frame whether or not anything
reflective is in view. `Reflections.SceneReflections` is false until asked for,
and every failure path - shader, framebuffer, engine texture ids - disables and
logs rather than throwing. The shader's validity uniform is 0 in all of those
cases, and 0 means the fallback, which is exactly what shipped before.

### Corrected after the first in-game test

Two screenshots and an external audit. The bridge was alive - view 39 showed
green, never blue - but the image was wrong, and there were four separate causes.

**The sampler was not surviving to the draw call.** Debug view 41 showed the
block atlas and pieces of unrelated textures where the captured frame should
have been. A texture unit is GLOBAL GL state, not per-program: binding once a
frame in `PbrShaderBinder.Upload` does not survive to the chunk draws, because
anything the game binds in between replaces it. The material atlases already had
this problem and `TerrainTextureBindInterceptor` exists to solve it; the capture
now rides the same per-draw path. **This also means every green pixel in view 39
was a false positive** - the march was finding alpha values in whatever texture
was on unit 13 and passing them off as depth.

**The camera reprojection had the sign backwards.** The shader holds a point as
`world - currentOrigin` and must hand the captured matrix `world -
captureOrigin`. Those differ by `currentOrigin - captureOrigin`; the binder
supplied the negative of that. The result moves every reflected point the wrong
way by TWICE the camera's travel - worse than having no correction at all, and
invisible in every view except a coordinate field, because a reflection of the
wrong part of the world still looks like a reflection.

The identity is now pinned numerically rather than by matching an expression, so
a rewrite that keeps the shape and flips the meaning still fails.

**The capture was bilinearly filtered.** Every lookup blended four captured
pixels, so the colour a texel received was an interpolation of things that are
not in the world - a blurry reconstruction wearing a pixel grid. Nearest now.

**The capture was too coarse.** A quarter in each axis was justified by "the
destination is 16x16", which confuses two resolutions: the destination decides
how many LOOKUPS there are, the source decides whether each lands on the right
thing. A block 30 pixels away got about eight source pixels to reflect a world
into. Half now.

### The march was shells, not a ray

Reported from a screenshot before it was understood: *"a circular checkerboard
of affected pixels"*, and *"the green in 40 mostly vanishes after moving"*.

Both are one defect. The march took eight samples and accepted a hit only if a
surface lay within 1.25 blocks of ONE of them - proximity to a sample, not
traversal of a ray. With geometrically growing steps the gaps outrun the
tolerance almost immediately:

| interval | gap | reachable |
|---|---|---|
| 0.35 - 1.0 | 0.65 | 100% |
| 2.2 - 4.4 | 2.22 | 100% |
| 4.4 - 8.5 | 4.10 | **61%** |
| 8.5 - 16.1 | 7.58 | **33%** |
| 16.1 - 30.1 | 14.03 | **18%** |
| 30.1 - 56.1 | 25.96 | **9.6%** |

So the march was a set of concentric SHELLS around the eye. Whether a texel
found anything depended on whether its ray length happened to land in one, and
ray length varies smoothly across a surface - which draws exactly the concentric
bands that were reported as a circular checkerboard. Walking changes every ray
length at once, so whole bands drop out together: the vanishing on movement.

**Adding steps could not have fixed this.** It makes the bands finer and more
numerous, and the coverage still collapses with distance.

Crossing detection fixes the class of error. A ray point is either in front of
the captured surface or behind it; the moment that flips, the ray passed through
geometry somewhere in the interval just marched. Twelve shells become twelve
intervals with **no gaps between them**, and two bisection passes locate the
crossing inside its interval. The thickness test then asks whether the ray
stopped at that surface or sailed past something thin - the classic screen-space
error where a reflection picks up what was hiding behind a fence post.

Reach is 39 blocks in 12 steps, fully covered, against 56 blocks in 8 steps that
were mostly unreachable.

The test pins the mechanism and computes the old scheme's coverage numerically,
so the regression has a number attached rather than a description, and so a
future "optimisation" back to proximity fails rather than looking tidier.

### The rings were the refinement, not the march

Second report of rings, this time with two extra clues that identified them:
*"the reflections only occupy an area around the player, and if standing on a
flat reflective plane the cutoff is visible"*.

A cutoff radius and rings are one cause. Bisection leaves `interval / 2^n`
between the refined point and the true surface; the thickness test then rejects
anything further behind the surface than `VV_SSR_THICKNESS`. So a hit survives
only where

```
interval / 2^n  <  VV_SSR_THICKNESS
```

The intervals grow geometrically, so with two passes:

| interval | length | residual | survives? |
|---|---|---|---|
| 2.8 - 4.3 | 1.44 | 0.36 | yes |
| 4.3 - 6.3 | 2.05 | 0.51 | yes |
| 6.3 - 9.2 | 2.91 | 0.73 | **no** |
| 27.6 - 39.4 | 11.83 | 2.96 | **no** |

Every hit beyond 6.3 blocks was found correctly and then **thrown away**. On a
flat plane, distance-dependent rejection centred on the viewer is a set of rings
with a hard edge where it stops - precisely what was on screen.

The widest interval is 11.8 blocks, so `11.8 / 2^n < 0.6` gives **n = 5**. The
number is derived, and a test recomputes it from the march constants and fails if
a future change to either breaks the inequality. Cost is five lookups, only on
rays that crossed a surface.

The thickness constant is now documented as a real geometric tolerance rather
than a knob: if it ever has to be raised to make distant reflections appear, the
refinement is too shallow and raising it would hide that.

### Metals got brighter: the capture is the finished frame

Reported alongside: metals reading much shinier, in daylight, with no torch.

The capture is the composed frame, which has already been colour graded, bloomed
and exposure adapted. Reflecting it verbatim applies all of that a SECOND time
inside the reflection, and a bright sky then pushes a metal past anything the
ambient term it replaces could have produced - the white-metal failure returning
through the new path rather than the old one.

Capped by luminance against the environment colour, at the same
`VV_REFLECT_MAX` the fallback uses. Scaled uniformly rather than clamped per
channel, because a per-channel clamp desaturates exactly the bright reflections
that carry the most information: a reflected tree has to stay green.

### The march is now in screen space, and the depths are in one space

Two defects, both found from a description rather than from code.

**The smear.** Reported as *"the trunk is reflected all the way from the base of
the tree to where the reflection ends at my feet, instead of rendering a
simulated reflection of the tree properly in perspective"*.

That is overshoot. The march stepped in WORLD space, and world-space steps
project to wildly different screen distances - near the camera one step can leap
across the frame, far away a hundred land in a single texel. Every ground point
whose ray leapt over the trunk registered its crossing at the same few trunk
texels, so a whole band of ground sampled one colour instead of sampling
progressively higher up the tree.

The march now steps a fixed number of CAPTURE TEXELS, with the step count
derived per ray from how far it travels across the image. Uniform sampling in
the image is what makes a reflection foreshorten. Depth is interpolated as 1/w,
which is what is linear across a screen - interpolating depth directly bends the
ray and puts every hit at the wrong distance.

**The depth spaces disagreed.** The capture linearises the depth buffer, which
yields AXIAL view-space z. The march compared that against `length()`, which is
RADIAL distance. They differ by 1/cos of the angle from the view axis:

| off axis | radial overstates axial by |
|---|---|
| 10 degrees | 1.5% |
| 20 degrees | 6.4% |
| 30 degrees | 15.5% |
| 40 degrees | 30.5% |

The error is radially symmetric about the screen centre, so it drew its own set
of rings on top of every other problem. Ray depth is now `clip.w`, which IS
view-space z, taken from the same projection - the two numbers come from one
space rather than being reconciled by a tolerance.

The test for this previously read march constants that the rewrite removed, so
it returned early and silently asserted nothing. Missing constants are now a
failure rather than an exit.

### Wet blocks already work. Water does not.

**Rain-wet surfaces need no further work.** `vvApplyEnvironmentLayers` sets
roughness to 0.08 and the specular mask to 0.60 when wet, and both feed the same
`f0` and `roughness` the reflection uses - so a wet block reflects the world more
strongly than a dry one through the existing material path, with no reflection
specific wetness code. That was the design intent and it holds.

**Water is not patched at all.** `chunkliquid.fsh` appears in no patch group;
the mod has never touched it. Giving it scene reflections means a new patch
group on a shader with its own animated normals and its own lighting, which is a
second surface to debug at the same time as the first. The reflection geometry
should be right on terrain before water inherits it - otherwise a wrong
reflection appears in two places and it is harder to tell which one is lying.

### Not changed, and why

The audit also suggested using `CameraOffset` rather than the player position as
the capture origin. That would introduce an error rather than remove one:
`CameraMatrixOriginf` is documented as *"player camera matrix with **player**
positioned at 0,0,0"*, and `chunkopaque.vsh` builds its `worldPos` the same way -
`xyz + origin`, with origin chunk-relative-to-player. Both ends already agree on
the player as the origin and the camera's own offset is baked into the matrix. A
test pins this so it is not "fixed" later.

Ray traversal and depth precision are deliberately untouched. The audit is right
that tuning them before the coordinate system is correct means tuning against a
broken transform.

### Limits

- **NOT SEEN ON SCREEN. L2.** Every claim here is static: shaders compile in all
  48 prefix combinations and 578 checks pass. Whether a reflective block visibly
  shows a tree is unverified.
- **One frame stale.** The price of the bridge. A quantised reflection tolerates
  it far better than a smooth one, but fast camera motion will show it.
- Screen-space limits apply and are not bugs: nothing behind the camera, off the
  frame, or hidden behind nearer geometry can be reflected.
- **Entities: unsupported.** No material atlas, so no texel grid.
- **Water: untouched.** It has its own shader path and was not patched.
- Not profiled.

## 8e. Documentation closure, and what the checks cannot do

The reconciliation pass installed `DocumentationChecks`. A closure pass over it
found three more stale claims it did not catch, because they are PROSE rather
than paths or phrasing about directories:

| What the document said | Reality |
|---|---|
| `src/PseudoPBR/README.md` said "`chunktopsoil.fsh` - not started" | Patched by `pseudopbrtopsoil`, 9 anchors, all 24 combinations |
| `src/PseudoPBR/README.md` said "Roughness modulates SSR blur - not started (needs Phase 3)" | Roughness controls reflection COARSENESS. Blur is a rejected concept (D11), and "Phase 3" referred to a roadmap that no longer exists |
| `CHANGELOG.md` said "Weather, reflections and the in-game PBR pipeline are not implemented" | All three have shipped |

The check now takes evidence from the patch YAML: a shader named there IS
patched, and a document saying otherwise on the same line is wrong.

**A false positive worth recording.** The new check immediately failed on
`DECISIONS.md`, which quotes those exact stale claims as examples of what went
wrong. Recording a past error is not making it. The checks now distinguish the
two by tense - "is empty" is an assertion, "was an empty directory" is a report -
which is a heuristic, and a deliberately narrow one. Someone writing carelessly
in the past tense about a present state would slip through.

**What these checks still cannot do**, and what therefore stays a human job:

- They cannot tell whether prose is TRUE. Only whether it still refers to things
  that exist and quotes constants correctly.
- They cannot verify a verification level. Nothing stops a future pass writing
  L4 next to work that has never been seen; only the definition of done in
  `CLAUDE.md` does, and that is a rule rather than a check.
- They cannot detect an omission of judgement - a limitation that is real and
  simply not written down.

Those belong in `docs/CHECKLIST.md`, which is explicitly a record of evidence
rather than a record of intent.

## 9. Open problems

Things known to be wrong or unverified right now. Each should be closed or
re-explained rather than quietly dropped.

### Closed: the specular lobe had no energy limit

Reported as "an ugly disco ball pattern moving rapidly across ALL surfaces,
even the inside of caves", and separately as "water drops and wetness effects
are no longer working". One cause, one commit, three symptoms.

`vvDistributionGGX` had its denominator clamp moved from `1e-5` to `1e-12` in
5800f56, on the reasoning that a divide-by-zero guard should not be truncating
real values. The reasoning was checked at roughness 0.2, where the two agree to
within a factor of one. They do not agree anywhere else:

| roughness | peak with `1e-5` | peak with `1e-12` | ratio |
|---|---|---|---|
| 0.04 (the material floor) | 0.26 | 124339.8 | 485702x |
| 0.08 (wet stone) | 4.10 | 7771.2 | 1896x |
| 0.15 | 50.62 | 628.8 | 12x |
| 0.20 | 160.00 | 198.9 | 1x |

GGX integrates to one over the hemisphere, so its peak rises as
`1/(pi*alpha^2)` without limit. The `1e-5` clamp was not a divide-by-zero
guard at all - it was the renderer's **only** specular energy limit, and
removing it let a plain dielectric reach 1119x sunlight.

The same commit did worse. It added `vvDistributionGGXAnisotropic` with no
equivalent clamp, and `PseudoPBR.GrainAnisotropy` defaults to **0.6**, so the
branch condition `vv_pbrGrain > 0.001` sends **every terrain fragment in the
world** down the uncapped path regardless of whether the surface has any grain.
The isotropic edit and the anisotropic omission each independently uncapped the
whole world.

Why the three symptoms:

| Symptom | Mechanism |
|---|---|
| Sparkle everywhere | A lobe at alpha 0.0016 is about 0.09 degrees wide - narrower than a pixel. It cannot fade in and out as the view moves, so it pops one fragment at a time |
| Inside caves | `vvBlockLightSpecular` deliberately ignores the shadow map and daylight, because a torch burns at midnight. It calls the same lobe |
| Wetness stopped reading | Wet stone is roughness 0.08. It went from a peak of 4.1 to 7771 - past darkening, past sheen, into flat white |

Fixed by flooring **alpha**, not the denominator: `VV_GGX_MIN_ALPHA = 0.04`,
applied in both forms before the anisotropic split so parity still holds
exactly, and applied before the split so `ax * ay` stays `a * a` and the bound
holds at every anisotropy. 0.04 is the largest floor that leaves roughness 0.21
and above bit-identical to the build before 5800f56; below it the peak holds at
199, about 1.8x sunlight for a dielectric, instead of running away.

Two things this deliberately does not restore. The old clamp capped the peak at
`1e5*roughness^4`, so a **smoother** surface got a **dimmer** highlight, falling
to nothing as it approached a mirror - which is backwards, and is why wet stone
at `VV_LAYER_WET_SPECULAR = 0.60` had never once produced a sheen. Wet surfaces
will now be visibly shinier than in any build tested so far. That is what the
wet constants were written for, but it has never been seen, and it is the first
thing to look at.

The lesson is in the test, not the constant. `CheckAnisotropy` already asserted
the lobe was **finite**, and 124339.8 is finite. `CheckLobeIsBounded` now
asserts it is bounded and that a smoother surface is never dimmer, with the
bound as a literal so that lowering the floor cannot lower the bound along with
it. All three mutations - dropping the floor, lowering it, and restoring the
old denominator clamp - were confirmed to fail the suite before this was
committed.

### Closed by the pre-test proofing pass

Seven defects found by inspection, not by symptom - none of them would have
announced itself in a log, and five would have looked like "the feature does
nothing" on screen.

| What was wrong | What it cost |
|---|---|
| `UploadScene` returned early unless the program had `vv_sceneRestraint` | Only `pseudopbr.glsl` calls `vvSceneVisibilityDampen()`, so only terrain reads that uniform. Entities and particles got **none** of the scene block |
| `vv_sceneDayLight`, `vv_sceneWetness`, `vv_sceneOvercast` uploaded ad hoc by the terrain and entity paths | Particles never received them. Unset reads as 0, 0 multiplies the visibility term, particle specular was dead |
| Every other upload in `PbrShaderBinder` was unguarded | `particlesquad` never calls the lobe, so the compiler removes six uniforms the binder wrote to every frame |
| `ColorGradeSubsystem` null-checked the tracker then dereferenced it twice more | Crash on world exit, where the tracker is unregistered before the subsystems |
| `PseudoPBR.DebugView` clamped to `0..10`, slider went to `0..14` | Debug views 11-14 (ripples, crevice, foliage, emission) unreachable; the slider moved and the next load silently reset it |
| `vvApplyParticleGlow` seeded its flicker with `vec3(0.0)` | Every spark in the world flickered in step, reading as one pulsing fire |
| `vv_sceneAutumn`, `vv_sceneWinter`, `vv_sceneEnclosure` declared, uploaded, never read | GLSL removes an unread uniform, `HasUniform` then says absent, and the binder writes to a name the program lacks - once a frame, forever |

Each has a check behind it now: a combined-group pass in `verifypatches` that
applies every group to a shader together as the game does, an `UploadScene`
contract check in both directions, a slider-range vs config-clamp check, and a
dead-code scan over the shipped GLSL. Three of the five were verified to bite
by reintroducing the bug.

**None of this is L4.** It raises the odds that a visual test is testing the
feature rather than a wiring mistake; it does not say anything looks right.

- **Cloud shadows now read the game's own clouds.** Confirmed in game:
  `weather: cloud shadows on Chunkopaque ... source the game's own cloud tiles`.
  Four rounds to get there, and the causes were, in order: the tile path was
  compared against a wrapped camera position and contributed nothing anywhere;
  the renderer was never found, first by a one-level field walk and then by a
  breadth-first search that ran out of budget; the failure was disguised by an
  invented noise field; and the install was carrying two patch groups that had
  been deleted from the repo. All four are fixed. The renderer is now found by
  type name across loaded assemblies and captured by a Harmony postfix on its
  own per-frame method.
- **What the renderer actually reports** (v1.22.7 with the FluffyClouds mod
  installed, `FluffyClouds.CloudRendererMap`):
  `CloudTileLength 81` (a 6561-tile square array, so the 16-tile window is a
  centre crop), `offset.y 256.5` - the cloud altitude, now read rather than
  guessed - and `windOffsetX/Z`, which accumulate and are almost certainly the
  sub-tile drift the window still does not account for.
- **The field stepped four times a second**, and it was the throttle. The
  reflective tile read is throttled to 4 Hz, which is ample for data that
  changes as slowly as a cloud - but the window's CORNER was being recomputed
  inside that same throttle, and the corner is what keeps the shadows attached
  to the world rather than to the camera. Between reads it was stale, so the
  whole field slid along with the player and snapped back on the next read.
  The corner costs two divisions and now runs every frame; only the read is
  throttled. The field also eases toward each reading over a third of a second,
  which absorbs both the 4 Hz steps and any residue at a tile boundary.
- **Shadows sat too close under their clouds all morning and evening.** Two
  compounding faults. The throw was capped at 320 blocks, and the cap was
  applied to `climb/sin(elevation)` while the quantity being capped was
  `climb/tan(elevation)` - a different and always larger number, so the
  effective limit moved with the sun and bit hardest exactly when shadows should
  have been longest. And the 16-tile window was only 800 blocks across, so a
  20-degree sun threw its shadows 400 blocks, straight into the edge fade. The
  ray is now written as an explicit run and direction with the cap on the run
  itself, and the window is 24 tiles - 1200 blocks, 144 vec4s, still inside the
  1024 fragment uniform components OpenGL 3.3 guarantees.
- **The window corner is still assumed**, not read: the player snapped down to
  the 50-block grid. `windOffsetX/Z` are the missing term, and their sign is not
  derivable from outside a running game. Expect shadows to step by up to a tile
  as the clouds drift rather than sliding smoothly. Cloud diagnostic view 2
  settles it.
- **Occlusion is no longer vanilla's draw alpha.** Copying
  `min(1, cloudOpaqueness * min(1, 10 * selfThickness))` from `clouds.vsh` was
  using the game's own answer to the wrong question: that expression saturates
  on purpose, because it exists to make a cloud look solid from underneath, and
  it is fully opaque at a tenth of full thickness. Measured against a real sky
  it gave **mean 0.646 with 64% of tiles past half coverage** - the whole world
  in shadow all day, with no edges left to read as shadows, which is what
  "weirdly obsessive amount of cloud coverage" was. How much sunlight a cloud
  stops is optical depth, not draw opacity, so the field is now Beer-Lambert:
  a wisp takes a little light, a thick cloud takes most, and the gradient
  between is what makes a shadow legible crossing a field.
- **Rain ripples swam across the ground with the player** - the same bug as the
  cloud window, in a different sampler. A shader only sees camera-relative
  coordinates, so the ripple field rebuilds a world position as
  `cameraRelativePos + vv_pbrOrigin`. The first term changes every frame; the
  second was sampled on `EnvironmentTracker`'s 0.1s tick, so the sum drifted
  with the player and snapped back ten times a second. The camera is now
  sampled every frame ahead of the tick gate - two doubles and a modulo, and
  `EnvironmentState` is a readonly struct so rebuilding it allocates nothing -
  while the light-level lookup beside it, a chunk query, stays on the tick.
  This is the THIRD time an anchor has been throttled along with the sampler
  around it, so `tools/smoketest` now pins both places by call order rather than
  by presence: the anchor must come before the gate.
- **Ripples are now snapped to the block texture's texel grid.** A perfectly
  round anti-aliased ripple on a 32-pixel texture reads as belonging to a
  different renderer. Ripples only land on up-facing surfaces and a block is one
  unit across, so world XZ quantised to 1/32 is exactly the top face's texel
  grid. Space only - the ring still expands smoothly, only its edge is made of
  pixels. The second octave's offset was also a whole number, which put its
  one-block cell boundaries exactly on the half-block grid's, so both octaves
  broke along the same lines and reinforced the lattice the second one existed
  to hide.
- **Sunlight dapple, fourth pass, L2.** Third pass came back as "a strobing
  disco ball effect basically everywhere". Two separate faults, neither of them
  the animation.
  The strobe was **aliasing**. A fleck is about half a block across, so past
  some distance several fall inside one pixel and which one is sampled depends
  on exactly where that pixel lands; move the camera slightly and the answer
  changes, every pixel independently. Slowing the blink could never have helped,
  because the blink was not what was moving. Flecks now dissolve toward "no
  light removed" between 22 and 44 blocks. Faded on distance rather than
  `fwidth` deliberately: the field sits inside a gate that early-outs on open
  ground, which is divergent control flow, and a quad's helper lanes may have
  taken the other branch - so the derivative is least reliable exactly at the
  edges of the effect.
  "Everywhere" was the gate's upper rolloff. It ran `smoothstep(0.99, 0.72,
  exposure)`, so ground reading even 0.95 still passed a sixth of the effect -
  across every open field in the world that is not a leak, it is the effect
  being on. Now 0.97 to 0.62, where 0.95 passes one part in a hundred.
  Default strength also cut from 0.55 to 0.35 and the shade depth from 0.34 to
  0.28.
- **Sunbeams through the canopy, L2.** Built on vanilla's own god-ray pass
  rather than beside it: `outGlow.g` is the mask `godrays.fsh` radially blurs
  from the sun's screen position, terrain barely writes to it, and backlit
  leaves plus lit sunflecks are written into it for the cost of one number per
  fragment. Gated on the camera facing near the sun, which is also where the
  beams get their dependence on the real sun position. Inherits the player's
  god-ray graphics setting and is simply absent when that is off. Debug view 17
  shows the mask.
- **Fleck blink is now driven by the world's wind speed** rather than a fixed
  clock, so still air is nearly still and a gust makes the canopy flicker.
- **`vv_sunExposure`'s actual range has still never been measured.** The dapple gate
  assumes 1 under open sky and clearly less under canopy. If leaves absorb only
  a little, "clearly less" might be 0.9 and the gate barely fires; if open
  ground does not reach 1, it leaks onto every field in the world. Debug view 16
  draws it raw - one screenshot in the open and one under a tree settles it.
- **The season phase is assumed, not verified.** `depth` is derived by
  splitting `GetSeasonRel` into quarters and assuming the quarter it lands in
  is the season `GetSeason` names. If the year does not start where that
  assumes, depth peaks at a season handover rather than mid-season - inverted,
  not merely offset. Logged on every season change with the numbers needed to
  tell, since it is not a crash and only a running game can answer it.
- **`colorgrade`'s reference dump is still dirty**, though the group itself is
  now verified. The dump in `reference/game-shaders/final.fsh` was taken with
  the mod running, so `verifypatches` refuses it by name. Its four injections
  are individually reversible, so vanilla was reconstructed by hand - strip the
  prepended snippet, un-annotate `out vec4 outColor;`, rename `vvSceneMain`
  back to `main`, drop the appended wrapper - and against that reconstruction
  all four anchors match and the result compiles in every combination. That is
  a real L2 for the group. It is not a substitute for a clean dump, because the
  reconstruction is only as good as our memory of what we injected; re-dump
  with `ColorGrade.Enabled`, `PseudoPBR.Enabled` and `Weather.Enabled` all
  false when convenient.
- **`TonemapStrength` ships at 0.0** and is the one part of colour grading never
  confirmed on screen.
- **Nothing in Weather past wetness has reached L4.** Five effects sit at L2.
- **Adaptive grading has never been seen in game.** 34 rule checks pass; that
  says the arithmetic is right, not that the look is.
- **Nothing from the entity/foliage/crevice pass has been seen on screen.** Three
  features, all L2. The entity group is the first patch this mod has ever made
  to a non-terrain shader, so it is also the first time a failure there could
  cost every mob in the world rather than the ground.
- **Dynamic block lighting reads as unnoticeable in play** — the debug view
  shows it working. Either the effect is too subtle or the debug view is
  flattering it.

## 10. Log of abandoned ideas

Kept so they are not proposed again without new information.

| Idea | Why it is gone |
|---|---|
| Reshaping volumetric clouds in the cloud shader | Cloud shape comes from a CPU tile array; `octave()` is unreachable in normal play |
| Scaling cloud density by a uniform | Zero must mean vanilla; here zero meant no clouds at all |
| Fogging the sky dome with weather | Flattens cloud/sky contrast into haze in both renderers |
| Transpiler-based Harmony patches | Too brittle against `VintagestoryLib`; prefix/postfix only |
| Reading metalness from pixels | Pixels cannot answer "is this metal"; `EnumBlockMaterial` can |
| A second sampler in `chunkopaque.fsh` | Twice cost the entire world render. Uniform arrays below ~1000 values |
| Ripple clock from `windWaveCounter` | Unbounded float32 clock collapses every phase to one value |

---

## 11. Ranked backlog

Revised after a review of the project against its own stated goal. The previous
ranking was ordered by "idea quality x visual payoff"; this one is ordered by
the [visual hierarchy](VISUAL-LANGUAGE.md#3-visual-hierarchy), because ranking a
cinematic effect beside a material-readability effect is a category error and
the first version of this list made it.

Four entries stay demoted because the game already ships them: **bloom**
(`bloomParts` in `final.fsh`, plus `Findbright`/`Blur`), **SSAO** (`SSAOLEVEL`),
**godrays** (`GODRAYS`) and **per-vertex vegetation wind** (`windWaveCounter`).
Building any from scratch is a duplicate, not a feature.

### Tier S - foundational

| # | Feature | Why |
|---|---|---|
| 1 | **Finish PBR reach** - entities `[x]`, foliage `[x]`, particles `[x]`, **held items still open** | A surface lit by a different model than the surface beside it is the one defect that devalues every other lighting feature. Held items are what remains, and are the weakest of the four: `helditem.fsh` has no `worldPos`, no `blockLight` and no `rgbaFog` |
| 2 | **Scene intent, budgets, contribution records** `[x]` | Done. Elevated from implementation detail to product rule - see VISUAL-LANGUAGE.md §4 |
| 3 | ~~**Emissive materials**~~ **(built, L2)** | Moved up four phases. This game is *about* fire: forges, bloomeries, firepits, lamps, lava. A forge should read as a hot object lighting a room, not as an orange texture, and light sources matter disproportionately to a survival game built on darkness and shelter |
| 4 | ~~**Gameplay readability**~~ **(built, L2)** | Weather now gives up fog while something is near. It will not outline a threat - only decline to hide one |
| 5 | ~~**Visual test scenes**~~ **(written)** - [VISUAL-TESTS.md](VISUAL-TESTS.md) | A process feature ranked as high as a rendering one, because L4 is the only level that closes anything and it is currently "a developer looked at the game" |

### Tier A - major visual work

| # | Feature | Why |
|---|---|---|
| 6 | Atmospheric renderer | Aerial perspective, haze, horizon, sun and moon attenuation. Atmosphere and weather are **one** system, not a weather modifier bolted to vanilla fog |
| 7 | Environmental material layers | Snow, frost, wetness and season as a layering model with a fixed precedence, not four independent effects. See VISUAL-LANGUAGE.md §7 |
| 8 | Water as a **material**, not as SSR | Fresnel, depth colouration, normals, rain disturbance, underwater. SSR is one possible implementation component of the last 10%, not the feature |
| 9 | Vegetation as thin living material | Translucency `[x]`, leaf wet response `[x]`, layering `[x]`, seasonal framework `[x]`. Remaining: a consumer for autumn, and wind only where vanilla's is visibly short |
| 10 | Occlusion hierarchy | Crevice `[x]` -> contact (blocked on depth) -> SSAO (vanilla has it) |

### Tier B - worthwhile

Fire and light interaction (part of emissive, worth naming separately) ·
sky and cloud interaction · weather transitions as one
coordinated event · moonlight · snow and frost environmental interaction ·
exposure responding to fire and lightning · persistent `MaterialDefinition` ·
material authoring overrides and a material API for other mods · compatibility
diagnostics · rendering debug HUD · height fog · rain streaks · split-toning ·
shared scene vocabulary `[x]` · effect provenance reporting

### Tier C - optional polish

Restrained bloom (driving vanilla's) · temporal accumulation · dynamic
resolution · depth of field · environmental camera effects

### Tier D - probably never

Chromatic aberration · aggressive vignette · film grain · lens dirt · motion
blur · excessive bloom · parallax mapping · generic cinematic filters.

Not because they cannot be built. Because
[VISUAL-LANGUAGE.md §8](VISUAL-LANGUAGE.md#8-what-this-project-does-not-build)
says the test is whether the game still looks like itself while being *played*,
and these are the fastest way to fail it.

### On SSR specifically

Ranked last as a *technique* and reframed as part of Tier A's water system. This
game's hard block geometry, low-poly forms and sparse scenes make screen-space
artefacts unusually visible, and a bounded environment reflection is likely to
beat a technically correct one. Order to attempt: Fresnel, sky/environment
reflection, depth colouration, roughness, normal distortion, bounded reflection
impression - and true SSR only if testing shows it genuinely improves the image.
