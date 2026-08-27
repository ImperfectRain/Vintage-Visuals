using System;
using System.Collections.Generic;
using System.Linq;

namespace VintageVisuals.Common.Patching
{
    /// <summary>
    /// Which patch groups can never be applied to the same shader at once.
    ///
    /// WHY THIS HAS TO BE DECLARED SOMEWHERE BOTH SIDES CAN READ.
    ///
    /// Two groups that inject the same uniform declaration cannot coexist: GLSL
    /// rejects `uniform sampler2D vv_materialTex` twice with "declared more than
    /// once", and a terrain shader that will not compile costs the world render
    /// rather than the feature. Today nothing reaches that state, because
    /// IsPatchGroupEnabled turns pseudopbr off whenever the newer terrain
    /// material path is on - but that is a fact living in one method, and
    /// tools/verifypatches applies every group it finds on disk with no idea the
    /// method exists. So the tool reported a failure the game cannot have, which
    /// is the kind of false alarm that gets a real one ignored.
    ///
    /// The exclusion is therefore stated once, here, and read from both places:
    /// the tool skips the impossible combination and says why, and a smoke check
    /// proves the runtime gating actually honours it. The migration between the
    /// two terrain paths can then proceed without either half drifting from the
    /// other - and a future edit that switches both on fails a test rather than
    /// a world.
    /// </summary>
    public static class ShaderPatchGroups
    {
        /// <summary>
        /// Groups that must never be live together, as sets. Two groups in the
        /// same set are mutually exclusive; groups in different sets are not.
        /// </summary>
        public static readonly IReadOnlyList<IReadOnlyCollection<string>> MutuallyExclusive =
            new List<IReadOnlyCollection<string>>
            {
                // The terrain material migration. `pbrterrainmaterial` is the
                // newer path and `pseudopbr`/`pseudopbrtopsoil` the older one;
                // both declare the material atlas samplers, so whichever is live
                // owns them alone.
                new[] { "pbrterrainmaterial", "pseudopbr" },
                new[] { "pbrterrainmaterial", "pseudopbrtopsoil" },
            };

        /// <summary>Whether these two groups are declared mutually exclusive.</summary>
        public static bool AreExclusive(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return false;

            foreach (IReadOnlyCollection<string> set in MutuallyExclusive)
            {
                if (set.Contains(a, StringComparer.Ordinal) && set.Contains(b, StringComparer.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The first exclusive pair inside a set of groups, or null if the set
        /// is a combination the game could actually produce.
        /// </summary>
        public static string[] FirstExclusivePair(IEnumerable<string> groups)
        {
            string[] list = groups as string[] ?? groups.ToArray();

            for (int i = 0; i < list.Length; i++)
            {
                for (int j = i + 1; j < list.Length; j++)
                {
                    if (AreExclusive(list[i], list[j])) return new[] { list[i], list[j] };
                }
            }
            return null;
        }
    }
}
