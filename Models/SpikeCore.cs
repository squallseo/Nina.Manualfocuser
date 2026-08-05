using System;
using System.Collections.Generic;
using System.Linq;

namespace Cwseo.NINA.ManualFocuser.Models {

    /// <summary>
    /// Which quantity the focus metric reports. Everything except Legacy is derived
    /// from the same flux profile across the spike, so they can be compared against
    /// each other on one pass over the data.
    /// </summary>
    public enum SpikeMetricKind {
        /// <summary>betaVar*varC + betaSplit*(1/kurtosis)^p. The original formulation.</summary>
        Legacy = 0,

        /// <summary>RMS width of the profile, in pixels.</summary>
        Sigma,

        /// <summary>Flux weighted mean |u| - the one dimensional analogue of HFR, in pixels.</summary>
        Hfw,

        /// <summary>Full width at half maximum of the profile envelope, in pixels.</summary>
        Fwhm,

        /// <summary>Separation of the two outer peaks, zero while the spike is single.</summary>
        Split,

        /// <summary>Hfw + splitWeight * separation. Smooth near focus, sharper once split.</summary>
        Hybrid
    }

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

        // Measure the spike orientation from the image and use it instead of
        // spikeAngleDeg. The orientation is always measured and reported; this
        // only controls whether the metric acts on it.
        public bool autoSpikeAngle { get; set; } = false;

        // Peak-over-mean a directional profile must reach before it counts as a
        // spike rather than noise.
        public double angleMinStrength { get; set; } = 1.15;

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

        // ---- profile based metrics ----

        public SpikeMetricKind metricKind { get; set; } = SpikeMetricKind.Legacy;

        /// <summary>Half range of the u profile, in pixels. Must cover the split.</summary>
        public double uMaxPx { get; set; } = 40.0;

        /// <summary>
        /// Fraction of the profile at each end treated as baseline. Clipping negative
        /// residuals to zero leaves a positive noise pedestal that dominates any
        /// moment taken over a wide u range, so it has to be removed explicitly.
        /// </summary>
        public double profileBaselineFraction { get; set; } = 0.15;

        /// <summary>Weight of the split separation term in the hybrid metric.</summary>
        public double splitWeight { get; set; } = 1.0;

        /// <summary>A local maximum must reach this fraction of the peak to count.</summary>
        public double peakThresholdFraction { get; set; } = 0.35;

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

        /// <summary>Spike orientation measured on this star, or NaN.</summary>
        public double AngleDeg { get; set; } = double.NaN;

        // Profile shape across the spike, all in pixels except DipDepth.
        public double Sigma { get; set; } = double.NaN;
        public double Hfw { get; set; } = double.NaN;
        public double Fwhm { get; set; } = double.NaN;
        public double Separation { get; set; } = double.NaN;
        public double DipDepth { get; set; } = double.NaN;
        public double ProfileSnr { get; set; } = double.NaN;
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

        // Running spike orientation. Kept across frames so the reported angle does
        // not flip between the two axes of a four vane spider.
        public double LastAngleDeg { get; set; } = double.NaN;
        public double LastAngleStrength { get; set; } = 0;
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
    public sealed class SpikeFrameResult {
        public SpikeStatus Status { get; init; } = SpikeStatus.Ok;
        public double Metric { get; init; } = double.NaN;
        public double Spread { get; init; } = 0;
        public int UsedStars { get; init; } = 0;
        public double MedianVarC { get; init; } = double.NaN;
        public double MedianVarG { get; init; } = double.NaN;
        public double MedianKurtosis { get; init; } = double.NaN;

        // Profile shape, computed for every metric kind so a frame stays diagnosable.
        public double MedianSigma { get; init; } = double.NaN;
        public double MedianHfw { get; init; } = double.NaN;
        public double MedianFwhm { get; init; } = double.NaN;
        public double MedianSeparation { get; init; } = double.NaN;
        public double MedianDipDepth { get; init; } = double.NaN;
        public double MedianProfileSnr { get; init; } = double.NaN;

        /// <summary>Spike orientation measured from this frame, or NaN.</summary>
        public double MeasuredAngleDeg { get; init; } = double.NaN;

        /// <summary>Peak over mean of the directional profile behind MeasuredAngleDeg.</summary>
        public double AngleStrength { get; init; } = 0;

        /// <summary>The angle the metric was actually computed with.</summary>
        public double UsedAngleDeg { get; init; } = double.NaN;

        public List<SpikeStarPoint> StarPoints { get; init; } = new List<SpikeStarPoint>();

        public bool IsValid => Status == SpikeStatus.Ok && !double.IsNaN(Metric);
        public bool HasAngleEstimate => !double.IsNaN(MeasuredAngleDeg);

        public static SpikeFrameResult Failed(SpikeStatus status)
            => new SpikeFrameResult { Status = status };
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

        // Per-star intermediate state, kept between the two passes.
        private sealed class RoiWork {
            public TrackedStar Star;
            public float[] Roi;
            public int Size;
            public double OffX;
            public double OffY;
            public double AngleDeg = double.NaN;
            public double AngleStrength;
        }

        // ====================================================
        // Evaluate one frame against an existing tracking state
        //
        // Two passes: the first extracts every ROI and measures the spike
        // orientation, the second computes the metric. They are separate because
        // the orientation is a property of the frame, not of one star - taking
        // the median over all stars before using it keeps a single noisy star
        // from steering the whole measurement.
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

            // ---------- pass 1: ROI, background, centroid, orientation ----------
            var work = new List<RoiWork>(state.TrackedStars.Count);

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

                var item = new RoiWork { Star = t, Roi = roi, Size = roiSize, OffX = cxOff, OffY = cyOff };

                double rOuter = roiSize / 2.0 - 2.0;
                double rInner = Math.Max(Math.Max(6.0, param.coreRejectSigmaPx), roiSize * 0.15);
                if (rOuter > rInner + 4) {
                    item.AngleDeg = EstimateAngleDeg(
                        roi, roiSize,
                        roiSize / 2.0 + cxOff, roiSize / 2.0 + cyOff,
                        rInner, rOuter,
                        state.LastAngleDeg,
                        out double strength);
                    item.AngleStrength = strength;
                }

                work.Add(item);
            }

            // ---------- frame orientation ----------
            var usable = work.Where(w => !double.IsNaN(w.AngleDeg) && w.AngleStrength >= param.angleMinStrength).ToList();

            double measuredAngle = state.LastAngleDeg;
            double measuredStrength = state.LastAngleStrength;

            // Median across stars, not across frames. Averaging over frames was tried
            // and measured worse: a focus sweep spans states where the orientation is
            // crisp and states where it is not, so a time window mixes good estimates
            // with bad ones instead of averaging repeats of the same measurement.
            if (usable.Count > 0) {
                measuredAngle = CircularMedianDeg(usable.Select(w => w.AngleDeg).ToList(), state.LastAngleDeg);
                measuredStrength = Median(usable.Select(w => w.AngleStrength).ToList());

                state.LastAngleDeg = measuredAngle;
                state.LastAngleStrength = measuredStrength;
            }

            double usedAngle = param.spikeAngleDeg;
            if (param.autoSpikeAngle && !double.IsNaN(measuredAngle)) usedAngle = measuredAngle;

            // ---------- pass 2: metric ----------
            var metrics = new List<double>(work.Count);
            var varCs = new List<double>(work.Count);
            var varGs = new List<double>(work.Count);
            var kurts = new List<double>(work.Count);
            var sigmas = new List<double>(work.Count);
            var hfws = new List<double>(work.Count);
            var fwhms = new List<double>(work.Count);
            var seps = new List<double>(work.Count);
            var dips = new List<double>(work.Count);
            var snrs = new List<double>(work.Count);
            var perStar = new List<SpikeStarPoint>(work.Count);

            int uMax = (int)Math.Round(Math.Clamp(param.uMaxPx, 6, 400));

            foreach (var w in work) {
                double j = double.NaN;
                var point = new SpikeStarPoint {
                    X = w.Star.X,
                    Y = w.Star.Y,
                    AngleDeg = w.AngleDeg,
                    BoxSizePx = Math.Clamp((int)Math.Round(w.Star.BaseSizePx * 2.0), 12, 120)
                };

                // The profile terms are computed for every kind, not just the selected
                // one: they cost one pass and they are what makes a bad frame
                // diagnosable after the fact.
                int uLimit = Math.Min(uMax, w.Size / 2 - 1);
                var profile = BuildUProfile(w.Roi, w.Size, param, usedAngle, w.OffX, w.OffY, uLimit);
                bool haveProfile = TryComputeProfileTerms(profile, param, out ProfileTerms pt);

                if (haveProfile) {
                    point.Sigma = pt.Sigma;
                    point.Hfw = pt.Hfw;
                    point.Fwhm = pt.Fwhm;
                    point.Separation = pt.Separation;
                    point.DipDepth = pt.DipDepth;
                    point.ProfileSnr = pt.ProfileSnr;
                }

                if (param.metricKind == SpikeMetricKind.Legacy) {
                    if (TryComputeSpikeMetric(w.Roi, w.Size, param, usedAngle, w.OffX, w.OffY, out SpikeTerms terms)) {
                        j = terms.J;
                        point.VarC = terms.VarC;
                        point.VarG = terms.VarG;
                        point.Kurtosis = terms.Kurtosis;
                        varCs.Add(terms.VarC);
                        varGs.Add(terms.VarG);
                        kurts.Add(terms.Kurtosis);
                    }
                } else if (haveProfile) {
                    j = SelectMetric(param.metricKind, pt, param);
                }

                if (double.IsNaN(j) || double.IsInfinity(j)) {
                    w.Star.MissCount++;
                    continue;
                }

                if (haveProfile) {
                    sigmas.Add(pt.Sigma);
                    hfws.Add(pt.Hfw);
                    fwhms.Add(pt.Fwhm);
                    seps.Add(pt.Separation);
                    dips.Add(pt.DipDepth);
                    snrs.Add(pt.ProfileSnr);
                }

                point.Metric = j;
                metrics.Add(j);
                perStar.Add(point);
            }

            state.FrameIndex++;
            state.LastUsedStars = metrics.Count;
            state.LastStarPoints = perStar;

            if (metrics.Count == 0) {
                return new SpikeFrameResult {
                    Status = SpikeStatus.NoValidStars,
                    MeasuredAngleDeg = measuredAngle,
                    AngleStrength = measuredStrength
                };
            }

            var status = metrics.Count < Math.Max(1, param.minUsedStarsForValidFrame)
                ? SpikeStatus.TooFewStars
                : SpikeStatus.Ok;

            return new SpikeFrameResult {
                Status = status,
                Metric = status == SpikeStatus.Ok ? Median(metrics) : double.NaN,
                Spread = status == SpikeStatus.Ok ? StdDevSample(metrics) : 0,
                UsedStars = metrics.Count,
                MedianVarC = Median(varCs),
                MedianVarG = Median(varGs),
                MedianKurtosis = Median(kurts),
                MedianSigma = Median(sigmas),
                MedianHfw = Median(hfws),
                MedianFwhm = Median(fwhms),
                MedianSeparation = Median(seps),
                MedianDipDepth = Median(dips),
                MedianProfileSnr = Median(snrs),
                MeasuredAngleDeg = measuredAngle,
                AngleStrength = measuredStrength,
                UsedAngleDeg = usedAngle,
                StarPoints = perStar
            };
        }

        // ====================================================
        // Profile based metrics
        //
        // Everything here works on p(u): flux projected onto the axis perpendicular
        // to the spike, with the same s-window and core suppression the legacy path
        // applies. Working from the profile rather than from raw pixel moments makes
        // the quantities directly interpretable - every one of them is in pixels -
        // and lets all candidates be computed from a single pass.
        // ====================================================

        public readonly struct ProfileTerms {
            public ProfileTerms(double sigma, double hfw, double fwhm, double separation, double dipDepth, int peaks, double snr) {
                Sigma = sigma; Hfw = hfw; Fwhm = fwhm; Separation = separation;
                DipDepth = dipDepth; PeakCount = peaks; ProfileSnr = snr;
            }

            /// <summary>RMS width of p(u), px.</summary>
            public double Sigma { get; }

            /// <summary>Flux weighted mean |u - mean|, px. The 1D analogue of HFR.</summary>
            public double Hfw { get; }

            /// <summary>Full width at half maximum of the envelope, px.</summary>
            public double Fwhm { get; }

            /// <summary>Distance between the outermost peaks, px. Zero while unimodal.</summary>
            public double Separation { get; }

            /// <summary>1 - p(centre)/p(peak). Zero when single, approaches 1 when fully split.</summary>
            public double DipDepth { get; }

            public int PeakCount { get; }

            /// <summary>Peak over baseline scatter. Below a few, the profile is noise.</summary>
            public double ProfileSnr { get; }
        }

        /// <summary>
        /// Builds p(u) on a one pixel grid. The grid spacing matters: with a spike near
        /// 0 or 90 degrees, u lands on integers, and any bin width that is not a
        /// divisor of one pixel leaves every other bin empty.
        /// </summary>
        public static double[] BuildUProfile(
            float[] roi, int size,
            SpikeAnalysisParams param,
            double angleDeg,
            double offX, double offY,
            int uMax) {

            int bins = 2 * uMax + 1;
            var sum = new double[bins];
            var count = new double[bins];

            double centerX = size / 2.0 + offX;
            double centerY = size / 2.0 + offY;

            double theta = angleDeg * Math.PI / 180.0;
            double cosT = Math.Cos(theta);
            double sinT = Math.Sin(theta);

            double r0 = Math.Max(0.5, param.coreRejectSigmaPx);
            double inv2r02 = 1.0 / (2.0 * r0 * r0);
            double sSigma = Math.Max(1.0, param.axisSigmaPx);
            double inv2sSig2 = 1.0 / (2.0 * sSigma * sSigma);
            double sReject = Math.Max(0.5, param.axisRejectSigmaPx);
            double inv2sRej2 = 1.0 / (2.0 * sReject * sReject);

            for (int y = 0; y < size; y++) {
                double dy = y - centerY;
                int row = y * size;
                for (int x = 0; x < size; x++) {
                    double I = roi[row + x];

                    double dx = x - centerX;
                    double u = -dx * sinT + dy * cosT;

                    int bin = (int)Math.Round(u) + uMax;
                    if (bin < 0 || bin >= bins) continue;

                    double s = dx * cosT + dy * sinT;
                    double r2 = dx * dx + dy * dy;

                    double wCore = 1.0 - Math.Exp(-r2 * inv2r02);
                    double wAxis = Math.Exp(-s * s * inv2sSig2) * (1.0 - Math.Exp(-s * s * inv2sRej2));
                    double w = wCore * wAxis;
                    if (w <= 0) continue;

                    // Negative residuals are kept here on purpose; they cancel the
                    // positive noise excursions instead of biasing the profile up.
                    sum[bin] += I * w;
                    count[bin] += w;
                }
            }

            var profile = new double[bins];
            for (int i = 0; i < bins; i++)
                profile[i] = count[i] > 0 ? sum[i] / count[i] : 0;
            return profile;
        }

        public static bool TryComputeProfileTerms(double[] profile, SpikeAnalysisParams param, out ProfileTerms terms) {
            terms = default;
            if (profile == null || profile.Length < 9) return false;

            int bins = profile.Length;
            int uMax = (bins - 1) / 2;

            // --- baseline from the wings ---
            int edge = Math.Max(2, (int)Math.Round(bins * Math.Clamp(param.profileBaselineFraction, 0.02, 0.4)));
            var wing = new List<double>(edge * 2);
            for (int i = 0; i < edge; i++) { wing.Add(profile[i]); wing.Add(profile[bins - 1 - i]); }

            double baseline = Median(wing);
            double noise = MAD(wing) * 1.4826;
            if (noise <= 0) noise = 1e-6;

            var p = new double[bins];
            double peak = 0;
            for (int i = 0; i < bins; i++) {
                p[i] = Math.Max(0, profile[i] - baseline);
                if (p[i] > peak) peak = p[i];
            }
            if (peak <= 0) return false;

            double snr = peak / noise;

            double w = 0, wu = 0;
            for (int i = 0; i < bins; i++) { w += p[i]; wu += p[i] * (i - uMax); }
            if (w <= 0) return false;
            double mean = wu / w;

            double m2 = 0, m1 = 0;
            for (int i = 0; i < bins; i++) {
                double d = (i - uMax) - mean;
                m2 += p[i] * d * d;
                m1 += p[i] * Math.Abs(d);
            }
            double sigma = Math.Sqrt(m2 / w);
            double hfw = m1 / w;

            // --- smoothed copy for shape features ---
            var sm = new double[bins];
            for (int i = 0; i < bins; i++) {
                double s = 0; int n = 0;
                for (int k = -1; k <= 1; k++) {
                    int j = i + k;
                    if (j < 0 || j >= bins) continue;
                    s += p[j]; n++;
                }
                sm[i] = s / n;
            }
            double smPeak = sm.Max();
            if (smPeak <= 0) return false;

            // --- FWHM of the envelope: outermost half maximum crossings ---
            double half = smPeak * 0.5;
            int lo = 0, hi = bins - 1;
            while (lo < bins && sm[lo] < half) lo++;
            while (hi >= 0 && sm[hi] < half) hi--;
            double fwhm = hi > lo ? hi - lo + 1 : 1;

            // --- peaks and the dip between them ---
            double peakThreshold = smPeak * Math.Clamp(param.peakThresholdFraction, 0.05, 0.95);
            var peaks = new List<int>();
            for (int i = 1; i < bins - 1; i++) {
                if (sm[i] < peakThreshold) continue;
                if (sm[i] >= sm[i - 1] && sm[i] > sm[i + 1]) peaks.Add(i);
            }

            double separation = 0;
            double dip = 0;
            if (peaks.Count >= 2) {
                int first = peaks[0], last = peaks[peaks.Count - 1];
                separation = last - first;

                double valley = double.MaxValue;
                for (int i = first; i <= last; i++) valley = Math.Min(valley, sm[i]);
                double outer = Math.Min(sm[first], sm[last]);
                if (outer > 0) dip = Math.Clamp(1.0 - valley / outer, 0, 1);
            }

            terms = new ProfileTerms(sigma, hfw, fwhm, separation, dip, peaks.Count, snr);
            return true;
        }

        public static double SelectMetric(SpikeMetricKind kind, in ProfileTerms t, SpikeAnalysisParams param) {
            switch (kind) {
                case SpikeMetricKind.Sigma: return t.Sigma;
                case SpikeMetricKind.Hfw: return t.Hfw;
                case SpikeMetricKind.Fwhm: return t.Fwhm;
                case SpikeMetricKind.Split: return t.Separation;
                case SpikeMetricKind.Hybrid: return t.Hfw + param.splitWeight * t.Separation;
                default: return double.NaN;
            }
        }

        // ====================================================
        // Diagnostics
        //
        // Exposed so the offline evaluator can look at exactly the pixels the
        // metric sees, rather than at a re-implementation of the extraction.
        // ====================================================
        public static bool TryGetDiagnosticRoi(
            ushort[] data, int width, int height,
            double x, double y, int baseSizePx,
            SpikeAnalysisParams param,
            out float[] roi, out int size, out double offX, out double offY) {

            offX = 0;
            offY = 0;
            param ??= new SpikeAnalysisParams();

            if (!TryExtractROIAt(data, width, height, x, y, baseSizePx, param, out roi, out size, out _, out _))
                return false;

            RemoveBackground(roi, size, param);

            if (param.enableCentroidTracking && TryRefineCentroid(roi, size, param, out double dx, out double dy)) {
                offX = dx;
                offY = dy;
            }
            return true;
        }

        /// <summary>
        /// Flux projected onto the u axis (perpendicular to the spike), using the same
        /// s-window and core suppression the metric applies. This is the curve whose
        /// shape the whole formulation is trying to summarise.
        /// </summary>
        public static double[] ComputeUProfile(
            float[] roi, int size,
            SpikeAnalysisParams param,
            double angleDeg,
            double offX, double offY,
            double uMax, int bins) {

            var profile = new double[bins];
            var counts = new double[bins];

            double centerX = size / 2.0 + offX;
            double centerY = size / 2.0 + offY;

            double theta = angleDeg * Math.PI / 180.0;
            double cosT = Math.Cos(theta);
            double sinT = Math.Sin(theta);

            double r0 = Math.Max(0.5, param.coreRejectSigmaPx);
            double inv2r02 = 1.0 / (2.0 * r0 * r0);
            double sSigma = Math.Max(1.0, param.axisSigmaPx);
            double inv2sSig2 = 1.0 / (2.0 * sSigma * sSigma);
            double sReject = Math.Max(0.5, param.axisRejectSigmaPx);
            double inv2sRej2 = 1.0 / (2.0 * sReject * sReject);

            for (int y = 0; y < size; y++) {
                double dy = y - centerY;
                int row = y * size;
                for (int x = 0; x < size; x++) {
                    double I = roi[row + x];
                    if (I <= 0) continue;

                    double dx = x - centerX;
                    double s = dx * cosT + dy * sinT;
                    double u = -dx * sinT + dy * cosT;
                    if (u < -uMax || u > uMax) continue;

                    double r2 = dx * dx + dy * dy;
                    double wCore = 1.0 - Math.Exp(-r2 * inv2r02);
                    double wAxis = Math.Exp(-s * s * inv2sSig2) * (1.0 - Math.Exp(-s * s * inv2sRej2));

                    int bin = (int)((u + uMax) / (2.0 * uMax) * bins);
                    if (bin < 0 || bin >= bins) continue;

                    profile[bin] += I * wCore * wAxis;
                    counts[bin] += 1;
                }
            }

            for (int i = 0; i < bins; i++)
                if (counts[i] > 0) profile[i] /= counts[i];

            return profile;
        }

        // ====================================================
        // Spike orientation
        //
        // Integrates background subtracted flux along rays through the star and
        // picks the direction that collects the most. Sampling both directions of
        // each ray makes it a line integral: a spike is a line, not a ray, so the
        // result is only defined modulo 180 degrees.
        // ====================================================
        public static double EstimateAngleDeg(
            float[] roi,
            int size,
            double centerX,
            double centerY,
            double rInner,
            double rOuter,
            double preferNearDeg,
            out double strength) {

            strength = 0;
            const int bins = 180;   // one bin per degree

            if (roi == null || rOuter - rInner < 4) return double.NaN;

            var profile = new double[bins];
            for (int b = 0; b < bins; b++) {
                double a = b * Math.PI / bins;
                double ca = Math.Cos(a), sa = Math.Sin(a);

                double sum = 0;
                int n = 0;
                for (double r = rInner; r <= rOuter; r += 1.0) {
                    for (int sign = -1; sign <= 1; sign += 2) {
                        int x = (int)Math.Round(centerX + sign * r * ca);
                        int y = (int)Math.Round(centerY + sign * r * sa);
                        if (x < 0 || y < 0 || x >= size || y >= size) continue;
                        double v = roi[y * size + x];
                        if (v > 0) sum += v;
                        n++;
                    }
                }
                profile[b] = n > 0 ? sum / n : 0;
            }

            var sm = new double[bins];
            for (int b = 0; b < bins; b++) {
                double s = 0;
                for (int k = -2; k <= 2; k++) s += profile[((b + k) % bins + bins) % bins];
                sm[b] = s / 5.0;
            }

            double mean = sm.Average();
            if (mean <= 0) return double.NaN;

            int best = 0;
            for (int b = 1; b < bins; b++) if (sm[b] > sm[best]) best = b;
            double bestVal = sm[best];
            if (bestVal <= 0) return double.NaN;

            // A four vane spider produces two axes 90 degrees apart of similar
            // strength. Without this the reported angle hops between them frame to
            // frame, which would make an auto angle worse than a fixed one.
            if (!double.IsNaN(preferNearDeg)) {
                int near = -1;
                for (int b = 0; b < bins; b++) {
                    if (CircularDistanceDeg(b, preferNearDeg) > 35.0) continue;
                    if (near < 0 || sm[b] > sm[near]) near = b;
                }
                if (near >= 0 && sm[near] >= 0.75 * bestVal) best = near;
            }

            // sub-degree refinement from the two neighbouring bins
            double ym1 = sm[(best - 1 + bins) % bins];
            double y0 = sm[best];
            double yp1 = sm[(best + 1) % bins];
            double denom = ym1 - 2 * y0 + yp1;
            double shift = Math.Abs(denom) > 1e-12 ? 0.5 * (ym1 - yp1) / denom : 0;
            shift = Math.Clamp(shift, -1.0, 1.0);

            strength = y0 / mean;
            return Wrap180((best + shift) * 180.0 / bins);
        }

        private static double CircularDistanceDeg(double a, double b) {
            double d = Math.Abs(a - b) % 180.0;
            return Math.Min(d, 180.0 - d);
        }

        private static double Wrap180(double deg) => ((deg % 180.0) + 180.0) % 180.0;

        /// <summary>
        /// Median of angles that are only defined modulo 180 degrees.
        ///
        /// Unwrapping needs a reference to fold the values around. Passing a stable
        /// one (the previous estimate) matters: anchoring on an arbitrary element of
        /// a sliding window lets the result jump whenever that element ages out.
        /// </summary>
        public static double CircularMedianDeg(IReadOnlyList<double> anglesDeg, double anchorDeg = double.NaN) {
            if (anglesDeg == null || anglesDeg.Count == 0) return double.NaN;

            double anchor = double.IsNaN(anchorDeg) ? anglesDeg[0] : anchorDeg;
            var unwrapped = new List<double>(anglesDeg.Count);
            foreach (var a in anglesDeg) {
                double v = a;
                while (v - anchor > 90.0) v -= 180.0;
                while (v - anchor < -90.0) v += 180.0;
                unwrapped.Add(v);
            }
            return Wrap180(Median(unwrapped));
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
            double angleDeg,
            double centerOffsetX,
            double centerOffsetY,
            out SpikeTerms terms) {

            terms = default;

            double baseCenter = size / 2;  // matches TryExtractROIAt / TryRefineCentroid
            double centerX = baseCenter + centerOffsetX;
            double centerY = baseCenter + centerOffsetY;

            double theta = angleDeg * Math.PI / 180.0;
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
