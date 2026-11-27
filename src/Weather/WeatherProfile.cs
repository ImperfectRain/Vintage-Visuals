using VintageVisuals.Common.Diagnostics.Profiling;

namespace VintageVisuals.Weather
{
    /// <summary>
    /// D99. What the profiler measures for weather. Found by reflection
    /// (<see cref="IProfileDeclarer"/>); keep it beside the code it describes
    /// and update it when a patch group, setting or renderer is added here.
    /// </summary>
    internal sealed class WeatherProfile : IProfileDeclarer
    {
        public void DeclareProfile(ProfileRegistry r)
        {
            r.Declare("weather", "Weather: wetness, ripples, cloud shadows, frost, snow")
             .Groups("weather")
             .SettingsUnder("Weather.")
             .Settings("PseudoPBR.CreviceWater")
             .Sweep("weather_wetness", 0f, 0.5f, 1f)
             .Sweep("weather_ripples", 0f, 0.5f, 1f)
             .Sweep("weather_cloudshadow", 0f, 1f)
             .Sweep("pbr_crevicewater", 0f, 1f)
             .Renderers("vintagevisuals-weather", "vintagevisuals-weather-uniforms")
             .Scenes("R")
             .Note("rain effects only show while it rains: measure in a scene saved with rain=1")
             .ActiveWhen(c => c.Weather.Enabled);
        }
    }
}
