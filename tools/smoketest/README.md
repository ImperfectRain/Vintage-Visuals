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

## What it cannot check

- **Whether the anchors match your game's real `final.fsh`.** It uses a
  stand-in. Only the game can answer that; set `EnableShaderDebugDump` and read
  `VintagestoryData/ShaderDebug/final.fsh`.
- **The Harmony hook.** `ShaderRegistry` lives in `VintagestoryLib` and is
  resolved by name at runtime, so nothing here touches it.
- **Anything on the GPU** — whether the GLSL compiles, and whether the grading
  looks right.

A pass here means the plumbing is sound. It does not mean the mod works.
