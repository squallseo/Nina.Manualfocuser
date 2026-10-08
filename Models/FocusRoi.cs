using System;

namespace Cwseo.NINA.ManualFocuser.Models {
    public static class FocusRoi {
        public readonly record struct Rectangle(int X, int Y, int Width, int Height);
        public static Rectangle Fit(int sensorWidth, int sensorHeight, int width, int height, double centerXPercent, double centerYPercent, int alignment = 1, int? heightAlignment = null) {
            int verticalAlignment=heightAlignment ?? alignment;
            if (sensorWidth < 32 || sensorHeight < 32 || width < 32 || height < 32 || alignment < 1 || verticalAlignment < 1 ||
                !double.IsFinite(centerXPercent) || !double.IsFinite(centerYPercent)) throw new ArgumentException("Invalid ROI dimensions or position.");
            width = Math.Min(width, sensorWidth) / alignment * alignment;
            height = Math.Min(height, sensorHeight) / verticalAlignment * verticalAlignment;
            int x = Math.Clamp((int)(sensorWidth * centerXPercent / 100) - width / 2, 0, sensorWidth - width) / alignment * alignment;
            int y = Math.Clamp((int)(sensorHeight * centerYPercent / 100) - height / 2, 0, sensorHeight - height) / verticalAlignment * verticalAlignment;
            return new Rectangle(x, y, width, height);
        }
        public static (double[] Pixels, int Width, int Height) CenterWindow(double[] pixels, int width, int height, int size = 256) {
            if (width < 1 || height < 1 || pixels == null || pixels.Length != (long)width * height) throw new ArgumentException("Invalid image.");
            int w = Math.Min(size, width), h = Math.Min(size, height);
            if (w == width && h == height) return (pixels, width, height);
            var result = new double[w * h];
            int left = (width - w) / 2, top = (height - h) / 2;
            for (int row = 0; row < h; row++) Array.Copy(pixels, (top + row) * width + left, result, row * w, w);
            return (result, w, h);
        }
    }
}
