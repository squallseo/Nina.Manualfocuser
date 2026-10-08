using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Cwseo.NINA.ManualFocuser.Models {
    public static class SpikeFocusRunner {
        public static async Task<(int Position, double Width)> RunAsync(int origin, int step, int offsets, Func<int, CancellationToken, Task<int>> move, Func<int, CancellationToken, Task<double>> measure, CancellationToken token,Func<double> measurementUncertainty=null) {
            if (step < 4 || step > 10000 || offsets < 1 || offsets > 12 || origin < (long)step * offsets || origin + (long)step * offsets > int.MaxValue)
                throw new ArgumentException("Invalid bounded spike scan range.");
            int lower = origin - step * offsets, upper = origin + step * offsets;
            var samples = new SortedDictionary<int, double>();
            var uncertainties = new Dictionary<int,double>();
            bool ClearlyLess(int first,int second) => samples[second]-samples[first]>
                FocusMeasurementNoise.DifferenceThreshold(uncertainties[first],uncertainties[second]);
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
                double uncertainty=measurementUncertainty?.Invoke() ?? 0;
                FocusMeasurementNoise.DifferenceThreshold(uncertainty,0);
                uncertainties[p]=uncertainty;
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
            await Sample(origin + step);
            int direction = ClearlyLess(origin+step,origin) ? 1 : ClearlyLess(origin,origin+step) ? -1 : 0;
            int current = direction > 0 ? origin + step : origin - step;
            if(direction<=0) await Sample(current);
            if(direction==0 && ClearlyLess(current,origin)) direction=-1;
            long stride = step;
            int a = 0, b = 0, c = 0;
            bool bracketed = false;
            // Expand only while seeking a measured minimum, never outside the original bounds.
            for (int iteration = 0; iteration < 14; iteration++) {
                var points = samples.ToArray();
                foreach(var candidatePoint in points.OrderBy(p=>p.Value)) {
                    var leftEvidence=points.Where(p=>p.Key<candidatePoint.Key && ClearlyLess(candidatePoint.Key,p.Key)).ToArray();
                    var rightEvidence=points.Where(p=>p.Key>candidatePoint.Key && ClearlyLess(candidatePoint.Key,p.Key)).ToArray();
                    if(leftEvidence.Length>0 && rightEvidence.Length>0) {
                        a = leftEvidence[^1].Key;
                        b = candidatePoint.Key;
                        c = rightEvidence[0].Key;
                        bracketed = true;
                        break;
                    }
                }
                if (bracketed)
                    break;
                stride = Math.Min(stride * 2, (long)step * offsets);
                if(direction==0) {
                    int rightProbe=(int)Math.Clamp(origin+stride,lower,upper);
                    int leftProbe=(int)Math.Clamp(origin-stride,lower,upper);
                    bool haveBoth=samples.ContainsKey(rightProbe) && samples.ContainsKey(leftProbe);
                    if(haveBoth) throw new InvalidOperationException("Spike width changes remain below measurement noise in the bounded range. Increase AF step size or improve the measurement.");
                    await Sample(rightProbe);
                    await Sample(leftProbe);
                    int best=samples.OrderBy(p=>p.Value).First().Key;
                    if(ClearlyLess(best,origin)) { direction=Math.Sign(best-origin); current=best; }
                    continue;
                }
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
