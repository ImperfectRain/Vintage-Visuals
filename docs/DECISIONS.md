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

**Chosen.** Option 3. `SceneCaptureRenderer` copies the composed framebuffer
available at `AfterPostProcessing` into a half-resolution RGBA target: RGB the
scene, alpha the linear view depth. It tries `IRenderAPI.CurrentFrameBuffer`
first and falls back to `EnumFrameBuffer.Primary`, because a runtime capture
showed that a public Primary texture can be present without containing the
terrain colour image needed by reflections.

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

**Context.** Superseded by the emergency terrain recovery pass. `vvCanopyShaft`
used to write `outGlow.g`, the source mask `godrays.fsh` radially blurs outward
from the sun's screen position. That write is now disabled in both patched
terrain programs, and debug view 17 is the only remaining consumer until the
terrain render-state contract is re-proven.

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

---

## D31. Use() binds, so a binder that loops must unbind between programs

**This one shipped and crashed a real client**, on the first attempt to run the
mod in a world:

```
System.InvalidOperationException: Already a different shader (chunkopaque) in use!
  at VintageVisuals.Atmosphere.AtmosphereShaderBinder.Upload(...)
```

**Cause.** `IShaderProgram.Use()` does not merely select a program for the next
uniform write - it BINDS it, and throws if a different one is already bound.
`AtmosphereShaderBinder` uploads to four programs in a loop, bound the first and
never unbound it, so the second iteration threw.

Every other binder in this mod pairs `Use()` with `Stop()`. This one did not, and
it is the only one that iterates more than two programs, which is why the mistake
was survivable in every other file and fatal here.

**Why the existing guard did not help.** `OnRenderFrame` already checks
`CurrentActiveShader != null` and skips the frame - the documented protection
against this exact exception. It is the right guard for the case it was written
for, which is *someone else* holding a program at this render stage. By the
second loop iteration the offending bind was the binder's own, and the guard had
already been passed.

**Why nothing caught it.** 794 static checks, 34 shader checks, all 48 prefix
combinations, and a green mutation suite. The defect is in no shader and in no
value: it is a lifecycle that only exists at runtime. The repository's own
CLAUDE.md documents that `Use()` throws, and the code still shipped without the
pairing, which is the strongest available argument that prose warnings do not
substitute for checks.

**Chosen.** Unbind inside the per-program helper, and add
`tools/smoketest/ShaderBindingChecks.cs`, which asserts:

- every `Use()` in `src/` is paired with a `Stop()`, comments stripped first
  because this repository discusses both at length;
- the atmosphere binder's per-program helper binds and unbinds **within itself** -
  a `Stop()` after the loop pairs correctly and still crashes;
- the unbind follows the bind;
- no early `return` sits between them, which leaks a bound program by another
  route.

All three failure shapes were confirmed to fail the checks.

**The wider lesson, recorded because it will recur.** This mod's static suite is
strong on shader semantics and had nothing at all to say about render-thread
lifecycles. Anything that binds, allocates, or holds GL state belongs in that
second category, and the honest conclusion from this crash is that L2 evidence
was worth less than the volume of it suggested.

**Status.** Fixed. The fix itself is **runtime-unverified** - it needs the world
that crashed to load and stay up.

---

## D32. A gate is not a dimmer: the sunset suppression

**Found by looking at the game**, in a screenshot of a sunset forest that
looked like a midday forest with an orange sky.

`vv_sceneDayLight` is documented in `scene.glsl` as **"0 midnight, 1 noon"**. It
falls steadily through the afternoon and is well under half at sunset. Three
effects multiplied by it directly:

- foliage transmission,
- canopy dapple,
- light shafts.

All three **peak at a low sun**. Backlighting needs the sun behind the leaves;
shafts need a shallow angle through the air; a canopy at a low sun throws long,
structured shade. Every one of them was being suppressed hardest at the exact
moment it should have been strongest, and left at full strength at noon, when a
high sun makes all three least interesting.

**The factor was never meant to be a scale.** What it was there to say is *not at
night*. That is a gate, and implementing a gate as a linear multiply is the
defect. `vvSunPresence()` is a smoothstep that reaches full strength while the
sun is still comfortably up and closes as it sets.

**The direct lobe deliberately keeps the linear scale.** A dimmer sun really does
make a dimmer highlight; that one is physics rather than a gate, and converting
it would have made midnight as bright as noon. A check pins the distinction from
both sides: the three gated effects must not scale linearly, and the direct lobe
must.

**Why no test caught it.** Every check asked whether the effects *stop at night*,
and they did. None asked whether they *survive sunset*, because nothing in the
suite knew that sunset was the case that mattered. The invariant now pinned is
the threshold's range: open through golden hour, closed before dark. If it crept
upward the defect would return gradually and look like tuning.

**The wider lesson.** This is the second defect in a row that no amount of static
analysis would have found and one screenshot did. The first was a shader
lifecycle; this one is a curve being used for the wrong purpose. Both were
invisible to a suite that reasons about code and not about a scene.

**Status.** Fixed, runtime-unverified. The scene that proves it is the one that
exposed it: a low sun behind a forest.

---

## D33. A metal may only give up the diffuse the environment pays back

**Reported from the game**: "reflective materials seem a bit broken... they absorb
a lot of light." The surface was a gold block.

**Cause.** Metalness works in two halves - raise F0, and remove the diffuse,
because a conductor scatters almost nothing back out. The removed diffuse is
meant to reappear as an environment reflection. But that reflection is

```glsl
environment * fresnel * vv_pbrAmbient
```

and `vv_pbrAmbient` is the sky-reflection slider, which defaults to **0.2**. So a
metal lost **all** of its diffuse and got back **a fifth** of a reflection. It
went dark, and the darker the player set their sky reflections, the darker every
metal in the world became.

**The slider was doing two unrelated jobs.** On a dielectric, lowering sky
reflection is a taste choice about how much sky a surface shows - it removes a
highlight that was sitting on top of an intact diffuse. On a metal it is the only
thing funding the diffuse that metalness takes away, so lowering it removes light
with nothing to replace it.

**Chosen.** Scale the removal by the payback:

```glsl
float metalPayback = clamp(vv_pbrAmbient, 0.0, 1.0);
result *= mix(1.0, 1.0 - metalness * metalPayback, vv_pbrSpecularStrength);
```

At `vv_pbrAmbient` 1 the behaviour is exactly what it was - this is not a silent
retune of every metal. Below that, a metal keeps the share of its diffuse the
environment is not going to return, and what it keeps plus what it reflects sums
to 1 across the whole slider. A check drives that sum at four settings.

**Not a physical correction so much as a refusal** to let one artistic slider
break energy conservation for one material class. The physically right answer is
that ambient light intensity is not a per-material taste control at all, which is
a larger change to how the ambient term is fed and is not attempted here.

**What this does NOT fix.** The same report also said reflections "don't display
a crisp texel perfect colourful reflection" and lack "proper reflection
perspective". That is the screen-space march's quality, not its energy, and it
has already been through four rounds of correction driven by screenshots. It
remains unresolved and runtime-bound.

**Status.** Fixed, runtime-unverified. The scene is a gold block in daylight with
sky reflection below 1.

---

## D34. Foliage transmission was inverted, and only a screenshot could tell

**The single largest visual defect found so far**, and the reason a sunset forest
looked flat.

`vvTranslucency` built the bent light ray from **`-l`** instead of `l`:

```glsl
vec3 through = normalize(-l + n * distortion);   // wrong
return pow(max(0.0, dot(v, -through)), power);
```

`l` points **to** the sun and `v` points **to** the viewer, so `dot(v, -through)`
reduces to roughly `dot(v, l)`. That is:

| Case | `dot(v, l)` | Result | Should be |
|---|---|---|---|
| Backlit - sun behind the leaf | -1 | clamped to **0** | **maximum** |
| Front-lit - sun behind the camera | +1 | **maximum** | ~zero |

So the effect fired on front-lit foliage, where a leaf has nothing to transmit,
and switched itself off on backlit foliage, which is the only case it exists for.

**How it was found.** Debug view 13, photographed twice from one position at a low
sun: facing **away** the canopy glowed; facing **into** the sun the canopy went
black. Two screenshots, six blocks apart, and the diagnosis was unambiguous.

**Why nothing static caught it.** It is a sign, in a formula that is
dimensionally correct either way. Every existing check asked whether transmission
*existed*, whether it stopped at night, whether it collapsed under overcast,
whether it varied per plant - all true, all of it, with the effect pointing the
wrong way. None asked which direction it pointed, because from inside the code
there was nothing to suggest it pointed anywhere in particular.

**The correct form** bends the light direction itself through the surface,
`l + n * distortion`, and asks how directly the viewer is looking back down that
ray - the standard fast-subsurface approximation.

**The check now drives the arithmetic**, transcribed independently, and pins the
ORDERING backlit > side-lit > front-lit. That ordering is the definition of the
effect and no retuning of distortion or power may reverse it. A separate check
pins the shader's own sign, so the two together catch both a reintroduced
inversion and a drift in the transcription.

**Status.** Fixed, runtime-unverified. The scene that proves it is the one that
exposed it: debug view 13, low sun, facing into it.

---

## D35. The dapple gate and its application required opposite conditions

**Reported from the game**: "no visible sunspots apart from where the sun breaks
through the shadows in vanilla." That sentence is a precise description of a
self-cancelling pair, and it was one.

`vvCanopyEvidence` opens with

```glsl
float shadowed = 1.0 - vvSunVisibility();
if (shadowed < 0.01) return 0.0;      // must be IN SHADOW
```

and the application site was

```glsl
float shaded = vvCanopyDapple(...) * clamp(shadowBrightness, 0.0, 1.0);
                                     //  ^ high only where LIT
```

The product is near zero at both ends and non-zero only in the narrow penumbra
between them. So the mod contributed essentially nothing beyond the shadow edges
vanilla already draws - which is exactly what the player saw and exactly what
they said.

**Both halves had a comment explaining why they were right.** The evidence needs
shadow because a lit fragment has no shadow to break up. The multiply was there
so a fragment vanilla had already put in full shade would not be darkened twice.
Each is defensible alone. Nothing in the codebase asks whether two conditions can
be true at the same time.

**Worse, the multiply contradicted the same function's stated purpose**, one
screen above it: *"CONTRAST. Vanilla's shadow bottoms out at 1 - shadowIntensity/2
... Deepening the shade BETWEEN the gaps, and leaving the gaps at exactly what
vanilla lit them to, widens the gap between the two."* Deepening the shade can
only happen where the fragment is shadowed. The multiply removed the effect
precisely there.

**Chosen.** Drop the multiply. Everything it nominally protected is protected
three times over already:

| Concern | Handled by |
|---|---|
| A wall or cave mouth reading as canopy | `vvCanopyStructure` rejects a single straight edge |
| Anything reaching black | the application caps at 0.85 |
| Torches being dimmed | `vvLocalLightShare` - D28 |

A check now pins that the gate and the application do not require opposite
conditions, and separately that each of those three protections is still in
place - because the multiply's removal is only safe while they are.

**The pattern, third time.** Two terms, each individually correct, each with a
comment justifying it, multiplying to nothing. This is the third defect of that
shape found by running the game rather than reading the code, after the inverted
transmission sign and the sunset gate. Static analysis reasons about statements;
it has no notion of whether two predicates can hold together in a real scene.

**Status.** Fixed, runtime-unverified. The scene is a forest floor under broken
canopy, and it is the scene that has failed three times now.

---

## D36. An automatic exposure lift must bring its own shoulder

**Reported from the game**: "severe highlight clipping around the sun."

`colorgrade.glsl` does

```glsl
graded *= vv_exposure * adaptation;
...
graded = mix(graded, vvACESFitted(graded), vv_tonemapStrength);
```

Eye adaptation multiplies the whole frame by up to `DarkGain`, which defaults to
**1.6**. `vv_tonemapStrength` - the curve that would roll the result off -
defaults to **0**.

So the shipped default combination **guaranteed clipping**. Anything vanilla put
above `1 / 1.6 = 0.625` was pushed past white and flattened. Around a low sun that
is a wide band of sky, and in a dark forest adaptation sits near its maximum
precisely when the player is looking out at a bright one.

**This file's own header already said so**: *"exposure -> white balance ->
tonemap ... the curve's shoulder rolls off highlights that exposure was..."*. The
design knew the two belong together and then shipped them as independent settings
whose defaults contradict each other.

**The distinction that fixes it without removing the control.** `vv_exposure` is
the PLAYER'S choice; they may clip with it if they want to. Adaptation is the
RENDERER'S choice - nobody asked for it, it happened because the scene got dark -
so the renderer owes the highlights that pay for it.

```glsl
float autoLift = clamp(adaptation - 1.0, 0.0, 1.0);
float shoulder = max(clamp(vv_tonemapStrength, 0.0, 1.0), autoLift);
```

At adaptation 1 this is exactly `vv_tonemapStrength`, so a bright scene, or a
player who dialled the tonemap to zero deliberately, gets precisely what they got
before. **Zero still means vanilla.** The shoulder can only ever rise, never fall
below what the player asked for, and a check drives that across the whole grid of
adaptation and tonemap settings.

**Not a tuning change.** No constant moved. `DarkGain` is still 1.6 and
`TonemapStrength` still defaults to 0; what changed is that an unrequested
brightening can no longer be applied without the curve that absorbs it.

**Status.** Fixed, runtime-unverified.

---

## D37. The comparison wipe had to announce itself

**Reported from the game as a defect**: "a highly visible vertical lighting and
atmosphere discontinuity through the scene."

It was `CompareWipe: 0.5` - the comparison tool from D-none/the wipe commit,
still switched on. `gl_FragCoord.x < 0.5 * FrameWidth` returns vanilla, so
lighting, atmosphere, brightness and foliage response all change abruptly across a
vertical line. That is the tool's definition, working exactly as built, and the
seam in the reported screenshot sits at half the frame width.

**The tool was indistinguishable from a bug**, and it cost a full diagnostic round
- several other observations in the same report were made while comparing against
a half-vanilla frame, and are unreliable for that reason.

**Chosen.** Draw a 2px mid-grey marker at the seam. A deliberate divider reads as
deliberate; an unmarked one reads as a renderer boundary. Mid grey because it must
be visible against both bright sky and dark forest floor without being mistaken
for a light or a shadow.

**The wider point.** A diagnostic that can be mistaken for the fault it is meant
to diagnose is worse than no diagnostic. This one inverted a whole round of
investigation, and the marker costs one comparison per fragment.

**Status.** Fixed, runtime-unverified.

---

## D38. Checks that ask about one thing cannot find defects that live between two

**The count that forced this.** Seven defects have been found in this mod by
looking at the game: the binder crash, reflections disabling themselves every
session, sunset suppression, the dark gold block, inverted foliage transmission,
canopy dapple cancelling itself, and highlight clipping around the sun. The
static suite found **none** of them, and it was green - 837 checks - the whole
time they shipped.

That is not a gap in coverage. It is a gap in KIND.

Every check in the suite before this one asks a question about a single thing: is
this uniform uploaded, does this anchor still match, is this constant quoted
correctly, does this file exist, is this text ASCII. Every one of the seven
defects was a question about two things multiplied together:

| Defect | Line A | Line B |
|---|---|---|
| Sunset suppression | a gate meaning "not at night" | implemented as a linear dimmer |
| Dark gold | metalness removes the diffuse | the ambient slider funds a fifth of it back |
| Inverted transmission | a bent ray built from `-l` | a viewer dot product that then reduces to `dot(v, l)` |
| Dapple cancelled | evidence requires the fragment SHADOWED | application multiplied by it being LIT |
| Highlight clipping | adaptation lifts by up to 1.6x | the tonemap that would roll it off defaults to 0 |

Each line, read alone, is correct - which is exactly what made them survive
review. The product is where the defect is, and no amount of reading either line
finds it.

**Chosen.** A check category that runs ARITHMETIC on compositions:
`tools/smoketest/SceneInvariantChecks.cs`. It began with ten invariants and now
runs I1-I19, each naming the defect it exists to prevent and the commit that
fixed it. An invariant with no
defect behind it is not added - it would be one more green line making the suite
look like evidence it is not.

**The expressions are pulled out of the shipped `.glsl` at test time**, by
`GlslEval`, rather than retyped into C#. A transcription is a second
implementation of the thing under test, and two implementations disagreeing is
the entire bug class here. Only the leaf calls that read textures and shadow maps
are stubbed; every operator, constant and factor in between is literally the one
that ships, so a factor someone adds later is in the check whether or not anyone
updates the check.

**What it cannot do**, stated because a check that overclaims is worse than none:
it cannot say an effect is VISIBLE. Every invariant here passes on a mod with
every strength defaulted to zero. Visibility stays a runtime question.

**Rejected: porting the shading model to C# and comparing.** That is
`PbrParityChecks`, it is the right tool for an algorithm with a reference
implementation, and it is the wrong one here - there is no reference for "the
canopy term composed with the local-light exemption", only the shipped source.

**Status.** Nineteen invariants, all passing, with mutation coverage tracked in
`tools/mutate/mutations.tsv`. Found one live disagreement on its first run: see
D39.

---

## D39. A check is evidence only when it has been seen to fail

**The problem with the number 837.** A suite that has been green throughout the
period in which seven defects shipped has demonstrated that it passes. It has
demonstrated nothing about whether it would fail. Those are different claims and
only the second is worth anything, and the count of checks - which is what gets
reported - measures the first.

**Chosen.** `tools/mutate/mutation-test.sh` and `tools/mutate/mutations.tsv`.
Each row reintroduces a real historical defect by exact literal substitution,
runs `tools/smoketest`, and requires a failure whose name matches the invariant
that claims to guard it. The mutation is then reverted with `git checkout --`, so
the harness refuses to run on a dirty tree.

The table began with twelve rows - the seven runtime defects plus five that were
the next instance of a class that had already bitten once. It now contains 52
mutations.

**What it changes about how this project reports itself.** "N checks pass" is
retired as a statement of confidence. The statement that means something is "this
defect, reintroduced, is caught", and it is the one the audit and the checklist
now carry. `docs/CHECKLIST.md` grew a mutation-coverage table for exactly this:
a row there names a defect, the mutation that reintroduces it, and the check that
catches it.

**Rejected: extending the L0-L4 ladder to L6** as the runtime-evidence brief
proposed. The complaint behind it is real and precise - L2 with 837 green checks
and L2 with nothing look identical - but the fix does not want a new level. A
level says how far something has been PROVEN to work, and mutation coverage says
how well its failure is GUARDED; they are orthogonal, and folding one into the
other would renumber 1300 lines of STATUS to say something a column says better.
Adding a third numbering scheme to a project whose stated rule is "do not add a
third account of a disagreement" would have been the wrong shape.

**Found on the first run, and this is the point of the exercise.** I4's
arithmetic contradicted a comment in `pseudopbr.glsl`: the green shade tint
claimed to "move colour between channels rather than adding or removing any",
and its weights (-1.0, +0.6, -0.7) sum to -1.1 per unit of tint. It removes a
little under 2% of luminance at the capped canopy. The comment was corrected
rather than the weights rebalanced - 2% is far below visible, no forest has been
looked at since the dapple gate was fixed, and retuning the colour of unobserved
shade is guessing - and the invariant now bounds the cost so it cannot grow into
a real light loss outside `VisualBudget` without a check failing.

**Status.** 52 mutations, 52 caught, 0 missed on the last documented full run.
Run it with `VINTAGE_STORY=... bash tools/mutate/mutation-test.sh` on a clean
tree.

---

## D40. "Loaded but not applied" named a symptom shared by two opposite causes

**Reported from the game**: "effects apart from some atmosphere effects are no
longer functional." The log agreed, and was specific about it:

```
patch group 'pbrparticle':     OK (particlescube.fsh)
patch group 'pbrentity':       OK (entityanimated.fsh)
patch group 'atmosphere':      OK (entityanimated.fsh, particlescube.fsh)
patch group 'pseudopbr':       loaded but not applied to any shader yet.
patch group 'pseudopbrtopsoil':loaded but not applied to any shader yet.
patch group 'colorgrade':      loaded but not applied to any shader yet.
patch group 'weather':         loaded but not applied to any shader yet.
```

`_everApplied` is session-lifetime, so that is not a snapshot: across the whole
session the only two files ever patched were `entityanimated.fsh` and
`particlescube.fsh`. `chunkopaque.fsh`, `chunkopaque.vsh`, `chunktopsoil.*`,
`final.fsh` and `particlesquad.fsh` were never patched once - and `atmosphere`,
which targets both sets, landed on exactly the first and none of the second.

The split is not per group, per subsystem or per config flag. It is per FILE, and
it falls exactly along the game's two shader-loading batches: everything in the
partial reload was patched, everything that only appears under the game's own
`Loading shaders...` pass was not. Meanwhile `tools/verifypatches` applies every
one of those patches to the game's own dumped shaders and compiles the result in
all 48 define combinations. The patches are fine. The delivery is not.

**The diagnostic could not tell the two apart, and that is the actual defect
being fixed here.** "Loaded but not applied" is reported in two situations with
nothing in common but the wording: the hook never saw the target shader, or the
hook saw it under a name no patch matches. Opposite fixes. The line sent this
round hunting a patch failure that had never occurred, which is the second time
this exact message has cost a debugging round - see the comment on
`_everApplied`, written after the first.

**Chosen, first.** A census: every filename the hook has ever handed the patcher,
and every patch target that never arrived. Silent when nothing is missing,
because in the healthy case it is forty lines saying nothing. When something is
missing it prints both lists, so a target absent from the delivered list means
the hook never saw the program, and a target present under another name means the
patch filename is wrong. One run now separates causes that previously needed
guessing.

**Chosen, second, and this one is a HYPOTHESIS.** `FindLoadShaderMethod` took
`FirstOrDefault` over the `LoadShader(.., EnumShaderType)` overloads.
`Type.GetMethods` makes no ordering guarantee, so which overload got hooked was a
property of how the game's assembly happened to be compiled rather than of
anything this mod decided. If `ShaderRegistry` exposes more than one route into
shader loading, hooking one of them produces precisely the observed shape:
some files patched, others silently never seen, no error anywhere. The hook now
patches EVERY matching overload, and logs their signatures at install so the next
log answers the question directly.

That it is the right fix is not yet established - the census run will say. It is
a defect on its own terms regardless: a lookup whose result depends on reflection
ordering is a coin toss dressed as a lookup.

**What makes hooking all of them safe.** Applying a group twice is not a doubled
effect, it is a duplicate function definition and a shader that will not compile
- so it costs the world render rather than the feature. `ShaderPatcher` now
appends `// VintageVisuals:<group>` after a successful group and refuses source
that already carries it. A GLSL line comment, after the last line, so it can
never displace `#version`; visible in a dump, where it tells a reader exactly
which groups reached the file; and it is the same guard that would have caught
this mod's own contaminated shader dumps.

**Status.** Census and idempotence: implemented, L2, mutation-proven. Root cause:
NOT established. The next log decides whether the overload change fixed it or
merely ruled a cause out.

---

## D41. Hooking every loader overload was a regression, not a fix

**Supersedes the second half of [D40](#d40-loaded-but-not-applied-named-a-symptom-shared-by-two-opposite-causes).** D40's census stands; its overload change is reverted.

**Reported from the game**: after `bd988bd`, none of the mod's visuals work. Before
it, two shaders were being patched and the rest were not. So the change made to
diagnose a partial delivery failure produced a total one.

**The mechanism, from the code alone.** `Install()` wraps its patching in a single
`try` and sets `_installed = true` only after the loop finishes:

```csharp
foreach (MethodInfo target in targets) _harmony.Patch(target, postfix: postfix);
_installed = true;
```

A throw on ANY element - an abstract declaration, an open generic, anything
Harmony declines - aborts the whole block. The hook that was working is lost
along with the ones that were not, `_installed` stays false, and every subsystem
that depends on shader patching goes inert. One `Patch` call cannot fail
partially; N of them share a fate, and the fate of the set is the worst fate in
it.

That is a structural certainty and it does not need a log to establish. Whether
it is what fired is a separate question, and the answer is not needed to act:
D40 said in its own text that the overload change was a hypothesis with the root
cause NOT established, it made the observed behaviour worse, and reverting
returns to the last state the user saw working at all.

**Chosen.** Hook exactly one method, selected exactly as the last known good
build selected it - same predicate, same `GetMethods` order, so the method that
gets hooked is unchanged rather than merely similar. Swapping the selection rule
while diagnosing a delivery failure would change two things at once.

**The candidates are still enumerated and logged.** Whether `ShaderRegistry`
exposes a second loading route is a real open question and one line answers it:
the install now names every candidate it found and says which one it hooked.
Listing them costs nothing. Patching them cost everything.

**The sentinel goes too.** `// VintageVisuals:<group>` existed only to make
multi-hook delivery idempotent. With one hook there is no second delivery, and
it was writing this mod's bookkeeping into every shader it touched to guard
against a problem that no longer exists. Its group names are also prefixes of
each other - `pseudopbr` inside `pseudopbrtopsoil` - which is a collision waiting
for the first file both groups target.

**What replaces both, and this is the part worth keeping.** `LogDelivery()`
prints one line per patch target and keeps apart the six states that "OK" and
"loaded but not applied" were collapsing:

```
chunkopaque.fsh: program=ShaderProgram type=FragmentShader source=48213 -> 51002 applicable=14 applied=14 writtenBack=yes
final.fsh:       NEVER reached the hook. Nothing was tried; no patch of its group has failed, because none was attempted.
```

Seen, matched, applied, written back - measured. Compiled and actually used are
the game's side of the boundary and are not claimed.

**The rule this leaves behind.** Prefer one authoritative interception point to
several speculative ones. When the authoritative path is not known, say so and
measure - do not widen the net and hope. Widening the net is what took a mod that
half worked and made it not work at all.

**Status.** Reverted and instrumented, and **confirmed in game**: reported
functional again at `8b318ea`. That closes the total loss and settles the
mechanism above - the multi-overload hook was the regression.

The ORIGINAL partial failure that `bd988bd` was trying to diagnose is a separate,
still-open question, and it is now unmeasured rather than merely unexplained. It
was observed in exactly one log; the visuals being back suggests the chunk and
final shaders are being delivered, which that log said they were not. Either the
earlier reading was of a transient startup state, or it persists and something
else is carrying the visuals. `LogDelivery()` answers it in one line per target
and has not been read yet.

---

## D42. A budget that overrides a rate must say when it does

**The reported problem**: reflective materials receive strong light and read as
environmental colouring rather than as a reflection of the world. Trees, walls
and sky are not recognizable in them.

**No runtime access this pass**, so nothing here is a visual claim. What a source
audit can establish, it did.

**Finding one: the stride constant is not the stride.** The march computes

```glsl
float wanted = max(travel.x, travel.y) / max(0.5, VV_SSR_STRIDE);
int steps = int(clamp(wanted, 4.0, float(VV_SSR_STEPS)));
```

`VV_SSR_STRIDE` is 2 capture texels and carries a long comment explaining that a
uniform screen-space rate is what makes a reflection foreshorten instead of
smear — it was written after a reported artefact where a trunk smeared across the
whole floor. The first visible scene used `VV_SSR_STEPS = 24`. For a 500-texel
traverse — an ordinary grazing ray on a flat floor, and the only kind that
carries a reflected tree — `wanted` is 250, `steps` was 24, and the ray was
walked at about 21 texels per step. The overshoot the stride exists to prevent
returned in full on exactly the rays the feature exists for, and the runtime
debug masks showed ringed hit/miss bands. The cap is now 96: still bounded, but
much less likely to skip whole reflected silhouettes on flat grazing surfaces.

**Finding two: one red was four faults.** Debug view 39 reports every miss as
red. A miss means the ray pointed back at the camera, or its origin projected off
the captured frame, or it walked off the edge without crossing anything, or it
crossed the right surface and was rejected for landing further behind it than
`VV_SSR_THICKNESS` allows. The last two are opposites. "Never found" wants a finer
march; "found and thrown away" wants a tolerance argument. They were the same
colour, so the brief's own A-to-I diagnosis could not be carried out from the
views that existed.

That is the third time this project has lost a round to a diagnostic collapsing
distinct states - after `loaded but not applied` and after `OK` - which is why it
now has an invariant of its own rather than a fix of its own.

**Chosen.** Instrument, do not tune. `VvSceneHit` carries the outcome code, the
steps taken, the steps the stride asked for, and the residual thickness at the
crossing. Nothing downstream reads them; the control flow is unchanged, and every
existing return goes to the same place with the same value. Three views expose
them: 48 the outcome as a category, 49 whether the stride survived the budget, 50
the crossing residual against its tolerance.

**Explicitly not done.** No constant was changed. Not the range, not the step
count, not the stride, not the thickness, not the facing fade, not the capture
resolution, and no term in the ambient-specular integration. The brief's central
question - reconstruction quality or contribution quality - is answerable in one
run with these views and is not answerable from source, and answering it wrongly
would mean tuning a downstream variable to compensate for an upstream failure.

**Status.** STRUCTURALLY CORRECT - VISUALLY UNVALIDATED. Two new invariants,
I11 and I12, four new mutations, all caught. `verifypatches` compiles the new
views in all 48 combinations.

---

## D43. The reflection's hit tolerance is finer than the depth it judges

**Extends [D42](#d42-a-budget-that-overrides-a-rate-must-say-when-it-does).** D42
found the march coarse. This is the reason a finer march would not have helped.

An external audit of the reflection path was put to the code rather than taken at
its word. Most of it holds; one item needed correcting; one is an addition the
audit did not reach.

**Confirmed and fixed.** The capture target was `Rgba8` and
`vvscenecapture.fsh` wrote
`clamp(linear / max(1.0, zFar), 0.0, 1.0)` into alpha, so depth was **one byte**
and the finest difference expressible anywhere was `zFar / 255` blocks. The
target is now `Rgba16f`, the capture writes linear depth directly to alpha, and
the terrain shader reads that alpha directly. Debug view 51 now reports the local
half-float quantum at the sampled depth instead of the old zFar-wide byte step.

**The addition the audit did not reach still matters: refinement cannot rescue
quantisation.** More bisection converges the ray parameter against the same
sample. The fix belonged in the capture format, not in `VV_SSR_REFINE`.

**Confirmed: roughness does not coarsen the scene reflection.**
`vvPixelReflection` picks `cells` from roughness, quantises the direction, and
hands the quantised direction to `vvReflectionFallback` only. The captured world
ray now uses the geometric face normal, not the material normal, because
normal-map flecks produced a cone of near-floor hits instead of stable reflected
geometry. So the documented hierarchy - smooth sharp, rough coarse - governs the
analytic sky and not the captured world, and one surface can change character
across a hit boundary.

**Corrected: the luminance ceiling is a compression rule wearing an energy
rule's clothes.** The first scene-hit cap scaled anything above `envLuma * 1.2`
down to it. Environment 0.20 against a sunlit tree at 0.70 delivered 0.24 - two
thirds of the tree's luminance gone before PBR saw it. The cap is now
`envLuma * 2.0`: still bounded, but no longer crushing reflected object colour
against the local horizon.

**Confirmed and fixed, because it is a comment**: `vv_reflectCameraDelta` was
documented as "capture camera position minus this frame's" while
`PbrShaderBinder` uploads `now - then`. The upload is the correct one for the
shader's addition; the comment was backwards, in the one place a reader
debugging a sign error would look first.

**Corrected.** The audit reported that the documentation still describes the
capture's scale inconsistently. The code comments are explicit and right - HALF
in each axis, which is a quarter of the pixels - and the runtime log line used
the pixel-count wording, true of the count and ambiguous about the axis. It
misled a careful reader, so the log now says "half resolution in each axis". The
audit's conclusion was wrong; its instinct was not.

**Chosen: measure, do not convert.** `SceneCaptureRenderer` reports the actual
quantum against the tolerance once per session, at warning level when the
tolerance is the finer of the two. Debug view 51 shows the same comparison per
texel, with each crossing's residual expressed in QUANTA rather than blocks -
below one quantum, the number the march is deciding on is noise.

**Not done, deliberately.** No capture format change, no constant changed, no
roughness plumbed into the SSR path, no ceiling raised. An `Rgba16f` target keeps
the bridge conceptually identical and costs memory and bandwidth - a measurable
trade, and one to make against a measured `zFar` rather than an assumed one. The
roughness inconsistency and the luminance ceiling both sit DOWNSTREAM of the
depth problem, and changing them first would be tuning the visible result before
the information pipeline is trustworthy, which is the mistake the vegetation work
spent this project's last four passes eliminating.

**Ranking, for the next pass.** Depth precision first, because everything else in
the march is conditioned on it. Then the step budget from D42. Then the roughness
inconsistency. Then the luminance ceiling. Only then anything that could be
called strength.

**Status.** STRUCTURALLY CORRECT - VISUALLY UNVALIDATED, unchanged. I13 added,
three mutations, 24 of 24 caught.

---

## D44. The atmosphere was not disappointing, it was switched off

**Reported from the game**: "still a little disappointed in the vegetation and
atmosphere effects."

**Atmosphere shipped `Enabled = true` with all eleven of its effects at 0.0.**

It registered two renderers, held a slot in the game's ambient stack, uploaded
twenty-two uniforms, gated a patch group, and changed nothing whatsoever. The log
reported the patch group applied, which was true. The player saw no atmosphere,
which was also true. Nothing anywhere connected the two, so the conclusion drawn
from the screen was that the atmospheric work was weak - when in fact none of it
had ever run.

`PseudoPBR.Enabled` was `false` for a documented and good reason: it is the only
subsystem that patches the shader drawing the world, its two failures were a
sepia screen and missing terrain, and the comment said "flip it once someone has
looked". Someone has now looked, across several sessions, with debug views
photographed and a gold block identified through it. The condition the default
named has been met.

**Chosen.** Ten of the eleven atmospheric effects get non-zero defaults, and
`PseudoPBR.Enabled` becomes true. `CloudEdgeScattering` and `DappleInteraction`
stay at zero because both are documented FOUNDATION ONLY - shipping them on would
be shipping something that does not do what its name says.

**The numbers are chosen, not measured**, and this record exists partly so that
is not forgotten. They are conservative, they sit inside the ranges each feature
documents for itself, and the first job of the next session is to find the ones
that are wrong. What they are not is arbitrary in the way zero was arbitrary: any
value produces evidence and zero produces none.

**Deliberately unchanged: the three vegetation strengths.** `SunDapple` 0.35,
`FoliageTranslucency` 0.7 and `SunShafts` 0.45 were all tuned against
implementations that were later found broken - the dapple cancelled itself
against its own gate, transmission fired only when front-lit, and all three were
suppressed at sunset by a gate implemented as a dimmer. So the numbers carry no
information about the fixed code, and there is a real argument for raising them.

They are staying put anyway, because the fixes have never been seen. Change the
number and the fix at once and the next screenshot cannot say which one it is
showing. Atmosphere had nothing to confound - it was at zero. Vegetation has four
unmeasured fixes in flight, and they get measured first.

**The guard, which matters more than the numbers.** I14 requires that a subsystem
enabled by default is not inert by default, read from the subsystem's own
activity predicate rather than from a heuristic. The obvious heuristic - "at least
one float is non-zero" - passes on the exact configuration that shipped:
`WeatherTint` was 0.6 and `GodrayQuality` was 1.0, both non-zero, neither
contributing anything alone. A tint for an extinction of zero and a quality for a
feature of zero. `AtmosphereConfig.WantsShader` already answered the question
correctly and nothing was reading it.

`AtmosphereSubsystem` also says so at runtime now, at warning level, naming it as
a configuration state rather than a failure - because a player who reads
"enabled" and sees nothing deserves the line that connects them.

**Status.** L2. The defaults have not been validated as a look in game. The
current mutation table records 52 caught mutations.

---

## D45. The terrain shaders did not compile, and nothing green noticed

Found by running `tools/verifypatches` after pulling 46 commits of other work.
`chunkopaque.fsh` failed **48 of 48** define combinations and `chunktopsoil.fsh`
**24 of 24**:

```
ERROR: 0:3628: 'vvLeafBacklightSource' : no matching overloaded function found
```

`vvCanopyShaft` calls `vvLeafBacklightSource` about a thousand lines above the
definition. GLSL has no forward references, so that message is what a compiler
says when it has not seen the name yet. The world would not have rendered.

**Nothing caught it.** `dotnet build` compiles C# and never opens a shader. The
smoke suite was green at 1116 checks. `tools/verifypatches` is the only tool that
compiles GLSL, and it needs glslang installed plus the game's own dumped shaders
present, and takes minutes - which makes it the one tool most likely to be
skipped before a push. That is not a criticism of whoever pushed it; it is a hole
in the process, and the fix belongs in the fast suite rather than in a habit.

**Chosen, three parts.**

**One prototype**, immediately above `vvCanopyShaft` and the only forward
declaration in the file. Kept on a single line so it is one literal a mutation
can remove.

**A check that needs no GPU**: `GlslOrderChecks` reads every shipped snippet and
fails when a file calls a function it declares later in itself. Function order is
a property of the text, so it costs milliseconds and runs for everyone on every
run. It is deliberately same-file only - a snippet calling into another snippet
is injection order's business and is not knowable from one file - and the defect
that shipped was a file calling forward into itself.

**A declared exclusion.** With the compile error gone, two more surfaced:
`pbrterrainmaterial` and `pseudopbr` both inject
`uniform sampler2D vv_materialTex`, and applying both is "declared more than
once". They cannot both be live - `IsPatchGroupEnabled` keeps them apart during
the terrain material migration - but `verifypatches` applies every group it finds
on disk and knew nothing about the gating, so it reported a failure the game
cannot have. A false alarm is how a real one gets ignored.

`ShaderPatchGroups.MutuallyExclusive` now states the exclusion once. The tool
skips the impossible combination and says why; a smoke check DERIVES the conflict
rather than trusting the list, requiring every pair of groups that shares a
uniform on a shared target to be declared exclusive, and requiring the runtime
flags to honour it. Finishing the migration by flipping
`TerrainShaderPatchesEnabled` back on now fails a test instead of a world.

**Status.** Terrain shaders compile again: 38 ok, up from 36, with the two
impossible combinations skipped. The one remaining `verifypatches` failure is the
long-standing contaminated `final.fsh` reference dump, which is a stale file
rather than a code defect.

---

## D46. An invariant that cannot be evaluated must fail, not crash

Found while repairing six stale or missed mutation rows.

Mutating the foliage transmission sign no longer failed `I2`. It did something
worse: `I2`'s `try` wrapped only the EXTRACTION of the expression, not the
arithmetic over it, so an expression the evaluator could not parse threw straight
out of `SceneInvariantChecks.Run`. **Every check after I2 - several hundred of
them - silently never ran.** The suite exited non-zero, so the mutation harness
saw a failure and moved on; it was the wrong failure, and the coverage loss was
invisible.

The proximate cause was a hardcoded string substitution. `I2` did
`.Replace("vvSafeNormalize(l + n * distortion, l)", "normalize(...)")` to get an
expression the evaluator understood. That works on exactly one spelling: change a
single character in the shader and the replacement stops matching, the original
call reaches the parser, and it throws. An invariant whose reading of the source
depends on the source not changing is not reading the source.

**Chosen.** `GlslEval` now knows `vvSafeNormalize` as a builtin, with the
shader's own semantics - unit-length the first argument, fall back to the second
when it has no length - so the brittle replacement is gone and the arithmetic
reads whatever ships. And every invariant runs inside a guard that turns a throw
into a failed check naming the invariant, because an invariant that cannot
evaluate the shipped source HAS failed, and reporting it as one keeps the rest of
the sweep alive to report its own.

**The other five rows.** Two literals had gone stale against refactored code and
matched nothing, which the harness reports rather than passing over. Two named
the wrong check in their expectation, so a guard that fired correctly was scored
as a miss. One could not compile at all. All repaired against the current source.

**`I9` was weakened by its own looseness**, separately: it searched for the string
`Event.ReloadShader` anywhere in a subsystem folder, and an UNSUBSCRIBE satisfied
it. Removing the `+=` while leaving the matching `-=` kept the check green -
which is precisely the defect it exists for. It now requires an actual `+=`.

**Status.** 55 mutations, 55 caught, 0 missed - up from 49 caught with 6 holes.

## D47. The Tuning Studio shows what the config holds and what the build runs

Found reading the studio against the 1.22 API rather than in game, so every
point below is a code fact; none has been seen on screen yet.

**Stale rows.** The value text, a toggle's ON/OFF and the per-row Reset were
built from the value at compose time, and slider/toggle callbacks suppressed the
refresh and never touched them. The thumb moved; the number and Reset did not.
Every row now keeps the keys of its widgets, and after each edit the dialog
re-reads the config - after the controller's clamp - and updates thumb, value,
toggle, dropdown and Reset together. A slider never recomposes, so the composer
outlives the drag.

**Refresh suppression.** `_ignoreNextConfigRefresh` was set BEFORE the write,
and the controller sends no notification for a no-op or clamped-away change, so
the flag stayed armed and swallowed the next real ConfigLib or disk reload.
`ConfigManager.ConfigChanged` is raised synchronously, so the dialog now counts
its own controller calls (`_localEditDepth`, closed in a `finally`) and ignores
notifications only while one is running. An external change with the same rows
updates them in place; one that changes which rows exist recomposes.

**Scrolling, three faults.**
1. `GuiComposer.OnMouseWheel` offers the wheel to every interactive element
   regardless of the pointer, so the scrollbar scrolled from anywhere and then
   the dialog scrolled again, and a slider under the pointer - even clipped out
   of view - took the wheel as a value change. The dialog no longer calls the
   native path: an open dropdown owns the wheel; inside the viewport or over
   its scrollbar the wheel scrolls; elsewhere in the dialog it is consumed.
2. `GuiElementScrollbar.SetScrollbarPosition` takes HANDLE PIXELS and then
   reports a scroll of 0 to the callback; it was being given content units.
   Combined with `SetHeights` echoing the old handle position, the list snapped
   back and per-tab positions were lost on recompose. The handle is now set
   through `CurrentYPosition` (content units) with the echo ignored.
3. Static elements are painted once into the composer's static texture, and
   the clip is a scissor applied only at interactive render time. Labels,
   descriptions and section headings inside the scroll child therefore neither
   moved with it nor clipped. They are dynamic text now; `tools/smoketest`
   fails if a static element appears in row code.

**Availability.** The registry exposes every config property, but with the
legacy terrain gate off nothing reads foliage translucency, sun dapple, sun
shafts, canopy scale, wood grain, emissive glow, scene reflections or
reflection strength, and the two atmosphere FOUNDATION ONLY sliders do nothing
by declaration. `SettingAvailabilityRules` separates **unavailable in this
build** (hidden; disabled with the reason under Advanced), **parent off** (no
control; "Turn on X") and **active only in some scenes** (a working control
with a note on where to look). The build facts come from the runtime gates. The
smoke check derives the rest independently: it maps each PseudoPBR property to
its uniform through the binder, traces which snippet functions the active patch
files actually reach, and fails if a setting nothing reachable reads is shown as
working, or a read one is hidden. Emissive strength, warmth and flicker survive
that trace through the entity and particle programs and stay as conditional
controls; emissive glow does not, because only the legacy terrain patch calls
`vvEmissiveGlow`.

**First in-game run (same day): a crash and a half-fix.** The wheel crashed the
game with a NullReferenceException in `ElementBounds.PointInside`: the
scrollbar's bounds were remembered even when the list fit and no scrollbar was
added, so they had no parent. They are now kept only once the scrollbar exists,
and the hit test refuses an unattached bounds. The screenshot also showed that
point 3 above was only half the story: slider tracks, switch backgrounds and
dropdown boxes are the static parts of interactive elements, and they too were
baked into the composer texture at their compose-time positions - tracks floated
over the title bar and a dropdown box was drawn outside the dialog. Rows now go
into a native `GuiElementContainer`, which paints its children's static parts
into its own texture and draws it at its current bounds inside the clip, the way
vanilla scroll lists do. Row widgets are held by reference, since the container's
children are not in the composer's keyed table.

**Audit follow-up (same day).** The disabled-systems audit found three more
controls presented as live: rain ripples and the shelter threshold (on terrain
only `pseudopbr.glsl` reads `vv_weatherRipples` and `vv_weatherRainCover`), and
both godray sliders (`vvAtmosGodrayLevel` is called only from atmosphere debug
view 9). All four are now unavailable. The reachability trace no longer walks
through functions named `*Debug*`, since a value read only on the way to a
diagnostic is still dead in play, and it now traces those four settings through
a stated code-to-uniform table. Separately, `ReflectionsSubsystem` no longer
starts the scene capture while the legacy terrain gate is off: nothing else
samples it, so it was the most expensive setting in the mod producing no pixel.

**Not done here.** The panel is still the centred 860 x 610 layout; rows got a
wider slider, a fixed Reset slot and a real Advanced switch, but the side-anchored
narrow panel is a separate change. Hover help stays off (the existing check and
its reason stand). Weather and atmosphere rows are not traced to their shaders,
only gated on their parents. (The scene capture
running unsampled was fixed afterwards; see the audit follow-up above.)



## D48. Reflections come back in stages, starting with the bridge

The legacy reflection stack was never seen working: debug view 41, the proof
that the captured frame reaching terrain contains the world, was still open
when the terrain gate closed in August, and the probable cause of that
corruption was auxiliary samplers rebound per draw without restoring the
active texture unit.

So the modular path restores it in stages, each its own patch group with its
own switch. **Stage one** (`pbrterrainreflect`) is only the bridge: the capture
sampler on unit 13, declared directly above `main` (below every vanilla
sampler), its uniforms, and debug views 41 and 42. The capture is rebound per
terrain draw inside the same active-texture save/restore the material pages
use, and only into a program that declares it. The world volume and canopy
textures stay unbound. The group asserts the material group's debug entry
point and is declared in `ShaderPatchGroups.Requires`, so it fails closed
without the material path, and `tools/verifypatches` checks that it does.

The texture slot report read from a 1.22.7 client before this landed showed
vanilla terrain on units 0..6, the material atlases on 15 and 14, and no
shared unit. **Stage two** - the march and the material response - waits until
view 41 has been seen to show the world.

Supersedes nothing; extends D13 (reflection through the existing BRDF), which
still describes how stage two must consume the hit.

## D49. Plant lighting on the modular path reads the shadow map, not `b`

The first foliage pass (f72bc9d) took sun visibility from vanilla's
`getBrightnessFromShadowMap()` result `b`, rescaled as
`1 - (1 - b) / shadowIntensity`. That rescale assumed a fully shadowed `b` of
`1 - shadowIntensity`. The real function subtracts `shadowIntensity * 0.5`, and
then adds `blockBrightness`, so a leaf in full shade read as half lit and a
torch under a tree read as sunlight. The smoke check passed because its fixture
used the same wrong assumption about where the floor sits.

`vvTerrainSunVisibility` is now the legacy patch's geometric visibility: the
same 3x3 comparison taps vanilla takes, cascade-weighted, without
`shadowIntensity` and without block light, and 1 with shadows off. Transmission,
front scatter, the cuticle highlight and the canopy gate all read it. It costs
up to 18 more shadow taps per flora fragment; dapple on opaque terrain takes them
only after its cheap early-outs (slider, receiver, distance, sun presence). Not
measured.

Canopy dapple comes across in the same pass with the legacy constants. Its one
deliberate change is on plants: the legacy branch tinted the shade by the leaf's
own pigment, and this one uses the same green tint as the ground, so one
function applies the canopy everywhere.

Supersedes the sun-visibility choice in the f72bc9d foliage commit; extends D7
(dapple reads vanilla's shadow map).

## D50. Sunflecks inside a solid crown: a real gate, an invented pattern

**Problem.** Seen in game on 2026-11-23: the dapple mask (view 15) was almost
entirely white on a forest floor that was plainly in tree shade. D7 measures
canopy from gaps in vanilla's shadow map, and under a solid crown at the
player's shadow resolution there are none: one flat shadow, nothing to count.
The player asked for broken, sunfleck-filled shade like the reference photos.

**Chosen.** Two halves, deliberately unequal in authority.

- **The gate is game data.** `CanopyMap` (in `EnvironmentTracker`) reads the
  world's leaf blocks in a 32 x 32 grid of 2x2 cells around the player and
  uploads density and lowest-leaf height as a 128-vec4 uniform array: no
  sampler, so vanilla's texture units cannot move. A fragment is canopy-shaded
  only if the shadow map says the sun is blocked AND the sun ray from it passes
  through leaves on that map. A wall, roof or cliff shadow has no leaves on its
  ray and gets nothing.
- **The pattern is invented.** DATA GAP: the game does not resolve gaps inside
  a crown, so fleck positions are a stable hash on the canopy plane. They are
  projected along the real sun direction (slide as the sun moves, stretch by
  1/sin(elevation)), sized by canopy height, and sway with the wind clock.

A fleck only restores the shadow vanilla applied (`vvTerrainSunRelight`),
exactly to vanilla's sunlit level and never past it, so it cannot reach bloom
or hide anything. The dapple's shade now also accepts leaf-map evidence
(`VV_TERRAIN_LEAF_SHADE` 0.7) so a solid crown deepens and greens like broken
shade does.

**Rejected.** Re-reading vanilla's shadow map at a finer radius (the gaps are
not in it); a canopy sampler (two world-render losses from samplers in
`chunkopaque`, see CLAUDE.md); reviving the deleted procedural field of D7 with
its old `vv_sunExposure` gate, which measured ~1 under every crown.

**Cost.** CPU: about 50 block reads per column, 128 columns per 0.1 s tick,
only while Sun dapple or Sunflecks is above zero. GPU: up to ten uniform-array
reads and nine hashes per shaded receiver fragment within 48 blocks. Not
measured.

**Limits.** Daytime only; moonlight flecks wait on checking how the game's
night shadows behave. The map covers 64 x 64 blocks around the player and
refreshes in about a second, so a tree felled in view keeps its flecks briefly.

Partly supersedes D7: D7's count stays the gate for broken shade; this adds the
leaf map for solid shade and a pattern D7 had removed, with the gate it lacked.

## D51. Sunfleck shapes: leaf-bitten gaps, stepped at the block texel

**Problem.** Seen in game on 2026-11-23, after D50 shipped: the player called
the flecks a good foundation, but too mathematically soft and regular for the
look they want, and some were cut by long straight edges. The reference photos
show irregular, broken patches of varied size, not a field of ovals.

**Chosen.**

- **Shape.** A gap is one to three overlapping lobes of varied size, and a
  small gap is dimmer, as a pinhole smaller than the sun's disc is. Two
  pointed leaf lenses per cell bite into the gaps, which turns ovals into the
  concave, broken shapes of light through a crown. A coarse value noise
  (8.192-block cells) bunches gaps where a crown is thin. Gaps and leaves sway
  on different wind phases.
- **Edge.** Evaluated once per block texel (1/16 block) along the receiving
  face and posterised to four levels, so a fleck has the stepped edge of the
  game's own textures rather than a smooth gradient nothing else in the world
  has. Plant cards snap on all three axes, block faces only along the face,
  because snapping across a face that lies on a texel boundary speckles it.
- **Projection.** From ONE canopy plane for the scene, `vv_canopyPlaneY` (the
  mean lowest leaf on the map + 1.5, eased over about two seconds), not from
  each cell's own lowest leaf.

**The straight cuts had three causes**, each now an interaction check with a
mutation: a lobe stretched by a low sun reached past the 3x3 cells searched
(the reach is now bounded against `VV_TERRAIN_FLECK_MIN_STRETCH`); the
per-cell projection height shifted the pattern at every 2-block cell edge; and
the leaf map itself stopped on 2-block lines, at its cells and at the
30-block edge of the march. The map is now blended across its four nearest
cells, eased out between 20 and 28 blocks, and flecks finish fading by 26.
The scan reads both diagonal columns of each cell, because one column missed
crowns that left that corner open and each miss was a square hole.

**Rejected.** Smoother and larger ovals (tried offline first: still read as a
regular field); inverting to "light wherever no leaf covers" (needs about four
leaves per cell for realistic cover, and loses the gap-size control); a
per-cell plane height (the cuts).

**Cost.** CPU: the scan reads twice the blocks D50 did (two columns per cell).
GPU: per shaded receiver within 26 blocks, 18 leaf tests and up to 27 lobe tests
over nine cells, plus four leaf-map reads per march step. Not measured.

**Limits.** The pattern is still invented (DATA GAP, as D50). One plane for the
scene means a forest of very mixed heights projects some flecks from the wrong
height; that shows as a pattern that slides slightly more or less with the sun
than it should, not as cuts. Moving between forests eases the plane, so flecks
drift for a couple of seconds.

Amends D50's pattern and its cost; D50's gate is unchanged.

**Amendment, same day (third pattern).** The second pattern was seen in game
and read as stains: too sparse, small and dim, a flat tan, random confetti on a
flat shade. Lobes alone cannot make pools, because the 3x3 search bounds a gap
to about half a block. So a third layer, POOLS: two octaves of value noise on
the fleck grid (2.048 and 1.024 blocks, both tiling the wrap), thresholded at a
level the coarse thin-crown noise lowers, so light runs together into patches a
few blocks wide under a thin crown and the leaves cut it up. The same noise
drives gap count and size, and a smooth WASH (`VV_TERRAIN_FLECK_WASH`, not
stepped, since contour bands would show) lifts thin-crown shade so the shade
varies. `VV_TERRAIN_FLECK_GAIN` 1.6 lets the default slider reach a full gap.
Lit patches go through `vvTerrainSunlitTint`: 1.2x saturation and a warm shift
at the same luminance, checked as an interaction, so the sunlit cap holds.
Cost: eight more hashes per shaded receiver. Not measured.

**Second amendment, same day (fourth pattern, with an audit).** The third
pattern was seen in game: the scale and warmth were right, but the pools read
as soft decals, some were rings, the tint was more vivid than open sun beside
it, there were no small specks, nothing tied the pools to the crown, and from
a flying camera at midday almost nothing showed.

- Pools take a hard one-texel edge (`step`, not a 0.06-wide ramp that
  posterised into a soft gradient), two brightness levels, and a third, finer
  octave for a lumpy outline. Leaf cuts are smaller (0.14 to 0.28 cell) so they
  cut edges rather than punching rings.
- The leaf profile is no longer clamped at its tips. Clamped, it had zero width
  past them and its soft edge drew a faint line along each leaf's axis to the
  edge of the search, through every pool (now an interaction check).
- `VV_TERRAIN_FLECK_SATURATION` is 1: warmth only, same luminance.
- Plants take the sun on either face with a falloff to about half edge-on.
- "Thin crown" is mostly game data now: 0.45 from fewer leaf blocks on the
  leaf map along the sun ray, 0.5 from vanilla's shadow already broken here
  (the D7 ring count, measured once per fragment and shared with the shade),
  0.35 from noise. A constant scatter of small faint gaps runs under all of it.
- The fleck distance is horizontal (the leaf map's measure, ending at 26), plus
  a straight-line limit of 40 that finishes before the resolve's 44-block exit.
- The leaf map searches 20 blocks either side of each column's rain-map height
  instead of a window around the player's Y, which missed the whole canopy
  from above. Where the rain map reads 0 it falls back to the old window. Up
  as well as down, in case a leaf lets rain through.
- Audit: canopy shade now fades with overcast as the flecks do. It is a shadow
  of the direct sun; without this an overcast forest took the weather's
  darkening and a full canopy shade on top of it, the stacking VisualBudget
  exists to prevent. The shade stays out of the budget itself: it is local and
  bounded by its cap, like a shadow, not a scene-wide wash.

Cost: about 70 hashes, up to 40 uniform-array reads and the 12 shadow taps the
shade already paid, per shadowed receiver within 26 blocks horizontally; the
scan reads up to 80 blocks per cell. Not measured.

**Third amendment, 2026-11-23 (in-game audit).** The fourth pattern was played
in the player's world at noon and with a low sun.

- Under a low sun the flecks came out as squares and stripes. The leaf map was
  read with a hard per-cell height test, and the march returned at the first
  cell with any leaves, so each 2-block cell switched on or off as a block. The
  read is now continuous: smoothstepped bilinear weights, a soft crown height
  band, a soft "above the floor" test, the strongest density along the whole
  march rather than the first, and a fade before the march's reach. Smoke
  checked for continuity, with mutations for both causes.
- Pools crowded the rim of vanilla's own shadow, because broken shade from the
  shadow map carried most of the placement. Thin crown from the leaf map now
  leads it.
- Debug view 64 drew flecks on the leaves themselves. It now shows only
  surfaces that can receive canopy light.

Seen in game before the fix, and again after it with a low sun: irregular pools with texel edges, no squares.

**Fourth amendment, 2026-11-23 (fifth pattern, after an art-director audit).**
The audit of the fourth pattern's screenshots asked for eighteen changes; all
are in this pattern. Pools are now metaball kernels (`max(0, 1 - x^2)^2` summed
over a 5x5 cell search, thresholded at 0.5) rather than a noise outline, because
a kernel centred on a cell can be stretched along the sun azimuth where a noise
domain anchored to the world would slide as the sun moves. Gap sizes are cubed
uniform draws, so there are many small pools and a few large ones; the nearer
3x3 cells add leaf cuts and faint specks. A half-bright rim
(`soft = rise * 0.012`, clamped 0.04..0.3) stands in for the penumbra a higher
crown throws. Flutter scales with `vv_sceneWind`, so calm air holds still. The
wash is gone: lit patches take a hue (warm by day, golden near the horizon,
cool by moonlight) at preserved luminance, and the relight cap still bounds
them at vanilla's sunlit level. A faint warm bounce (at most 0.07) lifts the
ground round a pool. Plants take double gain. Canopy shade is graded by crown
thickness (0.6 under a thin crown). A clear-sky gate closes flecks between 45%
and 65% cover, read from the fused cover of D53. Moonlight flecks reuse the
same field at most 0.45 of a sunfleck, behind their own slider. All of it is
invented pattern on real placement (DATA GAP, as D50), checked by evaluating the
shipped GLSL, and not yet seen in game. Rejected: a second leaf-map read per
kernel (the 5x5 loop already costs 25 hashes per fragment, not measured).

**Fifth amendment, same day (sixth pattern).** The player's v5 screenshot read
as evenly spread coins of one size, with notches, soft edges and flat shade.
Gaps now open only where a cluster mask (the existing 8.192-block noise) is
high, so pools gather in groups with plain shade between them, and grow up to
1.4x inside a group until they merge. The threshold wanders with a 0.512-block
lobe noise and a per-texel jitter (`VV_TERRAIN_FLECK_RAGGED` 0.36), so the
outline is ragged and stepped rather than a circle. The half-lit rim and the
leaf bites are removed: on the ground they read as blur and as notches. Canopy
shade now grades from 0.3 under a thin crown to 1 under a closed one. Radii
were reduced so the merged reach (0.546 cell / min stretch) still fits the 5x5
search. Seen in game 2026-11-23: ragged, texel-stepped pools, but still
spread fairly evenly.

**Sixth amendment, same day (seventh pattern, v7).** The player's reference
photographs (two tree-lined roads, an autumn road, three backlit forests)
were compared with the v5 and v6 screenshots. Under a real crown light and
shade weave into ONE pattern whose lit share follows the crown: about half
under a thin crown with the shade broken into islands, shade with small holes
under a closed one, a linked network between, streaked along a low sun. v6
lit 3-5% of the floor everywhere as separate pools on solid shade, and its
tint read as yellow discs. Every pattern so far placed positive features and
tuned their number; none could produce a network, because a network is what a
threshold on a continuous field does at half coverage.

Decision: the pattern is one continuous field (three octaves of value noise,
1 / 0.5 / 0.25 block, under a 0.4-block warp that hides the lattice's axes),
cut at `1 - coverage`, where coverage runs 0.04..0.55 with how thin the crown
is (leaf map along the sun ray, vanilla's broken shadow, a regional noise).
The summed noise is expanded (`VV_TERRAIN_FLECK_CONTRAST`) so the lit share
equals the coverage asked for; a smoke check evaluates the shipped cut over a
stand-in of the sum and fails if they part by more than 0.07. The coarse
octaves are averaged along the sun azimuth over a reach that grows as the sun
drops, which streaks the field without breaking the 4096-block tiling a
rotated lattice would. One hard level; colour is plain sunlight. The cell
search, kernels, cluster mask, specks and leaf bites are removed. Still an
invented pattern on real placement (DATA GAP, as D50). Rejected: rotating the
noise lattice to the sun (does not tile the world wrap); a stretched
coordinate anchored near the player (the pattern jumps when the anchor moves).
Seen in game the same day: linked shapes at noon, streaks toward a low sun.

## D55. Shaft sources line the holes in the leaves

**Context.** Vanilla's terrain writes zero to the god-ray channel; its beams
come from the sky seen through the holes cut in leaf textures, which is why
real shafts read as a comb of thin rays. v5 and v6 marked whole leaf faces at
thin leaf-map cells. Broad bright patches in that channel fill the dark gaps
between rays, and in game at 16:30 the result was one soft wedge.

**Decision.** A leaf stays at zero in the channel except on texels that line a
hole: `vvTerrainLeafHoleRim` reads the four neighbouring texels of vanilla's
own `terrainTex` at vanilla's `uv` (base level) and counts the cut-away ones.
The other gates stay (thin leaf-map cell, facing the sun, humid air, clear
sky, cap 0.35) and the test fades out by 32 blocks, where a texel is under a
pixel. It uses no sampler of ours, so no texture unit moves. It depends on the
game's see-through leaves; with opaque leaves there are no holes and no
sources, as in vanilla. Rejected: patching the sky shader to brighten the
gaps (the sky is not patched, see the Weather README); darkening leaves below
zero (the channel is already zero there). Seen in game: sources sit on hole edges and the soft wedge is gone; no distinct beams yet.

**Amendment (v8).** Sources everywhere along a thin crown blurred into a haze.
A beam is the air between a gap and the fleck it lands on, so a leaf now marks
the channel only where the sunfleck field is open at the canopy-plane point
its sun ray crosses - the same `src` the flecks below are projected from. Each
beam lands on a lit patch, and beams come in the flecks' groups. Placement
reads the leaf map's cell density (vanilla's broken shadow is unknown at a
leaf), so beams are a subset of patches. Dust motes thicken with the same
humid-air term the beams use. Not yet seen in game.

**Amendment (v8): the relight includes vanilla's normal shade.** Vanilla lights
a fragment at min(shadow, nb), so a sunlit plant sits at its per-vertex nb.
The relight divided by the shadow alone and lifted plants with low nb past
their sunlit level (v7 screenshots: flecks brighter than open grass, read as
yellow). The call sites now pass nb in `vv_vanillaNormalShade` and the relight
restores exactly nb. Canopy shade also deepens toward a low sun
(`VV_TERRAIN_DAPPLE_LOW_SUN` 1.6, under the existing caps), so a golden-hour
floor is dark enough for flecks and beams to read.

**Amendment (v9): canopy shade is skylit, not green.** (Belongs to D51's
pattern; recorded here beside the v8 relight it completes.) The v8 screenshot
at 13:00 showed a linked, organic fleck field whose lit patches still read
olive-yellow over murky green shade, while the session log showed a near
neutral grade. Vanilla's shadow halves the sunlit colour without changing its
hue, and `vvTerrainApplyCanopy` pushed that green (`VV_TERRAIN_DAPPLE_GREEN`
0.055). A fleck was then the shade's own hue made brighter, which reads as
paint, and on a yellow autumn albedo as yellow paint. Shade under a canopy is
lit by the sky: cooler and less saturated than the sunlit floor, which is what
lets a fleck read as light. The shaded fraction now moves toward its own
luminance by `canopy * VV_TERRAIN_DAPPLE_MUTE` (0.8, at most 0.5) and is cooled
by `canopy * VV_TERRAIN_DAPPLE_SKY` (0.10), then clamped to vanilla's colour so
no channel is brightened. Both vanish with the canopy term, so the open floor,
an overcast forest and a zero slider are unchanged. The legacy `pseudopbr`
path keeps its green; it is disabled. Interaction-checked on an autumn-olive
colour and a blue-free yellow, four mutations. Seen in game: the shade reads
right.

**Amendment (v10): brightness variance, world-anchored cut, beams and bloom.**
(D51 eighth amendment, recorded here with the v8/v9 notes.) The v9 test found
three faults. Every spot was one flat brightness, so the field read as
camouflage blotches rather than dappled light. The spots jittered while
flying. No rays were visible.

*Brightness.* A gap's image is as bright as the gap is wide, so a patch that
only just clears the cut is a dim dapple (`VV_TERRAIN_FLECK_DIM` 0.3) and full
sun is reached `CORE` above the cut: 0.4 normally, 0.1 inside beam groups (a
4.096-block noise, 1000 cells per world wrap). The ramp is stepped into
`LEVELS` 3 flat tones, keeping the hard pixel-art edge; a soft edge is the v5
blur defect. Shafts land only over the bright patches.

*Jitter.* A hard cut turns any change in its inputs into popping edges. Three
inputs moved with the camera: vanilla's broken-shadow evidence (the shadow
map is fitted around the camera), the leaf-map density's edge fade (measured
from the camera), and the canopy plane, which eased toward every new mean as
cells entered the map. The cut now reads the density before the fade
(`vvTerrainCanopyOverhead` z); the fade stays on the gate, where it is a
smooth multiplier. The evidence no longer enters the field. The plane holds
until the mean moves `CanopyMap.PlaneHold` (3 blocks), then follows the
rounded mean. Accepted cost: the crown's thinness now comes from the leaf map
alone, which resolves 2-block cells, not individual leaf gaps.

*Beams.* Sources are the texels lining leaf holes, a sparse lace the god-ray
pass averages with dark leaf around it, so the v8 level never survived the
blur. A gain of 2.5 under a cap of 0.8 (`VV_TERRAIN_SHAFT_GAIN`,
`VV_TERRAIN_SHAFT_MAX`); a whole-face source would wash the forest out at
that level, a rim texel cannot. A dry noon keeps 0.6 of the humid strength.

*Bloom.* The player asked for a very subtle glow that scales with the shading.
`vvTerrainCanopyResolve` sets `vv_fleckGlow` = fleck x local x contrast x
`VV_TERRAIN_FLECK_GLOW` (0.08), contrast being how much darker the shade is
than the lit colour; both call sites add it to vanilla's glow channel through
`1 - fogAmount`. It rides the sunflecks slider rather than a new one, so zero
flecks means zero glow. No pass, no target, no sampler.

Interaction-checked (brightness distribution over the stand-in field, the
bloom's scaling, the beam landing, the plane hold), nine mutations. Not yet
seen in game.

## D53. Cloud cover: the cloud tiles outrank Ambient.BlendedCloudDensity

**Context.** In the 2026-11-23 in-game audit, a storm with every cloud tile
covered reported `Ambient.BlendedCloudDensity` as 0 throughout. Every consumer of
`EnvironmentState.CloudCover` therefore saw clear sky: sunflecks drew under the
storm and the overcast terms never engaged.

**Decision.** `CloudTileReader` already reads the array the clouds are drawn
from. It now computes the window's mean cover, and `WeatherSubsystem` reports it
to `EnvironmentTracker.ReportCloudField` after each read. The tracker takes
`max(ambient, tiles)` while the tile reading is under ten seconds old, and the
ambient figure alone otherwise, so a Weather subsystem switched off falls back
to the game's own figure rather than to a stale one. It logs once when the
tiles disagree with the ambient figure by more than 0.3.

**Consequences.** Weather writes a fact into the tracker, so data now flows
Weather -> tracker -> every subsystem. The tracker is still the one place the
mod asks the game: the tile array is game data the Weather subsystem already
holds, not a second query. The mean is over the reader's window around the
player, not the sky overhead. Not yet seen in game; cost is one pass over an
array already read.

**Amendment, same day (seen in game, superseded in part).** The tile mean
cannot tell weathers apart: it read 0.745 under the earlier storm and 0.745
again at noon under a 70% cirrocumulus sky with the sun casting shadows, where
it hid every sunfleck behind the clear-sky gate. The tiles say where cloud is,
not how much sun gets through. Cover is now `max(ambient, precipitation)`:
falling rain does not come from a clear sky, and a dry overcast without rain is
an accepted DATA GAP (flecks may draw under a dry grey sky). `ReportCloudField`
stays, and the tracker logs once when the tiles disagree with the cover by more
than 0.3, so a future reader can see the two numbers side by side.

## D54. Cloud shadows from tile contrast, not tile value

**Context.** `CloudTileReader` normalises tile thickness and opaqueness against
the thickest tile recently seen, because their absolute units are unknown. A
sky whose tiles are all alike therefore reads as solid cloud. In game on
2026-11-23, a clear noon with sun shadows read every tile near 0.86 and the
cloud shadows (the player's strength 1) put the whole world in shade; turning
Weather response off brought the sun back.

**Decision.** After the Beer-Lambert step, each tile keeps only the part that
stands out from the window's mean: `t * clamp((t - mean) / max(peak - mean,
0.3), 0, 1)`. A uniform field - clear, hazy or a solid storm deck - casts no
cloud shadow; a broken sky keeps its clouds at full value. A shadow is an
edge, and a relative measure can only say where the edges are.

**Consequences.** Thin cloud that evenly dims the sun casts no mod shadow; that
is left to vanilla's lighting (DATA GAP). A sky mostly covered with a few
thicker tiles casts shadow only under those tiles. Checked by calling
`ApplyFieldContrast` from the smoke suite, with two mutations. Not yet seen in
game.

## D52. A cloud shadow is a sun shadow: floored at vanilla's, never multiplied

**Context.** `getBrightnessFromShadowMap` returned vanilla's brightness times
`1 - coverage * strength`. Nothing bounded the product. In the 2026-11-23 audit,
with the player's cloud shadow slider at 1 and a storm reporting 87% of tiles
covered, a forest at noon kept about 13% of its light and was black. Switching
Weather response off restored it, and switching Surface materials off did not.
The multiply also dimmed block light, which vanilla adds back after its sun
shadow and which no cloud can reach.

**Decision.** A cloud occludes the sun as a tree does, so it is built the way
vanilla builds its own shadowed brightness. That is
`1 - occlusion * shadowIntensity * depth + blockBrightness`, where depth is 1
with both shadow maps and 0.5 with one. It is then taken as a `min` with
vanilla's value, never multiplied. A full cloud is exactly as dark as full sun
shade, shade already present loses nothing more, and torchlight is untouched.
`shadowIntensity` is vanilla's own, so the shadow weakens wherever the game
weakens its sun shadows.

**Consequences.** Cloud shadows are shallower than before at high strength, and
cannot exceed what a tree's shade looks like. Under heavy overcast the whole
world can sit at full-shade level; that is vanilla's shade, not darker. The
invariant I20 and three mutations pin the floor, the min and the block light.
Seen in game after the fix: the same storm at noon with every tile covered
leaves the forest dim but readable. Torchlight under cloud was not checked in
game.

## D56. Sun shafts are light in the air, marched in the terrain shader

**Context.** Every shaft version so far fed vanilla's god-ray channel. That
pass blurs the channel radially toward the sun's position ON SCREEN, so it
can only draw rays while the sun is on or near the screen. In game at v10 the
rays showed only at extreme sun angles - a low sun straight ahead - and not at
all at midday, when the sun is overhead and out of view. The player's
reference photographs show the opposite: slanted beams seen from the side,
across a forest, at any sun height. Those are light scattered toward the eye
by the air inside each beam. No amount of tuning the god-ray source makes a
screen-space radial blur draw a beam whose source is off screen.

**Decision.** `vvTerrainBeamInscatter` integrates the scattered light along
the view ray between the camera and each terrain fragment, in the terrain
shader itself (twelve samples over at most 26 blocks, offset per pixel by
interleaved gradient noise). A point in the air is in a beam exactly when the
sun ray through it meets the canopy plane where the fleck field is BRIGHT -
the same field, projection and bright-patch test as the flecks (v10) and the
leaf-rim sources (D55), so every beam starts at a gap in the crown and ends on
a bright fleck, and there is no second pattern to drift out of step. Air above
the canopy plane, outside the leaf map's reach or under no leaves takes none.
Forward scattering: 0.45 of the full strength seen from the side, 1 looking
into the sun. Scaled by sun presence, the clear-sky gate, humid air (0.6 dry
to 1 wet, at a low sun or in fog) and the visibility dampen, and by the
existing sun shafts slider (the default 0.45 gives the stated strength); zero
means vanilla. Capped at 0.3 of the sun's colour, which warms toward golden
hour. Added to the colour at both terrain call sites through `1 - fogAmount`.
Debug view 68 shows it.

The god-ray route is kept: looking toward the sun it still draws the comb of
fine rays through the leaf holes, which a twelve-sample march cannot resolve.

**Consequences.** Beams in front of open sky are not drawn, because the sky is
not a terrain fragment (DATA GAP). Under a moving camera the per-pixel sample
offset can show as fine grain inside a beam. Cost: up to twelve fleck-field
evaluations per terrain fragment where leaves are overhead, none elsewhere;
not measured. No pass, no render target, no sampler, no uniform.
Interaction-checked (shared projection and bright-patch test, side and
toward-sun strength, the cap, slider zero, both call sites), six mutations.
Not yet seen in game.

**Amendment (v12): cost and default strength.** In game at v11 the beams
looked right at the slider's maximum and were hard to see at the default, and
looking at them took the game from 60 to about 20 fps. The march evaluated
the whole fleck field at each of twelve steps - about eleven noise reads,
some 500 hashes, per terrain fragment - and leaves, alpha-tested and layered,
run the fragment shader once per surviving layer. Now eight steps over 20
blocks, each one leaf-map read and at most two noise reads through
`vvTerrainBeamDensity`: the field's beam-group noise and its coarse octave,
same grids and keys, cut high, so a beam still stands on the heart of a
bright fleck while the field's warp, streak taps and fine octave (fractions
of a block on the outline) are left out. Outside beam groups a step costs one
read. About an eighth of v11's work per fragment; not measured. Scatter
raised from 0.06 to 0.2 per block of full beam, cap 0.32, so the default
reads without maxing the slider. Three more mutations.

**Amendment (v13): beams only in shaded air.** Ten v12 screenshots at many
angles and times: under a maple at midday the floor and the beams read well,
but elsewhere the beams were inconsistent. They stood over open scrub in full
sun. At a low sun they ran through whole crowns as near-horizontal slabs, and
a fine radial hatching covered the view near the sun. Cost was about 15 fps
while looking at beams. Causes: the march asked only whether the canopy plane
above a point had a gap, never whether the point itself was in shade or
whether another crown stood between it and the gap; and the leaf-rim god-ray
sources, raised to gain 2.5 under a 0.8 cap in v10, were blurred into
hatching. A point now carries a beam only when its own column has a crown
(`VV_TERRAIN_BEAM_CROWN` 0.3) and it sits below that crown's lowest leaf,
and when the sun ray halfway up to the canopy plane does not pass through
another crown (`VV_TERRAIN_BEAM_BLOCK` 0.45, over the crown's lowest leaf to
its estimated top). The march returns before any sample when no crown stands
at the camera, the middle or the end of the marched span, which also makes
open country free. The rim sources are back to gain 1 under a cap of 0.4.
Accepted limits: the leaf map resolves 2-block cells, so shade and blocking
are cell-coarse; the halfway test is one read and can miss a thin crown
elsewhere on the ray. Three mutations. Not yet seen in game; cost not
measured.

The overall yellow cast in the same screenshots is not the colour grade: the
session log shows it neutral (temperature 0.01, tint 1/1/1). Not chased here.

## D57. The v14 forest pass builds on what exists, and says what is invented

**Context.** The player asked for eleven forest and weather effects in one
pass: leaf translucency, backlit rim light, wet foliage with drips, forest
haze, ground mist, pollen and fireflies, travelling gusts, flutter linked to
the flecks, plant base shade, damp and moss, and a seasonal light tint - on
top of the existing foliage lighting and the work-in-progress atmosphere, and
without overlapping either. The audit found four of them already partly built:
leaf translucency (`vvTerrainFoliageTransmission`), wet foliage (wet-leaf
darkening and the wet cuticle), ground haze (`AmbientBridge`'s height fog)
and the fleck shimmer (`vvTerrainFleckShimmer`).

**Decision.** Extend those four rather than add a second copy, and give each
new effect its own slider and zero-means-vanilla uniform:

- Translucency keeps its lobe and gains a wide-cone diffuse share; the
  validation also found the root-to-tip gradient was dead for every
  single-block plant (`windData / 7` is 0) and fixed it from vanilla's own bend
  height.
- Rim light and the shaft sources read the hole rim once (`vvTerrainLeafRim`).
- Wet foliage is unchanged; drips are new particles.
- Ground mist rides the ground haze's one vanilla plane (`AmbientBridge.Combine`)
  instead of a shader fog, because vanilla's height fog already reaches every
  program including the sky and water, and a second plane does not exist.
- Gusts feed the existing fleck shimmer rather than a second flutter.
- Forest haze is atmosphere feature 12, a medium summed into the existing
  extinction under the shared ceiling, with only its own share tinted.
- Seasonal light is one more GradeStack influence; autumn deliberately does
  not warm, because the game's colormaps already paint it.
- Plant base shade and damp are new multipliers that can only darken.

Each new value that the game does not supply is labelled where it is made:
gust placement is invented (FOUNDATION ONLY), damp and moss placement is
inferred from shelter and rainfall (DATA GAP), hollows are the terrain read
near the player (DATA GAP), and pollen and fireflies do not exist in the
game (DATA GAP). Whether there is wind, which way, how wet, how warm, which
season and where the leaves and plants are, are all the game's.

**Consequences.** Nine new sliders (gusts, plant base shade, damp, drips,
pollen, fireflies, forest haze, ground mist, season) and four debug views
(69 gusts, 70 damp, 71 plant base, 72 rim). New per-fragment cost is small
and gated: two noise reads for gusts on plants and in the flecks, four
texture reads for the rim on backlit leaves within 24 blocks (already paid
by the shafts there), one leaf-map and one noise read for damp, a few
multiplies for forest haze; none of it measured. No new sampler, pass,
target or texture unit. The leaf map now also scans while forest haze, damp
or drips are on. Gusts do not bend anything: vanilla's vertex sway is left
alone, which keeps the vertex shader unpatched. One mist plane means the
mist lies in one hollow, the player's. Nothing here has been seen in game.

## D58. Falling leaves are the game's particles, coloured by the tree, gusted by the shader's field

**Context.** v15 asked for leaves falling from trees, using the source tree's
leaf texture, scaling with wind, time of day and season, from a leaf now and
then on a still spring day to a flurry in an autumn gust. Vanilla's forest
trees drop nothing: `BlockLeaves` carries only the `RainDrip` behaviour, and
the one leaf shedding in the game is the fruit trees' own block entity.

**Decision.** A fourth kind in `ForestLifeSpawner`, not a new system. Where:
the leaf map's crown underside, the same source the drips use. Colour:
`Block.GetRandomColor(capi, pos, UP)`, which is what vanilla's breaking
particles call - a texel of the block's own texture with the block's climate
and season colour maps applied at that position - so a maple's leaves fall in
the maple's colour for that place and week. Motion: the game's particle wind
(`WindAffected`) plus a downwind push, with vanilla's random velocity change for
flutter. Amount: season shed x local wind x a day bias, per try, under a cap.

The local wind is the game's plus the gust at that spot, from `GustField`: a
CPU port of the shader's `vvTerrainGust`, not a second field. A smoke check
reads its constants and integer hash back out of the shipped GLSL, so the
flurry stays under the gust the player sees in the grass.

**Rejected.** A textured leaf (its outline, not just a colour): the game's
particle API draws solid quads and cubes only, and a textured particle needs
its own renderer, mesh and shader - a new GL path for a few hundred quads.
Recorded as possible later work, not done. A shed driven by a vanilla
leaf-fall field: there is none.

**Consequences.** One slider (`pbr_leaves`), one new pure file
(`GustField.cs`), three checks and nine mutations. Up to 60 leaf-map lookups
a second and 160 particles alive at full slider in a gale; not measured.
DATA GAP: the shape of the shedding year (floor 0.1, autumn 1.0, winter 0.4)
is this mod's choice; the season, wind, daylight, crowns and colours are the
game's. Not seen in game.

## D59. Leaf fall follows the date, not the season depth; our fireflies replace the game's

**Context.** First in-game test of D58 on 2 September with wind: no leaves
seen, only a few coloured specks. Two causes. The shed was built on the
season depths, which are all 0 at a handover - exactly when the crowns have
just turned yellow - so it ran at its spring trickle. And 0.14 to 0.24 quads
draw as a few pixels: the game's quad sizes are not blocks. The same test
noted that the game already has fireflies, which D57 had recorded as absent.

**Decision.** `EnvironmentState.LeafFall`, from the same calendar reading as
the depths but continuous across every handover (late summer 0 to 0.25,
autumn 0.25 to 1, winter 1 to 0, spring 0), drives the shed; leaves draw at
0.35 to 0.55. Vanilla's fireflies are one `SimpleParticleProperties`
(`ModSystemAmbientParticles.fireflyParticles`) whose quantity is set once at
start; `VanillaFireflies` zeroes it while ours are on and puts it back when
they are off, so the two never draw together and our slider at 0 is vanilla.
Ours become swarms that flash together in waves, each flash a core in a halo,
in place of single specks - a different look, not a second copy.

**Rejected.** A Harmony patch on the ambient spawn tick: two float writes on a
field the game never rewrites do the same with nothing to unpatch.

**Consequences.** Reflection on a VSEssentials internal: a smoke check reads
the game's own DLL for the type and field, and a failure at runtime is logged
once and leaves vanilla's fireflies on. Not seen in game.

## D60. Falling leaves get their real shapes, drawn through the game's standard shader

Supersedes the "Rejected: a textured leaf" paragraph of D58.

**Context.** Asked for leaves with the tree's actual leaf shapes, tinted with
its seasonal colour, rather than coloured squares. The game's particles
cannot carry a texture. Its leaf textures are greyscale clusters of
overlapping leaves (64x64 for broadleaves), tinted at draw time by the
block's climate and season colour maps.

**Decision.**
- *Shapes:* `LeafSprites` cuts single leaves out of the PNG: a flood fill from
  a seed texel through opaque texels within 40 grey of the seed, inside a
  circle of half a cell (12 px on a 64 px texture), wrapping as the texture
  tiles. The art shades each leaf as one tone against its neighbours, so the
  fill stops at the leaf's own edge. Best of 60 seeds per sprite: fill near
  40%, stopped by its own edge rather than the circle, from a light (front,
  whole) leaf. 16 per texture, one 4x4 sheet, uploaded once.
- *Colour:* the game's own tinted samples (`Block.GetRandomColor` at the
  leaf block) over the texture's mean grey, as a per-channel multiplier.
- *Drawing:* flat two-sided quads through `PreparedStandardShader`, the
  program block entities use, so the game's lighting, fog, alpha test and
  render-target outputs apply and the mod ships no GLSL for it.
- *Motion:* `LeafFlight` on the CPU. The leaf falls at about 0.7 blocks a
  second, swinging across the wind, and takes up the wind's speed downwind:
  0.3 b/s in still air, 5 at a gale, by the square of the local wind,
  including the gust. It tumbles faster when blown. It lands flat on the first
  non-leaf collision box or floats on liquid, lies 8 s, then shrinks.

**Rejected.**
- An instanced draw with a custom shader. It is cheaper per leaf, but needs
  mod GLSL for lighting, fog and every render target. At 160 leaves, 160
  draws through the game's own program are the safer cost.
- Windows of the texture without a flood fill. They come out as square
  fragments of a cluster, not leaves.

**Consequences.**
- *Cost:* up to 160 `RenderMesh` calls per frame and one light lookup per leaf
  ten times a second. One small texture per leaf texture seen. Not measured.
- *Fallback:* a texture that yields no leaf, or any render failure, falls back
  to coloured particles, and the log says so once.
- *Unknown:* that the standard shader accepts the xyz/uv/rgba/flags layout used
  here, and the channel order of the tint. A wrong order would show as blue or
  purple autumn leaves.
- *Status:* seen working in game (2026-11-24, "leaves work and look good"),
  which settles both unknowns above. Its one-draw-per-leaf drawing is
  superseded by D61.

## D61. Leaves are batched, lie on the ground as litter, and ride water currents

Supersedes the *Drawing* point and the cost of D60.

**Context.** D60's leaves were seen working, but only as an occasional tumble,
and the ask was dozens on screen in heavy weather, litter that piles up and
decays at varied rates (only if it does not chug the game), and leaves that
float and move with the current.

**Decision.**
- *Batching:* each leaf texture's sheet owns two preallocated meshes, one for
  flying leaves (400 quads) and one for litter (1500), uploaded once. Flying
  leaves are rewritten on the CPU every frame (four corners from
  `LeafFlight.Corners`, light and tint in the vertex colour, divided by
  `LeafRenderer.TintScale` and restored by the shader's `RgbaTint`) and sent
  with one `UpdateMesh`; unused quads are zeroed. So the draw count is two per
  leaf texture seen, however many leaves there are.
- *More leaves:* 36 leaf-map tries a tick (was 12), cap 400 textured
  (`LeafRenderer.MaxFlying`; the coloured fallback keeps its 160), and a storm
  term: from a 0.45 to a 0.9 local wind the shed rises to at least 0.4 of the
  autumn peak in any season, because storms tear green leaves.
- *Litter:* a landed leaf stays as a quad in the litter mesh, which is relative
  to a held origin and rebuilt at most twice a second, only when it changed.
  Each lies for an exponential draw (mean 240 s on ground, 90 s on water,
  divided by 1 + 2 x rain; clamped 20 to 900 s), so most go within minutes and
  a few stay long. Leaves on the same block stack by 0.004 each, up to 24, so a
  pile builds without z-fighting. Forty litter leaves a frame are checked in
  turn: decayed ones shrink away, unsupported ones fall again, a strong gust
  (local wind over 0.7) lifts one now and then, and any beyond 64 blocks is
  dropped. Cap 1500; when it is full a landing leaf fades instead.
- *Water:* a leaf that meets liquid floats at its surface and is carried by the
  game's own current there (`IBlockFlowing.GetPushVector`, else the block's
  `PushVector`), at 0.9 b/s along it; on still water the wind nudges it. It
  strands at the bank and becomes litter there.

**Rejected.**
- Litter as blocks or block decals. Writing to chunks is server state and
  rebuilds chunk meshes; a client-side mesh costs neither.
- Persisting litter across sessions. Session-only keeps it free of save data
  and of a stale pile at a place the player has not seen for days.
- Instancing with mod GLSL: still not needed at these counts.

**Consequences.**
- *Cost:* per frame, up to 400 x 4 vertices written and uploaded per active
  sheet, two draws per sheet, and 40 litter checks (a block read each). Litter
  re-upload at most 2/s. Not measured.
- *Limits:* litter is session-only and does not survive a world reload; it does
  not follow a block broken under it until its turn in the round robin.
- *Status:* L2, not seen in game.

## D62. Reflections stage two: the pixel reflection and the march on the modular path

Extends D48 (stage one, the bridge) and D13 (reflection through the existing
BRDF). Supersedes the "views 41 and 42 only" scope of `pbrterrainreflect`.

**Context.** View 41 was seen in game on 2026-11-22: beneath the legacy blue
depth tint, the capture reaching terrain was the world, so the bridge holds.
The modular path's ambient highlight was still the flat fog colour split into
an upper and lower half, so no surface reflected anything, and the Studio
marked both reflection controls "not in this build".

**Decision.**
- *Pixel reflection in the material path, no sampler.* `vvTerrainPixelReflection`
  ports the legacy method: the ray leaves the texel centre (one colour per
  texel by construction), the direction is quantised into cells (16 smooth,
  3 rough), the analytic image is sky / lifted horizon / dim ground with the
  sun's half of the sky brighter, and the result is the IMAGE handed to the
  ambient specular, whose Fresnel, roughness fade, specular occlusion and
  slider still decide how much shows. At `pbr_pixelreflect` 0 it returns the
  exact flat environment the path drew before.
- *The march stays in `pbrterrainreflect`,* which owns the sampler, declared
  directly above `main` as stage one requires. The material path, which comes
  first in the file, reaches it through a prototype guarded by
  `VV_TERRAIN_SCENE_CAPTURE`, and only that group inserts the define (on the
  line after the material snippet's `vintagevisuals-hook` marker). A rollback of
  the capture group therefore removes the define, the march and the sampler
  together, and the material path compiles with no reference to the capture.
- *A budget, which legacy never had.* The march runs only where the reflection's
  own weight (strength x Fresnel x roughness fade x specular occlusion x the
  ambient slider, as luminance) is at least 0.025 and the texel is within 48
  blocks, fading from 32. That keeps it off grass, soil, leaves and dry rough
  stone, which are most of the screen; wet stone, metal and polished blocks
  take it. At most 40 steps (legacy 96) plus 5 bisection passes; a long grazing
  ray walks coarser than the 2-texel stride instead of paying for every texel.
  All capture reads in the loop are `textureLod`.
- *Kept from legacy, each a finding from the game:* screen-uniform stepping,
  axial depth from the projection's w, 1/w interpolation, bisection before the
  thickness test (0.5 blocks), edge / thickness / camera-facing fades, the
  luminance ceiling on the captured colour (2x the environment, hue kept) and
  the feedback guard against a floor reflecting its own brightened copy.
- *Debug views* 32, 34, 35, 36, 37, 39 and 48 return on the modular path; 39
  and 48 add a dark blue for "skipped, not worth a march".

**Rejected.**
- Declaring the capture sampler at the material snippet to avoid the
  prototype. Stage one's invariant is that the sampler sits below every vanilla
  sampler and in its own group; the prototype costs nothing and keeps it.
- Porting the world-volume fallback now. It needs a second auxiliary sampler,
  is unprofiled, and was never seen working; the analytic sky is the honest
  fallback until the scene reflection has been seen.

**Not measured.** Cost in game. Expected: the analytic part is a few ALU per
opaque fragment; the march is bounded by the gate. With Scene reflections on,
the capture still copies one frame per frame.

## D63. Terrain metals keep a body colour; walls take less canopy shade

**Context.** First in-game look at the D62 build (2026-11-24): a placed gold
metal block rendered with a near-black top and blotchy gold/black sides, and
the face of a trunk under its own crown rendered near black. Neither came from
the reflection itself. The modular path zeroed a metal's diffuse, leaving its
whole body to the ambient highlight, which the roughness fade (about 0.28 at
the player's roughness) and the ambient slider (0.3) cut to a few percent. And
the canopy shade darkened every receiver up to its 0.85 cap, so a trunk, which
always stands under the densest part of its own crown, lost 85% of a colour
vanilla had already darkened for facing away from the sun.

**Decision.**
- A metal keeps `mix(0.55, 0.85, roughness)` of vanilla's lit colour as its
  body: the lit colour is the texture's albedo under the scene's light, and a
  metal's albedo is its reflectance. A polished metal keeps less because its
  reflection carries more. Dielectrics are unchanged.
- A block's vertical faces take at most 0.45 of the canopy cap, rising to the
  full cap on a face pointing up. Plants keep their own cap.

**Not measured, not seen.** Both are L2 until the same blocks are looked at
again.

## D64. Torch light drives terrain PBR; rain follows each texture's relief

**Context.** Asked for (2026-11-24): block light from torches and the like
should drive the PBR realistically, and rain should pool in the divots of top
faces and stream down the channels of side faces.

**Decision - block light.**
- Terrain's block-light highlight now takes its direction from the shared
  core's `vvBlockLightDirection` (the surface gradient of vanilla's own block
  light across the face), as creatures and particles already did. It was a
  fixed nudge of the light colour upward, so every glint sat in the same place.
  The `Block light` and `Block light direction` sliders now reach terrain. The
  direction is taken before any early return because it uses derivatives.
- The pixel reflection's luminance ceiling follows the brighter of the sky
  colour and the local block light. Against the sky alone, a torch reflected in
  a wet cave floor was capped to near black.

**Decision - water in crevices** (`PseudoPBR.CreviceWater`, `pbr_crevicewater`,
`vv_pbrCreviceWater`, default 0.8; plus `vv_weatherRain`, the falling rain as a
fact).
- Reads the second atlas's height channel, which had no consumer.
- Top faces (normal y > 0.7): water fills texels below a level of 0.46 x the
  wetness, so joints and pits fill first and a soaked surface floods; pooled
  texels are fully wet, flattened to the face normal, and are the only place
  ripples land. Texels above the level keep 0.55 of the film at full slider.
- Side faces (|normal y| < 0.3), only while rain falls: a texel lower than its
  left and right neighbours and not lower than the ones above and below it is
  a channel; drops step down it a whole texel at a time, each column on its
  own offset and the block's seed. The down direction in texture space comes
  from screen derivatives, so it holds however the texture is rotated.
- Not on plants, beyond the detail distance, without the second atlas, or
  when dry. 0 is the even film from before. DATA GAP: the game has no water on
  block faces; where the low texels are is the texture's, the rest is ours.

**Cost, not measured.** Block light: a few ALU and derivatives per opaque
fragment. Crevices: none on a dry world; on a wet top face no extra read; on a
wall in rain four reads of the second atlas.

## D65. Crevice water must read as water; torchlight shades the relief

**Context.** In game after D64 (2026-11-24, rain, sliders at maximum): "not
seeing much of a pronounced effect", and no way to tell whether torch light
was shading the per-pixel normals. The player's Wetness view showed top faces
almost uniformly white with grey specks, walls near black, and the crevice
occlusion view nearly flat. Three causes, each from D64's own numbers:
- The pool level was ABSOLUTE (0.46 x wetness) on a height that the atlas
  normalises to each texture's own range, so on most textures it flooded close
  to half the face, and the 0.55 film left on the rest gave pools nothing to
  stand against.
- Pooled texels kept their crevice's baked occlusion, and specular occlusion
  then took the reflection away from exactly the texels holding water.
- Walls streamed only in strict grooves, scaled by the wall's own wetness
  (a quarter of a top face's), so streams were rare and faint.
- Vanilla lights a face with one block-light value per vertex, and D64 shaded
  the per-pixel normal only in the torch HIGHLIGHT, a narrow lobe that rough
  stone barely shows. The diffuse relief came from the sun term alone, which is
  absent at night and underground.

**Decision - pools.** Water collects where the texture is LOCALLY low: a texel
below the mean of both neighbours on either axis by 0.03 starts to hold water
and is full at 0.10, once the ground is a little wet (so a mortar joint, low
across and level along, fills as well as a pit); plus the texture's lowest
band, up to 0.24 x wetness. The rest of the face keeps 0.30 of the film. A
pooled texel's occlusion is lifted (the water surface sits above the crevice),
its roughness goes to the minimum and its specular to at least 0.8, and its
diffuse darkens a further 0.62 with a slight cool lean. Four second-atlas reads
on a wet top face (none before).

**Decision - streams.** Channels are judged more loosely (open along the fall
line up to 0.04-0.12 of a rise); the texture's lowest texels on a wall are
seams that stay wet while it rains; and about 30% of the columns of an open
face carry a rivulet of drops with no groove to follow. Gated on the sky
exposure and the falling rain rather than the wall's own wetness. Running
water gets the same mirror treatment as a pool.

**Decision - torchlight relief.** `vvTerrainBlockRelief` adds, in the torch's
own light on the texture's colour, the change in N.L between the texel's own
normal and the flat face toward the recovered block-light direction. Texels
tilted toward the torch brighten, texels tilted away darken, a flat texel is
untouched, and the result is bounded to 0.6 of vanilla's lit colour. It rides
`vv_pbrBlockLight` (clamped to 1 for this term), so 0 is still no block-light
response at all; no new slider.

**Debug views.** 73 Water in crevices (grey film, blue pools, cyan streams).
74 Torchlight on the normals (terrain lit by block light alone, as clay
through each texel's normal, with the torch highlight on top). The debug view
maximum is 74.

**Cost, not measured.** Pools: four texture reads on a wet top face within the
detail distance. Relief: one derivative-based direction per opaque fragment.

## D66. Crevice water asks the game which materials hold water; torch relief is smoothed

**Context.** In game with D65 (2026-11-25, rain, debug views 73 and 74):
pools lit up as speckle across a whole meadow and across speckled stone, not
along mortar joints; streams speckled walls and tree trunks; view 74 showed a
cobble wall beside a torch as black-and-white per-texel noise, and in normal
rendering the torch made little visible difference.

**Cause.** The second atlas's height is the texture's normalised luminance.
On grass, soil, sand, wood and speckled stone that field is colour noise, so
"darker than its neighbours" is true of every speck. D65's absolute low band
made it worse. For the torch, the direction recovered from a torch mounted on
the wall tilts up to about 70 degrees off the face, and against a grain-level
normal N.L swung from 0 to 1 between neighbouring texels.

**Decision - materials.** Ask the game first (the information ladder). The
second atlas's red byte now carries metalness in its top seven bits and a
hold-water flag in the lowest (`MaterialAtlas2Builder.PackMetalWater`,
format version 2, the flag hashed into the cache fingerprint). The flag comes
from the texture's resolved `EnumBlockMaterial` through
`MaterialProfiles.HoldsWater`: Stone, Mantle, Ceramic, Brick, Metal, Ore.
Everything else keeps D64's even wet film. A neutral texel (red 0) is neither
metal nor water-holding. Metalness loses one bit of precision (127 levels),
which nothing can see: it is per-region and takes three values in practice.

**Decision - structure.** On a top face, a joint is low against both
neighbours across it (by 0.06, full at 0.16) and runs on along it (its
along-neighbours no more than 0.05-0.125 higher); a pit is below all four.
No absolute height band. On a wall, a channel must be lower than BOTH side
neighbours (0.03-0.09); the low-seam term is gone; rivulets stay.

**Decision - torch relief.** `vvTerrainTorchShade` lights a softened normal
(0.6 of the way from flat to the texel's) from a bounded direction (0.6 of
the way from the face normal to the recovered one), and adds a coherent term
from the baked occlusion: a texel below its two-texel neighbourhood takes
0.8 of what it lacks less torchlight, which is what puts joints in shadow and
stone faces in light. Strength 1.2, still bounded to 0.6 of vanilla's lit
colour and 0 at `vv_pbrBlockLight` 0. View 74 now shows exactly this term.

**Cost, not measured.** Unchanged from D65: four reads on a wet top face of a
water-holding block, none on any other material.

## D67 - Crevice structure is measured on the CPU at load, not guessed per texel in the shader

**Context.** D64 to D66 (v17 to v19) each found "crevices" in the fragment
shader by comparing a texel's height with its four neighbours. In game
(2026-11-25, seven screenshots of v19) pools and streams were still speckle
and wall streams were straight vertical lines on random columns. An offline
audit against the game's own stone textures (`/mnt/project-files/audit/`)
found the root cause: the only height the mod has is the texture's
luminance, and on Vintage Story stone that is mostly painted speckle, while a
real joint is a long connected dark run about two texels wide. No per-texel
neighbour test can tell the two apart; it takes a view several texels wide.

**Decision.** `src/PseudoPBR/CreviceMaps.cs` computes three maps per
water-holding texture when the second atlas is built, a port of
`tools/pbrgen/crevice.py` (where the method was previewed on twelve real
textures and approved by the player before any of it was built):

- *joints*: black top-hat of the lightly smoothed height (closing radius 2),
  hysteresis from strong seeds (82nd percentile, at least 0.05) into weak
  neighbours (0.75 of that), keeping only 4-connected components of at least
  10 texels that span at least 0.3 of the texture. A speck or a chisel mark
  is short; a joint runs.
- *stone shape*: a pillow, rounded within three texels of a joint and flat on
  top, with 0.15 of the painted shading kept.
- *flow*: for a wall, water takes the lowest of the three texels below it on
  the shape (joints 0.25 lower: channels), so it slides off a bump's shoulder;
  under a ledge it runs sideways along the joint. Rain lands on every texel
  and the gathered amount, log-scaled, marks the streams. Each texel also gets
  a phase (its step along the longest path through it, over 9, plus that
  path's own hash) so drops slide along a path, bends included.

Packing (`MaterialAtlas2Builder`, format version 3): a region carries this
layout when its material holds water, the game says it does not glow and it
is at most 128 px; the lowest red bit now means exactly that. Green is the
shape, blue the joint depth (high nibble) and flow (low nibble), alpha the
phase. The shader derives AO from the shape (0.55 on a joint floor, 1 from
shape 0.6), pools top faces straight from the joint map, streams walls along
the paths with `fract(phase - sceneClock * 2 + blockSeed)` (an integer speed
so the 1.5 s wrap is seamless) and only where the texture is upright on the
face, and lights torch relief from the shape's gradient (four second-atlas
reads, only for torch-lit water-holding texels).

**Supersedes** D66's structure tests and D64's wall grooves and rivulets.
D66's material gate stands. Debug view 73 now shows the baked maps; the old
live-water view moved to 75.

**Evidence.** The C# port matches the reference texel for texel on a
formula fixture (`crevice_fixture.json`, smoketest) and, out of tree, on all
twelve audited textures (largest difference 0). A smoketest fails if a speck
pools or a kept joint is small. Not seen in game. Cost: about 7 ms per
texture at load including JIT, measured on twelve textures out of game;
runtime cost not measured.

**Limits.** Basalt brick vertical joints and a few short dry-stone joints are
missed. Paths are traced for an upright texture; a texture turned on its face
gets wet joints and no streams. Pools stop at the block edge.

## D68 - Torch terms read the block light alone; a stream is where paths merge

**Context.** D67 in game (2026-11-25, four screenshots). On ashlar (the
game's `stonebricks`, textures `block/stone/brick/*`) debug view 73 showed
every flat stone face flooded with stream, hatched diagonally; view 75 showed
chalk ashlar walls as cyan speckle. In a coral cobblestone room a wall torch
changed nothing near it, and view 74 lit daylit walls bright and flat.

**Cause - streams.** D67 scaled the raw count of rain through each texel. On
a flat face every column carries the rain of every texel above it, so each
column of a flat stone was as "full" as a real channel, and each column had
its own path hash, so the drops on neighbouring columns were out of step:
speckle. The diagonal hatching was the side preference letting paths wander
on a flat face.

**Cause - torch.** Every terrain torch term (relief, highlight, the reflected
torch ceiling, views 9 and 74) read vanilla's `blockLight` varying. In
`chunkopaque.vsh` that is written only when dynamic lights are on, and then
holds `(2*bSun*sun + bBlock*block + ...) / (...)`: the sun and block light
COLOURS mixed by brightness, about as bright in daylight as beside a torch.
With dynamic lights off it is never written. Neither is the torch.

**Decision.** `pbrterrainmaterial` carries its own varying, `vv_blockLight =
rgbaLightIn.rgb` (the block light alone, with vanilla's falloff), in both
terrain programs and in the same group as its fragment half; the terrain
material path reads it through `vvTerrainTorchLight()`. Water-holding stone
is shaded by its baked shape at full strength (the D66 softening was for the
grainy luminance normal), `VV_TERRAIN_TORCH_SHAPE` 2.5, relief 1.6, cap 0.75.

The flow amount is now convergence: rain through a texel over the length of
the longest path reaching it plus one, which is exactly 1 on a column nothing
joins. Streams start at 1.25 and are full at 3.25. Flat faces stay a film;
water gathers along the joints and drips from their ends and from under
stones. Ported to C# with the fixture regenerated; a check builds a flat
striped face and fails if it carries any stream.

**Evidence.** Previews of the reference on chalk, basalt and granite ashlar,
coral, granite and chalk cobblestone and two dry stones
(`/mnt/project-files/audit/crevice-v2-*`). Not seen in game.

**Limits.** Coral cobblestone's joints are mostly missed: its pores are
deeper in the top-hat than the gaps between its stones. The legacy pseudopbr
path still reads vanilla's `blockLight`; it is not the active terrain path.

## D69 - Torch relief darkens less than it brightens, and its joint shading fades with distance

**Context.** D68 in game (2026-11-25, five screenshots): with the torch read
correctly the relief finally showed, but a single wall torch turned every
joint in a small coral room near-black, the ceiling included, and a smooth
ashlar block set in the wall got thick black bars across its joints.

**Cause.** The joint term (shape occlusion, 0.8 of what a joint lacks) applied
wherever block light reached, and in a small room a torch reaches every wall,
so it darkened mortar everywhere by up to the 0.75 cap of vanilla's lit
colour. Near the flame that reads as relief; across the room the light has
no clear direction and the same darkening reads as black paint. On ashlar the
shape normal at full strength turned the three-texel rounded band either side
of each joint fully away from the light.

**Decision.** Darkening is capped at 0.35 of vanilla's lit colour and
brightening at 0.6; the joint term is 0.4 of what the joint lacks and fades to
0.3 of that where the recovered light direction is close to the face normal
(directness `(1 - dot(toTorch, n)) * 3`); the shape normal is 0.8 of the way
from flat at 1.6 per unit of slope; relief 1.5. Previewed near a torch and six
blocks away on coral, chalk and granite ashlar and granite cobblestone
(`/mnt/project-files/audit/torch-v3.png`), with D68 and vanilla beside it.

**Evidence.** A check fails if the dark cap exceeds 0.4 or reaches the light
cap, or if the joint term stops fading; two mutations prove it. Not seen in
game.

## D70 - An optional slider steepens the game's own torch falloff on blocks

**Context.** D69 in game (2026-11-25): more natural, but a room lit by a few
torches reads evenly bright, with no pooling of light by each flame. Vanilla
spreads block light in near-linear steps of one level per block, so the walls
between two torches are almost as bright as the walls beside one.

**Decision.** Elijah chose a slider over a fixed change: `PseudoPBR.TorchFalloff`
(0..1, default 0, Materials > Light response, "Torch light falloff"). The
terrain shader reads the game's own block light (`vv_blockLight`, D68), takes
its mean as a level `b`, and above zero rescales vanilla's lit colour by
`(steep + 0.08) / (b + 0.08)` with `steep = 0.85 * (b / 0.85)^(1 + 2 * amount)`.
Light at or above 0.85 (beside the flame) is untouched, so the slider only
darkens the dim edge of a torch's reach and never adds light; the factor is
floored at 0.4 so a far wall is never black, and it fades out with
`vv_sunExposure * vvTerrainSunPresence()` so daylit blocks keep vanilla light.
It is a reshaping of the game's value, not a new light: the light still comes
only from where the game says it does. Blocks only; creatures, items and
particles keep vanilla light, which can make a mob look brighter than the wall
behind it at full slider.

**Evidence.** I21 walks the shipped function statement by statement and fails
if the slider at 0 changes anything, if it ever brightens, if it drops below
its floor, if a brighter vanilla light can come out darker, or if it acts in
full daylight; two mutations prove the floor and the daylight fade. L2, not seen
in game; performance not measured (one pow per fragment).

## D71 - Water gets its own patch group on chunkliquid, texel-locked, building on vanilla

**Context.** Elijah picked water next (roadmap, 2026-11-25), with the fps
baseline first. `chunkliquid.fsh` had never been patched. Vanilla's water
already has fresnel alpha, foam from `depthTex`, vertex waves, rain droplets, a
broad sun highlight and a lava branch. Elijah asked that water use the texel
system from the terrain reflections, with a subtle blur if it looks good.

**Decision.** A new group `water` (`water.yaml`, `water.glsl`, `src/Water/`)
that runs on vanilla's FINISHED water colour just before vanilla's fog, and
never on lava. It keeps every vanilla layer and adds, in the planned order: a
sky and horizon reflection, depth colouring, the rain ripple field, and sun and
moon glints. Every surface input is evaluated at the centre of the water texel
(1/32 block, the water texture's pixel): wave normal, view vector, reflection
direction, the depth sample (taken at the texel centre's screen position via a
least-squares step on the screen derivatives) and the ripple. The sky is looked
up in the terrain's direction cells. Composition follows the split of light at
a surface: reflected share F (Schlick, F0 0.02, times the strength) plus the
body (vanilla's colour, deepened with the path behind) over `1 - F`; out alpha
is `F + a(1 - F)`.

The blur is in TEXEL space: a texel's reflection is averaged with those seen 3
texels away in four directions. Adjacent texels were tried first and blurred
nothing, because the waves barely change over 1/32 block (offline preview). At
the default 0.35 the stepped bands keep their texel edges with one intermediate
step between them.

Waves: four sines with integer wave numbers per 4096 blocks, so the CPU origin
wrap lands on a phase boundary, plus value-noise chop on cells wrapped to the
same period; the first preview with sines alone looked like regular stripes.
Vanilla's broad sun highlight is multiplied by `vvWaterVanillaSpecularKeep()`
(1 when the group is not live, a quarter at full glints) so the sun is not drawn
twice.

Shared code (terrain ripple field, sky cells, analytic horizon) is COPIED into
water.glsl because each group must roll back alone; `WaterChecks` compares the
bodies token for token after the prefix rename and fails on drift. The scene
block goes through the one upload path (`PbrShaderBinder.UploadSceneTo`), as
the uniform-wiring contract requires. The sun direction is the mod's own
(`AtmosphereState.SunDirection`), not vanilla's `lightPosition`, which is not
reliably uploaded to chunkliquid.

**Rejected.** A screen-space blur of the reflection (breaks one colour per
texel). Replacing vanilla's water colour (loses the climate tint, foam and the
game's look). Adding the scene-capture reflection in the same group (it needs a
sampler; it comes next, as its own group behind its own toggle).

**Evidence.** `tools/verifypatches`: all anchors match the game's own
chunkliquid.fsh and it compiles in every combination vanilla builds, with
FOAMEFFECT 0 and 1 (FOAMEFFECT added to the swept defines). `WaterChecks`:
parity, period tiling, zero-composes-to-vanilla, reflection never lowers alpha,
colour stays between reflection and body, master gate first, never on lava, no
sampler. Offline previews of the shipped GLSL in WebGL with the game's
`water.png`, `stone/sand/granite.png` and `soil/muddygravel.png`: noon, low sun,
rain, close-up, debug views, softness 0/0.35/1. L2. Not seen in game, not
measured.

## D72 - Frame time is measured with GPU timestamps per stage; quality is one tier that scales cost

**Context.** Nothing in the mod had been profiled, and Elijah is fps-sensitive.
The water and every later system need a baseline to be judged against.

**Decision.** `src/Common/Diagnostics/`. `FrameProfiler` registers marker
renderers at the start and end (orders -1e6 / +1e6) of every render stage and
around the mod's own passes at their known orders (scene capture 1.0 in
AfterPostProcessing, falling leaves 0.5 in Opaque). Each marker lays down a GPU
timestamp (`glQueryCounter`, reached by reflection like the existing GL bridge)
and a CPU stopwatch reading; results are read four frames later and only when
available, so measuring never stalls. Timestamps rather than elapsed-time
queries because those cannot nest. Markers exist only while the readout
(`.vvperf`, `Performance.ShowFrameTimes`) or a bench (`.vvbench [s]`,
`.vvbench tiers [s]`) needs them. Bench summaries go to chat and to
`client-main.log` as `[VintageVisuals] bench ...`.

`QualityTier` (`Performance.Quality`, 0 Low .. 3 Ultra, default 2 High) scales
cost only: terrain canopy ring radius (0.5/0.75/1/1), terrain detail distance
(0.5/0.75/1/1.25), water blur taps (1/3/5/5) and ripple layers (1/2/2/2), and a
water march budget reserved for the scene reflection. High is exactly the
previous build; a check fails if any scale is not 1 at High. A tier never
enables a feature that is off.

**Limits.** A stage figure includes vanilla's work and the mod's shader patches
together; a patch's own cost is only visible by comparison (the tier sweep, or
a feature toggled in the same scene). The profiler measures what it brackets,
so a vanilla renderer sharing order 1.0 in AfterPostProcessing would be counted
in "vv scene capture". L2: not yet run in game.

## D73 - Water is viewed from the eye, rain drops are hashed per drop

**Context.** First in-game look at the water (2026-11-25, Elijah's
screenshots): in daylight a marsh pond read as near-white, like snow or milk,
with the bottom hidden; in rain it was flat grey. Separately, rain drops on
water AND terrain landed on the same spots every time.

**Cause, water.** `chunkliquid`'s `fWorldPos` has the PLAYER at the origin,
not the eye: the eye height and any third-person offset live only in the view
matrix (`CameraMatrixOriginf`, the same fact `PbrShaderBinder` records for the
scene reflection). `vvWaterSurface` took the view vector as `normalize(-pos)`,
i.e. from the player's feet, so water at standing height was seen exactly
edge-on, Fresnel went to 1, and the whole surface mirrored the sky. The offline
previews placed the camera at the origin and so could not show it; the harness
now reproduces it (eye 1.62 above the origin, old vs new).

**Decision, water.** `WaterShaderBinder` recovers the eye from the view matrix
(`-R^T t`, column-major) and uploads `vv_waterEye`; both view vectors start
there. 0 is the previous build. The sky image is also capped at the sky's own
luminance (the terrain fallback's horizon and toward-sun lift suit a rough
surface, not a mirror), and below 0.32 of elevation the reflected sky gives way
to the surroundings at 0.30 of the sky light: near-horizontal rays from a pond
meet its banks and trees. DATA GAP: the banks are stood in for, not looked up,
until water has a scene trace. Rain ring crests raise the reflected share to
0.9 x ring at any angle, so drops read looking down as well as across; scaled
by the ripple strength only, never by the reflection slider.

**Cause, drops.** Every copy of the ripple field (`pseudopbr.glsl`,
`pbrterrainmaterial.glsl`, `water.glsl`) hashed whether a cell is struck and
where in it from the CELL alone, on a clock that wraps every 1.5 s. The drops
replayed identically forever.

**Decision, drops.** Each drop is hashed by its cell and its DROP NUMBER:
`floor(phase) + epoch * rate`, where `EnvironmentTracker.RippleEpoch` counts
whole turns of the ripple clock (mod 4096, even so the half-rate layer stays
continuous; uploaded as `vv_weatherRippleEpoch` through `SceneInputs` and
`vv_waterRippleEpoch`). Rain strength still decides how many cells are struck
per drop, so light rain is sparse and heavy rain dense, but not which ones. The
half-rate layer rebuilds its time and drop count from the epoch, which also
removes the jump it had at every wrap. Epoch 0 still re-deals the drops within
each 1.5 s turn. The 4096 wrap re-deals drops in flight once every ~1.7 hours.

**Limits.** Terrain view vectors are built the same feet-relative way and are
not changed here (the PBR thread owns them); recorded as a known issue in
STATUS. L2: previews and checks only, not seen in game.


## D74 - Terrain view vectors start at the eye, not the player's feet

**Context.** D73 found on water that chunk positions have the player's FEET at
the origin (`CameraMatrixOriginf` is the player camera matrix with the player
at 0,0,0; the eye height lives only in the matrix), so `normalize(-pos)` looks
from 1.6 blocks too low. Terrain built every view vector the same way, so a
floor at standing height near the player was shaded as if seen almost
edge-on: Fresnel, wet sheen and sky reflection pushed toward their grazing
values close to the player, the opposite of how a floor looks underfoot.

**Decision.** One helper, `vvTerrainToEye(p) = vv_pbrEye - p`, and every
terrain view vector goes through it (specular, ambient, the pixel reflection
march's direction, foliage transmission, flora, debug views). The eye comes
from `ViewEye.FromView` in `src/Common/Scene` (the water's `-R^T t`, moved
there so PseudoPBR does not depend on Water; `WaterShaderBinder.EyeFromView`
now delegates to it). Positions and distances stay in the feet-origin space,
which is the space the reflection capture's matrix also uses, so only the
direction changes. Unset, the uniform is zero and the vector is the old one.

**Evidence.** I22 evaluates the shipped helper (a floor three blocks away has
N.V above 0.4 from the eye, 0 from the feet), requires no terrain view vector
to normalise a raw negated position, and requires the binder to upload the
eye; two mutations prove it. Entities, particles and the legacy pseudopbr path
are not changed and their position space was not checked. L2, not seen in
game; cost is one vec3 uniform and a subtraction.

## D78 - Falling leaves reach 50 blocks, thinned by distance and view, inside a tier budget

**Context.** In game (2026-11-25) leaves fell only right next to the trees.
v15 tried a 40 x 40 square around the player (reach 20) against the shader's
leaf map, whose reach the terrain shader fixes at 32 blocks (32 x 32 cells of
2 blocks, 128 uniform vec4s). Elijah asked what a 50-block radius would cost,
with the rate tied to what makes leaves fall.

**Decision.**
- A second, CPU-only leaf map, `LeafSourceMap` in `Common/Scene`, scanned by
  `EnvironmentTracker` like the others and only while asked for: 32 x 32 cells
  of 4 blocks (64 blocks each way), one column per cell, read from the rain
  map's top (leaf blocks are not rain-permeable in the game's block types)
  down through the crown, stopping two blocks after it. About eight reads a
  column, so about 2,000 reads a tick against `CanopyMap`'s 10,000. Growing the
  shader's map instead would have quadrupled its uniforms in the terrain shader
  for a value only the spawner needs. The spawner uses the fine map where it
  reaches and the coarse one beyond.
- `LeafBudget` (pure): reach and cap by quality tier (Low 28/200, Medium
  40/350, High 50/500, Ultra 56/800); tries scale with the disc's area so the
  density near the player is exactly v15's; beyond 14 blocks the weight eases
  to 0.3 at the reach (a leaf 50 blocks off is about ten pixels and reads as
  forest motion, not as a leaf); beyond 10 blocks, crowns behind the camera
  get 0.2 (a leaf lives about ten seconds, so the ring behind is not empty when
  the player turns); leaves beyond 14 blocks may use only 70% of the cap; a
  leaf landing beyond 28 blocks fades instead of taking a litter slot. Every
  weight is a cheap rejection before any map, gust or block read.
- The rate adds two of the world's own drivers: rain knocks leaves loose even
  in still air (0.12 of the autumn peak at full rain, 0.3 of that outside the
  leaf-fall season), and frost in the leaf-fall season (1 C down to -3 C)
  up to doubles the season's shed. A spring frost sheds nothing extra.
- Leaf batches start at 64 quads and double as needed to the cap. Before, each
  sheet's flying batch was allocated at 400 and uploaded whole every frame,
  whatever the count; at an 800 cap that would be 3,200 vertices a frame per
  tree texture.
- `.vvbench leaves [s]` holds three leaf modes in turn (none, v15's reach and
  cap, the current tier's) with 15 s to settle each, and logs frame time and
  the vv.leaves segment with the leaf counts. Runtime only (`LeafBench`), and
  it never turns on leaves the player switched off.

**Evidence.** Three smoketest checks (reach and budget shape per tier, the
coarse map's column read and the batch growth, the rain and frost drivers) and
seven mutations proving them. L2: built, not seen in game, cost not measured.
The estimate it rests on: per flying leaf, two block reads a frame plus a
light read and a gust sample every 0.1 s and one quad; per tick about 220
tries at High, most rejected by the weights. GPU cost is two draws per leaf
texture whatever the count. **Not verified**: how far a leaf can be seen
before the thinning shows, whether the coarse map misses narrow crowns, and the
frame cost, which `.vvbench leaves` in a forest measures.

## D75 - One rain-drop system: vanilla's droplet rings step aside, impact capture rejected

**Context.** Elijah (2026-11-25) asked whether the PBR drops could trigger
only where vanilla's raindrops land on water, with the same logic on land.

**What vanilla does.** Two unrelated things. (1) `WeatherSimulationParticles.
SpawnRainParticles` (VSEssentials) does not detect impacts: every spawner tick
it picks 300 x precipitation random columns within 60 blocks (denser near the
player), puts a splash particle on the rain-map top there, liquid or solid, and
spawns it. (2) `chunkliquid`'s `dropletnoise` draws rings on a FIXED grid that
replay in the same cells forever, scaled by `dropletIntensity`.

**Decision.** The mod's ring field already follows (1)'s rule: random places
per drop (D73), count linear in precipitation, only on sky-exposed top faces,
water and land through the same parity-checked function. (2) is the
fixed-spot pattern Elijah objected to, so while the ring slider is above 0 it is
multiplied out on water (`vvWaterVanillaDropletKeep`, `vv_waterOwnDrops`; 1 when
the group is not live or the slider is 0). One drop system covers both surfaces.

**Rejected: capturing vanilla's splash positions.** Possible (a postfix on the
particle manager's Spawn matching the public static `splashParticles`), but the
splashes are themselves random draws rather than impacts, there are thousands
a second, they are spawned off the main thread, and matching them per fragment
needs either a loop over a uniform array on every wet terrain fragment or a new
sampler in `chunkopaque` (CLAUDE.md: twice cost the world render). The result
would be the same statistics at a large cost and a VintagestoryLib hook.

**Limits.** Splash particles and rings are not at the same points, only drawn
by the same rule. L2.

## D76 - Water glints glitter under a visible sun; far water shows more sky

**Context.** In game (2026-11-25, D75 build) the rings and shallows were
right, but the sun path was a flat white blob, even under a fully grey sky, and
distant water read flat.

**Decision.** Glints now need the sun's disc: `clearSky` also fades with the
game's cloud cover (`vv_sceneSkyCover`, 0.45 to 0.9), not only with the
overcast softening. Each water texel carries a facet that re-deals with
vanilla's wave counter: 38% glitter hard (x1.6), the rest softly (x0.3), so the
path breaks into sparkle rather than one shape. The clip at 4 is replaced by a
shoulder approaching a luminance of 1.2. The bank stand-in (D73) now starts
below 0.18 of elevation at 0.42 of the sky light, so far water shows the
reflected sky's variation. Supersedes D73's 0.32 / 0.30.

**Limits.** The facets are a stand-in for sub-texel wave detail. L2: previewed
offline, not seen in game.

## D77 - Water caustics: the waves focus sunlight onto the bed, drawn by the water pass

**Context.** Elijah asked for performance-friendly caustics that play with the
game's lighting and the mod's (sun shafts, PBR, the sun visibility the glints
use) (2026-11-25).

**Decision.** The water pass draws them, onto the bed it already sees through
each texel, rather than the terrain pass drawing them under the water. The bed
point is the texel centre pushed along its view ray by `behind / own` (vanilla's
`depthTex`, already read for the depth colour). The light is a refraction
model, not a texture: six short focusing waves (about 0.9 to 1.6 blocks, whole
cycles per 4096 blocks, bent by a slow warp so the lattice reads as cells) give
the surface curvature H; a ray reaching a floor `d` down was displaced by
`-d (1 - 1/1.33) grad h`, so the floor sees the surface through
`J = I - f H` and light gathers where `det J -> 0`. The gain over flat water is
`sqrt(1 + s^2) / sqrt(det^2 + s^2) - 1`, exactly 0 on flat water, softened by
`s` that grows with depth (the sun is a disc), shouldered below 1.6. The lens
settles at about 2 blocks of depth and holds instead of folding into noise.
The sample point is where the refracted sun ray entered the surface, the bed
texel is snapped to 1/32 block, and the pattern drifts with vanilla's flow.

It reads only what the glints read: the game's sun colour, `sunUp` and
`clearSky` (daylight, overcast and the game's cloud tiles), and the SURFACE's
shadow (`shadowBright`), so a tree's shadow, a cloud's shadow on the shadow map
and the mod's canopy dapple all take it away together. It is added to the
colour after the opacity is final, scaled by `(1 - outAlpha)`, so it is light
on what shows through and can never hide the bed. It fades past 4 to 8 blocks
of depth and when a bed texel is smaller than a pixel (0.04 to 0.08 blocks per
pixel), where the web only shimmers. `Water.Caustics` (0.6), zero is off;
`QualityTier.WaterCausticWaves` 3 on Low, 6 above (the 3-wave set is 60
degrees apart and its height scaled by sqrt 2 for the same curvature spread).
Debug view 7 shows the light alone.

**Rejected.** Caustics in `pbrterrainmaterial.glsl`, where the bed's albedo
and PBR response are known: chunkopaque does not know the water level above a
fragment without the water's own depth, and the change belongs to the PBR
thread's files. A caustic texture or a second sampler: CLAUDE.md, and the water
group declares none. Darkening between the lines: the water pass can only add
to what lies behind it, so the light is put on the lines and the rest of the
bed is left as the game lit it.

**Limits.** DATA GAP: the bed's colour is assumed (albedo 0.45), not read, so
lines on dark stone are as bright as on sand. The shadow is the surface's, not
the bed's. Side faces (waterfalls, walls under water) get none. The light on
its way down only loses a little red, because the game lights the bed as if no
water were above it and the water body colours the view path. Sun shafts: the
mod's shafts are read through the same shadow and canopy, no volumetric shaft
in the water is drawn. L2: previewed offline on the game's sand and water
textures, not seen in game; frame cost not measured.

**Amendment (2026-11-25, after the first look in game).** Elijah's screenshots
showed a high-contrast white web over the whole lake: as strong over deep water
as over the shallows, the same near and far, and on the sloped and vertical
faces under the water, so it read as a texture painted on top. Four causes, all
changed: the line peak and assumed albedo were high (1.6 and 0.45, now 1.0 and
0.35); the depth fade started at 4 blocks (now 1.5 to 5); nothing dimmed the bed
with the view path through clear water (now the mean of
`exp(-absorb * path * 2)`, the same absorption as the depth colour); and every
face took the full light. Light now falls on a face by N.L against the refracted
sun, with the bed's normal from the screen derivatives of the bed position
behind each pixel (one extra `depthTex` read per water fragment, taken before
the first branch); a block edge between two pixels counts as flat rather than
drawing a seam. The distance fade moved from 0.08 to 0.035 blocks of bed per
pixel. Previewed at the game's resolution: faint lines in the near shallows,
gone in deep water and in the distance. Still L2.

## D79 - Water shorelines, flow-aware rivers and far swells

**Context.** Elijah was happy with the water and caustics and asked how to make
it a polished stylistic alternative to vanilla's (2026-11-25). He picked the
cheap pass first: shorelines, rivers against lakes, far-water detail; then
reflections as their own step. (D78 is the leaf-fall thread's.)

**Decision.** Shorelines: the distance from a texel to what lies behind it,
measured across that surface (`abs(dot(centre - bed, bedNormal))`, using the
bed point and normal D77 already derives), so the depth over a floor and the
distance out from a bank wall, independent of the view angle; the view path
alone made the band widen at grazing views. Over its last `0.06` block the
water thins out, colour and opacity together, so the bank meets it softly. A
foam line laps over it: its reach swings between 0.45 and 1 of `0.30` block with
vanilla's wave counter, in a phase that wanders along the shore on 2-block value
noise, and each texel foams while the lap covers its own hashed threshold, so
the line advances and breaks per texel. Foam is lit by vanilla's vertex light
and the shadow, composited OVER the water (opacity never drops), and fades out
by 0.12 blocks of surface per pixel. Vanilla's own depth-edge foam steps back
to 0.35 while it is on (`vvWaterVanillaFoamKeep`, a token patch like D75's
droplets). `Water.Shore` (0.7), zero is vanilla.

Rivers: vanilla's `flowVectorf` now reaches `vvWaterSurface`. A current cuts
the wave slope along the flow by up to 0.65 and keeps it across (a linear map
of the slope, so the waves still tile the period), triples the chop's clock and
the glitter's re-deal rate. Still water in still air is calmed to 0.6 of the
height. Far swells: two long waves (11 to 12 blocks, whole cycles per 4096)
blending in from 20 to 60 blocks away, so open water keeps a texture where the
short waves are sub-pixel. Both ride the `Water.Waves` slider rather than new
ones. Debug view 8: foam red, flow green, swell blue.

**Limits.** Screen-space: a bank is found only where the ray behind a texel hits
it, so the line shows on banks facing the camera and around objects in the
water, not on the near side of a pond seen from its shore (vanilla's edge foam
has the same limit). DATA GAP: the scale of vanilla's flow vector is not
documented; full flow is taken at a length of 0.5. A water block stands most of
a block above its floor, so the soft edge acts at walls and raised beds rather
than on a sloped beach. L2: previewed offline (the preview's depth is marched,
so its bank normals are rough), not seen in game; frame cost not measured
(about two value-noise and one hash per shore texel, two cosines per texel for
the swells).

**Measured (2026-11-25, D78b).** Elijah's `.vvbench leaves` at High in a
forest (a 24 fps scene bound by the opaque pass): no leaves 40.86 ms a frame,
v15's reach 41.11 ms with 20 flying, D78's reach 41.05 ms with 35 flying. The
vv.leaves segment was 0.01, 0.06 and 0.09 ms CPU, 0.00 GPU: about 2.3 us per
flying leaf, so the 500 cap is worth about 1.2 ms at most. The cost was within
noise and the forest still read sparse, so D78b multiplies every crown's shed
by 3 (`ForestLifeSpawner.LeafAbundance`; the bench's legacy run keeps 1) and
raises `LeafBudget.FarShare` from 0.3 to 0.45. The rate's shape over season,
wind, rain and frost is unchanged. D78b is L2, not seen in game.

## D80 - Reflection phase 0: energy split, soft ceiling, eye distances everywhere

**Context.** The 2026-11-25 reflection review
(https://claude.ai/artifact/VmuoJ57XNtWe9zeqeXReua) found the terrain's
reflection capped rather than conserved: diffuse kept `1 - 0.35 F0` at every
angle while the reflection rose toward 1 at grazing views, `energyCap` lifted
every highlight and reflection by up to 18%, and scene hits were hard-clamped at
twice the environment's luminance. Creatures, particles and the terrain's
distance fades still measured from the player's feet (D73, D74). Elijah approved
the phased design and phase 0 the same day.

**Decision.**
- Diffuse gives up exactly what Fresnel ADDS over its head-on value,
  `(F(view) - F0) x roughness fade x specular occlusion x min(ambient, 1)`
  (`vvTerrainGrazingShare`). Facing a block nothing changes, so vanilla's look is
  kept. Rough stone loses almost nothing at a grazing view, which is the defect
  the old `(1 - F)` shipped with (blackened terrain; see the Stage C check). A
  polished wet floor hands real energy to its reflection.
- Multiple-scattering compensation applies to rough metals only:
  `1 + 0.18 x slider x metalness x roughness`.
- The scene-hit ceiling is a soft knee: full brightness below it, the excess
  compressed, never past twice it. The colour-match feedback guard is KEPT until
  phase 2 marks reflective pixels in the capture, because a floor reflecting its
  own brightened copy is a defect that was seen in game.
- `vvTerrainEyeDistance` drives every terrain distance fade, and the shaft
  march starts at the eye. Fog keeps vanilla's feet-origin distance, because
  vanilla's own fog uses it and the two must agree. `pbrentity` and `pbrparticle`
  read `vv_pbrEye`; their positions share the chunk space because they draw
  with the same view matrix (not separately measured).

**Evidence.** I23 evaluates the shipped share (zero head-on, under 0.03 on rough
stone at a grazing view, over 0.4 on a polished wet floor, never above the
reflection it pays for). I22 now also requires eye-measured fades and
eye-origin creature and particle vectors and evaluates the knee and the
rough-metal boost. Seven mutations. L2, not seen in game; performance not
measured (one extra Fresnel evaluation per terrain fragment).
