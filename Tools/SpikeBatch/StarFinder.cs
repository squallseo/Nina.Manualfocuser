using Cwseo.NINA.ManualFocuser.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cwseo.NINA.ManualFocuser.Tools.SpikeBatch {

    public sealed class BackgroundStats {
        public double Median;
        public double Sigma;   // MAD based, robust against stars
    }

    /// <summary>
    /// Deliberately simple bright-star finder. It only has to reproduce the role
    /// N.I.N.A.'s detector plays in the plugin: hand SpikeCore a set of seed
    /// positions and rough sizes. It is not a general purpose star detector.
    /// </summary>
    public static class StarFinder {

        public static BackgroundStats EstimateBackground(ushort[] data, int stride = 37) {
            var samples = new List<double>(data.Length / stride + 1);
            for (int i = 0; i < data.Length; i += stride) samples.Add(data[i]);

            var arr = samples.ToArray();
            Array.Sort(arr);
            double median = arr[arr.Length / 2];

            var dev = new double[arr.Length];
            for (int i = 0; i < arr.Length; i++) dev[i] = Math.Abs(arr[i] - median);
            Array.Sort(dev);
            double mad = dev[dev.Length / 2];

            return new BackgroundStats { Median = median, Sigma = Math.Max(1.0, 1.4826 * mad) };
        }

        public static List<SpikeSeedStar> Find(
            ushort[] data, int width, int height,
            BackgroundStats bg,
            double peakSigma = 40.0,
            int maxStars = 400,
            int minSeparationPx = 120,
            int edgeMarginPx = 200,
            int maxRadiusPx = 90) {

            double peakThreshold = bg.Median + peakSigma * bg.Sigma;
            double edgeThreshold = bg.Median + 3.0 * bg.Sigma;

            // --- collect local maxima ---
            var candidates = new List<(int x, int y, ushort v)>();
            const int win = 4; // 9x9 neighbourhood

            for (int y = edgeMarginPx; y < height - edgeMarginPx; y++) {
                int row = y * width;
                for (int x = edgeMarginPx; x < width - edgeMarginPx; x++) {
                    ushort v = data[row + x];
                    if (v < peakThreshold) continue;

                    bool isMax = true;
                    for (int dy = -win; dy <= win && isMax; dy++) {
                        int r2 = (y + dy) * width;
                        for (int dx = -win; dx <= win; dx++) {
                            if (dx == 0 && dy == 0) continue;
                            if (data[r2 + x + dx] > v) { isMax = false; break; }
                        }
                    }
                    if (isMax) candidates.Add((x, y, v));
                }
            }

            // --- brightest first, enforce separation ---
            var ordered = candidates.OrderByDescending(c => c.v).ToList();
            var accepted = new List<(int x, int y, ushort v)>();
            long sep2 = (long)minSeparationPx * minSeparationPx;

            foreach (var c in ordered) {
                if (accepted.Count >= maxStars) break;
                bool clash = false;
                foreach (var a in accepted) {
                    long dx = a.x - c.x, dy = a.y - c.y;
                    if (dx * dx + dy * dy < sep2) { clash = true; break; }
                }
                if (!clash) accepted.Add(c);
            }

            // --- measure a rough extent for each ---
            var stars = new List<SpikeSeedStar>(accepted.Count);
            foreach (var c in accepted) {
                int left = Walk(data, width, c.x, c.y, -1, 0, edgeThreshold, maxRadiusPx);
                int right = Walk(data, width, c.x, c.y, +1, 0, edgeThreshold, maxRadiusPx);
                int up = Walk(data, width, c.x, c.y, 0, -1, edgeThreshold, maxRadiusPx);
                int down = Walk(data, width, c.x, c.y, 0, +1, edgeThreshold, maxRadiusPx);

                int w = left + right + 1;
                int h = up + down + 1;

                stars.Add(new SpikeSeedStar {
                    X = c.x,
                    Y = c.y,
                    WidthPx = w,
                    HeightPx = h,
                    MaxBrightness = c.v
                });
            }

            return stars;
        }

        // Distance from the peak until the profile drops below threshold.
        // Tolerates a couple of dim pixels so noise does not truncate the walk.
        private static int Walk(ushort[] data, int width, int x0, int y0, int dx, int dy, double threshold, int maxRadius) {
            int misses = 0;
            int last = 0;
            for (int r = 1; r <= maxRadius; r++) {
                int x = x0 + dx * r;
                int y = y0 + dy * r;
                if (data[y * width + x] >= threshold) { last = r; misses = 0; } else if (++misses >= 3) break;
            }
            return last;
        }

        /// <summary>
        /// Classic half flux radius, background subtracted, over the same stars the
        /// spike metric uses. This is the reference curve to judge J against.
        /// </summary>
        public static double ComputeHFR(ushort[] data, int width, int height, double cx, double cy, int radius, double background) {
            int ix = (int)Math.Round(cx);
            int iy = (int)Math.Round(cy);
            radius = Math.Clamp(radius, 3, 120);

            if (ix - radius < 0 || iy - radius < 0 || ix + radius >= width || iy + radius >= height)
                return double.NaN;

            double sumFlux = 0;
            double sumFluxR = 0;
            double r2max = (double)radius * radius;

            for (int y = -radius; y <= radius; y++) {
                int row = (iy + y) * width;
                for (int x = -radius; x <= radius; x++) {
                    double rr = x * x + y * y;
                    if (rr > r2max) continue;
                    double v = data[row + ix + x] - background;
                    if (v <= 0) continue;
                    sumFlux += v;
                    sumFluxR += v * Math.Sqrt(rr);
                }
            }

            return sumFlux > 0 ? sumFluxR / sumFlux : double.NaN;
        }

        /// <summary>
        /// Estimates the diffraction spike orientation by integrating flux along
        /// rays in an annulus around a star. Diagnostic only - the metric itself
        /// still uses the angle configured by the user.
        /// </summary>
        public static List<(double angleDeg, double strength)> EstimateSpikeAngles(
            ushort[] data, int width, int height,
            double cx, double cy, double background,
            int rInner = 12, int rOuter = 60) {

            int ix = (int)Math.Round(cx);
            int iy = (int)Math.Round(cy);
            if (ix - rOuter < 0 || iy - rOuter < 0 || ix + rOuter >= width || iy + rOuter >= height)
                return new List<(double, double)>();

            const int bins = 180;
            var profile = new double[bins];

            for (int b = 0; b < bins; b++) {
                double a = b * Math.PI / bins;
                double ca = Math.Cos(a), sa = Math.Sin(a);
                double sum = 0;
                // sample both directions: a spike is a line, not a ray
                for (int r = rInner; r <= rOuter; r++) {
                    for (int sign = -1; sign <= 1; sign += 2) {
                        int x = ix + (int)Math.Round(sign * r * ca);
                        int y = iy + (int)Math.Round(sign * r * sa);
                        double v = data[y * width + x] - background;
                        if (v > 0) sum += v;
                    }
                }
                profile[b] = sum;
            }

            // smooth a little, then pick separated peaks
            var smooth = new double[bins];
            for (int b = 0; b < bins; b++) {
                double s = 0;
                for (int k = -2; k <= 2; k++) s += profile[((b + k) % bins + bins) % bins];
                smooth[b] = s / 5.0;
            }

            double mean = smooth.Average();
            var peaks = new List<(double angleDeg, double strength)>();
            var taken = new bool[bins];

            for (int iter = 0; iter < 2; iter++) {
                int best = -1;
                double bestV = double.MinValue;
                for (int b = 0; b < bins; b++) {
                    if (taken[b]) continue;
                    if (smooth[b] > bestV) { bestV = smooth[b]; best = b; }
                }
                if (best < 0 || mean <= 0) break;

                peaks.Add((best * 180.0 / bins, bestV / mean));
                for (int k = -20; k <= 20; k++) taken[((best + k) % bins + bins) % bins] = true;
            }

            return peaks;
        }
    }
}
