using Cwseo.NINA.ManualFocuser.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Cwseo.NINA.ManualFocuser.Tools.SpikeBatch {

    /// <summary>
    /// Writes what the metric actually sees: a stretched crop of the star ROI rotated
    /// so the spike runs horizontally, and the flux profile across it. Designing a
    /// metric from the formulation alone is guesswork; this is the ground truth.
    /// </summary>
    public static class Diagnostics {

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const int ProfileBins = 121;
        public const double ProfileUMax = 30.0;

        public sealed class FrameDump {
            public int Position;
            public double Hfr;
            public double AngleDeg;
            public double[] Profile;
        }

        public static FrameDump Dump(
            string dir,
            ushort[] data, int width, int height,
            TrackedStar star,
            SpikeAnalysisParams param,
            double angleDeg,
            int position,
            double hfr,
            bool writeImages) {

            if (!SpikeCore.TryGetDiagnosticRoi(data, width, height, star.X, star.Y, star.BaseSizePx, param,
                                               out float[] roi, out int size, out double offX, out double offY))
                return null;

            var profile = SpikeCore.ComputeUProfile(roi, size, param, angleDeg, offX, offY, ProfileUMax, ProfileBins);

            if (writeImages) {
                Directory.CreateDirectory(dir);
                WriteStretched(Path.Combine(dir, $"roi_{position}.png"), roi, size, size, 0.995);

                // The spike sits a few tenths of a percent of the core brightness, far
                // below anything a percentile of the (mostly background) ROI can reach.
                // Scaling logarithmically against the peak is what makes it visible.
                WriteLog(Path.Combine(dir, $"deep_{position}.png"), roi, size, size, 2e-3);

                var rotated = Rotate(roi, size, angleDeg, offX, offY, out int rw, out int rh);
                WriteLog(Path.Combine(dir, $"rot_{position}.png"), rotated, rw, rh, 2e-3);
            }

            return new FrameDump { Position = position, Hfr = hfr, AngleDeg = angleDeg, Profile = profile };
        }

        /// <summary>
        /// Resamples the ROI into (s, u) coordinates so the spike axis is horizontal
        /// and the profile direction is vertical. Makes the structure obvious by eye.
        /// </summary>
        private static float[] Rotate(float[] roi, int size, double angleDeg, double offX, double offY, out int outW, out int outH) {
            outW = size;
            outH = Math.Min(size, (int)(2 * ProfileUMax) + 1);

            var outBuf = new float[outW * outH];
            double cx = size / 2.0 + offX;
            double cy = size / 2.0 + offY;

            double theta = angleDeg * Math.PI / 180.0;
            double cosT = Math.Cos(theta);
            double sinT = Math.Sin(theta);

            for (int oy = 0; oy < outH; oy++) {
                double u = oy - outH / 2.0;
                for (int ox = 0; ox < outW; ox++) {
                    double s = ox - outW / 2.0;

                    // inverse of s = dx cos + dy sin, u = -dx sin + dy cos
                    double dx = s * cosT - u * sinT;
                    double dy = s * sinT + u * cosT;

                    int px = (int)Math.Round(cx + dx);
                    int py = (int)Math.Round(cy + dy);
                    if (px < 0 || py < 0 || px >= size || py >= size) continue;

                    outBuf[oy * outW + ox] = roi[py * size + px];
                }
            }
            return outBuf;
        }

        // Percentile clip plus a square-root curve: faint spike structure is several
        // orders of magnitude below the core and a linear map hides all of it.
        private static void WriteStretched(string path, float[] img, int w, int h, double highPercentile) {
            var sorted = img.Where(v => v > 0).OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return;

            double lo = sorted[(int)(sorted.Length * 0.10)];
            double hi = sorted[(int)Math.Clamp((int)(sorted.Length * highPercentile), 0, sorted.Length - 1)];
            if (hi <= lo) hi = lo + 1;

            var px = new byte[w * h];
            for (int i = 0; i < w * h; i++) {
                double v = (img[i] - lo) / (hi - lo);
                v = Math.Clamp(v, 0, 1);
                px[i] = (byte)Math.Round(255 * Math.Sqrt(v));
            }
            Png.WriteGray8(path, px, w, h);
        }

        /// <summary>Log stretch spanning [peak * floorFraction, peak].</summary>
        private static void WriteLog(string path, float[] img, int w, int h, double floorFraction) {
            double peak = 0;
            for (int i = 0; i < w * h; i++) if (img[i] > peak) peak = img[i];
            if (peak <= 0) return;

            double floor = peak * floorFraction;
            double logFloor = Math.Log10(floorFraction);

            var px = new byte[w * h];
            for (int i = 0; i < w * h; i++) {
                double x = Math.Max(img[i], floor);
                double v = (Math.Log10(x / peak) - logFloor) / (0 - logFloor);
                px[i] = (byte)Math.Round(255 * Math.Clamp(v, 0, 1));
            }
            Png.WriteGray8(path, px, w, h);
        }

        public static void WriteProfileCsv(string path, List<FrameDump> dumps) {
            var sb = new StringBuilder();
            sb.Append("u");
            foreach (var d in dumps.OrderBy(d => d.Position)) sb.Append(',').Append(d.Position.ToString(Inv));
            sb.AppendLine();

            var ordered = dumps.OrderBy(d => d.Position).ToList();
            for (int b = 0; b < ProfileBins; b++) {
                double u = -ProfileUMax + (b + 0.5) * (2 * ProfileUMax / ProfileBins);
                sb.Append(u.ToString("F2", Inv));
                foreach (var d in ordered) sb.Append(',').Append(d.Profile[b].ToString("F2", Inv));
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>ASCII rendering of every profile, normalised per row.</summary>
        public static void PrintProfiles(List<FrameDump> dumps) {
            const string ramp = " .:-=+*#%@";

            Console.WriteLine();
            Console.WriteLine("=== flux profile across the spike (u axis), normalised per row ===");
            Console.WriteLine($"    u = {-ProfileUMax:F0}px {new string(' ', ProfileBins / 2 - 12)}0{new string(' ', ProfileBins / 2 - 8)}+{ProfileUMax:F0}px");

            foreach (var d in dumps.OrderBy(d => d.Position)) {
                double max = d.Profile.Max();
                double min = d.Profile.Min();
                var sb = new StringBuilder();
                for (int b = 0; b < ProfileBins; b++) {
                    double v = max > min ? (d.Profile[b] - min) / (max - min) : 0;
                    sb.Append(ramp[Math.Clamp((int)(v * (ramp.Length - 1)), 0, ramp.Length - 1)]);
                }
                Console.WriteLine($"{d.Position,5} {d.Hfr,5:F2} |{sb}|");
            }
        }
    }
}
