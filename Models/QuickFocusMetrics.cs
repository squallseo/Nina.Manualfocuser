using System;
using System.Collections.Generic;

namespace Cwseo.NINA.ManualFocuser.Models {
    /// <summary>Local half-flux radius for a single isolated preview star, not NINA's multi-star HFR.</summary>
    public static class QuickFocusMetrics {
        public static double HalfFluxRadius(double[] pixels, int width, int height) {
            if (pixels == null || width < 16 || height < 16 || pixels.Length != width * height) return double.NaN;
            foreach (double pixel in pixels) if (!double.IsFinite(pixel)) return double.NaN;
            var sorted = (double[])pixels.Clone();
            Array.Sort(sorted);
            double background = sorted[sorted.Length / 2];
            double noise = Math.Max(1, (sorted[sorted.Length * 3 / 4] - sorted[sorted.Length / 4]) / 1.349);
            int peak = 0;
            for (int i = 1; i < pixels.Length; i++) if (pixels[i] > pixels[peak]) peak = i;
            if (pixels[peak] - background < 10 * noise) return double.NaN;
            int px = peak % width, py = peak / width;
            double sum = 0, sx = 0, sy = 0; int support = 0;
            for (int y = Math.Max(0, py - 8); y <= Math.Min(height - 1, py + 8); y++)
                for (int x = Math.Max(0, px - 8); x <= Math.Min(width - 1, px + 8); x++) {
                    double flux = Math.Max(0, pixels[y * width + x] - background - 3 * noise);
                    if (flux > 0) support++;
                    sum += flux; sx += flux * x; sy += flux * y;
                }
            if (sum <= 0 || support < 4) return double.NaN;
            double cx = sx / sum, cy = sy / sum;
            double radius = Math.Min(48, Math.Min(Math.Min(cx, cy), Math.Min(width - 1 - cx, height - 1 - cy)));
            if (radius < 12) return double.NaN;
            var samples = new List<(double Radius, double Flux)>();
            double total = 0;
            for (int y = (int)(cy - radius); y <= cy + radius; y++)
                for (int x = (int)(cx - radius); x <= cx + radius; x++) {
                    double r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (r > radius) continue;
                    double flux = Math.Max(0, pixels[y * width + x] - background - 3 * noise);
                    samples.Add((r, flux)); total += flux;
                }
            samples.Sort((a, b) => a.Radius.CompareTo(b.Radius));
            double accumulated = 0;
            foreach (var sample in samples) {
                accumulated += sample.Flux;
                if (accumulated >= total / 2) return sample.Radius;
            }
            return double.NaN;
        }
    }
}
