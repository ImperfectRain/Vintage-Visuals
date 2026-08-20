using System;
using VintageVisuals.Common;

namespace VintageVisuals.ColorGrade
{
    /// <summary>The grade actually on screen: the player's settings plus whatever the world added.</summary>
    public readonly struct GradeSample
    {
        public readonly float Exposure;
        public readonly float Contrast;
        public readonly float Saturation;
        public readonly float Temperature;
        public readonly float TintR;
        public readonly float TintG;
        public readonly float TintB;

        public GradeSample(float exposure, float contrast, float saturation, float temperature,
                           float tintR, float tintG, float tintB)
        {
            Exposure = exposure;
            Contrast = contrast;
            Saturation = saturation;
            Temperature = temperature;
            TintR = tintR;
            TintG = tintG;
            TintB = tintB;
        }

        /// <summary>Changes nothing. What the shader must see when adaptive grading is off.</summary>
        public static GradeSample Neutral
        {
            get { return new GradeSample(1f, 1f, 1f, 0f, 1f, 1f, 1f); }
        }

        /// <summary>
        /// How far apart two grades are, as one number.
        ///
        /// Used only to decide whether a re-upload is worth doing. Uniform
        /// values are per-program state that survives until the program is
        /// relinked, so a grade that has settled should cost nothing per frame.
        /// </summary>
        public float DistanceTo(GradeSample other)
        {
            return Math.Abs(Exposure - other.Exposure)
                 + Math.Abs(Contrast - other.Contrast)
                 + Math.Abs(Saturation - other.Saturation)
                 + Math.Abs(Temperature - other.Temperature)
                 + Math.Abs(TintR - other.TintR)
                 + Math.Abs(TintG - other.TintG)
                 + Math.Abs(TintB - other.TintB);
        }

        /// <summary>
        /// Eases toward a target on a time constant, frame-rate independently.
        ///
        /// Every influence changes discontinuously somewhere - stepping through
        /// a doorway, a shower starting, the camera going under - and easing is
        /// what turns those into the thing the eye reads as the world changing
        /// rather than as the renderer glitching. One constant for all seven
        /// fields on purpose: they describe one look, and letting saturation
        /// arrive before temperature would pass through grades that are not on
        /// the path between the two.
        /// </summary>
        public GradeSample EaseTo(GradeSample target, float deltaSeconds, float seconds)
        {
            if (deltaSeconds <= 0f) return this;

            float k = 1f - (float)Math.Exp(-deltaSeconds / Math.Max(0.05f, seconds));

            return new GradeSample(
                Exposure + (target.Exposure - Exposure) * k,
                Contrast + (target.Contrast - Contrast) * k,
                Saturation + (target.Saturation - Saturation) * k,
                Temperature + (target.Temperature - Temperature) * k,
                TintR + (target.TintR - TintR) * k,
                TintG + (target.TintG - TintG) * k,
                TintB + (target.TintB - TintB) * k);
        }
    }

    /// <summary>
    /// Turns what the world is doing into what the screen looks like.
    ///
    /// A weighted stack of looks over the player's own settings, which is how
    /// post-process volumes work in every engine that has them: each influence
    /// contributes a DELTA scaled by how much it currently applies, and they
    /// sum. Nothing here replaces the player's grade - a look that overrode it
    /// would make every slider in the config panel a lie the moment it started
    /// raining.
    ///
    /// Every delta is zero at weight zero, so a strength slider at 0 is exactly
    /// the same image as the feature not existing. That is the same rule the
    /// GLSL runs on and it matters for the same reason: this is the subsystem
    /// that grades the entire frame, so "off" has to mean off.
    ///
    /// Pure and free of game types so the rules can be driven through every
    /// combination in tools/smoketest.
    /// </summary>
    public static class GradeStack
    {
        /// <summary>Degrees C the biome influence treats as neither hot nor cold.</summary>
        public const float TemperateCelsius = 8f;

        /// <summary>Degrees C either side of temperate that reaches full weight.</summary>
        private const float ClimateSpanCelsius = 24f;

        /// <summary>Climate rainfall that is neither arid nor lush.</summary>
        private const float TemperateRainfall = 0.5f;

        /// <summary>
        /// Evaluates the stack.
        /// </summary>
        /// <param name="basis">The player's own settings, untouched by anything here.</param>
        public static GradeSample Evaluate(GradeSample basis, GradeContext ctx, AdaptiveGradeConfig weights)
        {
            if (weights == null || !weights.Enabled) return basis;

            var stack = new Accumulator(basis);

            float sky = Clamp01(ctx.SkyExposure);
            float dayLight = Clamp01(ctx.DayLight);

            // --- Time of day ------------------------------------------------
            //
            // Two looks, not one. The interesting part of a day is not that it
            // gets darker, which the game already does: it is that the light
            // changes colour twice on the way.

            // Golden hour. Peaks where daylight is partway up, which is where
            // the sun is near the horizon, rather than needing a sun vector -
            // one less piece of the API to be wrong about. Gated on sky
            // exposure because a cellar does not have a sunset.
            float golden = Band(dayLight, 0.05f, 0.35f, 0.45f, 0.75f);
            stack.Add(golden * weights.TimeOfDayStrength * sky,
                      1.00f, -0.06f, +0.12f, +0.30f, 1.03f, 1.00f, 0.96f);

            // Night. The Purkinje shift: at scotopic levels the eye's cones
            // stop contributing, so colour drains away and sensitivity moves
            // toward blue. Every night scene ever graded leans on this, and
            // getting it backwards - warm night - is what makes a night look
            // like a badly exposed day.
            stack.Add((1f - dayLight) * weights.TimeOfDayStrength,
                      1.04f, -0.10f, -0.45f, -0.28f, 0.96f, 0.99f, 1.06f);

            // --- Weather ----------------------------------------------------
            //
            // Both gated on sky exposure: rain does not change the colour of a
            // room with no windows.

            stack.Add(Clamp01(ctx.Rain) * weights.WeatherStrength * sky,
                      0.93f, -0.14f, -0.28f, -0.16f, 0.98f, 1.00f, 1.03f);

            stack.Add(Clamp01(ctx.CloudCover) * weights.WeatherStrength * sky,
                      0.97f, -0.10f, -0.12f, -0.08f, 1.00f, 1.00f, 1.00f);

            // --- Where the player is ----------------------------------------

            // Enclosed. Lit by fire rather than by sky, so warmer, and with the
            // sky's fill light gone the shadows go harder.
            stack.Add((1f - sky) * weights.IndoorStrength,
                      1.06f, +0.04f, -0.06f, +0.14f, 1.02f, 1.00f, 0.97f);

            // Underground. Stacks with enclosed rather than replacing it: a
            // cave is both, and the deep part of it is the part that should
            // drain the colour rather than merely warm it.
            stack.Add(Clamp01(ctx.Depth) * weights.DepthStrength,
                      1.05f, +0.06f, -0.22f, -0.10f, 0.98f, 0.99f, 1.02f);

            // Submerged. Water absorbs red first and blue last, which is why
            // everything below a few metres goes blue-green, and why this is
            // the one influence with a tint strong enough to see on its own.
            stack.Add(Clamp01(ctx.Underwater) * weights.UnderwaterStrength,
                      0.95f, -0.10f, -0.18f, -0.30f, 0.86f, 0.98f, 1.08f);

            // --- Biome ------------------------------------------------------
            //
            // Four one-sided influences rather than two signed ones, because
            // hot and cold are not each other's negative: a desert reads as
            // glare and haze, and tundra reads as colour draining out. Sharing
            // one slider keeps that as a single idea in the config panel.
            //
            // All four gated on sky exposure, for the same reason as weather -
            // the inside of a building is the same colour in every biome.

            float heat = Clamp((ctx.Temperature - TemperateCelsius) / ClimateSpanCelsius, -1f, 1f);
            float humidity = Clamp((ctx.Rainfall - TemperateRainfall) / TemperateRainfall, -1f, 1f);

            float biome = weights.BiomeStrength * sky;

            stack.Add(Math.Max(heat, 0f) * biome,
                      1.00f, +0.06f, -0.06f, +0.22f, 1.03f, 1.00f, 0.96f);

            stack.Add(Math.Max(-heat, 0f) * biome,
                      1.00f, -0.02f, -0.16f, -0.22f, 0.97f, 0.99f, 1.05f);

            stack.Add(Math.Max(humidity, 0f) * biome,
                      1.00f, +0.02f, +0.14f, 0.00f, 0.99f, 1.03f, 0.99f);

            stack.Add(Math.Max(-humidity, 0f) * biome,
                      1.00f, 0.00f, -0.10f, +0.06f, 1.02f, 1.00f, 0.97f);

            return stack.Resolve();
        }

        /// <summary>
        /// Rises across [inLow, inHigh] and falls across [outHigh, outLow].
        ///
        /// Written as two smoothsteps rather than as a curve so the four
        /// numbers in the call site say where the band is, which is the only
        /// thing anyone reading it wants to know.
        /// </summary>
        private static float Band(float x, float inLow, float inHigh, float outHigh, float outLow)
        {
            return SmoothStep(inLow, inHigh, x) * (1f - SmoothStep(outHigh, outLow, x));
        }

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            if (edge0 == edge1) return x < edge0 ? 0f : 1f;

            float t = Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        private static float Clamp01(float v)
        {
            return Clamp(v, 0f, 1f);
        }

        private static float Clamp(float v, float min, float max)
        {
            // NaN fails every comparison, so it is tested for rather than
            // clamped: an influence that went non-finite would otherwise reach
            // the uniform and take the whole screen with it.
            if (float.IsNaN(v)) return min;
            return v < min ? min : (v > max ? max : v);
        }

        /// <summary>
        /// Sums the stack.
        ///
        /// Exposure and tint multiply; contrast, saturation and temperature
        /// add. That split is not arbitrary. Exposure is a gain and tint is a
        /// per-channel gain, so two influences that each halve the light should
        /// quarter it rather than remove it twice; contrast and saturation are
        /// distances from a pivot, and two influences that each flatten the
        /// image should flatten it further rather than compound toward grey
        /// faster than either asked for.
        /// </summary>
        private struct Accumulator
        {
            private float _exposure;
            private float _contrast;
            private float _saturation;
            private float _temperature;
            private float _tintR;
            private float _tintG;
            private float _tintB;

            public Accumulator(GradeSample basis)
            {
                _exposure = basis.Exposure;
                _contrast = basis.Contrast;
                _saturation = basis.Saturation;
                _temperature = basis.Temperature;
                _tintR = basis.TintR;
                _tintG = basis.TintG;
                _tintB = basis.TintB;
            }

            public void Add(float weight, float exposure, float contrast, float saturation,
                            float temperature, float tintR, float tintG, float tintB)
            {
                float w = Clamp01(weight);
                if (w <= 0f) return;

                _exposure *= 1f + w * (exposure - 1f);
                _contrast += w * contrast;
                _saturation += w * saturation;
                _temperature += w * temperature;
                _tintR *= 1f + w * (tintR - 1f);
                _tintG *= 1f + w * (tintG - 1f);
                _tintB *= 1f + w * (tintB - 1f);
            }

            /// <summary>
            /// Clamped to the same ranges the config clamps its own values to,
            /// so a stack of influences can never reach a grade the player
            /// could not have dialled in by hand. Without this, enough
            /// influences agreeing would drive saturation negative, which
            /// inverts every colour on screen.
            /// </summary>
            public GradeSample Resolve()
            {
                return new GradeSample(
                    Clamp(_exposure, 0.1f, 4.0f),
                    Clamp(_contrast, 0.0f, 2.0f),
                    Clamp(_saturation, 0.0f, 2.0f),
                    Clamp(_temperature, -1.0f, 1.0f),
                    Clamp(_tintR, 0.5f, 1.5f),
                    Clamp(_tintG, 0.5f, 1.5f),
                    Clamp(_tintB, 0.5f, 1.5f));
            }
        }
    }
}
