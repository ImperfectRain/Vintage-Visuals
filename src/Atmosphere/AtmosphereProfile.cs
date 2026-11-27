using VintageVisuals.Common.Diagnostics.Profiling;

namespace VintageVisuals.Atmosphere
{
    /// <summary>
    /// D99. What the profiler measures for the atmosphere. Found by reflection
    /// (<see cref="IProfileDeclarer"/>); keep it beside the code it describes
    /// and update it when a patch group, setting or renderer is added here.
    /// </summary>
    internal sealed class AtmosphereProfile : IProfileDeclarer
    {
        public void DeclareProfile(ProfileRegistry r)
        {
            r.Declare("atmosphere", "Atmosphere: aerial perspective, scattering, haze, godrays")
             .Groups("atmosphere")
             .SettingsUnder("Atmosphere.")
             .Sweep("atmosphere_aerial", 0f, 0.5f, 1f)
             .Sweep("atmosphere_sunscatter", 0f, 0.5f, 1f)
             .Sweep("atmosphere_foresthaze", 0f, 0.5f, 1f)
             .Sweep("atmosphere_godrays")
             .Renderers("vintagevisuals-atmosphere", "vintagevisuals-atmosphere-uniforms")
             .Scenes("V", "F", "R")
             .Note("replaces applyFog in four programs (terrain, topsoil, entities, cube particles); the 13 debug views are compiled with it")
             .ActiveWhen(c => c.Atmosphere.WantsShader);
        }
    }
}
