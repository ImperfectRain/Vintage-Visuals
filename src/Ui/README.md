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
| `SettingAvailability.cs` | Whether a setting can change the picture in this build and config, and if not, why not. |
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

The two terrain shaders share one number space but not one set of views. Each
terrain entry says which path draws it (`TerrainPaths`: legacy patch, modular
material path, or both), and the Studio offers only the set the active path
implements. `tools/smoketest` compares each set to its own shader's `mode == N`
arms, so the list in the dropdown is read from the registry rather than from a
second hand-kept list in the dialog.

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

## UX shape

The studio is tuned for scan-first editing, not permanent documentation density.
Subsystems live in a fixed vertical navigation rail. Setting pages use two-line
rows with stable columns:

```
label            description, or where to look / why it is off
control (most of the width)                      value   [Reset]
```

Reset has a fixed slot and is shown only while the row is modified. After every
edit the row is re-read from the config - after clamping - so the thumb, value,
toggle label, dropdown and Reset always agree with what the renderer receives.
Every row element lives in a native `GuiElementContainer`, not on the composer.
The composer bakes the static part of each element it owns - text, slider track,
switch background, dropdown box - into one texture at compose time and does not
clip it; the container owns its own texture and draws it at its current bounds
inside the clip, so whole rows scroll and clip together.

Rows report one of four availability states (`SettingAvailability.cs`): active;
active only in some scenes (a working control with a note on where to look);
parent off (no control, "Turn on X"); and unavailable in this build (hidden; shown
disabled with the reason when **Show advanced and unavailable settings** is on).

Short descriptions are visible on each row because hover-only help is still too
easy to miss and too hard to validate from screenshots. The setting name and
current value remain visible at all times. Rows are clipped inside a native
scroll region with a draggable vertical scrollbar; the scroll operation moves the
child bounds instead of rebuilding the composer, so the navigation rail and footer
stay fixed. The dialog has ONE wheel handler and does not pass the wheel to the
native composer first, because that offers it to every element regardless of the
pointer: an open dropdown owns the wheel, the viewport and its scrollbar scroll,
and anywhere else in the dialog the wheel is consumed.

The Overview page is a set of subsystem cards with status, summary, description
and an explicit Open action. The Debug page is a tool surface: choose a debug
system, then a named debug view, with an active-view banner and a single Return
to Normal Rendering action. The underlying per-shader debug numbers do not
change.

## Status

**L2 — live row feedback, scoped refresh, one wheel owner, availability
([D47](../../docs/DECISIONS.md)).** Built against the 1.22 API and covered by
`tools/smoketest` and `tools/mutate`; not yet opened in a running game. The
acceptance tests at the end of this file are what moves it on.

Earlier, and still true: **production lifecycle simplification.** The crash-isolation footer and
deferred recomposition scheduler are removed. This build keeps the stable shaded
background and compact layout, but scroll/wheel handling now updates the native
scroll child bounds in place. Live setting edits do not rebuild the whole
composer, per-tab scroll positions are retained, and callbacks still carry
composer generation guards with `[VV UI #]` lifecycle logging so stale callbacks
from disposed UI trees are ignored.

The current layout is also constrained by a single grid: sidebar width/padding,
content left edge, label/info/value/control/reset columns and footer placement
are centralized in `VisualTuningStudioDialog`. The page title, section headings
and setting labels share one content axis; unfinished Presets text and the
redundant secondary Close button are removed. The settings body is a positioned
content child, then a local clip, then a local scroll child with local row
coordinates; using dialog coordinates inside that nested body double-applies the
content offset, while attaching the local scroll child directly to the root lets
rows escape the inset.
The sidebar uses a full-width button stack instead of native `AddVerticalTabs`
because the native element sizes its rendered tabs from text width. It has not
been re-opened in a running game from this environment, so presets,
import/export and persistence remain blocked.

## In-game acceptance tests

Nothing below has been run. The build, `tools/smoketest` and `tools/mutate` say
the code has the shape described above; only these say it works.

1. **Scroll every tab at two UI scales.** Wheel over labels, gaps, sliders and an
   open dropdown. Drag the scrollbar, reach the last row, switch tabs and come
   back. The list and handle agree, each tab keeps its own position, a clipped
   row never takes a click or the wheel, and an open dropdown scrolls its own
   list instead of the page. Wheel over a slider must scroll, not change it.
2. **Prove the edit reaches the screen.** In one daylight scene drag Exposure:
   the number and Reset follow the thumb during the drag, and the image changes.
   Then sweep Surface relief on close stone or planks with light across them
   (not grass or leaves, which this build excludes). If the number moves and the
   image does not, trace the shader and its uniform before touching the UI.
3. **Stress refresh and availability.** Make a no-op edit (drag a slider and
   return it), then change a setting through ConfigLib or edit the JSON and
   reload: the open studio must show the new value. Reset one row, switch tabs
   repeatedly, close and reopen. Foliage, sun shafts, emissive glow and the
   reflection rows must not appear as working controls; with Advanced on they
   appear disabled with a reason. Turn Surface materials off: its rows say
   "Turn on Surface materials".

