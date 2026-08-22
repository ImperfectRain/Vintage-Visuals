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

**Aerial perspective.** The one thing on the list above vanilla does not have,
and so the only thing here with any GLSL. Vanilla's fog is
`mix(pixel, rgbaFog.rgb, fogWeight)` - isotropic, so haze looking into a low sun
and haze looking away from it are the same grey. This adds a Henyey-Greenstein
in-scattering term and **nothing else**: no distance curve, no height band, no
desaturation, because vanilla already has the first two and the third belongs to
`VisualBudget`.

**Weather visibility.** Rain thickening the air. Not new - it moved here from the
weather group, which patched the two terrain shaders and nothing else. That is
why an animal standing in a fogged valley used to keep crisp edges while the
hillside behind it went soft. Weather still decides how much; it no longer
renders it.

Both haze features are off by default. See Status.

## The patch group

One anchor, four programs. Vanilla's `applyFog` is **byte-identical** in all
seven shading programs the game has, which makes it the single point every fogged
fragment passes through.

`applyFog` is not handed a position, so each program supplies its own varying at
the call site:

| Program | Varying | Patched |
|---|---|---|
| `chunkopaque.fsh` | `worldPos.xyz` | yes |
| `chunktopsoil.fsh` | `worldPos.xyz` | yes |
| `entityanimated.fsh` | `worldPos.xyz` | yes - the program this move was for |
| `particlescube.fsh` | `worldPos.xyz` | yes |
| `particlesquad.fsh` | `vexPos` | no - a sprite shader whose fragments are metres away |
| `chunkliquid.fsh` | `fWorldPos` | no - water is out of every patch group until the reflection is validated |

`AerialPerspective` gates the **patch**, not the effect. A strength of 0 skips
the whole group, so "off" is vanilla's own function rather than this mod's
arithmetic multiplied by nothing. That costs a shader reload when the value
crosses zero, which is the reason it is a strength rather than a tick box.

## Debug views

Numbered in this subsystem's own terms, per the project convention.
`Atmosphere.AirDebugView`, or "Atmosphere debug view" in the F7 panel.

| | Draws |
|---|---|
| 1 | Distance, as a fraction of vanilla's own 250-block fog clamp |
| 2 | The phase function alone. White facing the sun |
| 3 | The final in-scattering gain |
| 4 | The fog colour actually used, in-scattering included |
| 5 | The fog weight actually used, weather included |
| 6 | The sun direction, remapped so nothing is negative |
| 7 | The sun's own colour, flat |
| 8 | The rain term, so a slider that does nothing shows as black |

The property is `AirDebugView` and not `DebugView` because PseudoPBR already has
a `DebugView`, and the smoke check that pairs a slider with its config clamp keys
on the property name. Two properties sharing one gives that check two clamps for
one slider, and its response to an ambiguity is to skip - so the collision would
have silently removed the coverage rather than failing.

## Files

| File | Role |
|---|---|
| `AtmosphereSubsystem.cs` | Lifecycle, the render tick, and the claim on the haze budget |
| `AmbientBridge.cs` | Owns the modifier in the game's ambient stack. Installs, writes, verifies, removes |
| `AtmosphereShaderBinder.cs` | Uploads the aerial-perspective uniforms into the four patched programs |
| `../../assets/vintagevisuals/shadersnippets/atmosphere.glsl` | The in-scattering term and the weather fog, at vanilla's `applyFog` |
| `../../assets/vintagevisuals/shaderpatches/atmosphere.yaml` | One anchor, four programs |
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
| Per frame, CPU | A handful of field reads, a dictionary lookup, and eight uniform uploads into four programs |
| Per fragment | One `pow`, one `length`, and a `mix`, on fragments that were already going through `applyFog` |
| Render passes | None |
| Texture units | None |
| Shader patches | Two per program, in four programs. Skipped entirely at strength 0 |
| Measured | **No.** Nothing in this project has been profiled |

## Status

**L2 - implemented, compiles, tested statically, not seen in a world.**

`Atmosphere.HeightHaze` defaults to **0**, which is vanilla exactly. A haze layer
that turned out to sit at the wrong height would be the first thing a player
noticed about the mod, and the height is the one number here that no test can
settle - `AtmosphereChecks` proves the haze goes *below* the band using vanilla's
own transcribed formula, but whether 34 blocks above sea level is the right place
for its top is a question only a screenshot answers.

**Aerial perspective compiles in every prefix combination** the game supports -
48 for `chunkopaque` and `entityanimated`, 24 for `chunktopsoil`, 6 for
`particlescube` - verified by `tools/verifypatches` against the game's own
shaders, with all groups applied together as the game applies them. That proves
it compiles and that its anchors still match 1.22.7. It proves nothing about
whether it looks right.

`Atmosphere.AerialPerspective` defaults to **0**, which skips the patch group
entirely.

**Water and sprite particles are excluded.** Named in `atmosphere.yaml` so they
read as excluded rather than forgotten.
