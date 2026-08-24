# Visual Tuning Studio

The mod's own in-game tuning panel. A **configuration client**, not a rendering
subsystem: it edits `VintageVisualsConfig` and calls `ConfigManager.NotifyChanged()`,
and that is the entire extent of its contact with the renderer.

## The rule this exists under

```
User ──┬── Visual Tuning Studio ──┐
       ├── ConfigLib bridge ──────┤
       └── JSON / presets ────────┴──> ConfigManager ──> ConfigChanged ──> subsystems
```

One configuration object. One notification path. The studio is a third writer
alongside the two that already existed, exactly as `ConfigLibBridge` is a second
writer, and for the same reason: a UI that kept its own copy of a value is the
same defect as a subsystem keeping its own copy of the weather, and this project
has already paid for that one.

**The studio never touches GL.** No `Use()`, no uniform upload, no texture, no
`ReloadShaders()`. Those belong to the subsystems and to `VintageVisualsModSystem`,
which already detects a patch-gating change on the config-changed path and
schedules the reload itself. A dialog calling into the shader lifecycle from a
click handler is precisely the class of bug that cost this project three
debugging rounds.

## Files

| File | What it owns |
|---|---|
| `VisualSetting.cs` | What one setting looks like to a player. Holds no value. |
| `VisualSettingRegistry.cs` | Every setting the studio shows, in order, explicitly registered. |
| `ConfigAccess.cs` | Reading and writing one config property named by a dotted path. |
| `DebugViewRegistry.cs` | Friendly names for every numbered diagnostic the shaders implement. |
| `VisualTuningStudioController.cs` | The live mutation boundary: set, reset, debug select, notify once. |
| `VisualTuningStudioDialog.cs` | The native Vintage Story dialog shell and row composition. |

## Values live in the config; presentation lives here

The split is deliberate and load-bearing. The config is the authority for what a
value **is**; this layer is the authority for what it is **called**, where it
appears, and what the player is told it does. They are joined by a dotted path
string — `"PseudoPBR.NormalStrength"` — rather than by a lambda pair, because a
string can be checked. `tools/smoketest` resolves every path in the registry and
fails on the first one that does not exist, so renaming a config property breaks
a test rather than one silent slider.

Nothing in this directory stores a setting value.

## Why the registry is explicit rather than reflected

Walking the config object and emitting a slider per property would be a third of
the code and the wrong tool. Several properties are diagnostics rather than
tuning controls. Several want a different widget than their type implies. Several
belong under a heading their C# class does not match. And none of them carry a
sentence saying what they visibly do.

Reflection can find a float. It cannot find out that `RoughnessBias` belongs under
"Material response" and means *"shifts every surface toward polished or toward
worn"*.

## Debug views keep their per-shader numbering

`CLAUDE.md` records why diagnostics are numbered per shader: one global list
stopped making sense as soon as two subsystems wanted the same number. The studio
does not flatten that. Each entry in `DebugViewRegistry` carries the config
property that owns it, so choosing a name resolves to *(that property, that
number)* and nothing about how a shader reads a debug view changes.

What does change is that nobody has to know the number. "Debug View 13" is not a
user interface — it is a number to look up in a JSON comment, usable only by
someone who already knows the answer.

## The duplication with ConfigLib, declared

`assets/vintagevisuals/config/configlib-patches.json` already carries labels,
comments, ranges and defaults for the settings ConfigLib shows. This registry
does not read it — a JSON file cannot supply a typed accessor, a widget choice or
a tab — so the two overlap and could drift.

Rather than pretend otherwise, **the codes here are ConfigLib's own codes**, and
`tools/smoketest` compares the two on range and default for every code they share,
and separately requires that the studio exposes everything ConfigLib does. The
migration path, when someone wants it, is to generate the ConfigLib JSON *from*
this registry. That is not this pass.

That check earned its place on its first run: it found six atmosphere defaults
that the ConfigLib file still reported as `0` after the C# defaults had been
raised, and four settings whose codes had drifted apart entirely.

## What the checks cannot say

That the dialog looks right, reads well, or is usable. Every check here is about
the boundary between the studio and the configuration it edits — a label pointing
at a real property, a friendly name pointing at a real shader arm, a range that
survives the config's own clamp. Whether the panel is a good tool is a runtime
question and stays one.

## Status

**L2 — native dialog implemented and statically verified. It has not been opened
in a running game.** The next gate is an in-game UX pass before presets,
import/export or persistence are built.
