# Material data pipeline

Every material property, traced from the game's own data to the pixel. Written
because the second atlas doubled the number of stages a value passes through,
and a field that is computed and then quietly dropped looks identical, from any
one file, to a field that works.

That is not hypothetical. Metalness was derived by `MaterialProfile`, combined
by `MaterialProfiles.Combine`, hashed into the atlas cache key - and then
ignored at the `Pack` call, because the first atlas had four channels and five
things worth storing. It travelled four of the six stages below and reached the
GPU as nothing at all.

## One correction to the brief that commissioned this

The brief states that **both** metalness and roughness variation were computed
and discarded. Only metalness was.

`RoughnessVariation` is consumed on the CPU, inside `MaterialProfiles.Combine`:

```csharp
float variation = (Clamp01(derivedRoughness) - 0.5f) * 2.0f;
roughness = Clamp01(profile.Roughness + variation * profile.RoughnessVariation);
```

It modulates the roughness that goes into the first atlas's B channel, so it
reaches the GPU folded into another value rather than as one of its own. That is
a legitimate design - it is a *modulation depth*, not a per-texel property - and
it needs no channel. It is recorded here so nobody "fixes" it by giving it one.

## Material identity: what a surface IS

The pipeline above answers "what are this texture's properties". A separate and
earlier question is **what material does this rendered surface represent**, and
for a while the two were conflated: `MaterialProfiles.For(block.BlockMaterial)`
was computed once per block, so every texture of a wooden gate - including its
iron strapping - was given wood's roughness and a metalness of zero.

`MaterialResolver` answers it per **texture**, with the block's classification
demoted to a fallback:

| Evidence | Why it ranks there |
|---|---|
| the texture's **asset path** | the game files textures by substance (`block/metal/plate/iron`); authored data, per-texture, exactly the granularity a composite block needs |
| the texture's **slot name** | shapes reference textures by name, and blocks drawing two substances usually name them |
| `block.BlockMaterial` | correct for the overwhelming majority of blocks, which really are one substance |

Nothing here looks at a pixel. Every level is something a human author wrote
down - the difference between reading the game's answer and inventing one.

**Segments are matched whole, between slashes, never as substrings.** That is
the entire safety of the table: `blackstone` does not contain the segment
`stone`, `driftwood` does not contain `wood`, and `metalworking` does not
contain `metal`. A false positive would give a correct block a wrong material
with nothing to notice it, so the rule is tested against those exact traps.

Reclassifications are counted and sampled into the startup log, because a
silent one is indistinguishable from a bug.

### Chiseled blocks need none of this

It looks like the hardest case and it is not. A chiseled block's mesh textures
each voxel face with its **source block's** texture, so a block of limestone and
copper produces limestone UVs and copper UVs. The material atlas is keyed by UV.
Sampling it at those coordinates already returns limestone's material for the
limestone faces and copper's for the copper ones - **per voxel face, for free**,
with no per-voxel data, no material-ID vertex attribute and no extra atlas.

The mesher already put the identity in the texture coordinates. The right move
was to notice that rather than to build a parallel channel for it.

### Why there is no material-ID vertex attribute

The obvious alternative is to carry a material index on the geometry. It was
investigated and is not available:

- **`renderFlags` is completely full.** GlowLevel 0-7, ZOffset 8-10, Reflective
  11, Lod0 12, Normal 13-24, WindMode 25-28, WindData 29-31. Zero free bits.
- **`colormapData` has spare bits** (6-7, 13-15) but they are written by the
  chunk tesselator inside `VintagestoryLib`. Shader patching cannot reach the
  code that fills them.
- **A new vertex attribute** would mean changing the VAO layout the game builds
  for every chunk, which is far outside what this mod patches.

So identity is resolved on the CPU, at atlas build time, and delivered through
the texture coordinates the game already assigns. That is cheaper than a vertex
attribute and it survives a game update that reshuffles vertex flags.

## Stage map

| Stage | What happens | Where |
|---|---|---|
| SOURCE | the game's own answer | `Block.BlockMaterial`, `Block.VertexFlags.GlowLevel`, the block texture |
| PROCESSING | derived offline, deterministically | `MaterialProfiles`, `PbrMapGenerator`, `MaterialAtlas2Builder` |
| CACHE | fingerprinted so a stale page is rejected | `MaterialAtlasCache`, `MaterialAtlas2Builder.Fingerprint` |
| PACKING | four 0..1 channels into one RGBA texel | `MaterialAtlasBuilder.Pack` |
| GPU CHANNEL | which byte of which atlas | unit 15 (surface), unit 14 (second) |
| SHADER VARIABLE | named field, read in exactly one place | `VvMaterial2` via `vvSampleMaterial2` |
| FINAL USE | what it changes in the image | below |

## Metalness

```
SOURCE       Block.BlockMaterial  ->  MaterialProfiles.For()  ->  MaterialProfile.Metalness
             Metal 1.00, Ore 0.35, Stone 0, Wood 0, Soil 0, Leaves 0
PROCESSING   MaterialProfiles.Combine() -> per-region constant
             Per REGION, never per texel: whether a surface is a conductor is a
             property of what the block is made of, not of how dark its pixels
             are. Deriving it from pixels is how every dark block becomes metal.
CACHE        already in MaterialAtlasBuilder.Fingerprint (bit pattern), so a
             retune of the material table invalidates both atlases
PACKING      MaterialAtlas2Builder.WriteRegion -> Pack(metalness, ...)
GPU CHANNEL  second atlas, R, texture unit 14
SHADER VAR   VvMaterial2.metalness, scaled by vv_pbrMetalResponse
FINAL USE    F0 = mix(0.04, albedo, metalness)   -> tinted highlight
             diffuse *= 1 - metalness            -> conductors lose their diffuse
```

Both halves matter. Raising F0 alone makes metal a *brighter dielectric*, which
is the shiny-plastic look this replaces.

**Fallback:** without the second atlas, `vvReflectanceF0` falls back to the
specular-mask stand-in that shipped before, and the diffuse line evaluates to
exactly 1. The entity and particle paths have no atlas of their own and use the
stand-in permanently.

## Height

```
SOURCE       the block texture's luminance
PROCESSING   PbrMapGenerator.Luminance -> MaterialAtlas2Builder.Normalise
             Per-texture min/max, so a dark block still has relief. A flat
             texture normalises to 0.5, not to 0 or 1.
CACHE        covered by the source pixels already in the fingerprint
PACKING      Pack(_, height, _, _)
GPU CHANNEL  second atlas, G
SHADER VAR   VvMaterial2.height
FINAL USE    NONE YET - debug view 20 only
```

Deliberately unconsumed. Parallax is the obvious consumer and is **not**
implemented: a displacement built on misaligned height is indistinguishable from
one built on a bad scale, and separating those after the fact is the trap this
project has already spent several rounds climbing out of. The alignment is
proven first, by test, and the feature comes later.

Luminance is a known-bad stand-in for height in a specific way - a painted-on
dark line reads as a groove - but it is the *same* stand-in the first atlas
already uses for its normal map, so the two agree with each other even where
both are wrong about the real surface.

## Baked AO

```
SOURCE       the height field above
PROCESSING   height against its own local mean (BoxMean radius 2, tiling)
             Only the recessed half darkens; a raised texel is unoccluded, never
             over-bright.
CACHE        derived from cached pixels
PACKING      Pack(_, _, occlusion, _)
GPU CHANNEL  second atlas, B
SHADER VAR   VvMaterial2.occlusion
FINAL USE    NONE YET - debug view 21 only
```

**Deliberately not wired into lighting**, and not a substitute for `vvCavity`.
The two measure different things: this is the broad shape of the surface,
`vvCavity` is the grain of it, measured from normal curvature at a one-texel
radius. Multiplying two occlusion terms because both are called occlusion is how
surfaces get double-darkened. The combination rule needs to be decided by
looking at views 12 and 21 side by side, not by assuming.

## Emission mask

```
SOURCE       Block.VertexFlags.GlowLevel / 256   (the same divisor chunkopaque.vsh uses)
PROCESSING   glow == 0  -> mask is zero everywhere, unconditionally
             glow >  0  -> smoothstep between the texture's own 70th and 95th
                           luminance percentiles
CACHE        glow level mixed into MaterialAtlas2Builder.Fingerprint - the first
             atlas has no reason to hash it, and a block that starts or stops
             emitting must rebuild this page
PACKING      Pack(_, _, _, emission)
GPU CHANNEL  second atlas, A
SHADER VAR   VvMaterial2.emission, read through vvEmissionMask()
FINAL USE    emission *= mask        (vvApplyPbr)
             bloom feed *= mask      (vvEmissiveGlow, both terrain shaders)
```

The hierarchy is one-directional and enforced, not merely intended:

1. **vanilla's `glowLevel`** decides whether a block emits and how strongly.
   `vvEmission` returns black before the mask is consulted.
2. **the mask** decides only where within an already-emitting texture.
3. **existing colour/temperature logic** decides what it looks like.
4. **bloom** responds to the resulting emission, not to texture brightness.

Percentiles rather than an absolute threshold, so the mask picks the hottest part
of *this* texture. An absolute cut would make every pale block emit and every
dark one stay dark - the "bright pixels are emissive" mistake.

**Fallback is 1, not 0.** The mask multiplies emission the game already granted,
so "no data" must mean "emits everywhere it used to". Reading the neutral
material's zero here would have switched every light source in the world off.

## Failure behaviour, in one place

| Condition | Result |
|---|---|
| second atlas not built | `vv_material2Valid` = 0, all four read neutral |
| second atlas built, upload failed | same, and the first atlas is unaffected |
| patch group rolled back | uniform absent, reads 0, same fallback |
| region outside atlas bounds | that texture is neutral; the page is fine |
| block the game does not light | emission mask zero, whatever the pixels are |

`vv_material2Valid`'s zero **is** the fallback, and zero is what an unset uniform
reads - so an unpatched program, a rolled-back group and a failed build all reach
the same safe state without anyone remembering to handle them.
