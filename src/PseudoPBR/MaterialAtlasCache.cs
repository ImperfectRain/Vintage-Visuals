using System;
using System.IO;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Stores a built material atlas on disk so the cost is paid once per
    /// texture set rather than once per launch.
    ///
    /// Deriving maps for every block texture in the game is seconds of CPU
    /// work, and the inputs only change when the game updates, a mod is added,
    /// or the profile table is retuned. The fingerprint covers exactly those,
    /// so a stale cache is discarded automatically and a valid one loads in the
    /// time it takes to read a file.
    ///
    /// Format is deliberately trivial: a header and the raw pixels. Nothing
    /// here is worth a dependency, and a corrupt or truncated file must fail
    /// into "rebuild it" rather than into an exception.
    /// </summary>
    public static class MaterialAtlasCache
    {
        /// <summary>Bump when the packing or file layout changes, so old caches are rejected.</summary>
        public const int FormatVersion = 1;

        private const uint Magic = 0x564D4154; // "VMAT"

        public sealed class CachedAtlas
        {
            public int Width;
            public int Height;
            public ulong Fingerprint;
            public int[] Pixels;
        }

        public static void Save(string path, int width, int height, ulong fingerprint, int[] pixels)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Written to a temporary file and moved into place: a crash or a
            // full disk mid-write would otherwise leave a half-atlas that looks
            // valid by its header and renders as garbage.
            string temporary = path + ".tmp";

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(fingerprint);
                writer.Write(width);
                writer.Write(height);

                foreach (int pixel in pixels) writer.Write(pixel);
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        /// <summary>
        /// Loads a cached atlas if it exists, is intact, and matches the
        /// expected fingerprint. Returns null in every other case — including
        /// on a read error, because "rebuild it" is always a safe answer and
        /// there is nothing a player could do about a corrupt cache anyway.
        /// </summary>
        public static CachedAtlas TryLoad(string path, ulong expectedFingerprint)
        {
            if (!File.Exists(path)) return null;

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadUInt32() != Magic) return null;
                    if (reader.ReadInt32() != FormatVersion) return null;

                    ulong fingerprint = reader.ReadUInt64();
                    if (fingerprint != expectedFingerprint) return null;

                    int width = reader.ReadInt32();
                    int height = reader.ReadInt32();
                    if (width <= 0 || height <= 0) return null;

                    long expectedBytes = (long)width * height * sizeof(int);
                    if (stream.Length - stream.Position != expectedBytes) return null;

                    var pixels = new int[width * height];
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = reader.ReadInt32();

                    return new CachedAtlas
                    {
                        Width = width,
                        Height = height,
                        Fingerprint = fingerprint,
                        Pixels = pixels,
                    };
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
