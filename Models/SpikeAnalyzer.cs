using NINA.Core.Utility;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.WPF.Base.ViewModel.AutoFocus;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cwseo.NINA.ManualFocuser.Models {

    public static class SpikeAnalyzer {

        // ====================================================
        // Entry point
        // ====================================================
        public static MeasureAndError TryCalculateSpikeMetric(
            IImageData imageData,
            SpikeAnalysisParams param,
            StarDetectionResult starResult) {
            if (imageData == null || starResult == null)
                return new MeasureAndError() { Measure = -1, Stdev = 0 };

            var stars = SelectStars(starResult, param);
            if (stars.Count == 0)
                return new MeasureAndError() { Measure = -2, Stdev = 0 };

            var metrics = new List<double>();

            foreach (var star in stars) {
                if (!TryExtractROI(imageData, star, param, out float[,] roi))
                    continue;

                RemoveBackground(roi, param);

                if (TryComputeSpikeMetric(roi, param, out double value))
                    metrics.Add(value);
            }

            if (metrics.Count == 0)
                return new MeasureAndError() { Measure = -3, Stdev = 0 };

            double spikeMeasure = Median(metrics);
            Logger.Debug($"Spike metric J = {spikeMeasure:F6}");

            return new MeasureAndError() { Measure = spikeMeasure, Stdev = StdDev(metrics) };
        }

        // ====================================================
        // Star selection
        // ====================================================
        private static List<DetectedStar> SelectStars(
            StarDetectionResult result,
            SpikeAnalysisParams param) {

            var brightThreshold = result.StarList
                .OrderByDescending(s => s.MaxBrightness)
                .Take(Math.Max(1, result.StarList.Count / 5))
                .Last()
                .MaxBrightness;

            return result.StarList
                .Where(s =>
                    s.MaxBrightness >= brightThreshold &&
                    s.BoundingBox.Width >= param.minStarSizePx &&
                    s.BoundingBox.Height >= param.minStarSizePx &&
                    Math.Abs(s.BoundingBox.Width - s.BoundingBox.Height)
                        <= Math.Min(s.BoundingBox.Width, s.BoundingBox.Height) * 0.5
                )
                .OrderByDescending(s => s.MaxBrightness)
                .Take(param.maxStarS)
                .ToList();
        }

        // ====================================================
        // ROI extraction
        // ====================================================
        private static bool TryExtractROI(
            IImageData imageData,
            DetectedStar star,
            SpikeAnalysisParams param,
            out float[,] roi) {

            roi = null;

            int width = imageData.Properties.Width;
            int height = imageData.Properties.Height;
            ushort[] data = imageData.Data.FlatArray;

            int cx = (int)Math.Round(star.Position.X);
            int cy = (int)Math.Round(star.Position.Y);

            int baseSize = Math.Max(star.BoundingBox.Width, star.BoundingBox.Height) * 2;

            int halfSize = (int)(baseSize * param.roiScale);
            halfSize = Math.Clamp(
                halfSize,
                (int)(baseSize * 1.2),
                (int)(baseSize * 3.5)
            );

            halfSize = Math.Min(halfSize, height / 4);

            if (cx < halfSize || cy < halfSize ||
                cx + halfSize >= width || cy + halfSize >= height)
                return false;

            int size = halfSize * 2;
            roi = new float[size, size];

            for (int y = -halfSize; y < halfSize; y++) {
                for (int x = -halfSize; x < halfSize; x++) {
                    int px = cx + x;
                    int py = cy + y;
                    roi[y + halfSize, x + halfSize] = data[py * width + px];
                }
            }

            return true;
        }

        // ====================================================
        // Background removal
        // ====================================================
        private static void RemoveBackground(
            float[,] roi,
            SpikeAnalysisParams param) {

            int size = roi.GetLength(0);
            int c = size / 2;

            double rMin = (size * 0.5) * param.bgRingFraction;
            double rMin2 = rMin * rMin;

            var samples = new List<float>();

            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    double dx = x - c;
                    double dy = y - c;
                    if (dx * dx + dy * dy >= rMin2)
                        samples.Add(roi[y, x]);
                }
            }

            if (samples.Count == 0)
                return;

            float bg = Median(samples);

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    roi[y, x] = Math.Max(0, roi[y, x] - bg);
        }

        // ====================================================
        // Core metric computation
        //
        // J = betaVar * varC + betaSplit * (1/(kurtosis+eps))^p
        //
        // - varC: local variance across perpendicular coordinate u (px^2)
        // - kurtosis: m4 / (varG^2)   (dimensionless)
        // ====================================================
        private static bool TryComputeSpikeMetric(
            float[,] roi,
            SpikeAnalysisParams param,
            out double J) {

            J = 0;

            int size = roi.GetLength(0);
            double center = (size - 1) * 0.5;

            // Spike angle in radians (provided by user)
            double theta = param.spikeAngleDeg * Math.PI / 180.0;
            double cosT = Math.Cos(theta);
            double sinT = Math.Sin(theta);

            double eps = 1e-12;

            // Core suppression (radial)
            double r0 = Math.Max(0.5, param.coreRejectSigmaPx);
            double inv2r02 = 1.0 / (2.0 * r0 * r0);

            // Axis window (along s)
            double sSigma = Math.Max(1.0, param.axisSigmaPx);
            double inv2sSig2 = 1.0 / (2.0 * sSigma * sSigma);

            double sReject = Math.Max(0.5, param.axisRejectSigmaPx);
            double inv2sRej2 = 1.0 / (2.0 * sReject * sReject);

            // Local weighting across u for varC
            double tau = Math.Max(0.25, param.coreSigmaPx);
            double inv2tau2 = 1.0 / (2.0 * tau * tau);

            // Collect weighted samples for u
            double totalW = 0;
            var samples = new List<(double u, double w)>(size * size / 2);

            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    double I = roi[y, x];
                    if (I <= 0) continue;

                    double dx = x - center;
                    double dy = y - center;

                    // Along spike axis
                    double s = dx * cosT + dy * sinT;

                    // Perpendicular to spike axis (profile coordinate)
                    double u = -dx * sinT + dy * cosT;

                    double r2 = dx * dx + dy * dy;

                    // weights
                    double wCore = 1.0 - Math.Exp(-r2 * inv2r02);
                    double wAxis = Math.Exp(-s * s * inv2sSig2) * (1.0 - Math.Exp(-s * s * inv2sRej2));

                    double w = I * wCore * wAxis;
                    if (w <= 0) continue;

                    samples.Add((u, w));
                    totalW += w;
                }
            }

            if (totalW <= 0 || samples.Count < 50)
                return false;

            // Weighted mean in u
            double meanU = samples.Sum(v => v.w * v.u) / totalW;

            // Global variance varG (px^2)
            double varG = samples.Sum(v => {
                double d = v.u - meanU;
                return v.w * d * d;
            }) / totalW;

            varG = Math.Max(varG, eps);

            // Local variance varC (Gaussian around meanU, px^2)
            double wSumLocal = 0;
            double varC = 0;

            foreach (var (u, w0) in samples) {
                double d = u - meanU;
                double wl = Math.Exp(-d * d * inv2tau2);
                double w = w0 * wl;

                wSumLocal += w;
                varC += w * d * d;
            }

            if (wSumLocal <= 0)
                return false;

            varC = Math.Max(varC / wSumLocal, eps);

            // 4th central moment m4
            double m4 = samples.Sum(v => {
                double d = v.u - meanU;
                double d2 = d * d;
                return v.w * d2 * d2;
            }) / totalW;

            // Kurtosis kappa = m4 / varG^2
            double kurt = m4 / (varG * varG + eps);

            // Split penalty (enhanced)
            double splitBase = 1.0 / (kurt + param.kurtosisEps);
            double splitPenalty = Math.Pow(splitBase, Math.Max(1.0, param.splitPower));

            // Final metric (variance + split)
            J = param.betaVar * varC
              + param.betaSplit * splitPenalty;

            return true;
        }

        // ====================================================
        // Utilities
        // ====================================================
        public static double Median(List<double> values) {
            var arr = values.OrderBy(v => v).ToArray();
            int n = arr.Length;
            return n % 2 == 1
                ? arr[n / 2]
                : 0.5 * (arr[n / 2 - 1] + arr[n / 2]);
        }

        public static float Median(List<float> values) {
            var arr = values.OrderBy(v => v).ToArray();
            int n = arr.Length;
            return n % 2 == 1
                ? arr[n / 2]
                : 0.5f * (arr[n / 2 - 1] + arr[n / 2]);
        }

        // ====================================================
        // MAD (Median Absolute Deviation)
        // ====================================================
        public static double MAD(List<double> values, bool scaleToStd = false) {
            if (values == null || values.Count == 0)
                return 0.0;
            if (values.Count == 1) { return values[0]; }
            // 중앙값
            double median = Median(values);

            // 절대 편차 계산
            var deviations = values
                .Select(v => Math.Abs(v - median))
                .ToList();

            double mad = Median(deviations);

            // 정규분포 표준편차 추정값으로 변환할지 여부
            if (scaleToStd)
                mad *= 1.4826;   // Gaussian consistency constant

            return mad;
        }

        // ====================================================
        // Standard Deviation (Population)
        // ====================================================
        public static double StdDev(List<double> values) {
            if (values == null || values.Count == 0)
                return 0.0;

            double mean = values.Average();

            double variance = values
                .Select(v => (v - mean) * (v - mean))
                .Average();

            return Math.Sqrt(variance);
        }
    }

    // ========================================================
    // Parameter class (simplified)
    // ========================================================
    public class SpikeAnalysisParams {
        public double roiScale { get; set; } = 2.0;
        public double bgRingFraction { get; set; } = 0.7;
        public double minStarSizePx { get; set; } = 6;
        public int maxStarS { get; set; } = 5;

        // user-provided spike angle (deg)
        public double spikeAngleDeg { get; set; } = 90.0;

        // local u-gaussian sigma (tau)
        public double coreSigmaPx { get; set; } = 1.5;

        // radial core suppression strength
        public double coreRejectSigmaPx { get; set; } = 4.0;

        // along-axis window (s)
        public double axisSigmaPx { get; set; } = 25.0;

        // reject center along s
        public double axisRejectSigmaPx { get; set; } = 6.0;

        // metric weights
        public double betaVar { get; set; } = 1.0;     // variance weight
        public double betaSplit { get; set; } = 4.0;   // split penalty weight (start 3~6)

        // split penalty power p (>= 1). Recommend 2.
        public double splitPower { get; set; } = 2.0;

        // numeric stability
        public double kurtosisEps { get; set; } = 1e-6;
    }
}