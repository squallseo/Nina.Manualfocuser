using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using OxyPlot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cwseo.NINA.ManualFocuser.Models {

    public static class SpikeAnalyzer {

        public static double TryCalculateSigmaSquare(
            IImageData imageData, SpikeAnalysisParams param,
            StarDetectionResult starResult) {

            if (imageData == null || starResult == null)
                return -1;

            var stars = SelectStars(starResult, param);
            if (stars.Count == 0)
                return -1;
            
            Logger.Info($"SelectStars = {stars.Count}");

            var sigmaSquares = new List<double>();

            foreach (var star in stars) {
                if (!TryExtractROI(imageData, star, param, out float[,] roi))
                    continue;

                RemoveBackground(roi, param);

                if (TryComputeSigmaSquare(roi, param, out double sigma2))
                    sigmaSquares.Add(sigma2);
            }

            if (sigmaSquares.Count == 0)
                return -1;

            double result = Median(sigmaSquares);
            Logger.Info($"Sigma^2 = {result:F2} px^2");

            return result;
        }

        // ----------------------------------------------------

        private static List<DetectedStar> SelectStars(
            StarDetectionResult result,
            SpikeAnalysisParams param) {

            // 밝기 상위 20%
            var brightThreshold = result.StarList
                .OrderByDescending(s => s.MaxBrightness)
                .Take(Math.Max(1, result.StarList.Count / 5))
                .Last()
                .MaxBrightness;

            return result.StarList
                .Where(s =>
                    // 충분히 밝고
                    s.MaxBrightness >= brightThreshold &&

                    // 최소 크기 보장 (noise blob 제거)
                    s.BoundingBox.Width >= param.minStarSizePx &&
                    s.BoundingBox.Height >= param.minStarSizePx &&

                    // 너무 찌그러진 것 제외 (tracking error, edge)
                    Math.Abs(
                        s.BoundingBox.Width - s.BoundingBox.Height)
                        <= Math.Min(
                            s.BoundingBox.Width,
                            s.BoundingBox.Height) * 0.5
                )
                .OrderByDescending(s => s.MaxBrightness)
                .Take(param.maxStarS)
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
            SpikeAnalysisParams param,
            out float[,] roi) {

            roi = null;

            int width = imageData.Properties.Width;
            int height = imageData.Properties.Height;
            ushort[] data = imageData.Data.FlatArray;

            int cx = (int)Math.Round(star.Position.X);
            int cy = (int)Math.Round(star.Position.Y);

            int baseSize = Math.Max(
                star.BoundingBox.Width,
                star.BoundingBox.Height) * 2;

            int halfSize = (int)(baseSize * param.roiScale);
            int minHalfSize = (int)(baseSize * 1.2);
            int maxHalfSize = (int)(baseSize * 3.5);

            halfSize = Math.Clamp(halfSize, minHalfSize, maxHalfSize);
            
            if (halfSize > height / 4) { Logger.Info("bbox to big"); }
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
                    roi[x + halfSize, y + halfSize] =
                        data[py * width + px];
                }
            }

            return true;
        }

        // ----------------------------------------------------

        private static void RemoveBackground(float[,] roi, SpikeAnalysisParams param) {
            int size = roi.GetLength(0);
            int c = size / 2;
            double rMin = (size * 0.5) * param.bgRingFraction;
            double rMin2 = rMin * rMin;

            var samples = new List<float>();

            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    double dx = x - c;
                    double dy = y - c;
                    if ((dx * dx + dy * dy) >= rMin2)
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
            float[,] roi, SpikeAnalysisParams param,
            out double sigma2) {

            sigma2 = 0;

            int size = roi.GetLength(0);
            int c = size / 2;

            double maxI = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (roi[x, y] > maxI)
                        maxI = roi[x, y];

            double saturationThreshold = maxI * param.saturationLevel;

            double sumI = 0;
            double sumX = 0;
            double sumY = 0;

            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    double I = roi[x, y];
                    if (I <= 0 || I >= saturationThreshold) continue;

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

            double coreCut = size * param.coreCutFraction; // ROI 반경의 15%
            double coreCut2 = coreCut * coreCut;

            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    double I = roi[x, y];
                    if (I <= 0 || I >= saturationThreshold) continue;

                    double dx = x - mx;
                    double dy = y - my;
                    double r2 = dx * dx + dy * dy;
                    if (r2 < coreCut2)
                        continue;

                    sumR2 += I * r2;
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

    public class SpikeAnalysisParams {
        public double roiScale { get; set; } = 2.0;//1.5~3.0 - 초점근처에서 1.5쪽으로, split수준 3, 협대역 좀 크게 2.5
        public double coreCutFraction { get; set; } = 0.15;//0.1~0.25
        public double bgRingFraction { get; set; } = 0.7;//0.65~0.8
        public double minStarSizePx { get; set; } = 6; //5~8 시잉 좋으면 5로,,
        public double saturationLevel { get; set; } = 0.9;//0.9 ~ 0.95; //camera full well의 90~95%
        public int maxStarS { get; set; } = 5; //3~8 산개성단 3~5, 은하 5~8 협대역 3~4


    }
}//namespace
