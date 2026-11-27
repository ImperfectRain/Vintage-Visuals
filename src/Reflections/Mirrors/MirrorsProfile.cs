using VintageVisuals.Common.Diagnostics.Profiling;

namespace VintageVisuals.Reflections.Mirrors
{
    /// <summary>
    /// D99. What the profiler measures for mirror surfaces. Found by reflection
    /// (<see cref="IProfileDeclarer"/>); keep it beside the code it describes
    /// and update it when a patch group, setting or renderer is added here.
    /// </summary>
    internal sealed class MirrorsProfile : IProfileDeclarer
    {
        public void DeclareProfile(ProfileRegistry r)
        {
            r.Declare("mirrors", "Mirror surfaces (player-marked)")
             .Groups(MirrorsSubsystem.GroupName)
             .Requires("pbrterrainmaterial")
             .Renderers("vintagevisuals-mirrors")
             .GpuSpans("vv.mirrors")
             .Scenes("M")
             .Note("settings live in ModConfig/vintagevisuals-mirrors.json, not the panel; cost scales with visible planes " +
                   "(the vv mirror cameras span), so measure in a room with 2 planes and again with 4");
        }
    }
}
