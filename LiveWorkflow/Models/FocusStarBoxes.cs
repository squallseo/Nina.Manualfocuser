using System;
using System.Collections.Generic;
using System.Windows;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models {
    public static class FocusStarBoxes {
        public static bool HasBounds(Rect bounds) => !bounds.IsEmpty && bounds.Width>0 && bounds.Height>0 &&
            double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);
        public static Rect[] Project(FocusHfrMeasurement measurement, FocusRawFrame display, bool inspector) {
            if (measurement == null || display == null || measurement.Left != display.Left ||
                measurement.Top != display.Top || measurement.Width != display.Width || measurement.Height != display.Height)
                return Array.Empty<Rect>();
            var boxes = new List<Rect>();
            FocusAberrationMosaic.Layout layout = null;
            if (inspector) layout = FocusAberrationMosaic.Plan(display.SourceImage?.Properties.Width ?? display.Width,
                display.SourceImage?.Properties.Height ?? display.Height);
            foreach (var star in measurement.StarSamples) {
                if (!double.IsFinite(star.X) || !double.IsFinite(star.Y) || !double.IsFinite(star.Hfr) || star.Hfr <= 0 ||
                    star.X < 0 || star.Y < 0 || star.X >= display.Width || star.Y >= display.Height || !HasBounds(star.Bounds)) continue;
                // HFR describes the central flux, not the star's outer edge.
                // Enclose the detected signal in a square with 1px clearance.
                double side=Math.Max(star.Bounds.Width,star.Bounds.Height)+2;
                var square = new Rect(star.Bounds.X+star.Bounds.Width/2-side/2,
                    star.Bounds.Y+star.Bounds.Height/2-side/2,side,side);
                if (layout == null) {
                    square.Intersect(new Rect(0, 0, display.Width, display.Height));
                    if (!square.IsEmpty) boxes.Add(square);
                } else {
                    // Inspector may show the full source of a software ROI.
                    double offsetX = display.SourceImage == null ? 0 : display.Left;
                    double offsetY = display.SourceImage == null ? 0 : display.Top;
                    square.Offset(offsetX, offsetY);
                    foreach (var tile in layout.Tiles) {
                        if (!new Rect(tile.X, tile.Y, layout.Size, layout.Size).Contains(star.X + offsetX, star.Y + offsetY)) continue;
                        var clipped = square;
                        clipped.Intersect(new Rect(tile.X, tile.Y, layout.Size, layout.Size));
                        if (clipped.IsEmpty) continue;
                        clipped.Offset(tile.DisplayX - tile.X, tile.DisplayY - tile.Y);
                        boxes.Add(clipped);
                    }
                }
            }
            return boxes.ToArray();
        }
    }
}
