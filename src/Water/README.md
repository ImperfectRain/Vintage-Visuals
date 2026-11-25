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
   never make water more transparent (checked).
2. **Softness** (`ReflectionSoftness`): averages the reflection seen 3 texels
   away in four directions. Each texel stays one flat colour; neighbours step
   less sharply. Quality tier sets the taps (1 at Low = sharp only).
3. **Depth colour** (`DepthTint`, `ShallowClarity`): the path behind each texel
   from vanilla's own `depthTex`, sampled at the texel centre's screen position.
   Red is absorbed first, opacity grows with depth, water under about a block
   is clearer than vanilla.
4. **Rain ripples** (`RainRipples`): the terrain's ripple field, open water
   only, while rain falls.
5. **Glints** (`Glints`): GGX from `pbrcore.glsl` for the sun and the moon on
   the texel normal, shadow-aware. Vanilla's broad sun highlight steps back to a
   quarter at full strength (`vvWaterVanillaSpecularKeep`).

Waves: four travelling waves with whole cycles per 4096 blocks plus two
value-noise chop octaves on wrapped cells, so the CPU origin wrap is seamless.
Wind sets their height.

## Rules it follows

- **Zero is vanilla.** `vv_water` is the master and the first branch in
  `vvWaterSurface`; the specular keep factor is 1 while it is 0.
- **Off is vanilla source.** `Water.Enabled` gates the patch group
  (`IsPatchGroupEnabled`), and toggling it reloads shaders.
- **No sampler.** `depthTex` is vanilla's. The scene-capture reflection will
  come as its own group, with its sampler below vanilla's last (`liquidDepth`).
- **Shared code is copied, not shared at runtime**, because each group must
  roll back alone. The ripple and sky-cell functions are parity-checked against
  `pbrterrainmaterial.glsl` in `tools/smoketest/WaterChecks.cs`.
- **One scene upload path.** The scene block (daylight, overcast, wind,
  moonlight) reaches chunkliquid through `PbrShaderBinder.UploadSceneTo`.

## Debug views (`Water.DebugView`)

1 texel normal, 2 reflected sky image, 3 depth behind the surface (8 blocks is
white), 4 reflected share x4, 5 glints alone, 6 the water texel grid.

## Status

L2: builds, compiles against the game's own `chunkliquid.fsh` in every define
combination (foam on and off), checked by `WaterChecks`, previewed offline with
the game's water texture. **Not seen in game. Performance not measured** - use
`.vvbench` at the water test scene in `docs/VISUAL-TESTS.md`.

Not done: scene (screen-space) reflection on water, refraction, underwater.
