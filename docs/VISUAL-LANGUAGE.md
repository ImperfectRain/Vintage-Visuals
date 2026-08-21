# Visual language

The rules that decide whether a feature belongs in Vintage Visuals at all.

`ARCHITECTURE.md` says where code goes. This says what the image is allowed to
become. It exists because the project's real risk is no longer missing features
- it is building technically correct effects that collectively stop the game
looking like Vintage Story, or stop it being playable.

---

## 1. The information ladder

**When Vintage Story already knows something, use Vintage Story's answer.**

This is the single principle the project has learned the hard way, repeatedly.
Prefer, strictly in this order:

1. **Information the game exposes directly.** `EnumBlockMaterial`, the calendar,
   the climate, `glowLevel`, `renderFlags` wind modes, the light fields.
2. **Information derived from authoritative game data.** The cloud tile array,
   the block texture atlas.
3. **Information derived from geometry or textures.** The material atlas's
   normals and roughness.
4. **Information approximated from screen space.** Depth-derived anything.
5. **Invented simulation.** Only when nothing above exists.

Every serious bug in this project so far came from working too low on that
ladder. Three versions of a cloud-shadow noise field failed because the game
already had cloud placement (rung 2) and the mod was inventing one (rung 5).
Metalness inferred from pixels (rung 3) was wrong until it came from
`EnumBlockMaterial` (rung 1). Foliage identification could have been a colour
heuristic; it is vanilla's own wind-mode bits.

A feature proposal that sits on rung 5 must say why rungs 1-4 are unavailable.

## 2. What PBR means here

Not "make everything physically shiny." The goal is:

> **make materials communicate what they already appear to be.**

Stone should feel like stone, wood like wood, wet stone like wet stone, iron
like metal, leaves like something thin, clay like something matte. None of that
requires photorealism, and photorealism would actively cost the game its
identity.

Vintage Story's visual language is low-poly geometry, painterly textures, earthy
colour, strong environmental atmosphere, relatively soft light, deliberately
stylised skies, and a darkness that is oppressive but playable. Effects that
fight those are wrong here even when they are correct elsewhere.

## 3. Visual hierarchy

When two effects compete for the player's attention, the lower level wins.

| Level | What it is | Examples |
|---|---|---|
| **1. World readability** | Terrain, structures, entities, threats, resources, paths, light sources | never negotiable |
| **2. Material readability** | Stone vs clay, dry vs wet, metal vs wood, natural vs made | the actual product |
| **3. Environmental mood** | Weather, time, season, biome, atmospheric depth, cloud | what makes it feel alive |
| **4. Physical detail** | Micro-normal, specular, crevices, translucency | what makes it feel real |
| **5. Cinematic** | Bloom, DOF, lens effects, temporal | garnish |

**Levels 1 and 2 are the product. Level 5 is garnish.** A roadmap that ranks a
level 5 idea beside a level 2 idea has made a category error, and the first
ranked backlog in this repo did exactly that.

## 4. Budgets

Every effect that takes something away shares an allowance with everything else
that takes the same thing away. This is enforced in code by `VisualBudget`; it
is stated here because it is a product rule rather than an implementation
detail.

| Budget | Claimants |
|---|---|
| **Contrast** | grading, weather, crevice occlusion, AO, lighting |
| **Saturation** | biome, weather, night, atmosphere, grading |
| **Darkness** | shadows, cave exposure, weather, AO, crevice |
| **Highlight** | sun, block light, emissive, wetness, reflections |

They cannot all independently say "more". Rain plus overcast plus a cold biome
plus night plus underground plus wetness is six individually sensible modifiers
producing one absurd image, and that is the default outcome without a budget.

## 5. Readability is a hard constraint

There are situations where the prettiest image is the wrong image.

| Scene | Beautiful | Unplayable |
|---|---|---|
| Heavy rain | foggy, muted, atmospheric | a drifter you cannot see |
| Cave | nearly pitch black | terrain you cannot read |
| Night | true scotopic darkness | frustrating rather than tense |
| Storm | dramatic exposure shifts | flashes that break navigation |

The renderer has to know when to stop being clever. `IntentChannel.Restraint`
and `IntentChannel.Readability` carry that, and everything that removes light,
colour or contrast is scaled by them.

Readability is **context-aware**, not a toggle. Something hostile nearby is a
reason for the weather to stop hiding it - not by outlining the threat, which
would be a different game, but by declining to obscure information the player
needs.

## 6. Warm and cold spaces

Vintage Story's loop is leaving warm lit shelter for cold dark wilderness, and
descending from surface to cave to deep underground. The renderer should
reinforce that, because it is the game's design language and not merely
scenery.

| Space | Reads as |
|---|---|
| Shelter | warm local light, softer contrast, visible material detail, warm reflections |
| Wilderness | cooler ambient, stronger aerial perspective, wind, weather, broad sky light |
| Cave | darkness punctuated by local light, high material contrast near sources, little sky |
| Deep cave | almost no ambient, artificial light visually dominant, subtle colour separation |

## 7. Environmental layering

Environmental states stack in a fixed order and **transition** rather than
overwrite:

```
BaseMaterial -> Season -> Temperature -> Wetness -> Snow -> Frost -> Lighting
```

Snow falling on wet stone should move wetness down as snow coverage rises, not
replace the material outright. This matters more the more environmental
features exist, which is to say it will matter soon.

## 8. What this project does not build

Not every physically plausible technique is aesthetically compatible with this
game. The following need a Vintage-Story-specific justification, not merely a
working implementation:

chromatic aberration · heavy film grain · strong vignette · motion blur ·
cinematic depth of field · lens dirt · aggressive bloom · parallax mapping ·
hyper-real sky scattering

The test is not "does it look good in a screenshot". It is "does the game still
look like itself while being played".

## 9. Every feature declares its invariants

The recurring failure in this project is **silent semantic failure**: the code
compiled, the tests passed, and the feature was wrong in game. A uniform
declared and never uploaded. A patch that compiled but never executed. A zero
that meant "delete all clouds". A clock that lost precision at world scale.

So every shader feature states what must be true when it is off:

| Setting | Invariant |
|---|---|
| `Weather.Enabled = false` | vanilla shader source, not muted patched source |
| `Wetness = 0` | exactly the vanilla material response |
| `CloudShadowStrength = 0` | exactly the vanilla lighting |
| `NormalStrength = 0` | the vanilla normal |
| `EmissiveStrength = 0` | no emission contribution at all |

And diagnostics report **PATCHED / BOUND / ACTIVE / CONTRIBUTING**, not merely
"shader compiled". A binder that silently returns is indistinguishable from one
that works, and this project has lost several rounds to exactly that.

## 10. The target

The question to ask of any feature is not "what effect can we add", but:

> What does Vintage Story already know, how should that change what the player
> perceives, and what is the **smallest** rendering intervention that makes it
> legible?

What the finished thing should feel like:

| | Not | But |
|---|---|---|
| Summer afternoon | brighter | warm, expansive, textured, alive |
| Rain | a grey filter | wet materials, muted distance, soft light, disturbed surfaces, darker foliage, clouds actually changing the light |
| Sunset | an orange LUT | low warm directional light, a longer atmospheric path, cool shadows |
| Cave | a black screen | darkness punctuated by physically coherent local light |
| Snow | a white overlay | a world whose materials changed because winter happened |
| Forge | an orange texture | a hot object lighting the room |
| Forest | green blocks | thin foliage transmitting light, moving, wet, seasonal |
