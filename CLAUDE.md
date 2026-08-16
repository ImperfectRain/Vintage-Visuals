# CLAUDE.md — standing instructions for Vintage Visuals

Client-side Vintage Story visual overhaul mod: GLSL shader patches + a C# code
mod. Four independent subsystems — ColorGrade, Weather, Reflections, PseudoPBR.

## Build / run

```sh
export VINTAGE_STORY=/path/to/VintageStory   # folder holding VintagestoryAPI.dll
dotnet build                                  # -> bin/Debug/Mods/vintagevisuals/
dotnet build -c Release                       # also zips to Releases/
```

Copy or symlink `bin/Debug/Mods/vintagevisuals/` into your VintagestoryData
`Mods/` folder to test. There is no unit test suite for the mod assembly —
it is verified by running the game (see "Verification" below).

The offline PBR tool is a separate Python program, not part of the build:

```sh
python3 tools/pbrgen/pbrgen.py <in.png> --outdir out/    # generate maps
python3 -m pytest tools/pbrgen/                          # its tests DO run in CI-less envs
```

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
  runtime code; never reference it from the C# build.
- `docs/WORKFLOW.md` — the commit/doc conventions this repo follows. Read it
  before committing.
- `docs/IMPLEMENTATION_PLAN.md` — phase order and the MVP checklist. Phases are
  gated: do not start a phase until the previous phase's milestone is verified.

## Non-obvious constraints

- **Shader patches regex-match vanilla GLSL that changes every game version.**
  Before trusting any existing patch, verify its match string against the
  *currently installed* game's `assets/game/shaders/`. A patch that silently
  stops matching produces "mod does nothing", not a crash.
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
  path. ConfigLib integration, when it lands, must stay optional.
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

## Conventions

Conventional Commits, one logical change per commit, body explains *why*.
Update `CLAUDE.md` in the same commit as the convention change that caused it;
update the subsystem `README.md` when that subsystem's behavior changes;
update `CHANGELOG.md` at milestones only. Full detail in `docs/WORKFLOW.md`.
