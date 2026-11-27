# Profiling (D99)

The whole-mod profiler: what each system costs, measured the same way every
time, written to a file you can compare across builds. See
[D99](../../../../docs/DECISIONS.md) for why it is built this way.

## The rule for anything new

**Every feature declares itself.** A system that adds a patch group, a panel
setting, a per-frame renderer or a GPU span adds a small class implementing
`IProfileDeclarer` next to it (see `src/Water/WaterProfile.cs`):

```csharp
internal sealed class WaterProfile : IProfileDeclarer
{
    public void DeclareProfile(ProfileRegistry r)
    {
        r.Declare("water", "Water surface: sky reflection, waves, ripples, glints, depth tint")
         .Groups("water")
         .SettingsUnder("Water.")
         .Sweep("water_waves", 0f, 0.5f, 1f)
         .Renderers("vintagevisuals-water-uniforms")
         .Scenes("P", "R")
         .ActiveWhen(c => c.Water.Enabled);
    }
}
```

Declarers are found by reflection; nothing else has to be edited. Per-frame
renderers are registered through `ModRenderers.Register(capi, renderer, stage,
name)`, never `capi.Event.RegisterRenderer`, so their CPU time and allocations
are timed under `name` without the renderer knowing. Work inside a Harmony hook
or game event is timed with `using (CpuSpans.Measure("system.what")) { ... }`.

`tools/smoketest` (`ProfilingChecks.cs`) fails, by name, on a patch group,
non-debug panel setting, renderer or mod GPU span no system claims, on a raw
`Event.RegisterRenderer` outside `CpuSpans.cs` and `FrameProfiler.cs`, and on a
declared scope that nothing measures. Something that ships unclaimed anyway is
still measured under its raw name, and `.vvprofile coverage` lists it.

## Files

| File | What it is |
|---|---|
| `ProfileRegistry.cs` | the catalogue: systems, their groups, prerequisites, setting paths, sweeps, renderers, CPU scopes, GPU spans |
| `CpuSpans.cs` | always-on CPU ms and managed KB per frame per span; `ModRenderers` wraps every renderer |
| `BenchOverrides.cs` | forces a patch group in or out for a bench; consulted before the config and folded into the gating signature, so a change reloads shaders |
| `BenchRunner.cs` | the bench engine: plans of variants, ABBA rounds, reload waits, paired median deltas, noise, bottleneck |
| `GpuTelemetry.cs` | NVML clock, temperature, power, utilisation and throttle reasons, sampled off-thread; "n/a" without an NVIDIA driver |
| `BenchContext.cs` | build hash, GPU, resolution, render scale, view distance, shadows, SSAO, godrays, vsync, fps cap, tier, scene |
| `BenchResultsFile.cs` | `VintagestoryData/vv-bench/<date>-<build>-runs.csv` and `-summary.csv` |
| `BenchScenes.cs` | saved scenes per world; teleport, hour, weather held; frame-time settle probe |
| `SweepTarget.cs` | a panel setting as something to sweep; `IProfilerHost`, the mod's side of the contract |

The commands live in `../PerformanceTools.Profiler.cs`; the panel's Performance
tab has a Profiler section with buttons for the common ones.

## Commands

```
.vvbench groups [all|active|g1,g2|system] [secs] [rN] [@scene,...]
.vvbench sweep <setting code|config path|system> [v1,v2,...] [secs] [rN] [@scene]
.vvbench suite [quick] [secs] [rN] [@scene,...]
.vvbench stop | status
.vvscene save <name> [rain=0..1|auto] [wind=<code>] [note] | list | go <name> | delete <name> | release
.vvprofile coverage | cpu | systems
```

## How a number is made

- **Groups.** Each group is benched compiled out (the baseline) and compiled
  in, with its prerequisites held in and its dependents held out, so the delta
  is that group's source alone. A group forced in while its config is off
  measures the cost of the source with its effect at zero, which is the D90
  register-pressure cost.
- **Sweeps.** A slider runs at 0, 25, 50 and 100 % of its range, a toggle off
  then on, a choice every step; the first value is the baseline. The original
  value is restored at the end, and on stop or abort.
- **Rounds.** Variants run A B C, then C B A, and so on. Each variant's cost is
  the median, over rounds, of its difference from the baseline **in the same
  round**, so drift across the bench (heat, chunks, time) cancels. The noise is
  half the spread of those differences (never below 0.05 ms), and a result is
  significant only when it is larger than the noise.
- **Reloads.** A variant that changes the gating signature waits for the shader
  reload, then 5 s more; others wait 1.5 s.
- **Bottleneck.** GPU frame over frame time at 0.9 or more is GPU bound, 0.75
  mixed, below that CPU bound: a GPU saving in a CPU-bound scene does not show
  in fps.

## Known limitations

- Not yet run in game (L2). Wind pattern codes for `/weather setw` are not
  verified; `wind=` defaults to empty, which leaves the wind alone.
- The mirror subsystem is timed as one renderer; its internal steps are not split.
- `OnReloadShader` may fire at the start of a reload rather than the end; the
  5 s settle covers both.
- GPU telemetry is NVIDIA only.
- Cost of the always-on CPU spans: two timestamp and two allocation-counter
  reads per renderer per frame (about fifteen). Not measured.
