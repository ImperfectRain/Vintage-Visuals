using System;

namespace VintageVisuals.Common.Scene
{
    /// <summary>
    /// A named look the adaptive stack aims at.
    ///
    /// Dalashade solves this by pointing at a folder of reference images and
    /// biasing the generated preset toward their measured statistics. That is
    /// the better interface and it is not reachable here: it needs a decoded
    /// image, and - more importantly - it is open loop without a framebuffer
    /// readback to compare the result against. Aiming at a measured target with
    /// no way to measure the output is guesswork layered on guesswork.
    ///
    /// What IS reachable is the useful part underneath: a target the whole
    /// adaptive stack leans toward, expressed in the same four numbers the
    /// grading already speaks. A style is not a preset that replaces the
    /// player's settings; it is one more contributor, capped like the others, so
    /// "Muted" and a hand-dialled saturation of 1.3 produce something between
    /// them rather than a fight.
    /// </summary>
    public enum StyleKind
    {
        /// <summary>No target. The player's own settings and the world, nothing else.</summary>
        None,

        /// <summary>Lifted blacks, gentle contrast, slightly desaturated. Film rather than photograph.</summary>
        Filmic,

        /// <summary>Colour pulled back and contrast eased. Closer to the game's own palette than to a shader pack.</summary>
        Muted,

        /// <summary>More colour and more separation, without the crushed blacks that usually come with it.</summary>
        Vivid,

        /// <summary>Cool and clean. Northern light.</summary>
        Cold,

        /// <summary>Warm and slightly hazy. Late afternoon.</summary>
        Warm,
    }

    /// <summary>
    /// The offsets a style asks for, in the grading's own terms.
    ///
    /// Exposure is a multiplier because it is a gain; the rest are additive
    /// because they are distances from a pivot. That is the same split the
    /// adaptive stack already uses, and mixing it up is how two influences that
    /// each flatten an image end up racing each other to grey.
    /// </summary>
    public readonly struct StyleOffsets
    {
        public readonly float Exposure;
        public readonly float Contrast;
        public readonly float Saturation;
        public readonly float Temperature;

        public StyleOffsets(float exposure, float contrast, float saturation, float temperature)
        {
            Exposure = exposure;
            Contrast = contrast;
            Saturation = saturation;
            Temperature = temperature;
        }

        public static StyleOffsets None
        {
            get { return new StyleOffsets(1f, 0f, 0f, 0f); }
        }

        /// <summary>
        /// What each style asks for at full strength.
        ///
        /// Deliberately mild. These are targets the whole stack leans toward on
        /// top of everything the world is already doing, and a style with the
        /// force of a preset would flatten every weather and biome response
        /// underneath it - which is the failure mode of most shader packs and
        /// the thing this project's stated goal rules out.
        /// </summary>
        public static StyleOffsets For(StyleKind kind)
        {
            switch (kind)
            {
                case StyleKind.Filmic: return new StyleOffsets(1.00f, -0.06f, -0.08f, +0.04f);
                case StyleKind.Muted:  return new StyleOffsets(0.98f, -0.10f, -0.20f, 0.00f);
                case StyleKind.Vivid:  return new StyleOffsets(1.02f, +0.10f, +0.20f, 0.00f);
                case StyleKind.Cold:   return new StyleOffsets(1.00f, +0.04f, -0.06f, -0.28f);
                case StyleKind.Warm:   return new StyleOffsets(1.00f, -0.04f, +0.06f, +0.28f);
                default:               return None;
            }
        }

        /// <summary>Parses a config string, falling back to None rather than throwing.</summary>
        public static StyleKind Parse(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return StyleKind.None;

            foreach (StyleKind kind in Enum.GetValues(typeof(StyleKind)))
            {
                if (string.Equals(kind.ToString(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return kind;
                }
            }

            return StyleKind.None;
        }
    }
}
