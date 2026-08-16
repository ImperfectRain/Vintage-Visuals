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
  plus a filmic (ACES-fit) tonemap.
- Offline PBR prototype tool (`tools/pbrgen`) that derives normal, roughness and
  specular-mask maps from a vanilla diffuse texture.

### Known issues
- Nothing here has been verified inside the running game yet; the mod has not
  been compiled against a real Vintage Story install. See the "Current state"
  section of the README.
- The color grading tonemap ships **switched off** (`TonemapStrength: 0.0`).
  It needs one look in game to confirm it is being applied in the right color
  space before it can be turned on by default. The other four controls are
  neutral at their defaults and safe either way.
- Compatibility with other shader mods is untested and conflicts are expected.
