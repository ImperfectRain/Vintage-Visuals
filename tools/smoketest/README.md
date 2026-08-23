# smoketest

Exercises the compiled shader patch engine without launching the game.

```sh
export VINTAGE_STORY=/path/to/VintageStory
dotnet run --project tools/smoketest
```

Exit code 0 means every check passed; it prints one line per check either way.

## Why this exists

Most of this mod cannot be verified without a GPU and a running client, and
that made "does the patch engine actually work" an open question for far too
long. But the parts most likely to break silently on a game update — YAML
parsing, anchor matching, group rollback — are pure string manipulation. They
need no window, no GL context and no world.

So this drives the **real compiled assembly** against the **real
`colorgrade.yaml`** and a stand-in `final.fsh`. It is not a mock of the logic;
it is the shipped code, called directly.

## What it checks

- `colorgrade.yaml` deserializes into the expected patches, kinds and group
- the `snippet:` indirection resolves GLSL into patch content
- applying the group to a vanilla-shaped `final.fsh` injects the uniforms and
  grading function, renames `main()`, appends the new one, and leaves exactly
  one `main()` with balanced braces and `#version` still first
- shaders the group does not target come back byte-identical
- **rollback**: renaming the fragment output makes the group fail, return the
  source untouched with no half-applied GLSL, mark itself unhealthy, log
  `CRITICAL`, and stay skipped for later shaders
- the patcher never throws on null or empty input — its caller is the game's
  shader loader, where an exception costs the client
- malformed patch files are rejected loudly rather than silently ignored
- reflowed vanilla source still matches, which is the whole point of the
  whitespace-insensitive token matcher

## Interaction invariants

`SceneInvariantChecks.cs` is a different KIND of check from everything above it,
and the difference is why it exists.

Seven defects have been found in this mod by looking at the game. This suite
found none of them, and it was green the whole time they shipped. Not for lack of
coverage - for lack of the right question. Every other check here asks about ONE
thing: is this uniform uploaded, does this anchor match, is this file ASCII.
Every one of the seven was about TWO things multiplied together - a gate
implemented as a dimmer, an effect multiplied by the complement of its own
trigger, a diffuse term removed with nothing paying it back.

So invariants I1-I10 run arithmetic on compositions. `GlslEval` evaluates the
subset of GLSL these compositions are written in, over expressions pulled out of
the shipped `.glsl` at test time rather than retyped into C#: only the leaf calls
that read textures and shadow maps are stubbed, so a factor someone adds to a
chain later is in the check whether or not anyone updates the check.

Each invariant names the defect it prevents and the commit that fixed it. One
with no defect behind it is not added - it would be a green line making the suite
look like evidence it is not.

The evaluator is itself under test, at the top of the category. If `smoothstep`
were wrong there, I1 would pass on a shader that suppresses sunsets: a green line
asserting the opposite of the truth, which is worse than no line.

## Whether these checks would fail

A suite that has been green throughout the period in which seven defects shipped
has demonstrated that it passes, and nothing about whether it would fail. So:

```sh
VINTAGE_STORY=/path/to/VintageStory bash tools/mutate/mutation-test.sh
```

That reintroduces each historical defect by exact substitution, runs this suite
against the broken tree, and requires the guarding check to fail - then reverts
with `git checkout --`, so it refuses to run on a dirty tree. 12 mutations, 12
caught. A row that is not caught is reported as a hole in the suite rather than
as a pass.

**Add a mutation whenever you add an invariant.** An unmutated check is a claim,
not evidence.

## What it cannot check

- **Whether the anchors match your game's real `final.fsh`.** It uses a
  stand-in. Only the game can answer that; set `EnableShaderDebugDump` and read
  `VintagestoryData/ShaderDebug/final.fsh`.
- **The Harmony hook.** `ShaderRegistry` lives in `VintagestoryLib` and is
  resolved by name at runtime, so nothing here touches it.
- **Anything on the GPU** — whether the GLSL compiles, and whether the grading
  looks right.
- **Whether any effect is VISIBLE.** Every interaction invariant passes on a mod
  with every strength defaulted to zero. That is a runtime question and stays
  one; `docs/VISUAL-TESTS.md` is where it is answered.

A pass here means the plumbing is sound. It does not mean the mod works.
