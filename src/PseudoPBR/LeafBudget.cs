using System;
using VintageVisuals.Common.Diagnostics;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// D78. How far falling leaves reach, how many may fly, and how the spawn
    /// thins with distance and behind the camera. Pure, so tools/smoketest can
    /// drive it.
    ///
    /// The v15 spawner tried a 40 x 40 square (reach 20) with the same weight
    /// everywhere, which in game read as leaves only right next to the trees.
    /// Reaching 50 blocks covers about five times the ground, so a flat weight
    /// would need five times the leaves for the same look. Instead:
    ///
    /// - Tries scale with the area tried, so the density near the player is
    ///   what v15 tuned in game, not diluted over the wider ring.
    /// - Beyond <see cref="NearFull"/> the spawn thins smoothly to
    ///   <see cref="FarShare"/> at the reach: a leaf 50 blocks off is ten or so
    ///   pixels tall and reads as forest motion, not as a leaf, so fewer do
    ///   the same job.
    /// - Beyond <see cref="ViewNear"/>, crowns behind the camera spawn
    ///   <see cref="BehindShare"/> as many: a leaf lives ten seconds or so,
    ///   so the ring behind still has a few when the player turns.
    /// - Leaves beyond <see cref="NearFull"/> may only use
    ///   <see cref="FarCapShare"/> of the cap, so a distant flurry can never
    ///   take the leaves falling round the player.
    /// - A leaf landing beyond <see cref="LitterReach"/> fades instead of
    ///   lying, so the litter cap stays near the player where it is seen.
    ///
    /// The quality tier sets the reach and the cap. High is the reference.
    /// Quality is not enablement: Low still has leaves.
    /// </summary>
    public static class LeafBudget
    {
        /// <summary>v15's reach and cap, kept for the bench's comparison run.</summary>
        public const float LegacyReach = 20f;
        public const int LegacyCap = 400;

        public const float NearFull = 14f;
        public const float FarShare = 0.3f;
        public const float ViewNear = 10f;
        public const float BehindShare = 0.2f;

        /// <summary>The view cone: full weight inside cos = ViewFull, behind share outside cos = ViewEdge.</summary>
        public const float ViewFull = 0.45f;
        public const float ViewEdge = 0.05f;

        public const float FarCapShare = 0.7f;
        public const float LitterReach = 28f;

        /// <summary>The batched renderer's hard ceiling on flying leaves, any tier.</summary>
        public const int MaxCap = 800;

        public static float ReachFor(int tier)
        {
            switch (tier)
            {
                case QualityTier.Low: return 28f;
                case QualityTier.Medium: return 40f;
                case QualityTier.Ultra: return 56f;
                default: return 50f;
            }
        }

        public static int CapFor(int tier)
        {
            switch (tier)
            {
                case QualityTier.Low: return 200;
                case QualityTier.Medium: return 350;
                case QualityTier.Ultra: return MaxCap;
                default: return 500;
            }
        }

        /// <summary>The reach in force now: the tier's, or v15's while the bench compares.</summary>
        public static float Reach
        {
            get { return LeafBench.Mode == LeafBench.Legacy ? LegacyReach : ReachFor(QualityTier.Current); }
        }

        public static int Cap
        {
            get { return LeafBench.Mode == LeafBench.Legacy ? LegacyCap : CapFor(QualityTier.Current); }
        }

        /// <summary>
        /// Tries a tick for a reach, so that tries per square block match v15's
        /// <paramref name="legacyTries"/> over its 40 x 40 square. A disc of
        /// the reach has pi r^2 blocks against v15's (2 x 20)^2.
        /// </summary>
        public static int TriesFor(float reach, int legacyTries)
        {
            double legacyArea = (2.0 * LegacyReach) * (2.0 * LegacyReach);
            double area = Math.PI * reach * reach;
            return Math.Max(legacyTries, (int)Math.Ceiling(legacyTries * area / legacyArea));
        }

        /// <summary>The share of v15's spawn at <paramref name="distance"/> blocks: 1 near, easing to FarShare at the reach.</summary>
        public static float DistanceWeight(float distance, float reach)
        {
            if (distance <= NearFull) return 1f;
            float t = (distance - NearFull) / Math.Max(1f, reach - NearFull);
            t = Math.Max(0f, Math.Min(1f, t));
            t = t * t * (3f - 2f * t);
            return 1f + (FarShare - 1f) * t;
        }

        /// <summary>
        /// The share for where the crown is against the camera's facing:
        /// <paramref name="cosToView"/> is the cosine between the horizontal
        /// view direction and the direction to the crown. Always 1 within ViewNear.
        /// </summary>
        public static float ViewWeight(float cosToView, float distance)
        {
            if (distance <= ViewNear) return 1f;
            float t = (cosToView - ViewEdge) / (ViewFull - ViewEdge);
            t = Math.Max(0f, Math.Min(1f, t));
            t = t * t * (3f - 2f * t);
            return BehindShare + (1f - BehindShare) * t;
        }

        /// <summary>Whether a leaf at this distance may spawn with this many already flying.</summary>
        public static bool Admits(float distance, int flying, int cap)
        {
            if (flying >= cap) return false;
            return distance <= NearFull || flying < (int)(cap * FarCapShare);
        }

        /// <summary>How far below and above the player's feet a crown may sit at this distance and still shed.</summary>
        public static void HeightWindow(float distance, out float below, out float above)
        {
            below = 8f + 0.5f * distance;
            above = 24f + 0.25f * distance;
        }
    }
}
