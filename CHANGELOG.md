# Changelog

Notable changes, in plain language, for people using the mod. Updated at
milestones — not on every commit. Commit history is the developer-facing record.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); this
project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed
- **Reflections are more physically balanced.** A wet or polished floor seen at
  a low angle no longer shows its full colour and a near-full reflection on top
  (a milky sheen); rough stone is unchanged. Bright things such as sunlit trees
  and torches keep more of their brightness in reflections. Creatures and
  particles now take their highlights from your eyes rather than your feet.
- **Clearer reflections on water, wet stone and polished blocks.** Still
  pools, running water and smooth materials now reflect as mirrors instead of
  being held at the same blur as ordinary stone, so a puddle at a low angle
  shows the trees and sky clearly. Rough blocks lose the faint sky sheen they
  had. Rough surfaces show a softer reflection, smooth ones a sharp one.

### Added
- **Torchlight on terrain.** Highlights from torches, lanterns and lava now
  sit on the side of a block facing the light and slide across wet or polished
  surfaces as you move, and a wet cave floor can reflect a torch at night.
- **Water in crevices.** On stone, brick, ceramic and metal, rain pools in
  the joints between stones on a block's top, and runs down its sides while it
  rains, sliding off each stone and gathering in the gaps between them rather
  than falling in straight lines. The mod finds the joints and the shape of
  each stone once, when textures load. Soil, grass, sand and wood just get
  wet. Pools are dark, glossy and reflect the sky. New slider: Water in
  crevices (0 keeps the old even wet look). Debug view 73 shows the joints and
  rain paths the mod found, 75 where the water is right now.
- **Torchlight shades the surface relief.** Near a torch, each stone now
  catches the light on the side facing the flame and the joints between
  stones fall into shade, at night and underground too, and only near the
  torch. Debug view 74 shows it alone.
- **Torch light falloff (optional).** A new slider, Torch light falloff
  (off by default), makes torchlight on blocks fade faster away from the
  flame, so a room pools light around each torch instead of being evenly
  lit. It never brightens anything and leaves daylight alone.
- **Reflections on terrain are back.** Shiny and wet blocks now reflect a
  small, pixel-locked picture of what they face: the sky, the horizon and,
  with Scene reflections on, the actual trees and stone on screen. Rough
  ground, grass and leaves skip the expensive part, so the cost lands only on
  surfaces that can show it. Reflection strength 0 is exactly the old look.
- Repository scaffold, build definition, and project documentation.
- Shader patch engine: YAML-defined regex/token patches against vanilla shaders,
  grouped per subsystem so one bad patch disables only its own subsystem.
- Config file at `ModConfig/vintagevisuals.json` with live reload on
  <kbd>Ctrl</kbd>+<kbd>V</kbd>.
- **Color grading:** exposure, contrast, saturation and white-balance controls,
  plus a filmic (ACES-fit) tonemap. Working in game.
- Optional [ConfigLib](https://mods.vintagestory.at/configlib) support: install
  it and press <kbd>F7</kbd> for sliders. Completely optional — the mod declares
  no dependency on it and works identically without it.
- **Eye adaptation:** the view now brightens in dark places and settles back in
  the light, at different speeds each way — fast into light, slow into dark,
  like a real eye. On by default; tune or disable it under `AdaptiveExposure`.
- Offline PBR prototype tool (`tools/pbrgen`) and runtime C# material atlas
  generation for normal, roughness, specular, metalness, height, AO and emission
  masks.
- **PseudoPBR terrain lighting:** material-derived normals, roughness, specular,
  Cook-Torrance lighting, block-light specular, wetness, snow/frost layering,
  dapple, sun shafts, flora classification and forest-lighting context.
- **Weather:** rain wetness, rain ripples, overcast response and cloud shadows
  from the game's own cloud tiles.
- **Atmosphere:** dedicated state/input layer, ambient-stack bridge and shared
  shader transport for patched terrain, entity and particle programs.
- **Reflections:** scene capture, capture-texel screen-space march, local world
  reflection volume, canopy context texture and analytic fallback.
- **Visual Tuning Studio:** native config dialog with named debug views and row
  descriptions.

### Fixed
- Colour grading had no visible effect. Two causes: the vanilla `final` shader
  program is addressed by id rather than by name, so the settings were never
  uploaded; and the game compiles its shaders before mods load, so the patches
  never reached the running shader. The mod now requests one shader reload of
  its own when it detects this.
- Several runtime regressions around shader delivery, GL state, sampler binding,
  reflection marching, depth-space comparisons, and native UI recomposition now
  have smoke or mutation checks behind them.

### Known issues
- The tonemap ships **switched off** (`TonemapStrength: 0.0`). It still needs one
  look in game to confirm it is applied in the right colour space. Everything
  else in colour grading is confirmed working.
- Compatibility with other shader mods is untested and conflicts are expected.
- Reflections are implemented but not visually closed. Close-range contact,
  undersampling, banding and indoor/ceiling reflection cases remain active
  design problems.
- The Visual Tuning Studio still has reported tab-switch crash risk in game.
- Performance has not been measured. Quality tiers are therefore not honest yet.
- For current proof level read `docs/STATUS.md` and `docs/CHECKLIST.md`.
