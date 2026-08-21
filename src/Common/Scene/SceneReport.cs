using System;
using System.IO;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VintageVisuals.Common.Scene
{
    /// <summary>
    /// Writes down why the image looks the way it does.
    ///
    /// Effect provenance. When a scene comes out too dark or too grey the
    /// question is never "is something broken" - it is WHICH of nine influences
    /// and four claimants did it, and until now the only way to answer was to
    /// switch subsystems off one at a time. Contributions and claims are already
    /// recorded; this makes them readable.
    ///
    /// The report is not a debug view. A debug view answers "what is this
    /// shader computing"; this answers "what did the whole mod decide, and who
    /// decided it".
    /// </summary>
    public static class SceneReport
    {
        public const string FileName = "scenereport.txt";

        public static void Write(ICoreClientAPI capi, EnvironmentTracker tracker, ILogger logger)
        {
            if (capi == null || tracker == null) return;

            try
            {
                // Beside the material report, which is where anyone already
                // looking for a diagnostic from this mod will look first.
                string directory = Path.Combine(GamePaths.DataPath, "VintageVisuals");
                Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, FileName);
                File.WriteAllText(path, Build(tracker));

                logger.Notification("[VintageVisuals] scene report written to " + path);
            }
            catch (Exception ex)
            {
                logger.Warning("[VintageVisuals] could not write the scene report: " + ex.Message);
            }
        }

        /// <summary>Separated from the file write so the whole thing can be checked without a disk.</summary>
        public static string Build(EnvironmentTracker tracker)
        {
            EnvironmentState world = tracker.Current;
            SceneIntent intent = tracker.Intent;

            var text = new StringBuilder();

            text.AppendLine("Vintage Visuals - scene report");
            text.AppendLine("==============================");
            text.AppendLine();
            text.AppendLine("What the world is doing");
            text.AppendLine("-----------------------");
            Row(text, "daylight", world.DayLight);
            Row(text, "moonlight", world.MoonLight);
            Row(text, "cloud cover", world.CloudCover);
            Row(text, "precipitation", world.Precipitation);
            Row(text, "rain (eased)", world.Rain);
            Row(text, "snow (eased)", world.Snow);
            Row(text, "wetness", world.Wetness);
            Row(text, "sky exposure", world.SkyExposure);
            Row(text, "depth", world.Depth);
            Row(text, "underwater", world.Underwater);
            Row(text, "proximity", world.Proximity);
            text.AppendLine("  temperature          " + world.Temperature.ToString("0.0") + " C");
            text.AppendLine("  humidity             " + world.Humidity.ToString("0.00"));
            text.AppendLine();

            text.AppendLine("What the scene needs, and who asked for it");
            text.AppendLine("------------------------------------------");
            foreach (IntentChannel channel in Enum.GetValues(typeof(IntentChannel)))
            {
                float value = intent[channel];
                bool any = false;

                foreach (IntentContribution c in intent.For(channel))
                {
                    if (!any)
                    {
                        text.AppendLine("  " + channel.ToString().PadRight(18) + value.ToString("0.00"));
                        any = true;
                    }

                    text.AppendLine("      " + c.Amount.ToString("+0.00;-0.00") + "  " +
                                    c.Source.PadRight(12) + c.Reason +
                                    (c.Capped ? "   [capped]" : ""));
                }

                if (!any && value >= 0.005f)
                {
                    text.AppendLine("  " + channel.ToString().PadRight(18) + value.ToString("0.00"));
                }
            }
            text.AppendLine();

            text.AppendLine("What each subsystem was allowed to take");
            text.AppendLine("---------------------------------------");
            text.AppendLine("  A claim marked TRIMMED asked for more than the budget had left. That is");
            text.AppendLine("  the mechanism working, not a fault - but several trimmed claims at once");
            text.AppendLine("  means the scene is being fought over and something should give.");
            text.AppendLine();

            if (tracker.Budget == null)
            {
                text.AppendLine("  (no arbitration has run yet)");
            }
            else
            {
                foreach (VisualBudget.Claim claim in tracker.Budget.Claims)
                {
                    text.AppendLine("  " + claim);
                }

                text.AppendLine();
                foreach (VisualRole role in Enum.GetValues(typeof(VisualRole)))
                {
                    text.AppendLine("  " + role.ToString().PadRight(12) + " remaining " +
                                    tracker.Budget.Remaining(role).ToString("0.00"));
                }
            }

            text.AppendLine();
            text.AppendLine("Granted, as a fraction of what was wanted");
            text.AppendLine("-----------------------------------------");
            SceneGrants grants = tracker.Grants;
            Row(text, "grade saturation", grants.GradeSaturation);
            Row(text, "grade contrast", grants.GradeContrast);
            Row(text, "grade light", grants.GradeLight);
            Row(text, "rain fog", grants.RainFog);
            Row(text, "cloud shadow", grants.CloudShadow);
            Row(text, "overcast", grants.Overcast);

            return text.ToString();
        }

        private static void Row(StringBuilder text, string name, float value)
        {
            text.AppendLine("  " + name.PadRight(20) + value.ToString("0.00"));
        }
    }
}
