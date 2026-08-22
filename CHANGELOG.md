# Changelog

Notable changes, in plain language, for people using the mod. Updated at
milestones — not on every commit. Commit history is the developer-facing record.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); this
project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
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
- Offline PBR prototype tool (`tools/pbrgen`) that derives normal, roughness and
  specular-mask maps from a vanilla diffuse texture. Its three passes are now
  also ported to C# ready for in-game use, though nothing consumes them yet.

### Fixed
- Colour grading had no visible effect. Two causes: the vanilla `final` shader
  program is addressed by id rather than by name, so the settings were never
  uploaded; and the game compiles its shaders before mods load, so the patches
  never reached the running shader. The mod now requests one shader reload of
  its own when it detects this.

### Known issues
- The tonemap ships **switched off** (`TonemapStrength: 0.0`). It still needs one
  look in game to confirm it is applied in the right colour space. Everything
  else in colour grading is confirmed working.
- Compatibility with other shader mods is untested and conflicts are expected.
- Weather, reflections and the in-game PBR pipeline have shipped since this
  release. This entry described 0.1.0 and is kept as release history; for
  current state read `docs/STATUS.md`, and for how far each is proven read
  `docs/CHECKLIST.md`.
