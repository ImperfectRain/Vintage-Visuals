using VintageVisuals.Common.Diagnostics.Profiling;

namespace VintageVisuals.Common.Diagnostics
{
    /// <summary>
    /// D99. What the profiler measures for the profiler itself. Found by reflection
    /// (<see cref="IProfileDeclarer"/>); keep it beside the code it describes
    /// and update it when a patch group, setting or renderer is added here.
    /// </summary>
    internal sealed class DiagnosticsProfile : IProfileDeclarer
    {
        public void DeclareProfile(ProfileRegistry r)
        {
            r.Declare("diagnostics", "Profiler and frame-time readout")
             .Settings("Performance.ShowFrameTimes")
             .SettingsUnder("Performance.Profiler")
             .Note("the readout registers ~40 GPU timestamp markers only while shown; benches acquire them for their runs");
        }
    }
}
