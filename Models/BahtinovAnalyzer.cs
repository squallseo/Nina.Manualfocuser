using System;
using System.Collections.Generic;
using System.Linq;

namespace Cwseo.NINA.ManualFocuser.Models {
    public readonly record struct BahtinovLine(double Nx, double Ny, double Offset);
    public sealed class BahtinovMeasurement {
        public bool IsValid { get; init; }
        public string FailureReason { get; init; } = "";
        public double SignedErrorPixels { get; init; } = double.NaN;
        public double Confidence { get; init; }
        public BahtinovLine[] Lines { get; init; } = Array.Empty<BahtinovLine>();
    }

    /// <summary>
    /// Independent Radon-projection implementation of the three-line measurement described
    /// by BahtiFocus (521f63d), not a source copy. Geometry uses normal equations to avoid
    /// vertical-line singularities. Quality thresholds are project engineering choices.
    /// A detected three-line pattern does not establish that a physical mask is installed.
    /// </summary>
    public static class BahtinovAnalyzer {
        private sealed record Candidate(double Angle, double Offset, double Score, double Contrast);
        public static BahtinovMeasurement Analyze(double[] pixels, int width, int height) {
            BahtinovMeasurement Invalid(string reason) => new() { FailureReason = reason };
            if (pixels == null || width < 48 || height < 48 || (long)width * height != pixels.Length)
                return Invalid("ROI must contain at least 48 x 48 pixels.");
            if (pixels.Any(v => !double.IsFinite(v))) return Invalid("Non-finite image values.");
            var sorted = (double[])pixels.Clone(); Array.Sort(sorted);
            double background = sorted[sorted.Length / 2];
            double noise = Math.Max(1e-6, (sorted[(int)(sorted.Length * .84)] - sorted[(int)(sorted.Length * .16)]) / 2);
            double cx = (width - 1) / 2.0, cy = (height - 1) / 2.0;
            double radius = Math.Min(width, height) * .43, inner = Math.Max(7, radius * .3);
            int extent = (int)Math.Ceiling(radius), limit = Math.Max(4, (int)(radius * .35));
            var samples = new List<(double x, double y, double value)>();
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
                double dx = x - cx, dy = y - cy, r2 = dx * dx + dy * dy;
                if (r2 >= inner * inner && r2 <= radius * radius)
                    samples.Add((dx, dy, Math.Max(0, pixels[y * width + x] - background)));
            }
            var candidates = new List<Candidate>();
            for (int halfDegrees = 0; halfDegrees < 360; halfDegrees++) {
                double angle = halfDegrees * Math.PI / 360, nx = Math.Cos(angle), ny = Math.Sin(angle);
                var bins = new double[extent * 2 + 3];
                var positive = new double[bins.Length]; var negative = new double[bins.Length];
                foreach (var p in samples) {
                    double u = p.x * nx + p.y * ny + extent;
                    int i = (int)Math.Floor(u); double t = u - i;
                    if (i >= 0 && i + 1 < bins.Length) {
                        var half = -p.x * ny + p.y * nx >= 0 ? positive : negative;
                        half[i] += p.value * (1 - t); half[i + 1] += p.value * t;
                    }
                }
                for (int i = 0; i < bins.Length; i++) bins[i] = 2 * Math.Min(positive[i], negative[i]);
                int peak = extent - limit;
                for (int i = extent - limit + 1; i <= extent + limit; i++) if (bins[i] > bins[peak]) peak = i;
                double side = (bins[peak - 4] + bins[peak + 4] + bins[peak - 6] + bins[peak + 6]) / 4;
                double prominence = bins[peak] - side;
                double contrast = prominence / Math.Max(noise * Math.Sqrt(radius * 2), side * .15);
                double denominator = bins[peak - 1] - 2 * bins[peak] + bins[peak + 1];
                double sub = denominator < -1e-9 ? .5 * (bins[peak - 1] - bins[peak + 1]) / denominator : 0;
                sub = Math.Clamp(sub, -.5, .5);
                candidates.Add(new(angle, peak - extent + sub, prominence, contrast));
            }
            var chosen = new List<Candidate>();
            var angularPeaks = candidates.Where((c, i) => Enumerable.Range(-4, 9).All(d =>
                c.Score >= candidates[(i + d + candidates.Count) % candidates.Count].Score));
            foreach (var c in angularPeaks.OrderByDescending(c => c.Score)) {
                if (chosen.All(p => AngleDistance(p.Angle, c.Angle) >= 18 * Math.PI / 180)) chosen.Add(c);
                if (chosen.Count == 3) break;
            }
            if (chosen.Count != 3 || chosen.Any(c => c.Contrast < 3 || c.Score <= 0)) return Invalid("Three distinct high-contrast diffraction lines were not detected.");
            double typicalScore = candidates.Select(c => c.Score).OrderBy(v => v).ElementAt(candidates.Count / 2);
            if (chosen.Any(c => c.Score < Math.Max(1e-9, typicalScore * 1.2))) return Invalid("Pattern lacks directional diffraction evidence.");
            chosen.Sort((a, b) => a.Angle.CompareTo(b.Angle));
            int start = 0; double largestGap = -1;
            for (int i = 0; i < 3; i++) {
                double gap = chosen[(i + 1) % 3].Angle + (i == 2 ? Math.PI : 0) - chosen[i].Angle;
                if (gap > largestGap) { largestGap = gap; start = (i + 1) % 3; }
            }
            var ordered = new List<Candidate>();
            for (int i = 0; i < 3; i++) {
                int index = (start + i) % 3; var c = chosen[index];
                ordered.Add(index < start ? c with { Angle = c.Angle + Math.PI, Offset = -c.Offset } : c);
            }
            double g1 = ordered[1].Angle - ordered[0].Angle, g2 = ordered[2].Angle - ordered[1].Angle;
            if (g1 + g2 > Math.PI * .6 || Math.Abs(g1 - g2) > 12 * Math.PI / 180)
                return Invalid("Detected angles do not form a symmetric Bahtinov triplet.");
            var lines = ordered.Select(c => new BahtinovLine(Math.Cos(c.Angle), Math.Sin(c.Angle), c.Offset)).ToArray();
            double det = lines[0].Nx * lines[2].Ny - lines[2].Nx * lines[0].Ny;
            if (Math.Abs(det) < .15) return Invalid("Outer diffraction lines are nearly parallel.");
            double ix = (lines[0].Offset * lines[2].Ny - lines[2].Offset * lines[0].Ny) / det;
            double iy = (lines[0].Nx * lines[2].Offset - lines[2].Nx * lines[0].Offset) / det;
            if (Math.Sqrt(ix * ix + iy * iy) > radius * .5) return Invalid("Intersection lies too far from ROI center.");
            double error = lines[1].Offset - lines[1].Nx * ix - lines[1].Ny * iy;
            return new() { IsValid = true, SignedErrorPixels = error, Confidence = ordered.Min(c => c.Contrast), Lines = lines };
        }
        private static double AngleDistance(double a, double b) { double d = Math.Abs(a - b); return Math.Min(d, Math.PI - d); }
    }
}
