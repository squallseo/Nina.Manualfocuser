using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.WPF.Base.ViewModel.AutoFocus;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows;

namespace Cwseo.NINA.ManualFocuser.Models {
    // ========================================================
    // Return container
    // ========================================================
    public readonly struct SpikeAutoOutput {
        public SpikeAutoOutput(StarDetectionResult analysisResult, MeasureAndError spike) {
            AnalysisResult = analysisResult;
            Spike = spike;
        }

        public StarDetectionResult AnalysisResult { get; }
        public MeasureAndError Spike { get; }
    }

    // ========================================================
    // Per-star spike point (for annotation)
    // ========================================================
    public sealed class SpikeStarPoint {
        public double X { get; set; }      // image coordinates
        public double Y { get; set; }
        public double Metric { get; set; } // per-star J
        public int BoxSizePx { get; set; }
    }

    // ========================================================
    // Tracking state
    // ========================================================
    public sealed class SpikeTrackingState {
        public List<TrackedStar> TrackedStars { get; set; } = new List<TrackedStar>();
        public int FrameIndex { get; set; } = 0;

        public int LastUsedStars { get; set; } = 0;
        public List<SpikeStarPoint> LastStarPoints { get; set; } = new List<SpikeStarPoint>();

        public int MaxMissedFrames { get; set; } = 12;
    }

    public sealed class TrackedStar {
        public float X { get; set; }
        public float Y { get; set; }
        public int BaseSizePx { get; set; }

        public int LastSeenFrameIndex { get; set; } = 0;
        public int MissCount { get; set; } = 0;
    }

    // ========================================================
    // Parameters
    // ========================================================
    public sealed class SpikeAnalysisParams {
        // ROI & background
        public double roiScale { get; set; } = 2.0;
        public double bgRingFraction { get; set; } = 0.7;

        // Star selection (initial seed)
        public double minStarSizePx { get; set; } = 6;
        public int maxStarS { get; set; } = 5;

        // User-provided spike angle (deg)
        public double spikeAngleDeg { get; set; } = 90.0;

        // Local u-gaussian sigma (tau)
        public double coreSigmaPx { get; set; } = 1.5;

        // Radial core suppression strength
        public double coreRejectSigmaPx { get; set; } = 4.0;

        // Along-axis window (s)
        public double axisSigmaPx { get; set; } = 25.0;

        // Reject center along s
        public double axisRejectSigmaPx { get; set; } = 6.0;

        // Metric weights
        public double betaVar { get; set; } = 1.0;
        public double betaSplit { get; set; } = 4.0;

        // Split penalty power p (>= 1), recommend 2
        public double splitPower { get; set; } = 2.0;

        // Numeric stability for kurtosis
        public double kurtosisEps { get; set; } = 1e-6;

        // Tracking / centroid refinement
        public bool enableCentroidTracking { get; set; } = true;
        public int centroidWindowPx { get; set; } = 17;       // odd recommended
        public double centroidThreshK { get; set; } = 3.0;    // threshold = median + k*MAD
        public double maxCentroidShiftPx { get; set; } = 30.0; // clamp per frame

        // Frame validity
        public int minUsedStarsForValidFrame { get; set; } = 2;
    }

    // ========================================================
    // Analyzer
    // ========================================================
    public static class SpikeAnalyzer {
        // ====================================================
        // ✅ 외부에서 매 프레임 호출
        //
        // 핵심 정책:
        // 1) UI가 그리는 StarDetectionResult는 대개 "원본 analysisResult"
        // 2) 그래서 새 StarDetectionResult를 만들지 않고,
        //    "원본 analysisResult"에 spike용 StarList/값을 덮어쓴다.
        //
        // 이렇게 해야 annotation이 확실히 그려진다.
        // ====================================================
        public static SpikeAutoOutput TryCalculateSpikeMetricAuto(
            IImageData imageData,
            SpikeAnalysisParams param,
            StarDetectionResult analysisResult,
            ref SpikeTrackingState trackingState) {
            if (param == null) param = new SpikeAnalysisParams();

            if (imageData == null)
                return new SpikeAutoOutput(analysisResult, new MeasureAndError { Measure = -1, Stdev = 0 });

            if (analysisResult == null)
                return new SpikeAutoOutput(null, new MeasureAndError { Measure = -2, Stdev = 0 });

            // Seed if needed
            if (trackingState == null || trackingState.TrackedStars == null || trackingState.TrackedStars.Count == 0) {
                trackingState = InitializeTrackingState(param, analysisResult);
                if (trackingState == null)
                    return new SpikeAutoOutput(analysisResult, new MeasureAndError { Measure = -3, Stdev = 0 });
            }

            // 1) compute
            double spread;
            double metric = TryCalculateSpikeMetricTracked(imageData, param, trackingState, out spread);

            bool needReseed =
                metric < 0 ||
                trackingState.LastUsedStars < Math.Max(1, param.minUsedStarsForValidFrame);

            // 2) reseed once if needed
            if (needReseed) {
                var reseed = InitializeTrackingState(param, analysisResult);
                if (reseed != null) {
                    trackingState = reseed;
                    metric = TryCalculateSpikeMetricTracked(imageData, param, trackingState, out spread);
                }
            }

            var spike = new MeasureAndError { Measure = metric, Stdev = spread };

            // 3) ✅ 여기서 "원본 analysisResult"에 spike 결과를 직접 주입
            //    -> UI가 보는 객체가 바뀌지 않으니 annotation이 반드시 그려짐
            if (metric >= 0 && trackingState.LastStarPoints != null && trackingState.LastStarPoints.Count > 0) {
                ApplySpikeToStarDetectionResult(
                    analysisResult,
                    trackingState.LastStarPoints,
                    metric,
                    spread);
            } else {
                // 실패 시에도 "왜 안그려지는지" 디버깅에 도움이 되도록 최소 상태를 남김
                // (원하면 여기서 기존 StarList를 유지하도록 바꿔도 됩니다)
                analysisResult.DetectedStars = 0;
            }

            return new SpikeAutoOutput(analysisResult, spike);
        }

        // ====================================================
        // Seed tracking state (select stars once)
        // ====================================================
        public static SpikeTrackingState InitializeTrackingState(
            SpikeAnalysisParams param,
            StarDetectionResult starResult) {
            if (starResult?.StarList == null || starResult.StarList.Count == 0)
                return null;

            var stars = SelectStars(starResult, param);
            if (stars.Count == 0)
                return null;

            var tracked = stars.Select(s => {
                int bb = Math.Max(s.BoundingBox.Width, s.BoundingBox.Height);
                int baseSize = Math.Clamp(bb, 6, 60);

                return new TrackedStar {
                    X = s.Position.X,
                    Y = s.Position.Y,
                    BaseSizePx = baseSize,
                    LastSeenFrameIndex = 0,
                    MissCount = 0
                };
            }).ToList();

            return new SpikeTrackingState { TrackedStars = tracked, FrameIndex = 0 };
        }

        private static List<DetectedStar> SelectStars(StarDetectionResult result, SpikeAnalysisParams param) {
            var list = result?.StarList;
            if (list == null || list.Count == 0)
                return new List<DetectedStar>();

            int topN = Math.Max(1, list.Count / 5);
            var brightThreshold = list
                .OrderByDescending(s => s.MaxBrightness)
                .Take(topN)
                .Last()
                .MaxBrightness;

            return list
                .Where(s =>
                    s.MaxBrightness >= brightThreshold &&
                    s.BoundingBox.Width >= param.minStarSizePx &&
                    s.BoundingBox.Height >= param.minStarSizePx &&
                    Math.Abs(s.BoundingBox.Width - s.BoundingBox.Height)
                        <= Math.Min(s.BoundingBox.Width, s.BoundingBox.Height) * 0.5
                )
                .OrderByDescending(s => s.MaxBrightness)
                .Take(Math.Max(1, param.maxStarS))
                .ToList();
        }

        // ====================================================
        // ✅ 원본 analysisResult에 Spike용 데이터를 "직접" 주입
        // - StarList: spike에 실제 사용된 별 좌표로 교체
        // - HFR: 별 HFR 자리에 per-star spike metric 넣기
        // - AverageHFR/HFRStdDev: 프레임 대표값/분산 넣기
        //
        // 이게 "annotation이 안그려짐" 문제를 가장 확실하게 끝냅니다.
        // ====================================================
        private static void ApplySpikeToStarDetectionResult(
            StarDetectionResult target,
            List<SpikeStarPoint> starPoints,
            double frameMetricMedian,
            double frameSpread) {
            if (target == null) return;

            // 1) 새 데이터 준비
            var newStars = new List<DetectedStar>(starPoints.Count);
            var newBright = new List<Accord.Point>(starPoints.Count);

            foreach (var p in starPoints) {
                int px = (int)Math.Round(p.X);
                int py = (int)Math.Round(p.Y);
                int size = Math.Clamp(p.BoxSizePx, 8, 160); // 안전장치
                int half = size / 2;
                newStars.Add(new DetectedStar {
                    Position = new Accord.Point((float)p.X, (float)p.Y),
                    HFR = p.Metric, // spike metric

                    // 표시 필터를 통과시키기 위한 값들 (0이면 걸러질 수 있음)
                    MaxBrightness = 1_000_000,
                    AverageBrightness = 10,
                    Background = 0,

                    BoundingBox = new Rectangle(px - half, py - half, size, size)
                });

                newBright.Add(new Accord.Point((float)p.X, (float)p.Y));
            }

            // 2) ✅ "리스트 교체" 금지 — 기존 리스트를 갱신
            if (target.StarList == null) {
                target.StarList = newStars;
            } else {
                target.StarList.Clear();
                target.StarList.AddRange(newStars);
            }

            if (target.BrightestStarPositions == null) {
                target.BrightestStarPositions = newBright;
            } else {
                target.BrightestStarPositions.Clear();
                target.BrightestStarPositions.AddRange(newBright);
            }

            // 3) NINA가 화면/통계에 쓰는 값들
            target.DetectedStars = newStars.Count;
            target.AverageHFR = frameMetricMedian;
            target.HFRStdDev = frameSpread;
        }

        // ====================================================
        // Tracking-based metric per frame
        // - ROI at last known position
        // - background removal
        // - centroid refine (optional)
        // - compute per-star spike metric (centroid 오프셋 반영)
        // - store per-star points (for annotation)
        // - return median + spread
        // ====================================================
        private static double TryCalculateSpikeMetricTracked(
            IImageData imageData,
            SpikeAnalysisParams param,
            SpikeTrackingState state,
            out double starSpread) {
            starSpread = 0;
            state.LastStarPoints = new List<SpikeStarPoint>();

            if (imageData == null || state?.TrackedStars == null || state.TrackedStars.Count == 0)
                return -10;

            PruneOldTracks(state);

            var metrics = new List<double>(state.TrackedStars.Count);
            var perStar = new List<SpikeStarPoint>(state.TrackedStars.Count);

            foreach (var t in state.TrackedStars) {
                if (!TryExtractROIAt(imageData, t.X, t.Y, t.BaseSizePx, param,
                    out float[,] roi, out int roiCenterX, out int roiCenterY)) {
                    t.MissCount++;
                    continue;
                }

                RemoveBackground(roi, param);

                // centroid refine
                double cxOff = 0, cyOff = 0;
                if (param.enableCentroidTracking) {
                    if (TryRefineCentroid(roi, param, out double dx, out double dy)) {
                        cxOff = dx;
                        cyOff = dy;

                        // 이미지 좌표로 update
                        t.X = (float)(roiCenterX + dx);
                        t.Y = (float)(roiCenterY + dy);
                        t.LastSeenFrameIndex = state.FrameIndex;
                        t.MissCount = 0;
                    } else {
                        t.MissCount++;
                    }
                } else {
                    t.LastSeenFrameIndex = state.FrameIndex;
                    t.MissCount = 0;
                }

                // ✅ centroid offset 반영하여 metric 계산
                if (TryComputeSpikeMetric(roi, param, cxOff, cyOff, out double value)) {
                    metrics.Add(value);
                    perStar.Add(new SpikeStarPoint {
                        X = t.X,
                        Y = t.Y,
                        Metric = value,
                        BoxSizePx = Math.Clamp((int)Math.Round(t.BaseSizePx * 2.0), 12, 120) // ✅ 원하는 스케일로
                    });
                } else {
                    t.MissCount++;
                }
            }

            state.FrameIndex++;
            state.LastUsedStars = metrics.Count;
            state.LastStarPoints = perStar;

            if (metrics.Count == 0)
                return -20;

            double result = Median(metrics);
            starSpread = StdDevSample(metrics);
            return result;
        }

        private static void PruneOldTracks(SpikeTrackingState state) {
            if (state?.TrackedStars == null) return;

            int maxMiss = Math.Max(1, state.MaxMissedFrames);
            state.TrackedStars = state.TrackedStars
                .Where(t =>
                    t.MissCount <= maxMiss &&
                    (state.FrameIndex - t.LastSeenFrameIndex) <= (maxMiss + 2))
                .ToList();
        }

        // ====================================================
        // ROI extraction
        // ====================================================
        private static bool TryExtractROIAt(
            IImageData imageData,
            float x,
            float y,
            int baseSizePx,
            SpikeAnalysisParams param,
            out float[,] roi,
            out int roiCenterX,
            out int roiCenterY) {
            roi = null;
            roiCenterX = 0;
            roiCenterY = 0;

            int width = imageData.Properties.Width;
            int height = imageData.Properties.Height;

            // ✅ 원본 코드와 동일하게 ushort[]로 읽되, FlatArray가 ushort[]가 아닐 가능성까지 방어
            // (여기서 타입이 안 맞으면 ROI가 0으로 채워지고 metric이 0/실패로 흐릅니다.)
            if (imageData.Data?.FlatArray is not ushort[] data)
                return false;

            int cx = (int)Math.Round(x);
            int cy = (int)Math.Round(y);

            int hs = (int)Math.Round(baseSizePx * param.roiScale);
            hs = Math.Clamp(hs, (int)Math.Round(baseSizePx * 1.2), (int)Math.Round(baseSizePx * 3.5));

            hs = Math.Min(hs, Math.Min(width, height) / 6);
            hs = Math.Max(hs, 12);

            if (cx < hs || cy < hs || cx + hs >= width || cy + hs >= height)
                return false;

            int size = hs * 2;
            var tmp = new float[size, size];

            for (int yy = -hs; yy < hs; yy++) {
                int py = cy + yy;
                int row = py * width;
                int ry = yy + hs;

                for (int xx = -hs; xx < hs; xx++) {
                    int px = cx + xx;
                    tmp[ry, xx + hs] = data[row + px];
                }
            }

            roi = tmp;
            roiCenterX = cx;
            roiCenterY = cy;
            return true;
        }

        // ====================================================
        // Background removal (ring median)
        // ====================================================
        private static void RemoveBackground(float[,] roi, SpikeAnalysisParams param) {
            int size = roi.GetLength(0);
            int c = size / 2;

            double rMin = (size * 0.5) * param.bgRingFraction;
            double rMin2 = rMin * rMin;

            var samples = new List<float>(size * size / 3);

            for (int y = 0; y < size; y++) {
                double dy = y - c;
                for (int x = 0; x < size; x++) {
                    double dx = x - c;
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
        // Centroid refinement
        // ====================================================
        private static bool TryRefineCentroid(float[,] roi, SpikeAnalysisParams param, out double dx, out double dy) {
            dx = 0;
            dy = 0;

            int size = roi.GetLength(0);
            double center = (size - 1) * 0.5;

            int win = Math.Max(5, param.centroidWindowPx);
            if (win % 2 == 0) win += 1;
            int half = win / 2;

            int cx = (int)Math.Round(center);
            int cy = (int)Math.Round(center);

            int x0 = Math.Max(0, cx - half);
            int x1 = Math.Min(size - 1, cx + half);
            int y0 = Math.Max(0, cy - half);
            int y1 = Math.Min(size - 1, cy + half);

            var winSamples = new List<double>((x1 - x0 + 1) * (y1 - y0 + 1));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    winSamples.Add(roi[y, x]);

            if (winSamples.Count < 10)
                return false;

            double med = Median(winSamples);
            double mad = MAD(winSamples);

            double thr = med + param.centroidThreshK * mad;
            if (mad <= 0) thr = med + 1e-6;

            double sumW = 0;
            double sumX = 0;
            double sumY = 0;

            for (int y = y0; y <= y1; y++) {
                for (int x = x0; x <= x1; x++) {
                    double v = roi[y, x];
                    if (v <= thr) continue;

                    double w = v - thr;
                    sumW += w;
                    sumX += w * x;
                    sumY += w * y;
                }
            }

            if (sumW <= 0)
                return false;

            double cxNew = sumX / sumW;
            double cyNew = sumY / sumW;

            dx = cxNew - center;
            dy = cyNew - center;

            double maxJump = Math.Max(1.0, param.maxCentroidShiftPx);
            dx = Math.Clamp(dx, -maxJump, maxJump);
            dy = Math.Clamp(dy, -maxJump, maxJump);

            return true;
        }

        // ====================================================
        // Core metric computation
        // centroid offset 반영 버전
        // ====================================================
        private static bool TryComputeSpikeMetric(
            float[,] roi,
            SpikeAnalysisParams param,
            double centerOffsetX,
            double centerOffsetY,
            out double J) {
            J = 0;

            int size = roi.GetLength(0);

            double baseCenter = (size - 1) * 0.5;
            double centerX = baseCenter + centerOffsetX;
            double centerY = baseCenter + centerOffsetY;

            double theta = param.spikeAngleDeg * Math.PI / 180.0;
            double cosT = Math.Cos(theta);
            double sinT = Math.Sin(theta);

            const double eps = 1e-12;
            const double minVar = 1e-10;

            double r0 = Math.Max(0.5, param.coreRejectSigmaPx);
            double inv2r02 = 1.0 / (2.0 * r0 * r0);

            double sSigma = Math.Max(1.0, param.axisSigmaPx);
            double inv2sSig2 = 1.0 / (2.0 * sSigma * sSigma);

            double sReject = Math.Max(0.5, param.axisRejectSigmaPx);
            double inv2sRej2 = 1.0 / (2.0 * sReject * sReject);

            double tau = Math.Max(0.25, param.coreSigmaPx);
            double inv2tau2 = 1.0 / (2.0 * tau * tau);

            double totalW = 0;
            var samples = new List<(double u, double w)>(size * size / 2);

            for (int y = 0; y < size; y++) {
                double dy = y - centerY;
                for (int x = 0; x < size; x++) {
                    double I = roi[y, x];
                    if (I <= 0) continue;

                    double dx = x - centerX;

                    double s = dx * cosT + dy * sinT;
                    double u = -dx * sinT + dy * cosT;

                    double r2 = dx * dx + dy * dy;

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

            double sumWU = 0;
            for (int i = 0; i < samples.Count; i++)
                sumWU += samples[i].w * samples[i].u;
            double meanU = sumWU / totalW;

            double sumWVar = 0;
            double sumWM4 = 0;
            for (int i = 0; i < samples.Count; i++) {
                double d = samples[i].u - meanU;
                double d2 = d * d;
                sumWVar += samples[i].w * d2;
                sumWM4 += samples[i].w * d2 * d2;
            }

            double varG = sumWVar / totalW;
            varG = Math.Max(varG, minVar);

            double m4 = sumWM4 / totalW;

            double wSumLocal = 0;
            double sumLocal = 0;
            for (int i = 0; i < samples.Count; i++) {
                double d = samples[i].u - meanU;
                double wl = Math.Exp(-d * d * inv2tau2);
                double w = samples[i].w * wl;

                wSumLocal += w;
                sumLocal += w * d * d;
            }

            if (wSumLocal <= 0)
                return false;

            double varC = sumLocal / wSumLocal;
            varC = Math.Max(varC, minVar);

            double kurt = m4 / (varG * varG + eps);

            double splitBase = 1.0 / (kurt + param.kurtosisEps);
            double p = Math.Max(1.0, param.splitPower);
            double splitPenalty = Math.Pow(splitBase, p);

            J = param.betaVar * varC + param.betaSplit * splitPenalty;

            if (double.IsNaN(J) || double.IsInfinity(J))
                return false;

            return true;
        }

        // ====================================================
        // Utilities
        // ====================================================
        public static double Median(List<double> values) {
            if (values == null || values.Count == 0) return 0.0;
            var arr = values.OrderBy(v => v).ToArray();
            int n = arr.Length;
            return n % 2 == 1 ? arr[n / 2] : 0.5 * (arr[n / 2 - 1] + arr[n / 2]);
        }

        private static double Median(List<double> values, bool _) => Median(values);

        private static double Median(List<double> values, int __) => Median(values);

        private static double Median(IEnumerable<double> values)
            => Median(values?.ToList() ?? new List<double>());

        private static double MAD(List<double> values) {
            if (values == null || values.Count == 0) return 0.0;
            double median = Median(values);
            var dev = values.Select(v => Math.Abs(v - median)).ToList();
            return Median(dev);
        }

        private static float Median(List<float> values) {
            if (values == null || values.Count == 0) return 0.0f;
            var arr = values.OrderBy(v => v).ToArray();
            int n = arr.Length;
            return n % 2 == 1 ? arr[n / 2] : 0.5f * (arr[n / 2 - 1] + arr[n / 2]);
        }

        private static double StdDevSample(List<double> values) {
            if (values == null || values.Count < 2) return 0.0;
            double mean = values.Average();
            double var = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
            return Math.Sqrt(var);
        }
    }
}