# Architecture

Vintage Visuals is a **client-side rendering framework that ships a visual
overhaul**, not a shader pack. The distinction is load-bearing: it decides where
a feature lives, what it is allowed to depend on, and what has to be true before
it can ship.

## The goal, stated precisely

> Reconstruct as much of a modern physically-inspired rendering pipeline as
> Vintage Story's existing renderer allows, **while preserving the game's
> original art direction.**

Not "make Vintage Story prettier", and emphatically not "make it look like
Unreal". The second clause is the constraint that makes the first one
interesting. Every effect here has to be defensible as *the fidelity the
original engine could not provide*, rather than as a look borrowed from another
game.

Two rules follow from it, and both have already been paid for in this repo:

- **Vanilla is the default, everywhere.** Every uniform's zero, every strength
  slider's zero, and every failed patch must produce the image the player would
  have had without this mod. Not close to it - it.
- **A subsystem that cannot be seen working has not shipped.** See
  [Verification](#verification).

## The layers

```
                         Rendering Core
              shader patching, config, quality, debug
                                │
                                ▼
                        Scene Understanding
             material system  ·  environment state  ·  camera
                                │
        ┌───────────────────────┼───────────────────────┐
        ▼                       ▼                       ▼
  World Rendering          Environment            Image Processing
  PBR, lighting,        weather, clouds,      exposure, tonemap, grade,
  shadows, water,       rain, snow,           bloom, AO, SSR, temporal
  atmosphere,           wetness
  vegetation
```

Dependencies point **down and inward only**. World Rendering may read Scene
Understanding; Scene Understanding never reads World Rendering. Nothing reads
another system at its own level.

That last rule is the one that keeps costing something and keeps being worth
it. PseudoPBR used to read the Weather subsystem directly for wetness, which
meant the material system stopped working correctly when weather was disabled -
a coupling nobody intended and nothing detected. Both now read the environment
state instead, and neither knows the other exists.

### Rendering Core

| | |
|---|---|
| `src/Common/Patching/` | YAML patch engine, per-group rollback, token matching |
| `src/Common/ConfigManager.cs` | live-reloadable config |
| `src/Common/ConfigLibBridge.cs` | optional F7 panel, over the event bus, zero coupling |
| `tools/verifypatches` | every patch against the game's own shaders, every define combination |
| `tools/smoketest` | the engine and every pure rule, without a game |

### Scene Understanding

The layer that answers *what is here* and *what is happening*, so that nothing
above it has to ask the game twice.

| | |
|---|---|
| `src/Common/Scene/EnvironmentState.cs` | one snapshot of the world, shared by everything |
| `src/Common/Scene/EnvironmentTracker.cs` | the only file outside a subsystem that touches the world API |
| `src/PseudoPBR/MaterialProfile.cs` | what a block is made of, from `EnumBlockMaterial` |
| `src/PseudoPBR/MaterialAtlas*.cs` | derived normal / roughness / specular, cached |

`EnvironmentState` exists because the alternative was already happening. Three
subsystems each asked the game the same questions on their own timers: the
climate was sampled twice a second in two places, cloud density was read through
two copies of the same magic gain, daylight was read in three. Nothing was
*wrong* with any of them - which is the problem. Two copies of a constant drift
apart quietly, and the symptom is two subsystems disagreeing about the weather.

It carries two kinds of field and keeps them apart:

- **Sampled** - what the game says, normalised once at the point of reading.
- **Derived** - an answer more than one subsystem needs and none should own.
  Wetness is the example: Weather computes it, PseudoPBR consumes it, and
  neither is its natural home.

What does **not** belong in it is anything scaled by a config value. A strength
slider states what the player wants, not what the world is doing, and mixing the
two is how "off" stops meaning off. Those live in per-subsystem input structs -
`SceneInputs` is the pattern.

### The intent layer, and why nothing bypasses it

`EnvironmentState` says what the world **is**. `SceneIntent` says what the scene
**needs**, in a bounded 0..1 vocabulary every subsystem shares: `Wetness`,
`Cold`, `Heat`, `Gloom`, `Atmosphere`, `Night`, `Enclosure`, `ArtificialLight`,
`Readability`, `Restraint`.

The layer exists because the alternative had already started. Five subsystems
each re-derived their own reading of the same facts, and at ten they would have
drifted: two would decide "wet" meant different things and nothing would say so.

Three rules hold it together.

**Every push is recorded.** An `IntentContribution` carries its source, amount,
reason, and whether a cap trimmed it. When a scene comes out wrong the only
question worth asking is which influence did it, and a bare float cannot answer
- the same blindness that made the cloud shadows take three rounds.

**Indirect pushes are capped; direct restatements are not.** Rain pushing
`Wetness` *is* that channel restated. Rain also thickening the air is a side
effect, and without a bound one badly tuned side effect owns a channel outright
while every other input becomes decoration.

**`Restraint` outranks the rest.** It rises where the scene is already hard to
read - deep underground, at night, in a storm - and scales down everything that
removes light, colour or contrast. A visual overhaul that makes a cave
unnavigable has not improved the game, and it is an easy mistake to make: every
effect here was tuned in daylight on the surface.

### Arbitration: one allowance, several claimants

`VisualBudget` fixes a defect rather than adding a feature. In heavy rain this
mod was removing colour and light three times over - colour grading, the
overcast term, and rain fog - each tuned alone, each reasonable alone, with
nothing between them.

Each visual role (`SceneLight`, `Saturation`, `Contrast`, `Haze`) has a total
allowance and an **owner**. The owner gets what it asks for; everyone else is
**dampened rather than refused**, because a secondary that goes to zero is a
feature that silently stops working and nobody finds out why. Claims are
recorded like intent contributions.

Arbitration runs **once per tick in `EnvironmentTracker`**, not in each
subsystem. That is not tidiness: the subsystems tick at different rates and
stages, so a budget rebuilt once would be exhausted by whoever ran first and the
rest would collapse to nothing on most frames.

### World Rendering, Environment, Image Processing

The systems themselves. Their status is tracked in
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md); their design lives in a
README beside the code.

## Two shared snippets, and the rule attached to each

`pbrcore.glsl` is the one evaluation of Cook-Torrance, injected into all three
shaded programs. `scene.glsl` is the one description of the conditions it runs
in, injected at the same three anchors.

The rule matters more than either file: **a new shader maps its inputs into
these names and reads these lanes.** It does not invent a local meaning for wet,
cold, enclosed, night, restrained or readable. Without that, five subsystems
each grow their own idea of "wet" and nothing says they have stopped agreeing -
which was already beginning, with `pseudopbr.glsl` and `weather.glsl` each
declaring their own wetness and overcast uniforms matched only by convention.

Debug views are numbered **per shader, in that shader's own terms**, not from
one global list. The material system's numbers mean material layers; an entity
has none of them, and a shared list would have to skip most of itself to stay
meaningful.

## Why the lighting model is not a separate system

An outside reading of this repo will suggest that the obvious next milestone is
"a unified lighting model", with PseudoPBR feeding it material data. It is worth
saying plainly why the code is not arranged that way: **the lighting model
already exists, and PseudoPBR is its name.**

`assets/vintagevisuals/shadersnippets/pseudopbr.glsl` takes albedo, a derived
normal, roughness, a specular mask and a metal response, and evaluates
Cook-Torrance - GGX distribution, Smith-Schlick geometry, Schlick Fresnel, with
energy conservation - against sun direction, sky irradiance, block light with a
recovered direction, shadow-map occlusion, fog, water murk and weather
attenuation. Roughness and specular are not "waiting for a lighting term"; they
have been shading the world since `1750ee5`.

The real gap is **reach**, not existence. That model is welded to two vanilla
programs, `chunkopaque` and `chunktopsoil`. Entities, held items, liquids and
particles are lit by vanilla, so a mob standing on a PBR-lit floor is shaded by
a different model than the floor. Closing that is the next milestone, and it is
a patching-and-plumbing problem rather than a lighting-theory one.

## The information ladder

**When Vintage Story already knows something, use Vintage Story's answer.**

Prefer, strictly in order: information the game exposes directly; information
derived from authoritative game data; information derived from geometry or
textures; information approximated from screen space; and only then an invented
simulation.

Every serious bug in this project came from working too low on that ladder.
Three cloud-shadow noise fields failed because the game already had cloud
placement. Metalness from pixels was wrong until it came from
`EnumBlockMaterial`. A feature that sits on the bottom rung has to say why the
rungs above it are unavailable.

The full set of rules that decide whether a feature belongs at all - the visual
hierarchy, the budgets, readability as a hard constraint, and what this project
deliberately does not build - is in [VISUAL-LANGUAGE.md](VISUAL-LANGUAGE.md).

## The pipeline, end to end

The layers above say what belongs where. This says what actually runs, in order,
and who owns each step. It exists because an isolated shader looks like an
independent system until you find out what feeds it.

```
Vintage Story engine
  block textures, atlas positions, light grid, shadow map,
  cloud tiles, calendar, climate, wind, framebuffers
        |
        v
EnvironmentTracker            src/Common/Scene/   CPU, once per tick
  one shared worldview; the ONLY place the game is asked what is happening
        |
        v
SceneIntent + VisualBudget    src/Common/Scene/   CPU, arbitrated once
  what the scene needs, in one bounded vocabulary
        |
        v
MaterialResolver              src/PseudoPBR/      CPU, at load
  per-TEXTURE identity from asset path and slot name
        |
        v
Material atlases 1 and 2      src/PseudoPBR/      GPU, units 15 and 14
  normal XY / roughness / specular      metalness / height / AO / emission
        |
        v
Terrain, entity, particle shading      patched vanilla fragment shaders
  pbrcore.glsl  - the ONE Cook-Torrance evaluation
  scene.glsl    - the ONE description of the conditions it runs in
  pseudopbr.glsl- terrain material, dapple, ripples, reflection
        |
        +---- vanilla shadow map -> sun visibility -> canopy dapple, shafts
        +---- cloud tiles         -> cloud shadows
        +---- environment layers  -> wetness, snow, frost
        +---- scene capture       -> pixelated reflection      (see below)
        |
        v
outColor, outGlow              vanilla's own buffers
        |
        v
final.fsh                      src/ColorGrade/    GPU, post pass
  exposure, tonemap, contrast, saturation, white balance, adaptive stack
        |
        v
frame
```

### The atmosphere, which leaves through two doors

Half of it is not in a shader at all.

```
                    VINTAGE STORY
     calendar    ambient manager    climate    cloud renderer
        |              |              |             |
        +--------------+------+-------+-------------+
                              |
                     EnvironmentTracker            src/Common/Scene/
                     the ONE place the mod asks
                              |
              +---------------+---------------+
              |                               |
       EnvironmentState                 AtmosphereState
       what the world is doing          what the AIR is doing
              |                               |
              +---------------+---------------+
                              |
                      AtmosphereInputs         src/Atmosphere/
                      config x state x budget
                      normalisation lives here
                              |
              +---------------+---------------+
              |                               |
       AmbientBridge                   AtmosphereShaderBinder
       writes the game's own           22 uniforms, derived ONCE
       ambient stack                   per frame
              |                               |
       the game's blend                atmosphere.glsl
              |                        one transport
              |                               |
   flatFogDensity, fogDensityIn,       +--> chunkopaque
   fogMinIn, rgbaFog                   +--> chunktopsoil
              |                        +--> entityanimated
              +--> EVERY program       +--> particlescube
                   the game has,
                   sky and water
                   included
```

The left branch is the point. The sky, the water, `chunktransparent` and the
sprite particles are in no patch group and never will be, so anything expressed
as an ambient modifier reaches parts of the frame that GLSL in this mod cannot.
Height haze, fog colour, density and floor all go that way.

The right branch carries what the game does not compute at all: air that knows
where the sun is. It anchors on `applyFog`, which is byte-identical in all seven
shading programs, so terrain, ground cover, entities and particles receive the
same uniforms and run the same function.

`AtmosphereState` reads the ambient blend back, so both branches work from one
set of numbers and cannot drift.

### The reflection loop, which spans two frames

The only part of the pipeline that is not a straight line. `chunkopaque.fsh` is a
forward opaque pass: it knows the material texel but cannot see the scene. The
post pass can see the scene but not the texel. So the image crosses a frame
boundary instead of a pass boundary.

```
frame N                                   frame N+1
-------                                   ---------
render-stage framebuffer (colour + depth)
        |
   AfterPostProcessing
        |
SceneCaptureRenderer   src/Reflections/
  half res, RGB scene, alpha linear depth
        |                                        |
        +----------------- unit 13 -------------->
                                                 |
                                    vvSceneReflection  (pseudopbr.glsl)
                                      ray from texel centre
                                      screen-space march
                                      crossing + bisection
                                                 |
                                    vvAmbientSpecular   <- substituted, not added
                                                 |
                                            final colour
```

## Data ownership

Where each rendering input comes from, and who consumes it. The rule from the
information ladder applies throughout: prefer what the game already computed.

| Input | Source | Transformation | GPU form | Consumer |
|---|---|---|---|---|
| Block texture | game atlas | none | vanilla `terrainTex` | vanilla shading |
| Texture UV | vertex data | none | `uv` varying | material sampling |
| Material identity | asset path + slot name | `MaterialResolver`, CPU at load | baked into atlas texels | all PBR |
| Normal / roughness / specular | derived from block textures | `PbrMapGenerator`, CPU, cached | atlas 1, unit 15 | `vvSampleMaterial` |
| Metalness / height / AO / emission mask | derived, plus `glowLevel` | `MaterialAtlas2Builder` | atlas 2, unit 14 | `vvSampleMaterial2` |
| Sun / moon direction | vanilla `lightPosition` | none | uniform | dapple, reflection, lobe |
| Sun occlusion | vanilla shadow map | PCF, no block-light term | `shadowMapNear/Far` | `vvSunVisibility` |
| Sky light level | `rgbaLightIn.a` | none | `vv_sunExposure` varying | wetness, snow, frost |
| Glow level | vertex `renderFlags` | bit unpack | `glowLevel` | emission |
| Wind | `GetWindSpeedAt` | tracked, clocked | `vv_sceneBreeze` | foliage response |
| Cloud placement | game's cloud renderer tiles | `CloudTileReader`, Beer-Lambert | uniform array | cloud shadows |
| Season / temperature / rainfall | calendar + climate | `EnvironmentTracker` | scene uniforms | material response |
| Framebuffer colour + depth | `IRenderAPI.CurrentFrameBuffer`, falling back to `FrameBuffers[Primary]` | half-res copy, depth to alpha | capture texture, unit 13 | `vvSceneReflection` |
| Camera matrices | `CurrentProjectionMatrix` x `CameraMatrixOriginf` | multiplied, stored per capture | `vv_reflectViewProj` | reflection projection |
| Fog colour, density, floor | `IAmbientManager.Blended*` | none | vanilla `rgbaFog`, `fogDensityIn`, `fogMinIn` | `AtmosphereState` |
| Height fog | `BlendedFlatFogDensity` / `...YPosForShader` | none | vanilla `flatFogDensity`, `flatFogStart` | `AtmosphereState`, and written back by `AmbientBridge` |
| Sun colour and direction | `IClientGameCalendar.SunColor`, `SunPositionNormalized` | none | `vv_atmosSunColor`, `vv_atmosSunDir` | `atmosphere.glsl` |
| Moon direction and light | `MoonPosition`, `MoonPhaseBrightness` | normalised in double; light scaled by darkness | `vv_atmosMoonDir`, `vv_atmosMoonLight` | moon scattering |
| Broken cloud | the same blended cloud density the shadows read | `4c(1-c)` | `vv_atmosBrokenCloud` | cloud-edge scattering |
| Camera height above sea level, far plane | `DefaultShaderUniforms` | none | vanilla `playerToSealevelOffset`, `zFar` | `AtmosphereState` |

### Configuration, which has three writers and one owner

The renderer never asks who moved a value. `ConfigManager` owns the one
`VintageVisualsConfig` instance and raises one `ConfigChanged` event; every writer
mutates that object and calls `NotifyChanged()`.

```
User ──┬── Visual Tuning Studio (src/Ui/)  ──┐
       ├── ConfigLib bridge                 ─┤
       └── ModConfig JSON + Ctrl+V reload   ─┴──> ConfigManager ──> ConfigChanged ──> subsystems
```

| Writer | Reaches the config by | Owns |
|---|---|---|
| Visual Tuning Studio | `ConfigAccess` over a dotted property path | presentation only: labels, ranges, tabs, descriptions |
| `ConfigLibBridge` | the game's event bus, no ConfigLib types referenced | nothing; it is a relay |
| JSON + hotkey | `LoadModConfig` / `StoreModConfig` | the file on disk |

`src/Ui/` is a configuration client and holds no rendering state: no GL call, no
uniform upload, no shader reload. Patch-gating changes are detected on the
config-changed path by `VintageVisualsModSystem`, which schedules the reload — the
UI only ever writes a value and notifies.

**Not plumbed, and authoritative if it were:** the rain map
(`IBlockAccessor.GetRainMapHeightAt`, `IMapChunk.RainHeightMap`) is what the game
itself uses to place splash particles and extinguish torches. Wetness currently
thresholds `vv_sunExposure` instead, which measures flat.

## Fallbacks

Every system that can lack data has a defined answer. A future change must not
quietly invalidate one of these.

| System | Normal path | Fallback | Why | Visual consequence |
|---|---|---|---|---|
| Any patch group | applied | group disabled, rest load | a failure must not take the world render | that subsystem is vanilla |
| Material atlas | sampled | `vv_pbrEnabled` 0 | unpatched or rolled back | vanilla shading |
| Second atlas | sampled | `vv_material2Valid` 0, neutral material | atlas absent or failed | dielectric response, emission everywhere |
| Atlas page | bind hook selects | page 0 | single-page installs need no swap | correct on one page |
| Sun visibility | shadow map | returns 1.0 | `SHADOWQUALITY 0` — no shadow data exists | no dapple, rather than a dark world |
| Canopy structure | ring of taps | returns 0 | shadows off | no dapple |
| Cloud shadows | game's cloud tiles | **off**, not substituted | invented clouds correspond to nothing | vanilla sky lighting |
| Scene capture | previous frame | `vv_reflectValid` 0 | shader, framebuffer or texture id failed | analytic sky fallback |
| Reflection ray | scene hit | analytic sky/horizon/ground | off screen, occluded, or facing the camera | plain sky instead of wrong geometry |
| Entity / particle PBR | own patch group | independently gated | terrain problems must not disable them | vanilla flat diffuse |
| Height haze | modifier in the game's ambient stack | modifier **removed**, not zeroed | a zeroed entry is residue in a dictionary shared with every mod | vanilla's own atmosphere |
| Ambient stack unavailable | modifier installed | logged once, feature off | nothing else in the frame depends on it | vanilla's own atmosphere |
| Atmosphere uniforms | uploaded every frame | all zero | unset GLSL uniforms read as 0, which IS the vanilla value | vanilla's own `applyFog`, algebraically |
| Cloud edge | a real cloud edge | partial-cover proxy | the game's tiles carry no edge information | a broad response instead of a rim |
| Godrays, dapple interaction | writes into vanilla's glow channel | **nothing** | the anchor belongs to another patch group | vanilla's own godrays |

## Performance contracts

**Nothing here has been measured.** Every figure below is a count of operations
or a resource size, not a profile. "Not measured" is the honest entry and it
appears often.

| System | Cost | Every frame? | Previous frame? | Scales with |
|---|---|---|---|---|
| Material atlas build | 2 pages, 4096x2048 each, cached to disk | no, once at load | no | texture count |
| Atlas upload | 2 textures resident | no | no | — |
| Scene capture | 1 RGBA8 at half frame per axis; one fullscreen copy | yes, while enabled | produces it | resolution |
| Reflection march | up to 24 texture taps plus 5 bisection taps, only on rays that cross | per reflective fragment | yes, reads it | reflective pixel count |
| Sun visibility | 9-18 shadow taps | per terrain fragment | no | resolution |
| Canopy structure | 12 shadow taps, gated behind its strength slider | per terrain fragment when dapple on | no | resolution |
| Cloud shadows | uniform array lookup, no sampler | per terrain fragment | no | resolution |
| Colour grading | one fullscreen pass | yes | no | resolution |

Two costs are paid whether or not anything on screen uses them: the scene capture
and the terrain-wide shadow taps. Both are behind switches that default off or
are cheap to zero.

## Related reading

[INSPIRATION.md](INSPIRATION.md) records what was taken from
[Dalashade](https://github.com/ImperfectRain/Dalashade), the same author's
scene-aware ReShade preset generator for FFXIV, and - more usefully - what was
deliberately not taken. The short version: Dalashade is post-process only with
no G-buffer, so much of its machinery exists to *infer* what a pixel is from
image evidence. This project patches the geometry shaders and knows. Copy the
layers above that inference; never the inference.

## Verification

Nothing that touches GLSL or the render pipeline can be verified by building.
The levels, in increasing order, and the rule that only the last one closes a
milestone:

1. **Parses** - YAML/JSON syntax checked, GLSL reviewed by eye.
2. **Compiles** - `dotnet build`, plus `tools/verifypatches` applying every
   group to the game's own dumped shaders and compiling the result in every
   define combination vanilla supports.
3. **Loads** - the game starts, the log shows every group applied.
4. **Renders** - the change is visible and survives a world reload.

Two habits sit underneath that and have both caught real bugs:

- **Pure rules go in `tools/smoketest`.** Anything that is ordinary arithmetic -
  the grade stack, the wetness model, eye adaptation, the material profile -
  gets driven through its whole input range without a client. The
  load-bearing check is always the same one: every strength at zero returns the
  input **bit for bit**.
- **Distributions get measured, not estimated.** Both the cloud-shadow field and
  the rain ripples shipped wrong because a threshold was placed on a
  distribution nobody had sampled - once far too strong, once fifteen times too
  weak. Transcribing the shader into Python and measuring it is now the
  expected step before tuning anything statistical.
