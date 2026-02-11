using Accord;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Cwseo.NINA.ManualFocuser.Models {
    public class SpikeAnalyzer {

        public SpikeAnalyzer() { }

        public static double TryCalculateAverageSpikeThickness(
            IImageData imageData,
            StarDetectionResult starResult) {
            Logger.Info("Start spike analyzer");
            var candidates = SelectSpikeCandidates(starResult);
            if (candidates.Count == 0)
                return -1;

            var thicknesses = new List<double>();

            foreach (var star in candidates) {
                var roi = ExtractROI(imageData, star);
                if (roi == null)
                    continue;

                if (TryMeasureSpikeThickness(roi, out double thickness))
                    thicknesses.Add(thickness);
            }

            if (thicknesses.Count == 0)
                return -1;

            //가중평균 고려
            var avgthick = Median(thicknesses);
            Logger.Info($"spike thickness {avgthick} px");

            return avgthick;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort GetPixel(ushort[] data, int width, int x, int y) {
            return data[y * width + x];
        }

        private static List<DetectedStar> SelectSpikeCandidates(StarDetectionResult result) {
            double hfrMedian = result.AverageHFR;

            // 밝기 기준 상위 20% 컷
            var brightThreshold = result.StarList
                .OrderByDescending(s => s.MaxBrightness)
                .Take(Math.Max(1, result.StarList.Count / 5))
                .Last()
                .MaxBrightness;

            return result.StarList
                .Where(s =>
                    s.MaxBrightness > brightThreshold &&
                    s.HFR < hfrMedian * 1.3 &&            // 심하게 퍼진 별 제외
                    s.BoundingBox.Width > 5 &&             // 잡음 blob 제외
                    s.BoundingBox.Height > 5)
                .OrderByDescending(s => s.MaxBrightness)
                .Take(3)                                  // 많을 필요 없음
                .ToList();
        }


        private static float[,] ExtractROI(
            IImageData imageData,
            DetectedStar star) {
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
                return null;

            int size = halfSize * 2;
            var roi = new float[size, size];

            for (int y = -halfSize; y < halfSize; y++)
                for (int x = -halfSize; x < halfSize; x++) {
                    int px = cx + x;
                    int py = cy + y;

                    double r = Math.Sqrt(x * x + y * y);

                    // 포화 core 제거
                    if (r < 3) {
                        roi[x + halfSize, y + halfSize] = 0;
                        continue;
                    }

                    roi[x + halfSize, y + halfSize] =
                        GetPixel(data, width, px, py);
                }

            return roi;
        }



        private static bool TryMeasureSpikeThickness(
            float[,] roi,
            out double thicknessPx) {
            thicknessPx = 0;

            int size = roi.GetLength(0);

            double bestAngle = 0;
            double bestPeak = 0;

            // 0~90도면 충분 (직교 spike)
            for (int i = 0; i < 90; i++) {
                double angle = i * Math.PI / 90.0;
                var profile = ProjectProfile(roi, angle);

                double peak = profile.Max();
                if (peak > bestPeak) {
                    bestPeak = peak;
                    bestAngle = angle;
                }
            }

            if (bestPeak <= 0)
                return false;

            var thicknessSamples = new List<double>();
            int c = size / 2;

            // spike 방향 단위 벡터
            double dx = Math.Cos(bestAngle);
            double dy = Math.Sin(bestAngle);

            // 중심에서 떨어진 위치들 (core 피해서)
            for (int r = 6; r < size / 2 - 4; r += 2) {
                int ox = (int)(c + dx * r);
                int oy = (int)(c + dy * r);

                var profile = ProjectProfileAtOffset(roi, bestAngle, ox, oy);
                double fwhm = MeasureFWHM(profile);

                if (fwhm > 0 && fwhm < size * 0.4)
                    thicknessSamples.Add(fwhm);
            }

            if (thicknessSamples.Count == 0)
                return false;

            thicknessPx = Median(thicknessSamples);
            return true;
        }

        private static double[] ProjectProfileAtOffset(
            float[,] img,
            double angle,
            int cx,
            int cy) {

            int size = img.GetLength(0);
            int half = size / 2;

            double sin = Math.Sin(angle);
            double cos = Math.Cos(angle);

            var profile = new double[size];

            for (int t = -half; t < half; t++) {
                double sum = 0;
                int cnt = 0;

                // spike에 수직한 방향으로 샘플
                for (int w = -6; w <= 6; w++) {
                    int x = (int)(cx + w * -sin);
                    int y = (int)(cy + w * cos);

                    if (x >= 0 && y >= 0 && x < size && y < size) {
                        sum += img[x, y];
                        cnt++;
                    }
                }

                profile[t + half] = cnt > 0 ? sum / cnt : 0;

                cx += (int)cos;
                cy += (int)sin;
            }

            return profile;
        }


        private static double[] ProjectProfile(float[,] img, double angle) {
            int size = img.GetLength(0);
            int c = size / 2;

            double sin = Math.Sin(angle);
            double cos = Math.Cos(angle);

            var profile = new double[size];

            for (int y = 0; y < size; y++) {
                double sum = 0;
                int cnt = 0;

                for (int x = 0; x < size; x++) {
                    int rx = (int)(c + (x - c) * cos + (y - c) * sin);
                    int ry = (int)(c - (x - c) * sin + (y - c) * cos);

                    if (rx >= 0 && ry >= 0 && rx < size && ry < size) {
                        sum += img[rx, ry];
                        cnt++;
                    }
                }

                profile[y] = cnt > 0 ? sum / cnt : 0;
            }

            return profile;
        }


        private static double MeasureFWHM(double[] profile) {
            int peakIdx = Array.IndexOf(profile, profile.Max());
            double peak = profile[peakIdx];

            double background = profile.Average();
            double half = background + (peak - background) * 0.5;

            int l = peakIdx;
            while (l > 0 && profile[l] > half) l--;

            int r = peakIdx;
            while (r < profile.Length - 1 && profile[r] > half) r++;

            return r - l;
        }


        private static double Median(List<double> values) {
            var arr = values.OrderBy(v => v).ToArray();
            int n = arr.Length;
            return n % 2 == 1
                ? arr[n / 2]
                : (arr[n / 2 - 1] + arr[n / 2]) * 0.5;
        }



    } //class
}//namespace
