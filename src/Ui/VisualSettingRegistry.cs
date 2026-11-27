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

        /// <summary>Performance.Quality 0..3, by name.</summary>
        public static readonly string[] QualityTiers = { "Low", "Medium", "High", "Ultra" };

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

            // Sidebar order, then top to bottom within each tab. Within a tab
            // the master switch comes first, then the controls a player reaches
            // for most, then the fine ones (most of those are Advanced).
            //
            // Each description opens with ONE sentence that stands alone: that
            // sentence is what the row shows beside the name, and the details
            // strip under the list shows the rest when the pointer is over it.

            void Choice(string code, string path, string name, string desc, SettingTab tab,
                        string section, string[] choices, bool advanced = false,
                        SettingCost cost = SettingCost.Free, bool reload = false, bool debugOnly = false)
                => s.Add(new VisualSetting(code, path, name, desc, tab, section, SettingKind.Choice,
                                           0f, choices.Length - 1, 1f, null, advanced, cost, reload, debugOnly, choices));

            // ---- COLOUR ------------------------------------------------------
            const SettingTab C = SettingTab.Color;

            Toggle("colorgrade_enabled", "ColorGrade.Enabled", "Colour grading",
                   "Master switch for exposure, contrast, saturation and the tonemap. Off shows the game's own colours exactly.",
                   C, "Basic look");

            Slider("colorgrade_exposure", "ColorGrade.Exposure", "Exposure",
                   "Overall brightness. Applied before the tonemap, so high values can clip highlights if you want them to.",
                   C, "Basic look", 0.1f, 4f, 0.05f);

            Slider("colorgrade_contrast", "ColorGrade.Contrast", "Contrast",
                   "How far apart the darks and the brights sit. 1 leaves the image as the game rendered it.",
                   C, "Basic look", 0f, 2f, 0.05f);

            Slider("colorgrade_saturation", "ColorGrade.Saturation", "Saturation",
                   "How strong the colours are. 1 is the game's palette, which this mod deliberately keeps.",
                   C, "Basic look", 0f, 2f, 0.05f);

            Slider("colorgrade_temperature", "ColorGrade.Temperature", "Temperature",
                   "Warms the image toward orange or cools it toward blue. 0 is neutral.",
                   C, "Basic look", -1f, 1f, 0.05f);

            Slider("colorgrade_tonemapstrength", "ColorGrade.TonemapStrength", "Highlight roll-off",
                   "Rolls bright areas off softly instead of letting them clip to white. Eye adaptation raises this on its own when it brightens a dark scene.",
                   C, "Basic look", 0f, 1f, 0.05f);

            s.Add(new VisualSetting("grade_style", "AdaptiveGrade.Style", "Style",
                "A look to lean toward: filmic, muted, vivid, cold or warm. It adds on top of the world's own grading and never replaces your settings.",
                C, "Style", SettingKind.Dropdown, choices: GradeStyles));

            Slider("grade_stylestrength", "AdaptiveGrade.StyleStrength", "Style strength",
                   "How far toward the chosen style to lean. 0 ignores the style.",
                   C, "Style", 0f, 1f, 0.05f);

            Toggle("adaptive_enabled", "AdaptiveExposure.Enabled", "Eye adaptation",
                   "Brightens dark scenes and settles bright ones, like an eye walking out of a cave.",
                   C, "Eye adaptation");

            Slider("adaptive_darkgain", "AdaptiveExposure.DarkGain", "Lift in the dark",
                   "How far the image is lifted in caves and at night. Raising it also raises the highlight roll-off, so the extra light has room.",
                   C, "Eye adaptation", 0.25f, 4f, 0.05f);

            Slider("adaptive_brightgain", "AdaptiveExposure.BrightGain", "Calm in bright light",
                   "How far the image is pulled down in very bright scenes, such as snow at noon. 1 leaves them alone.",
                   C, "Eye adaptation", 0.25f, 4f, 0.05f);

            Slider("adaptive_brightenseconds", "AdaptiveExposure.BrightenSeconds", "Time to adjust to dark",
                   "How long the eye takes to adjust to darkness. Slower than the reverse, as in life.",
                   C, "Eye adaptation", 0f, 60f, 0.1f, "s", true);

            Slider("adaptive_darkenseconds", "AdaptiveExposure.DarkenSeconds", "Time to adjust to light",
                   "How long the eye takes to adjust to sudden brightness.",
                   C, "Eye adaptation", 0f, 60f, 0.1f, "s", true);

            Toggle("grade_adaptive", "AdaptiveGrade.Enabled", "World-responsive colour",
                   "Lets the time of day, weather, biome, season and where you stand shift the colours on their own. Off keeps only your own settings and the style.",
                   C, "World-responsive colour");

            Slider("grade_timeofday", "AdaptiveGrade.TimeOfDayStrength", "Time of day",
                   "Golden warmth near sunrise and sunset, and the cool blue of night vision.",
                   C, "World-responsive colour", 0f, 2f, 0.05f);

            Slider("grade_weather", "AdaptiveGrade.WeatherStrength", "Weather",
                   "Rain and cloud draining colour and contrast from the scene.",
                   C, "World-responsive colour", 0f, 2f, 0.05f);

            Slider("grade_season", "AdaptiveGrade.SeasonStrength", "Season",
                   "The colour of the light through the year, cooler and crisper in winter. It never recolours leaves or grass; the game does that itself.",
                   C, "World-responsive colour", 0f, 2.0f, 0.05f);

            Slider("grade_biome", "AdaptiveGrade.BiomeStrength", "Biome",
                   "Heat, cold, dryness and lushness colouring the light where you stand.",
                   C, "World-responsive colour", 0f, 2f, 0.05f);

            Slider("grade_indoor", "AdaptiveGrade.IndoorStrength", "Indoors",
                   "How differently light reads under a roof or inside a building.",
                   C, "World-responsive colour", 0f, 2f, 0.05f);

            Slider("grade_depth", "AdaptiveGrade.DepthStrength", "Underground",
                   "How the palette shifts as you go deeper below the surface.",
                   C, "World-responsive colour", 0f, 2f, 0.05f);

            Slider("grade_underwater", "AdaptiveGrade.UnderwaterStrength", "Underwater",
                   "How strongly the water colours everything you see while submerged.",
                   C, "World-responsive colour", 0f, 2f, 0.05f);

            Slider("grade_response", "AdaptiveGrade.ResponseSeconds", "Response time",
                   "How quickly the world-responsive colour follows a change in the weather or the light.",
                   C, "World-responsive colour", 0.1f, 30f, 0.1f, "s", true);

            // ---- LIGHT -------------------------------------------------------
            const SettingTab L = SettingTab.Light;

            Slider("pbr_dapple", "PseudoPBR.SunDapple", "Canopy shade",
                   "Soft green shade and dappling on the ground under trees, taken from the game's own shadow map. Torchlight is left alone.",
                   L, "Sunlight under trees", 0f, 2f, 0.05f);

            Slider("pbr_sunflecks", "PseudoPBR.SunFlecks", "Sunflecks",
                   "Bright spots and pools of sunlight inside the shade under trees. Stretched by a low sun and swaying in the wind; only under leaves, and never brighter than open sunlight.",
                   L, "Sunlight under trees", 0f, 2f, 0.05f);

            Slider("pbr_shafts", "PseudoPBR.SunShafts", "Sun shafts",
                   "Beams of light through real gaps in a canopy, strongest looking toward the sun in humid air. Drawn through the game's own god-ray pass.",
                   L, "Sunlight under trees", 0f, 2f, 0.05f);

            Toggle("pbr_fastbeams", "PseudoPBR.FastBeams", "Fast shaft pass",
                   "Draws the sun shafts in one half-resolution pass instead of inside every terrain pixel, saving 2 to 4 ms. In the 2026-11-27 test it showed fewer beams under a canopy than the full pass.",
                   L, "Sunlight under trees", false, SettingCost.Low, true);

            Slider("pbr_motes", "PseudoPBR.DustMotes", "Dust motes",
                   "A few specks of dust drifting in the sun shafts under a canopy. None under cloud or at night.",
                   L, "Sunlight under trees", 0f, 1f, 0.05f);

            Slider("pbr_canopyradius", "PseudoPBR.CanopyRadius", "Canopy scale",
                   "How wide a patch of shadow counts as a canopy. Larger reads as bigger trees; 0 turns canopy shade off.",
                   L, "Sunlight under trees", 0f, 16f, 0.5f, "blocks", true);

            Slider("pbr_moonflecks", "PseudoPBR.MoonFlecks", "Moonlight flecks",
                   "A faint, cool version of the sunflecks by moonlight. As bright as the game says the moon is, and always far dimmer than the sun's.",
                   L, "Moonlight", 0f, 1f, 0.05f);

            // ---- SURFACES (Materials) ----------------------------------------
            const SettingTab M = SettingTab.Materials;

            Toggle("pbr_enabled", "PseudoPBR.Enabled", "Surface materials",
                   "Master switch for surface relief, roughness, metals, foliage light and the sun through leaves. It patches the shader that draws the world, so switching it rebuilds shaders.",
                   M, "Surface detail", false, SettingCost.Medium, true);

            Slider("pbr_normalstrength", "PseudoPBR.NormalStrength", "Surface relief",
                   "How strongly small details such as mortar lines, plank edges and stone grain catch the light and read as shape.",
                   M, "Surface detail", 0f, 3f, 0.05f);

            Slider("pbr_cavity", "PseudoPBR.CavityStrength", "Crevice shading",
                   "Darkens the grooves of a surface, at a scale the game's own ambient occlusion cannot see.",
                   M, "Surface detail", 0f, 2f, 0.05f);

            Slider("pbr_detaildistance", "PseudoPBR.DetailDistance", "Detail distance",
                   "How far away fine surface detail keeps working before it fades out. Lower is cheaper.",
                   M, "Surface detail", 4f, 192f, 1f, "blocks", false, SettingCost.Low);

            Slider("pbr_specularstrength", "PseudoPBR.SpecularStrength", "Sun highlights",
                   "How brightly surfaces catch a direct highlight from the sun and the moon.",
                   M, "Light response", 0f, 2f, 0.05f);

            Slider("pbr_ambientspecular", "PseudoPBR.AmbientSpecular", "Sky reflection",
                   "How much of the sky a surface reflects. On metals this is most of their brightness, so lowering it dims metals rather than only calming them.",
                   M, "Light response", 0f, 2f, 0.05f);

            Slider("pbr_blocklight", "PseudoPBR.BlockLightSpecular", "Torch and lava highlights",
                   "Lets firelight make a real highlight on nearby surfaces and shade their relief toward the flame, instead of only brightening them.",
                   M, "Light response", 0f, 2f, 0.05f);

            Slider("pbr_torchfalloff", "PseudoPBR.TorchFalloff", "Torch light falloff",
                   "Makes light pool around each torch instead of filling a room evenly, by steepening the game's own falloff. Only the dim edge of a torch's reach changes; 0 is vanilla.",
                   M, "Light response", 0f, 1f, 0.05f);

            Slider("pbr_blocklightdir", "PseudoPBR.BlockLightDirectionality", "Torch highlight direction",
                   "How strongly a firelight highlight points back at the flame rather than sitting flat on the surface.",
                   M, "Light response", 0f, 1f, 0.05f, null, true);

            Slider("pbr_roughnessbias", "PseudoPBR.RoughnessBias", "Polish or wear",
                   "Shifts every surface toward polished (below 0) or worn (above 0). 0 uses each material as measured.",
                   M, "Material response", -0.5f, 0.5f, 0.05f);

            Slider("pbr_metalresponse", "PseudoPBR.MetalResponse", "Metal response",
                   "How strongly metals behave like metals: coloured reflections and no diffuse colour of their own.",
                   M, "Material response", 0f, 1f, 0.05f, null, true);

            Slider("pbr_grain", "PseudoPBR.GrainAnisotropy", "Wood grain highlight",
                   "Stretches the highlight along the grain on wood, the way a brushed surface streaks a reflection.",
                   M, "Material response", 0f, 1f, 0.05f, null, true);

            Slider("pbr_specularaa", "PseudoPBR.SpecularAntiAliasing", "Sparkle suppression",
                   "Stops distant detailed surfaces from sparkling as you move. Lower it only to see what it was doing.",
                   M, "Material response", 0f, 2f, 0.05f, null, true);

            Slider("pbr_specocclusion", "PseudoPBR.SpecularOcclusion", "Reflections in crevices",
                   "Keeps reflections out of crevices the sky cannot see into.",
                   M, "Material response", 0f, 1f, 0.05f, null, true);

            Slider("pbr_energy", "PseudoPBR.EnergyCompensation", "Rough-surface brightness",
                   "Returns the light a rough surface would otherwise lose, so roughening something does not quietly darken it.",
                   M, "Material response", 0f, 1f, 0.05f, null, true);

            Slider("pbr_emissive", "PseudoPBR.EmissiveStrength", "Glow strength",
                   "How strongly forges, lamps and lava read as light sources rather than as bright textures.",
                   M, "Glowing materials", 0f, 2f, 0.05f);

            Slider("pbr_emissivetemperature", "PseudoPBR.EmissiveTemperature", "Glow warmth",
                   "How hot glowing surfaces look, from deep red through orange to near white.",
                   M, "Glowing materials", 0f, 1f, 0.05f);

            Slider("pbr_emissiveflicker", "PseudoPBR.EmissiveFlicker", "Glow flicker",
                   "Movement in firelight. 0 is a perfectly steady flame.",
                   M, "Glowing materials", 0f, 1f, 0.05f);

            Slider("pbr_emissivebloom", "PseudoPBR.EmissiveBloom", "Glow bloom",
                   "How much glowing surfaces feed the game's own bloom.",
                   M, "Glowing materials", 0f, 1f, 0.05f);

            Toggle("pbr_entitylighting", "PseudoPBR.EntityLighting", "Creature and player lighting",
                   "Gives animals, players and held items the same light response as the world.",
                   M, "Creatures and particles");

            Slider("pbr_entityroughness", "PseudoPBR.EntityRoughness", "Creature roughness",
                   "How polished creatures and players read. Higher is more matte.",
                   M, "Creatures and particles", 0.04f, 1f, 0.05f, null, true);

            Slider("pbr_entityspecular", "PseudoPBR.EntitySpecular", "Creature highlights",
                   "How brightly creatures and players catch a highlight.",
                   M, "Creatures and particles", 0f, 2f, 0.05f, null, true);

            Toggle("pbr_particlelighting", "PseudoPBR.ParticleLighting", "Particle lighting",
                   "Lights sparks, smoke and falling leaves the same way as everything else.",
                   M, "Creatures and particles");

            Slider("pbr_particlespecular", "PseudoPBR.ParticleSpecular", "Particle highlights",
                   "How brightly particles catch a highlight.",
                   M, "Creatures and particles", 0f, 2f, 0.05f, null, true);

            // ---- NATURE ------------------------------------------------------
            const SettingTab N = SettingTab.Nature;

            Slider("pbr_foliage", "PseudoPBR.FoliageTranslucency", "Light through leaves",
                   "Sunlight glowing through leaves and grass, with a faint sheen on their sunlit side. Strongest with a low clear sun behind the canopy.",
                   N, "Plants", 0f, 2f, 0.05f);

            Slider("pbr_gusts", "PseudoPBR.WindGusts", "Wind gusts",
                   "Gusts rolling downwind across grass and crowns, brightening the bent tips and fluttering the sunflecks. Uses the game's own wind; none in still air.",
                   N, "Plants", 0f, 1.0f, 0.05f);

            Slider("pbr_contactshade", "PseudoPBR.ContactShade", "Plant base shade",
                   "Darkens the foot of grass, flowers and crops where stems crowd, so plants sit in the ground.",
                   N, "Plants", 0f, 1.0f, 0.05f);

            Slider("pbr_damp", "PseudoPBR.Damp", "Damp and moss",
                   "Sheltered rough stone, wood and soil under trees go a little darker and greener. Most on north faces and in rainy climates.",
                   N, "Plants", 0f, 1.0f, 0.05f);

            Slider("pbr_leaves", "PseudoPBR.FallingLeaves", "Falling leaves",
                   "Leaves in each tree's own colours drifting down on the wind. A few on still days, a flurry in an autumn gust.",
                   N, "Life in the air", 0f, 1.0f, 0.05f);

            Slider("pbr_drips", "PseudoPBR.LeafDrips", "Leaf drips",
                   "Drops falling from the leaves overhead during and after rain, until they dry.",
                   N, "Life in the air", 0f, 1.0f, 0.05f);

            Slider("pbr_pollen", "PseudoPBR.Pollen", "Pollen",
                   "Pale specks drifting downwind over plants on warm, dry spring and summer days.",
                   N, "Life in the air", 0f, 1.0f, 0.05f);

            Slider("pbr_fireflies", "PseudoPBR.Fireflies", "Fireflies",
                   "Swarms flashing together low over grass at dusk and on warm, humid summer nights. Replaces the game's fireflies; 0 brings the game's back.",
                   N, "Life in the air", 0f, 1.0f, 0.05f);

            // ---- WEATHER -----------------------------------------------------
            const SettingTab W = SettingTab.Weather;

            Toggle("weather_enabled", "Weather.Enabled", "Weather response",
                   "Master switch for wet surfaces, rain fog, cloud shadows, frost and snow. Changing it rebuilds shaders.",
                   W, "Rain and wetness", false, SettingCost.Free, true);

            Slider("weather_wetness", "Weather.WetnessStrength", "Wet surfaces",
                   "How strongly rain darkens surfaces, smooths them and makes them reflect. The biggest single change rain makes to a scene.",
                   W, "Rain and wetness", 0f, 2f, 0.05f);

            Slider("weather_ripples", "Weather.RippleStrength", "Rain ripples",
                   "Rings spreading on wet ground while it rains.",
                   W, "Rain and wetness", 0f, 1f, 0.05f);

            Slider("pbr_crevicewater", "PseudoPBR.CreviceWater", "Water in crevices",
                   "On stone and brick, rain pools in the joints on top and runs down the sides around each stone. 0 is an even wet film.",
                   W, "Rain and wetness", 0.0f, 1.0f, 0.05f);

            Slider("weather_dryingseconds", "Weather.DryingSeconds", "Drying time",
                   "How long surfaces stay wet after the rain stops.",
                   W, "Rain and wetness", 1f, 600f, 5f, "s");

            Slider("weather_raincover", "Weather.RainCoverThreshold", "Shelter threshold",
                   "How much open sky a surface needs before rain reaches it. Higher keeps porches and overhangs drier.",
                   W, "Rain and wetness", 0f, 1f, 0.02f, null, true);

            Slider("weather_fogstrength", "Weather.FogStrength", "Rain fog",
                   "How much falling rain thickens the air and shortens the view.",
                   W, "Fog and overcast", 0f, 1f, 0.05f);

            Slider("weather_fogtint", "Weather.FogTint", "Rain fog colour",
                   "How strongly rain fog takes on its own grey rather than the sky's colour.",
                   W, "Fog and overcast", 0f, 1f, 0.05f);

            Slider("weather_overcast", "Weather.OvercastStrength", "Overcast softening",
                   "How much heavy cloud flattens the light and softens the shadows.",
                   W, "Fog and overcast", 0f, 1f, 0.05f);

            Slider("weather_cloudshadow", "Weather.CloudShadowStrength", "Cloud shadows",
                   "Shadows of the sky's own clouds moving across the ground.",
                   W, "Clouds", 0f, 1f, 0.05f);

            Toggle("weather_cloudsfromgame", "Weather.CloudsFromGame", "Follow the game's clouds",
                   "Reads the game's own cloud positions so the shadows line up with the sky. Off uses a generated pattern instead.",
                   W, "Clouds", true);

            Slider("weather_cloudscale", "Weather.CloudScale", "Cloud size",
                   "How large the generated cloud pattern is. Only used when the game's own clouds are not followed.",
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
                   "Master switch for how light travels through the air: distance haze, sunlit air, mist. Changing it rebuilds shaders.",
                   A, "Distance and sky", false, SettingCost.Low, true);

            Slider("atmosphere_aerial", "Atmosphere.AerialPerspective", "Distance haze",
                   "Far things take on the colour of the air between you and them, tinted by where the sun is. The one atmospheric effect the game itself lacks.",
                   A, "Distance and sky", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_sunscatter", "Atmosphere.SunScattering", "Sunlit air",
                   "Light scattering in the air around the sun, brightening the half of the sky that faces it.",
                   A, "Distance and sky", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_horizon", "Atmosphere.HorizonScattering", "Horizon glow",
                   "The horizon reads as more atmospheric than the ground at your feet.",
                   A, "Distance and sky", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_heightattenuation", "Atmosphere.HeightAttenuation", "Thin mountain air",
                   "The air thins with altitude, so a mountain top sees further than a valley floor.",
                   A, "Distance and sky", 0f, 1f, 0.05f, null, true, SettingCost.Low, true);

            Slider("atmosphere_heighthaze", "Atmosphere.HeightHaze", "Valley haze",
                   "Haze pooling in low ground and valleys. Drawn by the game's own fog, so it costs nothing extra.",
                   A, "Haze and mist", 0f, 1f, 0.05f);

            Slider("atmosphere_groundmist", "Atmosphere.GroundMist", "Ground mist",
                   "A shallow mist in low ground and over water on still, humid nights and mornings, and after rain.",
                   A, "Haze and mist", 0f, 1.0f, 0.05f);

            Slider("atmosphere_foresthaze", "Atmosphere.ForestHaze", "Forest haze",
                   "A soft haze under a canopy, so trunks fade into depth. Strongest under thick crowns in humid air.",
                   A, "Haze and mist", 0f, 1.0f, 0.05f);

            Slider("atmosphere_weatherextinction", "Atmosphere.WeatherExtinction", "Weather visibility",
                   "How much rain and snow shorten the view.",
                   A, "Weather in the air", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_weathertint", "Atmosphere.WeatherTint", "Weather colour",
                   "What the thickened air itself looks like, as opposed to how much it hides.",
                   A, "Weather in the air", 0f, 1f, 0.05f);

            Slider("atmosphere_precipitation", "Atmosphere.PrecipitationScattering", "Falling rain and snow",
                   "What rain and snow in the air do to the light passing through them.",
                   A, "Weather in the air", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_cloudatmosphere", "Atmosphere.CloudAtmosphere", "Cloud softening",
                   "Heavy cloud softening the whole atmosphere, not only the shadows.",
                   A, "Weather in the air", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_moon", "Atmosphere.MoonScattering", "Moonlit air",
                   "Moonlight scattering in the air on a clear night, following the game's own moon phase.",
                   A, "Night", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_godrays", "Atmosphere.Godrays", "God-ray boost",
                   "How much this mod's atmosphere feeds the game's own god-ray pass. 0 leaves the game's rays exactly as they were.",
                   A, "Not wired yet", 0f, 1f, 0.05f, null, false, SettingCost.Low, true);

            Slider("atmosphere_godrayquality", "Atmosphere.GodrayQuality", "God-ray quality",
                   "0 low, 1 medium, 2 high. Quality only; it cannot switch god-rays on.",
                   A, "Not wired yet", 0f, 2f, 1f, null, true, SettingCost.Medium);

            Slider("atmosphere_cloudedge", "Atmosphere.CloudEdgeScattering", "Sun through broken cloud",
                   "FOUNDATION ONLY. The data this needs, which way a cloud edge faces, is not available yet, so it does not do what its name says.",
                   A, "Not wired yet", 0f, 1f, 0.05f, null, true, SettingCost.Low, true);

            Slider("atmosphere_dapple", "Atmosphere.DappleInteraction", "Dappled air",
                   "FOUNDATION ONLY. The air responding to dappled sunlight needs a per-pixel link the current structure does not carry.",
                   A, "Not wired yet", 0f, 1f, 0.05f, null, true, SettingCost.Low, true);

            // ---- WATER -------------------------------------------------------
            const SettingTab H = SettingTab.Water;

            Toggle("water_enabled", "Water.Enabled", "Water surface",
                   "This mod's water: pixel reflections, depth colour, ripples, glints, shorelines and caustics, built on the game's own water. Off compiles the game's water untouched.",
                   H, "Surface", false, SettingCost.Low, true);

            Slider("water_waves", "Water.Waves", "Wave relief",
                   "How much the small waves tilt the reflection and the glints. Wind makes them taller, rivers stretch them along the current and still ponds calm them. 0 is a flat mirror.",
                   H, "Surface", 0f, 2f, 0.05f);

            Slider("water_ripples", "Water.RainRipples", "Rain ripples on water",
                   "Rings where rain lands on open water, only while rain is falling.",
                   H, "Surface", 0f, 1f, 0.05f);

            Slider("water_shore", "Water.Shore", "Shorelines",
                   "The water thins over its last sliver of depth so the bank meets it softly, and a pixel foam line laps along the edge. The game's own edge foam steps back while this is on.",
                   H, "Surface", 0f, 1f, 0.05f);

            Slider("water_glints", "Water.Glints", "Sun and moon glints",
                   "Sharp highlights of the sun and moon on the waves, one water pixel at a time. The game's own broad highlight steps back as these come in.",
                   H, "Surface", 0f, 1f, 0.05f);

            Slider("water_sky", "Water.SkyReflection", "Reflection strength",
                   "How much of the sky and the surroundings the water reflects, more at a glancing angle. One colour per water pixel, like the block reflections.",
                   H, "Reflection", 0f, 1f, 0.05f);

            Toggle("water_scene", "Water.SceneReflections", "World in the water",
                   "Banks, trees and the far shore reflected in the water, found in the frame already drawn. Anything off screen falls back to the sky; off compiles the sky-only water.",
                   H, "Reflection", false, SettingCost.Medium, true);

            Slider("water_refraction", "Water.Refraction", "Refraction",
                   "How much the waves bend your view of the bottom. 0 is a straight view.",
                   H, "Reflection", 0f, 1f, 0.05f, null, false, SettingCost.Low);

            Slider("water_soft", "Water.ReflectionSoftness", "Reflection softness",
                   "A subtle blur of the reflection between neighbouring water pixels. Each pixel stays one colour; 0 is the sharp pixel image.",
                   H, "Reflection", 0f, 1f, 0.05f, null, false, SettingCost.Low);

            Slider("water_depthtint", "Water.DepthTint", "Depth colour",
                   "Deep water turns blue-green and opaque while shallow water stays clear, measured from what is really behind the surface.",
                   H, "Depth and clarity", 0f, 1f, 0.05f);

            Slider("water_clarity", "Water.ShallowClarity", "Shallow clarity",
                   "How see-through water under about a block deep is. 0 keeps the game's own opacity.",
                   H, "Depth and clarity", 0f, 1f, 0.05f);

            Slider("water_caustics", "Water.Caustics", "Caustics",
                   "The moving web of sunlight the waves focus onto the bottom, in the colour of the bed it lands on. Follows the sun, shadows and clouds; 0 compiles the caustic code out.",
                   H, "Caustics", 0f, 1f, 0.05f);

            Slider("water_causticblur", "Water.CausticBlur", "Caustic softness",
                   "Blurs the caustic web on the bottom's own pixel grid. 0 is crisp focus lines, around 0.35 the soft glow of a clear lake, 1 broad dappled light.",
                   H, "Caustics", 0f, 1f, 0.05f);

            Slider("water_causticglow", "Water.CausticGlow", "Caustic glow",
                   "Blooms the brightest caustic lines into a soft halo while the cells between keep their edges. 0 is even softness.",
                   H, "Caustics", 0f, 1f, 0.05f);

            Slider("water_causticdispersion", "Water.CausticDispersion", "Caustic colour split",
                   "Faint coloured fringes on the sharpest caustic lines, as water bends blue light more than red. 1 exaggerates it for style.",
                   H, "Caustics", 0f, 1f, 0.05f);

            // ---- REFLECTIONS -------------------------------------------------
            const SettingTab R = SettingTab.Reflections;

            Slider("pbr_pixelreflect", "PseudoPBR.PixelReflection", "Block reflections",
                   "How much of the surroundings a wet, polished or metal block reflects. One colour per texture pixel on purpose: a pixel-art reflection, not a mirror.",
                   R, "Blocks", 0f, 1f, 0.05f);

            Toggle("reflections_scene", "Reflections.SceneReflections", "Reflect the world on blocks",
                   "Reflects real trees, terrain and buildings on blocks instead of only the sky. Copies the finished frame every frame, which makes it the most expensive setting in the mod.",
                   R, "Blocks", false, SettingCost.High, true);

            // ---- PERFORMANCE -------------------------------------------------
            const SettingTab P = SettingTab.Performance;

            Choice("perf_quality", "Performance.Quality", "Quality tier",
                   "Scales what the effects cost (canopy taps, detail distance, water blur, ripple layers and caustic waves). It never switches on an effect you turned off; High is the reference look.",
                   P, "Quality", QualityTiers);

            Toggle("perf_prepass", "Performance.DepthPrepass", "Depth prepass",
                   "Draws the world's depth first so hidden pixels are skipped later. It stands aside on its own where measured GPU time says it costs more than it saves. Changing it rebuilds shaders.",
                   P, "Quality", false, SettingCost.Low, true);

            Toggle("perf_frametimes", "Performance.ShowFrameTimes", "Frame-time readout",
                   "Shows milliseconds per render stage and per mod pass in the top-left corner, measured on the GPU. Same as .vvperf.",
                   P, "Measure");

            // ---- DEBUG -------------------------------------------------------
            const SettingTab D = SettingTab.Debug;

            Slider("compare_wipe", "CompareWipe", "Compare wipe",
                   "Splits the frame between vanilla and Vintage Visuals at this point. 0 is normal rendering and 0.5 puts the seam in the middle; the Compare button below sets it for you.",
                   D, "Debug utilities", 0f, 1f, 0.05f, null, false, SettingCost.Free, false, true);

            Toggle("shader_debug_dump", "EnableShaderDebugDump", "Shader debug dump",
                   "Writes the patched shader source to the game's ShaderDebug folder on every load, so patch delivery can be inspected. Leave off for play.",
                   D, "Debug utilities", true, SettingCost.Free, false, true);

            Toggle("write_scene_report", "WriteSceneReport", "Write scene report",
                   "Writes one report describing what the world asked for and what each subsystem was allowed to change.",
                   D, "Debug utilities", true, SettingCost.Free, false, true);

            return s;
        }
    }
}
