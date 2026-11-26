# Water

The water surface on vanilla's liquid program: patch group `water`
(`assets/vintagevisuals/shaderpatches/water.yaml`) injecting
`shadersnippets/water.glsl` into `chunkliquid.fsh`. Decision record: D71.

## What it builds on

Vanilla's water already has a lot, and all of it stays: the animated texture
and flow, foam from the opaque depth, vertex waves, rain droplets, a fresnel
alpha, a broad sun highlight and the whole lava branch. This group runs on the
finished water colour, after all of that and just before vanilla's fog. Lava
never reaches it.

## What it adds, in order

1. **Sky and horizon reflection, texel-locked.** The water texel (1/32 block,
   the water texture's own pixel) is the unit: its wave normal, its view vector
   and its reflection direction are evaluated at the texel centre, and the sky
   is looked up in the terrain's direction cells with the terrain's analytic
   horizon. One colour per water pixel, as on blocks. Composed by Fresnel
   (F0 0.02): the reflected share arrives as the sky colour, the rest meets the
   water body, and out alpha is `F + bodyAlpha * (1 - F)`, so a reflection can
   never make water more transparent (checked). The view vector starts at the
   EYE (`vv_waterEye`, recovered from `CameraMatrixOriginf`): `fWorldPos` has
   the player at the origin, and from the feet a pond at standing height is
   edge-on and mirrors the whole sky, which is how the first build looked
   (D73). The sky image is capped at the sky's own brightness, and below about
   0.18 of elevation it gives way to the surroundings at 0.42 of the sky light (D76),
   because near-horizontal rays meet the banks (DATA GAP until a scene trace).
2. **Softness** (`ReflectionSoftness`): averages the reflection seen 3 texels
   away in four directions. Each texel stays one flat colour; neighbours step
   less sharply. Quality tier sets the taps (1 at Low = sharp only).
3. **Depth colour** (`DepthTint`, `ShallowClarity`): the path behind each texel
   from vanilla's own `depthTex`, sampled at the texel centre's screen position.
   Red is absorbed first, opacity grows with depth, water under about a block
   is clearer than vanilla.
4. **Rain ripples** (`RainRipples`): the terrain's ripple field, open water
   only, while rain falls. Each drop is hashed by its cell AND its drop number
   (`RippleEpoch` counts turns of the 1.5 s clock), so drops land somewhere new
   every time and the rain amount sets how many fall (D73). A ring's crest
   raises the reflected share at any angle, so drops read looking down too.
   While the ring slider is above 0, vanilla's own droplet rings (a fixed grid
   that replays in the same cells) are multiplied out, so water and land show
   one drop system (D75).
5. **Glints** (`Glints`): GGX from `pbrcore.glsl` for the sun and the moon on
   the texel normal, shadow-aware, hidden by the game's cloud cover, broken into
   per-texel glitter and shouldered at a luminance of 1.2 (D76). Vanilla's broad sun highlight steps back to a
   quarter at full strength (`vvWaterVanillaSpecularKeep`).
6. **Caustics** (`Caustics`, D77): the bed seen through a top-face texel (from
   vanilla's `depthTex`) gets the sunlight six short focusing waves gather onto
   it, a refraction model (`vvWaterCausticGain`, 0 on flat water) sampled where
   the refracted sun entered the surface, snapped to 1/32 block. Gated by the
   glints' visible-sun term and the surface shadow, added to the colour after
   the opacity is final. Fades from 1.5 to 5 blocks of depth, with the view
   path through the water and with distance, and falls on slopes and walls by
   N.L (the bed's normal from its screen slope). DATA
   GAP: the bed's colour is assumed. Tier: 3 waves on Low, 6 above.
7. **Shoreline** (`Shore`, D79): the distance from the texel to the bank or bed
   behind it, measured across that surface; the water thins out over the last
   0.06 block, and a per-texel foam line laps in and out, composited over the
   water. Vanilla's edge foam steps back to 0.35 (`vvWaterVanillaFoamKeep`).

8. **The world in the water** (`SceneReflections`, group `waterscene`, D82):
   each top-face texel's reflected ray, from the eye and following 0.4 of the
   wave tilt, is projected with vanilla's matrices, cut at the screen edge and
   marched by the shared `vvReflectMarch` (reflect.glsl) over the LIVE opaque
   frame: Primary's colour, bound on unit 10 by `WaterSceneBindInterceptor`
   at the moment vanilla binds `depthTex` (the water draws into the
   Transparent framebuffer, so that frame is complete and not being written),
   and vanilla's `depthTex`. A hit replaces the sky after the roof factor; a
   miss keeps the sky. Steps 0/16/24/40 by tier.
9. **Refraction** (`Refraction`, same group): the bed behind the texel moved
   along the wave slope (to 2 blocks of path), sampled from the same frame,
   covering the same share of the transmitted light it replaces.

Rivers (D79): vanilla's flow vector streaks the wave slope along the current
and quickens the chop and the glitter; still water in still air calms to 0.6.
Far swells (two ~12-block waves) blend in from 20 to 60 blocks.

Waves: four travelling waves with whole cycles per 4096 blocks plus two
value-noise chop octaves on wrapped cells, so the CPU origin wrap is seamless.
Wind sets their height.

## Rules it follows

- **Zero is vanilla.** `vv_water` is the master and the first branch in
  `vvWaterSurface`; the specular keep factor is 1 while it is 0.
- **Off is vanilla source.** `Water.Enabled` gates the patch group
  (`IsPatchGroupEnabled`), and toggling it reloads shaders.
- **No sampler in `water`.** `depthTex` is vanilla's. The one mod sampler,
  `vv_waterScene`, lives in the `waterscene` group, declared below vanilla's
  last (`liquidDepth`), so switching the world reflection off removes it from
  the source.
- **Shared code is copied, not shared at runtime**, because each group must
  roll back alone. The ripple and sky-cell functions are parity-checked against
  `pbrterrainmaterial.glsl` in `tools/smoketest/WaterChecks.cs`.
- **One scene upload path.** The scene block (daylight, overcast, wind,
  moonlight) reaches chunkliquid through `PbrShaderBinder.UploadSceneTo`.

## Debug views (`Water.DebugView`)

1 texel normal, 2 reflected sky image, 3 depth behind the surface (8 blocks is
white), 4 reflected share x4, 5 glints alone, 6 the water texel grid, 7 caustic
light on the bed, 8 foam (red), flow (green) and far swell (blue), 9 the world the reflected ray
found (black is a miss).

## Status

L2: builds, compiles against the game's own `chunkliquid.fsh` in every define
combination (foam on and off), checked by `WaterChecks`, previewed offline with
the game's water texture. First seen in game 2026-11-25 and failed (white
daylight water, drops on fixed spots); D73 fixes both and is **not yet seen in
game. Performance not measured** - use
`.vvbench` at the water test scene in `docs/VISUAL-TESTS.md`.

D82 (the world reflected, refraction) is L2: previewed offline with banks
and trees, not seen in game, cost not measured. Not done: underwater; the
caustics still assume the bed's colour although the frame now holds it.
