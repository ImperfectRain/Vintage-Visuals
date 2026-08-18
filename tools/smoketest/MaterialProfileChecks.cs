using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using VintageVisuals.PseudoPBR;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Checks the material classification table.
    ///
    /// These assert *relationships* rather than exact numbers — that metal is
    /// smoother than stone, that water is the most reflective thing in the
    /// game, that soil is duller than ceramic. The absolute values are art
    /// direction and are expected to be retuned by eye; the orderings are what
    /// would make the world look wrong if they inverted, and they are the kind
    /// of thing a careless edit breaks silently.
    /// </summary>
    public static class MaterialProfileChecks
    {
        public static void Run(Action<string, bool, string> check)
        {
            Action<string, bool> ok = (name, condition) => check(name, condition, "");

            // --- every material resolves ---
            var unclassified = new List<string>();
            foreach (EnumBlockMaterial material in Enum.GetValues(typeof(EnumBlockMaterial)))
            {
                if (!MaterialProfiles.IsClassified(material)) unclassified.Add(material.ToString());
            }
            check("every EnumBlockMaterial has a profile", unclassified.Count == 0,
                "missing: " + string.Join(", ", unclassified));

            // --- values stay in range ---
            bool inRange = true;
            string offender = "";
            foreach (EnumBlockMaterial material in Enum.GetValues(typeof(EnumBlockMaterial)))
            {
                MaterialProfile p = MaterialProfiles.For(material);
                if (p.Roughness < 0f || p.Roughness > 1f ||
                    p.Metalness < 0f || p.Metalness > 1f ||
                    p.SpecularScale < 0f || p.SpecularScale > 1f ||
                    p.NormalStrength < 0f || p.NormalStrength > 4f ||
                    p.RoughnessVariation < 0f || p.RoughnessVariation > 1f)
                {
                    inRange = false;
                    if (offender.Length == 0) offender = material.ToString();
                }
            }
            check("all profile values are in range", inRange, "first offender: " + offender);

            MaterialProfile metal = MaterialProfiles.For(EnumBlockMaterial.Metal);
            MaterialProfile stone = MaterialProfiles.For(EnumBlockMaterial.Stone);
            MaterialProfile wood = MaterialProfiles.For(EnumBlockMaterial.Wood);
            MaterialProfile soil = MaterialProfiles.For(EnumBlockMaterial.Soil);
            MaterialProfile water = MaterialProfiles.For(EnumBlockMaterial.Water);
            MaterialProfile glass = MaterialProfiles.For(EnumBlockMaterial.Glass);
            MaterialProfile ice = MaterialProfiles.For(EnumBlockMaterial.Ice);
            MaterialProfile ore = MaterialProfiles.For(EnumBlockMaterial.Ore);
            MaterialProfile leaves = MaterialProfiles.For(EnumBlockMaterial.Leaves);
            MaterialProfile gravel = MaterialProfiles.For(EnumBlockMaterial.Gravel);
            MaterialProfile lava = MaterialProfiles.For(EnumBlockMaterial.Lava);
            MaterialProfile ceramic = MaterialProfiles.For(EnumBlockMaterial.Ceramic);
            MaterialProfile brick = MaterialProfiles.For(EnumBlockMaterial.Brick);

            // --- metalness comes only from the block material ---
            ok("metal is fully metallic", metal.Metalness >= 0.99f);
            ok("ore is partly metallic", ore.Metalness > 0.1f && ore.Metalness < 0.9f);
            ok("stone is not metallic", stone.Metalness == 0f);
            ok("wood is not metallic", wood.Metalness == 0f);
            ok("water is not metallic", water.Metalness == 0f);

            // --- roughness ordering: the shape of the whole look ---
            ok("water is the smoothest surface", water.Roughness < glass.Roughness || water.Roughness <= 0.06f);
            ok("glass and ice are smoother than stone",
                glass.Roughness < stone.Roughness && ice.Roughness < stone.Roughness);
            ok("metal is smoother than stone", metal.Roughness < stone.Roughness);
            ok("soil is rougher than stone", soil.Roughness > stone.Roughness);
            ok("gravel is among the roughest", gravel.Roughness >= 0.9f);

            // Ceramic is 14% of a real registry and is almost entirely brickwork
            // and roof tile, not glazed pottery. It must stay much closer to
            // stone than to glass, or a seventh of the world looks wet.
            ok("ceramic is matte, not glazed",
                ceramic.Roughness >= 0.6f && ceramic.SpecularScale <= 0.4f);
            ok("ceramic is nearer stone than glass",
                Math.Abs(ceramic.Roughness - stone.Roughness) < Math.Abs(ceramic.Roughness - glass.Roughness));
            ok("brick and ceramic match so identical walls shade alike",
                Math.Abs(brick.Roughness - ceramic.Roughness) < 1e-6f &&
                Math.Abs(brick.SpecularScale - ceramic.SpecularScale) < 1e-6f);

            // --- specular ordering ---
            ok("water and glass are the most reflective",
                water.SpecularScale >= 0.9f && glass.SpecularScale >= 0.9f);
            ok("metal is highly reflective", metal.SpecularScale >= 0.9f);
            ok("soil is barely reflective", soil.SpecularScale <= 0.1f);
            ok("lava is emissive, not reflective", lava.SpecularScale <= 0.1f);
            ok("stone is duller than metal", stone.SpecularScale < metal.SpecularScale);

            // --- normal strength: relief where it is real, flat where it is painted ---
            ok("wood exaggerates relief for grooves", wood.NormalStrength > 1.0f);
            ok("gravel exaggerates relief", gravel.NormalStrength > 1.0f);
            ok("water and glass suppress painted relief",
                water.NormalStrength < 0.5f && glass.NormalStrength < 0.5f);
            ok("soil and leaves stay soft",
                soil.NormalStrength < 1.0f && leaves.NormalStrength < 1.0f);

            // --- Combine: authority split between material and texture ---
            float roughness, specular, metalness;

            MaterialProfiles.Combine(metal, 0.5f, 0.5f, out roughness, out specular, out metalness);
            ok("combine passes metalness through untouched", Math.Abs(metalness - metal.Metalness) < 1e-6f);
            ok("neutral texture roughness leaves the material's own value",
                Math.Abs(roughness - metal.Roughness) < 1e-6f);

            MaterialProfiles.Combine(stone, 1.0f, 0.5f, out float rough1, out _, out _);
            MaterialProfiles.Combine(stone, 0.0f, 0.5f, out float rough0, out _, out _);
            ok("a busier texture reads as rougher", rough1 > rough0);
            ok("texture cannot push roughness outside the material's band",
                Math.Abs(rough1 - stone.Roughness) <= stone.RoughnessVariation + 1e-6f &&
                Math.Abs(rough0 - stone.Roughness) <= stone.RoughnessVariation + 1e-6f);

            // The key design property: a zero derived spec must not flatten a
            // metal block, because the material is the more trustworthy signal.
            MaterialProfiles.Combine(metal, 0.5f, 0.0f, out _, out float specFloor, out _);
            MaterialProfiles.Combine(metal, 0.5f, 1.0f, out _, out float specCeiling, out _);
            ok("zero derived spec still leaves metal reflective", specFloor > 0.4f);
            ok("derived spec raises within the material's ceiling",
                specCeiling > specFloor && specCeiling <= metal.SpecularScale + 1e-6f);

            MaterialProfiles.Combine(soil, 0.5f, 1.0f, out _, out float soilSpec, out _);
            ok("a bright texel cannot make soil shiny", soilSpec <= soil.SpecularScale + 1e-6f);

            // --- degenerate inputs ---
            MaterialProfiles.Combine(stone, float.NaN, float.NaN, out float nanRough, out float nanSpec, out float nanMetal);
            ok("NaN inputs do not escape as NaN",
                !float.IsNaN(nanRough) && !float.IsNaN(nanSpec) && !float.IsNaN(nanMetal));

            MaterialProfiles.Combine(stone, 5f, -5f, out float wildRough, out float wildSpec, out _);
            ok("out-of-range inputs stay clamped",
                wildRough >= 0f && wildRough <= 1f && wildSpec >= 0f && wildSpec <= 1f);

            // --- report formatting works without a client ---
            var rows = new List<MaterialReport.Row>
            {
                new MaterialReport.Row { BlockCode = "game:rock-granite", Material = "Stone", Classified = true, Profile = stone },
                new MaterialReport.Row { BlockCode = "game:rock-basalt", Material = "Stone", Classified = true, Profile = stone },
                new MaterialReport.Row { BlockCode = "game:water-still", Material = "Water", Classified = true, Profile = water },
            };
            string report = MaterialReport.Format(rows);
            ok("report lists every block", report.Contains("game:rock-granite") &&
                report.Contains("game:rock-basalt") && report.Contains("game:water-still"));
            ok("report groups by material and counts", report.Contains("Stone") && report.Contains("Water"));
            ok("report notes full classification", report.Contains("Every block matched a profile."));
        }
    }
}
