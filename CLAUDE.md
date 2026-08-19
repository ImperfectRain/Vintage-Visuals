# CLAUDE.md — standing instructions for Vintage Visuals

Client-side Vintage Story visual overhaul mod: GLSL shader patches + a C# code
mod. Four independent subsystems — ColorGrade, Weather, Reflections, PseudoPBR.

## Build / run

```sh
export VINTAGE_STORY=/path/to/VintageStory   # folder holding VintagestoryAPI.dll
dotnet build                                  # -> bin/Debug/Mods/vintagevisuals/
dotnet build -c Release                       # also zips to Releases/
```

No `-p:` flags needed: the project defaults to **net10.0**, which is correct for
1.22+ and is confirmed building clean against 1.22.7.

`TargetFramework` must be **at least** the framework the installed game's
`VintagestoryAPI.dll` targets — net10.0 for 1.22+, net8.0 for 1.21. This is a
compile-time rule that runs opposite to how runtime loading works: a net8.0
assembly loads fine on a .NET 10 runtime, but you cannot *compile* against a
reference assembly targeting a higher framework. Too low gives dozens of MSB3277
warnings and then `error CS1705: ... uses 'System.Runtime, Version=10.0.0.0'
which has a higher version than referenced assembly`. Override in
`Properties/localSettings.props`, not on the command line — `-p:TargetFramework=`
skips restore and fails with `NETSDK1005` instead.

Copy or symlink `bin/Debug/Mods/vintagevisuals/` into your VintagestoryData
`Mods/` folder to test.

```sh
dotnet run --project tools/smoketest     # patch engine checks, no game needed
dotnet run --project tools/verifypatches # patches vs the game's OWN shaders
```

That drives the compiled patch engine against the real patch YAML: parsing,
anchor matching, and group rollback. It cannot tell you whether the anchors
match *your* game's shaders, or whether anything renders — see "Verification".

The offline PBR tool is a separate Python program, not part of the build:

```sh
python3 tools/pbrgen/pbrgen.py <in.png> --outdir out/    # generate maps
python3 -m pytest tools/pbrgen/                          # its tests DO run in CI-less envs
```

## Reference shaders

`reference/game-shaders/` holds the game's own shaders as
`EnableShaderDebugDump` writes them. **Gitignored and never committed** - they
are Vintage Story's assets, not this project's.

`tools/verifypatches` applies every shipped patch group to them and compiles the
result in all 48 combinations of the game's prefix defines. That is a different
question from `tools/smoketest`, which checks the engine against stand-in
shaders: an anchor can match a hand-written fixture perfectly and miss the real
file because the game reworded a line. Run it before claiming level 2 on
anything touching GLSL.

To refresh the dumps, **switch every subsystem off first**
(`ColorGrade.Enabled` and `PseudoPBR.Enabled` both false), then set
`EnableShaderDebugDump`, load a world, and copy `VintagestoryData/ShaderDebug/`
over the directory. Every group is gated on its subsystem's flag precisely so
this produces clean vanilla source - a dump taken with the mod half on contains
the mod's own injections, and patching that reports success while proving
nothing. `verifypatches` refuses such a file by name rather than trusting it.

## Where things live

- `src/Common/` — shader patch engine, config, Harmony hooks. Subsystem-agnostic.
- `src/ColorGrade/`, `src/Weather/`, `src/Reflections/`, `src/PseudoPBR/` — one
  folder per subsystem, each with its own `README.md`.
- `assets/vintagevisuals/shaderpatches/*.yaml` — regex/token patches against
  **vanilla** shaders, one file per subsystem.
- `assets/vintagevisuals/shadersnippets/*.glsl` — GLSL bodies the patches inject.
  Kept out of the YAML so the GLSL stays readable and diffable.
- `assets/vintagevisuals/shaders/` — our own standalone shader programs.
- `tools/pbrgen/` — offline PBR prototype (Python). Content-authoring aid, **not**
  runtime code; never reference it from the C# build. `src/PseudoPBR/` is a
  **port** of it: `tools/smoketest` asserts the two agree on a fixture, so a
  retune means editing both and regenerating `tools/pbrgen/parity_fixture.json`.
- `docs/WORKFLOW.md` — the commit/doc conventions this repo follows. Read it
  before committing.
- `docs/IMPLEMENTATION_PLAN.md` — phase order and the MVP checklist. Phases are
  gated: do not start a phase until the previous phase's milestone is verified.

## Non-obvious constraints

- **Shader patches regex-match vanilla GLSL that changes every game version.**
  Before trusting any existing patch, verify its match string against the
  *currently installed* game's `assets/game/shaders/`. A patch that silently
  stops matching produces "mod does nothing", not a crash.
- **`#include` directives are already expanded when we see the source.** The
  hook runs on `ShaderRegistry.LoadShader`, and by then `chunkopaque.fsh` is one
  flat ~1180-line file with `vertexflagbits.ash`, `fogandlight.fsh` and
  `colormap.fsh` inlined. Two consequences: an include is never its own patch
  target (unlike in Volumetric Shading, which hooks earlier), and **where** in
  the file a patch injects matters — a `start` patch lands above declarations
  that live in the includes, so anything using e.g. `lightPosition` must anchor
  below it instead. Confirm with `EnableShaderDebugDump`, which writes the exact
  source handed to the compiler.
- **Shipped GLSL must be plain 7-bit ASCII, comments included.** GLSL's source
  character set is ASCII, and NVIDIA's frontend rejects the whole shader rather
  than the character: thirteen em dashes in `pseudopbr.glsl`'s comments produced
  `error C0000: syntax error, unexpected $end at token "<EOF>"` on
  `chunkopaque.fsh` and took the world render with it. `glslangValidator`
  compiled the identical file cleanly in all 48 settings combinations, so a
  passing validator proves nothing here. `ShaderPatchLoader` now refuses
  non-ASCII patch content at load time, and `tools/smoketest` scans the shipped
  assets.
- **Never declare a sampler above vanilla's.** Sampler texture units are
  assigned at LINK time from the program's active sampler list, so inserting one
  above vanilla's shifts every sampler below it. In `chunkopaque.fsh` that
  pushed `liquidDepth` off the end, `getUnderwaterMurkiness()` saturated, and
  `applyUnderwaterEffects` mixed every terrain fragment to the water murk
  colour — a world that looked transparent, with only unpatched `chunktopsoil`
  geometry left. `glslangValidator` passes this happily; the fault is in the
  driver's link step, not the compile. Anchor injections on the LAST vanilla
  sampler (`uniform sampler2D liquidDepth`) and pin the ordering with a test.
- **Texture units in vanilla shaders are all taken.** `chunkopaque.fsh` declares
  seven samplers, so units 0..6 are vanilla's. Binding a mod texture over one of
  them does not break the mod's effect, it breaks the frame. Count the
  `uniform sampler` lines in the dumped shader before choosing a unit, and
  prefer the top of the range OpenGL 3.3 guarantees (0..15).
- **Never call `IShaderProgram.Use()` off the render thread.** It THROWS
  ("Already a different shader (gui) in use!") when a program is already bound,
  and the client does not recover — OpenGL goes to `InvalidOperation` and the
  world stops drawing. A ConfigLib slider fires mid-frame with the gui shader
  bound, so a config handler must never touch GL. Set a dirty flag and upload
  from an `IRenderer` at `EnumRenderStage.Before`, guarded by
  `capi.Render.CurrentActiveShader != null`. The same applies to creating
  textures: `LoadOrUpdateTextureFromRgba` binds to the active unit as a side
  effect.
- **`configlib-patches.json` is unvalidated data with a silent failure mode.**
  ConfigLib parses it, builds the F7 panel from it, and raises events back by
  setting code - every link is a string. Two settings sharing a `weight` blanks
  the WHOLE panel, not one row. A settings array instead of a keyed object
  yields zero settings. Neither shows up in a build or a log.
  `tools/smoketest` now checks weights, codes, ranges, and that every setting
  has a `case` in `ConfigLibBridge` and vice versa.
- **A config flag for a shader patch must gate the PATCH, not the effect.**
  Muting a uniform leaves the patched GLSL compiling and occupying a sampler, so
  when the damage comes from the source existing at all, the player's off switch
  does nothing. `IsPatchGroupEnabled` in `VintageVisualsModSystem` skips the
  whole group and reloads shaders, so "off" means vanilla source.
- **A varying belongs to ONE patch group.** It is a contract between the `.vsh`
  and `.fsh` of the same program: if the vertex half could roll back
  independently, the fragment shader would declare an input nothing writes and
  the program would not link, costing the world rather than the feature. The
  sky-exposure varying lives in `pseudopbr.yaml`, not a weather group.
- **Zero must mean "behave like vanilla" for every uniform.** An unset GLSL
  uniform reads as exactly 0, and a uniform can be unset for many reasons - the
  binder skipped, the program was not patched, a group rolled back. So 0 has to
  be the harmless value. `vv_cloudDensity` multiplied vanilla's density term,
  where 0 meant NO CLOUDS AT ALL rather than "normal clouds", and that shipped.
  Check the zero case of every new uniform before adding it.
- **A declared uniform is not an uploaded uniform.** An unset GLSL uniform reads
  as zero, and zero is a legal value, so a missing `program.Uniform(...)` call
  is invisible from C# and from the log. Five shipped that way at once. The
  fix is `tools/smoketest`'s uniform-wiring check, which compares the shader's
  declarations against the binder's uploads in both directions.
- **A patch that replaces its anchor must paste the anchor back.** Replacement
  content is literal, never a regex template. `pseudopbr.glsl` re-declares
  `uniform vec3 lightPosition;` for this reason.
- **Never let a failed patch take down the game.** Patches are grouped by
  subsystem; a failure disables that subsystem, logs `[VintageVisuals] CRITICAL
  shader patch failure in <group>`, and lets everything else load. This is
  deliberate — see `src/Common/ShaderPatchLoader.cs`.
- `VintagestoryLib` is **not** a stable API. Every type we touch there
  (`ShaderRegistry`, `ShaderProgram`, `ShaderProgramBase`) can change without
  notice. All Harmony patches must be reflection-guarded and must log loudly
  rather than throw. Prefer prefix/postfix over transpilers — we removed the
  transpiler approach on purpose, see `src/Common/HarmonyPatches.cs`.
- The mod is **client-side only**. `ShouldLoad` returns false for the server.

## What NOT to do

- Do not hand-edit anything under `generated/` or `tools/pbrgen/out/` — both are
  regenerated, and both are gitignored.
- Do not commit derived atlases, `bin/`, `obj/`, or `Properties/localSettings.props`.
- Do not add a hard dependency on another mod without a graceful-degradation
  path. The ConfigLib integration (`src/Common/ConfigLibBridge.cs`) is the
  reference for how: it talks to ConfigLib purely over the game's event bus, so
  it references no ConfigLib type, appears nowhere in `modinfo.json`, and needs
  no fallback code path — absent ConfigLib, the events just never fire.
- Do not bundle a shader change with an unrelated refactor in one commit —
  `git bisect` on broken rendering is the main debugging tool here.

## Verification

Anything touching GLSL or the render pipeline **cannot be verified by building**.
State plainly what was and was not verified. The honest levels are:

1. *Parses* — YAML/JSON syntax checked, GLSL snippet reviewed by eye.
2. *Compiles* — `dotnet build` succeeded against a real game install.
3. *Loads* — game starts with the mod, log shows every patch group applied.
4. *Renders* — the visual change is actually visible and survives a world reload.

Only (4) closes a milestone. Say which level you reached.

Level 2 goes further than `dotnet build` for anything that patches GLSL. Dump
the real shader, run the patch engine over it, and compile the result:

```sh
apt-get install -y glslang-tools
glslangValidator patched.frag      # silence means it compiled
```

`#version` must stay line 1; strip `#extension` lines glslang does not know and
inject `#define`s for `SSAOLEVEL`, `SHADOWQUALITY`, `GODRAYS`, `NORMALVIEW` and
`SHINYEFFECT`, which the game supplies as prefix code. Sweep every combination
and compile vanilla alongside patched, so a failure is attributable to the patch
rather than to the harness.

## Conventions

Conventional Commits, one logical change per commit, body explains *why*.
Update `CLAUDE.md` in the same commit as the convention change that caused it;
update the subsystem `README.md` when that subsystem's behavior changes;
update `CHANGELOG.md` at milestones only. Full detail in `docs/WORKFLOW.md`.
