# Visual tests

Level 4 - "it renders" - is the only level that closes anything in this project,
and it is also the weakest link in it. Every other level has a tool behind it;
level 4 currently means *a developer looked at the game*, which is not
repeatable and does not survive a refactor.

This page makes it repeatable. It is not automated image diffing, which is a
long way off for a mod that cannot run headless. It is a fixed set of scenes and
a fixed set of things each feature must be seen to do.

## The scene matrix

A feature is not "seen working" until it has been seen in the scenes that could
break it. Most only need a handful of cells.

| Axis | Values |
|---|---|
| **Place** | surface open · forest · desert · snowfield · shallow cave · deep cave · built structure · water's edge |
| **Time** | noon · sunset · night · dawn |
| **Weather** | clear · rain · storm · snow |
| **Material** | stone · wood · metal · soil · foliage · water · snow |

Save a screenshot per cell you exercise, from the same position and angle, with
the mod on and off. The pair matters more than the shot: **"looks good" is not a
result, "different from vanilla in exactly the intended way" is.**

## Acceptance criteria

Each feature lists what must be *seen*, not what must compile. A criterion that
cannot fail is not a criterion.

### Wetness

- [ ] stone visibly darkens in rain
- [ ] highlights sharpen as roughness collapses
- [ ] stone under an overhang stays dry while the ground beside it does not
- [ ] wetness persists for about a minute after the rain stops
- [ ] snowfall below freezing does **not** wet the ground

### Rain ripples

- [ ] rings appear on wet up-facing ground and nowhere else
- [ ] a wall beside a puddle shows none
- [ ] drops are scattered, not on a grid, and not all in phase
- [ ] they stop the moment the shower does, while the ground stays wet
- [ ] no visible change at 500,000 blocks from the world origin (the float32 trap)

### Cloud shadows

- [ ] a shadow sits under the cloud casting it
- [ ] shadows travel the way the clouds travel
- [ ] gaps between shadows remain, at every cloud cover
- [ ] no shadows at night
- [ ] no uniform darkening anywhere - the failure this has shipped twice

### Entity lighting

- [ ] a mob's shading matches the ground it stands on
- [ ] sunlight, torchlight and cave light all read correctly on it
- [ ] fur and skin do not read as metal
- [ ] a mob in rain darkens

### Foliage translucency

- [ ] a canopy with the sun behind it glows
- [ ] the same canopy with the sun behind the camera does not
- [ ] leaves in shadow do not glow
- [ ] stone is unaffected

### Crevice shading

- [ ] mortar lines, plank gaps and bark furrows read as grooves
- [ ] ridges are **not** darkened - that is an edge detector, not cavity
- [ ] a flat painted texture stays flat
- [ ] the effect fades with distance rather than shimmering

### Emissive materials

- [ ] a forge reads as hot rather than merely bright
- [ ] its light reaches the blocks around it
- [ ] metal near it catches a warm highlight
- [ ] wet ground near it reflects it
- [ ] flicker is visible but not distracting
- [ ] `EmissiveStrength = 0` is indistinguishable from vanilla

### Environmental layers

- [ ] wet stone films over and goes glossy; a wet leaf **darkens** and does not
- [ ] snow settling on wet ground reduces the wetness rather than sitting on it
- [ ] a frosted rail is rough and loses its highlight, and is not a white blob -
      vanilla already tinted it, so doubling the tint is the failure to watch for
- [ ] `FrostStrength = 0` is indistinguishable from vanilla's own frost
- [ ] snow dusting lands on flat up-faces and slides off angled ones
- [ ] no rain ripples on any leaf
- [ ] no crevice darkening on any leaf

### Seasons

- [ ] leaf colour through the year is **unchanged from vanilla** - if it differs
      at all, something here is recolouring and must not be
- [ ] the material response fades in rather than popping on the day a season turns
- [ ] a southern-hemisphere world reads autumn at the same time it does in vanilla

### Particles

- [ ] a falling leaf catches the sun the way the canopy it fell from does
- [ ] sparks and embers read as hot rather than merely bright
- [ ] dust in a shaft of light is lit by that light
- [ ] no twinkling on a moving cloud of dust - the failure this restraint exists for
- [ ] `ParticleLighting = false` is indistinguishable from vanilla

### Adaptive grading

- [ ] golden hour warms without the whole scene turning orange
- [ ] night desaturates and cools rather than merely dimming
- [ ] going indoors is a transition, not a cut
- [ ] a desert reads hot, a tundra cold, without either becoming a colour filter

### The budget

The one that needs the worst-case cell, because it exists for exactly that:

- [ ] **storm + night + underground + cold biome** is still playable
- [ ] the same scene with every subsystem off is not dramatically brighter
- [ ] no single effect has visibly taken the whole allowance

### Readability

- [ ] a cave remains navigable at every weather and time
- [ ] a hostile mob approaching in heavy rain is visible before it is in range
- [ ] no exposure flash on a lightning strike that costs navigation

## Flora scene matrix

Ten scenes, none of them run. Every row below is **unverified** - the
classification is checked against the game's own shader by `tools/smoketest`,
which proves the mod and vanilla agree about what a plant is, and proves nothing
about what it looks like.

Set `PseudoPBR.DebugView` to 44 first in every scene. If the flat colours do not
follow the plants, nothing further down is worth judging.

| # | Scene | What to look for | Result |
|---|---|---|---|
| 1 | Open meadow, noon | Grass tips brighter than roots, not a flat glow. Restrained - midday is the case that reads as neon when the transmission is too strong | – |
| 1b | Open meadow, sunrise and sunset | Backlit grass visibly transmitting; front-lit grass not | – |
| 1c | Open meadow, overcast | Transmission nearly gone. There is no directional sun to come through | – |
| 2 | Dense forest, noon | Canopy shade with sparse bright openings. View 46: red canopy, green undergrowth | – |
| 2b | Dense forest, sunset | Warm upper canopy, cool shaded interior | – |
| 2c | Dense forest, rain | Darker, wetter, subdued flecks. Leaves darker AND sharper-highlighted, not merely darker | – |
| 2d | **Dense forest, after rain** | The flagship. Foliage still wet, ground still wet, sun returning, wet leaves catching sharp highlights | – |
| 3 | Forest edge | Larger mixed areas rather than a hard line. The transition is where a bad canopy measure shows | – |
| 4 | Forest floor | **The regression this pass fixed.** Sunflecks must fall on the tall grass and the soil alike. If the grass is evenly lit while the soil is dappled, the understory test is not reaching it | – |
| 5 | Flower meadow, backlit | Petals transmit; colours stay saturated and do not wash toward yellow-green | – |
| 6 | Crops, sunlight and wind | Distinct from wild grass. Lighting pattern moves because the crop moves | – |
| 7 | Night forest, torchlit | **Canopy must not extinguish torchlight.** Leaves near a torch lit; no transmission from a sun that is not there | – |
| 8 | Snow and frost | Silhouette and material identity preserved, not a blue tint | – |
| 9 | Distance: near, medium, far | No shimmer or crawl on distant foliage. Atmosphere, not a foliage-specific fog | – |
| 10 | Modded / resource-pack flora | Renders safely. Anything with a wind mode is classified; anything unrecognised gets the conservative middle | – |

### Fruit

A hanging pear, backlit, at sunset. It must **not** glow green. This is the
single clearest one-object check that the taxonomy is live: before it, fruit
carried a wind mode, was called foliage, and was tinted toward yellow-green on
the way out.

### The three benchmarks

**A - sunrise through forest.** Warm low sun, foliage backlit, canopy blocking
direct light, real gaps making flecks, floor receiving irregular warm light,
distant trees losing contrast. Should read as *sunlight interacting with a
forest*, not as a yellow filter over green blocks.

**B - rain to clearing to sunset.** Rain: dark, wet, subdued. Stops: still wet,
air clearing. Sun breaks: transmission rises, wet leaves catch sharp highlights,
canopy gaps brighten, wet floor reflects. Sunset: warm upper foliage, cool
shaded interior.

**C - torchlit forest at night.** Silhouettes dimensional, nearby leaves lit by
the torch, canopy not extinguishing block light, no phantom sun transmission.

### Temporal checks, all unverified

Stationary camera; walking; rotating; wind alone; wind plus movement. Watch for
crawling flecks, shimmer at distance, popping as shadow cascades switch, and
flashes as the sun crosses the horizon.

The dapple field is derived from vanilla's shadow map and carries no clock or
noise of its own, so it should move only because the leaves move. Anything that
moves while the world is still is a defect, not a setting.

## Reporting a result

In `STATUS.md`, a feature moves to **L4** only with the scenes it was seen in.
"Renders" on its own is the claim this process exists to stop.
