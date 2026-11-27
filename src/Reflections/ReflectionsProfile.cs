using VintageVisuals.Common.Diagnostics.Profiling;

namespace VintageVisuals.Reflections
{
    /// <summary>
    /// D99. What the profiler measures for terrain reflections. Found by reflection
    /// (<see cref="IProfileDeclarer"/>); keep it beside the code it describes
    /// and update it when a patch group, setting or renderer is added here.
    /// </summary>
    internal sealed class ReflectionsProfile : IProfileDeclarer
    {
        public void DeclareProfile(ProfileRegistry r)
        {
            r.Declare("terrainreflect", "Reflections of the world on blocks")
             .Groups("pbrterrainreflect")
             .Requires("pbrterrainmaterial")
             .SettingsUnder("Reflections.")
             .Settings("PseudoPBR.PixelReflection")
             .Sweep("reflections_scene")
             .Sweep("pbr_pixelreflect", 0f, 0.5f, 1f)
             .Renderers("vintagevisuals-scenecapture")
             .GpuSpans("vv.capture")
             .Scenes("R", "P")
             .Note("copies the finished frame every frame (vv scene capture) plus a march on wet or shiny blocks")
             .ActiveWhen(c => c.PseudoPBR.Enabled && c.Reflections.SceneReflections);
        }
    }
}
