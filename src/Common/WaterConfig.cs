using System.Collections.Generic;

namespace VintageVisuals.Common
{
    /// <summary>
    /// The water surface on vanilla's liquid program (patch group "water").
    /// Every strength is zero-is-off; Enabled false compiles vanilla's liquid
    /// source with no patch at all.
    /// </summary>
    public class WaterConfig
    {
        /// <summary>Gates the patch group: off is vanilla's chunkliquid source.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Texel-locked reflection of the sky and horizon, by Fresnel.</summary>
        public float SkyReflection { get; set; } = 0.75f;

        /// <summary>
        /// A subtle blur of the reflection in TEXEL space: each texel stays one
        /// colour, neighbouring texels differ by less. 0 is the sharp pixel image.
        /// </summary>
        public float ReflectionSoftness { get; set; } = 0.35f;

        /// <summary>How much the texel waves tilt the reflection and glints.</summary>
        public float Waves { get; set; } = 1.0f;

        /// <summary>Colour and opacity gained with the depth of water behind the surface.</summary>
        public float DepthTint { get; set; } = 0.6f;

        /// <summary>How clear shallow water is (less opaque under about a block of depth).</summary>
        public float ShallowClarity { get; set; } = 0.4f;

        /// <summary>Rain ripples on open water, driven by rain actually falling.</summary>
        public float RainRipples { get; set; } = 0.8f;

        /// <summary>Sun and moon highlights on the texel waves.</summary>
        public float Glints { get; set; } = 0.7f;

        /// <summary>Debug views: 1 normal, 2 sky image, 3 depth behind, 4 reflected share, 5 glints, 6 texel grid.</summary>
        public float DebugView { get; set; } = 0f;

        public void ClampToValidRanges(List<string> corrections)
        {
            SkyReflection = ColorGradeConfig.Clamp(SkyReflection, 0f, 1f, "Water.SkyReflection", corrections);
            ReflectionSoftness = ColorGradeConfig.Clamp(ReflectionSoftness, 0f, 1f, "Water.ReflectionSoftness", corrections);
            Waves = ColorGradeConfig.Clamp(Waves, 0f, 2f, "Water.Waves", corrections);
            DepthTint = ColorGradeConfig.Clamp(DepthTint, 0f, 1f, "Water.DepthTint", corrections);
            ShallowClarity = ColorGradeConfig.Clamp(ShallowClarity, 0f, 1f, "Water.ShallowClarity", corrections);
            RainRipples = ColorGradeConfig.Clamp(RainRipples, 0f, 1f, "Water.RainRipples", corrections);
            Glints = ColorGradeConfig.Clamp(Glints, 0f, 1f, "Water.Glints", corrections);
            DebugView = ColorGradeConfig.Clamp(DebugView, 0f, 6f, "Water.DebugView", corrections);
        }
    }
}
