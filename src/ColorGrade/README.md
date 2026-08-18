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
| **Uniforms** | `vv_enabled`, `vv_exposure`, `vv_contrast`, `vv_saturation`, `vv_temperature`, `vv_tonemapStrength`, `vv_adaptation` |
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

Grading activates on two conditions, each with its own log line when unmet:

- the vanilla `final` program resolves (see below)
- that compiled program exposes `vv_enabled` (`IShaderProgram.HasUniform`)

The second is deliberately the *only* evidence consulted about whether the patch
landed. `ShaderPatchingAvailable` and `ShaderPatcher.IsGroupHealthy` record what
this mod believes it did; `HasUniform` is direct evidence of what actually
reached the compiled program, and is strictly stronger. Requiring all three
meant a reset of our own bookkeeping — a shader reload rebuilds the patch groups
— could veto a shader that was in fact correctly patched. A shader can pass
patching and still fail to compile downstream, and only `HasUniform` catches
that.

Additionally, `vv_enabled` reads as `0` when uniforms were never uploaded (an
unset GLSL uniform is zero), and the grading function bails out on that. So a
failure to upload degrades to *vanilla output*, never to a black screen.

## Testing it standalone

1. Set `EnableShaderDebugDump: true` in the config, restart, and read
   `VintagestoryData/ShaderDebug/final.fsh`. That file is what reached the GLSL
   compiler, prefix code and all — driver error line numbers refer to it.
2. Set `ColorGrade.Saturation` to `0.0` and press <kbd>Ctrl</kbd>+<kbd>V</kbd>.
   The world turns greyscale immediately, with no world reload. This is the
   cheapest end-to-end proof that patching, config and uniform upload all work,
   and it is the check that closed Phase 1's milestone. With ConfigLib
   installed, dragging the Saturation slider on <kbd>F7</kbd> does the same
   thing through the same code path.
3. Set it back to `1.0`, press <kbd>Ctrl</kbd>+<kbd>V</kbd>, confirm it returns.

## Eye adaptation

`AdaptiveExposure` multiplies `Exposure` — it does not replace it — so a
manually dialled exposure survives. Dark surroundings raise the multiplier
toward `DarkGain`, bright ones settle it at `BrightGain`.

It is driven by the **game's light level at the player**, not by measuring the
rendered frame. Measuring the frame is the textbook approach, but it needs a
luminance reduction (mip chain or downsample pass) plus a GPU→CPU readback to
smooth over time — a render pipeline of its own, and this mod deliberately owns
no framebuffers. `MaxTimeOfDayLight` at the player's head is a direct measure of
the same thing an eye would adapt to, costs one block lookup, and needs no
readback.

The honest trade-off: it reacts to *where the player is*, not to what is on
screen. Staring at bright sky from inside a dark room will not stop it down. For
cave-to-surface transitions — the case players actually notice — the two agree.

Brightening and darkening have separate time constants, because human adaptation
is asymmetric: adapting to light takes seconds, adapting to dark takes minutes.
That asymmetry is most of what makes it read as an eye rather than a fade.

Ticks at 10 Hz and uploads only when the value moved by more than 1e-3, so once
adaptation settles it costs a block lookup and an early return. Easing uses
exponential smoothing on a time constant, so speed does not change with frame
rate — `tools/smoketest` checks that 10 Hz and 100 Hz land in the same place.

## Resolving the vanilla program

`IShaderAPI.GetProgramByName("final")` returns **null**. That lookup covers
name-registered programs, which in practice means ones a mod registered; the
vanilla passes are addressed by their `EnumShaderProgram` id, so this subsystem
uses `GetProgram((int)EnumShaderProgram.Final)`.

Getting this wrong was invisible rather than loud, and that is worth
remembering: the grading function bails out when `vv_enabled < 0.5`, and an
unset GLSL uniform reads as exactly `0`. So "never uploaded the uniforms" and
"correctly disabled" render *identically*. The fail-safe is still right — a
failed upload must not black out the screen — but when debugging, trust
`HasUniform` and the `uniforms uploaded, active=...` log line, not your eyes.

## Known limitations

- **The color space of the input is unconfirmed.** The ACES curve expects
  linear, scene-referred input. Whether `final.fsh`'s output is still linear
  where this grades it has not been checked against a running game, so
  `TonemapStrength` **defaults to 0** and the other four controls — which are
  space-agnostic enough to be useful either way — default to neutral. Confirm
  this and update the default; it is the single most valuable thing left to
  verify in this subsystem. Everything else here is confirmed rendering.
- **Grading runs after vanilla's own tonemap and bloom**, not instead of them.
  Highlights already clipped upstream cannot be recovered by lowering exposure
  here.
- **White balance is a two-multiply approximation**, not a chromatic adaptation
  transform. It is monotonic and cheap and adequate for a control the player
  nudges by eye; it is not colorimetrically correct.
- **The HUD is assumed to be drawn after this pass** and therefore ungraded.
  Not yet verified — if the hotbar changes color when saturation is dropped,
  this assumption is wrong and the patch needs to move.
