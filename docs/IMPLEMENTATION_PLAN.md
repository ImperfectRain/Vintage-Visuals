# Vintage Story Visual Overhaul Mod — Implementation Plan

Scope: color grading, weather/sky, screen-space reflections, and a pseudo-PBR
texture pipeline (derived normal/roughness/specular from vanilla diffuse
textures), built as GLSL shader patches + a C# code mod on top of Vintage
Story's rendering pipeline.

---

## 0. Architecture Decisions (lock these before writing code)

| Decision | Choice | Why |
|---|---|---|
| Shader integration method | **YAML regex patching** over vanilla `.fsh`/`.vsh`, not full-file replacement | Survives game updates better, coexists with other shader mods (pattern used by Coriaender / Volumetric Shading Refreshed) |
| PBR map generation | **Preprocess at texture-atlas build time**, cached, not per-frame | Sobel/variance/BFS passes are too expensive to run every frame per-texel |
| Config/UI | Use **ConfigLib** integration + a Ctrl+C style debug menu | Matches community convention, avoids building a settings UI from scratch |
| Module boundaries | 4 independent, toggleable subsystems (ColorGrade, Weather, Reflections, PseudoPBR) | Lets each ship and be tested/disabled independently; PBR maps feed Reflections but Reflections must degrade gracefully without them |
| Repo layout | Single mod, multiple internal namespaces, one shaderpatches folder per subsystem | Keeps patch conflicts traceable to a subsystem when debugging |

---

## 1. Repository Scaffolding (Day 0)

```
/YourModName
  modinfo.json
  modicon.png
  /assets/yourmodname/
    /shaders/            <- your NEW standalone shaders (post-process, compute)
    /shaderpatches/       <- YAML regex patches against vanilla .fsh/.vsh
      colorgrade.yaml
      weather.yaml
      reflections.yaml
      pbr.yaml
    /lang/
  /src/
    YourModSystem.cs       <- ModSystem entrypoint
    /ColorGrade/
    /Weather/
    /Reflections/
    /PseudoPBR/
      AtlasPreprocessor.cs
      NormalFromLuminance.cs
      RoughnessFromVariance.cs
      SpecMaskFromColorAvg.cs
    /Common/
      ShaderPatchLoader.cs
      ConfigManager.cs
  README.md
  CHANGELOG.md
  .gitignore
```

**Commit 1:** scaffold + empty ModSystem that loads and logs "Hello World" on
game start. Confirms build pipeline works before any shader code exists.

Reference repos to pull boilerplate from:
- `anegostudios/vsmodexamples` → `ScreenOverlayShaderExample`, `HudOverlaySample`
- Coriaender Shaders (Codeberg) → shaderpatches YAML structure, build pipeline
  that renames `.frag/.vert/.glsl/.comp` → `.fsh/.vsh/.ash/.comp` at build time

---

## 2. Phase Order (build in this sequence — each phase is independently shippable)

**Why this order:** color grading has zero dependencies and validates your
patch pipeline cheaply. Weather is visually rewarding and still low-risk.
Reflections needs the render pipeline understanding you'll have built by
then. PBR is last because it's the most novel/unproven and everything else
should be stable before you're debugging texture-atlas preprocessing bugs.

### Phase 1 — Color Grading (lowest risk, validates your whole pipeline)
1. Get `ShaderPatchLoader` working: read a YAML file, regex-match against
   `final.fsh`, apply replacement, log success/failure clearly.
2. Patch `final.fsh` to insert a tonemap + exposure + saturation/contrast
   function before final output.
3. Expose 4 config values (exposure, saturation, contrast, temperature) via
   ConfigLib.
4. **Milestone:** toggling config values visibly changes the game's look with
   no crashes across a world reload.

### Phase 2 — Weather / Sky
1. Patch `cloudvolumetric.fsh` for basic volumetric cloud shape (start by
   copying/adapting an existing open-source approach, don't write noise
   functions from scratch).
2. Add cloud-shadow projection onto terrain (requires a shadow-sample pass
   in `chunkopaque.fsh` or shadow shaders).
3. Patch sky shader for Rayleigh-ish scattering gradient + god rays.
4. Tie fog density/color to weather state (rain, time of day) via existing
   `fogandlight.fsh` hook.
5. **Milestone:** clouds cast moving shadows, sky color shifts with sun angle,
   rain visibly darkens/thickens fog.

### Phase 3 — Reflections (SSR)
1. Implement basic screen-space raymarch reflection pass sampling the
   existing depth buffer — start **water-only**, flat plane, no roughness
   input yet (binary reflective/not, like existing mods).
2. Add fallback/fade at raymarch misses (screen edge, occluded) so it
   doesn't produce hard reflection cutoffs.
3. Gate behind the "deferred lighting" pipeline the same way existing mods
   do; add explicit fallback path for non-deferred rendering so the mod
   doesn't hard-crash on unsupported configs.
4. **Milestone:** water reflects sky/terrain, gracefully turns off (not crash)
   with deferred rendering disabled.

### Phase 4 — Pseudo-PBR pipeline
1. **Offline prototype first, outside the game.** Write a standalone
   command-line/script tool that takes a vanilla texture PNG and outputs
   normal/roughness/specular PNGs using Sobel + variance + color-average.
   Validate visually on 10–15 representative vanilla textures before
   touching the live atlas.
2. Port the validated logic into a compute shader (`.comp`) or a
   `TextureAtlasManager` hook (Harmony patch on atlas upload, same pattern
   Ancestral Bliss Shaders uses) that generates 3 sibling atlases at
   world-load time, cached to disk keyed by texture hash so you don't
   recompute every launch.
3. Wire the derived maps into your existing lighting shader: sample all four
   atlases, replace flat Blinn-Phong with roughness/spec-modulated term.
4. Feed roughness/specMask into the Phase 3 reflection pass — blur/attenuate
   reflections by roughness instead of binary on/off.
5. **Milestone:** at least stone, metal ore, and wood block categories show
   visually distinct specular/roughness response without manual per-block
   authoring.

---

## 3. MVP Checklist

Use this as your GitHub Projects board / issue list. Check off top-to-bottom;
don't start a phase's tasks until the previous phase's milestone is met.

- [ ] Repo scaffold builds and loads in-game (empty mod)
- [ ] `ShaderPatchLoader` applies a YAML patch and logs pass/fail clearly
- [ ] Config system (ConfigLib) wired, at least one live-tunable value
- [ ] **Color grade:** exposure/saturation/contrast/temperature tunable live
- [ ] **Color grade:** basic tonemap curve (avoid blown highlights)
- [ ] **Weather:** volumetric clouds render and move
- [ ] **Weather:** clouds cast shadows on terrain
- [ ] **Weather:** sky gradient reacts to sun angle
- [ ] **Weather:** rain affects fog density/color
- [ ] **Reflections:** water SSR working, flat/binary
- [ ] **Reflections:** graceful fallback when deferred rendering is off
- [ ] **Reflections:** edge/occlusion fade (no hard cutoffs)
- [ ] **PBR:** offline prototype tool validated on sample textures
- [ ] **PBR:** normal map atlas generated in-game, cached to disk
- [ ] **PBR:** roughness atlas generated in-game, cached to disk
- [ ] **PBR:** spec-mask atlas generated in-game, cached to disk
- [ ] **PBR:** lighting shader consumes all 3 derived maps
- [ ] **PBR → Reflections integration:** roughness modulates SSR blur
- [ ] Mod-compat pass: verify no crash when loaded alongside 1–2 popular
      existing shader mods (expect conflicts, document them)
- [ ] README with config docs + known limitations
- [ ] Tag v0.1.0

---

## 4. Flowchart

```mermaid
flowchart TD
    A[Repo Scaffold + Empty ModSystem] --> B[ShaderPatchLoader + Config System]
    B --> C[Phase 1: Color Grading]
    C -->|Milestone: live tunable look| D[Phase 2: Weather/Sky]
    D -->|Milestone: dynamic clouds + fog| E[Phase 3: Reflections/SSR]
    E -->|Milestone: water reflects, graceful fallback| F1[Phase 4a: Offline PBR prototype tool]
    F1 -->|Validated on sample textures| F2[Phase 4b: In-game atlas preprocessor]
    F2 --> F3[Phase 4c: Lighting shader consumes normal/rough/spec]
    F3 --> F4[Phase 4d: Roughness feeds SSR blur]
    F4 -->|Milestone: material-differentiated PBR look| G[Mod-compat pass]
    G --> H[README + docs]
    H --> I[Tag v0.1.0]

    style C fill:#d4f4dd
    style D fill:#d4f4dd
    style E fill:#fff3cd
    style F1 fill:#f8d7da
    style F2 fill:#f8d7da
    style F3 fill:#f8d7da
    style F4 fill:#f8d7da
```

Legend: green = low risk/well-trodden, yellow = medium risk (pipeline
dependency, fallback needed), red = novel/experimental, validate offline
before committing to live game code.

---

## 5. Working Agentically / Committing Strategy

Since you're planning to commit this agentically:

- **One commit per checklist item**, not per phase. Small, revertable units —
  shader patch work is fragile and you will need to `git bisect` when a
  patch breaks on a game update.
- **Branch per phase** (`phase/color-grade`, `phase/weather`, etc.), merge to
  `main` only at a milestone, tag a pre-release at each milestone
  (`v0.1.0-colorgrade`, `v0.1.0-weather`, ...).
- Keep the **offline PBR prototype tool** in `/tools/` as a separate
  standalone script, not entangled with the mod's C# build — it's a
  content-authoring aid, not runtime code, and iterating on it shouldn't
  require a game restart.
- Log every shader patch failure loudly and distinctly (mirror what
  Ancestral Bliss Shaders does — "Critical shader patch failure in X" +
  automatic fallback) so future-you debugging a version bump isn't guessing.
- Write the README's "known limitations" section as you go, not at the end —
  you will forget the SSR occlusion caveat and the Sobel-misreads-painted-
  shading caveat by the time you get to Phase 4.
