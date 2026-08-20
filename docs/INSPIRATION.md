# Notes from Dalashade

[ImperfectRain/Dalashade](https://github.com/ImperfectRain/Dalashade) is the same
author solving an adjacent problem: a Dalamud plugin that writes a ReShade
preset for the FFXIV scene the player is actually in. Reviewed for architecture
and control design; this page records what is worth taking, what is not, and
why.

## The one structural difference that decides everything else

Dalashade is **post-process only, with no ground truth**. It says so plainly:
no prepass, no render-target chain, no G-buffer, no motion vectors, no material
IDs. So a large part of its machinery exists to *infer* from image evidence what
a pixel is - `RawMaterialCandidates` guesses foliage from green organic detail,
water from broad smooth blue, skin from colour family - and then resolve the
guesses against each other in a competition stage.

Vintage Visuals patches the geometry shaders. It has the block's own
`EnumBlockMaterial`, real per-vertex normals, real UVs into an atlas it derived
itself, and the game's own cloud tile array. **None of the inference machinery
should be copied.** It is Dalashade routing around a constraint this project
does not have, and importing it would mean guessing at answers already known.

What *should* be taken is everything above that layer: how scene state becomes
shader behaviour, how contributions are bounded and reported, and what stops
several systems pressing on the same pixel at once.

---

## 1. The intent layer

Dalashade's pipeline is:

```
GameContext -> SceneIntent -> MaterialIntent -> generated uniforms -> shaders
```

Vintage Visuals' is:

```
EnvironmentState -> per-subsystem input struct -> uniforms -> shaders
```

The difference is the middle. `SceneIntent` is a **named, bounded, normalised
channel vocabulary** - `Readability`, `Atmosphere`, `HighlightProtection`,
`ShadowProtection`, `Haze`, `Wetness`, `Cold`, `Heat`, `FoliageDensity`,
`Night`, `Moonlight`, `ArtificialLight`, `AmbientDarkness`, `Sunlight`,
`OpenSkyLight`, `DayReflection`, `CombatPressure`, `CinematicPermission` - all
0..1, sitting between "what is happening" and "what any given effect does about
it".

Vintage Visuals has the two ends and nothing in the middle. `EnvironmentState`
is facts; each subsystem then re-derives its own reading of those facts. That is
fine at five subsystems and will not be at ten: every new system invents its own
idea of what "wet" or "cold" means, and they drift.

**Worth adopting**, and the shape is already half-built: `GradeStack` is
effectively an intent layer with the channels hardcoded into one consumer.

## 2. Per-contribution caps, and saying which one was capped

Dalashade caps user tag contributions at **+/-0.20 per row and +/-0.35 per
channel**, and reports each as active, inactive, invalid, or **capped**. Every
contribution is a record: `SceneIntentContribution(Source, Intent, Amount,
Reason)`.

`GradeStack` clamps only the final result. When a grade saturates there is no
way to tell which of nine influences did it - the same blindness that made the
cloud shadows take three rounds. Recording contributions costs almost nothing
and turns "the image looks wrong" into a list.

**Worth adopting, and small.** This is the cheapest item on this page.

## 3. Stack budgets: stopping the same idea from applying twice

> Current stack budgets protect bloom, AO, shadow lift, bloom dirt, and
> saturation from repeated context reductions/additions. Snow weather also
> dampens snow-biome handling so snow does not apply full-strength twice.

Vintage Visuals has this bug **today**, unnoticed. In heavy rain: adaptive
grading drops saturation and contrast, the overcast term weakens the direct
lobe and lifts ambient, and rain fog washes distance out. Three systems, one
direction, no budget between them. Each was tuned alone and looks reasonable
alone.

**Worth adopting**, and it is a real defect rather than a nicety.

## 4. Gameplay readability as a first-class constraint

Dalashade carries `combatReadable`, `dutyReadable`, `gameplayRestrained` and
`cinematicAllowed` as tags, and `GameplayDampen`, `ReadabilityDampen` and
`ReflectionDampen` as shader-side lanes. Effects yield to playability.

Vintage Visuals has **nothing** for this, and the game gives it obvious hooks: a
cave should not go so dark the player cannot navigate it, rain fog should not
hide a drifter that is about to hit them, and a temporal storm is exactly when
the screen should not also be doing something clever.

This did not appear anywhere in the ranked backlog. It should have.

## 5. A shader-facing shared vocabulary, with a rule attached

`Dalashade_FrameData.fxh` normalises the plugin's tags into HLSL lanes, and the
docs carry an explicit rule:

> When adding a new first-party shader, prefer mapping its generated scene
> uniforms into `Dalashade_FrameSceneSettings` and consuming
> `Dalashade_FrameSceneData` instead of creating local one-off meanings for
> combat, readability, wetness, heat, cold, aether, neon, day, night, or
> Standalone mode.

`pbrcore.glsl` is the same idea for the lighting *math*. The analogue for scene
*semantics* does not exist here: `weather.glsl` and `pseudopbr.glsl` each
declare their own `vv_weatherWetness`, `vv_weatherOvercast` and so on, with the
meanings agreed by convention and nothing enforcing it.

## 6. Receivers, and effect authorities

Two ideas that will matter as soon as water or emissive land.

**Receivers.** A mask saying whether a pixel may *receive* an effect at all -
reflection receiver, GI receiver, AO receiver, skin-safe - separate from what
the pixel is made of. Vintage Visuals has identity and no receiver concept.

**Authorities.** `GenerationAuthorityPolicy` detects primary and secondary
owners of a visual role - colour grade, bloom, sharpen, AO/GI - and **dampens
the secondary rather than disabling it**. Vintage Visuals has competing owners
of perceived contrast right now (ColorGrade contrast, crevice occlusion, rain
fog, the overcast term) and no arbitration. The same idea also covers
coexisting with other Vintage Story shader mods.

## 7. Two documentation habits worth stealing

- **Per-file status vocabulary** in `CodebaseIndex.md`: *Stable* (core contract,
  edit with regression checks), *Experimental* (edit narrowly, verify debug
  views), *Debug-only* (must not change visuals), *Release asset*. `STATUS.md`
  tracks features; this tracks code, and the two are not the same question.
- **Per-shader debug enums.** Every Dalashade shader has its own numbered debug
  list in its own terms - contact mask, depth edge, receiver mask, suppression
  mask, final contribution. Vintage Visuals has one global 0..13 list shared
  across subsystems, which is already crowded and will not survive water.

## 8. Detected versus effective state

If an override layer ever lands here, this is the trap Dalashade already hit:
derived buckets regenerate *after* the override runs, so a **removal** has to be
stored as an explicit suppression or it comes back. Their diagnostics show
detected and effective tags side by side so support can tell whether a tag was
automatic, added, removed, or suppressed.

## 9. MasterStyle

Point the plugin at a folder of images you like and it biases the generated
preset toward them. A genuinely novel control - "make it look like this" rather
than "set fourteen sliders" - and it fits a mod whose whole grading stack is
already numeric.

---

## What this changes about the ranked backlog

**Screen-space reflections were ranked too low (#34).** The argument was that
SSR needs depth and normal buffers this mod does not have wired, and that its
artefacts would be worse than its benefit. Dalashade ships a reflection pass
with *strictly less* to work with - no G-buffer, no material IDs, inferred
normals from a depth field - by not attempting true SSR at all: a layered,
bounded *impression* built from environment sheen, a column-projected source, a
clamped compositor, and a pseudo-SSR fallback, all gated on receiver masks.

Vintage Visuals has real geometry, real normals and real material identity. A
bounded reflection impression on water is more achievable than that ranking
credited. The objection to *true* SSR stands; the objection to reflections does
not.

## What not to take

- **Image-evidence material inference.** Solving a problem this project does not
  have, less accurately than the answer it already holds.
- **Screen-space material competition.** Same reason.
- **A tag taxonomy this large.** Dalashade needs ~40 tags because FFXIV zones
  are authored, discrete and unlabelled. Vintage Story worlds are generated and
  the game will *tell* you the rainfall, temperature and season. Continuous
  values from `EnvironmentState` are the better primitive; the lesson to take is
  the **channel layer above** them, not the taxonomy below.
