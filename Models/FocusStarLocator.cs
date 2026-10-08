using System;
using System.Threading;

namespace Cwseo.NINA.ManualFocuser.Models {
    public static class FocusStarLocator {
        public readonly record struct Core(double X, double Y, double Background, double Noise);

        public static Core Find(double[] pixels, int width, int height, CancellationToken token,
            double? searchX = null, double? searchY = null) {
            token.ThrowIfCancellationRequested();
            if (pixels == null || (long)width * height != pixels.Length || width < 64 || height < 64)
                throw new ArgumentException("Focus scout frame is too small.");
            foreach (double pixel in pixels)
                if (!double.IsFinite(pixel)) throw new ArgumentException("Non-finite focus scout pixels.");
            double selectedX = searchX ?? width / 2.0, selectedY = searchY ?? height / 2.0;
            if (!double.IsFinite(selectedX) || !double.IsFinite(selectedY)) throw new ArgumentException("Invalid selected star position.");
            selectedX = Math.Clamp(selectedX, 0, width - 1); selectedY = Math.Clamp(selectedY, 0, height - 1);
            var sorted = (double[])pixels.Clone(); Array.Sort(sorted);
            double background = sorted[sorted.Length / 2];
            double noise = Math.Max(1, (sorted[sorted.Length * 3 / 4] - sorted[sorted.Length / 4]) / 1.349);
            int peakX = 0, peakY = 0; double bestFlux = 0;
            // Supported compact flux avoids isolated hot pixels near the user's selection.
            int left = Math.Max(2, (int)selectedX - 256), right = Math.Min(width - 2, (int)selectedX + 256);
            int top = Math.Max(2, (int)selectedY - 256), bottom = Math.Min(height - 2, (int)selectedY + 256);
            for (int y = top; y < bottom; y++) {
                token.ThrowIfCancellationRequested();
                for (int x = left; x < right; x++) {
                    double flux = 0; int support = 0;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) {
                        double value = Math.Max(0, pixels[(y + dy) * width + x + dx] - background - 5 * noise);
                        flux += value; if (value > 0) support++;
                    }
                    if (support >= 4 && flux > bestFlux) { bestFlux = flux; peakX = x; peakY = y; }
                }
            }
            if (bestFlux <= 0)
                throw new InvalidOperationException("No supported star core near the selected ROI. Diagnostic scout saved.");
            double sum = 0, sx = 0, sy = 0;
            for (int y = Math.Max(0, peakY - 8); y <= Math.Min(height - 1, peakY + 8); y++)
                for (int x = Math.Max(0, peakX - 8); x <= Math.Min(width - 1, peakX + 8); x++) {
                    double flux = Math.Max(0, pixels[y * width + x] - background - 5 * noise);
                    sum += flux; sx += x * flux; sy += y * flux;
                }
            return new(sx / sum, sy / sum, background, noise);
        }
    }
}
