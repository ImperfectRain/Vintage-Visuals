using System;

namespace VintageVisuals.ColorGrade
{
    /// <summary>
    /// Eye adaptation: an exposure multiplier that eases toward how bright the
    /// player's surroundings are, so stepping out of a cave briefly blows out
    /// and stepping into one briefly goes dark before the eye catches up.
    ///
    /// Driven by the game's light level at the player, NOT by measuring the
    /// rendered frame. Measuring the frame is the textbook approach, but it
    /// needs a luminance reduction — a mip chain or a downsample pass — and
    /// then a GPU-to-CPU readback to smooth it over time. That is a render
    /// pipeline of its own, and this mod deliberately owns no framebuffers.
    /// The light level at the player's head is a direct measure of the same
    /// thing the player's eye would be adapting to, costs one block lookup,
    /// and needs no readback.
    ///
    /// The trade-off is honest and worth stating: this reacts to *where the
    /// player is*, not to what is on screen. Staring at a bright sky from
    /// inside a dark room will not stop it down, because the room is what the
    /// light level reports. For cave-to-surface transitions — the case players
    /// actually notice — the two agree.
    ///
    /// Pure logic, no game types: everything here is exercised by
    /// tools/smoketest without a running client.
    /// </summary>
    public sealed class AdaptiveExposure
    {
        /// <summary>
        /// Vintage Story light levels run 0..31. Used only to normalise the
        /// raw level into 0..1; a game change here shifts the curve, it does
        /// not break anything.
        /// </summary>
        public const int MaxGameLightLevel = 31;

        /// <summary>Current multiplier. Starts neutral so the first frame is never wrong.</summary>
        public float Current { get; private set; } = 1.0f;

        /// <summary>Resets to neutral, e.g. when the subsystem is disabled.</summary>
        public void Reset()
        {
            Current = 1.0f;
        }

        /// <summary>Maps a raw game light level to 0 (pitch dark) .. 1 (full light).</summary>
        public static float NormaliseLightLevel(int lightLevel)
        {
            float normalised = lightLevel / (float)MaxGameLightLevel;
            return normalised < 0f ? 0f : (normalised > 1f ? 1f : normalised);
        }

        /// <summary>
        /// The multiplier this brightness should settle at.
        ///
        /// Linear in normalised light rather than logarithmic. Real eye
        /// response is logarithmic, but the input is already a quantised 0..31
        /// game value rather than a physical luminance, so a log curve would be
        /// false precision on top of an approximation — and it would spend most
        /// of its resolution on the darkest few levels, where this is least
        /// able to tell one from another.
        /// </summary>
        public static float TargetFor(float normalisedLight, float darkGain, float brightGain)
        {
            if (normalisedLight < 0f) normalisedLight = 0f;
            else if (normalisedLight > 1f) normalisedLight = 1f;

            return brightGain + (darkGain - brightGain) * (1f - normalisedLight);
        }

        /// <summary>
        /// Eases <see cref="Current"/> toward <paramref name="target"/>.
        ///
        /// Exponential smoothing on a time constant, not a fixed step per call,
        /// so the speed is the same whether this ticks at 10 Hz or 60 and does
        /// not change with frame rate.
        ///
        /// Brightening and darkening have separate time constants because human
        /// adaptation is famously asymmetric: adapting to bright light takes
        /// seconds, adapting to darkness takes minutes. Matching that asymmetry
        /// is most of what makes the effect read as an eye rather than as a
        /// fade.
        /// </summary>
        /// <param name="deltaSeconds">Elapsed time. Non-positive is ignored.</param>
        /// <param name="brightenSeconds">Time constant while the multiplier rises (going dark).</param>
        /// <param name="darkenSeconds">Time constant while it falls (going bright).</param>
        public float Step(float target, float deltaSeconds, float brightenSeconds, float darkenSeconds)
        {
            if (deltaSeconds <= 0f || float.IsNaN(deltaSeconds)) return Current;
            if (float.IsNaN(target)) return Current;

            float tau = target > Current ? brightenSeconds : darkenSeconds;

            // A zero or negative time constant means "no smoothing"; snapping is
            // the honest interpretation and avoids a divide by zero.
            if (tau <= 0f)
            {
                Current = target;
                return Current;
            }

            float blend = 1f - (float)Math.Exp(-deltaSeconds / tau);
            Current += (target - Current) * blend;

            // Land exactly on the target once within float noise, so Current
            // stops changing and the uniform upload can be skipped.
            if (Math.Abs(target - Current) < 1e-4f) Current = target;

            return Current;
        }
    }
}
