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
| Preview images for inspection | done |
| Atlas uploaded to the GPU | done, level 2 (compiles) |
| `chunkopaque.fsh` samples it for normals | done, level 2 (compiles), 24 checks |
| Specular / roughness consumed by a shader | not started — needs a light direction |
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
game already shades by normal, so it is given a better normal:

```glsl
outColor = applyFogAndShadowWithNormal(texColor, fogAmount, vvPerturbNormal(normal, uv), 1, intensity);
```

That one token replacement in `chunkopaque.fsh` is the whole render-side change.
Everything else exists to make it possible:

- `MaterialAtlasTexture` uploads the derived atlas as one RGBA texture with the
  block atlas's exact dimensions, so it samples with the same `uv`.
- `PbrShaderBinder` is an `IRenderer` that draws nothing. It re-binds the atlas
  and refreshes the uniforms once per frame at render order **0.35**, which is
  the gap immediately before terrain opaque (0.37). Once-at-startup would be
  cheaper, but texture unit bindings are global GL state that this mod does not
  own — anything can rebind unit 6, and the failure would be silent and ugly.
- `vvTangentFrame` builds the tangent basis. Chunk geometry carries no tangents,
  and does not need to: every block face is axis-aligned, so one consistent
  frame per axis is exact rather than approximate.

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
| `chunkopaque.fsh` patch | **2 (compiles)** — applies and rolls back correctly against a stand-in shader, 24 smoke-test checks |
| Relief visible in game | **not reached** |

Two specific things are unconfirmed and one of them is the real risk:

1. **The patch anchors have not been checked against 1.22.7's own
   `chunkopaque.fsh`.** They come from Volumetric Shading's patch set, which has
   tracked these same two lines across several game versions, corroborated by
   the chunk-shader idioms it ships (`uniform sampler2D terrainTex`,
   `in vec2 uv`, world-space `normal` compared against `vec3(0,1,0)`). If either
   anchor is wrong the group rolls back to vanilla and logs `CRITICAL` — loud and
   safe, which is why shipping on best evidence is defensible here and would not
   be if the patch could half-apply. The `uv` assertion patch exists solely to
   make that true.
2. **Texture unit 6 is a convention, not a reservation.** If the game or another
   mod binds something else there mid-frame, the relief samples the wrong
   texture. Re-binding every frame before terrain draws makes this unlikely
   rather than impossible.

## What the material system still needs

1. **Specular and roughness are derived, packed, cached and unused.** Consuming
   them needs a light direction, which `chunkopaque.fsh` does not hand us at the
   point the patch intervenes. That is the next piece of render work, and it is
   what turns "metal trim has relief" into "metal trim is reflective".
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
