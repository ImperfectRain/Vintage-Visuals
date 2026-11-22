using System;
using System.Collections.Generic;
using VintageVisuals.Common;
using VintageVisuals.PseudoPBR;

namespace VintageVisuals.Ui
{
    /// <summary>Whether a setting can change the picture right now, and if not, why not.</summary>
    public enum AvailabilityState
    {
        /// <summary>The control drives code that runs in this build and with this config.</summary>
        Active,

        /// <summary>
        /// The control works, but only in certain scenes or on certain things.
        /// It stays a working control and carries a note saying where to look.
        /// </summary>
        Conditional,

        /// <summary>
        /// The code exists in this build, but a switch above it is off, so the
        /// value is kept and does nothing until that switch is turned on.
        /// </summary>
        ParentOff,

        /// <summary>
        /// Nothing in this build reads the value. The row is hidden from normal
        /// tuning and shown disabled, with the reason, under Advanced.
        /// </summary>
        Unavailable,
    }

    public struct SettingAvailability
    {
        public readonly AvailabilityState State;

        /// <summary>One line the player can act on. Null when Active.</summary>
        public readonly string Reason;

        public SettingAvailability(AvailabilityState state, string reason)
        {
            State = state;
            Reason = reason;
        }

        public bool HasWorkingControl
        {
            get { return State == AvailabilityState.Active || State == AvailabilityState.Conditional; }
        }

        public static readonly SettingAvailability Active = new SettingAvailability(AvailabilityState.Active, null);
    }

    /// <summary>
    /// Decides, per setting, whether the studio may present it as a working
    /// control.
    ///
    /// WHY THIS EXISTS. The registry lists every setting the config has, but the
    /// terrain is currently drawn by the modular material shader
    /// (pbrterrainmaterial.glsl) while the legacy pseudopbr monolith is gated
    /// off by <see cref="PseudoPbrSubsystem.TerrainShaderPatchesEnabled"/>.
    /// Foliage light, sun shafts, wood grain, the emissive sliders and the
    /// scene/pixel reflections are read ONLY by that monolith, so in this build
    /// they are sliders wired to nothing. A slider that moves and changes
    /// nothing is worse than no slider: it invites the player to blame it for
    /// whatever else they are seeing (CLAUDE.md, "check a function is
    /// REACHABLE").
    ///
    /// Three answers are kept apart because they have three different fixes:
    /// unavailable in this build (nothing the player can do), parent feature off
    /// (turn a switch on) and active only in some scenes (go and look in the
    /// right place).
    ///
    /// THE BUILD FACTS COME FROM THE RUNTIME GATES, not from a list kept here.
    /// When the terrain gate is flipped back on, the rows tied to it become
    /// available without anyone editing this file. The per-setting mapping of
    /// WHICH shader reads a value is still hand-maintained, and that is the part
    /// a future shader change can leave stale - tools/smoketest checks that
    /// every code named here is a real registry code.
    /// </summary>
    public static class SettingAvailabilityRules
    {
        private const string LegacyTerrainReason =
            "Not in this build: only the legacy terrain shader reads this.";

        private const string FoundationReason =
            "Foundation only: the data it needs does not exist yet.";

        /// <summary>Read only by pseudopbr.glsl, which is gated off with the legacy terrain path.</summary>
        public static readonly HashSet<string> LegacyTerrainOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "pbr_grain",
            "pbr_foliage",
            "pbr_dapple",
            "pbr_shafts",
            "pbr_canopyradius",
            "pbr_emissivebloom",
            "reflections_scene",
            "pbr_pixelreflect",

            // Uploaded by the PBR binder, but on terrain only pseudopbr.glsl
            // reads vv_weatherRipples and vv_weatherRainCover.
            "weather_ripples",
            "weather_raincover",
        };

        /// <summary>Declared FOUNDATION ONLY in their own descriptions and in src/Atmosphere/README.md.</summary>
        public static readonly HashSet<string> FoundationOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "atmosphere_cloudedge",
            "atmosphere_dapple",
        };

        private const string DebugOnlyReason =
            "Not wired yet: only an atmosphere debug view uses it.";

        /// <summary>
        /// Uploaded and declared, but the one function that reads the value is
        /// called only from a debug view. vvAtmosGodrayLevel feeds atmosphere
        /// debug view 9 and nothing else, so neither slider reaches the
        /// game's crepuscular ray pass in this build.
        /// </summary>
        public static readonly HashSet<string> DebugViewOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "atmosphere_godrays",
            "atmosphere_godrayquality",
        };

        /// <summary>
        /// Work, but only on certain things or in certain scenes. The note says
        /// where to look, so a test of the control is made in a scene that can
        /// show it.
        /// </summary>
        public static readonly Dictionary<string, string> Conditions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "pbr_normalstrength", "Judge on close stone or planks, not grass or leaves." },
            { "pbr_cavity", "Judge on close stone or planks, not grass or leaves." },
            { "pbr_detaildistance", "Grass and leaves are excluded in this build." },
            { "pbr_blocklight", "Creatures, items and particles only in this build." },
            { "pbr_emissive", "Glowing creatures, items and particles only." },
            { "pbr_emissivetemperature", "Glowing creatures, items and particles only." },
            { "pbr_emissiveflicker", "Glowing creatures, items and particles only." },
            { "pbr_blocklightdir", "Creatures, items and particles only in this build." },
            { "weather_wetness", "Shows while it rains and while surfaces dry." },
            { "weather_fogstrength", "Shows only while it is raining." },
            { "weather_fogtint", "Shows only while it is raining." },
            { "weather_frost", "Shows only in freezing weather." },
            { "weather_snowdusting", "Shows only where snow is settling." },
            { "adaptive_darkgain", "Shows in dark scenes such as caves or night." },
            { "adaptive_brightgain", "Shows in bright scenes such as snow at noon." },
            { "atmosphere_moon", "Shows on clear nights." },
        };

        /// <summary>
        /// The switches a setting sits under, outermost first. Each entry is a
        /// config path and the name the player knows that switch by.
        /// </summary>
        private static readonly Dictionary<string, Parent[]> Parents = BuildParents();

        public static SettingAvailability Evaluate(VisualSetting setting, VintageVisualsConfig config)
        {
            if (setting == null) return SettingAvailability.Active;

            if (!PseudoPbrSubsystem.TerrainShaderPatchesEnabled && LegacyTerrainOnly.Contains(setting.Code))
            {
                return new SettingAvailability(AvailabilityState.Unavailable, LegacyTerrainReason);
            }

            if (FoundationOnly.Contains(setting.Code))
            {
                return new SettingAvailability(AvailabilityState.Unavailable, FoundationReason);
            }

            if (DebugViewOnly.Contains(setting.Code))
            {
                return new SettingAvailability(AvailabilityState.Unavailable, DebugOnlyReason);
            }

            if (setting.Code == "pbr_blocklight" || setting.Code == "pbr_blocklightdir")
            {
                // The terrain material shader does not read these, so they reach
                // the screen only through the entity and particle programs.
                if (config != null && !config.PseudoPBR.EntityLighting && !config.PseudoPBR.ParticleLighting)
                {
                    return new SettingAvailability(AvailabilityState.ParentOff,
                        "Turn on Creature and player lighting or Particle lighting.");
                }
            }

            Parent[] parents;
            if (config != null && Parents.TryGetValue(setting.Code, out parents))
            {
                foreach (Parent parent in parents)
                {
                    if (ConfigAccess.Get(config, parent.Path) < 0.5f)
                    {
                        return new SettingAvailability(AvailabilityState.ParentOff,
                            "Turn on " + parent.Name + " to adjust this.");
                    }
                }
            }

            if (setting.Code == "weather_cloudscale" && config != null && config.Weather.CloudsFromGame)
            {
                // The one inverted parent: the size belongs to the generated
                // pattern, which is not drawn while the game's clouds are followed.
                return new SettingAvailability(AvailabilityState.ParentOff,
                    "Turn off Follow the game's clouds to use this.");
            }

            string condition;
            if (Conditions.TryGetValue(setting.Code, out condition))
            {
                return new SettingAvailability(AvailabilityState.Conditional, condition);
            }

            return SettingAvailability.Active;
        }

        /// <summary>Every code this file names, for the smoke check that they are real.</summary>
        public static IEnumerable<string> NamedCodes()
        {
            foreach (string code in LegacyTerrainOnly) yield return code;
            foreach (string code in FoundationOnly) yield return code;
            foreach (string code in DebugViewOnly) yield return code;
            foreach (string code in Conditions.Keys) yield return code;
            foreach (string code in Parents.Keys) yield return code;
        }

        /// <summary>Every parent path this file names, for the smoke check that they resolve.</summary>
        public static IEnumerable<string> ParentPaths()
        {
            foreach (Parent[] chain in Parents.Values)
            {
                foreach (Parent parent in chain) yield return parent.Path;
            }
        }

        private struct Parent
        {
            public readonly string Path;
            public readonly string Name;

            public Parent(string path, string name)
            {
                Path = path;
                Name = name;
            }
        }

        private static Dictionary<string, Parent[]> BuildParents()
        {
            var map = new Dictionary<string, Parent[]>(StringComparer.Ordinal);

            var colour = new Parent("ColorGrade.Enabled", "Colour grading");
            var eye = new Parent("AdaptiveExposure.Enabled", "Eye adaptation");
            var world = new Parent("AdaptiveGrade.Enabled", "World-responsive colour");
            var pbr = new Parent("PseudoPBR.Enabled", "Surface materials");
            var entity = new Parent("PseudoPBR.EntityLighting", "Creature and player lighting");
            var particle = new Parent("PseudoPBR.ParticleLighting", "Particle lighting");
            var weather = new Parent("Weather.Enabled", "Weather response");
            var air = new Parent("Atmosphere.Enabled", "Atmosphere");

            void Under(Parent[] chain, params string[] codes)
            {
                foreach (string code in codes) map[code] = chain;
            }

            Under(new[] { colour },
                "colorgrade_exposure", "colorgrade_contrast", "colorgrade_saturation",
                "colorgrade_temperature", "colorgrade_tonemapstrength",
                "adaptive_enabled", "grade_adaptive", "grade_style", "grade_stylestrength", "grade_response");

            // StepAdaptation eases to neutral unless BOTH switches are on.
            Under(new[] { colour, eye },
                "adaptive_darkgain", "adaptive_brightgain", "adaptive_brightenseconds", "adaptive_darkenseconds");

            // GradeStack.Evaluate applies only the style when adaptive grading is off.
            Under(new[] { colour, world },
                "grade_timeofday", "grade_weather", "grade_biome", "grade_indoor", "grade_depth", "grade_underwater");

            // Read by the terrain material shader, which IsPatchGroupEnabled
            // removes entirely when Surface materials is off.
            Under(new[] { pbr },
                "pbr_normalstrength", "pbr_detaildistance", "pbr_cavity", "pbr_specularstrength",
                "pbr_ambientspecular", "pbr_roughnessbias", "pbr_metalresponse", "pbr_specularaa",
                "pbr_specocclusion", "pbr_energy",
                "pbr_grain", "pbr_foliage", "pbr_dapple", "pbr_shafts", "pbr_canopyradius",
                "pbr_emissive", "pbr_emissivetemperature", "pbr_emissiveflicker", "pbr_emissivebloom",
                "reflections_scene", "pbr_pixelreflect");

            // The entity and particle groups are gated on their own flags, not
            // on Surface materials - see IsPatchGroupEnabled.
            Under(new[] { entity }, "pbr_entityroughness", "pbr_entityspecular");
            Under(new[] { particle }, "pbr_particlespecular");

            Under(new[] { weather },
                "weather_wetness", "weather_dryingseconds", "weather_raincover", "weather_ripples",
                "weather_fogstrength", "weather_fogtint", "weather_cloudshadow", "weather_cloudsfromgame",
                "weather_cloudheight", "weather_frost", "weather_snowdusting", "weather_clouddrift");

            // WeatherSubsystem zeroes the overcast term unless both are on.
            Under(new[] { weather, pbr }, "weather_overcast");

            // Only the generated pattern has a size; see Evaluate for the inverse switch.
            Under(new[] { weather }, "weather_cloudscale");

            Under(new[] { air },
                "atmosphere_aerial", "atmosphere_horizon", "atmosphere_sunscatter", "atmosphere_heighthaze",
                "atmosphere_heightattenuation", "atmosphere_weatherextinction", "atmosphere_weathertint",
                "atmosphere_cloudatmosphere", "atmosphere_precipitation", "atmosphere_godrays",
                "atmosphere_godrayquality", "atmosphere_moon", "atmosphere_cloudedge", "atmosphere_dapple");

            return map;
        }
    }
}
