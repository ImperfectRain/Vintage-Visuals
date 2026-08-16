# ColorGrade

Filmic tonemapping and look controls applied to the composed frame.

## What it does

Patches vanilla `final.fsh` to run every output pixel through a grading
function before it is written. Five controls, applied in this order:

```
exposure -> white balance -> tonemap -> contrast -> saturation
```

The split is not arbitrary. Exposure and white balance are *scene-referred*
operations: they describe how much light reached the sensor, so they belong
before the tonemap or its shoulder rolls off the highlights exposure was meant
to recover. Contrast and saturation are *look* operations on the display-referred
result, which is also why the contrast pivot is 0.5 rather than the 0.18
mid-grey you would use in linear space.

## Inputs / outputs

| | |
|---|---|
| **Input** | `outColor` as vanilla `final.fsh` leaves it |
| **Output** | the same variable, graded |
| **Uniforms** | `vv_enabled`, `vv_exposure`, `vv_contrast`, `vv_saturation`, `vv_temperature`, `vv_tonemapStrength` |
| **Config** | `ColorGrade` section of `ModConfig/vintagevisuals.json` |
| **Patch group** | `colorgrade` (from `assets/vintagevisuals/shaderpatches/colorgrade.yaml`) |

## How the patch works

Instead of guessing where vanilla writes its final pixel — a line that moves
between versions — the patch renames vanilla's `main()` to `vvSceneMain()` and
appends a new `main()` that calls it and grades the result. That reduces the
version coupling to two facts:

1. `final.fsh` contains `void main(void) {`
2. its color output is named `outColor`

Fact 2 is *asserted* by a deliberately semantic no-op patch that matches
`out vec4 outColor;` and rewrites it to itself plus a comment. If vanilla
renames the output, that anchor stops matching, the whole group rolls back to
vanilla, and this subsystem switches itself off. Without the assertion the
appended `main()` would reference an undeclared variable and fail to *compile*,
which costs the player every shader instead of just this effect.

## Failure behaviour

Three independent things must be true before any grading happens, and each has
its own log line when it is not:

- the Harmony hook installed (`ShaderPatchingAvailable`)
- the patch group applied cleanly (`ShaderPatcher.IsGroupHealthy`)
- the compiled program actually exposes `vv_enabled` (`IShaderProgram.HasUniform`)

The third is the ground truth — a shader can pass patching and still fail to
compile downstream. If it is false, the GLSL never reached the GPU.

Additionally, `vv_enabled` reads as `0` when uniforms were never uploaded (an
unset GLSL uniform is zero), and the grading function bails out on that. So a
failure to upload degrades to *vanilla output*, never to a black screen.

## Testing it standalone

1. Set `EnableShaderDebugDump: true` in the config, restart, and read
   `VintagestoryData/ShaderDebug/final.fsh`. That file is what reached the GLSL
   compiler, prefix code and all — driver error line numbers refer to it.
2. Set `ColorGrade.Saturation` to `0.0` and press <kbd>Ctrl</kbd>+<kbd>V</kbd>.
   The world should turn greyscale immediately, with no world reload. This is
   the cheapest end-to-end proof that patching, config and uniform upload all
   work.
3. Set it back to `1.0`, press <kbd>Ctrl</kbd>+<kbd>V</kbd>, confirm it returns.

## Known limitations

- **The color space of the input is unconfirmed.** The ACES curve expects
  linear, scene-referred input. Whether `final.fsh`'s output is still linear
  where this grades it has not been checked against a running game, so
  `TonemapStrength` **defaults to 0** and the other four controls — which are
  space-agnostic enough to be useful either way — default to neutral. Confirm
  this and update the default; it is the single most valuable thing to verify
  in this subsystem.
- **Grading runs after vanilla's own tonemap and bloom**, not instead of them.
  Highlights already clipped upstream cannot be recovered by lowering exposure
  here.
- **White balance is a two-multiply approximation**, not a chromatic adaptation
  transform. It is monotonic and cheap and adequate for a control the player
  nudges by eye; it is not colorimetrically correct.
- **The HUD is assumed to be drawn after this pass** and therefore ungraded.
  Not yet verified — if the hotbar changes color when saturation is dropped,
  this assumption is wrong and the patch needs to move.
