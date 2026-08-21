# PseudoPBR

Derives normal, roughness and specular-mask data from vanilla diffuse textures,
so materials respond to light differently without anyone hand-authoring maps
per block.

## Status

**Phase 4, surface relief wired to the screen.** The mod works out what material
every block is made of, derives a material atlas from the block textures, and
feeds it to the chunk shader so the game's own lighting shades real surface
relief instead of flat faces.

Not yet verified in game — see [Verification](#verification) below for exactly
what that means and what is still unconfirmed.

| Piece | State |
|---|---|
| Offline prototype (`tools/pbrgen`) | done, 31 tests |
| C# port of the three passes | done, 21 parity checks |
| Material classification + profile table | done, 36 checks |
| Material report (`material-report.txt`) | done, run against 14090 blocks |
| Derived material atlas | done, 29 checks |
| Disk cache keyed by content fingerprint | done |
| Preview images for inspection | done, one set per atlas page |
| Multi-page block atlases | done, level 2 (compiles) |
| Atlas uploaded to the GPU | done, level 2 (compiles) |
| `chunkopaque.fsh` samples it for normals | done, level 2 (compiles), 49 checks |
| Cook-Torrance specular + energy conservation | done, level 2 (compiles) |
| Per-layer debug views | done, level 2 (compiles) |
| `chunktopsoil.fsh` (grass, dirt tops) | not started |
| Roughness modulates SSR blur | not started (needs Phase 3) |

## The atlas

`MaterialAtlasSource` walks every block, finds each of its textures in the game's
block atlas, loads the source PNG, and pairs it with the block's material
profile. `MaterialAtlasBuilder` derives the maps and packs them into one texture
laid out to match the block atlas exactly — so the shader can sample it with the
UVs the block already has, and no new UV plumbing is needed.

Channel packing, the decision everything downstream depends on:

| Channel | Holds |
|---|---|
| R | tangent-space normal X (0.5 flat) |
| G | tangent-space normal Y (0.5 flat) |
| B | roughness |
| A | specular reflectance |

Normal Z is not stored; it is reconstructed in the shader as
`sqrt(1 - x² - y²)`, which is exact for a unit normal and buys the fourth
channel for something that cannot be derived.

**Metalness is deliberately absent**, even though `MaterialProfile` carries it.
There are five values worth storing and four channels. Metalness is the one that
drops with least visible loss — its main effect is tinting the specular
highlight by albedo rather than white, a refinement on top of "is this shiny at
all". Storing it needs either a second atlas (a whole extra texture unit) or a
bit-packing trick that bilinear filtering would destroy. Revisit if metal ends
up looking like shiny plastic; the fix is a second atlas, not a cleverer pack.

Two details that are easy to get wrong:

- **Gaps are filled with a neutral texel** — flat normal, default roughness, no
  specular. Any part of the atlas no texture covers, and any texture that failed
  to process, then shades like an ordinary matte surface rather than like a hole.
- **Each region is derived with tiling on**, so gradients wrap within that one
  texture instead of bleeding into whatever unrelated texture happens to sit
  beside it in the atlas.

Textures are deduplicated by `TextureSubId`: every fence variant of a wood type
points at the same planks, and deriving it per block would be thousands of
redundant Sobel passes over identical pixels.

### Every baked texture, not just the base

A `CompositeTexture` with `Alternates` bakes **one variant per alternate**, each
with its own `TextureSubId` and its own slot in the atlas, and Vintage Story
uses alternates heavily for natural blocks — granite being the obvious one.
Walking only `composite.Baked` left every variant slot at the neutral texel,
which in game is a granite block with no surface at all sitting next to one that
has it. Tiled textures bake the same way.

Two consequences for how they are reached:

- **`Positions` is indexed by `TextureSubId`.** `GetPosition(block, textureCode)`
  answers for the composite's base and knows nothing about its alternates, so a
  variant's slot is only reachable through the index.
- **Each variant resolves its own file** via `TextureFilenames[0]`, its own base
  path, rather than inheriting whatever the composite's base happened to be.

The collector now reports **coverage** — how many of the page's allocated slots
it actually filled — because "3749 collected, 0 skipped" reads like success and
says nothing about textures that were never enumerated at all. That is exactly
how a whole class of blocks came to render with no surface while the log looked
clean.

## Resolving a block's texture file

Two traps, both of which returned null silently on all 3749 textures of a real
atlas before they were found:

- **Texture locations carry no category or extension.** A block declares
  `block/stone/granite`; the asset is `textures/block/stone/granite.png`. They
  must be resolved with `WithPathPrefixOnce("textures/")` and
  `WithPathAppendixOnce(".png")` — the same pattern the game's own
  `ITextureSource` uses.
- **`BakedName` is not a filename.** `CompositeTexture.Bake` appends synthetic
  suffixes for overlays (`++`), rotation (`@`) and alpha (`~`), so a baked name
  can describe a composite that exists only inside the atlas with no file
  behind it.

Resolution therefore goes through `CompositeTexture.Base`. The cost is that
overlays, rotation and alpha are not composited into the derived maps — material
response comes from the base texture. For roughness and surface relief that is a
fair approximation; if an overlaid block ever looks wrong, this is why.

Skips are counted **by reason**, with an example block for each. A bare
"3749 skipped" says something is wrong but not what, and a fail-soft path that
swallows its own cause turns a two-minute fix into a debugging round.

## Cache and previews

The built atlas is cached to `VintagestoryData/VintageVisuals/material-atlas-0.bin`,
keyed by an FNV-1a fingerprint over the atlas dimensions, every region's
rectangle and profile, and the source pixels themselves. A game update, an added
mod, a retexture or a profile retune all invalidate it automatically. Writes go
to a temporary file and are moved into place, so a crash mid-write cannot leave a
half-atlas that passes its own header check. A corrupt, truncated or stale cache
reads as "rebuild", never as an exception.

`WriteAtlasPreview` also writes three viewable PNGs — normal, roughness and
specular, separately, because a packed RGBA image is unreadable to a human. Same
reasoning as the offline tool's contact sheet: these maps cannot be judged from
statistics, and "do the log grooves read as grooves" is a question for eyes.

These were BMP first, which was simpler to write and the wrong choice: BMP is
awkward to share, does not preview in most tools, and several upload paths
reject it outright. An image nobody can send you is not a diagnostic. The PNG
writer is hand-rolled with stored (uncompressed) deflate blocks — about sixty
lines, no dependency — and is verified pixel-exact against an independent
decoder on deliberately awkward dimensions.

`pbr-diagnostics.txt` lands in the same folder and records what the pipeline
did: texture counts, skip reasons with examples, atlas size, timing, cache hit
or miss. It duplicates the log on purpose. The log is the right primary sink,
but the outputs live here, this is the folder someone opens when something
looks wrong, and `client-main.txt` is elsewhere among thousands of unrelated
lines — so the folder should contain its own answer.

## Reaching the screen

The relief effect is deliberately the smallest possible intervention in the
render pipeline. There is **no second lighting model** and no extra pass — the
game already shades by normal, so it is given a better one:

```glsl
outColor = applyFogAndShadowFromBrightness(texColor, clamp(fogAmount - 50*murkiness, 0, 1),
                                           min(b, vvSurfaceBrightness(nb, normal, uv)), worldPos.xyz);
```

Two facts about 1.22.7's `chunkopaque.fsh` shaped that line, and both were
guessed wrong before the real file was read:

- **`#include` directives are already expanded** when the mod sees the source.
  The shader arrives as one flat ~1180-line file with `vertexflagbits.ash`,
  `fogandlight.fsh` and `colormap.fsh` inlined. So `fogandlight.fsh` is not a
  separate patch target — and `uniform vec3 lightPosition` sits around line 171,
  which means injecting at the top of the file puts our code *above* the uniform
  it depends on. The snippet is therefore anchored on that declaration: one
  anchor both places the code in scope and asserts the dependency. It also has
  to paste the declaration back, since a token patch replaces its anchor.
- **Normal shading moved to the vertex shader.** 1.22.7 never calls
  `applyFogAndShadowWithNormal`; the function is still defined but dead. `main()`
  reads a per-vertex `nb` varying instead. Perturbing `normal` in the fragment
  shader would have changed nothing at all — the value that has to be modulated
  is `nb`.

`vvSurfaceBrightness` therefore returns `nb` plus the *difference* the perturbed
normal makes to vanilla's own directional term, not a replacement for it. A
difference, for two reasons: `nb`'s absolute value already carries whatever
`normalShadeIntensity` and `minNormalShade` the vertex shader chose — values
this shader cannot see and should not guess — and a difference is exactly zero
where the atlas is flat, so every texture the mod failed to process renders
precisely as vanilla.

`nb` is wrapped rather than the surrounding `min(b, nb)`, so the shadow map
keeps its authority and relief cannot light up a face that is in shadow.

The delta uses only the directional half of vanilla's `getBrightnessFromNormal`,
deliberately dropping its `max(nb, normal.y * 0.95)` sky-bounce term. That term
saturates every upward-facing surface at 0.95; including it would make floors
and ground the one place relief could never appear, which is most of what a
player looks at.

Everything else exists to make that line possible:

- `MaterialAtlasTexture` holds the derived atlas. Its pixels and its GL texture
  have separate lifetimes on purpose: the pixels are produced during asset load,
  and the **upload happens on the render thread**, at a stage this mod chose.
  Creating a texture binds it to the active unit as a side effect, so uploading
  from an asset-load event means clobbering whatever the game had bound there.
- `PbrShaderBinder` is an `IRenderer` that draws nothing and owns every piece of
  GL work here — the upload, the re-bind and the uniforms — once per frame at
  render order **0.35**, the gap immediately before terrain opaque (0.37).
  Once-at-startup would be cheaper, but texture unit bindings are global GL
  state this mod does not own.
- **Off means off.** While disabled the binder applies no patch, uploads
  nothing, binds nothing, and *releases* the texture — because a texture that
  exists stays bound to its unit. A flag that leaves shared GL state occupied
  gives the player no way to rule this subsystem out, which is exactly what they
  need when something looks wrong.
- **Every path that declines to act says so, once.** A renderer that silently
  returns is indistinguishable from one that is working. Twice this subsystem
  was debugged from a screenshot because the log had nothing to say; each
  precondition now names itself when it fails.
- `vvTangentFrame` builds the tangent basis. Chunk geometry carries no tangents,
  and does not need to: every block face is axis-aligned, so one consistent
  frame per axis is exact rather than approximate.
- The full `applyFogAndShadowFromBrightness` statement is the anchor rather than
  the `min(b, nb)` fragment inside it, because that fragment appears twice in the
  file and the patch engine rejects an ambiguous anchor instead of guessing.

`vv_pbrEnabled` follows the same defensive shape as `vv_enabled` in ColorGrade —
an unset GLSL uniform reads as exactly `0`, so a failure to bind lands on the
same branch as a deliberate disable and renders vanilla normals. The one thing
that must never happen here is a black world: `chunkopaque.fsh` draws the
terrain, so unlike every other patch in this mod, a failure that reached the
GLSL compiler would cost the player everything, not one effect.

### Single-page atlases only

The shader samples our atlas with the diffuse's own `uv`, which works because
both atlases share a layout. When the block atlas needs more than one page the
game binds a different terrain texture per draw call, and `uv` alone no longer
says which page a fragment came from — and there is nowhere left in the vertex
format to tell us (see [Why not vertex flags](#why-not-vertex-flags)).

So a multi-page atlas switches the relief off and logs why. Vanilla renders
untouched; the report, the atlas and the previews are still produced. A 1.22.7
install packs ~3700 block textures into a single 4096×2048 page with room to
spare, so this is a guard for heavily modded installs, not the common case.
Lifting it means one material atlas per page plus a hook into the chunk draw
call to bind alongside whichever terrain page is active.

## Two things that make relief read as texture rather than smear

Both were learned from looking at it in game, and neither is obvious from the
maths.

**Nearest sampling, enforced twice.** The atlas is uploaded with
`linearMag: false`, *and* the shader snaps every lookup to the texel centre with
`vvSnapToTexel`. Belt and braces on purpose: the filter mode is set through the
game's texture API on a texture this mod does not fully own, and a round of
"still looks muddy" was spent unable to tell from a screenshot whether the
setting had taken. Snapping makes it nearest by construction, for one
`textureSize` and a `floor`.

**Derived maps fill the allocated slot, not the source's size.** The atlas
stores textures at whatever size the game chose, which is not necessarily the
source PNG's - a texture pack or an upscaled atlas makes them diverge. Writing
source-sized data into a differently sized slot puts the relief at the wrong
scale, so it stops lining up with the texture it came from, which looks like
"soft and muddy" rather than like a bug. The builder maps slot texels back to
source texels with nearest sampling, and the collector reports how many textures
needed rescaling.

**Nearest filtering, not linear.** The atlas was uploaded with `linearMag: true`
on the reasoning that magnified normals want to be smooth. That reasoning
belongs to a game with high-resolution art. Vintage Story is pixel art: the
diffuse is sampled with nearest, so one texel covers a large, flat, hard-edged
patch of screen. Filtering the *normal* linearly across that patch makes the
lighting roll smoothly over a surface whose colour steps sharply, and the two
disagreeing is exactly what "soft and gloopy" looks like - stone reading as wet
clay, because the shading says curved while the texture says blocky. Nearest
keeps one normal per texel so relief lands on the pixel grid.

**Relief fades out with distance.** Nearest minification aliases harder than
linear did, and with no mipmaps on the atlas, one screen pixel at range covers
many texels and picks one essentially at random - which crawls as the camera
moves. Relief is a close-up detail anyway; past a few blocks the eye reads
silhouette and colour, not grain. `VV_DETAIL_FULL` (16 blocks) to
`VV_DETAIL_NONE` (48) costs one `length()` and removes the aliasing problem
rather than managing it.

## Adjusting the look

Seven live uniforms, no rebuild. They fall into three groups by what they are
actually for.

| Slider | Range | Default | For |
|---|---|---|---|
| Surface relief strength | 0 – 2 | 1.0 | taste |
| Roughness bias | -0.5 – 0.5 | 0.0 | style |
| Metal response | 0 – 1 | 1.0 | style |
| Specular strength | 0 – 2 | 1.0 | taste |
| Sky reflection | 0 – 2 | 0.35 | realism |
| Specular antialiasing | 0 – 2 | 1.0 | correctness |
| Detail distance | 4 – 192 | 48 | performance/quality |
| Torch & lava highlights | 0 – 2 | 1.0 | realism |
| Torch highlight directionality | 0 – 1 | 0.7 | taste |

**Roughness bias** is the strongest single style control, because roughness is
what separates a look that reads as *wet* from one that reads as *dry* — and
where that line sits is taste, not physics.

**Metal response** dials the whole metalness stand-in. At 0 every surface is a
dielectric with a white highlight; at 1 metals tint their highlight by their own
albedo. A coloured specular is one of the strongest "modern renderer" cues, so
turning it down is the fastest route to a flatter, more stylised image.

**Sky reflection** is the cheapest single step toward realism here. Without it
the sun is the only light this shader knows about, so a metal block in shade or
indoors has no highlight at all and reads as dark plastic. There is no
reflection probe to sample, so vanilla's own fog colour stands in — it is
already the colour the horizon is being blended toward, which makes it a better
environment estimate than any constant. It is deliberately **not** multiplied by
the shadow map: sky light reaches surfaces the sun does not, and killing it in
shadow is what makes metal in a doorway look like painted wood.

### Light that is not the sun

Until this existed, `lightPosition` was the only light the material system knew
about — so underground, where every light is a torch, none of it did anything. A
metal wall beside a lantern had no highlight; the same wall in daylight did.

Vanilla bakes every torch, lantern, lava pool and glowing block into a single
per-vertex colour, `blockLight`. That gives intensity and hue but **no
position**, and a specular highlight needs a direction.

The direction is recoverable without the CPU ever telling us where the lights
are. Block light is a scalar field over the surface, and **the gradient of that
field points toward whatever is emitting it**. Both pieces are already to hand —
screen-space derivatives of the light and of the world position — so
`vvBlockLightDirection` solves for the world-space gradient consistent with
both (Mikkelsen's surface-gradient construction, which lands it in the tangent
plane by construction) and tilts the normal toward it. The tilt grows with the
gradient's magnitude: a steep falloff means the source is close and therefore
off to one side, a flat one means it is distant or genuinely ambient.

Degenerate cases — a uniform light field, a degenerate derivative basis — fall
back to the normal, which *is* "treat this as ambient" and is the right answer
in exactly those cases.

Two consequences worth stating:

- The highlight is **not** scaled by the shadow map or by daylight. A torch
  burns in a cave at midnight, and gating it on either would remove the
  highlight from precisely the places this exists to light.
- `BlockLightDirectionality` exists because this is an estimate. At 0 block
  light is treated as purely ambient — safe and dull. At 1 the estimate is
  trusted fully, which gives a highlight that tracks a torch as you walk past
  it, and can wobble where the light field is noisy. Debug view 9 shows the
  estimated direction directly.

### Specular antialiasing

Not taste — this one is closer to a correctness fix, and it exists because of
what this mod does to a surface.

The normals here are *derived from texture detail*, so they carry far higher
frequencies than a hand-authored map. A normal that changes faster than one
screen pixel makes the specular lobe flicker as the camera moves, because each
pixel samples a different microfacet orientation every frame. Rough stone with a
tight highlight is the worst case, and it is exactly the "rocks look shiny and
sparkly" complaint.

The standard answer is **not** to smooth the normal — that discards the detail
the mod exists to add — but to widen the lobe by however much the normal varies
inside the pixel, measured from its screen-space derivative. A surface whose
normals scatter within one pixel genuinely *is* rougher at that scale; this
makes the shading model agree with that. Following Kaplanyan et al. (2016) and
the simplified kernel of Tokuyoshi and Kaplanyan (2019), with the kernel clamped
because the screen-space derivative estimate degrades badly at grazing angles
and silhouettes — the known weakness of this form.

The widening happens in *alpha* (roughness squared), which is the space the NDF
integrates over; adding to roughness directly would widen smooth surfaces far
more than rough ones.

Debug view 8 shows the roughness the model actually uses. Where it is much
brighter than view 2, a surface was sparkling and is now being held down.

### A note on the F7 panel

Every slider above is declared in `assets/vintagevisuals/config/configlib-patches.json`,
which is data nothing validates at build time. Two settings sharing a `weight`
empties the entire panel - every mod setting, not just the two - and neither the
build nor the log says a word. `tools/smoketest` now checks weights, codes and
ranges, and that every setting has a matching `case` in `ConfigLibBridge` in
both directions.

## Debug views

`PseudoPBR.DebugView` replaces the finished image with one layer of the material
system. It applies live — through <kbd>Ctrl</kbd>+<kbd>V</kbd> or the F7 slider
— because it is a uniform, not a patch decision.

| Value | Shows | What it tells you |
|---|---|---|
| 0 | normal rendering | — |
| 1 | normal map as stored, blue forced flat | Should match the preview PNG exactly. Block textures here mean `vv_materialTex` is not reading the atlas |
| 2 | roughness | Metal and water dark, soil and gravel near-white, brick mid-grey |
| 3 | specular mask | Water, glass and metal bright; soil, wood and leaves near-black |
| 4 | relief contribution alone, mid-grey biased | Whether the relief is subtle or overdriven, with lighting isolated from albedo |
| 5 | specular highlight alone | Where highlights land, without albedo hiding them |
| 6 | perturbed normal in world space | The six block faces should read as six flat colours, with relief as variation inside each |
| 7 | reflectance at normal incidence | Grey is dielectric, coloured is metal — whether the metalness stand-in behaves |
| 8 | roughness as shaded | Stored roughness plus bias plus specular antialiasing. Brighter than view 2 means aliasing is being suppressed there |
| 9 | estimated block-light direction | Flat means the gradient found nothing and the light is ambient; variation across a wall means a torch is being located |

Views 1 and 6 are the pair worth understanding: view 1 is what the atlas
*stores*, view 6 is what the shader *derives from it* after the tangent frame is
applied. If 1 looks right and 6 does not, the tangent frame is wrong; if 1 is
already wrong, nothing downstream can be right.

This is not just developer scaffolding. Four derived quantities are stacked on
top of each other here, and when the composite looks wrong there is no way from
the finished image to tell which layer is at fault. View 1 is also the fastest
possible check for a sampler problem — the class of bug that has cost this
subsystem the most time.

## Specular

The atlas's roughness and specular channels feed a Blinn-Phong highlight added
at the same point vanilla composes its lit colour, so it is shadowed, fogged and
day-scaled by the same terms.

Blinn-Phong rather than a microfacet BRDF, deliberately. The inputs are
*derived*, not measured — roughness from local texture variance, the specular
mask from flood-filled colour regions — and evaluating approximate data
precisely buys nothing that a half-vector power does not, while costing a square
root and two divisions on every terrain fragment.

The exponent is what actually reads as material: `mix(256, 4, roughness)`, so
polished metal gets a tight glint and soil gets a broad soft sheen from the same
four lines. Everything that should suppress a highlight does — facing away from
the sun, shadow, night, fog — and the `dot(n, l)` term matters most: without it
a surface lit from behind catches a rim highlight, which reads as glass.

## The two sources of material data

This is the central design decision, and it resolves the limitation the offline
prototype could never get past.

`tools/pbrgen` infers detail from pixels and is good at it — but it cannot
*classify*. A grey texture might be polished steel or unlit granite, and no
filter can tell the difference, which is why a gold block comes out matte to it.

Vintage Story already knows. Every block carries an `EnumBlockMaterial`:
`Metal`, `Stone`, `Wood`, `Water`, `Soil`, `Leaves`, `Glass`, `Ice`, `Ore` and
more. It is authoritative, free, and covers modded blocks too.

So the two sources split by what each is actually good for:

| Source | Answers | Example |
|---|---|---|
| `EnumBlockMaterial` | **what** it is | this is metal, so it is reflective |
| Texture pixels | **where** the detail is | these texels are the grooves in the log |

`MaterialProfiles.Combine` merges them with a deliberate split of authority:

- **Metalness** comes from the block material alone. Pixels cannot recover it.
- **Roughness**: the material sets the centre, texture variance nudges it within
  `RoughnessVariation`. All stone is rough; pitted stone is rougher than cut.
- **Specular**: the material sets the *ceiling* and a floor at half of it; the
  texture decides where between. Not a raw multiply — a derived mask of zero
  would kill a metal block's highlight entirely, and the material type is the
  more trustworthy signal.

## Why not vertex flags

The obvious alternative is tagging faces at tesselation time with a material id
in `VertexFlags`. It was rejected on evidence: the field is fully packed —
glow 0-7, zoffset 8-10, reflective 11, lod0 12, normal 13-24, wind 25-31. There
is no free bit for a material id, and the single existing `ReflectiveBitMask` is
already claimed by other shader mods for ice and glass.

That leaves the atlas route: material data has to travel through a texture keyed
by the same UVs the block already uses.

## What a real registry showed

Run against a 1.22.7 client with mods: **14,090 blocks, 20 materials, every one
classified** — no fallbacks. Distribution, largest first:

| Material | Blocks | Share |
|---|---|---|
| Wood | 4189 | 30% |
| Stone | 3513 | 25% |
| Ceramic | 2035 | 14% |
| Plant | 1053 | 7% |
| Metal | 766 | 5% |
| Ore | 537 | 4% |
| Cloth / Gravel / Water / Leaves / Soil | 335–230 | ~9% |
| Glass / Sand / Other / Snow / Lava | 159–70 | ~4% |
| Meta / Ice / Mantle / Fire | 17 | <1% |

Two findings changed the table, and neither was guessable without the data:

- **Ceramic was mis-tuned, and it is 14% of the world.** The first values
  assumed glazed pottery (roughness 0.45, specular 0.60). The registry says the
  bucket is brickwork and roofing — brick stairs, brick slabs, brick courses,
  clay shingles, slanted roofing, tool molds — all matte fired clay. The
  original numbers would have given the game permanently wet-looking roofs
  across a seventh of every build. Retuned to 0.70 / 0.30, just slightly
  smoother than raw stone.
- **`EnumBlockMaterial.Brick` is assigned to zero blocks.** Vintage Story files
  all of its brickwork under Ceramic. The entry is kept for content mods and
  deliberately matched to Ceramic's values, so two identical-looking walls
  cannot shade differently.

`Other` accounts for 118 blocks (0.8%) — oil lamps, skeps, beehives, candles,
cobwebs. All small props where the neutral default is the right answer, so no
special casing is warranted.

## The material report

`PseudoPBR.WriteMaterialReport` (on by default) writes
`VintagestoryData/VintageVisuals/material-report.txt` — every loaded block, its
material, its profile, and a summary ordered by how many blocks share each
material.

It exists *before* the rendering work on purpose. Classification is what the
atlas and lighting model will both be built on, and it is much cheaper to read a
list and spot that a third of the world landed in `Other` than to build two more
stages and then wonder why everything looks flat. It also surfaces modded
blocks, which no amount of testing against vanilla would reveal.

Read-only: it inspects the block registry and writes a text file.

## This is a port, not a reimplementation

`tools/pbrgen/pbrgen.py` is the reference. Its constants were tuned against
measured output across a 16-texture sample set, and its behaviour is pinned by
31 tests. `PbrMapGenerator.cs` mirrors it operation for operation.

That relationship only holds if something enforces it, so
`tools/smoketest` recomputes a fixed input in both languages and compares:
every tuning constant, plus mean/min/max and seven pinned texels per map.
Statistics alone would miss a transposed or mirrored map; the pinned texels
catch an axis flip.

**If you change a constant, change it in both places and re-run both suites.**
A divergence means the tool people tune with no longer predicts what the mod
does — which is worse than either being wrong on its own, because it is silent.

```sh
python3 -m pytest tools/pbrgen/          # the reference behaves as designed
python3 tools/pbrgen/parity_fixture.py   # regenerate the fixture after a retune
dotnet run --project tools/smoketest     # the port still matches the reference
```

## The three passes

| Pass | Method | Output |
|---|---|---|
| Normal | Sobel gradient of linear-light luminance as a height field | RGB, OpenGL convention (+Y up) |
| Roughness | Local standard deviation of luminance, absolute mapping | greyscale, floored at 0.25 |
| Spec mask | Flood fill into same-colour regions, one value per region | greyscale |

All three wrap at texture edges, because block textures tile. Clamping instead
puts a ring of wrong values around every block face.

Full rationale for each — including why roughness is *not* normalised per
texture, and why the flood fill compares against the seed colour rather than a
running mean — is in [tools/pbrgen/README.md](../../tools/pbrgen/README.md).

## Known limitations

These are inherent to inferring material data from pixels that never carried
it, and they carry over from the prototype unchanged:

- **Sobel cannot tell painted shading from geometry.** A painted highlight
  becomes a bulge.
- **Variance keys on contrast, not detail density.** A busy but low-contrast
  weave reads as smooth; a finely detailed glossy tile reads as rough.
- **Hard albedo edges produce rough halos**, because the variance window
  straddles the boundary.
- **Albedo does not carry metalness.** A gold block comes out matte, because
  the pass keys on *desaturated* brightness. Fixing this needs per-block
  metadata, not a better filter.
- **Constants were tuned on synthetic stand-ins**, not on Vintage Story's real
  textures. Re-run the sweep on real assets before shipping this.

## Verification

Per [CLAUDE.md](../../CLAUDE.md)'s ladder, honestly:

| Piece | Level |
|---|---|
| Classification, report, atlas build, disk cache, previews | **3 (loads)** — run against a real 14,090-block registry and a real 4096×2048 atlas |
| GPU upload and per-frame binding | **2 (compiles)** |
| `chunkopaque.fsh` patch | **2 (compiles)** — all six anchors matched against the real 1.22.7 shader, result compiles as GLSL, 49 smoke-test checks |
| Relief visible in game | **not reached** |

The anchors *are* confirmed, and confirmed against the real thing rather than a
stand-in. `EnableShaderDebugDump` produced the game's own `chunkopaque.fsh`
exactly as it reaches the GLSL compiler; the patch engine was run over that file
and all four anchors matched, in the right order, producing source that
`glslangValidator` accepts. Vanilla and patched were each compiled across all
**48** combinations of `SSAOLEVEL`, `SHADOWQUALITY`, `GODRAYS`, `NORMALVIEW` and
`SHINYEFFECT`; both are clean in all 48, so the patch introduces no error in any
graphics-settings configuration. That is as far as verification goes without a
GPU: it proves the shader compiles, not that the relief looks right.

Three things remain genuinely unconfirmed:

1. **The sampler-ordering fix has not been seen working.** The reasoning is
   solid and the ordering is pinned by a test, but link-time unit assignment
   happens in the driver and no check this repo can run reaches it. Debug view 1
   answers it in one glance.
2. **Nothing has been looked at on screen.** Whether the relief reads as grooves
   rather than as noise, and whether the default strengths are right, are
   questions for eyes.
3. **Texture unit 15 is a convention, not a reservation.** If the game or
   another mod binds something else there mid-frame, the relief samples the
   wrong texture. Re-binding every frame before terrain draws makes this
   unlikely rather than impossible.

### Sampler declaration order, and what it cost

The one that took two rounds to find, because the symptom pointed nowhere near
the cause.

`vv_materialTex` was first declared near the top of the file. Vanilla declares
seven samplers — `terrainTex`, `terrainTexLinear`, `shadowMapFar`,
`shadowMapNear`, `glow`, `sky`, `liquidDepth` — and inserting one above them
shifts the link-time texture unit of every sampler below it. `liquidDepth` fell
off the end and read a unit nothing was ever bound to.

Nothing about that says "sampler" from inside the game. What it says is:

```glsl
float getUnderwaterMurkiness() { ... }                        // saturates to 1
outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness); // mix(color, waterMurkColor * 0.4, 1.0)
```

Every fragment this shader draws becomes flat water murk. In game that reads as
a world gone transparent — and the only thing left with colour is the grass
tops, because those come from `chunktopsoil.fsh`, which this mod does not patch.
Both reported failures were this one cause; only the water colour differed
between them.

Three things follow, and all three are now enforced:

- **The injection anchors on `uniform sampler2D liquidDepth`** — the last
  sampler vanilla declares — so ours is always declared after all of them. That
  position is not a free choice and the patch file says so.
- **A smoke test pins the ordering** against a fixture carrying all seven
  vanilla samplers in their real order, so a future edit that moves the
  injection fails a check rather than a world.
- **The config flag gates the patch, not the effect.** With `Enabled: false`
  the group is never handed to the patcher, so `chunkopaque.fsh` reaches the
  compiler as vanilla source. A flag that merely mutes an effect is no use when
  the damage comes from the patched source existing at all.

The deeper lesson: `glslangValidator` passed this in all 48 settings
combinations, and was right to — the shader compiles perfectly. Unit assignment
happens at link time, in the driver, from the program's active sampler list.
A compiler check cannot see it, and neither can any test this repo can run
without a GPU.

### The unit-6 collision, and what it cost

Worth writing down, because the failure mode was so much worse than the mistake.

The atlas was first bound to texture unit 6, picked as "well clear of what
vanilla uses" without counting. Vanilla `chunkopaque.fsh` declares **seven**
samplers — `terrainTex`, `terrainTexLinear`, `shadowMapFar`, `shadowMapNear`,
`glow`, `sky`, `liquidDepth` — so units 0..6 were all taken, and this overwrote
one of them.

The result was not a missing effect. It was a broken world: with `liquidDepth`
reading the material atlas, `getUnderwaterMurkiness()` saturates to 1
everywhere, and `applyUnderwaterEffects` then mixes the *entire frame* to the
water murk colour. The whole screen came out sepia.

Two lessons, both now in the code:

- **Count, do not estimate.** The number was sitting in a file already on disk.
  A shader's sampler count is not a thing to have an intuition about.
- **An off switch that still binds is not an off switch.** The binder used to
  bind every frame and merely vary the uniform, so `PseudoPBR.Enabled: false`
  kept occupying the unit. At the exact moment the config flag was the player's
  only escape from a corrupted frame, it did nothing and only a restart helped.
  Whatever else a renderer does, "off" has to mean it touches no shared state.

## Known weakness: albedo is not height

The normal pass runs a Sobel over the texture's luminance, which assumes darker
means lower — the "dark is deep" heuristic. It is the standard way to get a
normal map out of pixel art and it is wrong in a specific, visible way: a dark
*marking* on a flat surface becomes a dent, and a light one becomes a bulge.
Painted mortar lines get real depth; a dark mineral fleck in stone becomes a
pit.

This is inherent to reading albedo as height, not a bug in the implementation,
and it is why relief on busy textures reads as noise rather than structure. The
fix is to separate local contrast from the albedo level before the gradient
pass — subtracting a local mean so only *relative* variation contributes, which
the pipeline already computes for the roughness pass and could reuse.

That change belongs to `tools/pbrgen` first: it is the reference implementation,
its constants were tuned against measured output, and `tools/smoketest` asserts
the C# port agrees with it texel for texel. Changing one without the other is
the one failure mode that would be silent.

## What the material system still needs

1. **Metalness is still not in the atlas** — four channels, five values. Metal
   currently gets a white highlight rather than one tinted by its own albedo,
   which is the difference between steel and shiny plastic. Revisit if it reads
   wrong; the fix is a second atlas, not a cleverer pack.
2. **Grass and dirt tops go through `chunktopsoil.fsh`**, a different shader with
   its own anchors. Left for a separate change: bundling it would mean one
   rollback takes both shaders down, and `git bisect` on broken rendering is the
   main debugging tool here.
3. **Region labelling cost.** `LabelColourRegions` is a scalar flood fill. Fine
   per texture; it will not survive being run over every block in the game
   without becoming a compute pass or a vectorised connected-components
   implementation.
4. **No mipmaps on the material atlas.** Magnified normals are smooth, minified
   ones alias. The fix is a mipped upload, not nearest sampling.


## The lighting core is shared

`pbrcore.glsl` holds the one evaluation of Cook-Torrance in this mod - GGX,
Smith-Schlick, Schlick Fresnel, specular antialiasing, ambient and block-light
specular - and is injected into **three** programs: `chunkopaque`,
`chunktopsoil` and `entityanimated`.

It deliberately knows nothing about where its inputs come from. Terrain reads
roughness and a specular mask out of the derived atlas; entities have no such
atlas and use a default material; a future water surface will supply its own.
All three want the same lobe, and the difference between them is which material
reaches it, not how light behaves once it does.

Injected per group rather than shared at runtime, because a group has to be able
to roll back to vanilla without taking another group's declarations with it. The
anchor is `uniform vec3 lightPosition;`, which all three shaders declare and the
core needs - pasted back, because replacement content is literal.

A smoke check asserts `vvDistributionGGX` is defined **exactly once** across
every snippet. Copies would get edited one at a time whenever a highlight looked
wrong, and nothing else would notice them drifting apart.

## Entities

Mobs, animals and players now use that lobe. Before this they did not, and the
inconsistency is obvious once looked for: a creature standing on PBR-lit ground
was shaded by a completely different model than the ground. The floor had a
specular response to the sun, a sky term, torch highlights and a wet response;
the thing standing on it had vanilla's flat diffuse.

**There is no material atlas here, on purpose.** Entities draw from `entityTex`,
a different atlas from the block one this system derives from, and running Sobel
over a mob skin would be the "dark is deep" fallacy at its worst - painted-on
fur shading read as geometry, a face coming out with cheekbones wherever the
artist put shadow. So entities get a default material: one roughness, one
dielectric reflectance at 0.04 (skin, fur, cloth, chitin and horn all sit within
a whisker of it), and the mesh's own normals - which, unlike a block face, are
real per-vertex geometry and are better than anything an atlas could have given.

Rain darkens creatures more than it glosses them, which is the opposite of what
happens to stone: fur and cloth hold water rather than filming it. There is no
sky-exposure test, unlike terrain - an entity is a moving thing with no such
varying, and creatures indoors getting slightly wet in a storm is a smaller
wrong answer than a mob standing in the rain looking bone dry.

`helditem.fsh` is **not** patched. It has no `worldPos`, no `blockLight` and no
`rgbaFog`, so there is far less to work with and far less to gain.

## Light through leaves

A leaf is thin and translucent: light hitting its far side scatters through
rather than stopping, so a canopy with the sun behind it glows. Vanilla shades
foliage with the same opaque diffuse it uses for stone - its own wind
deformation moves leaves without touching how they are shaded - so this is a gap
rather than a re-tint.

Which fragments count as foliage comes from **vanilla's own answer**: the
wind-mode bits it already sets on anything that bends, which in practice is
exactly the set of things thin enough to transmit light. `chunkopaque.vsh` uses
the same test for its own `isLeaves`, so there is no second guess to disagree
with the first.

Two details carry it:

- **Distortion in the bent light direction.** Without it the effect is a
  mirror-sharp hotspot that reads as a bug; with it, the soft wrap real foliage
  has.
- **A yellow-green tint on the transmitted colour.** Light that came through a
  leaf was filtered by chlorophyll on the way out and leaves warmer and more
  saturated than light reflected off the same leaf. Skipping this is what makes
  cheap foliage translucency read as grey haze.

Shadowed leaves do not glow - there is no sun behind them to come through - and
wetness is deliberately absent, because a wet leaf transmits no differently, it
only reflects more. Debug view **13** shows the transmission alone.

## Crevice shading

Occlusion at the scale nothing else in the frame covers. Vanilla ships SSAO, and
SSAO works on **geometry**: it knows a block sits in a corner and darkens the
corner. It has no idea that the mortar line between two bricks is a groove,
because at the depth buffer's resolution it is not one.

The atlas stores a tangent-space normal whose xy **is** the height gradient, so
the divergence of that gradient - how much the surrounding normals lean toward
this texel rather than away - is the surface's curvature: positive in a groove,
negative on a ridge. That is a real cavity estimate rather than an edge
detector, and the difference matters: an edge detector darkens ridges too, and
the result reads as dirt rather than as depth. Only the concave half darkens.

**It multiplies the diffuse and the hemispherical terms, and not the direct
lobe.** That split is the whole of why it is physical rather than a texture: a
crevice is dark because most of the sky cannot see into it, while the sun either
reaches it or does not and the normal already decides which. Multiplying the
highlight by cavity as well is the usual mistake and leaves polished stone
looking dusty.

Four extra texture samples, which is why it has its own control and rides the
same distance fade the relief does - once a texel is smaller than a pixel the
taps are sampling noise. Debug view **12** shows the occlusion alone: mortar
lines, plank gaps and bark furrows should read, and a flat painted texture
should stay white, which is also the check that this is finding curvature rather
than contrast.

**Screen-space contact shadows are not here and are blocked**, not merely
unstarted: they need the depth buffer, which is being written during the opaque
pass rather than read.


## Emissive materials

This game is about fire. Forges, bloomeries, firepits, lamps, lava and torches
are what a player builds a life around, and the loop is leaving a warm lit
shelter for a cold dark wilderness - so light sources matter more here than in a
game that is not built on darkness.

Vanilla already draws them **bright**. What it does not do is make them read as
**hot**, or let them behave like the source of the light they obviously are.

**Everything here is driven by `glowLevel`** - vanilla's own per-fragment answer
to "does this emit", packed into the low byte of `renderFlags` by the vertex
shader and available in all three shaded programs. Nothing is inferred from
pixel brightness: a white marble block is not a lamp, and a brightness heuristic
cannot tell the difference. This is the information ladder working as intended -
the game knows, so we ask it.

Four things it does that vanilla does not:

| | |
|---|---|
| **Hot, not merely bright** | a bright emitter shifts toward white at its core, the way a forge's centre is paler than its edge and iron at welding heat is nearly white |
| **Falls off fast** | `glowLevel` is close to linear in block light and light is not, so emission is squared - otherwise every faintly glowing thing reads as a lamp |
| **Dimmer in daylight** | a torch at noon is barely visible and a torch at midnight lights a room. Not physical - the torch has not changed - but perceptually right, and the reason a lamp indoors reads as a light source rather than a bright spot |
| **Flickers** | position-seeded so two torches on one wall are not in step, which is the difference between "the room is flickering" and "several fires are burning in it". Only downward: a flame dips and recovers, it does not periodically burn brighter than it burns |

**Emission is dampened by nothing.** It is the one term in the shader that is
not a response to light arriving - it *is* light leaving - so shadow, daylight
and the scene's restraint have no business scaling it. Restraint exists to stop
the mod removing light the player needs; a light source is the opposite problem.
Fog still applies, because a distant forge is genuinely behind more air.

### Bloom, without building a bloom

Vanilla already has one: `findbright`, `blur`, and `bloomParts` in `final.fsh`.
So emitters add to **that**, through the `glow` output the terrain shaders
already write, rather than getting a second pass of their own.

Two things follow. It is driven by the emission the material system computed
rather than by how bright the pixel came out - the difference between a bloom
that finds light sources and one that finds snow. And it is restrained by
construction: it can only ever add to something the game already balanced.

Debug view **14** shows emission alone. A forge, a lamp and lava should read; a
white marble block should not, which is also the check that this is coming from
`glowLevel` rather than from brightness.

## Sunlight dapple

Sun breaking through gaps in a canopy and landing on what is underneath.
`PseudoPBR.SunDapple`; debug view 15 shows the field alone.

### What a sunfleck is

A gap in a canopy is a **pinhole**. The sun is not a point - it subtends about
0.53 degrees, 0.0093 radians - so a small gap does not project its own shape
onto the ground, it projects an **image of the sun**. That is why sunflecks
under a tall tree are all roughly the same rounded shape whatever the gaps above
them look like, and why they turn into crescents during a partial eclipse. A gap
smaller than about `0.0093 x height` stops mattering as a shape and matters only
as a hole.

Three consequences, and all three are what makes the effect read as light rather
than as a texture:

1. **Flecks are discrete.** Individual soft-edged spots with dark between them,
   not a continuous field that happens to be brighter in places. The first
   version thresholded summed noise, which gives amorphous blobs that slide.
2. **Every fleck has a penumbra** about `0.0093 x height` across. Under a high
   canopy that is the same order as the fleck itself, so everything is soft;
   close under a bush the edges are crisp. Size and softness scale together with
   the height above, so both are driven from the same term here.
3. **They are ellipses**, stretched along the sun's azimuth by
   `1 / sin(elevation)`, because a round beam meets a horizontal floor at an
   angle. This is the whole reason afternoon dapple reads as shafts and midday
   dapple reads as spots.

**Coverage** is a minority of the floor - a closed canopy passes something like
5-20% of the light. The model here measures out at **14.5%**, inside the 10-25%
real sunflecks occupy.

**Movement is two things on two timescales.** The sun slides the whole pattern
over minutes, which the azimuth throw does. Wind makes individual flecks *wink*
open and shut semi-independently as leaves cross the gaps, over seconds. What it
is not is a coherent rotation or scroll - which is exactly what the first version
did, displacing the sample point by the sine and cosine of one phase so the
field orbited once every 1.5 seconds. Flecks now blink on their own phases
against a separate 26-second breeze clock.

### Where it is allowed to exist

`vv_sunExposure` is vanilla's own per-vertex sun light level - 0 under a roof, 1
under open sky, and **partial under a canopy**, because leaves absorb light on
the way down. Partial is an exact statement from the game that something leafy is
overhead. Both ends of the range return zero.

Foliage itself is excluded. A sunfleck is light that got **past** the leaves onto
something below; drawing it on leaf blocks lit every tree in the world from the
outside, which is the opposite of the effect - the tree should be casting this,
not wearing it.

That gating is why this is not the cloud shadow mistake repeated. There, an
invented field was drawn everywhere and corresponded to nothing. Here the
invention is limited to what the gaps look like.

### Green shade

A leaf transmits roughly 5-10% of the visible light that reaches it and reflects
about another 10, and both are far higher in green than in red or blue - which is
what makes a leaf green in the first place. Light that has been through a canopy
is therefore green-dominant: the floor of a wood is not merely darker than the
field beside it, it is a different colour, and that is the strongest single cue
that you are under trees.

Applied only to the **shaded** side of the dapple. A fleck is sunlight that
missed every leaf and has no business being tinted. It moves colour between
channels rather than adding or removing any.

### It only ever darkens

A fleck is where the leaves failed to block the sun, **not a light of its own**.
The brightest this can leave a pixel is exactly what vanilla lit it to.

That is not a stylistic preference. The previous version subtracted the measured
coverage so the effect was mean-preserving, on the reasoning that an effect which
never dims on average never has to argue with `VisualBudget`. The instinct is
right and it does not survive contact with the coverage: at 11% lit, holding the
mean fixed forces the bright ninth to be enormously brighter than the dark
eight-ninths. That is physically true - a real sunfleck is close to full sun
against shade a tenth of it - but a game has nowhere to put that range. Pixels
went past 1.0, `findbright` multiplies the whole frame rather than thresholding
it, and a forest floor came back as **white spotlights with bloom halos**.

Subtractive makes blowing out arithmetically impossible rather than merely
unlikely, and it is the more honest model anyway: a canopy removes light, it does
not make any. The cost is that dapple does dim on average, so it is a
light-removing term and belongs in `VisualBudget`'s accounting like the rest.

### What is still measured versus assumed

Measured: the fleck coverage (11%), by simulating the exact cell algorithm.

**Assumed:** what `vv_sunExposure` actually reads. The gate takes 1 as open sky
and treats clearly-less as canopy, but if leaves in this version absorb only a
little then "clearly less" might be 0.9 and the gate barely fires; if open ground
does not quite reach 1, the gate leaks onto every field in the world. **Debug
view 16** draws that number raw - white is full sun. Stand in the open, then
under a tree, read off the two values, and the gate stops being a guess.
