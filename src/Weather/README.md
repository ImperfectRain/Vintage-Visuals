# Weather

What the weather does to how the world looks.

## Status

**Wet surfaces, rain fog and cloud shadows.** All verified against the game's
own shaders with `tools/verifypatches`; only wetness has been seen on screen.

| Piece | State |
|---|---|
| Wetness model (`WetnessTracker`) | done, level 4 (renders) |
| Rain response in the material shaders | done, level 4 (renders) |
| Rain ripples in standing water | done, level 2 (compiles) |
| Overcast light response | done, level 2 (compiles) |
| Sky-exposure varying | done, verified against the real `.vsh` files |
| Fog and tint by weather state | terrain only, level 2 (compiles) |
| Cloud shadows | reworked, level 2 (compiles) |
| Cloud shaping | **removed** - not possible from a cloud shader, see below |

## Wet surfaces

This subsystem owns **no shader patch of its own**. Rain changes how surfaces
respond to light, and the material system already models that response — so
wetness is published as one number and the PseudoPBR shader consumes it, rather
than duplicating a lighting path to say the same thing twice.

That is also why it reads as weather at all. A wet surface is not the dry one
with water drawn on top. Three things change, and all three are already inputs
to the microfacet model:

| | | |
|---|---|---|
| **roughness** | collapses to ~0.08 | water fills the microscopic pits that scatter light |
| **specular** | rises toward 0.60 | a smooth water film is far more reflective than stone |
| **albedo** | darkens to ~0.72 | light entering the film scatters inside it and less comes back out |

Miss the darkening and wet stone reads as *polished* stone. It is the least
obvious of the three and the one that sells it.

### Where rain can reach

Two gates, both physical:

- **Rain falls downward**, so it pools on up-facing surfaces, runs off vertical
  ones and never touches undersides. Squared, so the falloff is steep rather
  than linear.
- **Rain cannot reach what the sky cannot see.** Vanilla already knows this —
  `rgbaLightIn.a` is the per-vertex sun light level, 0 under a roof and 1 in the
  open — but it lives in the vertex shader and never reaches the fragment stage.
  So this is the first patch in the mod to touch a `.vsh`.

That varying lives in **`pseudopbr.yaml`, not a weather group**, and the
distinction matters. A varying is a contract between the two stages of one
program: if the vertex half could roll back independently, the fragment shader
would declare an input nothing writes and the program would fail to *link* —
costing the world, not the feature. Both halves succeed or both roll back, and
a smoke test asserts it.

Debug view 10 shows wetness directly. An overhang should read black while the
ground beside it reads white.

## Drying

The asymmetry is the whole effect. Surfaces wet in about **8 seconds** and dry
over **60** by default; easing both at the same rate reads as a fade rather than
as weather. Exponential smoothing on a time constant, so the result does not
depend on frame rate.

`WetnessTracker` is deliberately free of every game type, like
`AdaptiveExposure`, so that behaviour can be checked without a client.

Two thresholds worth knowing:

- **Below freezing there is no wetness.** The same precipitation falls as snow,
  and a snowstorm making the ground look rained on would be the wrong effect at
  exactly the moment the player is most likely to be watching the sky.
- **Rainfall below 0.04 is ignored.** The game reports drizzle nobody sees, and
  without a floor surfaces sit permanently faintly wet.

Wetness is not linear in rainfall — it rises steeply and flattens. Light rain
already darkens and glosses a surface almost as much as heavy rain does; what
heavy rain adds is runoff and puddles, which this does not model.


## Rain ripples

Rain landing in the water it left. Like wetness, this owns no shader patch: it
perturbs the normal the material system already computes, because a ripple is a
disturbance of the water film rather than of the stone under it, and what the
eye actually reads is the **highlight it breaks up** - not any visible
displacement.

**One drop impact per grid cell**, returned as a slope rather than a height.
The lighting reads a normal, and building a height field only to difference it
costs three more evaluations for an answer the slope gives directly. Two grids
at unrelated scales and rates, because one grid on its own reads as a grid.

Two hashes per cell, and both earn their place:

- The first decides whether the cell is being rained into **at all**. This is
  what makes heavy rain visibly denser rather than merely faster - sampled, it
  moves the disturbed fraction of ground from 8% in light rain to 36% in heavy.
- The second offsets the cell's phase. Without it every drop in the world lands
  on the same frame.

Three gates, all different questions:

| gate | asks |
|---|---|
| wetness | can rain reach this surface at all |
| `faceNormal.y` squared | did what reached it *stay* - a wall sheds it |
| `vvDetailFade` | is a cell still bigger than a pixel at this distance |

The last one is not optional. Ripples are the highest-frequency thing this
shader produces and therefore the first to alias into sparkle.

Driven by the rain **falling**, not by the wetness left behind: ripples stop the
moment the shower does, while the ground stays wet for another minute. Vanilla's
`windWaveCounter` is the clock - already declared in both chunk shaders and
already uploaded every frame, so there is no second clock to drift out of step
with one. A patch asserts that name, so a rename fails the group rather than
taking the shader down with an undeclared symbol.

### Everything about this field was a precision problem

The first version produced drops that appeared **at the same time and in the
same place on every block**, which is the one thing rain never does. Neither
half was the hash, which was the obvious suspect and measured fine.

- **Position.** Vintage Story worlds run to roughly half a million blocks from
  the origin. A float32 at that magnitude resolves about **sixteen distinct
  positions inside one ripple cell** - so `fract()` of it gave the same handful
  of values in every cell and the ring collapsed into a coarse stamp repeated
  identically everywhere. The camera origin is now wrapped to 4096 blocks in
  double on the CPU, before it ever becomes a float. Wrapped, a cell holds two
  thousand positions. The whole frame stays continuous; the pattern shifts once
  per 4096 blocks travelled.
- **Time.** Vanilla's `windWaveCounter` was the obvious clock and is the same
  trap: it accumulates without bound, and past about ten million a float32
  cannot separate two phases **at all**, so every drop in the world lands on
  the same frame. The clock is now advanced in double and handed over
  pre-wrapped to 0..1.

Both octave scales divide the wrap period exactly - cells of half a block and
of one block - so the wrap lands on a cell boundary rather than cutting a ring
in half. A smoke check pins the GLSL constant against the C# one.

### Scattered, not stamped

Precision alone would still have left a lattice: one drop per cell, all at cell
centres. Each cell now draws four values from a bit-mixing integer hash - where
the drop landed, when, and how often that cell is hit - and the ring is measured
from the landing point rather than the cell centre. Sampled over 1600 cells:

| | |
|---|---|
| drop offset from cell centre | 0.19 cells mean, 0.35 max |
| distinct phases | 1506 of 1600 |
| impact rates | three, split evenly |
| cells showing a fresh drop at any instant | 14% |

Rates are whole numbers of drops per wrap of the clock, not a continuous
multiplier - anything else would jump when the clock wrapped.

### Tuned against measurements, not by eye

The first pass was **fifteen times too weak to see** - a median tilt of 0.2
degrees and a peak of 9, which no highlight would notice. Transcribing the field
into Python and sampling it is how that was caught before shipping rather than
after. The shipped constants measure as:

| | tilt |
|---|---|
| median | 0.5 deg (most ground is between rings) |
| 90th percentile | 5 deg |
| crest of a fresh drop | ~38 deg |
| ground disturbed past 2 deg | ~25% at full rain |

An average small enough that still water stays still, and a peak large enough to
scatter the specular where a drop just landed.

Debug view 11 shows the field directly: rings appearing and dying on up-facing
wet ground and nowhere else, so a wall beside a puddle is the check that all
three gates work.

## Overcast light

What cloud cover does to the *quality* of light, which is a different question
from what cloud shadows do to its quantity.

A clear sky lights the world with a small, very bright source - that is what
makes a sharp highlight. An overcast sky replaces it with a source the size of
the sky: dimmer per unit area and arriving from every direction. So the direct
lobe loses most of its strength (down to 35% under full overcast) and the sky
term gains half again.

The redistribution is the point. Modelling an overcast day as "everything gets
darker" is the usual way of getting it wrong: what actually happens is that
shadows go soft and surfaces flatten, while the total light barely changes.

Driven by the same cover figure the cloud shadows use, so the two agree about
what kind of day it is.

## Rain fog

Rain thickens the air and drains the colour out of it. Two details matter:

- **Fog is added as a fraction of what is left**, not as a sum. Heavy rain
  approaches full fog without ever exceeding it; a plain addition makes distant
  terrain pop to solid grey the moment a shower starts.
- **The fog colour is shifted, not replaced.** Vanilla's already tracks time of
  day, biome and altitude, and a fixed rain grey would fight every sunset it was
  drawn over. Rain pulls it toward its own luminance and slightly blue.

Driven by `Rain`, not by wetness. Fog belongs to the rain that is *falling*;
wetness belongs to the rain that *fell*. One number for both would leave the air
thick with fog for a minute after the sky cleared — the wrong half to linger.

### Terrain only, never the sky

Rain fog is applied to the two terrain shaders and to nothing else. See "The
clouds themselves are vanilla" below for what happened when it was applied to
the sky dome as well.

## Cloud shadows

Applied by **wrapping `getBrightnessFromShadowMap`** rather than by editing the
places light is used. A cloud occludes the sun, so it belongs wherever the sun's
occlusion is already decided - every caller picks it up, including this mod's
own specular term, and no line another patch group has already rewritten needs
touching. That last part is not a nicety: `pseudopbr` owns the lighting call in
both terrain shaders, and two groups editing one line means whichever runs
second finds its anchor gone.

### They come from the game's own clouds

Three versions of this were a noise field of the mod's own, and the last of them
failed for a reason no amount of tuning fixes: **an invented field cannot line
up with clouds it knows nothing about.** Cover and drift direction were taken
from the game, which got the amount and the heading right and the positions
wrong, and "not representative of the actual clouds" is the correct verdict on
that.

Cloud placement in **both** renderers comes from one per-tile array the CPU
builds and uploads as `mapData1`/`mapData2` - `cloudmap.fsh` turns it into the
`cloudMap` the volumetric raymarcher shades, and the classic renderer builds its
quads from the same data. Nothing a fragment shader can reach decides where a
cloud is. So `CloudTileReader` reads that array.

It lives in `VintagestoryLib`, which is explicitly not a stable API, so the
reader is built to survive it moving:

- The renderer is found by **type name** (`*CloudRenderer*`), searched across
  the client's fields and through any collection among them - not by field name,
  and not by assuming it is held directly.
- The tile array is found by **shape**: an array field whose element type is
  called something like `CloudTile`.
- The density member is picked from a priority list (`SelfThickness`,
  `Thickness`, ...) with a contains-match fallback, then multiplied by an
  opacity member if one exists - mirroring `cloudmap.fsh`'s own
  `cloudOpaqueness * min(1, 10 * selfThickness)`.
- Every failure logs **once, with the member names it did find**, so a version
  that renames something is a one-round fix rather than another guess.
- Any failure falls back to the noise field. Worse, but not nothing.

**No origin field has to be found.** The game's grid follows the camera -
`cloudmap.fsh` places tile *(i, j)* at `mapOffset + (i - width/2) * 50`, and
`mapOffset` tracks the player - so the centre of the array is where the player
is, and the window's world corner follows from the player's own position snapped
to the 50-block tile grid.

Tile values are whatever integer scale the game happens to use, so they are
normalised against a **decaying peak** rather than a guessed divisor: it settles
on the busiest sky seen recently, and an emptying sky fades instead of rescaling
itself brighter.

### Getting 256 numbers to the shader

The window is 24 tiles square - 1200 blocks - and travels as a **uniform array
of 144 vec4s**, not a texture. The size is set by the throw, not by the view
distance: a shadow lands `climb / tan(elevation)` blocks along the sun's
azimuth, which at a 20-degree morning sun is around 400, so a 16-tile window put
every early and late shadow into its own edge fade.

That is a deliberate choice against the obvious one. Adding a second sampler to
`chunkopaque.fsh` is the change that has twice cost this project the entire
world render, once through link-time unit reassignment and once through a
texture-unit collision. It is not a trade worth making for 256 numbers.

The lookup is bilinear with a smoothstep weight. Vanilla's clouds are drawn from
50-block tiles and look like it, which is part of the art direction; their
*shadows* looking like it is not, and a hard tile edge on the ground reads as a
bug rather than as a cloud.

`vv_cloudMapValid` is uploaded either way and its zero selects the fallback, so
a program that never received the window - unpatched, unbound, rolled back -
behaves exactly as a failed tile read does rather than sampling an array of
zeros as though it meant a cloudless sky.

`Weather.CloudsFromGame` turns the whole path off and returns to the noise
field.

### Two failures, one cause

The field shipped wrong twice in opposite directions, and both times the cause
was the same: **a threshold applied to a distribution nobody had measured.**

A sum of gradient noise octaves is not spread evenly over its range. Vanilla's
2D `gnoise` peaks near +-0.7 but spends nearly all its time inside +-0.2, and
the weighted three-octave sum used here has a standard deviation of **0.150**
around zero - measured, not estimated.

- **First version - the blanket.** The raw sum was mapped onto 0..1 (giving a
  field with a standard deviation of 0.075, hugging 0.5) and thresholded with a
  band 0.36 wide centred on the threshold. At cover 0.7 the mean shade came out
  at **0.967, with a minimum of 0.04** - the entire world darkened, no gaps
  anywhere. Not a shadow with an edge; an everywhere-dimmer world.
- **Second version - nothing at all.** The distribution was expanded 2.3x and
  the band narrowed, which was right, but coverage was driven straight from
  `BlendedCloudDensity` as though it were a fraction of sky. It is not - it is
  the game's density parameter, and it sits low. At a realistic 0.05-0.2 the
  threshold landed at 0.86-0.90 and the mean shade was **0.4% to 2%**:
  invisible.

The fix is to normalise the field by the deviation it actually has (x1.55, so
its standard deviation is 0.224 and it uses the range), and to move the
threshold through the band the field genuinely occupies rather than through
0..1. Measured across the whole cover range:

| cover | threshold | ground in shadow |
|---|---|---|
| 0.00 | 0.80 | 6% |
| 0.25 | 0.68 | 14% |
| 0.50 | 0.55 | 30% |
| 0.75 | 0.43 | 51% |
| 1.00 | 0.30 | 72% |

Both ends stop short of the extremes deliberately. Overcast in vanilla is still
a cloud *layer* with thin patches in it, and a clear sky still has the odd cloud
in it - a day with no shadow anywhere is rarer than either. And because the
range gives visible shadows even at cover 0, a miscalibrated cover input can no
longer take the feature to zero; it only decides how much *more* shade a cloudy
day gets.

### Sun directionality

A cloud's shadow is its cross-section projected along the sun ray. Two things
follow, and the field does both:

- **The shadow is offset**, by walking from the fragment up to the cloud deck
  along the sun direction. At a low sun that is a long way sideways.
- **The shadow is stretched along the sun's azimuth**, by the reciprocal of the
  sun's elevation, capped at 3x. With the sun overhead a shadow is the cloud's
  own footprint; late in the day it is that footprint smeared into a long streak
  pointing at the sun. Without this the shadows keep their noon shape all day
  and merely slide, which reads as a texture scrolling under the world rather
  than as something being cast.

### Registration: the part that has never worked

The cloud shadow field has three separable jobs, and only the first two have
ever been right.

1. **Density** - how much of the sun a tile blocks. This mirrors the game:
   `min(1, cloudOpaqueness * min(1, 10 * selfThickness))`, exactly what
   `clouds.vsh` writes into `rgbaCloud.a`. The inner `min` saturates, which is
   why vanilla's sky reads as solid tiles with sharp edges rather than as a
   gradient.
2. **Direction** - which way the shadow is thrown. This uses vanilla's own
   `lightPosition`, the same vector the terrain shader lights every face with,
   so it follows the sun and the moon without this code having any notion of
   where either one is.
3. **Registration** - *where the tile window sits in the world*. This has been
   wrong every time, and it is the only one that decides whether a shadow lands
   under the cloud that cast it.

Registration failed twice for different reasons:

- **Mixed coordinate spaces.** The window corner was handed to the shader as a
  true world coordinate and compared against a position built from
  `vv_cloudOrigin`, which is the camera position *wrapped to 4096 blocks* - it
  has to be, because a float32 cannot resolve a Vintage Story world coordinate.
  The residue is whatever multiple of 4096 the player is past, so the lookup
  landed thousands of tiles outside a sixteen-tile window and every fragment
  read clear sky. At spawn the residue is zero and it works. The corner is now
  **camera-relative** (`vv_cloudMapCorner`), which removes the question instead
  of answering it.
- **The corner is still assumed, not read.** It is taken to be the player's
  position snapped down to the 50-block grid. `clouds.vsh` places each tile at a
  camera-relative offset the CPU hands it directly, so the renderer knows the
  true answer - and since the game's clouds drift on the wind, a corner derived
  from the player's position alone cannot be complete. The reader now logs every
  member of the cloud renderer that could hold that offset, with live values.

### When they do not appear

**Debug: cloud diagnostic** on F7 (`Weather.CloudDebugView`) draws a diagnostic
instead of shading. Each view answers one question, because four rounds of this
have been spent blaming a different multiplied term each time.

| View | What it draws | What it tells you |
|---|---|---|
| 1 | The field the shadow uses, thrown along the light, at full strength with vanilla's shadow map out of the way | Is the GLSL running at all, or merely too faint |
| 2 | The tile field **straight down** - no throw toward the sun, no edge fade | **The calibration test.** Stand still, look up, look down. Matching pattern means the window is registered; a shifted pattern means the corner is off by that much; no resemblance means the array is not what this assumes |
| 3 | The sampling window's own tile grid and edge band | Where it is looking, rather than where it is assumed to look |

View 2 is the one that matters. Every other unknown - deck height, throw
distance, strength, vanilla's shadow - is downstream of it, and tuning any of
them while registration is wrong cannot help.

### The cloud deck height is a guess

`Weather.CloudHeight` is a slider, not a reading. The game does not expose the
altitude it draws clouds at: `IAmbientManager` carries `BlendedCloudDensity` and
`BlendedCloudBrightness` and no Y position at all, and the renderer that knows
lives in `VintagestoryLib`. The height decides how far a shadow is thrown from
the cloud casting it, so a wrong value mis-places every shadow except at noon -
which is exactly why diagnostic view 2 removes the throw entirely.

The throw is also capped at 320 blocks. The true projection runs to infinity at
the horizon and the window is 1200 blocks across, so an uncapped throw walks
out of the data and every shadow disappears at the hour they would be longest.
Shadows shorten instead, and since strength is already scaled by daylight, the
hours where the cap bites are the hours the effect is fading out anyway.

Alongside it, the binder now logs what it is actually driving the shadows with,
once and again on change, and warns if it has been unable to upload for several
seconds. A binder that silently returns is indistinguishable from one that is
working, which is how three rounds were lost.

### The rest of it

- **The shadow is projected along the sun direction**, walking from the fragment
  up to the cloud deck. At a low sun that is a long way sideways, which is what
  makes cloud shadows read as three-dimensional rather than as a texture on the
  ground.
- **The deck sits at 160 blocks by default, not at vanilla's cloud altitude**,
  and is now a setting. It is what decides how far a shadow slides from what
  casts it, so it is the control for whether shadows sit under the clouds or off
  to one side. Vanilla's clouds are far higher; using their real altitude moves
  the shadow the better part of a kilometre at a low sun, which reads as a bug
  rather than as evening.
- **Strength is scaled by daylight on the CPU.** A cloud shadow is the sun being
  blocked; at night there is nothing to block. Folding it into the strength
  keeps that as one uniform whose zero already means vanilla, rather than a
  second one the first depends on being uploaded.

Noise is vanilla's own `gnoise`, already compiled into both shaders - so this
costs instructions rather than a texture fetch or a second implementation to
keep in step.

## The clouds themselves are vanilla

The mod patches **no cloud shader and no sky shader**. Two attempts to change
how clouds look have been made and both are gone; what they cost is worth
keeping written down.

### Fogging the sky (`weathersky`)

An earlier version patched `sky.fsh` the same way as the terrain, reasoning that
rain should thicken the whole scene. The sky dome is not something you look
*through* - it is the thing at the far end. Fogging it flattens the contrast
between cloud and sky into a uniform haze, so clouds stop reading as clouds and
become a blanket with the cloud layer's tile seams showing through in
perspective. It happened with the classic and the volumetric renderer alike,
because neither of them was the problem: the sky behind them was. Vanilla
already has `horizonFog` for the sky's own weather response.

### Reshaping the clouds (`cloudshape`)

A patch replaced `octave()` in `cloudvolumetric.fsh` with a version carrying a
third noise frequency, on the theory that this would break the smooth billow
into a ragged cumulus fringe. It could not have. Two things were wrong with it,
and only the second was visible from the diff:

- **Cloud shape is not decided in the cloud shader.** `cloudvolumetric.fsh`
  fetches shape out of `cloudMap`, which `cloudmap.fsh` builds from the per-tile
  `mapData1`/`mapData2` arrays the CPU uploads. The raymarcher shades what those
  arrays already placed. There is no noise call anywhere in the silhouette.
- **`octave()` is only reachable from `warp()`**, which perturbs the ray
  *direction*, and which early-returns on `if (f < 0.0001)`. Its only caller
  passes `PerceptionEffectIntensity * 0.03`, and that is zero unless the player
  is under a perception effect. In normal play the function never ran.

So a slider sat in the config panel reading "Cloud detail 1.000" while doing
nothing at all, which is worse than doing something wrong - it invites the
player to blame it for whatever else they are seeing. Both the group and the
setting are gone, and the clouds are byte-for-byte vanilla in both renderers.

The lesson generalises past clouds: **check that a function is reachable before
patching it.** Grep its call sites in the dumped shader, and follow the guards.
`tools/verifypatches` confirms a patch compiles, not that the code it changed is
ever executed.

### The density patch, and why it is gone

A patch also scaled vanilla's density term inside `volume()`. It was removed
after it deleted every cloud in the sky.

`volume()` has exactly one caller, and its result gates `if (v > 0.0)` - the
test deciding whether a cloud is drawn at all. So an unset `vv_cloudDensity` did
not mean "vanilla density", it meant **no clouds anywhere**.

The rule it broke is the one this whole mod runs on: an unset GLSL uniform reads
as zero, and a uniform can be unset for many reasons - the binder skipped, the
program was not patched, a group rolled back - so zero has to be the harmless
value. Every other uniform here satisfies that: rain 0 is dry, cloud shadow 0 is
unshadowed. That one inverted it, and the failure was total rather than partial.

If clouds are ever to be restyled, the place to do it is the tile data, not the
shader that draws it.


## Readability, and the creature in the fog

Rain fog is arbitrated against a shared budget rather than applied at its config
strength, and one of the things that shrinks that budget is **something being
nearby**.

The point is narrow and worth stating precisely, because the obvious version of
this idea is a different game. Vintage Visuals will not outline a threat,
highlight it, or draw anything the player would not otherwise see. All it does
is **decline to obscure information the player already had**: heavy weather
gives up some of its fog while a creature is within about twenty-four blocks.

The signal is deliberately imprecise. It counts living non-player creatures
rather than trying to decide which are hostile, because Vintage Story has no
authoritative "is this dangerous" flag and a hand-maintained list of creature
codes would be wrong for every mod that adds one - exactly the kind of invented
answer this project avoids. A deer at twenty blocks costing a little fog is a
cheap false positive. A drifter hidden by fog is not.

Four smoke checks pin it: company raises readability, company raises restraint,
company changes **nothing else** about the scene, and the storm actually gives
up fog as a result.
