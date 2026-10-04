using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cwseo.NINA.ManualFocuser.Models {
    /// <summary>Documented scan/zero/same-direction approach, with bounded project interpolation and final verification.</summary>
    public static class BahtinovFocusRunner {
        public static async Task<(int Position, double Error)> RunAsync(int origin, int step, int offsets,
            Func<int, CancellationToken, Task<int>> move, Func<int, CancellationToken, Task<double>> measure, CancellationToken token) {
            if (step < 1 || step > 10000 || offsets < 1 || offsets > 12 || origin < (long)step * offsets || origin + (long)step * offsets > int.MaxValue)
                throw new ArgumentException("Invalid bounded scan range.");
            int lower = origin - step * offsets;
            var samples = new List<(int Position, double Error)>();
            async Task Move(int position) {
                token.ThrowIfCancellationRequested();
                if (await move(position, token) != position) throw new InvalidOperationException("Focuser did not reach requested position.");
                token.ThrowIfCancellationRequested();
            }
            async Task<double> Measure(int position) {
                token.ThrowIfCancellationRequested();
                double error = await measure(position, token);
                token.ThrowIfCancellationRequested();
                if (!double.IsFinite(error)) throw new InvalidOperationException("Invalid mask measurement. No further moves.");
                return error;
            }
            // Preflight checks image validity before issuing any motor command.
            await Measure(origin);
            for (int i = 0; i <= offsets * 2; i++) {
                int position = lower + i * step;
                await Move(position);
                samples.Add((position, await Measure(position)));
                if (samples.Count > 1 && BahtinovFocusController.TryFindZero(samples, out _)) break;
            }
            if (!BahtinovFocusController.TryFindZero(samples, out int target)) throw new InvalidOperationException("No zero-error bracket in scan range. Center focus manually and retry.");
            int approach = Math.Max(lower, target - step);
            if (approach >= target) throw new InvalidOperationException("Cannot approach zero from scan direction within the allowed range.");
            await Move(approach);
            await Move(target);
            double final = await Measure(target);
            if (Math.Abs(final) > .5) throw new InvalidOperationException($"Final error {final:F2} px exceeds 0.5 px. Autofocus was not confirmed.");
            return (target, final);
        }
    }
}
