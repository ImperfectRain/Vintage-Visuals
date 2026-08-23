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

## The three layers

```
        Vintage Story                    owns environmental truth
              |
   EnvironmentTracker                    the ONE place the mod asks
              |
      AtmosphereState                    what the air IS. No config, ever.
              |
     AtmosphereInputs                    what to DRAW. Config x state x budget.
              |                          Normalisation lives here.
        22 uniforms                      one derivation, four programs
              |
      atmosphere.glsl                    one transport, eleven contributors
```

The middle layer is the one that keeps the rest honest. `AtmosphereState` may
not hold a config-scaled value - a strength slider states what the player wants,
the state describes what the world is doing, and mixing them is how "off" stops
meaning off. `AtmosphereInputs` is where the two meet and the only place they do.

Both are pure and free of every game type except plain vector maths, so the whole
derivation runs in `tools/smoketest` without a client. That property is what lets
the independence and normalisation checks exist at all.

## One transport, not eleven effects

Eleven effects applied in turn lose energy uncontrollably, and every one of the
eleven looks reasonable while it happens. They contribute to two quantities:

| | |
|---|---|
| **Extinction** | how much of the surface's own light survives the trip |
| **Inscatter** | what colour the air adds in its place |

and the frame is composed once:

```
out = surface * T + inscatter * (1 - T)
```

Vanilla's `fogWeight` enters as its own transmittance, `1 - fogWeight`, and the
mod's media multiplies it. Multiplying **transmittances** is not the mistake of
multiplying effects - it is what stacked media actually do, and it is why every
strength at zero returns exactly `mix(pixel, fogColor, fogWeight)`.

That is an algebraic identity, not a tuning coincidence, and it is the whole
safety argument for a mod owning `applyFog` in four shading programs. So it is
tested as one, across a sweep of fog weights and distances.

Extinction sources **sum**, which is what extinction coefficients do. Inscatter
gains **sum** and are capped once. Both ceilings appear twice - once in GLSL,
once in C# - and a check keeps the pair honest, because a shader cannot read a
field.

## The eleven features

| # | Feature | State | Where it runs |
|---|---|---|---|
| 1 | Aerial perspective | implemented | extinction, `atmosphere.glsl` |
| 2 | Horizon scattering | implemented | inscatter colour |
| 3 | Sun-aware scattering | implemented | inscatter gain |
| 4 | Height attenuation | implemented | extinction multiplier |
| - | Height haze | implemented | **vanilla's own height fog**, via the ambient stack |
| 5 | Weather extinction | implemented | extinction, and the colour drain |
| 6 | Cloud-atmosphere coupling | implemented | damps the directional gains |
| 7 | Cloud-edge scattering | **foundation only** | inscatter gain, keyed on partial cover |
| 8 | Godrays | **foundation only** | function and debug view exist, no write |
| 9 | Precipitation scattering | implemented | inscatter gain |
| 10 | Moon scattering | implemented | inscatter gain |
| 11 | Dapple interaction | **foundation only** | strength arrives, nothing reads it |

Every one is a single strength with no separate enabled flag. Zero is already the
"behave like vanilla" value, it is what an unset GLSL uniform reads as, and a
second flag would be a second way to say the same thing that could disagree.

A check drives the real derivation with everything on, then zeroes each feature
in turn, and fails if a feature leaks when off, does nothing when on, or moves
another feature. `CloudAtmosphere` is the one documented exception: damping the
directional terms is the whole feature.

### Aerial perspective is the distance half only

The directional half is feature 3. They are separate sliders because they are
separate phenomena, and because a master blend over the mod's whole result would
make every other feature die when this one is zero.

### Height attenuation is not height haze

Two features, two phenomena:

- **Height haze** puts a layer on the ground, through vanilla's own height fog,
  reaching the sky and the water. It needs no shader and survives the patch group
  rolling back.
- **Height attenuation** thins the air with altitude, so a mountain top sees
  further. It multiplies the whole extinction, vanilla's contribution included -
  which is the point: at altitude you see further through the air *the game* put
  there, not just through the mod's.

Vintage Story models no vertical density profile at all, so the second is an
approximation standing in for one. A third off at a mountain top: enough to read
as "the air is clearer up here", little enough that being wrong costs nothing.

### Rain and snow are different media

| | Extinction/block | Phase `g` | Reads as |
|---|---|---|---|
| Rain | 0.004 | 0.65 | hard forward scatter, bright toward the sun only |
| Snow | 0.006 | 0.15 | near-isotropic, glows in every direction |

That difference is the one the player should be able to see between a downpour
and a blizzard, rather than the same haze in a different tint.

### Cloud shadows and cloud atmosphere are not the same thing

A cloud shadow decides how much **direct light lands on a surface**. Cloud
atmosphere decides what the **air in between scatters**. They read the same cloud
state - the game's own blended density, the same figure `CloudTileReader` uses -
and they are kept apart deliberately. Collapsing them into one multiplier is how
one overcast afternoon gets darkened twice.

## Foundation only, and why

Three features have their interface, config, state field and debug view, and no
effect. This is deliberate per the project's rules: a fake simulation that a
future reader mistakes for real game state is worse than an honest gap.

**Cloud-edge scattering — DATA GAP.** The game's cloud tiles say how much cloud
sits above a place. Nothing in them locates an individual cloud's *edge*. What is
knowable is that a sky which is neither clear nor solid has edges in it
somewhere, so the feature keys on `BrokenCloud` - `4c(1-c)`, peaking at half
cover, zero at both ends. That is the most the data honestly supports. Locating
a real edge would need the cloud renderer's own geometry, which is not reachable
from a terrain fragment shader.

**Godrays — ARCHITECTURE GAP, not a data gap.** Vanilla already has crepuscular
rays: `godrays.fsh` radially blurs the frame from the sun's screen position,
weighted per pixel by the **green channel** of the glow buffer that the shading
programs write. The correct integration is a number written into that channel -
no pass, no render target, no texture unit.

But `pseudopbr.yaml` already owns the `outGlow` write in `chunkopaque.fsh`, where
it folds in the canopy shafts. A second group patching the same line is exactly
what this project forbids: whichever applies second finds its anchor gone, and
rollback couples the two. `vvAtmosGodrayLevel` is written, correct, and reachable
through debug view 9; nothing calls it in the composed frame.

**Dapple interaction — the same architecture gap.** Dapple lives in the pseudopbr
group and cloud shadows in the weather group. A function or varying shared across
patch groups couples their rollbacks. Debug view 12 draws the strength flat,
which is the honest picture: the value arrives and nothing reads it.

The resolution for both is the same and it is not "merge the groups" - it is that
whoever next revises pseudopbr's `outGlow` patch decides whether to fold the
atmosphere's godray level in, accepting that the two would then share a fate.

## Render order

| Stage | What happens |
|---|---|
| `Before`, order -1.0 | `EnvironmentTracker` samples the game. Camera every frame; climate on a 1s tick |
| `Before`, order -1.0 | `AtmosphereSubsystem` writes the ambient modifier for height haze |
| `Before`, order 0.1 | `AtmosphereShaderBinder` derives `AtmosphereInputs` **once** and uploads it to four programs |
| the game's own frame setup | Vintage Story blends the ambient stack, including this mod's modifier |
| opaque pass | `applyFog` runs per fragment. One `pow`, one `length`, one `mix` |
| `AfterPostProcessing` | `SceneCaptureRenderer` copies the finished frame for reflections |

Nothing here touches GL outside a render stage. `Before` is the one stage this
project has documented as guaranteed quiet - a binder registered where a program
is always bound skips every frame forever and says nothing, which has cost three
rounds of debugging.

## Reflections

**The capture is taken at `AfterPostProcessing`, so it already contains vanilla's
fog and this mod's atmosphere.** A reflected distant mountain therefore arrives
pre-atmospheric, and the reflection must not apply atmosphere again.

It does not. `vvSceneReflection` samples the capture and substitutes the result
into `vvAmbientSpecular`, which happens **before** `applyFog` in the fragment's
own path - so the reflected colour then receives atmosphere once, for the
distance to the *reflecting surface*, which is the correct distance. The
mountain's own atmosphere came baked into the capture; the wall's is applied
fresh.

That is right for a mirror at arm's length and slightly wrong for a mirror across
a valley, where the reflected content should carry the sum of both paths. The
error is in the conservative direction - too little atmosphere on a reflection
rather than a double dose - and it has not been measured.

## PBR

Atmosphere changes how light **travels**. PBR decides how a surface **responds**.
A check fails the build if `atmosphere.glsl` so much as mentions roughness,
metalness, albedo or the material samplers.

A nearby metal block stays a metal block. A distant one loses contrast through
extinction, applied to the composed colour after the material has had its say.

## Entities and particles

The whole point of anchoring on `applyFog`: terrain, ground cover, entities and
particles receive the **same** uniforms and run the **same** function. There is
no terrain-specific data in the entity path because there is no terrain-specific
data at all.

| Program | Position varying | In the group |
|---|---|---|
| `chunkopaque.fsh` | `worldPos.xyz` | yes |
| `chunktopsoil.fsh` | `worldPos.xyz` | yes |
| `entityanimated.fsh` | `worldPos.xyz` | yes |
| `particlescube.fsh` | `worldPos.xyz` | yes |
| `particlesquad.fsh` | `vexPos` | no - a sprite shader whose fragments are metres away |
| `chunkliquid.fsh` | `fWorldPos` | no - water is out of every patch group until the reflection is validated |
| `chunktransparent.fsh` | none | no |
| the sky, the clouds | n/a | no, and never - D6 |

The excluded programs still get **height haze, fog colour, density and floor**,
because those go through the ambient stack rather than a patch. That is the
asymmetry the whole subsystem is built around.

## Colour space

Everything here operates on vanilla's already-composed output, in the same space
`applyFog` was operating in - which is display-referred, not linear radiance. See
DECISIONS D16.

That is a known correctness problem and it is **not** compensated for here. The
transport arithmetic is the physically correct form applied in the wrong space,
which is a defensible place to be only because it is exactly where vanilla's own
fog already sits: this mod's atmosphere and the game's are wrong in the same way,
so they agree. Fixing the space is a separate piece of work that would move both.

No atmospheric constant in this file exists to cancel a colour-management error.

## Debug views

`Atmosphere.AirDebugView`, or "Atmosphere debug view" in the F7 panel.

The list is built to separate four questions that look identical on screen:

| Question | Views |
|---|---|
| Is the **game** giving us the wrong data? | 2, 8, 11 |
| Are we **normalising** it wrongly? | 2, 6, 7, 8 |
| Is the **shader** wrong? | 3, 4, 5, 9, 10, 13 |
| Is the **tuning** wrong? | 1, 13 - and only once the others are ruled out |

| | Draws |
|---|---|
| 1 | Final atmosphere |
| 2 | Raw state: sun elevation red, overcast green, precipitation blue |
| 3 | Transmittance. White is clear air |
| 4 | The horizon colour alone |
| 5 | Sun scattering gain |
| 6 | Height factor |
| 7 | Added extinction against its own ceiling |
| 8 | Overcast red, broken cloud green |
| 9 | Godray contribution |
| 10 | Precipitation gain |
| 11 | Moonlight red, moon-facing gain green |
| 12 | Dapple strength, flat - foundation only |
| 13 | Combined: transmittance red, total gain green, distance blue |

The property is `AirDebugView` and not `DebugView` because PseudoPBR already has
a `DebugView`, and the smoke check that pairs a slider with its config clamp keys
on the property name. Two properties sharing one gives that check two clamps for
one slider, and its response to an ambiguity is to skip - so the collision would
have silently removed the coverage rather than failing.

## What drives the haze
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

## Temporal stability

Nothing here samples the screen, uses noise, or accumulates across frames, so
crawling and shimmer cannot occur by construction. Checks fail the build if a
texture read or a temporal term appears, because either would be a new class of
instability that needs a decision rather than a commit.

What *can* go wrong is a discontinuity in the arithmetic - a term that switches
where it should ramp - and the two places it would are both moments the player is
guaranteed to be watching: the sun crossing the horizon, and weather starting or
stopping. Both are swept numerically in `AtmosphereChecks`, comparing the largest
single-step change against the step size. A ramp stays proportional; a switch does
not.

The below-horizon guard is a ramp over roughly seven degrees for exactly this
reason. A `step()` there would pop the whole scattering term on at sunrise.

**One hitch is real and expected.** Dragging any shader-side strength through zero
crosses `WantsShader`, which adds or removes the patch group and reloads shaders.
That is the documented cost of gating the patch rather than muting a uniform, and
it matches how `PseudoPBR.Enabled` already behaves.

**Not tested:** everything above is source-level. No transition has been watched
in a world.

## Configuration

All fifteen settings appear in the F7 panel under Atmosphere, and every one is a
plain strength where 0 means vanilla. There is no separate enabled flag per
feature: zero is already the vanilla value, it is what an unset GLSL uniform reads
as, and a second flag would be a second way to say the same thing that could
disagree.

`configlib-patches.json` is unvalidated data with a silent failure mode - two
settings sharing a `weight` blanks the **whole** panel rather than one row, and
nothing reports it. Three checks guard the file: weights are unique, every
property is wired into `ConfigLibBridge`, and no case handles a setting the panel
does not define.

**Godray quality is the one tier here, and it scales contribution rather than
cost.** The sample count belongs to vanilla's own pass and the player's graphics
settings, not to this mod, so a claim to make that pass cheaper would be a lie. It
can never switch godrays on: at contribution 0 nothing is contributed whatever the
quality says.

## Data provenance

Every atmospheric input, where it comes from, and what happens to it. Nothing in
this table is modelled, approximated or invented; where something had to be, it
says so.

| Input | Source | Raw | Update | Normalisation | GPU |
|---|---|---|---|---|---|
| Fog colour | `Ambient.BlendedFogColor` | RGBA 0..1 | every frame | none | via `rgbaFog`, vanilla's own |
| Fog density | `Ambient.BlendedFogDensity` | per-block coefficient, ~0.00125 | every frame | **none, deliberately** - it is a coefficient and the shader uses it as one | `vv_atmosDensity` |
| Fog floor | `Ambient.BlendedFogMin` | 0..1 added after distance | every frame | none | via `fogAmount` |
| Height fog | `Ambient.BlendedFlatFogDensity` / `...YPosForShader` | signed coefficient, world Y | every frame | none | vanilla's own uniforms; **written** by `AmbientBridge` |
| Sun direction | `IClientGameCalendar.SunPositionNormalized` | unit vector | every frame | none | `vv_atmosSunDir` |
| Sun colour | `IClientGameCalendar.SunColor` | normalised RGB | every frame | none | `vv_atmosSunColor` |
| Sun elevation | `SunPositionNormalized.Y` | -1..1 | every frame | clamped to 0..1 - below the horizon reads as 0, not as negative | `vv_atmosSunElevation` |
| Daylight | `DayLightStrength` | 0..1 | 0.1s tick | none | folded into `vv_atmosSunScatter` |
| Moon direction | `MoonPosition` | **un-normalised** vector | every frame | normalised in double on the CPU, once per frame rather than once per fragment | `vv_atmosMoonDir` |
| Moon light | `MoonPhaseBrightness` x `(1 - daylight)` | 0..1 | 0.1s tick | scaled by darkness, so a full moon at noon is 0 | `vv_atmosMoonLight` |
| Rain | `EnvironmentState.Rain` | 0..1 | 0.1s tick, eased over seconds | already 0..1 | `vv_atmosRain` |
| Snow | `EnvironmentState.Snow` | 0..1 | 0.1s tick, eased over seconds | already 0..1 | `vv_atmosSnow` |
| Cloud cover | `Ambient.BlendedCloudDensity` | density, not coverage - reads "nearly clear" under a full sky | 0.1s tick | gained x2 **once**, in `EnvironmentTracker`, so there is one copy of that number in the codebase | `vv_atmosOvercast` |
| Broken cloud | derived from cover | - | 0.1s tick | `4c(1-c)`, peaking at half cover | `vv_atmosBrokenCloud` |
| Camera altitude | `DefaultShaderUniforms.PlayerToSealevelOffset` | blocks, signed | every frame | divided by 180 blocks and clamped - **an approximation**, the game models no density profile | `vv_atmosAltitude` |
| Sky exposure | `GetLightLevel(OnlySunLight)` | 0..max | 0.1s tick | divided by `SunBrightness` | **not uploaded** - applied on the CPU, so a second application cannot double-gate |
| Far plane | `DefaultShaderUniforms.ZFar` | blocks | every frame | none | not uploaded; the 250-block fog clamp is what matters |
| Temperature, humidity | `GetClimateAt` | degrees C, 0..1 | **1s tick** | none | not uploaded - used only for the haze pressure |

### Update frequencies, and why they differ

**Every frame:** anything that moves with the camera or the sun. Camera altitude
and the fog blend change continuously, and a value that moves with the camera
going stale for a tenth of a second is the exact shape of the bug that made rain
ripples swim across the ground.

**Every 0.1s tick:** weather, daylight, cloud cover, sky exposure. These are
eased over seconds or minutes anyway, so a tenth of a second of lag is invisible,
and the light-level lookup is a chunk query rather than a field read.

**Every 1s:** the climate query, which is the one genuinely expensive call here.
Biomes are not crossed in under a second.

Every sampler is guarded and holds its **last good value** on failure. A climate
query that starts throwing is a reason to stop reading it, not a reason to claim
the player has been teleported to a temperate plain.

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

## Use() binds, and this binder loops

`IShaderProgram.Use()` **binds** the program and **throws** if a different one is
already bound — and the client does not recover.

This binder uploads to four programs in a loop. It bound the first and never
unbound it, so the second iteration threw
`Already a different shader (chunkopaque) in use!` and took the client down on
world load.

The guard at the top of `OnRenderFrame` did not help. It exists for the case
where *someone else* holds a program; by the second iteration the offending bind
was this binder's own.

Nothing static caught it. 794 checks passed, all 48 prefix combinations compiled,
every mutation test was green — because the defect is in no shader and no value.
It is a lifecycle that only exists at runtime, in the one binder that iterates
more than two programs.

`tools/smoketest/ShaderBindingChecks.cs` now guards it, and guards the two
near-misses as well: a `Stop()` placed *after* the loop pairs correctly and still
crashes, and an early `return` between `Use()` and `Stop()` leaks a bound program
by another route.

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
| Per frame, CPU | ~20 field reads, one dictionary lookup, one derivation, 22 uniform uploads x 4 programs |
| Per fragment | 1-4 `pow` (one per active gain term), one `length`, one `log`, one `exp`, a few `mix`. On fragments already going through `applyFog` |
| Render passes | **None.** No new pass, no fullscreen quad |
| Render targets | **None** |
| Texture units | **None.** Adding a sampler to `chunkopaque.fsh` has twice cost this project the entire world render |
| Texture reads | **None** |
| Uniform components | ~30 floats. `chunkopaque` also carries weather's 576-float cloud window, well inside the 1024 OpenGL 3.3 guarantees |
| Shader patches | Two per program, in four programs. Skipped entirely when no shader feature is on |
| Quality tiers | Godrays only, and it scales contribution rather than cost - the sample count belongs to vanilla's pass |
| **Measured** | **No.** Nothing in this project has been profiled. Every number above is an operation count |
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
