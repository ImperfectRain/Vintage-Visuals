using System.Collections.Generic;
using Vintagestory.API.Common;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// How one class of material responds to light.
    ///
    /// These are the numbers that make stone read as stone and metal as metal.
    /// They are art direction expressed as physics-shaped parameters, not
    /// measured values — tuned to look right at Vintage Story's scale and
    /// lighting, and expected to be adjusted by eye.
    /// </summary>
    public struct MaterialProfile
    {
        /// <summary>Base roughness. 0 is a mirror, 1 is fully diffuse.</summary>
        public float Roughness;

        /// <summary>
        /// 0 for dielectrics, 1 for raw metal. This is the value the offline
        /// prototype fundamentally could not infer — a gold block is bright and
        /// saturated and reads as matte to any albedo-only analysis. The block's
        /// own material type is authoritative and free, which is why it drives
        /// this rather than the texture.
        /// </summary>
        public float Metalness;

        /// <summary>
        /// Ceiling for the derived specular mask. The texture decides where
        /// within a material is shinier; this decides how shiny that material
        /// can get at all.
        /// </summary>
        public float SpecularScale;

        /// <summary>
        /// Multiplier on the Sobel-derived normal. Above 1 exaggerates surface
        /// relief (log grooves, gravel), below 1 flattens it (glass, water,
        /// where the texture's painted detail is not geometry).
        /// </summary>
        public float NormalStrength;

        /// <summary>
        /// How far per-texel variance may push roughness away from
        /// <see cref="Roughness"/>. Zero pins the material to one value;
        /// larger lets a busy texture read as rougher than a smooth one of the
        /// same material.
        /// </summary>
        public float RoughnessVariation;
    }

    /// <summary>
    /// Maps a block's material to its light response.
    ///
    /// This is the piece that makes the whole PBR idea work. The offline
    /// prototype infers detail from pixels and is good at it, but it cannot
    /// classify: it has no way to know that a grey texture is polished steel
    /// rather than unlit granite, and no filter can tell it. Vintage Story
    /// already knows — every block carries an <see cref="EnumBlockMaterial"/>.
    ///
    /// So the two sources split by what each is actually good for:
    ///   block material -> WHAT it is  (metal, stone, wood, water)
    ///   texture pixels -> WHERE the detail is (grooves, pitting, ore specks)
    ///
    /// Free of game-render types on purpose, so the table can be checked
    /// without a client.
    /// </summary>
    public static class MaterialProfiles
    {
        /// <summary>
        /// Used for anything unclassified, and for the non-surface materials.
        ///
        /// Deliberately dull: an unknown surface should not sparkle. Measured
        /// against a real registry this covers 118 of 14090 blocks (0.8%) -
        /// oil lamps, skeps, beehives, candles, cobwebs - all small props where
        /// a neutral response is the right answer anyway.
        /// </summary>
        public static readonly MaterialProfile Default = new MaterialProfile
        {
            Roughness = 0.80f,
            Metalness = 0.0f,
            SpecularScale = 0.20f,
            NormalStrength = 1.0f,
            RoughnessVariation = 0.20f,
        };

        private static readonly Dictionary<EnumBlockMaterial, MaterialProfile> Table =
            new Dictionary<EnumBlockMaterial, MaterialProfile>
        {
            // Polished and worked metal: the only truly metallic surfaces, and
            // the only ones that should show sharp environment reflections.
            { EnumBlockMaterial.Metal, new MaterialProfile {
                Roughness = 0.30f, Metalness = 1.00f, SpecularScale = 1.00f,
                NormalStrength = 0.80f, RoughnessVariation = 0.15f } },

            // Ore is metal embedded in rock. Partially metallic so the specks
            // catch light while the host stone stays dull; the derived spec
            // mask is what separates the two within the texture.
            { EnumBlockMaterial.Ore, new MaterialProfile {
                Roughness = 0.65f, Metalness = 0.35f, SpecularScale = 0.85f,
                NormalStrength = 1.10f, RoughnessVariation = 0.35f } },

            { EnumBlockMaterial.Stone, new MaterialProfile {
                Roughness = 0.85f, Metalness = 0.0f, SpecularScale = 0.25f,
                NormalStrength = 1.00f, RoughnessVariation = 0.25f } },

            { EnumBlockMaterial.Mantle, new MaterialProfile {
                Roughness = 0.90f, Metalness = 0.0f, SpecularScale = 0.15f,
                NormalStrength = 1.00f, RoughnessVariation = 0.20f } },

            // Retuned against a real 14090-block registry, where Ceramic is the
            // third largest material at 2035 blocks - 14% of everything.
            //
            // The first guess here was 0.45 roughness / 0.60 specular, on the
            // reasoning that fired clay is slightly glazed. The report says
            // otherwise: this bucket is brickwork and roofing. Brick stairs,
            // brick slabs, brick courses, clay shingles, slanted roofing and
            // tool molds dominate it, and every one of those is matte fired
            // clay. Shipping the original numbers would have given the game
            // permanently wet-looking roofs and shiny brick across a seventh of
            // the world.
            //
            // Now only slightly smoother than raw stone, which is what fired
            // clay actually is.
            { EnumBlockMaterial.Ceramic, new MaterialProfile {
                Roughness = 0.70f, Metalness = 0.0f, SpecularScale = 0.30f,
                NormalStrength = 1.05f, RoughnessVariation = 0.25f } },

            // Vintage Story 1.22.7 assigns this to ZERO blocks - all of its
            // brickwork is Ceramic. Kept because a content mod may use it, and
            // matched to the Ceramic values so the two cannot drift apart and
            // make identical-looking walls shade differently.
            { EnumBlockMaterial.Brick, new MaterialProfile {
                Roughness = 0.70f, Metalness = 0.0f, SpecularScale = 0.30f,
                NormalStrength = 1.05f, RoughnessVariation = 0.25f } },

            // Normal strength above 1: log ends and plank seams are the clearest
            // real relief in the game's texture set, and the whole point of
            // deriving normals is to make those read as grooves.
            { EnumBlockMaterial.Wood, new MaterialProfile {
                Roughness = 0.75f, Metalness = 0.0f, SpecularScale = 0.20f,
                NormalStrength = 1.25f, RoughnessVariation = 0.25f } },

            // Soft and matte. Low normal strength because painted soil texture
            // is noise, not geometry, and amplifying it looks like gravel.
            { EnumBlockMaterial.Soil, new MaterialProfile {
                Roughness = 0.95f, Metalness = 0.0f, SpecularScale = 0.05f,
                NormalStrength = 0.70f, RoughnessVariation = 0.15f } },

            { EnumBlockMaterial.Gravel, new MaterialProfile {
                Roughness = 0.95f, Metalness = 0.0f, SpecularScale = 0.10f,
                NormalStrength = 1.30f, RoughnessVariation = 0.20f } },

            { EnumBlockMaterial.Sand, new MaterialProfile {
                Roughness = 0.90f, Metalness = 0.0f, SpecularScale = 0.12f,
                NormalStrength = 0.60f, RoughnessVariation = 0.15f } },

            // Fresh snow is matte but has a faint sparkle, hence some spec.
            { EnumBlockMaterial.Snow, new MaterialProfile {
                Roughness = 0.75f, Metalness = 0.0f, SpecularScale = 0.30f,
                NormalStrength = 0.60f, RoughnessVariation = 0.20f } },

            { EnumBlockMaterial.Ice, new MaterialProfile {
                Roughness = 0.15f, Metalness = 0.0f, SpecularScale = 0.90f,
                NormalStrength = 0.40f, RoughnessVariation = 0.10f } },

            { EnumBlockMaterial.Glass, new MaterialProfile {
                Roughness = 0.08f, Metalness = 0.0f, SpecularScale = 1.00f,
                NormalStrength = 0.20f, RoughnessVariation = 0.05f } },

            // Near-mirror. The actual reflection comes from the SSR pass; this
            // just tells the lighting model to expect one.
            { EnumBlockMaterial.Water, new MaterialProfile {
                Roughness = 0.05f, Metalness = 0.0f, SpecularScale = 1.00f,
                NormalStrength = 0.30f, RoughnessVariation = 0.05f } },

            // Emissive, not reflective. Lava glows; a specular highlight on it
            // would read as a wet surface, which is wrong.
            { EnumBlockMaterial.Lava, new MaterialProfile {
                Roughness = 0.70f, Metalness = 0.0f, SpecularScale = 0.05f,
                NormalStrength = 0.50f, RoughnessVariation = 0.15f } },

            // Foliage is thin and translucent rather than glossy. Kept matte
            // with weak normals so leaves stay soft instead of looking like
            // crumpled foil.
            { EnumBlockMaterial.Leaves, new MaterialProfile {
                Roughness = 0.85f, Metalness = 0.0f, SpecularScale = 0.12f,
                NormalStrength = 0.45f, RoughnessVariation = 0.20f } },

            { EnumBlockMaterial.Plant, new MaterialProfile {
                Roughness = 0.85f, Metalness = 0.0f, SpecularScale = 0.12f,
                NormalStrength = 0.50f, RoughnessVariation = 0.20f } },

            { EnumBlockMaterial.Cloth, new MaterialProfile {
                Roughness = 0.95f, Metalness = 0.0f, SpecularScale = 0.05f,
                NormalStrength = 0.60f, RoughnessVariation = 0.15f } },

            // Not surfaces. Neutral entries so nothing downstream has to
            // special-case them.
            { EnumBlockMaterial.Air, Default },
            { EnumBlockMaterial.Fire, Default },
            { EnumBlockMaterial.Meta, Default },
            { EnumBlockMaterial.Other, Default },
        };

        public static MaterialProfile For(EnumBlockMaterial material)
        {
            MaterialProfile profile;
            return Table.TryGetValue(material, out profile) ? profile : Default;
        }

        /// <summary>True when the table has an entry, as opposed to falling back.</summary>
        public static bool IsClassified(EnumBlockMaterial material)
        {
            return Table.ContainsKey(material);
        }

        /// <summary>
        /// Combines the material's base response with the texture-derived
        /// per-texel detail into the values a shader consumes.
        ///
        /// The split of authority is the whole design:
        ///
        /// - Roughness: the material sets the centre, the texture's local
        ///   variance nudges it within +/- RoughnessVariation. So all stone is
        ///   rough, but pitted stone is rougher than smooth-cut stone.
        /// - Specular: the material sets the ceiling and the texture decides
        ///   where within that range each texel sits. Deliberately not a raw
        ///   multiply — a derived mask of zero would kill a metal block's
        ///   highlight entirely, and the material type is the more trustworthy
        ///   of the two signals.
        /// - Metalness: comes from the material alone. No amount of pixel
        ///   analysis can recover it.
        /// </summary>
        public static void Combine(MaterialProfile profile, float derivedRoughness, float derivedSpec,
                                   out float roughness, out float specular, out float metalness)
        {
            // derivedRoughness is centred on its own floor-to-one range; shift
            // it to -0.5..+0.5 so it modulates around the material's value
            // rather than replacing it.
            float variation = (Clamp01(derivedRoughness) - 0.5f) * 2.0f;
            roughness = Clamp01(profile.Roughness + variation * profile.RoughnessVariation);

            // Half the material's ceiling is the floor: even the least
            // reflective texel of a metal block is still metal.
            float floor = profile.SpecularScale * 0.5f;
            specular = Clamp01(floor + (profile.SpecularScale - floor) * Clamp01(derivedSpec));

            metalness = Clamp01(profile.Metalness);
        }

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value)) return 0f;
            return value < 0f ? 0f : (value > 1f ? 1f : value);
        }
    }
}
