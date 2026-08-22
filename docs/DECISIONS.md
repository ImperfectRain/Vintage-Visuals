# Architecture decisions

Decisions that shaped the renderer, and the reasoning behind them.

**Why this file exists.** Several times this project has rediscovered an
approach it had already rejected, or rebuilt a system whose constraints were
recorded only in a commit message. A decision that lives in one commit is a
decision nobody will find. Each entry below can be checked against the code.

**Nothing here is invented.** Every entry is traceable to code, a commit, or an
explicit direction from the project owner. Where a decision was made implicitly
and only became explicit later, the entry says so.

**Superseded decisions are kept, not deleted.** A rejected approach that
disappears from the record is one a future agent will find attractive again.

---

## D1. The mod patches vanilla GLSL rather than replacing shaders

**Context.** Vintage Story ships its own shaders and changes them between
versions. A shader pack replaces them wholesale.

**Problem.** A replacement diverges from the game the moment the game updates,
and silently loses every feature added upstream.

**Chosen.** Regex/token patches applied to vanilla source at load time, grouped
by subsystem, with per-group rollback.

**Why.** The mod is a rendering *framework* shipping a visual overhaul, not a
shader pack. Patching keeps vanilla's own work — cascaded shadows, colour maps,
wind, godrays — and adds to it.

**Consequences.** Every anchor is a dependency on wording the game can change.
`tools/verifypatches` exists to catch that against the game's own dumped
shaders. A patch that stops matching produces "the mod does nothing", not a
crash.

**Status.** Active. `src/Common/Patching/`.

---

## D2. A failed patch disables its subsystem, never the game

**Problem.** A patch group that throws takes the world render with it.

**Chosen.** Patches are grouped by subsystem; a failure disables that group,
logs `CRITICAL shader patch failure in <group>`, and lets everything else load.

**Consequences.** Partial rendering is an expected state, so every subsystem
must degrade to vanilla rather than to a broken frame.

**Status.** Active. `src/Common/Patching/ShaderPatchLoader.cs`.

---

## D3. Prefer the game's own answer to a reconstructed one

**Context.** Most effects here could be derived either from data Vintage Story
already computes or from screen-space approximation.

**Chosen.** A strict preference order: what the game exposes directly →
what is derived from authoritative game data → geometry and textures →
screen space → invented simulation.

**Why.** Every serious defect in this project came from working too low on that
ladder. Cloud shadows corresponded to nothing until they read the game's own
cloud tiles; sun dapple fired in the wrong places until it read the game's own
shadow map.

**Consequences.** Some features are impossible at the preferred level and must
be honestly labelled as approximations rather than dressed up.

**Status.** Active, and the governing principle of `docs/VISUAL-LANGUAGE.md`.

---

## D4. Material identity is resolved per TEXTURE, not per block

**Problem.** A wooden gate with iron strapping is one block with one
`EnumBlockMaterial`. Resolving material per block gives the iron wood's
roughness, or the wood iron's.

**Chosen.** `MaterialResolver` resolves from the texture's own asset path and
slot name first, falling back to the block's material. Last matching whole path
segment wins.

**Consequences.** Composite blocks work. Chiselled blocks work with no
per-voxel data at all, because they inherit identity through the textures they
draw. The atlas build logs how many textures were reclassified this way — 1,530
on page 0 of a stock install.

**Status.** Active. `src/PseudoPBR/MaterialResolver.cs`.

---

## D5. A second material atlas rather than more channels in the first

**Context.** The first atlas packs normal XY, roughness, specular — full.

**Options.** Repack the first atlas; add a second; infer the extra values in the
shader.

**Chosen.** A second atlas: R metalness, G height, B baked AO, A emission mask.

**Why.** Repacking would have invalidated every cached atlas and the parity
fixture. Inference in-shader is exactly the guessing this project keeps
regretting.

**Consequences.** Two texture units (15 and 14). `vv_material2Valid` is 0 when
the second atlas is absent, and every consumer has a defined fallback for that.

**Status.** Active. `src/PseudoPBR/MaterialAtlas2Builder.cs`.

---

## D6. Zero must mean "behave like vanilla" for every uniform

**Problem.** An unset GLSL uniform reads as exactly 0, and a uniform can be
unset for many reasons — the binder skipped, the program was not patched, a
group rolled back.

**Chosen.** 0 is the harmless value for every uniform the mod adds.

**Why.** `vv_cloudDensity` multiplied vanilla's density term, so 0 meant NO
CLOUDS AT ALL rather than "normal clouds". That shipped.

**Consequences.** Some uniforms are inverted from the natural reading. Every new
uniform must have its zero case checked.

**Status.** Active, enforced by review and by `tools/smoketest`'s uniform-wiring
check.

---

## D7. Sun dapple reads vanilla's shadow map, not a procedural field

**Superseded:** a procedural sunfleck generator, deleted.

**Problem.** The procedural field corresponded to nothing in the world and was
gated on `vv_sunExposure`, which debug view 16 showed reads ~1 across an entire
forest — under crowns as well as open sky.

**Chosen.** Vanilla's shadow map already resolves individual leaf gaps
(`chunkshadowmap.fsh` alpha-discards below 0.02, so leaf cutouts punch real
holes) and already animates them with the game's own wind
(`chunkshadowmap.vsh` runs the same `applyVertexWarping`). Canopy identity is
the *count of separate occluders* around a fragment, measured as total variation
around a ring of shadow taps.

**Why the count.** A wall, a terrace lip, a cliff and a roof are each ONE edge;
a canopy is many. Thresholding above one edge rejects every solid occluder by
construction rather than by tuning.

**Rejected.** Disagreement at a radius (`4p(1-p)`) — it is an edge detector, so
it can only outline a shadow, never fill it. Reported as such from a screenshot.

**Consequences.** `vvSunflecks` and nine tuning constants are deleted, not
disabled. The mod's contribution is contrast and colour, not pattern.

**Status.** Active, L2. `assets/vintagevisuals/shadersnippets/pseudopbr.glsl`.

---

## D8. Reflections capture the previous frame rather than reading a G-buffer

**Context.** `chunkopaque.fsh` is a forward opaque pass: the frame it would
sample is the one it is still drawing. The post-process pass can see the scene
but has no idea which texture texel a fragment belongs to.

**Options considered.**

1. Do the reflection in a post pass and carry material identity through a spare
   G-buffer channel. Only `outGlow.b` is free, which cannot hold a texel index,
   and BRDF integration would have to move to the post pass — a full deferred
   renderer.
2. Read the current frame's buffers in the terrain pass. The primary buffer is
   being written by that very pass; reading it is undefined.
3. Capture the composed frame and read it back next frame.

**Chosen.** Option 3. `SceneCaptureRenderer` copies the primary framebuffer at
`AfterPostProcessing` into a half-resolution RGBA target: RGB the scene, alpha
the linear view depth.

**Why.** It puts the image and the material grid in the same pass, which is what
makes a pixel-art mirror possible at all. Everything needed is public API —
`IRenderAPI.FrameBuffers` and `FrameBufferRef.ColorTextureIds`.

**A previous pass wrongly recorded engine-internal texture IDs as a blocker.**
They are not; the list is public and documented.

**Consequences.** The reflection is ONE FRAME STALE. Camera movement is
corrected by reprojection; world movement (leaves, mobs, the player) cannot be
and lags by a frame.

**Status.** Active, L2. `src/Reflections/SceneCaptureRenderer.cs`.

---

## D9. Depth comes from the depth attachment, never from gPosition

**Problem.** `chunkopaque.fsh` writes `outGPosition` inside `#if SSAOLEVEL > 0`,
so the position G-buffer does not exist for a player with SSAO switched off.

**Chosen.** The capture linearises the framebuffer's own depth attachment, which
always exists.

**Consequences.** The reflection is independent of the SSAO setting rather than
silently vanishing at one of them. Depth precision at distance is poor because
it is packed into an 8-bit alpha channel — an accepted limitation, recorded in
STATUS.

**Status.** Active. `assets/vintagevisuals/shaders/vvscenecapture.fsh`.

---

## D10. The reflection ray starts at the material texel's centre

**Problem.** One colour per texture pixel is the whole visual contract. The
normal is already per-texel through `vvSnapToTexel`, but the VIEW vector is not
— it varies continuously across a texel, so the reflection direction did too and
could shade a gradient inside a single texture pixel while calling itself pixel
art.

**Chosen.** `vvTexelCentrePos` solves the UV-to-position Jacobian from screen
derivatives, so every fragment inside a texel computes the identical ray.

**Why by construction.** Quantising the result afterwards is a rounding step
that might or might not land; starting the ray at the texel centre makes the
property structural.

**Rejected.** Phase-shifting the quantisation grid per texel with an R2
sequence. That makes the structure out of a low-discrepancy sequence — a
procedural patchwork wearing a reflection's clothes.

**Status.** Active. Pinned by `tools/smoketest/PixelReflectionChecks.cs`.

---

## D11. The reflection march is in screen space

**Superseded:** two world-space marches.

**Problem 1.** A march that accepts a hit within a tolerance of a sample is a
march of discrete SHELLS. With geometrically growing steps the gaps outrun the
tolerance, so past a few blocks most distances were unreachable. Reported from a
screenshot as "a circular checkerboard".

**Problem 2.** World-space steps project to wildly different screen distances.
Near the camera one step leaps across the frame, so every ground point whose ray
jumped over a tree trunk registered its crossing at the same few texels.
Reported as the trunk smearing to the viewer's feet instead of foreshortening.

**Chosen.** March a fixed number of capture TEXELS, with the step count derived
per ray from how far it travels across the image; detect a depth CROSSING rather
than proximity; refine by bisection; interpolate depth as 1/w.

**Why 1/w.** That is what is linear across a screen. Interpolating depth
directly bends the ray and puts every hit at the wrong distance.

**Consequences.** Uniform sampling in the image is what makes a reflection
foreshorten. Refinement depth is DERIVED from the stride and the thickness test
rather than chosen — too few passes rejects real hits in rings.

**Status.** Active, L2.

---

## D12. Ray depth and captured depth must be in one space

**Problem.** The capture linearises the depth buffer, which yields AXIAL
view-space z. The march compared it against `length()`, which is RADIAL. They
differ by 1/cos of the angle from the view axis — 6% at 20 degrees, 30% at 40 —
and the error is radially symmetric about the screen centre, so it drew its own
rings.

**Chosen.** Ray depth is `clip.w`, which is view-space z, taken from the same
projection.

**Why not a tolerance.** Reconciling two spaces with a tolerance hides a
systematic error as noise.

**Status.** Active. Pinned by test.

---

## D13. The reflection is substituted into the ambient specular term

**Problem.** A reflection added as its own term inherits none of the existing
energy controls.

**Chosen.** `vvAmbientSpecular` already took a single flat environment colour.
The reflection replaces that colour; everything downstream — roughness-aware
Fresnel, metal tint through `f0`, energy compensation, specular occlusion,
daylight, fog, overcast — is unchanged.

**Consequences.** Wetness needed NO reflection-specific code:
`vvApplyEnvironmentLayers` already lowers roughness and raises the specular mask
when wet, and both feed the same terms the reflection uses.

The captured frame is the FINISHED image — graded, bloomed, exposure adapted —
so it is capped by luminance against the environment colour, scaled uniformly to
preserve hue. Without that cap a bright sky pushes metal past anything the
ambient term could have produced, which is the white-metal failure returning
through a new path.

**Status.** Active.

---

## D14. The specular lobe is bounded by an alpha floor

**Problem.** GGX integrates to one over the hemisphere, so its peak grows as
`1/(pi*alpha^2)` without limit. At the material roughness floor the peak reaches
124,340 — about 1119x sunlight off a plain dielectric.

**Chosen.** `VV_GGX_MIN_ALPHA = 0.04`, applied in both the isotropic and
anisotropic forms before the anisotropic split.

**Why an alpha floor rather than a denominator clamp.** A denominator clamp caps
the peak at `1e5*roughness^4`, so a SMOOTHER surface gets a DIMMER highlight —
backwards, and the reason wet stone never produced a sheen despite a specular
constant of 0.60.

**Consequences.** This is a DISPLAY limit, not a physical one, and it is
load-bearing: the mod composites specular into vanilla's colour before bloom with
no exposure control anywhere in the chain.

**Status.** Active. `assets/vintagevisuals/shadersnippets/pbrcore.glsl`.

---

## D15. Never declare a sampler above vanilla's

**Problem.** Sampler texture units are assigned at LINK time from the program's
active sampler list, so inserting one above vanilla's shifts every sampler below
it. In `chunkopaque.fsh` that pushed `liquidDepth` off the end and mixed every
terrain fragment to the water murk colour.

**Chosen.** All mod samplers are injected at an anchor on the LAST vanilla
sampler, and the ordering is pinned by test.

**Related.** A texture unit is GLOBAL GL state, not per-program, so a sampler
bound once per frame does not survive to the chunk draws.
`TerrainTextureBindInterceptor` rebinds per draw call — the atlases and the
scene capture all ride that path.

**Status.** Active, and the single most expensive lesson in the project.

---

## D16. Colour grading runs in display-referred space

**Context.** The grading pass operates on vanilla's already-composed output.

**Consequences.** Exposure, contrast and saturation are being applied to values
that are not linear radiance. This is a known correctness problem and it blocks
further grading work.

**Status.** OPEN. Recorded here so it is not rediscovered as a surprise. See
STATUS "Open problems".

---

## D17. Documentation consistency is checked mechanically, not by review

**Context.** The repository reached a state where three documents asserted
`src/Reflections/` was an empty directory while a working reflection system lived
in it, `CLAUDE.md` pointed at one file that had moved and one that had been
deleted, and `src/PseudoPBR/README.md` said `chunktopsoil.fsh` was "not started"
beside a patch group that patches it in all 24 combinations.

**Problem.** Every one of those was written by someone who had just read the
code. Review does not catch this class, because the stale claim is somewhere the
author was not looking.

**Options considered.**

1. A review checklist alone. Rejected: the failure is precisely that the author
   does not know which other document mentions their subsystem.
2. A documentation linter that judges prose. Rejected: it would need to know what
   is true, which is the hard part, and a check that can be argued with gets
   argued with.
3. Narrow, mechanical checks over facts that cannot be disputed.

**Chosen.** Option 3, in `tools/smoketest/DocumentationChecks.cs`. It verifies
that referenced paths exist, that no document calls a populated directory empty,
that numbers quoted in prose match the constants they quote, that no document
cites a debug view the slider cannot reach, that every subsystem appears in both
STATUS and ARCHITECTURE with its own README, and that no document calls a
patched shader unbuilt - evidence taken from the patch YAML, which is not a
matter of opinion.

**Why narrow.** Each check answers a question with one right answer. None of them
can tell whether prose is TRUE, and they do not try; anything they cannot verify
belongs in `docs/CHECKLIST.md` as a human check rather than being faked with a
regex.

**Consequences.** Renaming a file, moving a subsystem, changing a documented
constant, or adding a subsystem without documenting it now fails the build's test
step. Five distinct mutations were confirmed to fail it. The cost is that a
legitimate rename requires updating the docs in the same commit - which is the
intended pressure.

**Also chosen: a source-of-truth hierarchy.** STATUS is current state, CHECKLIST
is how far it is proven, DECISIONS is why, IMPLEMENTATION_PLAN is what is next
and what was rejected, ARCHITECTURE is data flow and ownership. Investigation
history is kept but explicitly labelled as history, because this project has
twice rediscovered an approach it had already rejected.

**Status.** Active. `tools/smoketest/DocumentationChecks.cs`, `CLAUDE.md`.

---

## D18. The atmosphere is driven through vanilla's ambient stack, not through GLSL

**Date.** The atmospheric foundation pass.

**Context.** The brief asked for five atmospheric features - aerial perspective,
height haze, horizon colouration, sun attenuation and weather visibility - and
said to determine what Vintage Story already provides before writing any shader
code. The audit changed what there was to build.

`IAmbientManager` blends a stack of named `AmbientModifier`s into the fog the
game actually renders. The stack is a public, writable
`OrderedDictionary<string, AmbientModifier>`, the blend is documented on the
interface, and its result feeds **every shading program the game has**:
`chunkopaque`, `chunktopsoil`, `entityanimated`, both particle shaders,
`chunkliquid`, `chunktransparent`, and the sky.

Four of the five features turned out to be values that stack can already express.

**The decisive asymmetry.** This mod patches four programs. It does not patch the
sky, the water, or `chunktransparent`, and `docs/DECISIONS.md` D6 records that
patching the sky was tried and rejected on its own merits. An atmosphere built in
GLSL therefore stops at the edge of what was patched - and the edges are not
hidden. They are where a hillside meets its own grass, and where an animal stands
in front of a fogged valley.

**Options considered.**

1. A snippet at the `applyFog` anchor, which is byte-identical in all seven
   shading programs. Rejected as the *primary* mechanism: it still misses the sky
   and it collides with the weather group, which already owns that anchor in two
   programs.
2. `getSkyColorAt`, which `chunkopaque.fsh` already contains and already calls.
   Rejected: it exists in `chunkopaque.fsh` and **nowhere else** - not in
   `chunktopsoil`, not in `entityanimated`, not in the particle shaders. Using it
   would give opaque terrain one atmosphere and everything in front of it
   another.
3. Writing the game's own ambient stack.

**Chosen.** Option 3 for everything it can express, with GLSL reserved for the
one thing it cannot.

**The rule this establishes.** If a value can be expressed as an ambient
modifier, it belongs there and not in a shader - not because it is easier, but
because it is the only way to get an answer that is consistent across the frame.

**Consequences.** Height haze ships with no shader patch, no texture unit, no
sampler, and no link-time risk, and it applies to the sky and the water for free.
The mod's atmosphere and vanilla's cannot drift, because they are the same
numbers. The cost is that the mod now writes into a dictionary it shares with the
game and every other mod, which is why D20 exists.

**Status.** Active. `src/Atmosphere/`, `src/Common/Scene/AtmosphereState.cs`.

---

## D19. Height fog is vanilla's, and the sign convention is vanilla's too

**Context.** Height fog looked like the most obviously missing atmospheric
feature. It is not missing. Every shading program in the game computes

```glsl
flatFog = 1 - 1 / exp((worldPosY - flatFogStart) * flatFogDensity);
val = max(flatFog, distanceFog);
```

from two uniforms the ambient stack feeds. Reimplementing it would have been a
second description of a term the game already evaluates seven times per frame.

**The sign.** `flatFogDensity` **negative** puts the fog *below* `flatFogStart`.
That is the game's own convention rather than an accident of the exponential:
`getFogAmountForSky` branches on `flatFogDensity < 0` to add an earth-curvature
bias, which only makes sense for a layer lying on the ground.

Getting it backwards would put the haze layer in the sky, and nothing else in the
repository could catch it - the value is written into the ambient stack and
rendered by code this project does not own, so the first report would be a
screenshot. `AtmosphereChecks` therefore transcribes vanilla's formula from the
dumped shader and asserts through it that a fragment at sea level is fogged, a
fragment at the band's top less so, and a hilltop above the band clear.

**The band is anchored to sea level, not to the camera.** Haze that followed the
camera would keep the player permanently at its surface, so climbing out of it -
the one thing that makes a haze layer read as a layer - could never happen.

**Status.** Active. Off by default: the band's height is the one number no test
can settle.

---

## D20. Writing a shared dictionary requires reproducing the blend

**Context.** `IAmbientManager.CurrentModifiers` is shared with the game and with
every other mod. The entire safety argument for writing into it is that a weight
of `0` is a true no-op, per the blend documented on the interface:

```
blended = w * modifier.Value + (1 - w) * blended
```

A documented formula in an interface comment is not a tested one, and this mod
cannot see the fold it depends on.

**Options considered.**

1. Trust it. Rejected: if it changed, the haze would land at the wrong strength
   silently, and the symptom - "the slider does not do much" - is the one this
   project has already been fooled by twice.
2. Invert the mod's own contribution out of the blended result to recover the
   baseline. Rejected: it assumes this modifier blends *last*, which nothing
   guarantees once another mod appends one.
3. Reproduce the fold and compare it against the game's answer.

**Chosen.** Option 3, once per install rather than per frame - a formula does not
move mid-session. Compared on fog density rather than flat fog density, because
that is the field with a non-zero value in a default world and therefore the one
where a wrong fold shows. A mismatch logs a warning and the feature still runs;
the warning says the strength may be wrong.

**Also chosen: off removes the modifier.** Not zeroes it. A zeroed entry is
invisible in the frame and visible everywhere else - it stays in a shared
dictionary, survives the player unticking the feature, and is exactly the residue
that makes a later "why is this mod in the ambient stack" unanswerable.

**Also chosen: the modifier is re-added every tick if missing.** Nothing
documents the dictionary's lifetime. A stack rebuilt on a world change would drop
the entry, and the feature would stop with nothing in the log. Re-adding an entry
that is already there costs a hash lookup.

**Status.** Active. `src/Atmosphere/AmbientBridge.cs`.

---

## D21. Sun attenuation is vanilla's answer, and the mod does not get a second one

**Context.** "Sun and moon attenuation through atmosphere" reads like a request
for a Rayleigh model: redden the sun as it approaches the horizon and the path
through the air lengthens.

Vintage Story already does it. `IClientGameCalendar.SunColor` is documented as
"a normalized color of the sun at the players current location", it varies with
the sun's altitude, and `SunsetMod` offsets the sky-glow lookup once per day so
that no two sunsets match.

**Chosen.** Sample it. `AtmosphereState.SunColor` is that value, unmodified.

**Why not model it anyway.** The sun disc is drawn by `celestialobject` and the
sky by the sky shader, neither of which this mod patches or intends to. A second
attenuation model would light the terrain with a sun of one colour while the
player looks at a sun of another. The information ladder in
`docs/VISUAL-LANGUAGE.md` puts "what the game exposes directly" at the top for
exactly this case, and every serious bug in this project so far has come from
working lower on it than necessary.

**DATA GAP, stated rather than invented.** There is no CPU accessor for the sky's
colour in a given direction. The gradient lives in `sky.png`, bound as the `sky`
sampler, and is evaluated by `getSkyColorAt` - which exists in `chunkopaque.fsh`
and in no other shading program. So a horizon colour usable by *all* surfaces
must be derived from `BlendedFogColor`, `SunColor` and the sun's elevation rather
than read. That derivation is not implemented yet, and when it is it should be
labelled as a derivation.

**Status.** Active for the sampling. The consumer does not exist yet - see
`docs/STATUS.md` section 6.
