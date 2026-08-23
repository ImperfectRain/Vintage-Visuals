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

---

## D22. Aerial perspective adds a directional term and nothing else

**Context.** Vanilla's fog is

```glsl
mix(rgbaPixel.rgb, rgbaFog.rgb, fogWeight)
```

- isotropic, so haze looking into a low sun and haze looking away from it are the
same grey. In the real thing they are not remotely the same, and the difference
is most of what makes distance read as distance. This is the one atmospheric
effect Vintage Story genuinely lacks, and so the only one in this subsystem with
any GLSL - see D18 for why everything else went through the ambient stack.

**The restraint is the decision.** A full aerial-perspective model also adds a
distance falloff, a height term and desaturation with depth. All three are
already present elsewhere:

| Term | Already owned by |
|---|---|
| Distance falloff | vanilla's `getFogLevel`, per vertex, in every program |
| Height band | vanilla's `flatFog`, and now driven by `AmbientBridge` - D19 |
| Desaturation with weather | colour grading, arbitrated through `VisualBudget` |

Adding any of them here would be a second subsystem quietly removing contrast
from the same pixel, outside the budget - which is the precise defect
`VisualBudget` was built to stop, and which three subsystems were already doing
to the same rainy afternoon before it existed. `AtmosphereChecks` therefore fails
the build if the snippet grows an exponential falloff, a height term, or a
saturation term.

**Normalised phase, capped gain.** Henyey-Greenstein is unbounded as `g`
approaches 1 and the term multiplies a colour that has already been through
vanilla's exposure. Un-normalised it returns about 0.08 facing the sun at
`g = 0.45`, which looks like the effect is off and invites raising the strength
until the peak is wrong instead; uncapped, a low sun pushes the horizon past
white and takes the sky's gradient with it. So the phase is normalised to 1 at
isotropic and the gain is capped at 0.85.

`g` is 0.45, deliberately below the 0.6-0.8 of real atmospheric aerosol. The
phase function is being applied to a fog term vanilla tuned by eye rather than to
an actual optical depth, so a physical `g` piles the whole effect into a bright
disc a few degrees wide around the sun - correct, and it reads as a bug.

**A sun below the horizon contributes nothing.** Forward scattering is strongest
when the sun is low and its light is travelling through the most air, so the
strength is inverted against elevation - but a sun *below* the horizon is lighting
the haze from underneath the world, and a bright band pointing at it would be a
hole in the ground.

**Status.** Active, L2. Defaults to 0, which skips the patch group entirely.

---

## D23. Fog has exactly one owner, and it is not Weather

**Context.** Rain-thickened fog lived in the weather patch group, which patches
`chunkopaque.fsh` and `chunktopsoil.fsh`. Nothing else. So rain thickened the air
for a hillside and not for the animal standing in front of it, and not for the
leaves blowing past it either.

That was not a tuning problem. It was a consequence of which programs the weather
group happened to reach, and no amount of adjusting `FogStrength` could have
fixed it.

**Chosen.** Fog moves to the atmosphere group, which anchors on vanilla's
`applyFog` - byte-identical in all seven shading programs - and patches four of
them. Weather still decides *how much*; it no longer renders it. The arithmetic
is unchanged, so the validated look is preserved; only its reach changed.

**Why not both groups on one anchor.** Two groups replacing the same line couples
their rollbacks, which this project forbids for a reason: whichever applies second
finds its anchor gone, and a group that rolls back takes the other's declarations
with it. Weather's snippet therefore moved five lines down, to
`getBrightnessFromShadowMap` - which it already renamed - so the rename and the
injection are now one patch instead of two, and the two groups share no line.

**Consequence to expect in game.** Entities and particles will fog in rain where
they previously did not. That is the fix, and it will read as a change.

**Status.** Active. `atmosphere.yaml` owns `applyFog`; a smoke check fails the
build if a second group takes it.

---

## D24. The atmosphere is one transport, not eleven effects

**Context.** Eleven atmospheric features landed in one shader. The obvious
arrangement - each one a multiplier applied in turn -

```glsl
color *= fog; color *= haze; color *= rain; color *= cloud; ...
```

loses energy uncontrollably, and the reason it keeps getting built is that every
one of the eleven looks reasonable while it happens. This project has already
shipped that failure once at a smaller scale: three subsystems each washing out
the same rainy afternoon, each tuned alone and each defensible alone, which is
why `VisualBudget` exists.

**Chosen.** The features contribute to two quantities rather than to the colour:

| | |
|---|---|
| Extinction | how much of the surface's own light survives the trip |
| Inscatter | what colour the air adds in its place |

composed once as participating media:

```
out = surface * T + inscatter * (1 - T)
```

Extinction sources **sum**, which is what extinction coefficients do, and the sum
is capped. Inscatter gains **sum** and are capped once, rather than each scaling
the result of the last.

**Vanilla's fog enters as a transmittance.** `fogWeight` is `1 - T_vanilla`, and
the mod's media multiplies it. Multiplying *transmittances* is not the mistake
above - it is what stacked media do, and it is what makes the zero case exact:

> With every strength at zero the added density is zero and the height factor is
> one, so `T = 1 - fogWeight`; the gain is zero and both colour shifts return
> their input, so `inscatter = fogColor`. The result is
> `mix(surface, fogColor, fogWeight)` - vanilla's `applyFog`, character for
> character.

That is an **algebraic identity**, not a tuning coincidence, and it is the whole
safety argument for a mod owning `applyFog` in four shading programs. It is
therefore tested as one, across a sweep of fog weights and distances, rather than
asserted in a comment.

**The ceilings are written twice.** A shader cannot read a C# field, so
`VV_ATMOS_MAX_DENSITY` and `AtmosphereInputs.MaxAddedDensity` are two copies of
one number. A smoke check compares them. The alternative - trusting two files to
be edited together - is how `vv_cloudDensity` shipped meaning "no clouds at all".

**Status.** Active. `atmosphere.glsl`, `AtmosphereInputs`.

---

## D25. Three layers, and the middle one exists to keep the other two honest

**Context.** `EnvironmentState` already had a rule: nothing config-scaled may
enter it, because a strength slider states what the player wants and the state
describes what the world is doing. The atmosphere needed somewhere for the two to
meet, and somewhere for normalisation to live.

**Chosen.**

```
game state -> AtmosphereState -> AtmosphereInputs -> uniforms
(the game)   (what the air is)  (what to draw)       (GPU)
```

`AtmosphereState` holds facts, guarded and last-good, never scaled. A check fails
the build if it references a client API, a render type, a config, or a uniform
name.

`AtmosphereInputs` is the only place config, world state and the `VisualBudget`
grant multiply together. It is also where **normalisation** happens: the shader
never sees a temperature in degrees, an altitude in blocks, or a cloud density in
whatever unit the ambient manager uses.

Both are pure and free of every game type except plain vector maths, which is
what lets the independence, coupling and normalisation checks run without a
client. That property is not incidental - it is the reason those checks can exist
at all.

**Why not normalise in the shader.** Because then every shading program would
need to understand Vintage Story's units, and four programs understanding them
separately is four places to disagree. It also moves work from once per frame to
once per fragment for no gain.

**Why the base fog density is NOT normalised.** It is an extinction coefficient
and the shader uses it as one, in the same `exp(-sigma * d)` the game uses.
Squashing it to 0..1 would destroy the only unit in the struct with a physical
meaning. Normalisation that erases a real difference is worse than none, because
the difference is gone and nothing reports it.

**Status.** Active.

---

## D26. Three atmospheric features are foundation only, and two of them for a
## structural reason rather than a missing API

**Context.** Section 35 of the brief: if a feature cannot be completed because a
data source is unavailable, build the interface and mark it, rather than faking
it. A fake simulation that a future reader mistakes for real game state is worse
than an honest gap. Three features landed there, for two different reasons.

**Cloud-edge scattering - a DATA GAP.** The game's cloud tiles say how much cloud
sits above a place. Nothing in them locates an individual cloud's *edge*, and
`CloudTileReader` already reads everything there is. What is knowable is that a
sky which is neither clear nor solid has edges in it somewhere, so the feature
keys on `BrokenCloud` = `4c(1-c)` - peaking at half cover, zero at both ends.
That is the most the data honestly supports, and it is labelled as such rather
than dressed up.

**Godrays and dapple interaction - an ARCHITECTURE GAP.** Both are blocked by the
same rule, and the rule is right.

Vanilla already has crepuscular rays: `godrays.fsh` radially blurs the frame from
the sun's screen position, weighted per pixel by the green channel of the glow
buffer the shading programs write. The correct integration is a number written
into that channel - no pass, no render target, no texture unit. But
`pseudopbr.yaml` already owns the `outGlow` write in `chunkopaque.fsh`, where it
folds in the canopy shafts.

Dapple is the same shape: it lives in the pseudopbr group and cloud shadows in
the weather group, while the atmosphere is its own.

A second group patching a line another group already rewrote is exactly what this
project forbids - whichever applies second finds its anchor gone - and a function
or varying shared across groups couples their rollbacks, so one group failing
would leave another calling something that no longer exists. That is not a
technicality: it is the rule that keeps a patch failure from taking the world
render.

**Chosen.** `vvAtmosGodrayLevel` is written, correct, and reachable through debug
view 9. Nothing calls it in the composed frame. Debug view 12 draws the dapple
strength flat, which is the honest picture: the value arrives and nothing reads
it.

**The resolution is not "merge the groups".** It is that whoever next revises
pseudopbr's `outGlow` patch decides whether to fold the atmosphere's godray level
in, accepting that the two would then share a fate. That is a real trade and it
should be made deliberately rather than by an agent who did not know the rule
existed.

**Status.** Active. Foundation only, recorded in STATUS section 6.

---

## D27. The flora taxonomy is vanilla's wind modes, not a block list

**Context.** Every vegetation feature in this mod - transmission, wetness
pooling, dapple, shafts - keyed on `vvIsFoliage()`, which returned "does this
fragment carry any wind mode at all". One bit. A grass blade, an oak leaf, a
hanging pear and a strand of seaweed were the same material to the renderer.

**What was already there.** Vintage Story classifies its own vegetation per
vertex. `renderFlags` bits 25-28 carry a wind mode and vanilla's own
`applyVertexWarping` names each one in its own comments: `Leaves`, `Fruit`,
`WaterPlant for Seaweed`, `Weak Wind No Bend (for foliage with non bending
stems)`, `Weak Wind, Inverse Bend (for vines)`. Eleven plant classes, plus two
liquid modes that are not plants at all.

Bits 29-31 carry wind data, a bend multiplier that for every bending mode is the
vertex's height up the plant - a free base-to-tip thickness gradient.

**Options considered.**

1. A block-ID list. Rejected: wrong for every mod that adds a plant, and it
   would need maintaining against every game update.
2. Inferring from the texture - green dominance, alpha coverage, luminance.
   Rejected, and it is wrong in both directions: an autumn canopy is not green
   and a painted green wall is not a plant. A check now fails the build if a
   colour test appears in the shader.
3. Reading the classification the game already computed.

**Chosen.** Option 3. It costs one shift and one mask, needs no list, and is
correct for content this mod has never seen, because a mod whose plant moves in
the wind has to set the same flag to get the animation.

**What it fixed, in order of visibility.**

**The forest floor.** `vvCanopyDapple` rejected every plant, to keep sunflecks
off the canopy that casts them. That excluded the whole understory as well, so a
forest floor's tall grass and flowers stayed evenly lit while the soil between
them was dappled - and the two read as different places standing in one wood.
Only the canopy is exempt now.

**Backlit fruit.** A pear carries a wind mode, so it was foliage: it transmitted
like a leaf and was tinted toward yellow-green on the way out. It is thick,
opaque and has no chlorophyll to filter with.

**Uniform transmission.** A grass blade is one cell layer; a canopy is many
leaves deep. They now differ, and the tip-to-root gradient means a backlit meadow
glows at the tips rather than uniformly.

**Uniform wetness.** How much rain STAYS on a plant is a fact about its shape. A
cupped flower holds what a grass blade sheds; aquatic flora cannot get wetter.

**Two exclusions worth stating.** Vines are not understory - they hang from the
occluder, so they are part of it rather than under it. Aquatic flora is not
either: the water above it already attenuates the sun and vanilla handles that.

**The failure mode.** An unrecognised wind mode - a future vanilla addition, or a
mod using one this taxonomy has not seen - gets a conservative middle thinness
and is still treated as a plant. Zero would look like painted cardboard and
grass-level would glow; the middle is wrong by a little in both directions rather
than badly in one.

**Why the mode numbers are tested rather than trusted.** They belong to the game.
A version that renumbered them would silently turn every grass blade into a pear,
and nothing else in the repository would notice, so `FloraTaxonomyChecks`
transcribes them from the dumped shader and compares.

**These bits are overloaded.** In `chunkliquid`, `LiquidWaterModeBitMask` is the
same `0xF << 25` and `LiquidExposedToSkyBitMask` overlaps the wind data. The
taxonomy is only meaningful in the block programs, which are the only two this
patch group touches.

**Wind is not implemented and must not be.** Vanilla animates the geometry, the
shadow map follows the geometry, and the canopy measurement reads the shadow map.
Sunflecks already move because the leaves move - no clock, no noise field, no
animation on this mod's side. Adding one would replace an authoritative motion
with an invented one.

**Status.** Active, L2. `pseudopbr.glsl`, `tools/smoketest/FloraTaxonomyChecks.cs`.

---

## D28. The canopy takes sunlight, and vanilla already says which light that is

**Context.** `vvCanopyDapple` returns how much light a canopy removes, and the
caller multiplied the shaded pixel by its complement. That pixel is vanilla's
`litColor`, which arrives with sun, sky and block light **already mixed** - so a
torch hanging under a tree was dimmed by the tree.

This was recorded as an accepted limitation rather than missed:
`CheckDappleTouchesSunlightOnly` said so in its own summary. It stood because
separating the terms looked impossible from inside the function - vanilla mixes
them in the vertex shader and hands over one colour.

**What was already there.** Vanilla computes, for its own use in
`getBrightnessFromShadowMap`:

```glsl
blockBrightness = clamp(max(bGlow, max(bPoint, bBlock)) - bSun/2, 0, 1)
```

The strongest of glow, point light and block light, net of half the sun. High
where a torch dominates, zero where the sun does. It is precisely "how much of
the light here is local", and it is already a varying in both patched programs.

**Chosen.** Scale the canopy term by its complement:

```glsl
float local = vvLocalLightShare();
float canopy = clamp(shaded, 0.0, 0.85) * (1.0 - local);
```

The green shade tint rides the same protected fraction, for the same reason:
torchlight did not pass through a leaf and did not pick up its colour.

**Why not an invented measure.** A second answer derived from daylight or scene
state would disagree with vanilla's in exactly the cases that matter - dusk, a
cave mouth, a lantern under a crown - and the disagreement would look like the
canopy flickering. A check fails the build if `vvLocalLightShare` stops reading
`blockBrightness`.

**Nothing changes where nothing should.** On an open forest floor at noon the
local share is zero and the term is bit-identical to what it was. The change is
confined to fragments a local light is actually reaching, which is the definition
of the defect.

**The guard is load-bearing, and measured.** `blockBrightness` is declared inside
`#if SHADOWQUALITY > 0`. `tools/verifypatches` was run with the guard removed:
**16 of 48 prefix combinations fail to compile**, exactly the `SHADOWQUALITY=0`
third. With shadows off there is no shadow map, `vvCanopyEvidence` returns 0 and
the canopy term is inert, so returning 0 keeps it inert rather than making it
wrong.

**A test lesson worth keeping.** The first draft of the new check verified only
that the sparing value was *defined*. Reverting the application while leaving the
definition in place passed it - the dead-check failure this suite has been bitten
by before. It now pins the application too, and the mutation fails.

**Status.** Active, L2. Not seen in a world: the scene that proves it is a torch
under a tree at dusk.

---

## D29. blockBrightness is the right local-light measure, because it is already
## vanilla's

**Context.** D28 scaled the canopy term by the complement of vanilla's
`blockBrightness`. That decision recorded *what* was done; this one records the
audit that was owed - whether the value actually means what the canopy term needs
it to mean.

**What vanilla computes.** Two branches, on `DYNLIGHTS`:

```glsl
// DYNLIGHTS == 0
blockBrightness = clamp(max(bGlow, bBlock) - bSun/2, 0, 1);
// DYNLIGHTS != 0
blockBrightness = clamp(max(bGlow, max(bPoint, bBlock)) - bSun/2, 0, 1);
```

Same shape in both: **the strongest local source, net of half the sun.** `bGlow`
is the block's own glow level from `renderFlags`; `bBlock` is the block-light
colour's mean; `bPoint` is the accumulated dynamic point lights; `bSun` is the
sun colour's mean. Identical in `chunkopaque` and `chunktopsoil`.

**What vanilla uses it for.** This is the finding that settles it. Inside
`getBrightnessFromShadowMap`:

```glsl
b = clamp(b + blockBrightness, 0, 1);
```

The game lifts its **own shadow term** by this value, so a torch-lit fragment is
not darkened by being in shadow. The canopy term is a second shadow-like
attenuation one stage later and needs the same protection for the same reason.
The mod is not repurposing the value; it is applying the value's own purpose once
more.

**Is it a share?** Not literally - it is a brightness difference, not a ratio.
Checked across the range anyway:

| State | max(local) | bSun/2 | Result | Behaviour |
|---|---|---|---|---|
| Daylight, no local light | 0 | 0.5 | 0 | canopy **bit-identical** to before |
| Strong torch, no sun | ~1 | 0 | ~1 | canopy fully spared |
| Strong torch, full sun | ~1 | 0.5 | 0.5 | canopy half applied |
| Weak torch, full sun | 0.3 | 0.5 | 0 | canopy full - the sun dominates |

Monotone, bounded, and zero exactly where there is no local light. `MINBRIGHT`
defaults to `0`, so `bBlock = max(MINBRIGHT, bBlock)` introduces no floor and the
first row is exact rather than approximate. Sufficient for a stylized renderer,
and it is the game's own number.

**The deliberate asymmetry.** `vvSunVisibility` **excludes** `blockBrightness`
and is tested for it. The two requirements are opposite and both are correct:
measuring how much canopy is overhead is a question about geometry, where a torch
is not a gap in the leaves; attenuating by that canopy is a question about which
light is solar, where a torch is not sunlight.

**Conclusion: no change.** The architecture was validated, not corrected.

---

## D30. A shaft needs a beam, and vanilla's godray pass never asks about weather

**Context.** `vvCanopyShaft` writes `outGlow.g`, the source mask `godrays.fsh`
radially blurs outward from the sun's screen position. It is **live** in both
patched programs - an earlier report calling it foundation-only had confused it
with the atmosphere subsystem's separate, genuinely unwired godray feature.

It responded to daylight, sun direction and angular proximity to the sun. It did
not respond to overcast.

**Why that is a gap and not a redundancy.** A shaft is sunlight scattering in air
along one direction. Under overcast there is no such direction: the sky becomes a
source the size of the sky, light arrives from everywhere, and there is nothing
for a beam to be made of.

Nothing downstream supplies it. `godrays.vsh` computes its `intensity` from the
sun-to-view angle and a dusk multiplier and **nothing else** - no cloud, no
weather, no ambient state. The mask this mod writes is the only place weather can
enter the beams at all.

**Chosen.** Scale the mask by `mix(1.0, VV_OVERCAST_DIRECT, overcast)` - the same
constant the direct specular lobe and foliage transmission already use.

Three places now model one physical fact: *a clear sky is a small bright source
and an overcast one is not*. Three constants would be three things to drift
apart, so there is one, and checks fail the build if a second appears.

**What was deliberately not changed.** The shaft's flora dependence -
`vvIsCanopy()` gives a strong mask, other flora none - looks at first like a
material property leaking into atmospheric transport. It is not: it asks *what is
occluding the sun here*, which is the shaft's own question. A check now fails the
build if it ever consults wetness, pooling, roughness or thinness, which would be
the real violation.

**Status.** Active, L2. Runtime-unverified: the scene is a low sun through a
forest opening, first clear and then overcast.
