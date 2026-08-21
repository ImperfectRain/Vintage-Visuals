# Status

The single tracker for every feature and idea in Vintage Visuals: what is
finished, what is half-finished, what is planned, and what was tried and
abandoned.

**This file is updated in the same commit as the work it describes.** A feature
that changed state and did not move here is a feature that will be forgotten or
re-litigated. See [WORKFLOW.md](WORKFLOW.md).

Layers and the rules about what may depend on what are in
[ARCHITECTURE.md](ARCHITECTURE.md). Phase ordering and rationale are in
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md). This file is the state.

---

## How to read the levels

Nothing that touches GLSL or the render pipeline can be verified by building, so
every row carries the level it has actually reached:

| | |
|---|---|
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
| `[~]` | Per-layer debug views (0–13) | L2 | adds 12 crevice occlusion, 13 foliage transmission |
| `[x]` | Offline `tools/pbrgen` prototype + parity fixture | L4 | 31 Python tests; smoketest asserts the C# port agrees |
| `[~]` | **Lighting reach: entities** (`pbrentity` group) | L2 | mobs, animals and players get the same lobe. Default material, not a derived atlas - see below |
| `[ ]` | Lighting reach: held items | — | `helditem.fsh` has no `worldPos`, `blockLight` or `rgbaFog`; much thinner, much less to gain |
| `[x]` | One shared lighting snippet (`pbrcore.glsl`) | L2 | injected into all three programs; a smoke check asserts Cook-Torrance is defined exactly once |
| `[~]` | Crevice shading from the material normal's curvature | L2 | divergence of the stored gradient - a real cavity estimate, not an edge detector |
| `[ ]` | Screen-space contact shadows | — | **blocked.** Needs the depth buffer, which is being written during the opaque pass, not readable |
| `[ ]` | Separate local contrast from albedo level before the Sobel pass | — | "dark is deep" is the known weakness; belongs in `tools/pbrgen` first |
| `[ ]` | Parallax / relief mapping | — | speculative; may fight the art direction |

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
| `[~]` | **Snow as a material transformation** | L2 | thin dusting on sky-exposed up-faces, cubed against the normal. Real accumulated depth is still vanilla's snow blocks |
| `[x]` | **Frost as an environmental layer** | L2 | built on vanilla's own `frostAlpha`/`fragFrostAlpha`. Vanilla tints; this adds the material body |
| `[ ]` | Rain streaks running down vertical wet faces | — | cut from the ripple commit on purpose, unverifiable blind |
| `[ ]` | Puddles pooling in depressions | — | needs a height signal the terrain shader does not have |
| `[ ]` | Weather transitions as first-class state | — | today each effect eases independently |
| `[-]` | Volumetric cloud shaping (`cloudshape` group) | — | **abandoned.** `octave()` is only reachable from `warp()`, which early-returns unless a perception effect is active. The slider did nothing |
| `[-]` | Cloud density scaling | — | **abandoned.** Unset uniform reads 0, and 0 meant *no clouds at all* |
| `[-]` | Fogging the sky dome (`weathersky` group) | — | **abandoned.** Flattens cloud against sky into a uniform haze in both renderers |

## 6. Atmosphere

Currently a rain modifier inside Weather rather than a system. The target is
`Atmosphere (always present) + Weather modifiers -> final atmospheric state`, so
a future weather type inherits the rendering instead of adding a special case.

| | Feature | Level | Notes |
|---|---|---|---|
| `[ ]` | Aerial perspective / distance haze | — | |
| `[ ]` | Sky scattering and horizon colouration | — | |
| `[ ]` | Sun and moon attenuation through atmosphere | — | |
| `[ ]` | Height fog | — | |
| `[ ]` | Cloud attenuation of direct sunlight | — | partly done as the overcast term; belongs here |
| `[ ]` | Godray interaction | — | vanilla has `GODRAYS`; untouched |

## 7. Water and reflections

`src/Reflections/` is an empty directory. Nothing here has been started.

| | Feature | Level | Notes |
|---|---|---|---|
| `[ ]` | Fresnel-weighted surface response | — | |
| `[ ]` | Screen-space reflections | — | |
| `[ ]` | Refraction | — | |
| `[ ]` | Animated wave and micro-normals | — | |
| `[ ]` | Depth colouration | — | |
| `[ ]` | Rain disturbance of the water surface | — | the ripple field already exists and should be reused |
| `[ ]` | Underwater: absorption, attenuation, caustics, distortion | — | |

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

## 9. Open problems

Things known to be wrong or unverified right now. Each should be closed or
re-explained rather than quietly dropped.

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
