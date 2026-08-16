#!/usr/bin/env python3
"""Derive normal, roughness and specular-mask maps from a diffuse texture.

Phase 4a of the implementation plan: the offline prototype that has to be
validated on real textures *before* any of this logic is ported into the game's
texture-atlas pipeline. It is a content-authoring aid, deliberately kept out of
the mod's C# build - iterating on it must not require a game restart.

The whole approach is an inference from pixels that were never meant to carry
material data, so every pass here is an approximation with a known failure
mode. Those are documented at each function rather than in one lump, because
the person tuning a constant is reading that function, not the module header.

Usage:
    python3 pbrgen.py TEXTURE.png [MORE.png ...] --outdir out/
    python3 pbrgen.py textures/ --outdir out/ --contact-sheet sheet.png
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import deque
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from PIL import Image

# --------------------------------------------------------------------------
# Tuning constants.
#
# Every one of these was chosen against the synthetic sample set in
# make_samples.py, NOT against real Vintage Story textures. They are starting
# points. Re-tune them on real assets before trusting the output, and record
# what you tuned against when you do.
# --------------------------------------------------------------------------

#: Local luminance standard deviation, in linear light, that maps to fully
#: rough.
#:
#: Tuned by sweeping 0.15/0.25/0.35/0.45 across the 16 samples in
#: make_samples.py and reading the resulting mean and median roughness. 0.15 -
#: the first guess - clipped: iron_ore came out at a median of 1.00, so every
#: high-contrast texture saturated and lost all discrimination against every
#: other high-contrast texture. 0.35 and above avoided clipping but compressed
#: the whole set into 0.26-0.53.
#:
#: 0.25 is the widest spread that still clips nothing: polished_metal 0.27,
#: ice 0.28, dirt 0.29, cloth 0.30, stone 0.32, sand 0.33, granite 0.34,
#: wood_planks 0.36, gravel 0.40, bricks 0.49, iron_ore 0.64 (median 0.71).
#: Ordering is monotonic and matches intuition across the whole set.
#:
#: Tuned against SYNTHETIC samples. Re-run the sweep on real textures.
ROUGHNESS_STD_REFERENCE = 0.25

#: Floor for roughness. Nothing in a hand-painted block texture is a mirror,
#: and a zero here makes the SSR pass produce hard, obviously-wrong reflections
#: on surfaces that merely happen to be flat-coloured.
ROUGHNESS_FLOOR = 0.25

#: Euclidean RGB distance within which two pixels count as the same material
#: during the spec-mask region pass. 0.35 keeps ore specks separate from their
#: surrounding stone while still merging the shading variation *within* one
#: speck; below ~0.2 regions shatter into single pixels and the mask goes noisy.
SPEC_REGION_THRESHOLD = 0.35

#: Regions smaller than this fraction of the texture are treated as inclusions
#: (ore specks, nail heads, gem facets) and get their specularity boosted. A
#: small bright patch inside a dull field is almost always meant to read as
#: metal or crystal.
SPEC_INCLUSION_MAX_AREA = 0.15
SPEC_INCLUSION_BOOST = 1.6

#: Pixels at or below this alpha contribute to nothing. Leaves and grass are
#: mostly transparent, and letting the empty region drag the averages around
#: makes every foliage texture read as smooth and dark.
ALPHA_CUTOFF = 0.5

#: Rec.709 luma weights, matching the primaries the game renders in.
LUMA_WEIGHTS = np.array([0.2126, 0.7152, 0.0722], dtype=np.float64)


# --------------------------------------------------------------------------
# Colour space helpers
# --------------------------------------------------------------------------

def srgb_to_linear(srgb: np.ndarray) -> np.ndarray:
    """Undo the sRGB transfer function.

    Gradients are computed in linear light because that is where a difference
    in value corresponds to a difference in the amount of light leaving the
    surface. Running Sobel directly on sRGB exaggerates slopes in the shadows,
    which shows up as chunky, over-sharp normals in dark textures.
    """
    srgb = np.clip(srgb, 0.0, 1.0)
    return np.where(srgb <= 0.04045, srgb / 12.92, ((srgb + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(linear: np.ndarray) -> np.ndarray:
    linear = np.clip(linear, 0.0, 1.0)
    return np.where(linear <= 0.0031308, linear * 12.92, 1.055 * linear ** (1 / 2.4) - 0.055)


def luminance(rgb_linear: np.ndarray) -> np.ndarray:
    """Rec.709 relative luminance of a linear-light HxWx3 image."""
    return rgb_linear @ LUMA_WEIGHTS


# --------------------------------------------------------------------------
# Neighbourhood helpers
# --------------------------------------------------------------------------

def _pad(a: np.ndarray, radius: int, tiling: bool) -> np.ndarray:
    """Pad for a neighbourhood operation.

    Block textures tile, so 'wrap' is the correct default: clamping instead
    produces a visible seam of wrong normals at every block edge, which is
    obvious in game and easy to mistake for a UV bug.
    """
    return np.pad(a, radius, mode="wrap" if tiling else "edge")


def _box_mean(a: np.ndarray, radius: int, tiling: bool) -> np.ndarray:
    """Mean over a square window, via a summed-area table.

    O(1) per pixel regardless of radius. Textures here are tiny so this is not
    a speed concern, but the same code is the reference for the eventual
    compute-shader port, where the window size does matter.
    """
    padded = _pad(a, radius, tiling)
    integral = np.cumsum(np.cumsum(padded, axis=0), axis=1)
    integral = np.pad(integral, ((1, 0), (1, 0)), mode="constant")

    size = 2 * radius + 1
    h, w = a.shape
    total = (
        integral[size:size + h, size:size + w]
        - integral[0:h, size:size + w]
        - integral[size:size + h, 0:w]
        + integral[0:h, 0:w]
    )
    return total / float(size * size)


# --------------------------------------------------------------------------
# Pass 1 - normal from luminance
# --------------------------------------------------------------------------

#: Default normal map strength.
#:
#: Also swept across the sample set, measuring mean normal tilt in degrees.
#: At 2.0 the mean tilt on iron_ore was 50 degrees and on gravel 35 - most
#: texels steeply tilted, which reads as lighting noise rather than as surface
#: detail. At 1.0 the set spans 1.7 degrees (polished_metal) to 35 (iron_ore),
#: with stone at 5.8 and gravel at 20: steep only where the texture genuinely
#: has hard steps. Raise it per-run with --strength for flat-looking textures.
NORMAL_STRENGTH = 1.0


def generate_normal_from_luminance(lum: np.ndarray, strength: float = NORMAL_STRENGTH,
                                   tiling: bool = True) -> np.ndarray:
    """Sobel-derived tangent-space normal map, OpenGL convention (+Y up).

    Treats luminance as a height field. That is the load-bearing assumption and
    it is wrong in a specific, predictable way: hand-painted textures already
    contain *painted* shading, so a dark band drawn to suggest a crack in stone
    is read as a real crack - which is usually what you want - while a
    highlight painted on the top face of a brick is read as geometry bulging
    outward, which is not. There is no way to tell the two apart from pixels
    alone; the mitigation is a modest default strength and eyes on the output.

    Returns HxWx3 in [0,1], ready to write as an 8-bit PNG.
    """
    p = _pad(lum, 1, tiling)

    # Sobel. gx is the rate of change to the right; gy is the rate of change
    # *down the image*, which is the opposite of the world-space +Y the normal
    # map convention wants.
    gx = ((p[0:-2, 2:] + 2 * p[1:-1, 2:] + p[2:, 2:])
          - (p[0:-2, 0:-2] + 2 * p[1:-1, 0:-2] + p[2:, 0:-2]))
    gy = ((p[2:, 0:-2] + 2 * p[2:, 1:-1] + p[2:, 2:])
          - (p[0:-2, 0:-2] + 2 * p[0:-2, 1:-1] + p[0:-2, 2:]))

    # n = normalize(-dh/dx, -dh/dy_world, 1); dh/dy_world == -gy because image
    # rows run downward, which is why y is +gy and not -gy here.
    nx = -gx * strength
    ny = gy * strength
    nz = np.ones_like(lum)

    length = np.sqrt(nx * nx + ny * ny + nz * nz)
    normal = np.stack([nx / length, ny / length, nz / length], axis=-1)

    return normal * 0.5 + 0.5


# --------------------------------------------------------------------------
# Pass 2 - roughness from local variance
# --------------------------------------------------------------------------

def generate_roughness_from_variance(lum: np.ndarray, radius: int = 1,
                                     tiling: bool = True,
                                     std_reference: float = ROUGHNESS_STD_REFERENCE,
                                     floor: float = ROUGHNESS_FLOOR) -> np.ndarray:
    """Roughness from local luminance variance.

    Uses variance rather than a proper frequency-domain estimate: it is far
    cheaper, and at the 16-32px texel resolution these textures live at there
    are not enough samples for a frequency analysis to say much that variance
    does not.

    The known failure: variance cannot distinguish "physically rough surface"
    from "busy pattern". A finely detailed but glossy tile reads as rough, and
    a smooth matte surface painted flat reads as polished - which is what the
    floor exists to blunt.

    Absolute mapping, deliberately not normalised per texture. Normalising
    would stretch every texture to the full 0-1 range, so uniform stone would
    come out as rough as gravel and the whole point - materials differing from
    each other - would be lost.
    """
    mean = _box_mean(lum, radius, tiling)
    mean_sq = _box_mean(lum * lum, radius, tiling)

    # Clamp at zero: catastrophic cancellation in E[x^2]-E[x]^2 can go slightly
    # negative on near-uniform regions, and sqrt of that is a NaN that
    # propagates all the way to the PNG.
    variance = np.maximum(mean_sq - mean * mean, 0.0)
    std = np.sqrt(variance)

    roughness = floor + (1.0 - floor) * np.clip(std / std_reference, 0.0, 1.0)
    return np.clip(roughness, 0.0, 1.0)


# --------------------------------------------------------------------------
# Pass 3 - specular mask from colour regions
# --------------------------------------------------------------------------

def _label_colour_regions(rgb: np.ndarray, valid: np.ndarray,
                          threshold: float, tiling: bool) -> tuple[np.ndarray, int]:
    """Flood-fill connected pixels whose colour is close to their seed.

    Compared against the *seed* colour rather than a running region mean: a
    running mean lets a region drift arbitrarily far from where it started, so
    a smooth gradient merges into one blob and the ore speck we were trying to
    isolate is swallowed by the stone around it.
    """
    h, w = valid.shape
    labels = np.full((h, w), -1, dtype=np.int32)
    region_count = 0
    threshold_sq = threshold * threshold

    for start_y in range(h):
        for start_x in range(w):
            if labels[start_y, start_x] != -1 or not valid[start_y, start_x]:
                continue

            seed = rgb[start_y, start_x]
            labels[start_y, start_x] = region_count
            queue = deque([(start_y, start_x)])

            while queue:
                y, x = queue.popleft()
                for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
                    ny, nx = y + dy, x + dx

                    if tiling:
                        ny, nx = ny % h, nx % w
                    elif not (0 <= ny < h and 0 <= nx < w):
                        continue

                    if labels[ny, nx] != -1 or not valid[ny, nx]:
                        continue

                    delta = rgb[ny, nx] - seed
                    if float(delta @ delta) > threshold_sq:
                        continue

                    labels[ny, nx] = region_count
                    queue.append((ny, nx))

            region_count += 1

    return labels, region_count


def generate_spec_mask_from_colour_average(rgb_linear: np.ndarray, alpha: np.ndarray,
                                           threshold: float = SPEC_REGION_THRESHOLD,
                                           tiling: bool = True) -> np.ndarray:
    """Specular mask from per-region average colour.

    Segments the texture into connected same-colour regions and gives each one
    a single specular value derived from its mean colour, on the reasoning that
    bright and desaturated reads as metal or stone polish while saturated reads
    as pigment. Assigning per region rather than per pixel is what makes ore
    veins come out as coherent metallic patches instead of speckled noise.

    Known failure: an unlit grey texture and a genuinely metallic grey texture
    are identical to this pass. It infers from albedo alone, and albedo does
    not carry metalness.
    """
    valid = alpha > ALPHA_CUTOFF
    spec = np.zeros(alpha.shape, dtype=np.float64)

    if not valid.any():
        return spec

    labels, region_count = _label_colour_regions(rgb_linear, valid, threshold, tiling)
    total_valid = float(valid.sum())

    for region in range(region_count):
        mask = labels == region
        area = float(mask.sum())
        if area == 0:
            continue

        mean_colour = rgb_linear[mask].mean(axis=0)
        peak = float(mean_colour.max())
        trough = float(mean_colour.min())
        saturation = 0.0 if peak <= 1e-6 else (peak - trough) / peak

        value = float(luminance(mean_colour))
        region_spec = (1.0 - saturation) * value

        if area / total_valid <= SPEC_INCLUSION_MAX_AREA:
            region_spec *= SPEC_INCLUSION_BOOST

        spec[mask] = min(region_spec, 1.0)

    return np.clip(spec, 0.0, 1.0)


# --------------------------------------------------------------------------
# Driver
# --------------------------------------------------------------------------

@dataclass
class TextureMaps:
    name: str
    width: int
    height: int
    normal: np.ndarray
    roughness: np.ndarray
    spec: np.ndarray

    def stats(self) -> dict:
        """Numbers worth eyeballing across a batch to spot a broken pass."""
        return {
            "name": self.name,
            "size": [self.width, self.height],
            "roughness_mean": round(float(self.roughness.mean()), 4),
            "roughness_min": round(float(self.roughness.min()), 4),
            "roughness_max": round(float(self.roughness.max()), 4),
            "spec_mean": round(float(self.spec.mean()), 4),
            "spec_max": round(float(self.spec.max()), 4),
            "normal_flatness": round(float(np.abs(self.normal[..., 2] - 1.0).mean()), 4),
        }


def load_texture(path: Path) -> tuple[np.ndarray, np.ndarray]:
    """Load a PNG as (linear-light RGB, alpha), both float64 in [0,1]."""
    image = Image.open(path).convert("RGBA")
    data = np.asarray(image, dtype=np.float64) / 255.0
    return srgb_to_linear(data[..., :3]), data[..., 3]


def process_texture(path: Path, strength: float = NORMAL_STRENGTH, radius: int = 1,
                    tiling: bool = True) -> TextureMaps:
    rgb_linear, alpha = load_texture(path)
    lum = luminance(rgb_linear)

    # Fully transparent texels have arbitrary RGB. Substituting the mean of the
    # visible pixels stops the gradient pass from carving a hard cliff around
    # every leaf edge.
    valid = alpha > ALPHA_CUTOFF
    if valid.any() and not valid.all():
        lum = np.where(valid, lum, lum[valid].mean())

    return TextureMaps(
        name=path.stem,
        width=lum.shape[1],
        height=lum.shape[0],
        normal=generate_normal_from_luminance(lum, strength, tiling),
        roughness=generate_roughness_from_variance(lum, radius, tiling),
        spec=generate_spec_mask_from_colour_average(rgb_linear, alpha, tiling=tiling),
    )


def _to_image(array: np.ndarray) -> Image.Image:
    """Encode a linear [0,1] array as an 8-bit PNG.

    Normal, roughness and spec maps are all *data*, not pictures, so they are
    written without a gamma curve. Applying one would make the shader's
    decoded values silently wrong.
    """
    return Image.fromarray(np.clip(array * 255.0 + 0.5, 0, 255).astype(np.uint8))


def write_maps(maps: TextureMaps, outdir: Path) -> list[Path]:
    outdir.mkdir(parents=True, exist_ok=True)
    written = []

    for suffix, array in (("normal", maps.normal),
                          ("rough", maps.roughness),
                          ("spec", maps.spec)):
        target = outdir / f"{maps.name}_{suffix}.png"
        _to_image(array).save(target)
        written.append(target)

    return written


def build_contact_sheet(all_maps: list[TextureMaps], source_paths: list[Path],
                        scale: int = 4) -> Image.Image:
    """Diffuse / normal / roughness / spec in a row per texture.

    The single most useful output of this tool. These passes cannot be judged
    by their numbers - you have to look at whether the stone reads as stone,
    and a contact sheet is what makes checking fifteen textures take a minute
    instead of an afternoon.
    """
    if not all_maps:
        raise ValueError("nothing to draw")

    cell = max(max(m.width, m.height) for m in all_maps) * scale
    sheet = Image.new("RGB", (cell * 4, cell * len(all_maps)), (24, 24, 28))

    for row, (maps, source) in enumerate(zip(all_maps, source_paths)):
        panels = [
            Image.open(source).convert("RGB"),
            _to_image(maps.normal),
            _to_image(maps.roughness).convert("RGB"),
            _to_image(maps.spec).convert("RGB"),
        ]
        for column, panel in enumerate(panels):
            # Nearest neighbour: these are pixel-art textures and any smoothing
            # hides exactly the per-texel detail being reviewed.
            sheet.paste(panel.resize((cell, cell), Image.NEAREST), (column * cell, row * cell))

    return sheet


def collect_inputs(paths: list[str]) -> list[Path]:
    found: list[Path] = []

    for raw in paths:
        path = Path(raw)
        if path.is_dir():
            found.extend(sorted(p for p in path.rglob("*.png") if p.is_file()))
        elif path.is_file():
            found.append(path)
        else:
            print(f"pbrgen: no such file or directory: {path}", file=sys.stderr)

    return found


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Derive normal/roughness/spec maps from diffuse textures.")
    parser.add_argument("inputs", nargs="+", help="PNG files or directories to scan")
    parser.add_argument("--outdir", type=Path, default=Path("out"),
                        help="where to write the generated maps (default: out/)")
    parser.add_argument("--strength", type=float, default=NORMAL_STRENGTH,
                        help=f"normal map strength (default: {NORMAL_STRENGTH})")
    parser.add_argument("--radius", type=int, default=1,
                        help="variance window radius for roughness (default: 1, a 3x3 window)")
    parser.add_argument("--no-tiling", action="store_true",
                        help="clamp at edges instead of wrapping; use for non-tiling textures")
    parser.add_argument("--contact-sheet", type=Path,
                        help="also write a side-by-side review sheet to this path")
    parser.add_argument("--stats", type=Path,
                        help="also write per-texture statistics as JSON")

    args = parser.parse_args(argv)
    inputs = collect_inputs(args.inputs)

    if not inputs:
        print("pbrgen: no input textures found", file=sys.stderr)
        return 1

    tiling = not args.no_tiling
    all_maps: list[TextureMaps] = []

    for path in inputs:
        try:
            maps = process_texture(path, args.strength, args.radius, tiling)
        except Exception as exc:  # noqa: BLE001 - one bad file must not stop a batch
            print(f"pbrgen: failed on {path}: {exc}", file=sys.stderr)
            continue

        write_maps(maps, args.outdir)
        all_maps.append(maps)
        print(f"{path.name}: {json.dumps(maps.stats())}")

    if not all_maps:
        return 1

    if args.contact_sheet:
        args.contact_sheet.parent.mkdir(parents=True, exist_ok=True)
        build_contact_sheet(all_maps, inputs[:len(all_maps)]).save(args.contact_sheet)
        print(f"contact sheet -> {args.contact_sheet}")

    if args.stats:
        args.stats.parent.mkdir(parents=True, exist_ok=True)
        args.stats.write_text(json.dumps([m.stats() for m in all_maps], indent=2))
        print(f"stats -> {args.stats}")

    print(f"{len(all_maps)} texture(s) -> {args.outdir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
