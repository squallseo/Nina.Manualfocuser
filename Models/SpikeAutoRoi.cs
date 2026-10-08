using System;
using System.Threading;

namespace Cwseo.NINA.ManualFocuser.Models {
    public static class SpikeAutoRoi {
        public readonly record struct Selection(FocusRoi.Rectangle Roi, FocusStarLocator.Core Core,
            double SupportedRadius, int PreferredSize);

        public static Selection Find(double[] pixels, int width, int height, CancellationToken token,
            double? searchX = null, double? searchY = null) {
            var core = FocusStarLocator.Find(pixels, width, height, token, searchX, searchY);
            double radius = 0;
            // Look for supported outer light, retaining margin for movement/seeing.
            // This chooses the crop; the actual spike/saturation checks happen on
            // fresh hardware-ROI frames, before any autofocus movement.
            int left = Math.Max(1, (int)Math.Floor(core.X - 192)), right = Math.Min(width - 1, (int)Math.Ceiling(core.X + 192));
            int top = Math.Max(1, (int)Math.Floor(core.Y - 192)), bottom = Math.Min(height - 1, (int)Math.Ceiling(core.Y + 192));
            for (int y = top; y < bottom; y++) {
                token.ThrowIfCancellationRequested();
                for (int x = left; x < right; x++) {
                    double distance = Math.Sqrt((x - core.X) * (x - core.X) + (y - core.Y) * (y - core.Y));
                    if (distance <= radius || distance > 192 || pixels[y * width + x] <= core.Background + 5 * core.Noise) continue;
                    int support = 0;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                        if (pixels[(y + dy) * width + x + dx] > core.Background + 3 * core.Noise) support++;
                    if (support >= 4) radius = distance;
                }
            }
            double needed = 2 * (radius + 24);
            int preferred = needed <= 256 ? 256 : needed <= 384 ? 384 : 512;
            foreach (int size in new[] { 512, 384, 256 }) {
                if (size > preferred) continue;
                int x = (int)Math.Round(core.X - size / 2.0), y = (int)Math.Round(core.Y - size / 2.0);
                // Fit a smaller centered window at an edge, never shift the star away
                // from the center. The remaining pattern must pass the AF preflight.
                if (x < 0 || y < 0 || x + size > width || y + size > height) continue;
                return new(new(x, y, size, size), core, radius, preferred);
            }
            throw new InvalidOperationException("Star is too close to the image edge for a centered Spike ROI. Choose another star or recenter it.");
        }
    }
}
