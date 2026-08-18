using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Dumps how every loaded block was classified, to
    /// <c>VintagestoryData/VintageVisuals/material-report.txt</c>.
    ///
    /// This exists before any rendering work on purpose. Classification is the
    /// foundation the whole subsystem sits on, and it is far cheaper to read a
    /// list and spot that half the game's blocks landed in "Other" than to
    /// build an atlas and a lighting model first and then wonder why the world
    /// looks flat. It also surfaces modded blocks, which no amount of testing
    /// against vanilla would reveal.
    ///
    /// Read-only: it inspects the block registry and writes a text file. It
    /// changes nothing about how the game renders.
    /// </summary>
    public static class MaterialReport
    {
        public const string OutputDirectory = "VintageVisuals";
        public const string OutputFilename = "material-report.txt";

        /// <summary>One row of the report; separated out so it can be built and checked without a client.</summary>
        public struct Row
        {
            public string BlockCode;
            public string Material;
            public bool Classified;
            public MaterialProfile Profile;
        }

        public static string Write(ICoreClientAPI capi, ILogger logger)
        {
            List<Row> rows = Collect(capi);
            string report = Format(rows);

            string directory = Path.Combine(GamePaths.DataPath, OutputDirectory);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, OutputFilename);
            File.WriteAllText(path, report);

            logger.Notification("[VintageVisuals] pseudopbr: material report for " + rows.Count +
                                " block(s) written to " + path);
            return path;
        }

        private static List<Row> Collect(ICoreClientAPI capi)
        {
            var rows = new List<Row>();
            IList<Block> blocks = capi.World?.Blocks;
            if (blocks == null) return rows;

            foreach (Block block in blocks)
            {
                // The registry is sparse: unused ids come back as null, and
                // Air is not a surface anyone will ever see shaded.
                if (block?.Code == null) continue;
                if (block.BlockMaterial == EnumBlockMaterial.Air) continue;

                rows.Add(new Row
                {
                    BlockCode = block.Code.ToString(),
                    Material = block.BlockMaterial.ToString(),
                    Classified = MaterialProfiles.IsClassified(block.BlockMaterial),
                    Profile = MaterialProfiles.For(block.BlockMaterial),
                });
            }

            return rows;
        }

        /// <summary>
        /// Renders the report. Pure string work so it can be exercised offline.
        ///
        /// Ordered by how many blocks share a material, because the useful
        /// question is "what does most of the world look like" — a mis-tuned
        /// profile on Stone matters enormously and one on Cloth barely at all.
        /// </summary>
        public static string Format(List<Row> rows)
        {
            var text = new StringBuilder();
            text.AppendLine("Vintage Visuals — block material classification");
            text.AppendLine("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            text.AppendLine();
            text.AppendLine("Material comes from each block's own EnumBlockMaterial, which the game");
            text.AppendLine("already knows. Texture analysis supplies per-texel detail on top; it is");
            text.AppendLine("not what decides whether something is metal.");
            text.AppendLine();

            var byMaterial = rows.GroupBy(r => r.Material)
                                 .OrderByDescending(g => g.Count())
                                 .ToList();

            text.AppendLine("SUMMARY (" + rows.Count + " blocks, " + byMaterial.Count + " materials)");
            text.AppendLine();
            text.AppendLine("  material      count  rough  metal   spec  normal  classified");
            text.AppendLine("  ------------  -----  -----  -----  -----  ------  ----------");

            foreach (var group in byMaterial)
            {
                Row sample = group.First();
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-12}  {1,5}  {2,5:0.00}  {3,5:0.00}  {4,5:0.00}  {5,6:0.00}  {6}",
                    group.Key, group.Count(), sample.Profile.Roughness, sample.Profile.Metalness,
                    sample.Profile.SpecularScale, sample.Profile.NormalStrength,
                    sample.Classified ? "yes" : "NO — falls back to Default"));
            }

            int unclassified = rows.Count(r => !r.Classified);
            text.AppendLine();
            if (unclassified > 0)
            {
                text.AppendLine("  " + unclassified + " block(s) fell back to the default profile. If that is a large");
                text.AppendLine("  share of the world, add the missing EnumBlockMaterial to MaterialProfiles.");
            }
            else
            {
                text.AppendLine("  Every block matched a profile.");
            }

            text.AppendLine();
            text.AppendLine("PER BLOCK");
            text.AppendLine();

            foreach (var group in byMaterial)
            {
                text.AppendLine("  [" + group.Key + "]");
                foreach (Row row in group.OrderBy(r => r.BlockCode, StringComparer.Ordinal))
                {
                    text.AppendLine("    " + row.BlockCode);
                }
                text.AppendLine();
            }

            return text.ToString();
        }
    }
}
