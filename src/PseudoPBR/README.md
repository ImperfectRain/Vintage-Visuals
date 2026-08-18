# PseudoPBR

Derives normal, roughness and specular-mask data from vanilla diffuse textures,
so materials respond to light differently without anyone hand-authoring maps
per block.

## Status

**Phase 4, classification landed.** The mod now works out what material every
block is made of, and can report it. Nothing changes how the game renders yet.

| Piece | State |
|---|---|
| Offline prototype (`tools/pbrgen`) | done, 31 tests |
| C# port of the three passes | done, 21 parity checks |
| Material classification + profile table | done, 33 checks |
| Material report (`material-report.txt`) | done |
| Derived atlas built at texture-upload time | not started |
| Disk cache keyed by texture hash | not started |
| Lighting shader consumes the maps | not started |
| Roughness modulates SSR blur | not started (needs Phase 3) |

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

## What the atlas stage still needs to decide

1. **Where to hook.** Texture-atlas upload time, per the plan — a Harmony patch
   on the atlas manager. That is `VintagestoryLib` territory, so it must be
   reflection-guarded and log loudly rather than throw, exactly like
   `ShaderSourceInterceptor`. `ITextureAtlasAPI` does expose
   `GetPosition(block, textureName)` and `AllocateTextureSpace`, so the mapping
   from a block to its atlas rectangle is public API — only the upload hook is
   internal.
2. **Cache key.** A hash of the source texture, so the cost is paid once per
   texture rather than once per launch.
3. **Region labelling cost.** `LabelColourRegions` is a scalar flood fill. Fine
   per texture, and it will not survive a full atlas — it needs to become a
   compute pass or a vectorised connected-components implementation before it
   runs over every block in the game.
