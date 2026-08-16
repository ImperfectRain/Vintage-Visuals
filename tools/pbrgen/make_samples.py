#!/usr/bin/env python3
"""Generate a representative sample set for reviewing pbrgen output.

These are stand-ins, not vanilla textures. Vintage Story's assets are not
redistributable, so the repo cannot ship the real thing, and a reviewer needs
*something* to look at before pointing the tool at their own install.

The set is chosen to cover the material categories the Phase 4 milestone names
- stone, metal ore and wood must come out visibly different - plus the cases
each pass is known to get wrong, so the failure modes are visible in the
contact sheet rather than discovered later:

  polished_metal   uniform and bright: variance says smooth, which is right
  ice              uniform and pale: variance says smooth, also right
  painted_shading  a flat surface with painted-on highlights. Sobel reads
                   these as geometry. This tile exists to show that clearly.
  leaves           mostly transparent: exercises the alpha masking
  bricks           hard albedo edges. Measured: brick faces land at roughness
                   0.28 but the mortar lines saturate. The variance window
                   straddles the boundary, so an albedo edge produces a rough
                   halo whether or not the surface is actually rough.
  cloth            fine weave. Measured at roughness 0.30 - it reads smooth,
                   because the weave amplitude is low even though the pattern
                   is busy. Variance keys on contrast, not on detail density.
  gold_block       saturated and bright. Comes out at spec 0.02, i.e. matte -
                   the clearest demonstration that albedo does not carry
                   metalness and this pass cannot invent it.

Run:  python3 make_samples.py --outdir samples/
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image

SIZE = 32
RNG_SEED = 20260816  # fixed so the sample set is reproducible across machines


def _noise(rng, size, scale):
    return (rng.random((size, size)) - 0.5) * scale


def _tint(base, noise):
    """Apply a scalar noise field to an RGB base colour."""
    rgb = np.array(base, dtype=np.float64)[None, None, :] + noise[..., None]
    return np.clip(rgb, 0.0, 1.0)


def stone(rng):
    return _tint((0.45, 0.45, 0.47), _noise(rng, SIZE, 0.10))


def granite(rng):
    coarse = np.repeat(np.repeat(_noise(rng, SIZE // 4, 0.22), 4, axis=0), 4, axis=1)
    return _tint((0.52, 0.46, 0.44), coarse + _noise(rng, SIZE, 0.05))


def gravel(rng):
    coarse = np.repeat(np.repeat(_noise(rng, SIZE // 2, 0.30), 2, axis=0), 2, axis=1)
    return _tint((0.40, 0.39, 0.38), coarse)


def sand(rng):
    return _tint((0.80, 0.72, 0.52), _noise(rng, SIZE, 0.07))


def dirt(rng):
    return _tint((0.34, 0.25, 0.17), _noise(rng, SIZE, 0.13))


def wood_planks(rng):
    img = _tint((0.52, 0.36, 0.20), _noise(rng, SIZE, 0.05))
    grain = 0.06 * np.sin(np.arange(SIZE)[:, None] * 1.7 + rng.random() * 6.0)
    img = np.clip(img + grain[..., None], 0, 1)
    img[::8, :, :] *= 0.55  # plank seams
    return img


def wood_log_end(rng):
    y, x = np.mgrid[0:SIZE, 0:SIZE]
    radius = np.hypot(y - SIZE / 2, x - SIZE / 2)
    rings = 0.10 * np.sin(radius * 1.6)
    return _tint((0.56, 0.40, 0.24), rings + _noise(rng, SIZE, 0.03))


def _ore(rng, stone_base, speck_colour, density=0.10):
    img = _tint(stone_base, _noise(rng, SIZE, 0.08))
    mask = rng.random((SIZE, SIZE)) < density
    # Dilate once so specks are clusters, not single pixels - a one-pixel
    # speck is below what the region pass can meaningfully segment.
    mask |= np.roll(mask, 1, axis=0) | np.roll(mask, 1, axis=1)
    img[mask] = np.array(speck_colour, dtype=np.float64)
    return img


def iron_ore(rng):
    return _ore(rng, (0.45, 0.45, 0.47), (0.78, 0.76, 0.72))


def copper_ore(rng):
    return _ore(rng, (0.45, 0.45, 0.47), (0.85, 0.48, 0.22))


def gold_block(rng):
    return _tint((0.90, 0.75, 0.20), _noise(rng, SIZE, 0.03))


def polished_metal(rng):
    return _tint((0.72, 0.73, 0.75), _noise(rng, SIZE, 0.015))


def ice(rng):
    return _tint((0.70, 0.82, 0.92), _noise(rng, SIZE, 0.02))


def bricks(rng):
    img = _tint((0.60, 0.30, 0.24), _noise(rng, SIZE, 0.05))
    mortar = np.array([0.72, 0.70, 0.66])
    img[::8, :, :] = mortar
    for row_index, row in enumerate(range(0, SIZE, 8)):
        offset = 0 if row_index % 2 == 0 else 8
        img[row:row + 8, (offset % SIZE)::16, :] = mortar
    return img


def cloth(rng):
    weave = 0.05 * ((np.arange(SIZE)[:, None] % 2) ^ (np.arange(SIZE)[None, :] % 2))
    return _tint((0.35, 0.30, 0.55), weave + _noise(rng, SIZE, 0.03))


def painted_shading(rng):
    """Flat surface with a painted highlight - the Sobel pass's known blind spot."""
    gradient = np.linspace(0.25, 0.0, SIZE)[None, :] * np.ones((SIZE, 1))
    return _tint((0.50, 0.44, 0.36), gradient)


def leaves(rng):
    img = _tint((0.20, 0.42, 0.16), _noise(rng, SIZE, 0.12))
    alpha = (rng.random((SIZE, SIZE)) > 0.35).astype(np.float64)
    return np.dstack([img, alpha])


SAMPLES = {
    "stone": stone,
    "granite": granite,
    "gravel": gravel,
    "sand": sand,
    "dirt": dirt,
    "wood_planks": wood_planks,
    "wood_log_end": wood_log_end,
    "iron_ore": iron_ore,
    "copper_ore": copper_ore,
    "gold_block": gold_block,
    "polished_metal": polished_metal,
    "ice": ice,
    "bricks": bricks,
    "cloth": cloth,
    "painted_shading": painted_shading,
    "leaves": leaves,
}


def generate(outdir: Path) -> list[Path]:
    outdir.mkdir(parents=True, exist_ok=True)
    written = []

    for index, (name, builder) in enumerate(sorted(SAMPLES.items())):
        # Per-sample seed derived from the master: adding a texture must not
        # change the pixels of the ones before it.
        rng = np.random.default_rng(RNG_SEED + index)
        image = builder(rng)

        if image.shape[-1] == 3:
            image = np.dstack([image, np.ones(image.shape[:2])])

        target = outdir / f"{name}.png"
        Image.fromarray((np.clip(image, 0, 1) * 255).astype(np.uint8), mode="RGBA").save(target)
        written.append(target)

    return written


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--outdir", type=Path, default=Path("samples"))
    args = parser.parse_args()

    written = generate(args.outdir)
    print(f"wrote {len(written)} sample textures to {args.outdir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
