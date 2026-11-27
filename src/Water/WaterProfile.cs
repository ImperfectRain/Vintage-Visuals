using VintageVisuals.Common.Diagnostics.Profiling;

namespace VintageVisuals.Water
{
    /// <summary>
    /// D99. What the profiler measures for water. Found by reflection
    /// (<see cref="IProfileDeclarer"/>); keep it beside the code it describes
    /// and update it when a patch group, setting or renderer is added here.
    /// </summary>
    internal sealed class WaterProfile : IProfileDeclarer
    {
        public void DeclareProfile(ProfileRegistry r)
        {
            r.Declare("water", "Water surface: sky reflection, waves, ripples, glints, depth tint")
             .Groups("water")
             .SettingsUnder("Water.")
             .Sweep("water_waves", 0f, 0.5f, 1f)
             .Sweep("water_ripples", 0f, 1f)
             .Sweep("water_glints", 0f, 1f)
             .Renderers("vintagevisuals-water-uniforms")
             .Scenes("P", "R")
             .ActiveWhen(c => c.Water.Enabled);

            r.Declare("waterscene", "Water reflections and refraction of the world")
             .Groups("waterscene")
             .Requires("water")
             .Settings("Water.SceneReflections", "Water.Refraction", "Water.ReflectionSoftness")
             .Sweep("water_scene")
             .Sweep("water_refraction", 0f, 0.5f, 1f)
             .Scenes("P")
             .Note("screen march in the OIT pass; cost scales with visible water, so stand at a pond")
             .ActiveWhen(c => c.Water.Enabled && c.Water.SceneReflections);

            r.Declare("watercaustics", "Water caustics")
             .Groups("watercaustics")
             .Requires("water") // its patches anchor in the water snippet
             .Settings("Water.Caustics", "Water.CausticBlur", "Water.CausticGlow", "Water.CausticDispersion")
             .Sweep("water_caustics", 0f, 0.3f, 0.6f, 1f)
             .Sweep("water_causticblur")
             .Scenes("P")
             .ActiveWhen(c => c.Water.Enabled && c.Water.CausticsCompiled);
        }
    }
}
