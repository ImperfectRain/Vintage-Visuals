using System;
using System.IO;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Writes the derived atlas out as images you can actually look at.
    ///
    /// The same reasoning as the offline tool's contact sheet: these maps
    /// cannot be judged from statistics. "Do the log grooves read as grooves,
    /// and is the roof matte" is a question for eyes.
    ///
    /// Emits three separate images rather than one packed RGBA, because a
    /// packed normal/roughness/spec image is unreadable to a human — the
    /// channels mean different things and only make sense viewed apart.
    ///
    /// PNG, written by hand, no dependencies. This was BMP first, which was
    /// simpler to write and turned out to be the wrong choice for the job: BMP
    /// is awkward to share, does not preview in most tools, and several
    /// upload paths reject it outright. An image nobody can send you is not a
    /// diagnostic. PNG costs about sixty extra lines and is universally
    /// viewable.
    ///
    /// The deflate stream uses stored (uncompressed) blocks. Real compression
    /// would need a full deflate implementation, and these files are written
    /// once, read by a human, and deleted — spending code on ratio here would
    /// be optimising the wrong axis.
    /// </summary>
    public static class AtlasPreview
    {
        /// <summary>Writes normal, roughness and specular views side by side.</summary>
        public static string[] WriteAll(string directory, int width, int height, int[] pixels)
        {
            Directory.CreateDirectory(directory);

            string normalPath = Path.Combine(directory, "material-atlas-normal.png");
            string roughnessPath = Path.Combine(directory, "material-atlas-roughness.png");
            string specularPath = Path.Combine(directory, "material-atlas-specular.png");

            WritePng(normalPath, width, height, pixels, ChannelView.Normal);
            WritePng(roughnessPath, width, height, pixels, ChannelView.Roughness);
            WritePng(specularPath, width, height, pixels, ChannelView.Specular);

            return new[] { normalPath, roughnessPath, specularPath };
        }

        private enum ChannelView { Normal, Roughness, Specular }

        private static void WritePng(string path, int width, int height, int[] pixels, ChannelView view)
        {
            byte[] raw = BuildScanlines(width, height, pixels, view);

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                var header = new byte[13];
                WriteBigEndian(header, 0, width);
                WriteBigEndian(header, 4, height);
                header[8] = 8;  // bit depth
                header[9] = 2;  // colour type 2: truecolour RGB
                header[10] = 0; // deflate
                header[11] = 0; // adaptive filtering
                header[12] = 0; // no interlace

                WriteChunk(stream, "IHDR", header);
                WriteChunk(stream, "IDAT", ZlibStore(raw));
                WriteChunk(stream, "IEND", new byte[0]);
            }
        }

        /// <summary>
        /// Builds the raw PNG image data: each row prefixed with a filter byte.
        /// </summary>
        private static byte[] BuildScanlines(int width, int height, int[] pixels, ChannelView view)
        {
            var raw = new byte[height * (1 + width * 3)];
            int at = 0;

            for (int y = 0; y < height; y++)
            {
                raw[at++] = 0; // filter type 0: none

                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int texel = pixels[row + x];
                    byte r = (byte)(texel & 0xFF);
                    byte g = (byte)((texel >> 8) & 0xFF);
                    byte b = (byte)((texel >> 16) & 0xFF);
                    byte a = (byte)((texel >> 24) & 0xFF);

                    switch (view)
                    {
                        case ChannelView.Normal:
                            // Blue forced flat so it reads like a conventional
                            // normal map at a glance.
                            raw[at++] = r; raw[at++] = g; raw[at++] = 255;
                            break;
                        case ChannelView.Roughness:
                            raw[at++] = b; raw[at++] = b; raw[at++] = b;
                            break;
                        default:
                            raw[at++] = a; raw[at++] = a; raw[at++] = a;
                            break;
                    }
                }
            }

            return raw;
        }

        /// <summary>Wraps data in a zlib stream built from stored deflate blocks.</summary>
        private static byte[] ZlibStore(byte[] data)
        {
            using (var buffer = new MemoryStream())
            {
                buffer.WriteByte(0x78); // CM=8, CINFO=7
                buffer.WriteByte(0x01); // no preset dictionary, fastest

                const int MaxBlock = 65535;
                int offset = 0;

                do
                {
                    int length = Math.Min(MaxBlock, data.Length - offset);
                    bool last = offset + length >= data.Length;

                    buffer.WriteByte((byte)(last ? 1 : 0));      // BFINAL, BTYPE=00 stored
                    buffer.WriteByte((byte)(length & 0xFF));      // LEN, little endian
                    buffer.WriteByte((byte)((length >> 8) & 0xFF));
                    buffer.WriteByte((byte)(~length & 0xFF));     // NLEN, ones complement
                    buffer.WriteByte((byte)((~length >> 8) & 0xFF));
                    buffer.Write(data, offset, length);

                    offset += length;
                }
                while (offset < data.Length);

                WriteBigEndianTo(buffer, Adler32(data));
                return buffer.ToArray();
            }
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var header = new byte[4];
            WriteBigEndian(header, 0, data.Length);
            stream.Write(header, 0, 4);

            var typeBytes = new byte[4];
            for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            stream.Write(typeBytes, 0, 4);
            stream.Write(data, 0, data.Length);

            // The CRC covers the chunk type and its data, but not the length.
            uint crc = Crc32(typeBytes, 0xFFFFFFFF);
            crc = Crc32(data, crc);

            var crcBytes = new byte[4];
            WriteBigEndian(crcBytes, 0, (int)(crc ^ 0xFFFFFFFF));
            stream.Write(crcBytes, 0, 4);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] data, uint crc)
        {
            foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static void WriteBigEndian(byte[] target, int offset, int value)
        {
            target[offset] = (byte)((value >> 24) & 0xFF);
            target[offset + 1] = (byte)((value >> 16) & 0xFF);
            target[offset + 2] = (byte)((value >> 8) & 0xFF);
            target[offset + 3] = (byte)(value & 0xFF);
        }

        private static void WriteBigEndianTo(Stream stream, uint value)
        {
            stream.WriteByte((byte)((value >> 24) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)(value & 0xFF));
        }
    }
}
