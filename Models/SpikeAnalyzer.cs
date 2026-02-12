using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cwseo.NINA.ManualFocuser.Models {

    public static class SpikeAnalyzer {

        public static double TryCalculateSigmaSquare(
            IImageData imageData,
            StarDetectionResult starResult) {

            if (imageData == null || starResult == null)
                return -1;

            var stars = SelectStars(starResult);
            if (stars.Count == 0)
                return -1;

            var sigmaSquares = new List<double>();

            foreach (var star in stars) {
                if (!TryExtractROI(imageData, star, out float[,] roi))
                    continue;

                RemoveBackground(roi);

                if (TryComputeSigmaSquare(roi, out double sigma2))
                    sigmaSquares.Add(sigma2);
            }

            if (sigmaSquares.Count == 0)
                return -1;

            double result = Median(sigmaSquares);
            Logger.Info($"Sigma^2 = {result:F2} px^2");

            return result;
        }

        // ----------------------------------------------------

        private static List<DetectedStar> SelectStars(StarDetectionResult result) {
            double hfrMedian = result.AverageHFR;

            var brightThreshold = result.StarList
                .OrderByDescending(s => s.MaxBrightness)
                .Take(Math.Max(1, result.StarList.Count / 5))
                .Last()
                .MaxBrightness;

            return result.StarList
                .Where(s =>
                    s.MaxBrightness >= brightThreshold &&
                    s.HFR < hfrMedian * 1.3 &&
                    s.BoundingBox.Width > 6 &&
                    s.BoundingBox.Height > 6)
                .OrderByDescending(s => s.MaxBrightness)
                .Take(3)
                .ToList();
        }

        public static double Median(IReadOnlyList<double> values) {
            if (values == null || values.Count == 0) {
                return double.NaN;
            }

            var sorted = values
                .OrderBy(v => v)
                .ToArray();

            int mid = sorted.Length / 2;

            // 홀수
            if (sorted.Length % 2 == 1) {
                return sorted[mid];
            }

            // 짝수
            return 0.5 * (sorted[mid - 1] + sorted[mid]);
        }


        // ----------------------------------------------------

        private static bool TryExtractROI(
            IImageData imageData,
            DetectedStar star,
            out float[,] roi) {

            roi = null;

            int width = imageData.Properties.Width;
            int height = imageData.Properties.Height;
            ushort[] data = imageData.Data.FlatArray;

            int cx = (int)Math.Round(star.Position.X);
            int cy = (int)Math.Round(star.Position.Y);

            int halfSize = Math.Max(
                star.BoundingBox.Width,
                star.BoundingBox.Height) * 2;

            halfSize = Math.Clamp(halfSize, 24, 64);

            if (cx < halfSize || cy < halfSize ||
                cx + halfSize >= width || cy + halfSize >= height)
                return false;

            int size = halfSize * 2;
            roi = new float[size, size];

            for (int y = -halfSize; y < halfSize; y++) {
                for (int x = -halfSize; x < halfSize; x++) {
                    int px = cx + x;
                    int py = cy + y;
                    roi[x + halfSize, y + halfSize] =
                        data[py * width + px];
                }
            }

            return true;
        }

        // ----------------------------------------------------

        private static void RemoveBackground(float[,] roi) {
            int size = roi.GetLength(0);
            int c = size / 2;
            double rMin = size * 0.7;

            var samples = new List<float>();

            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    double dx = x - c;
                    double dy = y - c;
                    if (Math.Sqrt(dx * dx + dy * dy) >= rMin)
                        samples.Add(roi[x, y]);
                }
            }

            if (samples.Count == 0)
                return;

            float bg = Median(samples);

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    roi[x, y] = Math.Max(0, roi[x, y] - bg);
        }

        // ----------------------------------------------------

        private static bool TryComputeSigmaSquare(
            float[,] roi,
            out double sigma2) {

            sigma2 = 0;

            int size = roi.GetLength(0);
            int c = size / 2;

            double sumI = 0;
            double sumX = 0;
            double sumY = 0;

            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    double I = roi[x, y];
                    if (I <= 0) continue;

                    sumI += I;
                    sumX += I * x;
                    sumY += I * y;
                }
            }

            if (sumI <= 0)
                return false;

            double mx = sumX / sumI;
            double my = sumY / sumI;

            double sumR2 = 0;

            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    double I = roi[x, y];
                    if (I <= 0) continue;

                    double dx = x - mx;
                    double dy = y - my;
                    sumR2 += I * (dx * dx + dy * dy);
                }
            }

            sigma2 = sumR2 / sumI;
            return true;
        }

        // ----------------------------------------------------

        private static double Median(List<double> values) {
            var arr = values.OrderBy(v => v).ToArray();
            int n = arr.Length;
            return n % 2 == 1
                ? arr[n / 2]
                : (arr[n / 2 - 1] + arr[n / 2]) * 0.5;
        }

        private static float Median(List<float> values) {
            var arr = values.OrderBy(v => v).ToArray();
            int n = arr.Length;
            return n % 2 == 1
                ? arr[n / 2]
                : (arr[n / 2 - 1] + arr[n / 2]) * 0.5f;
        }
    }//class
}//namespace
