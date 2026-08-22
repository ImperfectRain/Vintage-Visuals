using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>Where a texture's material classification came from.</summary>
    public enum MaterialEvidence
    {
        /// <summary>The asset path the game itself filed this texture under.</summary>
        TexturePath,

        /// <summary>The name the block's own definition gave this texture slot.</summary>
        TextureName,

        /// <summary>The block's EnumBlockMaterial. One answer for the whole block.</summary>
        BlockMaterial,
    }

    public sealed class MaterialResolution
    {
        public MaterialProfile Profile;
        public EnumBlockMaterial Material;
        public MaterialEvidence Evidence;

        /// <summary>True when the evidence disagreed with the block's own classification.</summary>
        public bool Reclassified;
    }

    /// <summary>
    /// Decides what material a rendered surface actually IS, rather than what
    /// material the block it belongs to is made of.
    ///
    /// Those are different questions, and conflating them is a real defect
    /// rather than a theoretical one. A wooden gate is
    /// <c>EnumBlockMaterial.Wood</c>, and its iron strapping is part of the same
    /// block - so every one of its textures, including the metal, was being
    /// given wood's roughness, wood's specular and wood's metalness of zero.
    /// The same is true of any block that draws more than one substance:
    /// lanterns, doors, barrels, tool racks, anything with fittings.
    ///
    /// The fix is not to look harder at the pixels. It is to notice that the
    /// game already answered the question and we were reading the wrong field.
    ///
    /// EVIDENCE ORDER, most specific first:
    ///
    ///   1. the texture's ASSET PATH. The game files its textures by substance -
    ///      block/metal/..., block/wood/..., block/stone/... - and that path is
    ///      authored data, not an inference. It is per-texture, which is exactly
    ///      the granularity a composite block needs.
    ///   2. the texture's NAME in the block definition. Shapes reference their
    ///      textures by name, and a block that draws two substances usually
    ///      names them ("metal", "wood").
    ///   3. the block's EnumBlockMaterial. Correct for the overwhelming majority
    ///      of blocks, which really are one substance, and now a fallback rather
    ///      than the foundation.
    ///
    /// WHAT THIS DELIBERATELY IS NOT: an image classifier. Nothing here looks at
    /// a single pixel. Every level of the hierarchy is something a human author
    /// wrote down, which is the difference between reading the game's answer and
    /// inventing one - and inventing one from brightness is how every dark
    /// surface becomes metal.
    ///
    /// CHISELED BLOCKS NEED NOTHING HERE, and that is worth writing down because
    /// it looks like the hardest case and is not. A chiseled block's mesh
    /// textures each voxel face with its SOURCE block's texture, so a block of
    /// limestone and copper produces limestone UVs and copper UVs. The material
    /// atlas is keyed by UV. Sampling it at those coordinates already returns
    /// limestone's material for the limestone faces and copper's for the copper
    /// ones - per voxel face, for free, with no per-voxel data and no material
    /// ID attribute. The texture carries the identity because the mesher put it
    /// there.
    /// </summary>
    public static class MaterialResolver
    {
        /// <summary>
        /// Path segments that name a substance.
        ///
        /// Matched as WHOLE segments between slashes, never as substrings. That
        /// distinction is the whole safety of the table: "blackstone" does not
        /// contain the segment "stone", "driftwood" does not contain "wood", and
        /// a colour or a variant name cannot accidentally reclassify a surface.
        /// A false positive here is worse than no change at all, because it
        /// would give a correct block a wrong material with no way to notice.
        /// </summary>
        private static readonly Dictionary<string, EnumBlockMaterial> BySegment =
            new Dictionary<string, EnumBlockMaterial>(StringComparer.OrdinalIgnoreCase)
            {
                { "metal", EnumBlockMaterial.Metal },
                { "ore", EnumBlockMaterial.Ore },
                { "wood", EnumBlockMaterial.Wood },
                { "planks", EnumBlockMaterial.Wood },
                { "plank", EnumBlockMaterial.Wood },
                { "log", EnumBlockMaterial.Wood },
                { "stone", EnumBlockMaterial.Stone },
                { "rock", EnumBlockMaterial.Stone },
                { "brick", EnumBlockMaterial.Brick },
                { "glass", EnumBlockMaterial.Glass },
                { "ceramic", EnumBlockMaterial.Ceramic },
                { "clay", EnumBlockMaterial.Ceramic },
                { "cloth", EnumBlockMaterial.Cloth },
                { "linen", EnumBlockMaterial.Cloth },
                { "wool", EnumBlockMaterial.Cloth },
                { "leaves", EnumBlockMaterial.Leaves },
                { "soil", EnumBlockMaterial.Soil },
                { "sand", EnumBlockMaterial.Sand },
                { "gravel", EnumBlockMaterial.Gravel },
                { "snow", EnumBlockMaterial.Snow },
                { "ice", EnumBlockMaterial.Ice },
                { "plant", EnumBlockMaterial.Plant },
            };

        /// <summary>
        /// Texture slot names that name a substance.
        ///
        /// Deliberately much smaller than the path table. A slot name is free
        /// text chosen by whoever authored the block, so only the handful that
        /// are unambiguous across the game's own content are listed. "side",
        /// "top", "up" and the like say nothing about substance and are absent
        /// rather than guessed at.
        /// </summary>
        private static readonly Dictionary<string, EnumBlockMaterial> ByTextureName =
            new Dictionary<string, EnumBlockMaterial>(StringComparer.OrdinalIgnoreCase)
            {
                { "metal", EnumBlockMaterial.Metal },
                { "iron", EnumBlockMaterial.Metal },
                { "wood", EnumBlockMaterial.Wood },
                { "glass", EnumBlockMaterial.Glass },
                { "cloth", EnumBlockMaterial.Cloth },
                { "stone", EnumBlockMaterial.Stone },
            };

        /// <summary>
        /// Resolves one texture of one block.
        ///
        /// <paramref name="textureName"/> is the key in the block's Textures
        /// dictionary; <paramref name="texturePath"/> is the asset location the
        /// texture actually resolves to.
        /// </summary>
        public static MaterialResolution Resolve(EnumBlockMaterial blockMaterial,
                                                 string textureName, string texturePath)
        {
            EnumBlockMaterial resolved;

            if (TryFromPath(texturePath, out resolved))
            {
                return Build(resolved, blockMaterial, MaterialEvidence.TexturePath);
            }

            if (textureName != null && ByTextureName.TryGetValue(textureName, out resolved))
            {
                return Build(resolved, blockMaterial, MaterialEvidence.TextureName);
            }

            return Build(blockMaterial, blockMaterial, MaterialEvidence.BlockMaterial);
        }

        private static MaterialResolution Build(EnumBlockMaterial resolved,
                                                EnumBlockMaterial blockMaterial,
                                                MaterialEvidence evidence)
        {
            return new MaterialResolution
            {
                Profile = MaterialProfiles.For(resolved),
                Material = resolved,
                Evidence = evidence,
                Reclassified = resolved != blockMaterial,
            };
        }

        /// <summary>
        /// The LAST matching segment wins.
        ///
        /// Paths run general to specific - block/metal/plate/iron - so a later
        /// segment is a narrower statement than an earlier one. Where a path
        /// names two substances, as an overlay or a composite might, the
        /// narrower is the one describing this texture.
        /// </summary>
        public static bool TryFromPath(string path, out EnumBlockMaterial material)
        {
            material = EnumBlockMaterial.Other;
            if (string.IsNullOrEmpty(path)) return false;

            bool found = false;

            foreach (string segment in Split(path))
            {
                EnumBlockMaterial candidate;
                if (!BySegment.TryGetValue(segment, out candidate)) continue;

                material = candidate;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Whole segments only - split on the separators a path and a domain
        /// use, and nothing else. Splitting on more than this would start
        /// matching inside words, which is exactly what the table must not do.
        /// </summary>
        private static IEnumerable<string> Split(string path)
        {
            return path.Split(new[] { '/', ':', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
