# Vintage Visuals

A client-side visual overhaul mod for [Vintage Story](https://www.vintagestory.at/):
color grading, weather and sky, screen-space reflections, and a pseudo-PBR
texture pipeline that derives normal/roughness/specular maps from the vanilla
diffuse textures.

Built as GLSL patches against the vanilla shaders plus a C# code mod. Four
subsystems, each independently toggleable, each degrading gracefully if it
cannot load.

> **Status: pre-alpha (0.0.1).** The scaffold, shader-patch engine and color
> grading subsystem are implemented; weather, reflections and the in-game PBR
> pipeline are not. See [Current state](#current-state) for exactly what has and
> has not been verified.

## Install

1. Install the mod: drop `vintagevisuals_<version>.zip` into your
   `VintagestoryData/Mods/` folder.
2. Launch the game. Settings live in
   `VintagestoryData/ModConfig/vintagevisuals.json`, created on first run.
3. Press <kbd>Ctrl</kbd>+<kbd>V</kbd> in game to reload the config from disk
   without restarting.

## Subsystems

| Subsystem | What it does | Docs |
|---|---|---|
| **ColorGrade** | Filmic tonemap, exposure, saturation, contrast, white-balance | [src/ColorGrade/README.md](src/ColorGrade/README.md) |
| **Weather** | Volumetric clouds, cloud shadows, sky scattering, weather-driven fog | not implemented |
| **Reflections** | Screen-space reflections on water | not implemented |
| **PseudoPBR** | Derived normal/roughness/spec atlases from vanilla textures | [tools/pbrgen/README.md](tools/pbrgen/README.md) (offline prototype only) |

## Configuration

All values live in `ModConfig/vintagevisuals.json`. Edit and press
<kbd>Ctrl</kbd>+<kbd>V</kbd> to apply live — no world reload needed.

| Key | Range | Default | Effect |
|---|---|---|---|
| `ColorGrade.Enabled` | bool | `true` | Master toggle for the subsystem |
| `ColorGrade.Exposure` | 0.1 – 4.0 | `1.0` | Linear exposure multiplier applied before the tonemap |
| `ColorGrade.Contrast` | 0.0 – 2.0 | `1.0` | Pivots around mid-grey (0.18); 1.0 is neutral |
| `ColorGrade.Saturation` | 0.0 – 2.0 | `1.0` | 0 is greyscale, 1.0 is neutral |
| `ColorGrade.Temperature` | -1.0 – 1.0 | `0.0` | Negative is cooler/bluer, positive is warmer/oranger |
| `ColorGrade.TonemapStrength` | 0.0 – 1.0 | `0.0` | Blend between vanilla output and the filmic curve. Off by default — see below |
| `EnableShaderDebugDump` | bool | `false` | Dump post-patch GLSL to `VintagestoryData/ShaderDebug/` |

Order of operations is fixed: exposure → white balance → tonemap → contrast →
saturation. Rationale in [src/ColorGrade/README.md](src/ColorGrade/README.md).

`TonemapStrength` ships at `0.0` deliberately. The ACES curve expects linear,
scene-referred input, and nobody has yet confirmed against a running game
whether `final.fsh`'s output is still linear where this mod grades it. Turning
it on before that is checked risks a washed-out image on first install. Set it
to `1.0`, look at the result, and if it holds up, change the default.

## Current state

Against the [MVP checklist](docs/IMPLEMENTATION_PLAN.md):

| Item | State |
|---|---|
| Repo scaffold builds | **compiles** clean against API 1.22.5, packages to a zip |
| `ShaderPatchLoader` applies a YAML patch, logs pass/fail | **33 checks pass** against the compiled assembly |
| Config system wired, live-tunable values | compiles, **not run in game** |
| Color grade: exposure/saturation/contrast/temperature | compiles, **not run in game** |
| Color grade: tonemap curve | compiles, ships off — see above |
| PBR: offline prototype validated on sample textures | **done**, 31 tests passing |
| Everything under Weather / Reflections / in-game PBR | not started |

Using the verification levels from [CLAUDE.md](CLAUDE.md), this sits at **2
(compiles)**, with the patch engine independently exercised beyond that:

- **The offline PBR tool runs.** Test suite, constants tuned against measured
  output, contact sheet reviewed. Working software.
- **The patch engine is exercised by `tools/smoketest`**, which drives the
  compiled assembly against the real `colorgrade.yaml`: parsing, anchor
  matching, and the rollback path. No game required.
- **Nothing has run inside Vintage Story.** No GPU has seen the GLSL, and the
  Harmony hook onto `ShaderRegistry` is resolved by name at runtime, so nothing
  offline can confirm it binds.

First run will tell you the rest. The patch engine logs every group it applies
or fails, `ColorGrade.Saturation: 0.0` plus <kbd>Ctrl</kbd>+<kbd>V</kbd> should
turn the world greyscale instantly, and `EnableShaderDebugDump` writes the exact
GLSL that reached the compiler.

## Known limitations

These are known now, not discovered later. Kept current as the mod grows.

- **Shader patches are version-fragile.** They regex-match vanilla GLSL. A game
  update that rewords the matched lines silently disables the affected
  subsystem (you get a loud log line, but the game will not crash). Expect to
  re-verify patches against `assets/game/shaders/` on every game update.
- **Compatibility with other shader mods is untested.** Volumetric Shading,
  Coriaender Shaders and Ancestral Bliss Shaders patch some of the same vanilla
  files. Conflicts are likely and are not yet documented.
- **Color grading runs after the vanilla tonemap**, not instead of it, because
  the patch inserts at the end of `final.fsh`. Highlights already clipped by
  vanilla cannot be recovered by lowering exposure here.
- **The PBR tool infers material properties from diffuse pixels alone.** Sobel
  edge detection reads *painted-on* shading in a texture as real geometry —
  hand-painted highlights become bogus normals. Likewise the variance-based
  roughness estimate cannot distinguish "rough surface" from "busy pattern".
  Both are inherent to the approach; the tool exposes tuning knobs rather than
  pretending to solve them.

## Development

See [CLAUDE.md](CLAUDE.md) for build commands and repo conventions,
[docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) for the phase plan and
MVP checklist, and [docs/WORKFLOW.md](docs/WORKFLOW.md) for commit conventions.

## Credits

Shader-patching approach follows the pattern established by
[Volumetric Shading](https://github.com/xxmicloxx/VolumetricShading) (xxmicloxx,
Novocain) and Coriaender Shaders. The ACES-approximation tonemap curve is Stephen
Hill's `ACESFitted`, widely published as a public-domain fit.

## License

Not yet chosen — see issue tracker before redistributing.
