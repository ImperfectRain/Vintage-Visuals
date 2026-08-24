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
`tools/smoketest/SceneInvariantChecks.cs`, ten invariants I1-I10, each naming the
defect it exists to prevent and the commit that fixed it. An invariant with no
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

**Status.** Ten invariants, all passing, all mutation-proven. Found one live
disagreement on its first run: see D39.

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

Twelve rows today - the seven runtime defects plus five that are the next
instance of a class that has already bitten once - and twelve caught.

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

**Status.** 12 mutations, 12 caught, 0 missed. Run it with
`VINTAGE_STORY=... bash tools/mutate/mutation-test.sh` on a clean tree.

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

**Confirmed: the luminance ceiling is a compression rule wearing an energy
rule's clothes.** A scene hit above `envLuma * 1.2` is scaled to it. Environment
0.20 against a sunlit tree at 0.70 delivers 0.24 - two thirds of the tree's
luminance gone before PBR sees it - and a sunlit surface is legitimately allowed
to be several times brighter than the local horizon colour.

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

**Status.** L2. The defaults have not been seen in a game. 935 checks, 25 of 25
mutations caught.
