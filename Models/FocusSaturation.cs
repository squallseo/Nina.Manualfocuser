using System;
using System.Collections.Generic;

namespace Cwseo.NINA.ManualFocuser.Models {
    public static class FocusSaturation {
        // All preview samples are unsigned 16-bit image values, before display stretch.
        // Ignore isolated defects; four connected clipped pixels constitute a plateau.
        public static bool HasClippedAnalysisArea(double[] pixels, int width, int height, bool mask) {
            if (pixels == null || width < 1 || height < 1 || (long)width * height != pixels.Length)
                throw new ArgumentException("Invalid saturation image dimensions.");
            double cx = (width - 1) / 2.0, cy = (height - 1) / 2.0;
            double outer = Math.Min(width, height) * .43, inner = Math.Max(7, outer * .3);
            int left = (width - Math.Min(width, 256)) / 2, top = (height - Math.Min(height, 256)) / 2;
            bool Included(int x, int y) {
                if (x < 0 || y < 0 || x >= width || y >= height) return false;
                if (!mask) return x >= left && y >= top && x < left + Math.Min(width,256) && y < top + Math.Min(height,256);
                double r2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                // Match BahtinovAnalyzer's sampled annulus: core clipping alone does
                // not invalidate the three outer diffraction-line positions.
                return r2 >= inner * inner && r2 <= outer * outer;
            }
            var component = new List<int>(4);
            int startX = mask ? Math.Max(0,(int)(cx-outer)) : left;
            int startY = mask ? Math.Max(0,(int)(cy-outer)) : top;
            int endX = mask ? Math.Min(width,(int)Math.Ceiling(cx+outer)+1) : left+Math.Min(width,256);
            int endY = mask ? Math.Min(height,(int)Math.Ceiling(cy+outer)+1) : top+Math.Min(height,256);
            for (int y = startY; y < endY; y++) for (int x = startX; x < endX; x++) {
                if (!Included(x, y) || pixels[y * width + x] < 65500) continue;
                component.Clear(); component.Add(y * width + x);
                for (int i = 0; i < component.Count; i++) {
                    int px = component[i] % width, py = component[i] / width;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) {
                        int nx = px + dx, ny = py + dy, index = ny * width + nx;
                        if (!Included(nx, ny) || component.Contains(index) || pixels[index] < 65500) continue;
                        component.Add(index);
                        if (component.Count >= 4) return true;
                    }
                }
            }
            return false;
        }
    }
}
