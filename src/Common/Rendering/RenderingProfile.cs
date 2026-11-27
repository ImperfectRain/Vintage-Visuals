using VintageVisuals.Common.Diagnostics.Profiling;

namespace VintageVisuals.Common.Rendering
{
    /// <summary>
    /// D99. What the profiler measures for the depth prepass and the quality tier. Found by reflection
    /// (<see cref="IProfileDeclarer"/>); keep it beside the code it describes
    /// and update it when a patch group, setting or renderer is added here.
    /// </summary>
    internal sealed class RenderingProfile : IProfileDeclarer
    {
        public void DeclareProfile(ProfileRegistry r)
        {
            r.Declare("depthprepass", "Depth prepass")
             .Groups(DepthPrepass.GroupName)
             .Settings("Performance.DepthPrepass")
             .Sweep("perf_prepass")
             .CpuScopes(DepthPrepass.CpuSpanName)
             .GpuSpans("chunks.prepass", "chunks.prepass.0", "chunks.prepass.1")
             .Scenes("F", "V")
             .Note("the governor (D95) may stand it aside where it does not pay; '.vvbench prepass' splits it per pool")
             .ActiveWhen(c => c.Performance.DepthPrepass);

            r.Declare("terrainsplit", "Split chunk programs")
             .Groups(ChunkProgramSplit.GroupName)
             .Settings("Performance.SplitPrograms")
             .Sweep("perf_split")
             .CpuScopes(ChunkProgramSplit.CopySpanName, ChunkProgramSplit.BuildSpanName)
             .GpuSpans("chunks.1")
             .Scenes("F", "V")
             .Note("D100: the prepass and the leaf pool draw with twin programs; '.vvprofile shaders' prints each program's registers")
             .ActiveWhen(c => c.Performance.SplitPrograms);

            r.Declare("quality", "Quality tier")
             .Settings("Performance.Quality")
             .Sweep("perf_quality")
             .Scenes("F", "P", "V")
             .Note("scales canopy taps, detail distance, water blur, ripple layers and caustic waves at once");
        }
    }
}
