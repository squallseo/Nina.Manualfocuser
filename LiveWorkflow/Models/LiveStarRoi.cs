using System;
using System.Threading;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models
{
    public static class LiveStarRoi
    {
        public static (FocusRoi.Rectangle Roi, double Hfr) Find(double[] pixels, int width, int height, CancellationToken token, double? searchX = null, double? searchY = null)
        {
            var star = FocusStarLocator.Find(pixels, width, height, token, searchX, searchY);
            var local = FocusRoi.Fit(width, height, 256, 256, star.X / width * 100, star.Y / height * 100);
            var crop = BahtinovAutoRoi.Crop(pixels, width, height, local);
            double hfr = QuickFocusMetrics.HalfFluxRadius(crop, local.Width, local.Height);
            if (!double.IsFinite(hfr)) throw new InvalidOperationException("No isolated star for automatic ROI selection.");
            int size = Math.Clamp((int)Math.Ceiling(hfr * 12 + 64), 128, 512);
            var roi = FocusRoi.Fit(width, height, size, size, star.X / width * 100, star.Y / height * 100);
            token.ThrowIfCancellationRequested();
            return (roi, hfr);
        }
    }
}