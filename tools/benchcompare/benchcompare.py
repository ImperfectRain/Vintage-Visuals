#!/usr/bin/env python3
"""Compare two Vintage Visuals profiler result files (D99).

    python3 tools/benchcompare/benchcompare.py OLD-summary.csv NEW-summary.csv
    python3 tools/benchcompare/benchcompare.py RESULTS-summary.csv        # one file: a cost table

Reads the *-summary.csv files the in-game profiler writes to
VintagestoryData/vv-bench/ (one row per variant of each plan: median cost
against the plan's baseline and its spread). Rows are matched on
(kind, subject, variant, scene). A change counts only when it is bigger than
the two runs' noise added together; everything else is printed as "same".

Also warns when the two files were taken with different game settings
(resolution, view distance, shadows, quality), since then nothing compares.
Standard library only.
"""
import csv
import sys

KEY = ("kind", "subject", "variant_key", "scene")


def load(path):
    with open(path, newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    # Later rows win: a re-run of the same plan replaces the earlier one.
    out = {}
    for r in rows:
        out[tuple(r.get(k, "") for k in KEY)] = r
    return out


def num(r, k):
    try:
        return float(r.get(k, ""))
    except (TypeError, ValueError):
        return None


def cost(r):
    g = num(r, "gpu_cost_ms")
    return g if g is not None else num(r, "cost_ms")


def fmt(v):
    return "   n/a" if v is None else "%+6.2f" % v


def table(rows):
    print("%-8s %-28s %-34s %8s %7s  %s" % ("kind", "subject", "variant", "cost ms", "noise", "verdict"))
    for key in sorted(rows):
        r = rows[key]
        if r.get("significant") == "baseline":
            continue
        print("%-8s %-28s %-34s %8s %7s  %s" % (r["kind"], r["subject"][:28], r["variant"][:34], fmt(cost(r)),
                                              r.get("noise_ms", ""), r.get("significant", "")))


def compare(old, new):
    changed = 0
    print("%-8s %-26s %-30s %8s %8s %8s  %s" % ("kind", "subject", "variant", "old", "new", "change", "verdict"))
    for key in sorted(set(old) | set(new)):
        a, b = old.get(key), new.get(key)
        if (a or b).get("significant") == "baseline":
            continue
        if a is None or b is None:
            print("%-8s %-26s %-30s %s" % (key[0], key[1][:26], (a or b)["variant"][:30],
                                           "only in " + ("new" if a is None else "old")))
            continue
        ca, cb = cost(a), cost(b)
        if ca is None or cb is None:
            continue
        noise = (num(a, "noise_ms") or 0.05) + (num(b, "noise_ms") or 0.05)
        delta = cb - ca
        verdict = "same" if abs(delta) <= noise else ("WORSE" if delta > 0 else "better")
        if verdict != "same":
            changed += 1
        print("%-8s %-26s %-30s %8s %8s %8s  %s" % (key[0], key[1][:26], b["variant"][:30], fmt(ca), fmt(cb), fmt(delta), verdict))
    print("\n%d change(s) bigger than the noise." % changed)


def runs_file(summary_path):
    return summary_path.replace("-summary.csv", "-runs.csv")


def settings_of(summary_path):
    keys = ("width", "height", "render_scale", "view_distance", "shadows", "ssao", "godrays", "vsync", "max_fps", "quality", "gpu")
    try:
        with open(runs_file(summary_path), newline="", encoding="utf-8") as f:
            for r in csv.DictReader(f):
                return {k: r.get(k, "") for k in keys}
    except OSError:
        return None
    return None


def main(argv):
    if len(argv) == 2:
        table(load(argv[1]))
        return 0
    if len(argv) != 3:
        print(__doc__)
        return 2
    sa, sb = settings_of(argv[1]), settings_of(argv[2])
    if sa and sb:
        diff = {k: (sa[k], sb[k]) for k in sa if sa[k] != sb[k]}
        if diff:
            print("WARNING: the two files were taken with different settings:")
            for k, (x, y) in diff.items():
                print("  %s: %s -> %s" % (k, x, y))
            print()
    compare(load(argv[1]), load(argv[2]))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
