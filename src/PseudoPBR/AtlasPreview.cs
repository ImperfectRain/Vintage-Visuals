using System.IO;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Writes the derived atlas out as images you can actually look at.
    ///
    /// The same reasoning as the offline tool's contact sheet: these maps
    /// cannot be judged from statistics. "Do the log grooves read as grooves,
    /// and is the roof matte" is a question for eyes, and being able to open a
    /// file beats inferring it from how the world looks after four more stages
    /// of pipeline.
    ///
    /// Emits three separate images rather than one packed RGBA, because a
    /// packed normal/roughness/spec image is unreadable to a human — the
    /// channels mean different things and only make sense viewed apart.
    ///
    /// BMP, 24-bit, no dependencies. The game ships SkiaSharp and could write
    /// PNG, but a 50-line writer for a debug artifact is a better trade than a
    /// reference to an image library.
    /// </summary>
    public static class AtlasPreview
    {
        /// <summary>Writes normal, roughness and specular views next to each other.</summary>
        public static string[] WriteAll(string directory, int width, int height, int[] pixels)
        {
            Directory.CreateDirectory(directory);

            string normalPath = Path.Combine(directory, "material-atlas-normal.bmp");
            string roughnessPath = Path.Combine(directory, "material-atlas-roughness.bmp");
            string specularPath = Path.Combine(directory, "material-atlas-specular.bmp");

            WriteBmp(normalPath, width, height, pixels, ChannelView.Normal);
            WriteBmp(roughnessPath, width, height, pixels, ChannelView.Roughness);
            WriteBmp(specularPath, width, height, pixels, ChannelView.Specular);

            return new[] { normalPath, roughnessPath, specularPath };
        }

        private enum ChannelView { Normal, Roughness, Specular }

        private static void WriteBmp(string path, int width, int height, int[] pixels, ChannelView view)
        {
            // BMP rows are padded to a 4-byte boundary and stored bottom-up.
            int rowBytes = width * 3;
            int padding = (4 - (rowBytes % 4)) % 4;
            int imageBytes = (rowBytes + padding) * height;
            int fileBytes = 54 + imageBytes;

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)'B');
                writer.Write((byte)'M');
                writer.Write(fileBytes);
                writer.Write(0);            // reserved
                writer.Write(54);           // pixel data offset

                writer.Write(40);           // DIB header size
                writer.Write(width);
                writer.Write(height);
                writer.Write((short)1);     // planes
                writer.Write((short)24);    // bits per pixel
                writer.Write(0);            // BI_RGB, no compression
                writer.Write(imageBytes);
                writer.Write(2835);         // 72 DPI, in pixels per metre
                writer.Write(2835);
                writer.Write(0);            // palette colours used
                writer.Write(0);            // important colours

                var pad = new byte[padding];

                for (int y = height - 1; y >= 0; y--)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int texel = pixels[row + x];
                        byte r = (byte)(texel & 0xFF);
                        byte g = (byte)((texel >> 8) & 0xFF);
                        byte b = (byte)((texel >> 16) & 0xFF);
                        byte a = (byte)((texel >> 24) & 0xFF);

                        byte outR, outG, outB;
                        switch (view)
                        {
                            case ChannelView.Normal:
                                // Blue reconstructed as flat so it reads like a
                                // conventional normal map at a glance.
                                outR = r; outG = g; outB = 255;
                                break;
                            case ChannelView.Roughness:
                                outR = outG = outB = b;
                                break;
                            default:
                                outR = outG = outB = a;
                                break;
                        }

                        // BMP stores BGR.
                        writer.Write(outB);
                        writer.Write(outG);
                        writer.Write(outR);
                    }

                    if (padding > 0) writer.Write(pad);
                }
            }
        }
    }
}
