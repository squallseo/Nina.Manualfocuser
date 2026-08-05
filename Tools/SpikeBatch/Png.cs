using System;
using System.IO;
using System.IO.Compression;

namespace Cwseo.NINA.ManualFocuser.Tools.SpikeBatch {

    /// <summary>
    /// Minimal 8 bit greyscale PNG writer. Exists so diagnostic crops can be looked
    /// at directly without dragging in an imaging dependency.
    /// </summary>
    public static class Png {

        public static void WriteGray8(string path, byte[] pixels, int width, int height) {
            if (pixels == null || pixels.Length < (long)width * height)
                throw new ArgumentException("pixel buffer too small");

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);

            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

            var ihdr = new byte[13];
            WriteBE(ihdr, 0, width);
            WriteBE(ihdr, 4, height);
            ihdr[8] = 8;    // bit depth
            ihdr[9] = 0;    // colour type: greyscale
            ihdr[10] = 0;   // compression
            ihdr[11] = 0;   // filter
            ihdr[12] = 0;   // interlace
            WriteChunk(fs, "IHDR", ihdr);

            // scanlines, each prefixed with filter type 0
            var raw = new byte[(width + 1) * height];
            for (int y = 0; y < height; y++) {
                raw[y * (width + 1)] = 0;
                Buffer.BlockCopy(pixels, y * width, raw, y * (width + 1) + 1, width);
            }

            WriteChunk(fs, "IDAT", ZlibCompress(raw));
            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        private static byte[] ZlibCompress(byte[] data) {
            using var ms = new MemoryStream();
            ms.WriteByte(0x78);   // CM = deflate, CINFO = 32k window
            ms.WriteByte(0x01);   // no dictionary, fastest
            using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(data, 0, data.Length);

            uint adler = Adler32(data);
            ms.WriteByte((byte)(adler >> 24));
            ms.WriteByte((byte)(adler >> 16));
            ms.WriteByte((byte)(adler >> 8));
            ms.WriteByte((byte)adler);
            return ms.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] data) {
            var len = new byte[4];
            WriteBE(len, 0, data.Length);
            s.Write(len, 0, 4);

            var typeBytes = new byte[4];
            for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);

            uint crc = Crc32(typeBytes, data);
            var crcBytes = new byte[4];
            WriteBE(crcBytes, 0, (int)crc);
            s.Write(crcBytes, 0, 4);
        }

        private static void WriteBE(byte[] buf, int offset, int value) {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static uint Adler32(byte[] data) {
            uint a = 1, b = 0;
            foreach (var v in data) {
                a = (a + v) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable() {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++) {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] a, byte[] b) {
            uint c = 0xFFFFFFFFu;
            foreach (var v in a) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
            foreach (var v in b) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
