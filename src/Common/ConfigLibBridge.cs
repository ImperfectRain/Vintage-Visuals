using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VintageVisuals.Common
{
    /// <summary>
    /// Optional live-tuning bridge to the ConfigLib mod (in-game GUI, F7).
    ///
    /// This file references NO ConfigLib types. ConfigLib publishes every
    /// setting change onto the game's ordinary event bus, so the whole
    /// integration is vanilla API plus three well-known event names. That
    /// matters for two reasons:
    ///
    ///  - CLAUDE.md requires ConfigLib stay optional. With nothing to resolve,
    ///    "degrades gracefully" is not a code path that has to be right — if
    ///    ConfigLib is absent the events simply never fire. There is no
    ///    assembly load to fail, so the failure mode does not exist.
    ///  - Maltiez.VintageStory.ConfigLib is not published on nuget.org (the
    ///    `configlib` package that is there is an unrelated, abandoned 2020
    ///    stub). A compile-time PackageReference would break `dotnet restore`
    ///    for everyone.
    ///
    /// This is an additional *writer* into the existing config object, never a
    /// second source of truth: it sets the same fields that
    /// ModConfig/vintagevisuals.json populates and then triggers the same
    /// notification Ctrl+V does. Remove ConfigLib and the JSON + hotkey flow
    /// works exactly as before.
    /// </summary>
    public sealed class ConfigLibBridge
    {
        /// <summary>Mod id of ConfigLib, as declared in its own modinfo.</summary>
        public const string ConfigLibModId = "configlib";

        /// <summary>
        /// Must match this mod's id: ConfigLib derives the domain from the mod
        /// whose assets/&lt;domain&gt;/config/configlib-patches.json it read.
        /// </summary>
        private const string Domain = "vintagevisuals";

        // Formats taken from ConfigLib's own public event-name constants.
        private const string SettingChangedEvent = "configlib:" + Domain + ":setting-changed";
        private const string SettingLoadedEvent = "configlib:" + Domain + ":setting-loaded";
        private const string ConfigSavedEvent = "configlib:" + Domain + ":config-saved";

        private readonly VintageVisualsModSystem _mod;

        public ConfigLibBridge(VintageVisualsModSystem mod)
        {
            _mod = mod;
        }

        public void Install()
        {
            // setting-loaded carries the initial values, setting-changed the
            // live edits. Both are handled identically: whatever the value is
            // now, put it in the config object and re-apply.
            _mod.Capi.Event.RegisterEventBusListener(OnSettingEvent, 0.5, SettingLoadedEvent);
            _mod.Capi.Event.RegisterEventBusListener(OnSettingEvent, 0.5, SettingChangedEvent);

            // Persistence is deliberately deferred to this event rather than
            // written on every change: dragging a GUI slider emits a stream of
            // setting-changed events, and writing the JSON on each one would
            // thrash the disk for no benefit. ConfigLib tells us when it has
            // committed, and that is when our own file should catch up so the
            // standalone fallback stays in sync.
            _mod.Capi.Event.RegisterEventBusListener(OnConfigSaved, 0.5, ConfigSavedEvent);
        }

        private void OnSettingEvent(string eventName, ref EnumHandling handling, IAttribute data)
        {
            try
            {
                ITreeAttribute tree = data as ITreeAttribute;
                if (tree == null) return;

                string code = tree.GetString("setting");
                if (string.IsNullOrEmpty(code)) return;

                if (!ApplySetting(code, tree)) return;

                // Same notification Ctrl+V raises, so there is exactly one
                // uniform-upload path regardless of who changed the value.
                _mod.ConfigManager.NotifyChanged();
            }
            catch (Exception ex)
            {
                // An optional convenience must never take the client down.
                _mod.Mod.Logger.Warning("[VintageVisuals] ConfigLib bridge failed to apply a setting: " + ex.Message);
            }
        }

        private void OnConfigSaved(string eventName, ref EnumHandling handling, IAttribute data)
        {
            try
            {
                _mod.ConfigManager.Save();
            }
            catch (Exception ex)
            {
                _mod.Mod.Logger.Warning("[VintageVisuals] ConfigLib bridge failed to persist config: " + ex.Message);
            }
        }

        /// <summary>
        /// Copies one setting into the shared config object.
        /// Returns false for codes this mod does not own.
        ///
        /// Phase 2-4 subsystems extend this by adding their block to
        /// configlib-patches.json and one case here — not by inventing a second
        /// integration approach per subsystem.
        /// </summary>
        private bool ApplySetting(string code, ITreeAttribute tree)
        {
            ColorGradeConfig colorGrade = _mod.ConfigManager.Config.ColorGrade;
            AdaptiveExposureConfig adaptive = _mod.ConfigManager.Config.AdaptiveExposure;
            PseudoPbrConfig pbr = _mod.ConfigManager.Config.PseudoPBR;
            WeatherConfig weather = _mod.ConfigManager.Config.Weather;
            AtmosphereConfig atmosphere = _mod.ConfigManager.Config.Atmosphere;
            AdaptiveGradeConfig adaptiveGrade = _mod.ConfigManager.Config.AdaptiveGrade;

            switch (code)
            {
                case "adaptive_enabled":
                    adaptive.Enabled = tree.GetBool("value", adaptive.Enabled);
                    break;
                case "adaptive_darkgain":
                    adaptive.DarkGain = tree.GetFloat("value", adaptive.DarkGain);
                    break;
                case "adaptive_brightgain":
                    adaptive.BrightGain = tree.GetFloat("value", adaptive.BrightGain);
                    break;
                case "adaptive_brightenseconds":
                    adaptive.BrightenSeconds = tree.GetFloat("value", adaptive.BrightenSeconds);
                    break;
                case "adaptive_darkenseconds":
                    adaptive.DarkenSeconds = tree.GetFloat("value", adaptive.DarkenSeconds);
                    break;
                case "colorgrade_enabled":
                    colorGrade.Enabled = tree.GetBool("value", colorGrade.Enabled);
                    break;
                case "colorgrade_exposure":
                    colorGrade.Exposure = tree.GetFloat("value", colorGrade.Exposure);
                    break;
                case "colorgrade_contrast":
                    colorGrade.Contrast = tree.GetFloat("value", colorGrade.Contrast);
                    break;
                case "colorgrade_saturation":
                    colorGrade.Saturation = tree.GetFloat("value", colorGrade.Saturation);
                    break;
                case "colorgrade_temperature":
                    colorGrade.Temperature = tree.GetFloat("value", colorGrade.Temperature);
                    break;
                case "colorgrade_tonemapstrength":
                    colorGrade.TonemapStrength = tree.GetFloat("value", colorGrade.TonemapStrength);
                    break;
                case "pbr_enabled":
                    pbr.Enabled = tree.GetBool("value", pbr.Enabled);
                    break;
                case "pbr_normalstrength":
                    pbr.NormalStrength = tree.GetFloat("value", pbr.NormalStrength);
                    break;
                case "pbr_specularstrength":
                    pbr.SpecularStrength = tree.GetFloat("value", pbr.SpecularStrength);
                    break;
                case "pbr_entitylighting":
                    pbr.EntityLighting = tree.GetBool("value", pbr.EntityLighting);
                    break;
                case "reflections_scene":
                    _mod.ConfigManager.Config.Reflections.SceneReflections = tree.GetBool("value", _mod.ConfigManager.Config.Reflections.SceneReflections);
                    break;
                case "pbr_emissive":
                    pbr.EmissiveStrength = tree.GetFloat("value", pbr.EmissiveStrength);
                    break;
                case "pbr_emissivetemperature":
                    pbr.EmissiveTemperature = tree.GetFloat("value", pbr.EmissiveTemperature);
                    break;
                case "pbr_emissiveflicker":
                    pbr.EmissiveFlicker = tree.GetFloat("value", pbr.EmissiveFlicker);
                    break;
                case "pbr_emissivebloom":
                    pbr.EmissiveBloom = tree.GetFloat("value", pbr.EmissiveBloom);
                    break;
                case "pbr_foliage":
                    pbr.FoliageTranslucency = tree.GetFloat("value", pbr.FoliageTranslucency);
                    break;
                case "pbr_shafts":
                    pbr.SunShafts = tree.GetFloat("value", pbr.SunShafts);
                    break;
                case "pbr_dapple":
                    pbr.SunDapple = tree.GetFloat("value", pbr.SunDapple);
                    break;
                case "pbr_canopyradius":
                    pbr.CanopyRadius = tree.GetFloat("value", pbr.CanopyRadius);
                    break;
                case "pbr_pixelreflect":
                    pbr.PixelReflection = tree.GetFloat("value", pbr.PixelReflection);
                    break;
                case "pbr_grain":
                    pbr.GrainAnisotropy = tree.GetFloat("value", pbr.GrainAnisotropy);
                    break;
                case "pbr_energy":
                    pbr.EnergyCompensation = tree.GetFloat("value", pbr.EnergyCompensation);
                    break;
                case "pbr_specocclusion":
                    pbr.SpecularOcclusion = tree.GetFloat("value", pbr.SpecularOcclusion);
                    break;
                case "pbr_cavity":
                    pbr.CavityStrength = tree.GetFloat("value", pbr.CavityStrength);
                    break;
                case "pbr_particlelighting":
                    pbr.ParticleLighting = tree.GetBool("value", pbr.ParticleLighting);
                    break;
                case "pbr_particlespecular":
                    pbr.ParticleSpecular = tree.GetFloat("value", pbr.ParticleSpecular);
                    break;
                case "pbr_entityroughness":
                    pbr.EntityRoughness = tree.GetFloat("value", pbr.EntityRoughness);
                    break;
                case "pbr_entityspecular":
                    pbr.EntitySpecular = tree.GetFloat("value", pbr.EntitySpecular);
                    break;
                case "pbr_entitydebug":
                    pbr.EntityDebugView = tree.GetFloat("value", pbr.EntityDebugView);
                    break;
                case "pbr_debugview":
                    pbr.DebugView = tree.GetFloat("value", pbr.DebugView);
                    break;
                case "pbr_roughnessbias":
                    pbr.RoughnessBias = tree.GetFloat("value", pbr.RoughnessBias);
                    break;
                case "pbr_metalresponse":
                    pbr.MetalResponse = tree.GetFloat("value", pbr.MetalResponse);
                    break;
                case "pbr_ambientspecular":
                    pbr.AmbientSpecular = tree.GetFloat("value", pbr.AmbientSpecular);
                    break;
                case "pbr_specularaa":
                    pbr.SpecularAntiAliasing = tree.GetFloat("value", pbr.SpecularAntiAliasing);
                    break;
                case "pbr_detaildistance":
                    pbr.DetailDistance = tree.GetFloat("value", pbr.DetailDistance);
                    break;
                case "pbr_blocklight":
                    pbr.BlockLightSpecular = tree.GetFloat("value", pbr.BlockLightSpecular);
                    break;
                case "weather_enabled":
                    weather.Enabled = tree.GetBool("value", weather.Enabled);
                    break;
                case "weather_wetness":
                    weather.WetnessStrength = tree.GetFloat("value", weather.WetnessStrength);
                    break;
                case "weather_dryingseconds":
                    weather.DryingSeconds = tree.GetFloat("value", weather.DryingSeconds);
                    break;
                case "weather_raincover":
                    weather.RainCoverThreshold = tree.GetFloat("value", weather.RainCoverThreshold);
                    break;
                case "grade_adaptive":
                    adaptiveGrade.Enabled = tree.GetBool("value", adaptiveGrade.Enabled);
                    break;
                case "grade_stylestrength":
                    adaptiveGrade.StyleStrength = tree.GetFloat("value", adaptiveGrade.StyleStrength);
                    break;
                case "grade_timeofday":
                    adaptiveGrade.TimeOfDayStrength = tree.GetFloat("value", adaptiveGrade.TimeOfDayStrength);
                    break;
                case "grade_weather":
                    adaptiveGrade.WeatherStrength = tree.GetFloat("value", adaptiveGrade.WeatherStrength);
                    break;
                case "grade_biome":
                    adaptiveGrade.BiomeStrength = tree.GetFloat("value", adaptiveGrade.BiomeStrength);
                    break;
                case "grade_indoor":
                    adaptiveGrade.IndoorStrength = tree.GetFloat("value", adaptiveGrade.IndoorStrength);
                    break;
                case "grade_depth":
                    adaptiveGrade.DepthStrength = tree.GetFloat("value", adaptiveGrade.DepthStrength);
                    break;
                case "grade_underwater":
                    adaptiveGrade.UnderwaterStrength = tree.GetFloat("value", adaptiveGrade.UnderwaterStrength);
                    break;
                case "grade_response":
                    adaptiveGrade.ResponseSeconds = tree.GetFloat("value", adaptiveGrade.ResponseSeconds);
                    break;
                case "weather_frost":
                    weather.FrostStrength = tree.GetFloat("value", weather.FrostStrength);
                    break;
                case "weather_snowdusting":
                    weather.SnowDusting = tree.GetFloat("value", weather.SnowDusting);
                    break;
                case "weather_ripples":
                    weather.RippleStrength = tree.GetFloat("value", weather.RippleStrength);
                    break;
                case "weather_overcast":
                    weather.OvercastStrength = tree.GetFloat("value", weather.OvercastStrength);
                    break;
                case "weather_fogstrength":
                    weather.FogStrength = tree.GetFloat("value", weather.FogStrength);
                    break;
                case "weather_fogtint":
                    weather.FogTint = tree.GetFloat("value", weather.FogTint);
                    break;
                case "weather_cloudsfromgame":
                    weather.CloudsFromGame = tree.GetBool("value", weather.CloudsFromGame);
                    break;
                case "weather_clouddebugview":
                    weather.CloudDebugView = tree.GetFloat("value", weather.CloudDebugView);
                    break;
                case "weather_cloudshadow":
                    weather.CloudShadowStrength = tree.GetFloat("value", weather.CloudShadowStrength);
                    break;
                case "weather_cloudscale":
                    weather.CloudScale = tree.GetFloat("value", weather.CloudScale);
                    break;
                case "weather_clouddrift":
                    weather.CloudDriftSpeed = tree.GetFloat("value", weather.CloudDriftSpeed);
                    break;
                case "weather_cloudheight":
                    weather.CloudHeight = tree.GetFloat("value", weather.CloudHeight);
                    break;
                case "compare_wipe":
                    _mod.ConfigManager.Config.CompareWipe = tree.GetFloat("value", _mod.ConfigManager.Config.CompareWipe);
                    break;
                case "atmosphere_enabled":
                    atmosphere.Enabled = tree.GetBool("value", atmosphere.Enabled);
                    break;
                case "atmosphere_heighthaze":
                    atmosphere.HeightHaze = tree.GetFloat("value", atmosphere.HeightHaze);
                    break;
                case "atmosphere_aerial":
                    atmosphere.AerialPerspective = tree.GetFloat("value", atmosphere.AerialPerspective);
                    break;
                case "atmosphere_horizon":
                    atmosphere.HorizonScattering = tree.GetFloat("value", atmosphere.HorizonScattering);
                    break;
                case "atmosphere_sunscatter":
                    atmosphere.SunScattering = tree.GetFloat("value", atmosphere.SunScattering);
                    break;
                case "atmosphere_heightattenuation":
                    atmosphere.HeightAttenuation = tree.GetFloat("value", atmosphere.HeightAttenuation);
                    break;
                case "atmosphere_weatherextinction":
                    atmosphere.WeatherExtinction = tree.GetFloat("value", atmosphere.WeatherExtinction);
                    break;
                case "atmosphere_weathertint":
                    atmosphere.WeatherTint = tree.GetFloat("value", atmosphere.WeatherTint);
                    break;
                case "atmosphere_cloudatmosphere":
                    atmosphere.CloudAtmosphere = tree.GetFloat("value", atmosphere.CloudAtmosphere);
                    break;
                case "atmosphere_cloudedge":
                    atmosphere.CloudEdgeScattering = tree.GetFloat("value", atmosphere.CloudEdgeScattering);
                    break;
                case "atmosphere_godrays":
                    atmosphere.Godrays = tree.GetFloat("value", atmosphere.Godrays);
                    break;
                case "atmosphere_godrayquality":
                    atmosphere.GodrayQuality = tree.GetFloat("value", atmosphere.GodrayQuality);
                    break;
                case "atmosphere_precipitation":
                    atmosphere.PrecipitationScattering = tree.GetFloat("value", atmosphere.PrecipitationScattering);
                    break;
                case "atmosphere_moon":
                    atmosphere.MoonScattering = tree.GetFloat("value", atmosphere.MoonScattering);
                    break;
                case "atmosphere_dapple":
                    atmosphere.DappleInteraction = tree.GetFloat("value", atmosphere.DappleInteraction);
                    break;
                case "atmosphere_debugview":
                    atmosphere.AirDebugView = tree.GetFloat("value", atmosphere.AirDebugView);
                    break;
                case "pbr_blocklightdir":
                    pbr.BlockLightDirectionality = tree.GetFloat("value", pbr.BlockLightDirectionality);
                    break;
                default:
                    return false;
            }

            // The GUI's own min/max should already keep values in range, but
            // the config file is still hand-editable and ConfigLib will happily
            // load whatever is in its YAML. Re-clamp rather than trust it.
            foreach (string correction in _mod.ConfigManager.Config.ClampToValidRanges())
            {
                _mod.Mod.Logger.Warning("[VintageVisuals] " + correction);
            }

            return true;
        }
    }
}
