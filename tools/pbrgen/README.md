# pbrgen — offline PBR prototype

Derives normal, roughness and specular-mask maps from a diffuse texture.

This is the offline reference for the runtime C# material atlas code in
`src/PseudoPBR/`. It remains useful because it can generate contact sheets,
stats and small fixtures without launching the game. The mod build does not
reference this Python code; `tools/smoketest` keeps the C# port aligned where a
fixture exists.

## Usage

```sh
pip install -r requirements.txt

# generate the synthetic sample set and review the passes end to end
python3 make_samples.py --outdir samples/
python3 pbrgen.py samples/ --outdir out/ --contact-sheet out/sheet.png --stats out/stats.json

# point it at real textures
python3 pbrgen.py /path/to/VintageStory/assets/game/textures/block/ \
    --outdir out/ --contact-sheet out/sheet.png
```

Options: `--strength` (normal strength), `--radius` (variance window radius),
`--no-tiling` (clamp at edges instead of wrapping), `--contact-sheet`, `--stats`.

Outputs are written as `<name>_normal.png`, `<name>_rough.png`, `<name>_spec.png`.

```sh
python3 -m pytest .        # 31 tests
```

## The three passes

| Pass | Method | Output |
|---|---|---|
| Normal | Sobel gradient of linear-light luminance, treated as a height field | RGB, OpenGL convention (+Y up) |
| Roughness | Local standard deviation of luminance, absolute mapping | greyscale, floored at 0.25 |
| Spec mask | Flood-fill into same-colour regions, one value per region from its mean colour | greyscale |

All three wrap at the edges by default, because block textures tile. Clamping
instead puts a ring of wrong values around every block face — obvious in game
and easy to mistake for a UV bug.

## Read the contact sheet, not the numbers

`--contact-sheet` writes one row per texture: diffuse, normal, roughness, spec.
It is the most useful thing this tool produces. These passes cannot be judged
from statistics — you have to look at whether the stone reads as stone. The
sheet makes checking fifteen textures take a minute.

## Tuning

The constants at the top of `pbrgen.py` were tuned by sweeping them across the
16 synthetic samples and reading the resulting distributions. Each one records
what it was tuned against and what the rejected values did. The two that matter:

- `ROUGHNESS_STD_REFERENCE = 0.25` — at the initial 0.15, `iron_ore` saturated
  at a median of 1.0 and all high-contrast textures became indistinguishable.
  0.25 is the widest spread that clips nothing.
- `NORMAL_STRENGTH = 1.0` — at 2.0 the mean normal tilt on `iron_ore` was 50°,
  which reads as lighting noise rather than surface detail.

**These were tuned on synthetic stand-ins, not on Vintage Story's actual
textures.** Re-run the sweep on real assets before trusting the output. That is
the entire purpose of this being a separate offline tool.

## Known limitations

Each is inherent to inferring material data from pixels that never carried it.
They are listed here so nobody rediscovers them while tuning the runtime atlas.

- **Sobel cannot tell painted shading from geometry.** A dark band drawn to
  suggest a crack becomes a real crack — usually what you want. A highlight
  painted on a brick's top face becomes geometry bulging outward — not what you
  want. The `painted_shading` sample exists to make this visible.
- **Variance keys on contrast, not on detail density.** `cloth` is visually busy
  but low-contrast, and comes out at 0.30 — smooth. A finely detailed glossy
  tile would come out rough. Neither is correctable from albedo alone.
- **Hard albedo edges produce rough halos.** The variance window straddles the
  boundary, so `bricks` reads smooth on the faces (0.28) but saturates along the
  mortar lines whether or not that surface is actually rough.
- **Albedo does not carry metalness.** The runtime path no longer infers this
  from pixels; it uses the game's material data. The offline prototype still
  demonstrates why a better filter was the wrong fix.
- **The sample set is synthetic.** Vintage Story's assets are not
  redistributable, so the repo ships stand-ins. They cover the material
  categories the Phase 4 milestone names and the known failure modes, but they
  are not a substitute for running this on the real thing.

## What the runtime path still needs from this

Before changing the runtime constants:

1. Re-run the constant sweeps against real vanilla textures and update them.
2. Confirm the three milestone categories — stone, metal ore, wood — still come
   out visibly distinct. `test_milestone_materials_are_visibly_distinct` encodes
   that acceptance criterion as a test.
3. Keep the C# port and this prototype aligned where the parity fixture covers
   the calculation.
4. Note that `_label_colour_regions` is a scalar Python flood fill. It is fine
   for single textures; the runtime path uses different atlas-scale plumbing.
