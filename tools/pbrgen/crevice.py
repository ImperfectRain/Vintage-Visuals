#!/usr/bin/env python3
"""Crevice maps: the joint network, the stone shape and the rain flow paths
of one tiling block texture (D67).

This is the reference implementation. src/PseudoPBR/CreviceMaps.cs is a port
of it, and tools/smoketest asserts the two agree on crevice_fixture.json. It
was tuned by eye on the game's own cobblestone, brick and dry stone textures
(previews: tools/pbrgen/crevice_preview.py), which is the step every earlier
version of the effect skipped.

Why it exists: the texture's luminance is the only height signal the mod has,
and on Vintage Story's stone it is mostly painted speckle. Any per-texel test
in the shader reads every speck as a dip. The STRUCTURE the artist drew - long
connected dark joints around lighter stones - only shows at a scale of
several texels, so it is measured here, once, on the CPU:

  joints  narrow dark features (black top-hat of the lightly smoothed height),
          grown by hysteresis from strong seeds into weaker neighbours, and
          kept only where the connected run is long: a joint runs, a speck or
          a chisel mark does not.
  shape   each stone as a pillow: rounded within three texels of a joint, flat
          on top, with a little of the painted shading kept.
  flow    for a wall face (rows run down): water takes the lowest of the three
          texels below it on the shape, so it slides off a bump's shoulder and
          gathers in channels; under a ledge it runs sideways along the joint;
          rain lands everywhere and the paths that collect the most become the
          visible streams. phase is how far along its path each texel is, plus
          the path's own offset, so the shader can slide drops along it.

Every array is HxW float64 in 0..1, indexed [y, x], tiling at the edges.
"""

from __future__ import annotations

import numpy as np

# Tuned on the game textures 2026-11-25; the C# port must match (smoketest).
JOINT_PERCENTILE = 82.0
JOINT_MIN_SEED = 0.05
JOINT_GROW = 0.75
JOINT_MIN_TEXELS = 10
JOINT_MIN_RUN = 0.3
POOL_FLOOR = 0.6
PILLOW_REACH = 3.0
PILLOW_PAINT = 0.15
FLOW_CHANNEL_DROP = 0.25
FLOW_SIDE_PREFERENCE = 0.015
FLOW_LEDGE = 0.02
FLOW_LEDGE_SLACK = 0.01
FLOW_STEPS = 96
FLOW_NORMALISE = 0.35
FLOW_START = 0.15
FLOW_RANGE = 0.35
PHASE_PERIOD = 9.0


def roll(a, dy, dx):
    """result[y, x] = a[y - dy, x - dx], wrapping."""
    return np.roll(np.roll(a, dy, 0), dx, 1)


def gauss(a, passes=1):
    k = ((0, 0, 4), (0, 1, 2), (0, -1, 2), (1, 0, 2), (-1, 0, 2),
         (1, 1, 1), (1, -1, 1), (-1, 1, 1), (-1, -1, 1))
    for _ in range(passes):
        out = np.zeros_like(a)
        for dy, dx, w in k:
            out = out + roll(a, dy, dx) * w
        a = out / 16.0
    return a


def _disk(r):
    return [(dy, dx) for dy in range(-r, r + 1) for dx in range(-r, r + 1) if dx * dx + dy * dy <= r * r]


def dilate(a, r):
    out = a.copy()
    for dy, dx in _disk(r):
        out = np.maximum(out, roll(a, dy, dx))
    return out


def erode(a, r):
    out = a.copy()
    for dy, dx in _disk(r):
        out = np.minimum(out, roll(a, dy, dx))
    return out


def percentile(values, p):
    """numpy's default (linear) percentile, written out so the port is exact."""
    s = sorted(float(v) for v in values.ravel())
    pos = (len(s) - 1) * p / 100.0
    lo = int(np.floor(pos))
    hi = min(lo + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (pos - lo)


def components(mask):
    """4-connected components on a torus, labelled in row-major discovery order."""
    H, W = mask.shape
    lab = -np.ones((H, W), int)
    sizes = []
    for y in range(H):
        for x in range(W):
            if mask[y, x] and lab[y, x] < 0:
                n = len(sizes)
                stack = [(y, x)]
                lab[y, x] = n
                count = 0
                while stack:
                    cy, cx = stack.pop()
                    count += 1
                    for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        ny, nx = (cy + dy) % H, (cx + dx) % W
                        if mask[ny, nx] and lab[ny, nx] < 0:
                            lab[ny, nx] = n
                            stack.append((ny, nx))
                sizes.append(count)
    return lab, sizes


def joints(h):
    H, W = h.shape
    hs = gauss(h, 1)
    depth = np.clip(erode(dilate(hs, 2), 2) - hs, 0.0, None)
    hi = max(JOINT_MIN_SEED, percentile(depth, JOINT_PERCENTILE))
    lo = hi * JOINT_GROW
    weak = depth > lo
    grown = depth > hi
    for _ in range(max(H, W)):
        nxt = grown.copy()
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nxt |= roll(grown, dy, dx) & weak
        if (nxt == grown).all():
            break
        grown = nxt
    lab, sizes = components(grown)
    keep = np.zeros_like(h)
    for i, n in enumerate(sizes):
        ys, xs = np.nonzero(lab == i)
        run = max(len(set(ys.tolist())) / H, len(set(xs.tolist())) / W)
        if n >= JOINT_MIN_TEXELS and run >= JOINT_MIN_RUN:
            keep[lab == i] = 1.0
    pool = keep * np.clip(depth / hi, POOL_FLOOR, 1.0)
    return pool, keep


def distance_to(mask, maxd=6):
    d = np.where(mask > 0.5, 0.0, float(maxd))
    for _ in range(maxd):
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            d = np.minimum(d, roll(d, dy, dx) + 1.0)
    return d


def shape(keep, h):
    d = distance_to(keep)
    t = np.clip(d / PILLOW_REACH, 0.0, 1.0)
    dome = 1.0 - (1.0 - t) * (1.0 - t)
    # Not renormalised: the flow thresholds below are absolute heights on
    # this scale, which is the scale the approved previews were tuned on.
    # Both terms are already 0..1, so the clip is a guard, not a rescale.
    return np.clip(gauss(dome * (1.0 - PILLOW_PAINT) + gauss(h, 2) * PILLOW_PAINT, 1), 0.0, 1.0)


def flow_next(sh, keep):
    H, W = sh.shape
    hh = sh - keep * FLOW_CHANNEL_DROP
    nxt = np.zeros((H, W, 2), int)
    for y in range(H):
        for x in range(W):
            c = hh[y, x]
            best_cost, by, bx = None, 0, 0
            for dx, pref in ((0, 0.0), (-1, FLOW_SIDE_PREFERENCE), (1, FLOW_SIDE_PREFERENCE)):
                yy, xx = (y + 1) % H, (x + dx) % W
                cost = hh[yy, xx] - c + pref
                if best_cost is None or cost < best_cost:
                    best_cost, by, bx = cost, yy, xx
            if best_cost > FLOW_LEDGE:
                l, r = hh[y, (x - 1) % W], hh[y, (x + 1) % W]
                side = -1 if l < r else 1
                if min(l, r) <= c + FLOW_LEDGE_SLACK:
                    by, bx = y, (x + side) % W
            nxt[y, x] = by, bx
    return nxt


def hash01(v):
    return ((v * 2654435761) % 4294967296 % 10007) / 10007.0


def flow(sh, keep):
    H, W = sh.shape
    nxt = flow_next(sh, keep)
    A = np.zeros((H, W))
    S = np.zeros((H, W))
    R = np.zeros((H, W), dtype=np.int64)
    for y0 in range(H):
        for x0 in range(W):
            y, x = y0, x0
            seen = set()
            for s in range(FLOW_STEPS):
                if (y, x) in seen:
                    break
                seen.add((y, x))
                A[y, x] += 1
                if s > S[y, x]:
                    S[y, x] = s
                    R[y, x] = x0 * 31 + y0
                y, x = int(nxt[y, x, 0]), int(nxt[y, x, 1])
    norm = np.log(1.0 + A.max() * FLOW_NORMALISE)
    an = np.clip(np.log(1.0 + A) / norm, 0.0, 1.0)
    amount = np.clip((an - FLOW_START) / FLOW_RANGE, 0.0, 1.0)
    phase = np.zeros((H, W))
    for y in range(H):
        for x in range(W):
            phase[y, x] = (S[y, x] / PHASE_PERIOD + hash01(int(R[y, x]))) % 1.0
    return amount, phase


def build(height):
    """height: HxW normalised luminance (MaterialAtlas2Builder's height)."""
    pool, keep = joints(height)
    sh = shape(keep, height)
    amount, phase = flow(sh, keep)
    return {"pool": pool, "shape": sh, "flow": amount, "phase": phase, "joint": keep}
