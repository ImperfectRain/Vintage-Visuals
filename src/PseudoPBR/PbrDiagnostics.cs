using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Vintagestory.API.Common;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Records what the PBR pipeline did, to the log AND to a file beside its
    /// other outputs.
    ///
    /// The log is the right place for this and remains the primary sink. The
    /// file exists because of how diagnosing this actually goes in practice:
    /// the outputs live in VintagestoryData/VintageVisuals/, that is the folder
    /// someone opens when something looks wrong, and client-main.txt is
    /// somewhere else entirely among thousands of unrelated lines. Twice now a
    /// question about the atlas has been answered with the contents of that
    /// folder rather than the log, which is a perfectly reasonable thing to
    /// send — so the folder should contain the answer.
    ///
    /// Every message goes to both, so the two cannot disagree.
    /// </summary>
    public sealed class PbrDiagnostics
    {
        public const string Filename = "pbr-diagnostics.txt";

        private readonly ILogger _logger;
        private readonly List<string> _lines = new List<string>();

        public PbrDiagnostics(ILogger logger)
        {
            _logger = logger;
        }

        public void Note(string message)
        {
            _lines.Add(message);
            _logger.Notification("[VintageVisuals] pseudopbr: " + message);
        }

        public void Warn(string message)
        {
            _lines.Add("WARNING: " + message);
            _logger.Warning("[VintageVisuals] pseudopbr: " + message);
        }

        public void Error(string message, Exception exception)
        {
            _lines.Add("ERROR: " + message);
            _logger.Error("[VintageVisuals] pseudopbr: " + message);

            if (exception != null)
            {
                _lines.Add(exception.ToString());
                _logger.LogException(EnumLogType.Error, exception);
            }
        }

        /// <summary>
        /// Writes the transcript. Never throws — a diagnostic that takes the
        /// game down is worse than no diagnostic.
        /// </summary>
        public void WriteTo(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);

                var text = new StringBuilder();
                text.AppendLine("Vintage Visuals — PBR pipeline diagnostics");
                text.AppendLine("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                text.AppendLine();
                text.AppendLine("If the material atlas did not appear, the reason is below.");
                text.AppendLine();

                foreach (string line in _lines) text.AppendLine(line);

                File.WriteAllText(Path.Combine(directory, Filename), text.ToString());
            }
            catch (Exception ex)
            {
                _logger.Warning("[VintageVisuals] pseudopbr: could not write " + Filename + ": " + ex.Message);
            }
        }
    }
}
