using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Cwseo.NINA.ManualFocuser.Tools.SpikeBatch {

    /// <summary>
    /// Minimal FITS reader: primary HDU, BITPIX 16 or -32, 2 axes.
    /// Enough for camera light frames written by N.I.N.A.
    /// </summary>
    public sealed class FitsImage {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public ushort[] Data { get; private set; }
        public Dictionary<string, string> Header { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Path { get; private set; }

        private const int BlockSize = 2880;
        private const int CardSize = 80;

        public static FitsImage Load(string path) {
            var img = new FitsImage { Path = path };

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);

            // ---- header ----
            var block = new byte[BlockSize];
            bool endSeen = false;
            while (!endSeen) {
                ReadExactly(fs, block, BlockSize);
                for (int i = 0; i < BlockSize; i += CardSize) {
                    string card = Encoding.ASCII.GetString(block, i, CardSize);
                    string key = card.Substring(0, Math.Min(8, card.Length)).Trim();
                    if (key == "END") { endSeen = true; break; }
                    if (key.Length == 0) continue;

                    int eq = card.IndexOf('=');
                    if (eq < 0 || eq > 10) continue;

                    string value = card.Substring(eq + 1);
                    int slash = IndexOfCommentSlash(value);
                    if (slash >= 0) value = value.Substring(0, slash);
                    value = value.Trim().Trim('\'').Trim();

                    img.Header[key] = value;
                }
            }

            int bitpix = img.GetInt("BITPIX", 16);
            int naxis = img.GetInt("NAXIS", 0);
            if (naxis != 2)
                throw new NotSupportedException($"{System.IO.Path.GetFileName(path)}: NAXIS={naxis}, only 2D images are supported");

            img.Width = img.GetInt("NAXIS1", 0);
            img.Height = img.GetInt("NAXIS2", 0);
            double bzero = img.GetDouble("BZERO", 0);
            double bscale = img.GetDouble("BSCALE", 1);

            long count = (long)img.Width * img.Height;
            if (count <= 0) throw new InvalidDataException("Invalid image dimensions");

            var outData = new ushort[count];

            if (bitpix == 16) {
                var raw = new byte[count * 2];
                ReadExactly(fs, raw, raw.Length);
                for (long i = 0; i < count; i++) {
                    // FITS is big endian, 16 bit integers are signed
                    short v = (short)((raw[i * 2] << 8) | raw[i * 2 + 1]);
                    double scaled = v * bscale + bzero;
                    outData[i] = (ushort)Math.Clamp(scaled, 0, 65535);
                }
            } else if (bitpix == -32) {
                var raw = new byte[count * 4];
                ReadExactly(fs, raw, raw.Length);
                for (long i = 0; i < count; i++) {
                    int bits = (raw[i * 4] << 24) | (raw[i * 4 + 1] << 16) | (raw[i * 4 + 2] << 8) | raw[i * 4 + 3];
                    double scaled = BitConverter.Int32BitsToSingle(bits) * bscale + bzero;
                    // float frames are typically normalised to [0,1]
                    if (scaled <= 1.0 && scaled >= 0.0) scaled *= 65535.0;
                    outData[i] = (ushort)Math.Clamp(scaled, 0, 65535);
                }
            } else {
                throw new NotSupportedException($"BITPIX={bitpix} is not supported");
            }

            img.Data = outData;
            return img;
        }

        /// <summary>Reads only the header, without touching the pixel data.</summary>
        public static Dictionary<string, string> LoadHeader(string path) {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var block = new byte[BlockSize];
            while (true) {
                ReadExactly(fs, block, BlockSize);
                for (int i = 0; i < BlockSize; i += CardSize) {
                    string card = Encoding.ASCII.GetString(block, i, CardSize);
                    string key = card.Substring(0, Math.Min(8, card.Length)).Trim();
                    if (key == "END") return header;
                    if (key.Length == 0) continue;
                    int eq = card.IndexOf('=');
                    if (eq < 0 || eq > 10) continue;
                    string value = card.Substring(eq + 1);
                    int slash = IndexOfCommentSlash(value);
                    if (slash >= 0) value = value.Substring(0, slash);
                    header[key] = value.Trim().Trim('\'').Trim();
                }
            }
        }

        // A slash inside a quoted string is part of the value, not a comment.
        private static int IndexOfCommentSlash(string s) {
            bool inQuote = false;
            for (int i = 0; i < s.Length; i++) {
                if (s[i] == '\'') inQuote = !inQuote;
                else if (s[i] == '/' && !inQuote) return i;
            }
            return -1;
        }

        private static void ReadExactly(Stream s, byte[] buffer, int count) {
            int read = 0;
            while (read < count) {
                int n = s.Read(buffer, read, count - read);
                if (n <= 0) throw new EndOfStreamException("Unexpected end of FITS file");
                read += n;
            }
        }

        public int GetInt(string key, int fallback) {
            if (Header.TryGetValue(key, out var v) && int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)) return r;
            return fallback;
        }

        public double GetDouble(string key, double fallback) {
            if (Header.TryGetValue(key, out var v) && double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return r;
            return fallback;
        }

        public static int? FocuserPosition(Dictionary<string, string> header) {
            foreach (var key in new[] { "FOCPOS", "FOCUSPOS" }) {
                if (header.TryGetValue(key, out var v) &&
                    int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)) return r;
            }
            return null;
        }
    }
}
