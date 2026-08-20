using System.Collections.Generic;

namespace VintageVisuals.Common
{
    /// <summary>
    /// The on-disk config, serialized to
    /// <c>VintagestoryData/ModConfig/vintagevisuals.json</c>.
    ///
    /// Public properties rather than fields: Newtonsoft's default contract
    /// resolver handles both, but properties are what every other VS mod uses
    /// and what round-trips predictably if the resolver is ever customised.
    /// </summary>
    public class VintageVisualsConfig
    {
        /// <summary>
        /// Dumps every patched shader to <c>VintagestoryData/ShaderDebug/</c>.
        /// This is the fastest way to answer "did my patch actually land, and
        /// what did the merged GLSL end up looking like".
        /// </summary>
        public bool EnableShaderDebugDump { get; set; } = false;

        public ColorGradeConfig ColorGrade { get; set; } = new ColorGradeConfig();

        public AdaptiveExposureConfig AdaptiveExposure { get; set; } = new AdaptiveExposureConfig();

        public PseudoPbrConfig PseudoPBR { get; set; } = new PseudoPbrConfig();

        public WeatherConfig Weather { get; set; } = new WeatherConfig();

        /// <summary>
        /// Clamps every value into its supported range, returning a description
        /// of anything that had to be corrected.
        ///
        /// Config is hand-edited, and an out-of-range value here means a fully
        /// white or fully black screen — a state in which the player cannot
        /// read the log to find out why. Clamping keeps the game usable and
        /// says what it did.
        /// </summary>
        public List<string> ClampToValidRanges()
        {
            var corrections = new List<string>();
            ColorGrade.ClampToValidRanges(corrections);
            AdaptiveExposure.ClampToValidRanges(corrections);
            PseudoPBR.ClampToValidRanges(corrections);
            Weather.ClampToValidRanges(corrections);
            return corrections;
        }
    }

    /// <summary>
    /// Phase 2 weather system. Currently one effect: what rain does to how
    /// surfaces respond to light.
    /// </summary>
    public class WeatherConfig
    {
        /// <summary>
        /// Master toggle. Off eases wetness back to dry rather than snapping,
        /// so flipping it mid-storm is not a visible jolt.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// How wet rain makes surfaces look. 0 is off; 1 is the tuned look.
        ///
        /// Above 1 exaggerates past anything physical, which is a legitimate
        /// place to be for a stylised look and a silly one for a realistic one.
        /// </summary>
        public float WetnessStrength { get; set; } = 1.0f;

        /// <summary>
        /// Seconds for a soaked surface to dry once the rain stops.
        ///
        /// The asymmetry is the whole effect. Surfaces wet in seconds and dry
        /// over a minute or more; easing both at the same rate reads as a fade
        /// rather than as weather.
        /// </summary>
        public float DryingSeconds { get; set; } = 60.0f;

        /// <summary>
        /// How exposed to the sky a surface must be before rain reaches it.
        ///
        /// The signal is vanilla's per-vertex sun light, which bleeds sideways
        /// under an overhang - so a linear reading only ever dries out fully
        /// enclosed spaces, and a porch stays as wet as the lawn. Raising this
        /// requires near-full exposure and gets overhangs, tree canopy and
        /// doorways back.
        ///
        /// It is a threshold on a soft signal, not a rain occlusion test. The
        /// game does have one - it is how torches are extinguished - but it
        /// answers per block on the CPU, and this question is per fragment.
        /// </summary>
        public float RainCoverThreshold { get; set; } = 0.82f;

        /// <summary>How much rain thickens the air. 0 leaves vanilla fog alone.</summary>
        public float FogStrength { get; set; } = 0.35f;

        /// <summary>How much rain drains colour from the fog and cools it.</summary>
        public float FogTint { get; set; } = 0.6f;

        /// <summary>
        /// Depth of cloud shadows on the ground. 0 is off.
        ///
        /// Scaled by daylight before it reaches the shader, so this is the
        /// depth at noon rather than a constant darkening.
        /// </summary>
        public float CloudShadowStrength { get; set; } = 0.35f;

        /// <summary>Blocks across one cloud cell. Larger means broader, slower shadows.</summary>
        public float CloudScale { get; set; } = 190.0f;

        /// <summary>How fast cloud shadows drift, in cells per minute, along the world's wind.</summary>
        public float CloudDriftSpeed { get; set; } = 0.9f;

        /// <summary>
        /// World height the shadow-casting cloud deck sits at, in blocks.
        ///
        /// This is what decides how far a shadow slides from the thing casting
        /// it as the sun drops, so it is the control for whether shadows sit
        /// under the clouds or somewhere off to the side of them. Vanilla's
        /// clouds are much higher than this default; using their real altitude
        /// moves the shadow the better part of a kilometre at a low sun, which
        /// reads as a bug rather than as evening.
        /// </summary>
        public float CloudHeight { get; set; } = 160.0f;

        internal void ClampToValidRanges(List<string> corrections)
        {
            WetnessStrength = ColorGradeConfig.Clamp(WetnessStrength, 0.0f, 2.0f,
                "Weather.WetnessStrength", corrections);
            DryingSeconds = ColorGradeConfig.Clamp(DryingSeconds, 1.0f, 600.0f,
                "Weather.DryingSeconds", corrections);
            RainCoverThreshold = ColorGradeConfig.Clamp(RainCoverThreshold, 0.0f, 1.0f,
                "Weather.RainCoverThreshold", corrections);
            FogStrength = ColorGradeConfig.Clamp(FogStrength, 0.0f, 1.0f,
                "Weather.FogStrength", corrections);
            FogTint = ColorGradeConfig.Clamp(FogTint, 0.0f, 1.0f,
                "Weather.FogTint", corrections);
            CloudShadowStrength = ColorGradeConfig.Clamp(CloudShadowStrength, 0.0f, 1.0f,
                "Weather.CloudShadowStrength", corrections);
            CloudScale = ColorGradeConfig.Clamp(CloudScale, 32.0f, 512.0f,
                "Weather.CloudScale", corrections);
            CloudDriftSpeed = ColorGradeConfig.Clamp(CloudDriftSpeed, 0.0f, 8.0f,
                "Weather.CloudDriftSpeed", corrections);
            CloudHeight = ColorGradeConfig.Clamp(CloudHeight, 40.0f, 400.0f,
                "Weather.CloudHeight", corrections);
        }
    }

    /// <summary>
    /// Phase 4 material system: what each block face is made of, and how that
    /// changes the way light lands on it.
    /// </summary>
    public class PseudoPbrConfig
    {
        /// <summary>
        /// Master switch for the rendering half. False leaves the atlas build
        /// and the reports alone — they are diagnostics that cost nothing at
        /// runtime — and stops chunkopaque.fsh being patched at all, so the
        /// world renders from vanilla source.
        ///
        /// Defaults to FALSE, unlike every other feature in this mod. Not
        /// because the code is believed wrong, but because of what it costs
        /// when it is: this is the only subsystem that patches the shader
        /// drawing the world, and its two failures so far were a sepia screen
        /// and missing terrain, neither of which a player could diagnose. The
        /// other subsystems degrade to "no effect"; this one degrades to "no
        /// world". Until it has been seen working on a real GPU, opting in is
        /// the right default. Flip it once someone has looked.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Global multiplier on the surface relief, on top of the per-material
        /// strength already baked into the atlas.
        ///
        /// 1.0 is the tuned look. This exists because relief is the one part of
        /// the material system that is purely a matter of taste and cannot be
        /// judged from a texture — some players will want log grooves obvious,
        /// others will want the vanilla flat look back without losing the rest.
        /// 0 flattens everything, which is not the same as Enabled=false: the
        /// shader still samples, so it still costs what it costs.
        /// </summary>
        public float NormalStrength { get; set; } = 1.0f;

        /// <summary>
        /// Global multiplier on the microfacet specular, and on the energy it
        /// takes back out of the diffuse.
        ///
        /// Both, deliberately, so 0 is exactly vanilla. A player who turns the
        /// effect off has to get their old image back, not a darker one.
        /// </summary>
        public float SpecularStrength { get; set; } = 1.0f;

        /// <summary>
        /// Shifts every material's roughness. Negative is glossier, positive is
        /// more matte.
        ///
        /// The single most useful control for style, because roughness is what
        /// separates a look that reads as "wet" from one that reads as "dry" -
        /// and where that line sits is taste, not physics.
        /// </summary>
        public float RoughnessBias { get; set; } = 0.0f;

        /// <summary>
        /// How metallic the reflective materials read. 0 makes every surface a
        /// dielectric with a white highlight; 1 lets metals tint their highlight
        /// by their own albedo, which is what makes copper look like copper.
        ///
        /// Worth turning down for a flatter, more stylised look, since a
        /// coloured specular is one of the strongest "modern renderer" cues.
        /// </summary>
        public float MetalResponse { get; set; } = 1.0f;

        /// <summary>
        /// Strength of the sky reflection, using vanilla's fog colour as the
        /// environment.
        ///
        /// Without it, the sun is the only light this shader knows about, so a
        /// metal block in shade or indoors has no highlight at all and reads as
        /// dark plastic. It is the cheapest single step toward realism here,
        /// and turning it off is the cheapest step toward a flat look.
        /// </summary>
        public float AmbientSpecular { get; set; } = 0.35f;

        /// <summary>
        /// Geometric specular antialiasing strength (Kaplanyan et al. 2016;
        /// Tokuyoshi and Kaplanyan 2019).
        ///
        /// Defaults to full, and should normally stay there. Derived normals
        /// carry far higher frequencies than a hand-authored map, so without
        /// this a rough surface with a tight highlight sparkles as the camera
        /// moves. Exposed because it is a real trade-off - it widens highlights
        /// slightly - and because being able to turn it off is how anyone
        /// confirms it is doing something.
        /// </summary>
        public float SpecularAntiAliasing { get; set; } = 1.0f;

        /// <summary>
        /// Distance in blocks at which surface relief has faded to nothing.
        /// Full strength is held to a third of it.
        ///
        /// Higher costs nothing in frame time but shows more aliasing, since
        /// the material atlas carries no mipmaps and one screen pixel covers
        /// many texels at range.
        /// </summary>
        public float DetailDistance { get; set; } = 48.0f;

        /// <summary>
        /// Strength of highlights from torches, lava, lanterns and glowing
        /// blocks.
        ///
        /// Until this existed, the sun was the only light the material system
        /// could produce a highlight from - so underground, where every light
        /// is a block light, none of it did anything. Deliberately not scaled
        /// by daylight or shadow: a torch burns in a cave at midnight.
        /// </summary>
        public float BlockLightSpecular { get; set; } = 1.0f;

        /// <summary>
        /// How much to trust the estimated direction of block light.
        ///
        /// Vanilla bakes every light source into one directionless colour, so
        /// the direction is recovered from the gradient of that colour across
        /// the surface - light gets brighter toward whatever is emitting it. At
        /// 0 block light is treated as purely ambient, which is safe and dull;
        /// at 1 the estimate is trusted fully, which gives a highlight that
        /// tracks a torch as you walk past it and can wobble where the light
        /// field is noisy.
        /// </summary>
        public float BlockLightDirectionality { get; set; } = 0.7f;

        /// <summary>
        /// Renders one layer of the material system on its own instead of the
        /// finished image. 0 renders normally.
        ///
        ///   1  normal map as stored (blue forced flat, matches the preview PNG)
        ///   2  roughness
        ///   3  specular mask
        ///   4  relief contribution, biased so "no change" is mid grey
        ///   5  specular highlight on its own
        ///   6  perturbed normal in world space
        ///   7  reflectance at normal incidence (grey = dielectric, coloured = metal)
        ///   8  the roughness the shading model actually uses, after bias and
        ///      specular antialiasing
        ///   9  estimated block-light direction (flat = treated as ambient)
        ///
        /// A float rather than an int because ConfigLib's float settings are
        /// the ones this mod has confirmed working in game; an integer category
        /// would be a second unverified thing in the same change. Stepped by 1
        /// in the GUI, rounded in the shader.
        /// </summary>
        public float DebugView { get; set; } = 0.0f;

        /// <summary>
        /// Writes VintagestoryData/VintageVisuals/material-report.txt listing
        /// how every loaded block was classified.
        ///
        /// On by default while the subsystem is being built: it is the only way
        /// to see whether modded blocks classify sensibly, it costs one file
        /// write per session, and it changes nothing about rendering. Turn it
        /// off once the classification looks right.
        /// </summary>
        public bool WriteMaterialReport { get; set; } = true;

        /// <summary>
        /// Derives the material atlas at world load, caching it to disk.
        ///
        /// Costs seconds of CPU on the first run for a given texture set and
        /// almost nothing afterwards. Nothing consumes the atlas yet, so this
        /// currently only produces the cache and the preview images.
        /// </summary>
        public bool BuildMaterialAtlas { get; set; } = true;

        /// <summary>
        /// Writes the derived atlas as viewable BMPs alongside the cache.
        ///
        /// On while the subsystem is being built: these maps cannot be judged
        /// from numbers, and looking at them is the only way to tell whether
        /// wood grooves read as grooves. Turn it off to save the disk write.
        /// </summary>
        public bool WriteAtlasPreview { get; set; } = true;

        internal void ClampToValidRanges(List<string> corrections)
        {
            // Capped at 2 rather than left open. Above roughly 1.4 the
            // reconstructed Z collapses toward zero and every face starts
            // shading as though lit from its own edge, which reads as a
            // rendering fault rather than as strong relief.
            NormalStrength = ColorGradeConfig.Clamp(NormalStrength, 0.0f, 2.0f,
                "PseudoPBR.NormalStrength", corrections);
            SpecularStrength = ColorGradeConfig.Clamp(SpecularStrength, 0.0f, 2.0f,
                "PseudoPBR.SpecularStrength", corrections);
            DebugView = ColorGradeConfig.Clamp(DebugView, 0.0f, 10.0f,
                "PseudoPBR.DebugView", corrections);
            RoughnessBias = ColorGradeConfig.Clamp(RoughnessBias, -0.5f, 0.5f,
                "PseudoPBR.RoughnessBias", corrections);
            MetalResponse = ColorGradeConfig.Clamp(MetalResponse, 0.0f, 1.0f,
                "PseudoPBR.MetalResponse", corrections);
            AmbientSpecular = ColorGradeConfig.Clamp(AmbientSpecular, 0.0f, 2.0f,
                "PseudoPBR.AmbientSpecular", corrections);
            SpecularAntiAliasing = ColorGradeConfig.Clamp(SpecularAntiAliasing, 0.0f, 2.0f,
                "PseudoPBR.SpecularAntiAliasing", corrections);
            DetailDistance = ColorGradeConfig.Clamp(DetailDistance, 4.0f, 192.0f,
                "PseudoPBR.DetailDistance", corrections);
            BlockLightSpecular = ColorGradeConfig.Clamp(BlockLightSpecular, 0.0f, 2.0f,
                "PseudoPBR.BlockLightSpecular", corrections);
            BlockLightDirectionality = ColorGradeConfig.Clamp(BlockLightDirectionality, 0.0f, 1.0f,
                "PseudoPBR.BlockLightDirectionality", corrections);
        }
    }

    /// <summary>
    /// Eye adaptation. Multiplies <see cref="ColorGradeConfig.Exposure"/>
    /// rather than replacing it, so a player who has dialled in a manual
    /// exposure keeps it and this rides on top.
    /// </summary>
    public class AdaptiveExposureConfig
    {
        /// <summary>
        /// On by default, unlike the tonemap. The tonemap is off because of an
        /// unresolved correctness question about colour space; this has no such
        /// question — it is bounded, clamped, and cannot blow the image out.
        /// The only risk is taste, and the effect is the point of the feature.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Multiplier in pitch darkness. Above 1 brightens.</summary>
        public float DarkGain { get; set; } = 1.6f;

        /// <summary>Multiplier in full light. 1.0 leaves bright scenes exactly as authored.</summary>
        public float BrightGain { get; set; } = 1.0f;

        /// <summary>
        /// Seconds to adapt toward darkness (the multiplier rising). Slow,
        /// mirroring how long human dark adaptation actually takes.
        /// </summary>
        public float BrightenSeconds { get; set; } = 4.0f;

        /// <summary>Seconds to adapt toward light (the multiplier falling). Fast.</summary>
        public float DarkenSeconds { get; set; } = 1.0f;

        internal void ClampToValidRanges(List<string> corrections)
        {
            DarkGain = ColorGradeConfig.Clamp(DarkGain, 0.25f, 4.0f, "AdaptiveExposure.DarkGain", corrections);
            BrightGain = ColorGradeConfig.Clamp(BrightGain, 0.25f, 4.0f, "AdaptiveExposure.BrightGain", corrections);
            BrightenSeconds = ColorGradeConfig.Clamp(BrightenSeconds, 0.0f, 60.0f, "AdaptiveExposure.BrightenSeconds", corrections);
            DarkenSeconds = ColorGradeConfig.Clamp(DarkenSeconds, 0.0f, 60.0f, "AdaptiveExposure.DarkenSeconds", corrections);
        }
    }

    public class ColorGradeConfig
    {
        /// <summary>Master toggle. When false no color grading uniforms are uploaded and the pass is a no-op.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Linear multiplier applied before the tonemap. 1.0 is unchanged.</summary>
        public float Exposure { get; set; } = 1.0f;

        /// <summary>Contrast pivoting around mid-grey. 1.0 is unchanged, 0 is flat grey.</summary>
        public float Contrast { get; set; } = 1.0f;

        /// <summary>Saturation. 1.0 is unchanged, 0 is greyscale, &gt;1 oversaturates.</summary>
        public float Saturation { get; set; } = 1.0f;

        /// <summary>White balance. Negative is cooler/bluer, positive is warmer/oranger, 0 is neutral.</summary>
        public float Temperature { get; set; } = 0.0f;

        /// <summary>
        /// Blend between the untouched color and the filmic curve. 0 disables
        /// the tonemap only, leaving the other controls working.
        ///
        /// Defaults to 0 — off — on purpose. The ACES curve expects a linear,
        /// scene-referred input, and whether vanilla's final.fsh output is
        /// still linear at the point this mod grades it has NOT been confirmed
        /// against a running game. If it is already display-referred, a curve
        /// applied on top washes the image out, and a mod that looks broken on
        /// first install is worse than one that waits to be switched on. Flip
        /// this to 1.0 once someone has actually looked at it in game, and
        /// update src/ColorGrade/README.md when they have.
        /// </summary>
        public float TonemapStrength { get; set; } = 0.0f;

        internal void ClampToValidRanges(List<string> corrections)
        {
            Exposure = Clamp(Exposure, 0.1f, 4.0f, "ColorGrade.Exposure", corrections);
            Contrast = Clamp(Contrast, 0.0f, 2.0f, "ColorGrade.Contrast", corrections);
            Saturation = Clamp(Saturation, 0.0f, 2.0f, "ColorGrade.Saturation", corrections);
            Temperature = Clamp(Temperature, -1.0f, 1.0f, "ColorGrade.Temperature", corrections);
            TonemapStrength = Clamp(TonemapStrength, 0.0f, 1.0f, "ColorGrade.TonemapStrength", corrections);
        }

        internal static float Clamp(float value, float min, float max, string name, List<string> corrections)
        {
            // NaN fails every comparison, so test for it explicitly rather than
            // letting it slip through and poison the shader uniform.
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                corrections.Add(name + " was not a finite number, reset to " + min.ToString("0.##"));
                return min;
            }

            if (value < min)
            {
                corrections.Add(name + " clamped from " + value.ToString("0.###") + " to " + min.ToString("0.##"));
                return min;
            }

            if (value > max)
            {
                corrections.Add(name + " clamped from " + value.ToString("0.###") + " to " + max.ToString("0.##"));
                return max;
            }

            return value;
        }
    }
}
