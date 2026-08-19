# Vintage Visuals

A client-side visual overhaul mod for [Vintage Story](https://www.vintagestory.at/):
color grading, weather and sky, screen-space reflections, and a pseudo-PBR
texture pipeline that derives normal/roughness/specular maps from the vanilla
diffuse textures.

Built as GLSL patches against the vanilla shaders plus a C# code mod. Four
subsystems, each independently toggleable, each degrading gracefully if it
cannot load.

> **Status: pre-alpha (0.0.1).** Colour grading works in game on 1.22.7 —
> Phase 1 of the plan is complete. Weather, reflections and the in-game PBR
> pipeline are not started. See [Current state](#current-state) for exactly what
> has and has not been verified.

## Install

1. Install the mod: drop `vintagevisuals_<version>.zip` into your
   `VintagestoryData/Mods/` folder.
2. Launch the game. Settings live in
   `VintagestoryData/ModConfig/vintagevisuals.json`, created on first run.
3. Press <kbd>Ctrl</kbd>+<kbd>V</kbd> in game to reload the config from disk
   without restarting. With the optional
   [ConfigLib](https://mods.vintagestory.at/configlib) mod installed you get
   sliders on <kbd>F7</kbd> instead.

## Subsystems

| Subsystem | What it does | Docs |
|---|---|---|
| **ColorGrade** | Eye adaptation, filmic tonemap, exposure, saturation, contrast, white-balance | [src/ColorGrade/README.md](src/ColorGrade/README.md) |
| **Weather** | Wet surfaces in rain, rain fog, cloud shadows, cloud shaping | [src/Weather/README.md](src/Weather/README.md) |
| **Reflections** | Screen-space reflections on water | not implemented |
| **PseudoPBR** | Derived normal/roughness/spec atlases from vanilla textures | [src/PseudoPBR/README.md](src/PseudoPBR/README.md) |

## Configuration

All values live in `ModConfig/vintagevisuals.json`. Edit and press
<kbd>Ctrl</kbd>+<kbd>V</kbd> to apply live — no world reload needed.

If you also have [ConfigLib](https://mods.vintagestory.at/configlib) installed,
press <kbd>F7</kbd> for sliders instead. It is entirely optional: this mod
declares no dependency on it, references none of its code, and behaves exactly
the same without it. The two are not separate settings — ConfigLib writes into
the same config this mod already uses, so a slider and a hand-edit are the same
value.

| Key | Range | Default | Effect |
|---|---|---|---|
| `ColorGrade.Enabled` | bool | `true` | Master toggle for the subsystem |
| `ColorGrade.Exposure` | 0.1 – 4.0 | `1.0` | Linear exposure multiplier applied before the tonemap |
| `ColorGrade.Contrast` | 0.0 – 2.0 | `1.0` | Pivots around display mid-grey (0.5); 1.0 is neutral |
| `ColorGrade.Saturation` | 0.0 – 2.0 | `1.0` | 0 is greyscale, 1.0 is neutral |
| `ColorGrade.Temperature` | -1.0 – 1.0 | `0.0` | Negative is cooler/bluer, positive is warmer/oranger |
| `ColorGrade.TonemapStrength` | 0.0 – 1.0 | `0.0` | Blend between vanilla output and the filmic curve. Off by default — see below |
| `AdaptiveExposure.Enabled` | bool | `true` | Eye adaptation: brightens dark places, settles in light |
| `AdaptiveExposure.DarkGain` | 0.25 – 4.0 | `1.6` | Exposure multiplier in pitch darkness |
| `AdaptiveExposure.BrightGain` | 0.25 – 4.0 | `1.0` | Exposure multiplier in full light |
| `AdaptiveExposure.BrightenSeconds` | 0 – 60 | `4.0` | Seconds to adapt to darkness (slow, like a real eye) |
| `AdaptiveExposure.DarkenSeconds` | 0 – 60 | `1.0` | Seconds to adapt to light (fast) |
| `PseudoPBR.Enabled` | bool | **`false`** | Surface relief. Off by default — it is the only setting that patches the shader drawing the world, and it has not been confirmed working on a GPU |
| `PseudoPBR.NormalStrength` | 0.0 – 2.0 | `1.0` | Global multiplier on the relief. 0 is flat, 1.0 is the tuned look |
| `PseudoPBR.SpecularStrength` | 0.0 – 2.0 | `1.0` | Global multiplier on the specular highlight |
| `PseudoPBR.RoughnessBias` | -0.5 – 0.5 | `0.0` | Shifts every material's roughness. Negative is glossier, positive is more matte |
| `PseudoPBR.MetalResponse` | 0.0 – 1.0 | `1.0` | How metallic reflective materials read. 0 gives every surface a white highlight |
| `PseudoPBR.AmbientSpecular` | 0.0 – 2.0 | `0.35` | Sky reflection strength, so metal in shade still has a highlight |
| `PseudoPBR.SpecularAntiAliasing` | 0.0 – 2.0 | `1.0` | Stops rough surfaces sparkling as the camera moves |
| `PseudoPBR.DetailDistance` | 4 – 192 | `48` | Blocks at which surface relief has faded out |
| `PseudoPBR.BlockLightSpecular` | 0.0 – 2.0 | `1.0` | Highlights from torches, lava and glowing blocks. Works underground and at night |
| `PseudoPBR.BlockLightDirectionality` | 0.0 – 1.0 | `0.7` | 0 treats block light as ambient; 1 estimates where the torch actually is |
| `PseudoPBR.DebugView` | 0 – 9 | `0` | Renders one layer on its own: 1 normal, 2 roughness, 3 specular, 4 relief, 5 highlight, 6 world normal, 7 reflectance |
| `PseudoPBR.WriteMaterialReport` | bool | `true` | Write `VintageVisuals/material-report.txt` listing every block's material |
| `PseudoPBR.BuildMaterialAtlas` | bool | `true` | Derive the material atlas at world load, cached to disk |
| `PseudoPBR.WriteAtlasPreview` | bool | `true` | Write viewable normal/roughness/specular PNGs beside the cache |
| `Weather.Enabled` | bool | `true` | Rain makes exposed surfaces wet: smoother, more reflective, darker |
| `Weather.WetnessStrength` | 0.0 – 2.0 | `1.0` | How wet rain makes surfaces look |
| `Weather.DryingSeconds` | 1 – 600 | `60` | How long a soaked surface takes to dry once rain stops |
| `Weather.RainCoverThreshold` | 0.0 – 1.0 | `0.82` | Sky exposure a surface needs before rain reaches it. Raise to keep porches dry |
| `Weather.FogStrength` | 0.0 – 1.0 | `0.35` | How much rain thickens the air |
| `Weather.FogTint` | 0.0 – 1.0 | `0.6` | How much rain drains colour from the fog |
| `Weather.CloudShadowStrength` | 0.0 – 1.0 | `0.45` | Depth of cloud shadows on the ground |
| `Weather.CloudScale` | 32 – 512 | `190` | Blocks across one cloud cell |
| `Weather.CloudDriftSpeed` | 0 – 8 | `0.9` | Cloud shadow speed, cells per minute |
| `Weather.CloudDetail` | 0.0 – 1.0 | `0.6` | Extra high-frequency shaping on volumetric clouds |
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

| Item | Level reached |
|---|---|
| Repo scaffold builds and loads in-game | **4 (renders)** |
| `ShaderPatchLoader` applies a YAML patch, logs pass/fail | **4 (renders)** — patches reach the running shader |
| Config system wired, live-tunable values | **4 (renders)** — Ctrl+V retunes without a reload |
| **Color grade:** exposure/saturation/contrast/temperature | **4 (renders)** — confirmed on 1.22.7 |
| **Color grade:** basic tonemap curve | 2 (compiles), ships off — see below |
| ConfigLib integration (optional in-game GUI) | **3 (loads)** — F7 panel lists all 11 settings with the right labels, ranges and defaults |
| **Adaptive exposure** (eye adaptation) | 2 (compiles) — 19 model checks pass, never run in game |
| **PBR:** three passes ported to C# | 2 (compiles) — 21 parity checks against the Python reference |
| **PBR:** block material classification | **3 (loads)** — 14090 blocks classified, 0 fallbacks |
| **PBR:** derived material atlas + disk cache | **4 (renders)** — 2 pages derived, uploaded and sampled in game |
| **PBR:** atlas uploaded to the GPU, bound per frame | 2 (compiles) |
| **PBR:** surface relief in `chunkopaque.fsh` | **4 (renders)** — normals visible in game via the debug views |
| **PBR:** surface relief in `chunktopsoil.fsh` (forest floor) | 2 (compiles) — anchors confirmed against the real shader |
| **PBR:** Cook-Torrance specular + energy conservation | **4 (renders)** — confirmed in game, being tuned by eye |
| **PBR:** per-layer debug views | 2 (compiles) |
| **PBR:** offline prototype validated on sample textures | **done**, 31 tests passing |
| Everything under Weather / Reflections | not started |

Levels are the ones defined in [CLAUDE.md](CLAUDE.md).

**Phase 1's milestone is met.** On a 1.22.7 install, setting
`ColorGrade.Saturation` to `0.0` renders the world fully greyscale in a live
session, with no manual shader-reload workaround, and the look survives a world
reload. That was blocked by two faults, both fixed: `GetProgramByName("final")`
never resolves the vanilla program (it is addressed by `EnumShaderProgram` id),
and the game compiles its shaders during the pre-mod bootstrap, so the mod now
requests one shader reload of its own when the hook has demonstrably seen
nothing.

`TonemapStrength` still ships at `0.0` and is the one part of colour grading
never confirmed on screen — see [Known limitations](#known-limitations).

## Known limitations

These are known now, not discovered later. Kept current as the mod grows.

- **Shader patches are version-fragile.** They regex-match vanilla GLSL. A game
  update that rewords the matched lines silently disables the affected
  subsystem (you get a loud log line, but the game will not crash). Expect to
  re-verify patches against `assets/game/shaders/` on every game update.
- **ConfigLib is integrated over the event bus, not its C# API.** Its NuGet
  package (`Maltiez.VintageStory.ConfigLib`) is not published on nuget.org, and
  the `configlib` package that *is* there is an unrelated abandoned stub. So the
  bridge listens for ConfigLib's `configlib:vintagevisuals:*` event-bus messages
  instead of referencing its assembly. Upside: nothing to resolve, so the
  optional dependency cannot fail to load. Limitation: this mod can *read*
  setting changes but cannot drive ConfigLib's GUI beyond what
  `configlib-patches.json` declares.
- **Compatibility with other shader mods is untested.** Volumetric Shading,
  Coriaender Shaders and Ancestral Bliss Shaders patch some of the same vanilla
  files. Conflicts are likely and are not yet documented.
- **Color grading runs after the vanilla tonemap**, not instead of it, because
  the patch inserts at the end of `final.fsh`. Highlights already clipped by
  vanilla cannot be recovered by lowering exposure here.
- **The material system ships switched off**, pending one look on a real GPU.
  It patches `chunkopaque.fsh`, which draws the world, and it broke that render
  repeatedly before the cause was found: non-ASCII characters in GLSL comments,
  which NVIDIA's driver rejects outright (`unexpected $end`) while
  `glslangValidator` compiles them without complaint. Guarded now at both load
  time and in the test suite. Several other real faults were fixed on the way
  there — sampler declaration order, texture-unit collisions, GL calls from
  config handlers — none of which were the one causing the symptom.
  Set `PseudoPBR.Enabled: true` (or tick it on F7) to try it; with it off the
  patch is skipped entirely and the compiler gets vanilla source.
  Multi-page block atlases are supported via a Harmony hook on the moment
  vanilla selects an atlas page; if that hook cannot be installed, a multi-page
  atlas falls back to vanilla rendering with a log line saying so.
- **Roughness and specular are derived but unused.** They sit in the atlas
  waiting for a lighting term, which needs a light direction the patch site does
  not currently provide. Today's effect is surface relief only.
- **Texture analysis infers detail, not identity.** Sobel edge detection reads
  *painted-on* shading as real geometry, and variance cannot distinguish "rough
  surface" from "busy pattern". Both are inherent to reading pixels. What pixels
  fundamentally *cannot* give — is this metal? — now comes from the block's own
  `EnumBlockMaterial` instead, which the game already knows and which covers
  modded blocks too. See [src/PseudoPBR/README.md](src/PseudoPBR/README.md).

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
