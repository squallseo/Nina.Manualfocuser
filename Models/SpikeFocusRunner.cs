using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Cwseo.NINA.ManualFocuser.Models {
    public static class SpikeFocusRunner {
        public static async Task<(int Position, double Width)> RunAsync(int origin, int step, int offsets, Func<int, CancellationToken, Task<int>> move, Func<int, CancellationToken, Task<double>> measure, CancellationToken token) {
            if (step < 4 || step > 10000 || offsets < 1 || offsets > 12 || origin < (long)step * offsets || origin + (long)step * offsets > int.MaxValue)
                throw new ArgumentException("Invalid bounded spike scan range.");
            int lower = origin - step * offsets, upper = origin + step * offsets;
            var samples = new SortedDictionary<int, double>();
            async Task Move(int p) {
                token.ThrowIfCancellationRequested();
                if (p < lower || p > upper || await move(p, token) != p)
                    throw new InvalidOperationException("Focuser did not reach bounded target.");
                token.ThrowIfCancellationRequested();
            }
            async Task<double> Read(int p) {
                token.ThrowIfCancellationRequested();
                double w = await measure(p, token);
                token.ThrowIfCancellationRequested();
                if (!double.IsFinite(w) || w <= 0)
                    throw new InvalidOperationException("Spike lost. No further moves.");
                return w;
            }
            async Task<double> Sample(int p) {
                if (samples.TryGetValue(p, out double cached))
                    return cached;
                await Move(p);
                double w = await Read(p);
                samples[p] = w;
                return w;
            }
            samples[origin] = await Read(origin);
            double right = await Sample(origin + step);
            int direction = right < samples[origin] ? 1 : -1, current = direction > 0 ? origin + step : origin - step;
            await Sample(current);
            long stride = step;
            int a = 0, b = 0, c = 0;
            bool bracketed = false;
            // Expand only while seeking a measured minimum, never outside the original bounds.
            for (int iteration = 0; iteration < 14; iteration++) {
                var points = samples.ToArray();
                for (int i = 1; i < points.Length - 1; i++)
                    if (points[i].Value < points[i - 1].Value && points[i].Value < points[i + 1].Value) {
                        a = points[i - 1].Key;
                        b = points[i].Key;
                        c = points[i + 1].Key;
                        bracketed = true;
                        break;
                    }
                if (bracketed)
                    break;
                if (samples.Values.All(v => v == samples[origin]))
                    throw new InvalidOperationException("Flat spike widths cannot locate focus.");
                stride = Math.Min(stride * 2, (long)step * offsets);
                int next = (int)Math.Clamp(current + direction * stride, lower, upper);
                if (next == current)
                    throw new InvalidOperationException("No interior spike-width minimum in the bounded range.");
                await Sample(next);
                current = next;
            }
            if (!bracketed)
                throw new InvalidOperationException("No reliable spike-width bracket.");
            // Safeguarded parabolic interpolation with a golden-section fallback.
            // Both bracket sides must converge; a plausible vertex alone is insufficient.
            int resolution = Math.Max(1, step / 16);
            for (int iteration = 0; iteration < 24 && (long)c - a > 2L * resolution; iteration++) {
                double left = (long)b - a, rightDistance = (long)c - b;
                double denominator = left * (samples[b] - samples[c]) + rightDistance * (samples[b] - samples[a]);
                double candidate = b - .5 * (left * left * (samples[b] - samples[c]) - rightDistance * rightDistance * (samples[b] - samples[a])) / denominator;
                int next;
                if (!double.IsFinite(candidate) || candidate <= a || candidate >= c || Math.Abs(candidate - b) < resolution)
                    next = (int)Math.Round(b + (left > rightDistance ? -left : rightDistance) * .38196601125);
                else
                    next = (int)Math.Round(candidate);
                next = Math.Clamp(next, a + 1, c - 1);
                if (next == b || samples.ContainsKey(next))
                    break;
                double w = await Sample(next);
                if (next < b) {
                    if (w < samples[b]) {
                        c = b;
                        b = next;
                    } else
                        a = next;
                } else {
                    if (w < samples[b]) {
                        a = b;
                        b = next;
                    } else
                        c = next;
                }
            }
            if ((long)c - a > 2L * resolution)
                throw new InvalidOperationException("Spike autofocus did not converge within the measurement limit.");
            await Move(Math.Max(lower, b - step));
            await Move(b);
            double final = await Read(b);
            if (final > samples[b] * 1.10)
                throw new InvalidOperationException("Final spike width did not reproduce the measured minimum.");
            return (b, final);
        }
    }
}
