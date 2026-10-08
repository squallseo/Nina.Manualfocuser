using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cwseo.NINA.ManualFocuser.Models {
    public sealed record SpikeBatchResult(double Median, double[] Values, double Spread,
        double Limit, int InlierCount, double InlierSpread, bool IsStable) {
        public double Uncertainty => FocusMeasurementNoise.Estimate(Spread<=Limit ? Values
            : Values.Where(v=>Math.Abs(v-Median)<=Limit/2).ToArray());
    }

    public static class SpikeMeasurementBatch {
        public const int ExtendedFrameCount = 7;
        public static SpikeBatchResult Evaluate(double[] values) {
            if (values == null || (values.Length != 3 && values.Length != ExtendedFrameCount) ||
                values.Any(v => !double.IsFinite(v) || v <= 0))
                throw new ArgumentException("A spike batch requires three or seven positive finite widths.");
            var sorted = values.OrderBy(v => v).ToArray();
            double median = sorted[sorted.Length / 2], spread = sorted[^1] - sorted[0];
            double limit = Math.Max(.25, median * .20);
            var inliers = spread <= limit ? sorted : sorted.Where(v => Math.Abs(v - median) <= limit / 2).ToArray();
            return new(median, sorted, spread, limit, inliers.Length, inliers[^1] - inliers[0],
                spread <= limit || (sorted.Length == ExtendedFrameCount && inliers.Length >= 5));
        }
        public static async Task<SpikeBatchResult> CollectAsync(Func<CancellationToken, Task<double>> read,
            Action collectingMore, CancellationToken token) {
            var values = new double[ExtendedFrameCount];
            for (int i = 0; i < values.Length; i++) {
                token.ThrowIfCancellationRequested();
                values[i] = await read(token);
                token.ThrowIfCancellationRequested();
                if (!double.IsFinite(values[i]) || values[i] <= 0)
                    throw new InvalidOperationException("Invalid spike width. No further moves.");
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
