using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cwseo.NINA.ManualFocuser.Models {
    public sealed record BahtinovBatchResult(double Median, double[] Values, double Spread,
        int InlierCount, double InlierSpread, bool IsStable) {
        public double Uncertainty => FocusMeasurementNoise.Estimate(Spread<=BahtinovMeasurementBatch.MaximumSpread ? Values
            : Values.Where(v=>Math.Abs(v-Median)<=BahtinovMeasurementBatch.MaximumSpread/2).ToArray());
    }

    /// <summary>Extra stationary frames distinguish occasional excursions from unstable geometry.</summary>
    public static class BahtinovMeasurementBatch {
        public const double MaximumSpread = 1;
        public const int ExtendedFrameCount = 7;

        public static BahtinovBatchResult Evaluate(double[] values) {
            if (values == null || (values.Length != 3 && values.Length != ExtendedFrameCount)
                || values.Any(v => !double.IsFinite(v)))
                throw new ArgumentException("A mask batch requires three or seven finite measurements.");
            var sorted = values.OrderBy(v => v).ToArray();
            double median = sorted[sorted.Length / 2], spread = sorted[^1] - sorted[0];
            // Three-frame batches retain the original whole-range check. With seven frames,
            // at least five must agree around the median within that same one-pixel width.
            var inliers = spread <= MaximumSpread ? sorted
                : sorted.Where(v => Math.Abs(v - median) <= MaximumSpread / 2).ToArray();
            double inlierSpread = inliers[^1] - inliers[0];
            bool stable = spread <= MaximumSpread || (sorted.Length == ExtendedFrameCount && inliers.Length >= 5);
            return new(median, sorted, spread, inliers.Length, inlierSpread, stable);
        }

        public static async Task<BahtinovBatchResult> CollectAsync(
            Func<CancellationToken, Task<double>> read, Action collectingMore, CancellationToken token) {
            var values = new double[ExtendedFrameCount];
            for (int i = 0; i < values.Length; i++) {
                token.ThrowIfCancellationRequested();
                values[i] = await read(token);
                token.ThrowIfCancellationRequested();
                if (!double.IsFinite(values[i]))
                    throw new InvalidOperationException("Invalid mask measurement. No further moves.");
                if (i == 2) {
                    var initial = Evaluate(values.Take(3).ToArray());
                    if (initial.IsStable) return initial;
                    collectingMore?.Invoke();
                }
            }
            return Evaluate(values);
        }
    }
}
