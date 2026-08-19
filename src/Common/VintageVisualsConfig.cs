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
            return corrections;
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
        /// Global multiplier on the specular highlight, on top of the
        /// per-material ceiling already baked into the atlas.
        /// </summary>
        public float SpecularStrength { get; set; } = 1.0f;

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
            DebugView = ColorGradeConfig.Clamp(DebugView, 0.0f, 6.0f,
                "PseudoPBR.DebugView", corrections);
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
