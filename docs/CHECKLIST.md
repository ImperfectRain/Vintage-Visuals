# Live checklist

Per-subsystem readiness. A box is ticked only where the evidence exists.

**A tick is a claim about evidence, not about effort.** "Runtime tested" means
someone ran it and looked. "Visually validated" means the specific scene in
`docs/VISUAL-TESTS.md` was checked and the result matched. Nothing here may be
ticked because a thing compiles.

Each section names the directory that owns it, so a subsystem can be traced from
the tree to its readiness without guessing which human name it goes by.

**Read with `docs/STATUS.md`.** STATUS says what each subsystem is and what is
wrong with it. This file says only how far it has been proven.

Column meanings:

| Column | Ticked when |
|---|---|
| Des | designed — the approach is written down somewhere, not just in a head |
| Imp | implemented in the repository |
| Cmp | compiles: `dotnet build`, and `tools/verifypatches` for anything patching GLSL |
| Tst | covered by `tools/smoketest` beyond "it exists" |
| Run | seen running in game: the log shows it active, or a debug view responds |
| Edge | edge cases deliberately exercised, listed under the table |
| Perf | cost measured, not argued |
| Vis | visually validated against a named scene |
| Doc | STATUS, ARCHITECTURE and this file agree with the code |

---

## Core — `src/Common/`, `src/Common/Patching/`, `src/Common/Scene/`

| Subsystem | Des | Imp | Cmp | Tst | Run | Edge | Perf | Vis | Doc |
|---|---|---|---|---|---|---|---|---|---|
| Shader patching | x | x | x | x | x | x | – | x | x |
| Shader verification (`verifypatches`) | x | x | x | x | x | x | – | n/a | x |
| Config + live reload | x | x | x | x | x | – | – | x | x |
| ConfigLib bridge | x | x | x | x | x | – | – | x | x |
| Environment state / tracker | x | x | x | x | x | – | – | n/a | x |
| Visual budget arbitration | x | x | x | x | x | – | – | – | x |

Edge cases exercised: patch group rollback on failure; a stale patch file in the
player's Mods folder (found in the wild, now cleaned by the csproj); missing game
install; SSAO on and off across all 48 prefix combinations.

---

## Material system — `src/PseudoPBR/`

| Subsystem | Des | Imp | Cmp | Tst | Run | Edge | Perf | Vis | Doc |
|---|---|---|---|---|---|---|---|---|---|
| Material inference (`pbrgen` port) | x | x | x | x | x | – | – | x | x |
| Material resolution per texture | x | x | x | x | x | x | – | – | x |
| Material atlas (page 1) | x | x | x | x | x | x | – | x | x |
| Second material atlas | x | x | x | x | x | – | – | – | x |
| Normal mapping | x | x | x | x | x | – | – | x | x |
| Roughness | x | x | x | x | x | – | – | x | x |
| Specular mask | x | x | x | x | x | – | – | x | x |
| Metalness | x | x | x | x | x | – | – | – | x |
| Anisotropic wood grain | x | x | x | x | – | – | – | – | x |
| Multi-scatter GGX compensation | x | x | x | x | – | – | – | – | x |
| Specular occlusion | x | x | x | x | – | – | – | – | x |
| Baked AO (atlas 2, channel B) | x | x | x | x | – | – | – | – | x |
| Height (atlas 2, channel G) | x | x | x | – | – | – | – | – | x |
| Emission masks | x | x | x | x | – | – | – | – | x |

Edge cases exercised: composite blocks (gate planks vs iron strapping) resolve to
different materials — verified in the atlas build log, 1,530 reclassifications on
page 0; multi-page atlases; atlas cache reuse; textures whose slot size differs
from the source texture.

**Height is implemented and packed but has no consumer.** Parallax was deferred.

---

## Lighting and shading — `src/PseudoPBR/`, `assets/vintagevisuals/shadersnippets/`

| Subsystem | Des | Imp | Cmp | Tst | Run | Edge | Perf | Vis | Doc |
|---|---|---|---|---|---|---|---|---|---|
| PBR terrain (`chunkopaque`, `chunktopsoil`) | x | x | x | x | x | x | – | x | x |
| Entity PBR | x | x | x | x | x | – | – | x | x |
| Particle PBR | x | x | x | x | x | – | – | – | x |
| Foliage transmission | x | x | x | x | x | – | – | x | x |
| Block-light specular | x | x | x | x | x | – | – | x | x |
| Sun dapple | x | x | x | x | x | – | – | – | x |
| God-ray shafts | x | x | x | x | – | – | – | – | x |

---

## Weather and atmosphere — `src/Weather/`

| Subsystem | Des | Imp | Cmp | Tst | Run | Edge | Perf | Vis | Doc |
|---|---|---|---|---|---|---|---|---|---|
| Wetness | x | x | x | x | x | – | – | x | x |
| Rain ripples | x | x | x | x | x | x | – | x | x |
| Snow dusting | x | x | x | x | – | – | – | – | x |
| Frost response | x | x | x | x | – | – | – | – | x |
| Cloud shadows (from game tiles) | x | x | x | x | x | x | – | x | x |
| Overcast response | x | x | x | x | x | – | – | x | x |
| Fog / atmosphere | x | x | x | x | x | – | – | x | x |

Edge cases exercised: rain ripples anchored to the world rather than the camera
(regression pinned by call-order test); cloud shadows when the game's cloud
renderer cannot be read — falls back to off rather than to invented shadows.

---

## Atmosphere — `src/Atmosphere/`

| Subsystem | Des | Imp | Cmp | Tst | Run | Edge | Perf | Vis | Doc |
|---|---|---|---|---|---|---|---|---|---|
| Atmosphere state from the game | x | x | x | x | – | – | – | n/a | x |
| Derivation / normalisation (`AtmosphereInputs`) | x | x | x | x | – | – | – | n/a | x |
| Transport model | x | x | x | x | – | – | – | – | x |
| Ambient stack bridge | x | x | x | x | – | – | – | n/a | x |
| Height haze | x | x | x | x | – | – | – | – | x |
| 1 Aerial perspective | x | x | x | x | – | – | – | – | x |
| 2 Horizon scattering | x | x | x | x | – | – | – | – | x |
| 3 Sun-aware scattering | x | x | x | x | – | – | – | – | x |
| 4 Height attenuation | x | x | x | x | – | – | – | – | x |
| 5 Weather extinction | x | x | x | x | – | – | – | – | x |
| 6 Cloud-atmosphere coupling | x | x | x | x | – | – | – | – | x |
| 7 Cloud-edge scattering | x | – | x | x | – | – | – | – | x |
| 8 Godrays | x | – | x | x | – | – | – | – | x |
| 9 Precipitation scattering | x | x | x | x | – | – | – | – | x |
| 10 Moon scattering | x | x | x | x | – | – | – | – | x |
| 11 Dapple interaction | x | – | x | x | – | – | – | – | x |
| Thirteen debug views | x | x | x | x | – | – | – | – | x |
| ConfigLib exposure (15 settings) | x | x | x | x | – | – | – | – | x |
| Temporal continuity | x | x | x | x | – | – | – | – | x |

Not yet validated, and each is a separate question:

- [ ] the haze band's top is at a believable height — 34 blocks above sea level is a guess
- [ ] climbing a hill takes the player out of the haze rather than carrying it along
- [ ] the haze reaches the sky and the water, which is the whole reason it is not in a shader
- [ ] switching it off leaves the ambient stack exactly as vanilla left it
- [ ] the blend reproduction agrees with the game — the log says so or it does not
- [ ] a low sun brightens the haze toward it and not away from it
- [ ] the in-scattering does not push the horizon past white at sunset
- [ ] an entity standing in fogged terrain now fogs with it — this is the regression the move was for
- [ ] terrain, ground cover, entities and particles agree at the same distance
- [ ] setting every shader feature to 0 restores vanilla's own `applyFog`, confirmed in a shader dump
- [ ] a blizzard reads differently from a downpour, not merely greyer
- [ ] a mountain top sees further than the valley below it
- [ ] a moonlit night is not brighter than vanilla's, only differently shaped
- [ ] each of the thirteen debug views shows what it claims
- [ ] the F7 panel renders at all — a duplicate weight blanks the whole thing, and only the game says
- [ ] dragging a strength through zero reloads shaders without a visible stall

**The test matrix that has NOT been run.** Time: noon, morning, sunset, night,
moonlit night. Weather: clear, overcast, rain, heavy rain, snow, and every
transition. Location: field, forest, mountain, valley, shore, underground, high
altitude. Distance: near, medium, far. Systems: all on, all off, each off in
turn. None of it. The independence and normalisation checks in
`tools/smoketest/AtmosphereChecks.cs` cover the same axes as *source-level*
tests, which is a different claim and a much weaker one.

**Sun attenuation has no row** because it is not this mod's to implement — see
DECISIONS D21. `AtmosphereState.SunColor` samples vanilla's own answer and no
consumer reads it yet.

---

## Reflections — `src/Reflections/`

| Subsystem | Des | Imp | Cmp | Tst | Run | Edge | Perf | Vis | Doc |
|---|---|---|---|---|---|---|---|---|---|
| Scene capture | x | x | x | x | x | – | – | – | x |
| Screen-space march | x | x | x | x | x | – | – | – | x |
| Texel quantisation | x | x | x | x | x | – | – | – | x |
| Analytic fallback | x | x | x | x | x | – | – | – | x |
| Water reflections | – | – | – | – | – | – | – | – | x |

Not yet validated, and each is a separate question:

- [ ] composite metal textures reflect while their wood does not
- [ ] chiselled blocks reflect per material region
- [ ] moving foliage does not smear (one-frame lag is expected here)
- [ ] correct at several camera distances
- [ ] correct with SSAO disabled — the design says it must work, untested
- [ ] wet stone reflects more strongly than dry, through the material path alone
- [ ] performance cost of the capture measured
- [ ] reflection survives a world reload

**Water is not implemented at all.** `chunkliquid.fsh` is in no patch group.

---

## Colour and output — `src/ColorGrade/`

| Subsystem | Des | Imp | Cmp | Tst | Run | Edge | Perf | Vis | Doc |
|---|---|---|---|---|---|---|---|---|---|
| Colour grading | x | x | x | x | x | – | – | x | x |
| Adaptive exposure | x | x | x | x | x | – | – | x | x |
| Adaptive grading | x | x | x | x | x | – | – | x | x |
| Tonemap | x | x | x | x | x | – | – | – | x |
| Post-processing (bloom, DoF, camera FX) | – | – | – | – | – | – | – | – | x |

**Colour grading operates in display-referred space.** See DECISIONS D16. It
works and is validated as a look, but it is not correct as colour management,
and that blocks further grading work.

---

## Quality and performance — cross-cutting

| Subsystem | Des | Imp | Cmp | Tst | Run | Edge | Perf | Vis | Doc |
|---|---|---|---|---|---|---|---|---|---|
| Detail-distance fade | x | x | x | x | x | – | – | x | x |
| Quality tiers | – | – | – | – | – | – | – | – | x |

**Nothing in this project has had its performance measured.** Every cost claim in
the documentation is an argument from operation count, not a measurement. That is
the single largest gap in the evidence, and it is why the Perf column is empty
everywhere.
