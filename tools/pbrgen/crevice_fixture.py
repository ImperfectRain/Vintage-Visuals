#!/usr/bin/env python3
"""Generate the parity fixture for src/PseudoPBR/CreviceMaps.cs (D67).

crevice.py is the reference the effect was tuned and approved on; the C# port
is what ships. tools/smoketest rebuilds the same input in C#, runs the port,
and compares every texel of every map against this file.

The input is a brick wall drawn from integer arithmetic only - three courses of
stones with offset vertical joints, a painted shading gradient, and dark specks
that must NOT become joints. Integer formulas, not sin() or an RNG, so both
languages build a bit-identical texture.

Regenerate with:  python3 tools/pbrgen/crevice_fixture.py
"""

from __future__ import annotations

import json
import pathlib

import numpy as np

import crevice

SIZE = 32
OUT = pathlib.Path(__file__).parent / "crevice_fixture.json"


def speck(x, y):
    return (x * 7 + y * 11) % 23 == 0


def texel(x, y):
    """Luminance byte of the fixture texture. Mirrored in the C# check."""
    course = y // 11
    row = y % 11
    offset = (course * 7) % 16
    col = (x + offset) % 16
    if row < 2 or col < 2:
        return 40 + ((x * 13 + y * 7) % 5) * 4            # mortar joint
    v = 150 + ((x * 3 + y * 5) % 9) * 6 - row * 3         # stone + painted shading
    if speck(x, y) or speck(x - 1, y) or speck(x, y - 1) or speck(x - 1, y - 1):
        v = 45                                            # a 2x2 speck, as dark as mortar
    return v


def build_input():
    lum = np.array([[texel(x, y) / 255.0 for x in range(SIZE)] for y in range(SIZE)])
    return (lum - lum.min()) / (lum.max() - lum.min())


def main():
    maps = crevice.build(build_input())
    doc = {"size": SIZE}
    for name in ("joint", "pool", "shape", "flow", "phase"):
        doc[name] = [round(float(v), 6) for v in maps[name].ravel()]
    OUT.write_text(json.dumps(doc, separators=(",", ":")) + "\n")
    j = maps["joint"]
    print(f"wrote {OUT.name}: joint texels {int(j.sum())}, flow>0 {(maps['flow'] > 0).sum()}")


if __name__ == "__main__":
    main()
