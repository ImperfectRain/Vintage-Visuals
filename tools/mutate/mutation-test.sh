#!/usr/bin/env bash
#
# Break the mod on purpose, one defect at a time, and require the suite to
# notice.
#
# WHY. Seven defects have been found in this mod by looking at the game and zero
# by the checks that were green while they shipped. A passing suite is therefore
# not evidence of anything on its own - it has been passing throughout. The only
# statement worth making about a check is that it FAILS when the thing it guards
# is broken, and the only way to make that statement is to break it.
#
# Each row of mutations.tsv reintroduces a real historical defect, or the next
# instance of a class that has already bitten once. The harness applies it, runs
# tools/smoketest, and requires a failure whose name starts with the expected
# invariant. Anything else - a pass, or a failure somewhere unrelated - is
# reported as a hole in the suite.
#
# Every mutation is reverted with `git checkout --`, so the tree must be clean.
set -uo pipefail

cd "$(dirname "$0")/../.."

if [ -n "$(git status --porcelain)" ]; then
    echo "working tree is dirty - mutations are reverted with 'git checkout --', so refusing" >&2
    exit 2
fi

if [ -z "${VINTAGE_STORY:-}" ]; then
    echo "VINTAGE_STORY must point at a folder holding VintagestoryAPI.dll" >&2
    exit 2
fi

pass=0
fail=0
log=$(mktemp)
trap 'rm -f "$log"' EXIT

while IFS=$'\t' read -r label file from to expect; do
    label=${label%$'\r'}
    file=${file%$'\r'}
    from=${from%$'\r'}
    to=${to%$'\r'}
    expect=${expect%$'\r'}

    case "$label" in ''|\#*) continue ;; esac

    hits=$(grep -F -c -- "$from" "$file" 2>/dev/null || echo 0)
    if [ "$hits" != "1" ]; then
        echo "  STALE $label -- matched $hits times in $file, expected 1"
        fail=$((fail + 1))
        continue
    fi

    python3 - "$file" "$from" "$to" <<'PY'
import sys
path, old, new = sys.argv[1], sys.argv[2], sys.argv[3]
s = open(path).read()
assert s.count(old) == 1
open(path, 'w').write(s.replace(old, new, 1))
PY

    dotnet run --project tools/smoketest >"$log" 2>&1
    status=$?
    caught=$(grep '^  FAIL' "$log" | grep -c -- "$expect" || true)
    total=$(grep -c '^  FAIL' "$log" || true)

    git checkout -- "$file"

    if [ "$caught" -gt 0 ]; then
        echo "  CAUGHT  $label -- $caught check(s) matching '$expect' failed"
        pass=$((pass + 1))
    elif [ "$status" -ne 0 ] && [ "$total" -eq 0 ]; then
        echo "  RUNFAIL $label -- smoke test did not run to checks"
        head -20 "$log"
        fail=$((fail + 1))
    else
        echo "  MISSED  $label -- expected a '$expect' failure, got $total failure(s)"
        grep '^  FAIL' "$log" | head -3
        fail=$((fail + 1))
    fi
done < tools/mutate/mutations.tsv

echo
echo "$pass mutation(s) caught, $fail missed"
[ "$fail" -eq 0 ]
