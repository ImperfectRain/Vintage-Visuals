# Weather

What the weather does to how the world looks.

## Status

**Wet surfaces in rain.** Clouds, cloud shadows and sky tint are not started.

| Piece | State |
|---|---|
| Wetness model (`WetnessTracker`) | done, level 2 (compiles) |
| Rain response in the material shaders | done, level 2 (compiles) |
| Sky-exposure varying | done, verified against the real `.vsh` files |
| Fog and sky tint by weather state | not started |
| Cloud shadows | not started |
| Volumetric cloud shaping | not started |

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
