using System;
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
            return corrections;
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

        private static float Clamp(float value, float min, float max, string name, List<string> corrections)
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
