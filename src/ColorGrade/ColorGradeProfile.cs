using VintageVisuals.Common.Diagnostics.Profiling;

namespace VintageVisuals.ColorGrade
{
    /// <summary>
    /// D99. What the profiler measures for colour grading. Found by reflection
    /// (<see cref="IProfileDeclarer"/>); keep it beside the code it describes
    /// and update it when a patch group, setting or renderer is added here.
    /// </summary>
    internal sealed class ColorGradeProfile : IProfileDeclarer
    {
        public void DeclareProfile(ProfileRegistry r)
        {
            r.Declare("colorgrade", "Colour grade and exposure")
             .Groups("colorgrade")
             .SettingsUnder("ColorGrade.", "AdaptiveGrade.", "AdaptiveExposure.")
             .Sweep("colorgrade_tonemapstrength", 0f, 1f)
             .Sweep("adaptive_enabled")
             .Renderers("vintagevisuals-colorgrade")
             .Scenes("V", "F")
             .Note("one pass in final.fsh; cost is per pixel and flat, so expect a compiled cost and a flat sweep")
             .ActiveWhen(c => c.ColorGrade.Enabled);
        }
    }
}
