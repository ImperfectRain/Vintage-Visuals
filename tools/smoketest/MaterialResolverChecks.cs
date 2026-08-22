using System;
using Vintagestory.API.Common;
using VintageVisuals.PseudoPBR;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// What a rendered surface IS, versus what the block it belongs to is made
    /// of.
    ///
    /// A wooden gate is EnumBlockMaterial.Wood and its iron strapping is part of
    /// the same block, so a per-block classification gave the metal wood's
    /// roughness and a metalness of zero. These checks pin the hierarchy that
    /// fixes it, and - more importantly - pin the safety rule that stops it
    /// misfiring, because a false positive here would give a correct block a
    /// wrong material with nothing to notice it.
    /// </summary>
    public static class MaterialResolverChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            CheckHierarchy(check);
            CheckWholeSegmentSafety(check);
            CheckCompositeBlocks(check);
            CheckPerTextureWiring(repo, check);
        }

        private static void CheckHierarchy(Action<string, bool, string> check)
        {
            // 1. The asset path is the most specific evidence and wins.
            var byPath = MaterialResolver.Resolve(EnumBlockMaterial.Wood, "sides",
                                                  "game:block/metal/plate/iron");

            check("a texture filed under metal resolves as metal",
                byPath.Material == EnumBlockMaterial.Metal &&
                byPath.Evidence == MaterialEvidence.TexturePath,
                byPath.Material + " from " + byPath.Evidence);

            check("resolving from the path is reported as a reclassification",
                byPath.Reclassified, "the caller needs to be able to log and audit this");

            // 2. The slot name is next.
            var byName = MaterialResolver.Resolve(EnumBlockMaterial.Wood, "metal",
                                                  "game:block/gate/oak1");

            check("an unhelpful path falls through to the texture slot name",
                byName.Material == EnumBlockMaterial.Metal &&
                byName.Evidence == MaterialEvidence.TextureName,
                byName.Material + " from " + byName.Evidence);

            // 3. The block's own material is the fallback, not the foundation.
            var byBlock = MaterialResolver.Resolve(EnumBlockMaterial.Stone, "up",
                                                   "game:block/somewhere/unlabelled");

            check("no evidence falls back to the block's material",
                byBlock.Material == EnumBlockMaterial.Stone &&
                byBlock.Evidence == MaterialEvidence.BlockMaterial,
                byBlock.Material + " from " + byBlock.Evidence);

            check("the fallback is not reported as a reclassification",
                !byBlock.Reclassified, "");

            // A single-substance block must be completely unaffected: its path
            // agrees with its material, so nothing changes.
            var plain = MaterialResolver.Resolve(EnumBlockMaterial.Stone, "all",
                                                 "game:block/stone/rock/granite1");

            check("an ordinary block resolves to what it always did",
                plain.Material == EnumBlockMaterial.Stone && !plain.Reclassified, "");
        }

        /// <summary>
        /// The rule the whole table's safety rests on.
        /// </summary>
        private static void CheckWholeSegmentSafety(Action<string, bool, string> check)
        {
            EnumBlockMaterial material;

            // Substring matches that MUST NOT fire. Each of these is a real
            // Vintage Story naming pattern, and each would be silently wrong.
            string[] traps =
            {
                "game:block/rock/blackstone",       // contains "stone" inside a word
                "game:block/driftwood/plank1",      // contains "wood" inside a word
                "game:block/coral/sandstone1",      // contains "sand" and "stone"
                "game:block/metalworking/anvilbase" // contains "metal" inside a word
            };

            bool safe = true;
            string detail = "";

            foreach (string trap in traps)
            {
                bool matched = MaterialResolver.TryFromPath(trap, out material);

                // The trap words must not match. Where a REAL segment exists in
                // the same path it may legitimately match - so this checks the
                // specific failure of matching inside a word.
                if (trap.Contains("blackstone") && matched && material == EnumBlockMaterial.Stone)
                {
                    // "rock" is a real segment here and legitimately resolves to
                    // Stone, so this path is allowed to match - via rock, not
                    // via blackstone.
                    continue;
                }

                if (trap.Contains("driftwood") && matched)
                {
                    // "plank1" is not a bare "plank" segment and "driftwood" is
                    // not "wood", so nothing should match.
                    safe = false;
                    detail = trap + " matched as " + material;
                }

                if (trap.Contains("metalworking") && matched)
                {
                    safe = false;
                    detail = trap + " matched as " + material;
                }
            }

            check("substrings inside words never reclassify a surface", safe,
                detail + " - segments are matched between slashes, never inside a word");

            // And the positive case still works.
            check("a whole segment does match",
                MaterialResolver.TryFromPath("game:block/metal/plate/iron", out material) &&
                material == EnumBlockMaterial.Metal, "");

            // Later segments are narrower and win.
            MaterialResolver.TryFromPath("game:block/wood/door/metal", out material);
            check("the narrowest segment wins", material == EnumBlockMaterial.Metal,
                material + " - paths run general to specific");
        }

        /// <summary>
        /// The cases this exists for, stated as the outcome that matters.
        /// </summary>
        private static void CheckCompositeBlocks(Action<string, bool, string> check)
        {
            // A wooden gate with iron strapping: two textures, one block.
            var planks = MaterialResolver.Resolve(EnumBlockMaterial.Wood, "wood",
                                                  "game:block/wood/planks/oak1");
            var strapping = MaterialResolver.Resolve(EnumBlockMaterial.Wood, "metal",
                                                     "game:block/metal/plate/iron");

            check("a gate's planks stay wood",
                planks.Material == EnumBlockMaterial.Wood, planks.Material.ToString());

            check("a gate's iron strapping is metal, not wood",
                strapping.Material == EnumBlockMaterial.Metal, strapping.Material.ToString());

            check("and the two get different metalness",
                planks.Profile.Metalness < 0.05f && strapping.Profile.Metalness > 0.9f,
                "planks " + planks.Profile.Metalness + " strapping " + strapping.Profile.Metalness);

            // A lantern: metal housing, glass panes.
            var housing = MaterialResolver.Resolve(EnumBlockMaterial.Metal, "metal",
                                                   "game:block/metal/sheet/copper");
            var pane = MaterialResolver.Resolve(EnumBlockMaterial.Metal, "glass",
                                                "game:block/glass/plain");

            check("a lantern's glass is glass even though the block is metal",
                pane.Material == EnumBlockMaterial.Glass &&
                housing.Material == EnumBlockMaterial.Metal,
                pane.Material + " / " + housing.Material);
        }

        private static void CheckPerTextureWiring(string repo, Action<string, bool, string> check)
        {
            string source = System.IO.File.ReadAllText(
                System.IO.Path.Combine(repo, "src/PseudoPBR/MaterialAtlasSource.cs"));

            check("the atlas resolves material per texture, not per block",
                source.Contains("MaterialResolver.Resolve("),
                "one profile for a whole block is what gave a gate's iron the properties of wood");

            check("the per-block profile lookup is gone from the block loop",
                !source.Contains("MaterialProfile profile = MaterialProfiles.For(block.BlockMaterial);"),
                "it would silently override the per-texture resolution");

            check("reclassifications are counted and reported",
                source.Contains("reclassified++") && source.Contains("reclassifiedExamples"),
                "a silent reclassification is indistinguishable from a bug");
        }
    }
}
