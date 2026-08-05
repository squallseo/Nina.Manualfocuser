using System;
using System.Collections.Generic;
using System.Linq;

namespace Cwseo.NINA.ManualFocuser.Models {

    // ========================================================
    // Parameters
    //
    // NOTE: this type deliberately has NO dependency on N.I.N.A.
    // so that the same code can be driven from the offline batch
    // evaluator (Tools/SpikeBatch) and from the plugin.
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
        public int centroidWindowPx { get; set; } = 17;        // odd recommended
        public double centroidThreshK { get; set; } = 3.0;     // threshold = median + k*MAD
        public double maxCentroidShiftPx { get; set; } = 30.0; // clamp per frame

        // Frame validity
        public int minUsedStarsForValidFrame { get; set; } = 2;

        public SpikeAnalysisParams Clone() => (SpikeAnalysisParams)MemberwiseClone();
    }

    // ========================================================
    // Seed star handed in by whatever star detector is upstream
    // ========================================================
    public sealed class SpikeSeedStar {
        public double X { get; set; }
        public double Y { get; set; }
        public int WidthPx { get; set; }
        public int HeightPx { get; set; }
        public double MaxBrightness { get; set; }
    }

    // ========================================================
    // Per-star spike point (for annotation / diagnostics)
    // ========================================================
    public sealed class SpikeStarPoint {
        public double X { get; set; }      // image coordinates
        public double Y { get; set; }
        public double Metric { get; set; } // per-star J
        public int BoxSizePx { get; set; }

        // diagnostics: the individual terms behind J
        public double VarC { get; set; }
        public double VarG { get; set; }
        public double Kurtosis { get; set; }
    }

    public sealed class TrackedStar {
        public double X { get; set; }
        public double Y { get; set; }
        public int BaseSizePx { get; set; }

        public int LastSeenFrameIndex { get; set; } = 0;
        public int MissCount { get; set; } = 0;
    }

    public sealed class SpikeTrackingState {
        public List<TrackedStar> TrackedStars { get; set; } = new List<TrackedStar>();
        public int FrameIndex { get; set; } = 0;

        public int LastUsedStars { get; set; } = 0;
        public List<SpikeStarPoint> LastStarPoints { get; set; } = new List<SpikeStarPoint>();

        public int MaxMissedFrames { get; set; } = 12;
    }

    public enum SpikeStatus {
        Ok = 0,
        Disabled,
        NoImage,
        NoSeedStars,
        NoTrackedStars,
        NoValidStars,
        TooFewStars
    }

    // ========================================================
    // Result of evaluating a single frame.
    //
    // Metric is double.NaN when Status != Ok. It is never a
    // negative sentinel - a sentinel that lands on a plot axis
    // destroys the scale and hides the real curve.
    // ========================================================
    public readonly struct SpikeFrameResult {
        public SpikeFrameResult(SpikeStatus status, double metric, double spread, int usedStars,
                                double medVarC, double medVarG, double medKurtosis,
                                List<SpikeStarPoint> starPoints) {
            Status = status;
            Metric = metric;
            Spread = spread;
            UsedStars = usedStars;
            MedianVarC = medVarC;
            MedianVarG = medVarG;
            MedianKurtosis = medKurtosis;
            StarPoints = starPoints ?? new List<SpikeStarPoint>();
        }

        public SpikeStatus Status { get; }
        public double Metric { get; }
        public double Spread { get; }
        public int UsedStars { get; }
        public double MedianVarC { get; }
        public double MedianVarG { get; }
        public double MedianKurtosis { get; }
        public List<SpikeStarPoint> StarPoints { get; }

        public bool IsValid => Status == SpikeStatus.Ok && !double.IsNaN(Metric);

        public static SpikeFrameResult Failed(SpikeStatus status)
            => new SpikeFrameResult(status, double.NaN, 0, 0, double.NaN, double.NaN, double.NaN, null);
    }

    // ========================================================
    // Pure image-space spike analysis.
    //
    // Everything operates on a plain 16 bit mono buffer so the
    // exact same code path runs in N.I.N.A. and in the offline
    // batch evaluator.
    // ========================================================
    public static class SpikeCore {

        // ====================================================
        // Seed selection from an upstream star list
        // ====================================================
        public static SpikeTrackingState CreateTrackingState(
            IReadOnlyList<SpikeSeedStar> seeds,
            SpikeAnalysisParams param) {

            if (seeds == null || seeds.Count == 0) return null;
            param ??= new SpikeAnalysisParams();

            var selected = SelectSeeds(seeds, param);
            if (selected.Count == 0) return null;

            var tracked = selected.Select(s => new TrackedStar {
                X = s.X,
                Y = s.Y,
                BaseSizePx = Math.Clamp(Math.Max(s.WidthPx, s.HeightPx), 6, 60),
                LastSeenFrameIndex = 0,
                MissCount = 0
            }).ToList();

            return new SpikeTrackingState { TrackedStars = tracked, FrameIndex = 0 };
        }

        private static List<SpikeSeedStar> SelectSeeds(IReadOnlyList<SpikeSeedStar> list, SpikeAnalysisParams param) {
            if (list == null || list.Count == 0) return new List<SpikeSeedStar>();

            int topN = Math.Max(1, list.Count / 5);
            double brightThreshold = list
                .OrderByDescending(s => s.MaxBrightness)
                .Take(topN)
                .Last()
                .MaxBrightness;

            return list
                .Where(s =>
                    s.MaxBrightness >= brightThreshold &&
                    s.WidthPx >= param.minStarSizePx &&
                    s.HeightPx >= param.minStarSizePx &&
                    Math.Abs(s.WidthPx - s.HeightPx) <= Math.Min(s.WidthPx, s.HeightPx) * 0.5)
                .OrderByDescending(s => s.MaxBrightness)
                .Take(Math.Max(1, param.maxStarS))
                .ToList();
        }

        // ====================================================
        // Evaluate one frame against an existing tracking state
        // ====================================================
        public static SpikeFrameResult Evaluate(
            ushort[] data,
            int width,
            int height,
            SpikeAnalysisParams param,
            SpikeTrackingState state) {

            if (data == null || width <= 0 || height <= 0 || (long)width * height > data.Length)
                return SpikeFrameResult.Failed(SpikeStatus.NoImage);

            if (state?.TrackedStars == null || state.TrackedStars.Count == 0)
                return SpikeFrameResult.Failed(SpikeStatus.NoTrackedStars);

            param ??= new SpikeAnalysisParams();

            PruneOldTracks(state);

            var metrics = new List<double>(state.TrackedStars.Count);
            var varCs = new List<double>(state.TrackedStars.Count);
            var varGs = new List<double>(state.TrackedStars.Count);
            var kurts = new List<double>(state.TrackedStars.Count);
            var perStar = new List<SpikeStarPoint>(state.TrackedStars.Count);

            foreach (var t in state.TrackedStars) {
                if (!TryExtractROIAt(data, width, height, t.X, t.Y, t.BaseSizePx, param,
                                     out float[] roi, out int roiSize, out int roiCenterX, out int roiCenterY)) {
                    t.MissCount++;
                    continue;
                }

                RemoveBackground(roi, roiSize, param);

                double cxOff = 0, cyOff = 0;
                if (param.enableCentroidTracking) {
                    if (TryRefineCentroid(roi, roiSize, param, out double dx, out double dy)) {
                        cxOff = dx;
                        cyOff = dy;
                        t.X = roiCenterX + dx;
                        t.Y = roiCenterY + dy;
                        t.LastSeenFrameIndex = state.FrameIndex;
                        t.MissCount = 0;
                    } else {
                        t.MissCount++;
                    }
                } else {
                    t.LastSeenFrameIndex = state.FrameIndex;
                    t.MissCount = 0;
                }

                if (TryComputeSpikeMetric(roi, roiSize, param, cxOff, cyOff, out SpikeTerms terms)) {
                    metrics.Add(terms.J);
                    varCs.Add(terms.VarC);
                    varGs.Add(terms.VarG);
                    kurts.Add(terms.Kurtosis);

                    perStar.Add(new SpikeStarPoint {
                        X = t.X,
                        Y = t.Y,
                        Metric = terms.J,
                        VarC = terms.VarC,
                        VarG = terms.VarG,
                        Kurtosis = terms.Kurtosis,
                        BoxSizePx = Math.Clamp((int)Math.Round(t.BaseSizePx * 2.0), 12, 120)
                    });
                } else {
                    t.MissCount++;
                }
            }

            state.FrameIndex++;
            state.LastUsedStars = metrics.Count;
            state.LastStarPoints = perStar;

            if (metrics.Count == 0)
                return SpikeFrameResult.Failed(SpikeStatus.NoValidStars);

            if (metrics.Count < Math.Max(1, param.minUsedStarsForValidFrame))
                return new SpikeFrameResult(SpikeStatus.TooFewStars, double.NaN, 0, metrics.Count,
                                            Median(varCs), Median(varGs), Median(kurts), perStar);

            return new SpikeFrameResult(
                SpikeStatus.Ok,
                Median(metrics),
                StdDevSample(metrics),
                metrics.Count,
                Median(varCs),
                Median(varGs),
                Median(kurts),
                perStar);
        }

        private static void PruneOldTracks(SpikeTrackingState state) {
            if (state?.TrackedStars == null) return;

            int maxMiss = Math.Max(1, state.MaxMissedFrames);
            state.TrackedStars = state.TrackedStars
                .Where(t => t.MissCount <= maxMiss &&
                            (state.FrameIndex - t.LastSeenFrameIndex) <= (maxMiss + 2))
                .ToList();
        }

        // ====================================================
        // ROI extraction
        //
        // The ROI is a size x size square whose LOCAL centre index
        // is exactly `hs` (not (size-1)/2). Getting this wrong biases
        // both the centroid and the u-origin by half a pixel, which
        // matters at tau ~ 1.5 px.
        // ====================================================
        private static bool TryExtractROIAt(
            ushort[] data,
            int width,
            int height,
            double x,
            double y,
            int baseSizePx,
            SpikeAnalysisParams param,
            out float[] roi,
            out int size,
            out int roiCenterX,
            out int roiCenterY) {

            roi = null;
            size = 0;
            roiCenterX = 0;
            roiCenterY = 0;

            int cx = (int)Math.Round(x);
            int cy = (int)Math.Round(y);

            int hs = (int)Math.Round(baseSizePx * param.roiScale);
            hs = Math.Clamp(hs, (int)Math.Round(baseSizePx * 1.2), (int)Math.Round(baseSizePx * 3.5));
            hs = Math.Min(hs, Math.Min(width, height) / 6);
            hs = Math.Max(hs, 12);

            if (cx < hs || cy < hs || cx + hs >= width || cy + hs >= height)
                return false;

            size = hs * 2;
            var tmp = new float[size * size];

            for (int yy = -hs; yy < hs; yy++) {
                int row = (cy + yy) * width;
                int dst = (yy + hs) * size;
                for (int xx = -hs; xx < hs; xx++)
                    tmp[dst + xx + hs] = data[row + cx + xx];
            }

            roi = tmp;
            roiCenterX = cx;
            roiCenterY = cy;
            return true;
        }

        // ====================================================
        // Background removal (ring median)
        // ====================================================
        private static void RemoveBackground(float[] roi, int size, SpikeAnalysisParams param) {
            double c = size / 2.0;
            double rMin = (size * 0.5) * param.bgRingFraction;
            double rMin2 = rMin * rMin;

            var samples = new List<float>(size * size / 3);

            for (int y = 0; y < size; y++) {
                double dy = y - c;
                int row = y * size;
                for (int x = 0; x < size; x++) {
                    double dx = x - c;
                    if (dx * dx + dy * dy >= rMin2)
                        samples.Add(roi[row + x]);
                }
            }

            if (samples.Count == 0) return;

            float bg = MedianF(samples);
            for (int i = 0; i < roi.Length; i++)
                roi[i] = Math.Max(0f, roi[i] - bg);
        }

        // ====================================================
        // Centroid refinement
        // ====================================================
        private static bool TryRefineCentroid(float[] roi, int size, SpikeAnalysisParams param, out double dx, out double dy) {
            dx = 0;
            dy = 0;

            int center = size / 2;   // exact local index of the ROI centre

            int win = Math.Max(5, param.centroidWindowPx);
            if (win % 2 == 0) win += 1;
            int half = win / 2;

            int x0 = Math.Max(0, center - half);
            int x1 = Math.Min(size - 1, center + half);
            int y0 = Math.Max(0, center - half);
            int y1 = Math.Min(size - 1, center + half);

            var winSamples = new List<double>((x1 - x0 + 1) * (y1 - y0 + 1));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    winSamples.Add(roi[y * size + x]);

            if (winSamples.Count < 10) return false;

            double med = Median(winSamples);
            double mad = MAD(winSamples);

            double thr = mad > 0 ? med + param.centroidThreshK * mad : med + 1e-6;

            double sumW = 0, sumX = 0, sumY = 0;
            for (int y = y0; y <= y1; y++) {
                int row = y * size;
                for (int x = x0; x <= x1; x++) {
                    double v = roi[row + x];
                    if (v <= thr) continue;
                    double w = v - thr;
                    sumW += w;
                    sumX += w * x;
                    sumY += w * y;
                }
            }

            if (sumW <= 0) return false;

            double maxJump = Math.Max(1.0, param.maxCentroidShiftPx);
            dx = Math.Clamp(sumX / sumW - center, -maxJump, maxJump);
            dy = Math.Clamp(sumY / sumW - center, -maxJump, maxJump);
            return true;
        }

        // ====================================================
        // Core metric computation
        // ====================================================
        private readonly struct SpikeTerms {
            public SpikeTerms(double j, double varC, double varG, double kurt) {
                J = j; VarC = varC; VarG = varG; Kurtosis = kurt;
            }
            public double J { get; }
            public double VarC { get; }
            public double VarG { get; }
            public double Kurtosis { get; }
        }

        private static bool TryComputeSpikeMetric(
            float[] roi,
            int size,
            SpikeAnalysisParams param,
            double centerOffsetX,
            double centerOffsetY,
            out SpikeTerms terms) {

            terms = default;

            double baseCenter = size / 2;  // matches TryExtractROIAt / TryRefineCentroid
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
            int n = 0;
            var us = new double[size * size];
            var ws = new double[size * size];

            for (int y = 0; y < size; y++) {
                double dy = y - centerY;
                int row = y * size;
                for (int x = 0; x < size; x++) {
                    double I = roi[row + x];
                    if (I <= 0) continue;

                    double dx = x - centerX;

                    double s = dx * cosT + dy * sinT;
                    double u = -dx * sinT + dy * cosT;
                    double r2 = dx * dx + dy * dy;

                    double wCore = 1.0 - Math.Exp(-r2 * inv2r02);
                    double wAxis = Math.Exp(-s * s * inv2sSig2) * (1.0 - Math.Exp(-s * s * inv2sRej2));

                    double w = I * wCore * wAxis;
                    if (w <= 0) continue;

                    us[n] = u;
                    ws[n] = w;
                    n++;
                    totalW += w;
                }
            }

            if (totalW <= 0 || n < 50) return false;

            double sumWU = 0;
            for (int i = 0; i < n; i++) sumWU += ws[i] * us[i];
            double meanU = sumWU / totalW;

            double sumWVar = 0, sumWM4 = 0;
            for (int i = 0; i < n; i++) {
                double d = us[i] - meanU;
                double d2 = d * d;
                sumWVar += ws[i] * d2;
                sumWM4 += ws[i] * d2 * d2;
            }

            double varG = Math.Max(sumWVar / totalW, minVar);
            double m4 = sumWM4 / totalW;

            double wSumLocal = 0, sumLocal = 0;
            for (int i = 0; i < n; i++) {
                double d = us[i] - meanU;
                double w = ws[i] * Math.Exp(-d * d * inv2tau2);
                wSumLocal += w;
                sumLocal += w * d * d;
            }

            if (wSumLocal <= 0) return false;

            double varC = Math.Max(sumLocal / wSumLocal, minVar);
            double kurt = m4 / (varG * varG + eps);

            double splitBase = 1.0 / (kurt + param.kurtosisEps);
            double p = Math.Max(1.0, param.splitPower);
            double splitPenalty = Math.Pow(splitBase, p);

            double J = param.betaVar * varC + param.betaSplit * splitPenalty;

            if (double.IsNaN(J) || double.IsInfinity(J)) return false;

            terms = new SpikeTerms(J, varC, varG, kurt);
            return true;
        }

        // ====================================================
        // Utilities
        // ====================================================
        public static double Median(IReadOnlyList<double> values) {
            if (values == null || values.Count == 0) return double.NaN;
            var arr = values.ToArray();
            Array.Sort(arr);
            int n = arr.Length;
            return n % 2 == 1 ? arr[n / 2] : 0.5 * (arr[n / 2 - 1] + arr[n / 2]);
        }

        private static float MedianF(List<float> values) {
            if (values == null || values.Count == 0) return 0f;
            var arr = values.ToArray();
            Array.Sort(arr);
            int n = arr.Length;
            return n % 2 == 1 ? arr[n / 2] : 0.5f * (arr[n / 2 - 1] + arr[n / 2]);
        }

        private static double MAD(IReadOnlyList<double> values) {
            if (values == null || values.Count == 0) return 0.0;
            double median = Median(values);
            var dev = new double[values.Count];
            for (int i = 0; i < values.Count; i++) dev[i] = Math.Abs(values[i] - median);
            return Median(dev);
        }

        public static double StdDevSample(IReadOnlyList<double> values) {
            if (values == null || values.Count < 2) return 0.0;
            double mean = values.Average();
            double var = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
            return Math.Sqrt(var);
        }
    }
}
