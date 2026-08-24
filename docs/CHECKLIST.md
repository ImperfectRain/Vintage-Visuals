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

**`Tst` is the weakest column here and this file used to overstate it.** Seven
defects have been found in this mod by looking at the game and none by the
checks, which were green throughout. A tick under `Tst` therefore says only that
something beyond existence is checked. Whether the check would FAIL if the thing
broke is a separate question with its own table, at the end of this file.

---

## Core — `src/Common/`, `src/Common/Patching/`, `src/Common/Scene/`

| Subsystem | Des | Imp | Cmp | Tst | Run | Edge | Perf | Vis | Doc |
|---|---|---|---|---|---|---|---|---|---|
| Shader patching | x | x | x | x | x | x | – | x | x |
| Shader verification (`verifypatches`) | x | x | x | x | x | x | – | n/a | x |
| Config + live reload | x | x | x | x | x | – | – | x | x |
| ConfigLib bridge | x | x | x | x | x | – | – | x | x |
| Visual Tuning Studio native dialog | x | x | x | x | – | – | – | – | x |
| Visual Tuning Studio UX redesign | x | x | x | x | – | – | – | – | x |
| Visual Tuning Studio layout geometry | x | x | x | x | – | – | – | – | x |
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
| Flora taxonomy (11 classes, vanilla wind modes) | x | x | x | x | – | x | – | – | x |
| Per-plant transmission and pooling | x | x | x | x | – | – | – | – | x |
| Understory receives canopy dapple | x | x | x | x | – | – | – | – | x |
| Optical role (`vvIsCanopyReceiver`) | x | x | x | x | – | x | – | – | x |
| Transmission responds to overcast | x | x | x | x | – | – | – | – | x |
| Canopy spares local light | x | x | x | x | – | x | – | – | x |
| Shafts collapse under overcast | x | x | x | x | – | – | – | – | x |
| Block-light specular | x | x | x | x | x | – | – | x | x |
| Sun dapple | x | x | x | x | x | – | – | – | x |
| God-ray shafts | x | x | x | x | – | – | – | – | x |

---

Flora edge cases exercised, statically: the eleven wind modes checked against the
game's own shader constants; the two liquid modes excluded; an unrecognised mode
landing on a conservative middle rather than zero or maximum; canopy and
understory proven disjoint; fruit proven the least translucent tissue.

Optical-role edge cases exercised, statically: the receiver set proven not to be
the understory set; fruit and vines proven NOT excluded; canopy and aquatic
proven excluded; the shade amount proven still measured from the shadow map
rather than assigned by class; transmission proven to share the direct lobe's
overcast constant rather than carry its own.

**No flora scene has been rendered.** `docs/VISUAL-TESTS.md` carries the matrix
and every row in it is unmarked. The single clearest one-object check is a
backlit pear at sunset: it must not glow green.

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
- [ ] a torch under a tree at dusk is not dimmed by the canopy — benchmark C's core claim
- [ ] an open forest floor at noon looks exactly as it did before the local-light change
- [ ] a low sun through a forest opening shows shafts when clear, and loses them when the sky closes over
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


---

## Mutation coverage

**A check is evidence only when it has been seen to fail.** Each row below is a
defect reintroduced by exact substitution in `tools/mutate/mutations.tsv`; the
harness runs `tools/smoketest` against the broken tree and requires the named
check to fail. Rows naming a commit are defects that SHIPPED and were found by
looking at the game rather than by the suite.

Run it on a clean tree:

```sh
VINTAGE_STORY=/path/to/VintageStory bash tools/mutate/mutation-test.sh
```

| Defect | Found by | Guarded by | Caught |
|---|---|---|---|
| Binder never unbinds between programs (`5a796d6`) | a client crash log | `ShaderBindingChecks` | x |
| Reflections dispose themselves on reload (`dc54d25`) | reading the log after weeks | I9 | x |
| Sunset suppression (`a67886b`) | screenshots at golden hour | I1 | x |
| Metal loses diffuse with no payback (`84f7926`) | "that dirt patch was a gold block" | I5 | x |
| Foliage transmission inverted (`140d3fb`) | debug view 13, photographed twice | I2 | x |
| Dapple cancelled by its own gate (`bf97881`) | "no visible sunspots" | I3 | x |
| Automatic lift with no shoulder (`8c0d4ce`) | clipping around the sun | I6 | x |
| Canopy dims torchlight | class already bitten once | I4 | x |
| Green shade grows into a light loss | found BY I4 on its first run | I4 | x |
| A strength of zero stops meaning off | `CLAUDE.md`'s oldest rule | I7 | x |
| The same factor applied twice in one chain | atmosphere contract rule 7 | I8 | x |
| Two debug views answering one question | "7-11 all show the same image" | I10 | x |
| The census hiding a target that never arrived | `D40` | `Program.cs` census check | x |
| One bad overload losing every hook (`D41`) | "none of the visuals are working" | `ShaderBindingChecks` | x |
| The patcher writing its own text into shipped GLSL (`D41`) | `D41` | `Program.cs` bookkeeping check | x |
| The delivery table hiding a target that never arrived | `D41` | `Program.cs` delivery check | x |
| The hook writing back source the patcher never saw | six-states audit | `ShaderBindingChecks` | x |
| A rejected reflection crossing painted as a miss (`D42`) | source audit | I11 | x |
| Two march outcomes sharing a code | source audit | I11 | x |
| The step-budget diagnostic never reporting saturation (`D42`) | source audit | I12 | x |
| The march sampling coarser than `VV_SSR_STRIDE` names | source audit | I12 | x |
| The depth report comparing against a stale tolerance (`D43`) | external audit, verified | I13 | x |
| The capture's depth assumed finer than one byte (`D43`) | external audit, verified | I13 | x |
| The depth-precision view never reporting coarse data | `D43` | I13 | x |
| A subsystem enabled by default and wholly inert (`D44`) | "disappointed in the atmosphere effects" | I14 | x |

**25 mutations, 25 caught, 0 missed** at `HEAD`.

**What this table does not say.** Every invariant above is arithmetic over source.
None of them can tell you an effect is visible, and all of them pass on a mod
whose every strength defaults to zero. `Vis` remains the only column that means
someone looked.
