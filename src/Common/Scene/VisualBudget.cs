using System;
using System.Collections.Generic;

namespace VintageVisuals.Common.Scene
{
    /// <summary>What a subsystem is competing for. One role, several claimants.</summary>
    public enum VisualRole
    {
        /// <summary>How much light leaves the scene.</summary>
        SceneLight,

        /// <summary>How much colour leaves the scene.</summary>
        Saturation,

        /// <summary>How much tonal separation leaves the scene.</summary>
        Contrast,

        /// <summary>How much air goes between the camera and the world.</summary>
        Haze,
    }

    /// <summary>
    /// Stops several subsystems doing the same thing to the same pixel.
    ///
    /// This is a defect report before it is a design. In heavy rain, this mod
    /// currently: drops saturation and contrast through adaptive grading,
    /// weakens the direct lobe and lifts ambient through the overcast term, and
    /// washes out distance through rain fog. Three systems, one direction, no
    /// budget between them. Each was tuned alone and each is reasonable alone,
    /// and together they are a grey screen.
    ///
    /// The fix borrows two ideas at once from Dalashade and merges them, because
    /// separately they are each half a mechanism:
    ///
    ///  - a BUDGET per visual role, so the total amount of colour that may be
    ///    removed is fixed no matter how many subsystems want to remove some;
    ///  - an AUTHORITY per role, so the subsystem that owns it gets what it asks
    ///    for and the others are dampened rather than switched off. Dampening
    ///    beats disabling: a secondary that goes to zero is a feature that
    ///    silently stops working, and nobody ever finds out why.
    ///
    /// Claims are recorded, so "the world went grey" resolves to a list rather
    /// than to another round of guessing.
    /// </summary>
    public sealed class VisualBudget
    {
        /// <summary>
        /// The most any one role may give up in total, before restraint.
        ///
        /// Not 1.0. A scene that has had all of its colour or all of its
        /// contrast removed is not a look, it is a bug, and no combination of
        /// weather should be able to reach it.
        /// </summary>
        public const float RoleAllowance = 0.75f;

        /// <summary>What a claim is scaled by when its owner is not the authority for that role.</summary>
        public const float SecondaryDamping = 0.5f;

        public readonly struct Claim
        {
            public readonly string Source;
            public readonly VisualRole Role;
            public readonly float Wanted;
            public readonly float Granted;
            public readonly bool Primary;

            public Claim(string source, VisualRole role, float wanted, float granted, bool primary)
            {
                Source = source;
                Role = role;
                Wanted = wanted;
                Granted = granted;
                Primary = primary;
            }

            /// <summary>True when the claimant asked for meaningfully more than it got.</summary>
            public bool Trimmed { get { return Wanted - Granted > 0.01f; } }

            public override string ToString()
            {
                return Source + " -> " + Role + " " + Granted.ToString("0.00") + " of " +
                       Wanted.ToString("0.00") + (Primary ? " (owner)" : " (secondary)") +
                       (Trimmed ? " TRIMMED" : "");
            }
        }

        /// <summary>
        /// Who owns each role.
        ///
        /// Assigned by what the role most nearly IS rather than by seniority.
        /// Colour grading owns saturation and contrast because that is its whole
        /// job and the player's own sliders live there. Weather owns haze,
        /// because fog is weather. Nobody owns scene light outright - it is the
        /// one every subsystem legitimately touches - so every claimant on it is
        /// dampened, which is the honest answer rather than picking a winner.
        /// </summary>
        private static readonly Dictionary<VisualRole, string> Owners = new Dictionary<VisualRole, string>
        {
            { VisualRole.Saturation, "colorgrade" },
            { VisualRole.Contrast, "colorgrade" },
            { VisualRole.Haze, "weather" },
        };

        private readonly float[] _spent = new float[Enum.GetValues(typeof(VisualRole)).Length];
        private readonly List<Claim> _claims = new List<Claim>();
        private readonly float _restraint;
        private readonly float _readability;

        /// <param name="restraint">
        /// From <see cref="IntentChannel.Restraint"/>. Shrinks every allowance,
        /// so a scene that is already hard to read gives up less of what little
        /// it has.
        /// </param>
        /// <param name="readability">
        /// From <see cref="IntentChannel.Readability"/>. Shrinks the two roles
        /// that make a scene unreadable specifically - light and contrast -
        /// harder than the rest.
        /// </param>
        public VisualBudget(float restraint, float readability)
        {
            _restraint = Clamp01(restraint);
            _readability = Clamp01(readability);
        }

        public IReadOnlyList<Claim> Claims { get { return _claims; } }

        /// <summary>How much of a role remains unclaimed.</summary>
        public float Remaining(VisualRole role)
        {
            return Math.Max(0f, AllowanceFor(role) - _spent[(int)role]);
        }

        /// <summary>
        /// Asks for some of a role and returns what may actually be taken.
        ///
        /// Callers must use the RETURN VALUE rather than what they asked for.
        /// That is the whole contract, and it is the one thing a reviewer should
        /// check at every call site.
        /// </summary>
        public float Request(string source, VisualRole role, float wanted)
        {
            if (float.IsNaN(wanted) || wanted <= 0f) return 0f;

            bool primary = Owners.TryGetValue(role, out string owner) && owner == source;

            float scaled = primary ? wanted : wanted * SecondaryDamping;
            float granted = Math.Min(scaled, Remaining(role));

            _spent[(int)role] += granted;
            _claims.Add(new Claim(source, role, wanted, granted, primary));

            return granted;
        }

        /// <summary>
        /// The allowance for a role after restraint and readability have taken
        /// their cut.
        ///
        /// Light and contrast are cut by readability as well as restraint,
        /// because those two are what actually stop a player seeing. Saturation
        /// and haze can be wrong without being dangerous.
        /// </summary>
        private float AllowanceFor(VisualRole role)
        {
            float allowance = RoleAllowance * (1f - _restraint * 0.6f);

            if (role == VisualRole.SceneLight || role == VisualRole.Contrast)
            {
                allowance *= 1f - _readability * 0.5f;
            }

            return Math.Max(0f, allowance);
        }

        /// <summary>A one-line summary of who took what, for the log.</summary>
        public string Describe()
        {
            var text = new System.Text.StringBuilder();

            foreach (Claim claim in _claims)
            {
                if (claim.Granted < 0.005f && !claim.Trimmed) continue;

                if (text.Length > 0) text.Append("; ");
                text.Append(claim);
            }

            return text.Length == 0 ? "(nothing claimed)" : text.ToString();
        }

        private static float Clamp01(float v)
        {
            if (float.IsNaN(v)) return 0f;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }
}
