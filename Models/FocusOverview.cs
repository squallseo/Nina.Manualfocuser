using System;
using System.Threading;
using NINA.Image.Interfaces;

namespace Cwseo.NINA.ManualFocuser.Models {
    public partial class ManualFocuserModel {
        public static (double[] Pixels, int Width, int Height, bool HardwareRoi) CreateFocusOverview(IImageData image, CancellationToken token) {
            var raw = image?.Data?.FlatArray;
            int width = image?.Properties?.Width ?? 0, height = image?.Properties?.Height ?? 0;
            if (width < 1 || height < 1 || raw == null || raw.Length != (long)width * height) throw new InvalidOperationException("Invalid overview image.");
            int stride = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / 1024.0));
            int w = (width + stride - 1) / stride, h = (height + stride - 1) / stride;
            var pixels = new double[w * h];
            for (int row = 0; row < h; row++) {
                token.ThrowIfCancellationRequested();
                for (int col = 0; col < w; col++) {
                    double maximum = 0;
                    for (int y = row * stride; y < Math.Min(height, (row + 1) * stride); y++)
                        for (int x = col * stride; x < Math.Min(width, (col + 1) * stride); x++) maximum = Math.Max(maximum, raw[y * width + x]);
                    pixels[row * w + col] = maximum;
                }
            }
            return (pixels, w, h, false);
        }
    }
}
