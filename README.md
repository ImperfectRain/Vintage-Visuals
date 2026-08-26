# Vintage Visuals

A client-side **rendering framework** for [Vintage Story](https://www.vintagestory.at/)
that ships a visual overhaul: colour grading, a pseudo-PBR material and lighting
model derived from vanilla textures, weather-aware material response,
atmospheric transport, pixelated reflections, and vegetation/forest lighting.

Built as GLSL patches against vanilla shaders plus a C# code mod. The goal is
not to make Vintage Story look like another game. It is to reconstruct as much
of a modern physically inspired pipeline as the existing renderer allows
**while preserving the game's art direction**. See
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

> **Status: pre-alpha (0.0.1).** Several systems have been seen in a real 1.22.7
> client, but the project is still evidence-limited. Colour grading, terrain PBR,
> wetness, cloud shadows, rain ripples, dapple, and parts of the reflection debug
> path have runtime evidence. Atmosphere, vegetation/forest lighting, entity and
> particle lighting, and the reflection resolver remain mostly L2/L3: built and
> statically tested, but not visually closed. Scene reflections are off by
> default; PseudoPBR, Weather, Atmosphere, and ColorGrade default on. The exact
> source of truth is [docs/STATUS.md](docs/STATUS.md).

## Systems

| System | Current state | Docs |
|---|---|---|
| **Colour management** | core grading renders; tonemap still ships at strength 0 | [src/ColorGrade/README.md](src/ColorGrade/README.md) |
| **Material and lighting** | terrain renders; advanced material, entity, particle, flora and forest lighting paths are mostly L2 | [src/PseudoPBR/README.md](src/PseudoPBR/README.md) |
| **Environment state** | shared worldview, intent and budget layer implemented | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| **Weather** | wetness, cloud shadows and ripples have runtime evidence; snow/frost remain L2 | [src/Weather/README.md](src/Weather/README.md) |
| **Atmosphere** | dedicated subsystem with ambient-stack and shader branches; needs runtime validation | [src/Atmosphere/README.md](src/Atmosphere/README.md) |
| **Reflections** | scene capture, screen-space march, world volume and analytic fallback exist; visuals are not closed | [src/Reflections/README.md](src/Reflections/README.md) |
| **Visual Tuning Studio** | native config dialog; production lifecycle simplification underway, with scroll moving native child bounds instead of rebuilding | [src/Ui/README.md](src/Ui/README.md) |
| **Water and post FX** | not implemented | [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) |

Feature-by-feature state is tracked in [docs/STATUS.md](docs/STATUS.md). Proof
level is tracked in [docs/CHECKLIST.md](docs/CHECKLIST.md). Design rules are in
[docs/VISUAL-LANGUAGE.md](docs/VISUAL-LANGUAGE.md).

## Install

1. Drop `vintagevisuals_<version>.zip` into `VintagestoryData/Mods/`.
2. Launch the game. Settings live in
   `VintagestoryData/ModConfig/vintagevisuals.json`, created on first run.
3. Press <kbd>Ctrl</kbd>+<kbd>V</kbd> in game to reload the config from disk.
   With the optional [ConfigLib](https://mods.vintagestory.at/configlib) mod
   installed, the F7 panel is also available. The native Visual Tuning Studio is
   opened by this mod's hotkey.

## Configuration

All values live in `ModConfig/vintagevisuals.json`. ConfigLib and the native
Visual Tuning Studio both write that same object; neither owns a separate copy.

The public config is now too large for this README to be authoritative by hand.
Use these files for exact values:

| Need | Source |
|---|---|
| Defaults and clamp ranges | `src/Common/VintageVisualsConfig.cs` |
| ConfigLib labels and UI ranges | `assets/vintagevisuals/config/configlib-patches.json` |
| Native studio labels, sections and descriptions | `src/Ui/VisualSettingRegistry.cs` |
| Debug-view names | `src/Ui/DebugViewRegistry.cs` |

High-risk defaults worth calling out:

| Key | Default | Notes |
|---|---|---|
| `ColorGrade.Enabled` | `true` | core grading is active |
| `ColorGrade.TonemapStrength` | `0.0` | ACES curve exists but ships blended out |
| `PseudoPBR.Enabled` | `true` | terrain material path is active by default |
| `PseudoPBR.DebugView` | `0` | range `0..62`; use the named UI entries instead of memorising numbers |
| `Reflections.SceneReflections` | `false` | screen-space scene reflections are experimental and off by default |
| `Weather.Enabled` | `true` | weather material response is active |
| `Atmosphere.Enabled` | `true` | atmosphere subsystem is active |
| `EnableShaderDebugDump` | `false` | dumps post-patch GLSL when enabled |
| `WriteSceneReport` | `false` | writes one scene-intent report when enabled |

## Current State

This is the short version. The detailed tracker is [docs/STATUS.md](docs/STATUS.md).

| Area | Current proof |
|---|---|
| Patch engine, config reload, shader delivery diagnostics | L4/L3 depending on path |
| Colour grade exposure, saturation, contrast and temperature | L4 |
| Tonemap curve | L2, ships off |
| Material atlas, terrain normal/roughness/specular response | L4 for the core terrain path |
| Advanced material model: metalness, multi-scatter, specular occlusion, anisotropy, AO, emission masks | L2 |
| Entity, particle and flora lighting | implemented, not visually closed |
| Weather wetness, ripples and cloud shadows | runtime evidence exists; tuning remains open |
| Snow, frost and atmosphere transport | L2 |
| Reflections | implemented as a hybrid scene/world/analytic resolver, but still visibly wrong in close-range and contact cases |
| Visual Tuning Studio | functional but still has reported tab-switch crash risk |
| Water and post-processing | not implemented |

Levels are defined in [docs/STATUS.md](docs/STATUS.md). **L2 means built and
checked without proving the image in game.**

## Known Limitations

- **Shader patches are version-fragile.** They match vanilla GLSL. A game update
  can disable a patch group with a loud log line instead of a crash.
- **ConfigLib integration is event-bus based.** The mod does not reference
  ConfigLib's assembly, so the optional dependency cannot break loading.
- **Compatibility with other shader mods is untested.** Anything patching the
  same vanilla shader can change the result.
- **Colour grading runs late.** It operates after vanilla composition, so it
  cannot recover highlights already clipped upstream.
- **Reflections are experimental.** The current system can reconstruct some
  world geometry on a texel grid, but close-range contact, undersampling,
  banding, and indoor/ceiling cases still need design work.
- **Performance is not measured.** Operation-count arguments are not profiling.
  The scene capture, reflection resolver and vegetation lighting need real GPU
  numbers before quality tiers can be honest.
- **Texture analysis infers detail, not identity.** Material identity now uses
  game data where possible, but normals and roughness still derive from painted
  textures and can read art shading as geometry.

## Development

See [CLAUDE.md](CLAUDE.md) for build commands and repo conventions,
[docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) for priorities, and
[docs/WORKFLOW.md](docs/WORKFLOW.md) for process.

## Credits

Shader-patching approach follows the pattern established by
[Volumetric Shading](https://github.com/xxmicloxx/VolumetricShading) and
Coriaender Shaders. The ACES-approximation tonemap curve is Stephen Hill's
`ACESFitted`, widely published as a public-domain fit.

## License

Not yet chosen. Do not redistribute without checking the issue tracker first.
