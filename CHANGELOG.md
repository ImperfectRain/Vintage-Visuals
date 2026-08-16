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
- **Color grading:** filmic (ACES-fit) tonemap plus exposure, contrast,
  saturation and white-balance controls.
- Offline PBR prototype tool (`tools/pbrgen`) that derives normal, roughness and
  specular-mask maps from a vanilla diffuse texture.

### Known issues
- Nothing here has been verified inside the running game yet; the mod has not
  been compiled against a real Vintage Story install. See the "Current state"
  section of the README.
- Compatibility with other shader mods is untested and conflicts are expected.
