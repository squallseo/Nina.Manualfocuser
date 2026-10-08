using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cwseo.NINA.ManualFocuser.Models {
    public sealed class FocusSaturationException : InvalidOperationException {
        public FocusSaturationException(string message) : base(message) { }
    }

    /// <summary>Restart a bounded spike scan after changing exposure, never mix its width samples.</summary>
    public static class SpikeExposureRunner {
        public const int MaximumReductions = 8;

        public static async Task<(int Position, double Width, double ExposureSeconds, int Reductions)> RunAsync(
            int origin, int step, int offsets, double initialSeconds, double minimumSeconds,
            Func<int, CancellationToken, Task<int>> move,
            Func<int, double, CancellationToken, Task<double>> measure,
            Func<double, CancellationToken, Task> applyExposure, CancellationToken token,Func<double> measurementUncertainty=null) {
            if (!double.IsFinite(initialSeconds) || !double.IsFinite(minimumSeconds)
                || minimumSeconds <= 0 || initialSeconds < minimumSeconds)
                throw new ArgumentException("Invalid spike autofocus exposure range.");
            double seconds = initialSeconds;
            int current = origin, reductions = 0;
            async Task<int> Move(int position, CancellationToken cancellation) {
                cancellation.ThrowIfCancellationRequested();
                int actual = await move(position, cancellation);
                cancellation.ThrowIfCancellationRequested();
                current = actual;
                return actual;
            }
            async Task<double> Read(int position, CancellationToken cancellation) {
                cancellation.ThrowIfCancellationRequested();
                double width = await measure(position, seconds, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!double.IsFinite(width) || width <= 0)
                    throw new InvalidOperationException("Spike lost. No further moves.");
                return width;
            }
            while (true) {
                token.ThrowIfCancellationRequested();
                try {
                    if (current != origin) {
                        // Confirm the new exposure at the current stationary position before
                        // returning to the original scan origin. A failed capture cannot move it.
                        await Read(current, token);
                        if (await Move(origin, token) != origin)
                            throw new InvalidOperationException("Focuser did not reach bounded target.");
                    }
                    var result = await SpikeFocusRunner.RunAsync(origin, step, offsets, Move, Read, token,measurementUncertainty);
                    return (result.Position, result.Width, seconds, reductions);
                } catch (FocusSaturationException error) {
                    token.ThrowIfCancellationRequested();
                    double next = Math.Max(minimumSeconds, seconds / 4);
                    if (reductions >= MaximumReductions || next >= seconds)
                        throw new InvalidOperationException($"Star is still saturated at {seconds * 1000:F3} ms. " +
                            "Autofocus exposure cannot be reduced further. Lower camera Gain or choose a dimmer star.", error);
                    // The application must await stream shutdown before applying this change.
                    await applyExposure(next, token);
                    token.ThrowIfCancellationRequested();
                    seconds = next;
                    reductions++;
                }
            }
        }
    }
}
