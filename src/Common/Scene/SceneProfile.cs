using VintageVisuals.Common.Diagnostics.Profiling;

namespace VintageVisuals.Common.Scene
{
    /// <summary>
    /// D99. What the profiler measures for the environment tracker. Found by reflection
    /// (<see cref="IProfileDeclarer"/>); keep it beside the code it describes
    /// and update it when a patch group, setting or renderer is added here.
    /// </summary>
    internal sealed class SceneProfile : IProfileDeclarer
    {
        public void DeclareProfile(ProfileRegistry r)
        {
            r.Declare("environment", "Environment tracker (canopy, ground and leaf-source scans)")
             .Renderers("vintagevisuals-environment")
             .Scenes("F")
             .Note("CPU only: block lookups for the canopy map and the leaf sources, every frame");
        }
    }
}
