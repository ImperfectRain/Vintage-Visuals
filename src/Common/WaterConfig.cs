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

        /// <summary>
        /// Sunlight the waves focus onto the bed under open water, in moving
        /// lines that follow the game's sun colour, shadows and cloud cover. 0 is off.
        /// </summary>
        public float Caustics { get; set; } = 0.6f;

        /// <summary>
        /// Softness of the caustic web (D93): 0 the sharp focus lines, 1 the
        /// soft glow of a sunlit lake. Blurs on the bed's texel grid.
        /// </summary>
        public float CausticBlur { get; set; } = 0.35f;

        /// <summary>Extra softness where the focused light is brightest, so lines bloom into a halo. 0 none.</summary>
        public float CausticGlow { get; set; } = 0.3f;

        /// <summary>Colour split at the focus lines (water bends blue more than red). 0 none.</summary>
        public float CausticDispersion { get; set; } = 0.25f;

        /// <summary>Whether the caustic code is compiled at all (patch group "watercaustics").</summary>
        public bool CausticsCompiled { get { return Caustics > 0.001f; } }

        /// <summary>
        /// Shorelines: the water thins out where it is only a sliver deep, and a
        /// pixel foam line laps in and out along the bank. 0 is off (vanilla's own foam).
        /// </summary>
        public float Shore { get; set; } = 0.7f;

        /// <summary>
        /// The world in the water: banks, trees and the far shore, found by
        /// marching each water texel's reflected ray across the finished
        /// opaque frame (patch group "waterscene", D82). Gates the PATCH, so
        /// off declares no sampler and the water reflects the sky alone.
        /// Needs no scene capture: the water reads the live frame.
        /// </summary>
        public bool SceneReflections { get; set; } = true;

        /// <summary>
        /// How far the bed is bent through the waves, seen from above. Needs
        /// SceneReflections (the same group carries it). 0 is the straight view.
        /// </summary>
        public float Refraction { get; set; } = 0.5f;

        /// <summary>Debug views: 1 normal, 2 sky image, 3 depth behind, 4 reflected share, 5 glints, 6 texel grid, 7 caustics, 8 foam (red), flow (green), far swell (blue), 9 the world the reflected ray found.</summary>
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
            Caustics = ColorGradeConfig.Clamp(Caustics, 0f, 1f, "Water.Caustics", corrections);
            CausticBlur = ColorGradeConfig.Clamp(CausticBlur, 0f, 1f, "Water.CausticBlur", corrections);
            CausticGlow = ColorGradeConfig.Clamp(CausticGlow, 0f, 1f, "Water.CausticGlow", corrections);
            CausticDispersion = ColorGradeConfig.Clamp(CausticDispersion, 0f, 1f, "Water.CausticDispersion", corrections);
            Shore = ColorGradeConfig.Clamp(Shore, 0f, 1f, "Water.Shore", corrections);
            Refraction = ColorGradeConfig.Clamp(Refraction, 0f, 1f, "Water.Refraction", corrections);
            DebugView = ColorGradeConfig.Clamp(DebugView, 0f, 9f, "Water.DebugView", corrections);
        }
    }
}
