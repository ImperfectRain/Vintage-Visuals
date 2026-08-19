using System;

namespace VintageVisuals.Weather
{
    /// <summary>
    /// How wet the world currently looks, eased over time.
    ///
    /// Deliberately free of every game type, like <c>AdaptiveExposure</c>, so
    /// the behaviour that matters - how fast surfaces wet and how slowly they
    /// dry - can be checked without a client. Rain that snaps on and off is the
    /// single most obvious way a wetness effect gives itself away.
    /// </summary>
    public sealed class WetnessTracker
    {
        /// <summary>
        /// Below this, precipitation falls as snow rather than rain.
        ///
        /// Matches the temperature the game itself uses to decide, so a
        /// snowstorm does not make the ground look rained on - which would be
        /// the wrong effect at exactly the moment the player is most likely to
        /// be looking at the sky.
        /// </summary>
        public const float FreezingTemperature = 0.0f;

        /// <summary>
        /// Rainfall below this is drizzle the game reports but nobody sees, so
        /// treating it as rain leaves surfaces permanently faintly wet.
        /// </summary>
        public const float RainfallThreshold = 0.04f;

        /// <summary>Seconds to reach full wetness in steady rain. Fast: rain wets a surface quickly.</summary>
        public const float WettingSeconds = 8.0f;

        public float Current { get; private set; }

        /// <summary>
        /// Wetness that steady rain at this intensity would eventually reach.
        ///
        /// Not linear in rainfall. Light rain still darkens and glosses a
        /// surface almost as much as heavy rain does - what heavy rain adds is
        /// runoff and puddles, which this does not model - so the curve rises
        /// steeply and then flattens.
        /// </summary>
        public static float TargetFor(float rainfall, float temperature)
        {
            if (float.IsNaN(rainfall) || float.IsNaN(temperature)) return 0f;
            if (temperature <= FreezingTemperature) return 0f;
            if (rainfall <= RainfallThreshold) return 0f;

            float normalised = Clamp01((rainfall - RainfallThreshold) / (1f - RainfallThreshold));

            return (float)Math.Sqrt(normalised);
        }

        /// <summary>
        /// Advances toward <paramref name="target"/>.
        ///
        /// Asymmetric on purpose, and the asymmetry is the whole effect: a
        /// surface wets in seconds and dries over a minute or more. Symmetric
        /// easing reads as a fade rather than as weather.
        ///
        /// Exponential smoothing on a time constant, so the result does not
        /// depend on how often this is called.
        /// </summary>
        public float Step(float target, float deltaSeconds, float dryingSeconds)
        {
            if (float.IsNaN(target) || float.IsNaN(deltaSeconds) || deltaSeconds <= 0f) return Current;

            target = Clamp01(target);

            float tau = target > Current ? WettingSeconds : Math.Max(0f, dryingSeconds);

            if (tau <= 0f)
            {
                Current = target;
                return Current;
            }

            float alpha = 1f - (float)Math.Exp(-deltaSeconds / tau);
            Current = Clamp01(Current + (target - Current) * Clamp01(alpha));

            return Current;
        }

        public void Reset()
        {
            Current = 0f;
        }

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value)) return 0f;
            return value < 0f ? 0f : (value > 1f ? 1f : value);
        }
    }
}
