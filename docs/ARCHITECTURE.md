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

### World Rendering, Environment, Image Processing

The systems themselves. Their status is tracked in
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md); their design lives in a
README beside the code.

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
