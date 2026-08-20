# Weather

What the weather does to how the world looks.

## Status

**Wet surfaces, rain fog and cloud shadows.** All verified against the game's
own shaders with `tools/verifypatches`; only wetness has been seen on screen.

| Piece | State |
|---|---|
| Wetness model (`WetnessTracker`) | done, level 4 (renders) |
| Rain response in the material shaders | done, level 4 (renders) |
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

### What comes from the game, and what does not

The shadow field is the mod's own noise. It is **not** the map vanilla places
its clouds from, and it cannot be: that map is built on the CPU and handed to
the renderers as `mapData1`/`mapData2` (see `cloudmap.fsh`), so a terrain shader
has no way to sample it. Both cloud renderers, classic and volumetric, read the
clouds' positions from there.

What can be taken from the game without it is everything except position, and
both halves are:

- **How much of the sky is covered** - `capi.Ambient.BlendedCloudDensity`, the
  same figure the cloud renderers are driven by. The climate map's
  `RainCloudOverlay` used to fill this role and was wrong for it: it is only the
  storm component, so it reads as clear on an ordinarily cloudy day.
- **Which way the clouds are going** - the wind at the player, which is what
  pushes vanilla's cloud tiles along. Drift is a vector accumulated along it,
  so the shadows and the clouds travel the same way. Below a breath of wind the
  direction is numerical noise, so the last heading is held rather than letting
  the shadows shimmer in place.

Matching the sky cloud for cloud would need the tile array out of
`VintagestoryLib`. Matching how much of it is covered and where it is heading
does not, and is most of what the eye reads from the ground.

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
