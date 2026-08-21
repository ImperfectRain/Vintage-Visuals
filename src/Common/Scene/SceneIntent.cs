using System;
using System.Collections.Generic;

namespace VintageVisuals.Common.Scene
{
    /// <summary>
    /// What the scene needs, as opposed to what the scene IS.
    ///
    /// <see cref="EnvironmentState"/> reports facts: it is raining this hard, the
    /// player is this far underground, the cloud cover is this. An intent
    /// channel is a normalised 0..1 answer to a question an effect actually
    /// asks - how much does this scene need help being legible, how much air
    /// belongs in it, how much should the mod hold back right now.
    ///
    /// The layer exists because the alternative was already starting. Five
    /// subsystems each re-derived their own reading of the same facts, and at
    /// ten they would have drifted: two of them would decide "wet" meant
    /// different things and nothing would say so. One vocabulary, built once
    /// per tick, read by everyone.
    /// </summary>
    public enum IntentChannel
    {
        /// <summary>How much this scene needs help being legible. Drives floors, not looks.</summary>
        Readability,

        /// <summary>How much air belongs in the scene - haze, damp, dust.</summary>
        Atmosphere,

        /// <summary>Rain pressure: what is falling now.</summary>
        Wetness,

        /// <summary>Snow, ice and freezing air.</summary>
        Cold,

        /// <summary>Desert glare and hot dry air.</summary>
        Heat,

        /// <summary>Overcast and storm darkness. Not night.</summary>
        Gloom,

        /// <summary>Night, as a context rather than a brightness.</summary>
        Night,

        /// <summary>Torches, lava, forges - local warm light doing the lighting.</summary>
        ArtificialLight,

        /// <summary>How boxed in the player is: indoors, underground, under canopy.</summary>
        Enclosure,

        /// <summary>
        /// How much the mod should hold back.
        ///
        /// The channel that outranks the rest. It rises when the scene is
        /// already hard to read - deep underground, at night, in a storm - and
        /// every effect that removes light, colour or contrast is scaled down
        /// by it. A visual overhaul that makes a cave unnavigable has not
        /// improved the game.
        /// </summary>
        Restraint,
    }

    /// <summary>
    /// One influence's push on one channel, kept so the result can be explained.
    ///
    /// This is the part that is easy to skip and expensive to skip. When a grade
    /// saturates or a scene comes out too dark, the question is always WHICH of
    /// nine influences did it, and a bare float cannot answer. Every push
    /// records where it came from, why, and whether it had to be trimmed.
    /// </summary>
    public readonly struct IntentContribution
    {
        public readonly string Source;
        public readonly IntentChannel Channel;
        public readonly float Amount;
        public readonly string Reason;

        /// <summary>True when the per-contribution cap trimmed this push.</summary>
        public readonly bool Capped;

        public IntentContribution(string source, IntentChannel channel, float amount, string reason, bool capped)
        {
            Source = source;
            Channel = channel;
            Amount = amount;
            Reason = reason;
            Capped = capped;
        }

        public override string ToString()
        {
            return Source + " -> " + Channel + " " + Amount.ToString("+0.00;-0.00") +
                   (Capped ? " (capped)" : "") + " [" + Reason + "]";
        }
    }

    /// <summary>
    /// The channel values for one tick, plus the record of how they got there.
    /// </summary>
    public sealed class SceneIntent
    {
        /// <summary>
        /// The most an INDIRECT influence may push a channel.
        ///
        /// The distinction matters, and the first version of this got it wrong
        /// by capping everything. Rain pushing Wetness is not an influence on
        /// that channel, it IS the channel restated - capping it meant a
        /// downpour could only ever ask for a third of the wetness it plainly
        /// had, and two smoke checks caught it.
        ///
        /// What the cap is for is the indirect pushes: rain also thickening the
        /// air, cloud also arguing for restraint. Without a bound, one badly
        /// tuned side effect can own a channel outright and every other input
        /// becomes decoration. The cap makes those a vote rather than a veto.
        ///
        /// Direct restatements pass <see cref="Uncapped"/>. When user-authored
        /// tuning rows exist they should get a much smaller cap again - Dalashade
        /// uses 0.20 for exactly that case - because a user row is the least
        /// trustworthy of the three kinds of input.
        /// </summary>
        public const float ContributionCap = 0.35f;

        /// <summary>For a push that restates a fact rather than being a side effect of one.</summary>
        public const float Uncapped = 1f;

        private readonly float[] _values = new float[Enum.GetValues(typeof(IntentChannel)).Length];
        private readonly List<IntentContribution> _contributions = new List<IntentContribution>();

        public IReadOnlyList<IntentContribution> Contributions { get { return _contributions; } }

        public float this[IntentChannel channel]
        {
            get { return _values[(int)channel]; }
        }

        /// <summary>
        /// Adds one influence's push, trimmed to the cap and recorded either way.
        ///
        /// Pushes below a thousandth are dropped rather than recorded: an
        /// influence that is off should leave no trace at all, or the diagnostic
        /// fills with noise and stops being read.
        /// </summary>
        public void Add(string source, IntentChannel channel, float amount, string reason,
                        float cap = ContributionCap)
        {
            if (float.IsNaN(amount) || Math.Abs(amount) < 0.001f) return;

            float capped = Math.Max(-cap, Math.Min(cap, amount));

            _contributions.Add(new IntentContribution(source, channel, capped, reason,
                                                      Math.Abs(capped - amount) > 1e-4f));

            _values[(int)channel] = Math.Max(0f, Math.Min(1f, _values[(int)channel] + capped));
        }

        /// <summary>Everything that pushed one channel, for a log line or a report.</summary>
        public IEnumerable<IntentContribution> For(IntentChannel channel)
        {
            foreach (IntentContribution c in _contributions)
            {
                if (c.Channel == channel) yield return c;
            }
        }

        /// <summary>A one-line summary of the channels that are actually doing something.</summary>
        public string Describe()
        {
            var text = new System.Text.StringBuilder();

            foreach (IntentChannel channel in Enum.GetValues(typeof(IntentChannel)))
            {
                float value = _values[(int)channel];
                if (value < 0.02f) continue;

                if (text.Length > 0) text.Append(", ");
                text.Append(channel).Append(' ').Append(value.ToString("0.00"));
            }

            return text.Length == 0 ? "(nothing active)" : text.ToString();
        }
    }
}
