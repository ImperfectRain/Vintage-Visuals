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
| `[ ]` | Emissive metadata (colour, strength, flicker, temperature) | — | prerequisite for §7 |
| `[ ]` | Camera state (velocity, FOV, underwater depth) as shared state | — | needed by motion effects and underwater |

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
| `[~]` | Surface relief in `chunktopsoil.fsh` (forest floor) | L2 | anchors confirmed against the real shader |
| `[x]` | Cook-Torrance: GGX + Smith-Schlick + Schlick Fresnel | L4 | energy-conserving |
| `[~]` | Geometric specular antialiasing | L2 | roughness widened in alpha from screen-space normal derivatives |
| `[~]` | Sky/ambient specular from `rgbaFog` | L2 | |
| `[~]` | Block-light specular with recovered direction | L2 | Mikkelsen surface-gradient of the light field |
| `[~]` | Per-layer debug views (0–11) | L2 | 11 modes: normal, roughness, spec, relief, highlight, world normal, reflectance, shaded roughness, block-light dir, wetness, ripples |
| `[x]` | Offline `tools/pbrgen` prototype + parity fixture | L4 | 31 Python tests; smoketest asserts the C# port agrees |
| `[ ]` | **Lighting reach: entities and held items** | — | *highest-value gap.* A mob on PBR-lit ground is shaded by a different model than the ground |
| `[ ]` | One shared lighting snippet across the three programs | — | three copies of Cook-Torrance is not the answer |
| `[ ]` | Contact shadows from the material normal/height | — | cheapest depth cue available, needs no new buffer |
| `[ ]` | Crevice shading (small-scale AO from the derived height) | — | third scale of occlusion, distinct from SSAO |
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
| `[ ]` | **Snow as a material transformation** | — | smoother normal, higher roughness, lighter albedo, accumulated height on sky-exposed up-faces |
| `[ ]` | Frost as a second environmental layer | — | `base material + wetness + snow + frost` is the target abstraction |
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
| `[ ]` | **Emissive materials** | `EmissionColor/Strength/Flicker/Temperature` so a forge produces illumination, metal highlights, warm reflections and glow rather than an orange texture |
| `[ ]` | **Vegetation wind** | per-class amplitude: grass, crops, leaves, bushes |
| `[ ]` | **Foliage translucency** | backface lighting, leaf-specific roughness, softer shadows |
| `[ ]` | Seasonal foliage response | autumn colour, frost, snow load, wet foliage |
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

- **Cloud shadows have never been seen working.** Reported invisible three
  times, then reported as three over-large patches unrelated to the sky. Now
  reading the game's own tile array via reflection. **Two things cannot be ruled
  out blind:** the array may be indexed `[x*N+z]` rather than `[z*N+x]` (shadows
  mirrored across the diagonal), and it may scroll with an offset rather than
  staying camera-centred (a constant shift). The reader logs what it found;
  `Weather.CloudShadowDebug` shows the field alone at full strength.
- **`colorgrade` cannot be verified against the real `final.fsh`.** The
  reference dump in `reference/game-shaders/` was taken with the mod running, so
  `verifypatches` refuses it. Needs a re-dump with `ColorGrade.Enabled`,
  `PseudoPBR.Enabled` and `Weather.Enabled` all false.
- **`TonemapStrength` ships at 0.0** and is the one part of colour grading never
  confirmed on screen.
- **Nothing in Weather past wetness has reached L4.** Five effects sit at L2.
- **Adaptive grading has never been seen in game.** 34 rule checks pass; that
  says the arithmetic is right, not that the look is.
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

The 44 unstarted entries above, ordered by **idea quality x visual payoff in
game**. Cost and risk are noted but are not what the order is built on - a cheap
idea that changes nothing still ranks low.

Four entries were demoted after re-reading the vanilla shaders: the game already
ships **bloom** (`bloomParts` in `final.fsh`, plus the `Findbright` and `Blur`
programs), **SSAO** (`SSAOLEVEL`, and `ssao.fsh`), **godrays** (`GODRAYS`,
`godrays.fsh`) and **per-vertex vegetation wind** (`windWaveCounter` and
`windWaveCounterHighFreq` in `chunkopaque.vsh`, with its own bend counters).
Building any of those from scratch is not a feature, it is a duplicate.

### Tier S - the four that would change the mod

| # | Feature | Why |
|---|---|---|
| 1 | **Emissive materials** | Vintage Story is a game about fire. Forges, bloomeries, firepits, torches, lava, lamps are all bright *textures* today. Making them light sources with colour temperature and flicker transforms night and interiors - the two places players spend most of their time. One system also feeds bloom, reflections and exposure, so it pays for four |
| 2 | **Lighting reach: entities and held items** | Not glamorous; it is a correctness fix. A mob standing on PBR-lit ground is shaded by a different model than the ground. Every future lighting feature is worth half until this lands. This is what makes the material system a pipeline rather than a terrain effect |
| 3 | **Foliage translucency** | Sunlight through leaves is the single strongest cue that a renderer is modern, and this game is *full* of foliage. Vanilla's wind deformation does not touch shading, so the gap is real. Technically it is a wrap-lighting term on the lobe that already exists, gated on a leaf class already classified |
| 4 | **Contact shadows + crevice shading** | One idea at two scales, and worth more here than in most games: a blocky world has little geometric detail to carry form, so occlusion does that work instead. Needs no new buffer - the material system already produces the normal and implied height. This is the gap vanilla's SSAO does *not* cover |

### Tier A - strong

| # | Feature | Why |
|---|---|---|
| 5 | Snow as a material transformation | Best abstraction on the list, and wetness already proves it works. Caveat that keeps it out of S: the game renders snow blocks and layers itself, so this has to be about what vanilla does *not* cover - roofs, leaves, partial melt, the transition - or it duplicates the game |
| 6 | Aerial perspective / distance haze | Huge payoff for scale, and the foundation for atmosphere as a system rather than a rain modifier. Docked slightly because vanilla has fog already, so much of it is a re-grade, and because "the whole world is hazy" is a failure this project has already shipped once |
| 7 | Underwater absorption, attenuation, caustics, distortion | Underwater currently reads flat. Caustics are high-impact and cheap. Self-contained: almost no risk to the surface render |
| 8 | Water: Fresnel + depth colouration + waves | The cheap two thirds of water, and most of what makes water read as water. None of it needs SSR |
| 9 | Sky scattering and horizon colouration | Sunrise and sunset are when people take screenshots. Held back by a hard-won lesson - patching the sky dome went badly once - and by overlap with adaptive grading, which already warms golden hour |
| 10 | Weather transitions as first-class state | No new visual at all, and it makes every existing weather effect better. Each one currently eases independently, so a storm arriving is not a coordinated event |

### Tier B - worth doing

| # | Feature | Why |
|---|---|---|
| 11 | Exposure responding to fire, lava, lightning | Walking into a forge and having your eyes adjust is memorable. Cheap once emissive exists, which is why it sits below it |
| 12 | Persistent `MaterialDefinition` per block | Infrastructure with no direct visual, and the prerequisite for emissive, snow response and subsurface |
| 13 | Rendering debug HUD | Would have saved several of the last ten rounds outright. Given how much of this project's time has gone to "is it even running", the return is real |
| 14 | Height fog | Valley mist at dawn is beautiful and cheap. Docked because it is an effect rather than a system, and vanilla has `flatFogDensity` already |
| 15 | Rain streaks on vertical wet faces | Completes wetness, rides the existing path, modest and honest |
| 16 | Split-toning | Would do more for golden hour than any other single grading control. A look control, not a system |
| 17 | Cloud attenuation of direct sunlight | Partly done as the overcast term; belongs in atmosphere rather than weather |
| 18 | Camera state as shared state | Infrastructure for motion effects and underwater |
| 19 | Rain disturbance of the water surface | Composes well - the ripple field already exists. Blocked on there being a water renderer |
| 20 | Refraction | Real payoff, moderate risk, needs the scene texture |
| 21 | Sun and moon attenuation through atmosphere | Small on its own, part of the atmosphere system |
| 22 | Frost as a second environmental layer | Good abstraction, only meaningful after snow lands |
| 23 | Separate local contrast from albedo before the Sobel pass | Fixes the known "dark is deep" weakness. High idea quality, subtle result, and real work in `tools/pbrgen` with a fixture regeneration |
| 24 | One shared lighting snippet across programs | Pure hygiene, but a prerequisite for doing #2 properly rather than by copy |
| 25 | Environmental camera effects | Heat shimmer and screen frost are good; rain-on-lens is a generic-shader-pack tell |
| 26 | Seasonal foliage response | Autumn colour would be lovely, but overlaps what the game already does to foliage and risks fighting it |
| 27 | Quality tiers | Necessary eventually. Premature: there are not yet enough expensive systems to tier |
| 28 | Godray interaction | Vanilla has godrays; modulating them by cloud cover is a small addition to someone else's effect |
| 29 | GPU/VRAM detection | Only useful once tiers exist |

### Tier C - low value, or actively wrong for this game

| # | Feature | Why |
|---|---|---|
| 30 | Bloom | **Vanilla already has it.** This is driving `bloomParts`, not building a bloom - cheaper than it looks, and the marginal gain is smaller than it sounds. Over-bloom is also the fastest way to break this game's art direction |
| 31 | SSAO | **Vanilla already has it.** The gap is at the contact and crevice scales, which is #4 |
| 32 | Vegetation wind | **Vanilla already has it**, including a high-frequency term and per-class bend counters. Marginal |
| 33 | Puddles pooling in depressions | Good idea, poor feasibility: needs a height or flow signal the terrain shader does not have. High risk of becoming a second cloud-shadow saga |
| 34 | Screen-space reflections | The entry to push back on hardest. Expensive, artefact-prone, needs depth and normal buffers that are not wired - and in a blocky, low-detail world the artefacts are more visible than the benefit. Real chance of looking *worse* |
| 35 | Event camera effects | Damage flash and explosion distortion are game feel, not rendering. Arguably out of scope |
| 36 | Camera effects (vignette, grain, chromatic aberration, lens dirt) | The plan's own note is right: these are the fastest way to make the mod feel like a generic shader pack. Ship them off by default, or not at all |
| 37 | Depth of field | Actively wrong for a first-person survival game - you look where you look. Screenshot mode only |
| 38 | Parallax / relief mapping | Fights the art direction hardest of anything here. Flat faces *are* the aesthetic, and it is expensive and artefact-prone at block edges |
| 39 | Temporal accumulation / TAA | Enormous complexity, needs motion vectors that are not obtainable, and ghosting is very visible on a blocky world |
| 40 | Dynamic resolution | Not this mod's layer |

Forty rows for forty-four entries: contact shadows and crevice shading are ranked
together at #4, and Fresnel, depth colouration and waves together at #8.
