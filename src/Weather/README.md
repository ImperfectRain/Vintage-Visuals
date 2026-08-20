# Weather

What the weather does to how the world looks.

## Status

**Wet surfaces, rain fog, cloud shadows and cloud shaping.** All verified
against the game's own shaders with `tools/verifypatches`; none seen on screen
yet beyond wetness.

| Piece | State |
|---|---|
| Wetness model (`WetnessTracker`) | done, level 2 (compiles) |
| Rain response in the material shaders | done, level 2 (compiles) |
| Sky-exposure varying | done, verified against the real `.vsh` files |
| Fog and sky tint by weather state | done, level 2 (compiles) |
| Cloud shadows | done, level 2 (compiles) |
| Volumetric cloud shaping | done, level 2 (compiles) |

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

An earlier version patched `sky.fsh` the same way, reasoning that rain should
thicken the whole scene. It does not work, and the reason is worth keeping.

The sky dome is not something you look *through* - it is the thing at the far
end. Fogging it flattens the contrast between cloud and sky into a uniform haze,
so clouds stop reading as clouds and become a blanket with the cloud layer's
tile seams showing through in perspective. It happened with the classic and the
volumetric renderer alike, because neither of them was the problem: the sky
behind them was.

Vanilla already has `horizonFog` for the sky's own weather response. The
`weathersky` group is gone.

## Cloud shadows

Applied by **wrapping `getBrightnessFromShadowMap`** rather than by editing the
places light is used. A cloud occludes the sun, so it belongs wherever the sun's
occlusion is already decided — every caller picks it up, including this mod's
own specular term, and no line another patch group has already rewritten needs
touching. That last part is not a nicety: `pseudopbr` owns the lighting call in
both terrain shaders, and two groups editing one line means whichever runs
second finds its anchor gone.

Three practical decisions:

- **Cover moves the threshold, not the amplitude.** A clear sky gets no shadows
  at all rather than faint ones everywhere, and an overcast sky goes fully
  shaded rather than uniformly grey. Cover comes from the game's own
  `RainCloudOverlay`, so shadows agree with the sky above them.
- **The shadow is projected along the sun direction**, walking from the fragment
  up to the cloud deck. At a low sun that is a long way sideways, which is what
  makes cloud shadows read as three-dimensional rather than as a texture on the
  ground.
- **The deck sits at 160 blocks, not at vanilla's cloud altitude.** Using the
  real height slides the shadow almost a kilometre at a low sun, which reads as
  a bug.

Noise is vanilla's own `gnoise`, already compiled into both shaders — so this
costs instructions rather than a texture fetch or a second implementation to
keep in step.

## Volumetric cloud shaping

A small intervention in vanilla's raymarcher, not a replacement for it. Its
traversal, lighting and depth handling are all doing work there is no reason to
redo; shape and density are the parts that read as weather.

- **A third noise octave** in `octave()`. Vanilla mixes two frequencies, which
  gives a smooth billow and no edge; a third at roughly three times the second
  breaks the silhouette into the ragged fringe real cumulus have. Blended rather
  than added, so 0 is exactly vanilla and sharpening a cloud does not also
  inflate it.
### The density patch, and why it is gone

A second patch scaled vanilla's density term inside `volume()`. It was removed
after it deleted every cloud in the sky.

`volume()` has exactly one caller, and its result gates `if (v > 0.0)` - the
test deciding whether a cloud is drawn at all. So an unset `vv_cloudDensity` did
not mean "vanilla density", it meant **no clouds anywhere**.

The rule it broke is the one this whole mod runs on: an unset GLSL uniform reads
as zero, and a uniform can be unset for many reasons - the binder skipped, the
program was not patched, a group rolled back - so zero has to be the harmless
value. Every other uniform here satisfies that. Rain 0 is dry, cloud shadow 0 is
unshadowed, cloud detail 0 is vanilla's own two octaves. That one inverted it,
and the failure was total rather than partial.

Density can come back, through a term where zero is harmless rather than fatal,
once the shaping patch has been seen working.
