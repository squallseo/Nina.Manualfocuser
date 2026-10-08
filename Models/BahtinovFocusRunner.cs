using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Cwseo.NINA.ManualFocuser.Models {
    public static class BahtinovFocusRunner {
        public static async Task<(int Position, double Error)> RunAsync(int origin, int step, int offsets, Func<int, CancellationToken, Task<int>> move, Func<int, CancellationToken, Task<double>> measure, CancellationToken token,Func<double> measurementUncertainty=null) {
            if (step < 1 || step > 10000 || offsets < 1 || offsets > 12 || origin < (long)step * offsets || origin + (long)step * offsets > int.MaxValue)
                throw new ArgumentException("Invalid bounded scan range.");
            int lower = origin - step * offsets, upper = origin + step * offsets;
            var samples = new SortedDictionary<int, double>();
            var uncertainties = new Dictionary<int,double>();
            async Task Move(int p) {
                token.ThrowIfCancellationRequested();
                if (p < lower || p > upper || await move(p, token) != p)
                    throw new InvalidOperationException("Focuser did not reach bounded target.");
                token.ThrowIfCancellationRequested();
            }
            async Task<double> Read(int p) {
                token.ThrowIfCancellationRequested();
                double e = await measure(p, token);
                token.ThrowIfCancellationRequested();
                if (!double.IsFinite(e))
                    throw new InvalidOperationException("Invalid mask measurement. No further moves.");
                double uncertainty=measurementUncertainty?.Invoke() ?? 0;
                FocusMeasurementNoise.DifferenceThreshold(uncertainty,0);
                uncertainties[p]=uncertainty;
                return e;
            }
            samples[origin] = await Read(origin);
            // Preflight before any motor command.
            if (Math.Abs(samples[origin]) <= .25) {
                double final = await Read(origin);
                if (Math.Abs(final) <= .25)
                    return (origin, final);
                throw new InvalidOperationException("Focus confirmation is unstable.");
            }
            int current = origin + step;
            await Move(current);
            samples[current] = await Read(current);
            for (int iteration = 0; iteration < 20; iteration++) {
                var ordered = samples.ToArray();
                var best = ordered.OrderBy(p => Math.Abs(p.Value)).First();
                if (Math.Abs(best.Value) <= .25) {
                    int approach = Math.Max(lower, best.Key - step);
                    if (approach >= best.Key)
                        throw new InvalidOperationException("Cannot approach focus from the configured direction.");
                    await Move(approach);
                    await Move(best.Key);
                    double final = await Read(best.Key);
                    if (Math.Abs(final) <= .25)
                        return (best.Key, final);
                    throw new InvalidOperationException("Final mask error exceeds 0.25 px. Focus not verified.");
                }
                // Once signs bracket zero, prediction stays inside that measured interval.
                int next = -1;
                for (int i = 1; i < ordered.Length; i++) {
                    var a = ordered[i - 1];
                    var b = ordered[i];
                    if (Math.Sign(a.Value) == Math.Sign(b.Value))
                        continue;
                    double fraction = Math.Abs(a.Value) / (Math.Abs(a.Value) + Math.Abs(b.Value));
                    if (!double.IsFinite(fraction))
                        throw new InvalidOperationException("Invalid mask slope.");
                    // Avoid endpoint stagnation on strongly nonlinear/noisy slopes.
                    fraction = Math.Clamp(fraction, .1, .9);
                    next = (int)Math.Round(a.Key + fraction * ((long)b.Key - a.Key));
                    if (next <= a.Key || next >= b.Key)
                        next = a.Key + (b.Key - a.Key) / 2;
                    break;
                }
                if (next < 0) {
                    // Use the widest pair with a change above stationary measurement
                    // noise, rather than extrapolating from a nearly identical neighbor.
                    long span=0; double slope=double.NaN;
                    for(int i=0;i<ordered.Length;i++) for(int j=i+1;j<ordered.Length;j++) {
                        var a=ordered[i]; var b=ordered[j];
                        long distance=(long)b.Key-a.Key;
                        if(distance>span && Math.Abs(b.Value-a.Value)>
                            FocusMeasurementNoise.DifferenceThreshold(uncertainties[a.Key],uncertainties[b.Key])) {
                            span=distance; slope=(b.Value-a.Value)/distance;
                        }
                    }
                    if(!double.IsFinite(slope) || Math.Abs(slope)<1e-12) {
                        long probeSpan=Math.Min(2*ordered.Max(p=>Math.Abs((long)p.Key-origin)),(long)step*offsets);
                        int right=(int)Math.Clamp(origin+probeSpan,lower,upper),left=(int)Math.Clamp(origin-probeSpan,lower,upper);
                        next=!samples.ContainsKey(right)?right:!samples.ContainsKey(left)?left:-1;
                        if(next<0) throw new InvalidOperationException("Mask error changes remain below measurement noise in the bounded range. Increase AF step size or improve the measurement.");
                    } else {
                        double travelLimit=2.0*Math.Max(step,span);
                        double delta=Math.Clamp(-samples[current]/slope,-travelLimit,travelLimit);
                        next=(int)Math.Clamp(Math.Round(current+delta),lower,upper);
                    }
                }
                if (samples.ContainsKey(next))
                    throw new InvalidOperationException("No verified zero in the bounded range.");
                await Move(next);
                samples[next] = await Read(next);
                current = next;
            }
            throw new InvalidOperationException("Mask autofocus did not converge within the measurement limit.");
        }
    }
}
