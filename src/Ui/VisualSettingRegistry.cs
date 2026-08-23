using System;
using System.Collections.Generic;
using System.Linq;

namespace VintageVisuals.Ui
{
    /// <summary>
    /// Every setting the tuning studio shows, in the order it shows them.
    ///
    /// EXPLICIT, NOT REFLECTED. Walking the config object and making a slider
    /// per property would be a third of the code and the wrong tool: several
    /// properties are diagnostics rather than tuning controls, several want a
    /// different widget than their type implies, several belong under a heading
    /// their C# class does not match, and none of them carry a sentence saying
    /// what they visibly do. Reflection can find a float. It cannot find out
    /// that RoughnessBias belongs under "Material response" and means "how
    /// polished or worn every surface reads".
    ///
    /// So the config stays the authority for VALUES and this stays the
    /// authority for PRESENTATION, and the two are joined by a path string that
    /// a test resolves. Nothing here holds a value.
    ///
    /// DUPLICATION, DECLARED. assets/vintagevisuals/config/configlib-patches.json
    /// already carries labels, comments, ranges and defaults for the settings
    /// ConfigLib shows. This registry does not read it - a JSON file cannot
    /// supply a typed accessor, a widget choice or a tab - so the two overlap
    /// and could drift. Rather than pretend otherwise, the codes here are
    /// deliberately ConfigLib's own codes, and tools/smoketest compares the two
    /// on range and default for every code they share. The migration path, when
    /// someone wants it, is to generate the ConfigLib JSON FROM this registry.
    /// That is not this pass.
    /// </summary>
    public static class VisualSettingRegistry
    {
        /// <summary>The six grading styles the implementation actually supports.</summary>
        public static readonly string[] GradeStyles =
            { "None", "Filmic", "Muted", "Vivid", "Cold", "Warm" };

        private static readonly List<VisualSetting> _all = Build();

        public static IReadOnlyList<VisualSetting> All { get { return _all; } }

        public static IEnumerable<VisualSetting> ForTab(SettingTab tab)
        {
            return _all.Where(s => s.Tab == tab);
        }

        /// <summary>Section headings for a tab, in registration order and without duplicates.</summary>
        public static IReadOnlyList<string> SectionsFor(SettingTab tab)
        {
            var seen = new List<string>();
            foreach (VisualSetting s in ForTab(tab))
            {
                if (!seen.Contains(s.Section, StringComparer.Ordinal)) seen.Add(s.Section);
            }
            return seen;
        }

        public static VisualSetting ByCode(string code)
        {
            return _all.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.Ordinal));
        }

        static List<VisualSetting> Build()
        {
            var s = new List<VisualSetting>();

            void Toggle(string code, string path, string name, string desc, SettingTab tab,
                        string section, bool advanced = false, SettingCost cost = SettingCost.Free,
                        bool reload = false, bool debugOnly = false)
                => s.Add(new VisualSetting(code, path, name, desc, tab, section, SettingKind.Toggle,
                                           0f, 1f, 1f, null, advanced, cost, reload, debugOnly));

            void Slider(string code, string path, string name, string desc, SettingTab tab,
                        string section, float min, float max, float step, string unit = null,
                        bool advanced = false, SettingCost cost = SettingCost.Free,
                        bool reload = false, bool debugOnly = false)
                => s.Add(new VisualSetting(code, path, name, desc, tab, section, SettingKind.Slider,
                                           min, max, step, unit, advanced, cost, reload, debugOnly));

            // ---- COLOUR ------------------------------------------------------
            const SettingTab C = SettingTab.Color;

            Toggle("colorgrade_enabled", "ColorGrade.Enabled", "Colour grading",
                   "Master switch for exposure, contrast, saturation and the tonemap. Off returns the game's own colours exactly.",
                   C, "Basic look");

            Slider("colorgrade_exposure", "ColorGrade.Exposure", "Exposure",
                   "Overall brightness, applied before the tonemap. Your choice, and you may clip the highlights with it if you want to.",
                   C, "Basic look", 0.1f, 4f, 0.05f);

            Slider("colorgrade_contrast", "ColorGrade.Contrast", "Contrast",
                   "How far apart the darks and brights sit. 1 leaves the image as the game rendered it.",
                   C, "Basic look", 0f, 2f, 0.05f);

            Slider("colorgrade_saturation", "ColorGrade.Saturation", "Saturation",
                   "How strong the colours are. This mod deliberately keeps its hand light here - Vintage Story's palette is the point.",
                   C, "Basic look", 0f, 2f, 0.05f);

            Slider("colorgrade_temperature", "ColorGrade.Temperature", "Temperature",
                   "Warms the image toward orange or cools it toward blue. 0 is neutral.",
                   C, "Basic look", -1f, 1f, 0.05f);

            Slider("colorgrade_tonemapstrength", "ColorGrade.TonemapStrength", "Tonemap",
                   "Rolls off bright areas instead of letting them clip to white. Automatic eye adaptation raises this on its own when it brightens a dark scene.",
                   C, "Basic look", 0f, 1f, 0.05f);

            Toggle("adaptive_enabled", "AdaptiveExposure.Enabled", "Eye adaptation",
                   "Brightens dark scenes and settles bright ones, the way an eye does walking out of a cave.",
                   C, "Automatic light adaptation");

            Slider("adaptive_darkgain", "AdaptiveExposure.DarkGain", "Dark adaptation",
                   "How far the image is lifted in the dark. Raising this also raises the tonemap, so the extra light has somewhere to go.",
                   C, "Automatic light adaptation", 0.25f, 4f, 0.05f);

            Slider("adaptive_brightgain", "AdaptiveExposure.BrightGain", "Bright adaptation",
                   "How far the image is pulled down in bright scenes. 1 leaves them alone.",
                   C, "Automatic light adaptation", 0.25f, 4f, 0.05f);

            Slider("adaptive_brightenseconds", "AdaptiveExposure.BrightenSeconds", "Time to brighten",
                   "How long the eye takes to adjust to darkness. Slower than the reverse, as in life.",
                   C, "Automatic light adaptation", 0f, 60f, 0.1f, "s", true);

            Slider("adaptive_darkenseconds", "AdaptiveExposure.DarkenSeconds", "Time to darken",
                   "How long the eye takes to adjust to sudden brightness.",
                   C, "Automatic light adaptation", 0f, 60f, 0.1f, "s", true);

            Toggle("grade_adaptive", "AdaptiveGrade.Enabled", "World-responsive colour",
                   "Lets the time of day, the weather, the biome and where you are standing shift the grade on their own.",
                   C, "World-responsive colour");

            s.Add(new VisualSetting("grade_style", "AdaptiveGrade.Style", "Style",
                "A look to lean toward. One more capped contributor on top of the world, never a preset that replaces your settings.",
                C, "Style", SettingKind.Dropdown, choices: GradeStyles));

            Slider("grade_stylestrength", "AdaptiveGrade.StyleStrength", "Style strength",
                   "How far toward the chosen style to lean. 0 ignores it entirely.",
                   C, "Style", 0f, 1f, 0.05f);

            Slider("grade_response", "AdaptiveGrade.ResponseSeconds", "Response speed",
                   "How quickly the world-responsive grade follows a change in the weather or the light.",
                   C, "Style", 0.1f, 30f, 0.1f, "s", true);

            Slider("grade_timeofday", "AdaptiveGrade.TimeOfDayStrength", "Time of day",
                   "Golden hour warmth near sunrise and sunset, and the desaturated blue of night vision.",
                   C, "World-responsive colour", 0f, 2f, 0.05f);

            Slider("grade_weather", "AdaptiveGrade.WeatherStrength", "Weather",
                   "Rain and cloud draining colour and contrast out of the scene.",
                   C, "World-responsive colour", 0f, 2f, 0.05f);

            Slider("grade_biome", "AdaptiveGrade.BiomeStrength", "Biome",
                   "Heat, cold, aridity and lushness colouring the light where you are standing.",
                   C, "World-responsive colour", 0f, 2f, 0.05f);

            Slider("grade_indoor", "AdaptiveGrade.IndoorStrength", "Indoors",
                   "How differently light reads under a roof or inside a building.",
                   C, "World-responsive colour", 0f, 2f, 0.05f);

            Slider("grade_depth", "AdaptiveGrade.DepthStrength", "Underground",
                   "How the palette shifts as you go deeper below the surface.",
                   C, "World-responsive colour", 0f, 2f, 0.05f);

            Slider("grade_underwater", "AdaptiveGrade.UnderwaterStrength", "Underwater",
                   "How strongly water colours everything seen through it.",
                   C, "World-responsive colour", 0f, 2f, 0.05f);

            // ---- MATERIALS ---------------------------------------------------
            const SettingTab M = SettingTab.Materials;

            Toggle("pbr_enabled", "PseudoPBR.Enabled", "Surface materials",
                   "Master switch for surface relief, roughness, metals, foliage light and the sun through leaves. This is the only subsystem that patches the shader drawing the world, so switching it rebuilds shaders.",
                   M, "Surface detail", false, SettingCost.Medium, true);

            Slider("pbr_normalstrength", "PseudoPBR.NormalStrength", "Surface relief",
                   "How strongly small surface details - mortar lines, plank edges, stone grain - catch the light and read as shape.",
                   M, "Surface detail", 0f, 2f, 0.05f);

            Slider("pbr_detaildistance", "PseudoPBR.DetailDistance", "Detail distance",
                   "How far away fine surface detail keeps working before it fades out. Lower is cheaper.",
                   M, "Surface detail", 4f, 192f, 1f, "blocks", false, SettingCost.Low);

            Slider("pbr_cavity", "PseudoPBR.CavityStrength", "Crevice shading",
                   "Darkening in the grooves of a surface, at a scale the game's own ambient occlusion cannot see.",
                   M, "Surface detail", 0f, 2f, 0.05f);

            Slider("pbr_specularstrength", "PseudoPBR.SpecularStrength", "Specular strength",
                   "How brightly surfaces catch a direct highlight from the sun and moon.",
                   M, "Light response", 0f, 2f, 0.05f);

            Slider("pbr_ambientspecular", "PseudoPBR.AmbientSpecular", "Sky reflection",
                   "How much of the sky a surface shows. On a metal this is also the only thing paying back the diffuse light metalness takes away, so lowering it dims metals rather than merely calming them.",
                   M, "Light response", 0f, 2f, 0.05f);

            Slider("pbr_blocklight", "PseudoPBR.BlockLightSpecular", "Torch and lava highlights",
                   "Whether firelight makes a proper highlight on nearby surfaces rather than only brightening them.",
                   M, "Light response", 0f, 2f, 0.05f);

            Slider("pbr_blocklightdir", "PseudoPBR.BlockLightDirectionality", "Torch highlight direction",
                   "How strongly a firelight highlight points back at the flame rather than sitting flat on the surface.",
                   M, "Light response", 0f, 1f, 0.05f, null, true);

            Slider("pbr_roughnessbias", "PseudoPBR.RoughnessBias", "Roughness bias",
                   "Shifts every surface toward polished or toward worn. 0 uses the material as it was measured.",
                   M, "Material response", -0.5f, 0.5f, 0.05f);

            Slider("pbr_metalresponse", "PseudoPBR.MetalResponse", "Metal response",
                   "How strongly metals behave like metals - coloured reflections, no diffuse of their own.",
                   M, "Material response", 0f, 1f, 0.05f, null, true);

            Slider("pbr_grain", "PseudoPBR.GrainAnisotropy", "Wood grain highlight",
                   "Stretches the highlight along the grain on wood, the way a brushed surface streaks a reflection.",
                   M, "Material response", 0f, 1f, 0.05f, null, true);

            Slider("pbr_specularaa", "PseudoPBR.SpecularAntiAliasing", "Specular antialiasing",
                   "Stops distant detailed surfaces from sparkling as you move. Lower it only to see what it was doing.",
                   M, "Material response", 0f, 2f, 0.05f, null, true);

            Slider("pbr_specocclusion", "PseudoPBR.SpecularOcclusion", "Specular occlusion",
                   "Keeps reflections out of crevices the sky cannot see into.",
                   M, "Material response", 0f, 1f, 0.05f, null, true);

            Slider("pbr_energy", "PseudoPBR.EnergyCompensation", "Energy compensation",
                   "Returns the light a rough surface would otherwise lose, so roughening something does not quietly darken it.",
                   M, "Material response", 0f, 1f, 0.05f, null, true);

            Slider("pbr_foliage", "PseudoPBR.FoliageTranslucency", "Foliage translucency",
                   "Light coming THROUGH leaves rather than off them. Strongest with a low sun behind the canopy, and it needs a clear sky - an overcast one has no beam to shine through.",
                   M, "Foliage", 0f, 2f, 0.05f);

            Slider("pbr_dapple", "PseudoPBR.SunDapple", "Sun dapple",
                   "Sunflecks and green shade on the ground under trees, measured from the game's own shadow map. It leaves torchlight alone.",
                   M, "Foliage", 0f, 2f, 0.05f);

            Slider("pbr_shafts", "PseudoPBR.SunShafts", "Sun shafts",
                   "Beams of light picked out where the sun breaks through a canopy.",
                   M, "Foliage", 0f, 2f, 0.05f);

            Slider("pbr_canopyradius", "PseudoPBR.CanopyRadius", "Canopy scale",
                   "How wide a patch of shadow counts as a canopy. Larger reads as bigger trees.",
                   M, "Foliage", 0f, 16f, 0.5f, "blocks", true);

            Slider("pbr_emissive", "PseudoPBR.EmissiveStrength", "Emissive strength",
                   "How strongly forges, lamps and lava read as light sources rather than as bright textures.",
                   M, "Emissive", 0f, 2f, 0.05f);

            Slider("pbr_emissivetemperature", "PseudoPBR.EmissiveTemperature", "Emissive warmth",
                   "How hot emitting surfaces look - deep red through orange to near white.",
                   M, "Emissive", 0f, 1f, 0.05f);

            Slider("pbr_emissiveflicker", "PseudoPBR.EmissiveFlicker", "Emissive flicker",
                   "Movement in firelight. 0 is a perfectly steady flame.",
                   M, "Emissive", 0f, 1f, 0.05f);

            Slider("pbr_emissivebloom", "PseudoPBR.EmissiveBloom", "Emissive glow",
                   "How much emitting surfaces feed the game's own bloom pass.",
                   M, "Emissive", 0f, 1f, 0.05f);

            Toggle("pbr_entitylighting", "PseudoPBR.EntityLighting", "Creature and player lighting",
                   "Applies the same light response to animals, players and held items.",
                   M, "Creatures and particles");

            Slider("pbr_entityroughness", "PseudoPBR.EntityRoughness", "Creature roughness",
                   "How polished creatures and players read. Higher is more matte.",
                   M, "Creatures and particles", 0.04f, 1f, 0.05f, null, true);

            Slider("pbr_entityspecular", "PseudoPBR.EntitySpecular", "Creature highlights",
                   "How brightly creatures catch a highlight.",
                   M, "Creatures and particles", 0f, 2f, 0.05f, null, true);

            Toggle("pbr_particlelighting", "PseudoPBR.ParticleLighting", "Particle lighting",
                   "Lights sparks, smoke and falling leaves the same way as everything else.",
                   M, "Creatures and particles");

            Slider("pbr_particlespecular", "PseudoPBR.ParticleSpecular", "Particle highlights",
                   "How brightly particles catch a highlight.",
                   M, "Creatures and particles", 0f, 2f, 0.05f, null, true);

            // ---- WEATHER -----------------------------------------------------
            const SettingTab W = SettingTab.Weather;

            Toggle("weather_enabled", "Weather.Enabled", "Weather response",
                   "Master switch for wetness, rain fog, cloud shadows, frost and snow.",
                   W, "Rain and wetness", false, SettingCost.Free, true);

            Slider("weather_wetness", "Weather.WetnessStrength", "Wetness",
                   "How strongly rain darkens surfaces, smooths them and makes them reflect. The single biggest change rain makes to a scene.",
                   W, "Rain and wetness", 0f, 2f, 0.05f);

            Slider("weather_dryingseconds", "Weather.DryingSeconds", "Drying time",
                   "How long surfaces stay wet after the rain stops.",
                   W, "Rain and wetness", 1f, 600f, 5f, "s");

            Slider("weather_raincover", "Weather.RainCoverThreshold", "Shelter threshold",
                   "How much open sky a surface needs before rain reaches it. Higher keeps porches and overhangs drier.",
                   W, "Rain and wetness", 0f, 1f, 0.02f, null, true);

            Slider("weather_ripples", "Weather.RippleStrength", "Rain ripples",
                   "Rings spreading on wet surfaces while it rains.",
                   W, "Rain and wetness", 0f, 1f, 0.05f);

            Slider("weather_fogstrength", "Weather.FogStrength", "Rain fog",
                   "How much falling rain thickens the air and shortens the view.",
                   W, "Fog", 0f, 1f, 0.05f);

            Slider("weather_fogtint", "Weather.FogTint", "Rain fog colour",
                   "How strongly rain fog takes on its own colour rather than the sky's.",
                   W, "Fog", 0f, 1f, 0.05f);

            Slider("weather_overcast", "Weather.OvercastStrength", "Overcast softening",
                   "How much heavy cloud flattens the light and softens shadows.",
                   W, "Fog", 0f, 1f, 0.05f);

            Slider("weather_cloudshadow", "Weather.CloudShadowStrength", "Cloud shadows",
                   "Shadows of the sky's own clouds moving across the ground.",
                   W, "Clouds", 0f, 1f, 0.05f);

            Toggle("weather_cloudsfromgame", "Weather.CloudsFromGame", "Follow the game's clouds",
                   "Reads cloud positions from the game's own cloud data so the shadows line up with the sky. Off uses a generated pattern instead.",
                   W, "Clouds", true);

            Slider("weather_cloudscale", "Weather.CloudScale", "Cloud size",
                   "How large the generated cloud pattern is. Only used when the game's own cloud data is not being followed.",
                   W, "Clouds", 32f, 512f, 10f, "blocks", true);

            Slider("weather_clouddrift", "Weather.CloudDriftSpeed", "Cloud drift",
                   "How fast cloud shadows travel across the ground.",
                   W, "Clouds", 0f, 8f, 0.05f, null, true);

            Slider("weather_cloudheight", "Weather.CloudHeight", "Cloud height",
                   "How high the clouds sit, which decides how far their shadows lean when the sun is low.",
                   W, "Clouds", 40f, 400f, 10f, "blocks", true);

            Slider("weather_frost", "Weather.FrostStrength", "Frost",
                   "Frost forming on surfaces in freezing weather.",
                   W, "Frost and snow", 0f, 1f, 0.05f);

            Slider("weather_snowdusting", "Weather.SnowDusting", "Snow dusting",
                   "A layer of settled snow on upward-facing surfaces.",
                   W, "Frost and snow", 0f, 1f, 0.05f);

            // ---- ATMOSPHERE --------------------------------------------------
            const SettingTab A = SettingTab.Atmosphere;

            Toggle("atmosphere_enabled", "Atmosphere.Enabled", "Atmosphere",
                   "Master switch for how light travels through the air. Changing it rebuilds shaders.",
                   A, "Air", false, SettingCost.Low, true);

            Slider("atmosphere_aerial", "Atmosphere.AerialPerspective", "Aerial perspective",
                   "Distant things taking on the colour of the air between you and them, and the sun's direction colouring that haze. The one atmospheric effect the game genuinely lacks.",
                   A, "Air", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_horizon", "Atmosphere.HorizonScattering", "Horizon glow",
                   "The horizon reading as more atmospheric than the ground at your feet.",
                   A, "Air", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_sunscatter", "Atmosphere.SunScattering", "Sun scattering",
                   "Light scattering in the air around the sun, brightening the half of the sky facing it.",
                   A, "Air", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_heighthaze", "Atmosphere.HeightHaze", "Ground haze",
                   "Haze pooling in low ground and valleys. Rendered by the game's own fog rather than by this mod, so it costs nothing extra.",
                   A, "Height", 0f, 1f, 0.05f);

            Slider("atmosphere_heightattenuation", "Atmosphere.HeightAttenuation", "Thin mountain air",
                   "Air thinning with altitude, so a mountain top sees further than a valley floor.",
                   A, "Height", 0f, 1f, 0.05f, null, true, SettingCost.Low, true);

            Slider("atmosphere_weatherextinction", "Atmosphere.WeatherExtinction", "Weather thickening",
                   "How much rain and snow shorten the view through the air.",
                   A, "Weather in the air", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_weathertint", "Atmosphere.WeatherTint", "Weather colour",
                   "What the thickened air itself looks like, as opposed to how much it hides.",
                   A, "Weather in the air", 0f, 1f, 0.05f);

            Slider("atmosphere_cloudatmosphere", "Atmosphere.CloudAtmosphere", "Cloud softening",
                   "Heavy cloud softening the whole atmosphere, not just the shadows.",
                   A, "Weather in the air", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_precipitation", "Atmosphere.PrecipitationScattering", "Falling rain and snow",
                   "What rain and snow in the air do to light passing through them.",
                   A, "Weather in the air", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_godrays", "Atmosphere.Godrays", "Godrays",
                   "How much this mod's atmosphere feeds the game's own crepuscular ray pass. 0 leaves vanilla's rays exactly as they were.",
                   A, "Godrays", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_godrayquality", "Atmosphere.GodrayQuality", "Godray quality",
                   "0 low, 1 medium, 2 high. Quality only - it cannot switch godrays on.",
                   A, "Godrays", 0f, 2f, 1f, null, true, SettingCost.Medium);

            Slider("atmosphere_moon", "Atmosphere.MoonScattering", "Moonlight in the air",
                   "Moonlight scattering on a clear night, driven by the game's own moon phase and capped hard.",
                   A, "Night", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_cloudedge", "Atmosphere.CloudEdgeScattering", "Broken cloud edges",
                   "FOUNDATION ONLY. The data this needs - which way a cloud edge faces - is not available yet, so it is wired but does not do what its name says.",
                   A, "Foundation", 0f, 1f, 0.05f, null, true, SettingCost.Low, true);

            Slider("atmosphere_dapple", "Atmosphere.DappleInteraction", "Dappled air",
                   "FOUNDATION ONLY. Atmosphere responding to dappled sunlight, which needs a per-fragment link the current structure does not carry.",
                   A, "Foundation", 0f, 1f, 0.05f, null, true, SettingCost.Low, true);

            // ---- REFLECTIONS -------------------------------------------------
            const SettingTab R = SettingTab.Reflections;

            Toggle("reflections_scene", "Reflections.SceneReflections", "Scene reflections",
                   "Reflect actual world content - trees, terrain, buildings - instead of only an approximated sky. Captures the finished frame every frame into its own buffer, so it is the most expensive setting in this mod.",
                   R, "Scene reflections", false, SettingCost.High, true);

            Slider("pbr_pixelreflect", "PseudoPBR.PixelReflection", "Reflection strength",
                   "How much of the environment a reflective surface shows. One colour per texture pixel, on purpose - this is a pixel-art reflection rather than a mirror.",
                   R, "Scene reflections", 0f, 1f, 0.05f);

            return s;
        }
    }
}
