# Atmosphere

The air between the camera and everything else.

Present on a clear day at noon; weather modifies it. That ordering is the whole
reason the subsystem exists. Before it, fog was something rain switched on
inside two terrain shaders, so a valley filling with haze left the animals
standing in it with crisp edges, and a new weather type would have had to add
its own special case rather than inherit the rendering.

## The finding that shaped this

**Vintage Story already has most of an atmosphere, and it is reachable from the
CPU.**

`IAmbientManager` blends a stack of named `AmbientModifier`s into the fog the
game actually renders, every frame. The stack is public and writable. What it
produces feeds *every* shading program the game has - including `sky`,
`chunkliquid`, `entityanimated` and the particle shaders, none of which this mod
patches and none of which any GLSL it could inject would reach.

So the rule for this subsystem is sharper than the project's usual one:

> If a value can be expressed as an ambient modifier, it belongs there and not
> in a shader - not because it is easier, but because it is the only way to get
> an answer that is consistent across the frame.

What the audit found, verified against `VintagestoryAPI` and the dumped shaders:

| Wanted | Vanilla already has it | Where |
|---|---|---|
| Distance fog | yes | `BlendedFogDensity`, `fogDensityIn`, per-vertex `getFogLevel` |
| Fog colour | yes | `BlendedFogColor`, `rgbaFog` |
| A fog floor | yes | `BlendedFogMin`, `fogMinIn` |
| **Height-banded fog** | **yes** | `BlendedFlatFogDensity` / `...YPosForShader` -> `flatFogDensity` / `flatFogStart` |
| Sun colour, reddened near the horizon | yes | `IClientGameCalendar.SunColor`, with a per-day `SunsetMod` |
| Local volumetric fog | yes | `fogSpheres`, 3 of them |
| Horizon fog on the sky | yes | `horizonFog`, `getFogAmountForSky` |
| Directional in-scattering | **no** | nothing computes it |

The last row is the only genuine gap, and it is the only thing here that would
justify GLSL. It is not implemented yet.

## What is implemented

**Height haze.** One number pair written into the game's ambient stack, and
vanilla renders it. `flatFogDensity` is written *negative*: that is the game's
own sign convention, not a trick - the sky shader branches on
`flatFogDensity < 0` to add an earth-curvature bias, which only makes sense for
a layer lying on the ground.

Off by default. See Status.

## Files

| File | Role |
|---|---|
| `AtmosphereSubsystem.cs` | Lifecycle, the render tick, and the claim on the haze budget |
| `AmbientBridge.cs` | Owns the modifier in the game's ambient stack. Installs, writes, verifies, removes |
| `../Common/Scene/AtmosphereState.cs` | The snapshot. What the air is doing, read from the game rather than modelled |

`AtmosphereState` lives in `Common/Scene/` beside `EnvironmentState` rather than
here, because it is shared state that any subsystem may read - the same reason
the environment tracker is not inside Weather.

## What drives the haze

Nothing in Vintage Story models ground haze, so unlike cloud shadows - which
read the game's own cloud tile array - there is nothing here to be faithful to.
The shape is invented, which puts it at the bottom of the information ladder in
`docs/VISUAL-LANGUAGE.md` by necessity rather than by choice. It is worth being
plain about that.

The terms are all facts the game supplies, and they multiply:

| Term | Why |
|---|---|
| Humidity (worldgen rainfall) | asks what KIND of place this is. A rainforest hazes, a desert does not, and neither changes because it rained yesterday |
| Wetness | carries the recent weather, so a valley hazes after rain |
| Wind, inverted | disperses it. This is the term that stops the haze being a constant the player learns to ignore |
| Daylight, inverted | burns it off, so it is a morning and evening thing |
| Sky exposure | keeps it out of caves, where "ground level" means nothing |

It is arbitrated in `SceneArbiter` as a **secondary** claim on the haze role.
Weather owns that role: on the rainy still night both want, the rain is the thing
the player can see a cause for, so it claims first and ground haze takes what is
left, damped.

## The one thing this cannot see

The blend itself. `IAmbientManager` documents it as

```
blended = w * modifier.Value + (1 - w) * blended
```

folded over the modifiers in order - and a documented formula in an interface
comment is not a tested one. The entire safety argument for writing into a
dictionary shared with the game and every other mod rests on a weight of `0`
being a true no-op.

So `AmbientBridge` reproduces the fold once per install and compares it against
the game's own `BlendedFogDensity`, logging a warning if they disagree. Fog
density rather than flat fog density, because it is the field with a non-zero
value in a default world and therefore the one where a wrong fold shows.

## Failure behaviour

| Failure | Result |
|---|---|
| `Ambient` or `CurrentModifiers` unavailable | Logged once, height haze off, everything else unaffected |
| The modifier is dropped from the stack | Re-added on the next tick. Nothing documents the dictionary's lifetime, and a stack rebuilt on a world change would stop the feature silently |
| The blend does not match | Logged as a warning. The feature still runs; the warning says the strength may be wrong |
| Feature switched off | The modifier is **removed** from the stack, not zeroed |

## Cost

| | |
|---|---|
| Per frame | A handful of field reads and a dictionary lookup |
| Render passes | None |
| Texture units | None |
| Shader patches | None |
| Measured | **No.** Nothing in this project has been profiled |

## Status

**L2 - implemented, compiles, tested statically, not seen in a world.**

`Atmosphere.HeightHaze` defaults to **0**, which is vanilla exactly. A haze layer
that turned out to sit at the wrong height would be the first thing a player
noticed about the mod, and the height is the one number here that no test can
settle - `AtmosphereChecks` proves the haze goes *below* the band using vanilla's
own transcribed formula, but whether 34 blocks above sea level is the right place
for its top is a question only a screenshot answers.

**Aerial perspective is not implemented.** It is the one atmospheric effect
vanilla does not have and the one that needs GLSL. See `docs/STATUS.md`.
