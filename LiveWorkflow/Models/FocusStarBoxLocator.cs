using System;
using System.Collections.Generic;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models {
    public static class FocusStarBoxLocator {
        // HFR remains the last detector result. Only the inexpensive local
        // centroid is refreshed from THIS displayed frame's original pixels.
        public static FocusHfrMeasurement Locate(FocusHfrMeasurement measurement, FocusRawFrame frame) {
            if (measurement == null || frame == null || measurement.Left != frame.Left || measurement.Top != frame.Top ||
                measurement.Width != frame.Width || measurement.Height != frame.Height) return null;
            var stars = new List<FocusStarSample>(measurement.StarSamples.Length);
            Span<double> perimeter = stackalloc double[100];
            foreach (var star in measurement.StarSamples) {
                if (!double.IsFinite(star.X) || !double.IsFinite(star.Y) || !double.IsFinite(star.Hfr) || star.Hfr <= 0) continue;
                int cx = (int)Math.Round(star.X), cy = (int)Math.Round(star.Y);
                if (cx < 0 || cy < 0 || cx >= frame.Width || cy >= frame.Height) continue;
                int radius = (int)Math.Clamp(Math.Ceiling(star.Hfr * 2 + 4), 8, 24);
                int x0 = Math.Max(0, cx-radius), x1 = Math.Min(frame.Width-1, cx+radius);
                int y0 = Math.Max(0, cy-radius), y1 = Math.Min(frame.Height-1, cy+radius);
                double Pixel(int x, int y) => frame.Pixels[(frame.Top+y)*frame.Stride+frame.Left+x];
                // A few noisy corner pixels must not move or hide a valid box.
                // Use the small search perimeter's median and robust noise.
                int count=0;
                for(int x=x0;x<=x1;x+=2) { perimeter[count++]=Pixel(x,y0); perimeter[count++]=Pixel(x,y1); }
                for(int y=y0+1;y<y1;y+=2) { perimeter[count++]=Pixel(x0,y); perimeter[count++]=Pixel(x1,y); }
                var values=perimeter.Slice(0,count); values.Sort();
                double background=(values[(count-1)/2]+values[count/2])/2;
                for(int i=0;i<count;i++)values[i]=Math.Abs(values[i]-background);
                values.Sort();
                double noise=(values[(count-1)/2]+values[count/2])/2*1.4826;
                double threshold = Math.Max(8, noise*3);
                double peak = background; int px=cx, py=cy;
                for (int y=y0; y<=y1; y++) for (int x=x0; x<=x1; x++) {
                    double value=Pixel(x,y);
                    if (value>peak) { peak=value; px=x; py=y; }
                }
                if (peak-background < threshold || Math.Abs(px-cx)>=radius || Math.Abs(py-cy)>=radius) continue;
                int aperture=(int)Math.Clamp(Math.Ceiling(star.Hfr*2),3,16);
                double flux=0, sumX=0, sumY=0; int bright=0;
                int minX=frame.Width,minY=frame.Height,maxX=-1,maxY=-1;
                for (int y=Math.Max(0,py-aperture);y<=Math.Min(frame.Height-1,py+aperture);y++)
                    for (int x=Math.Max(0,px-aperture);x<=Math.Min(frame.Width-1,px+aperture);x++) {
                        double value=Pixel(x,y)-background;
                        if (value<=threshold) continue;
                        bright++; flux+=value; sumX+=value*x; sumY+=value*y;
                        minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);
                    }
                if (bright<3 || flux<=0) continue;
                double xCenter=sumX/flux, yCenter=sumY/flux;
                if ((xCenter-star.X)*(xCenter-star.X)+(yCenter-star.Y)*(yCenter-star.Y)>=radius*radius) continue;
                var bounds=new System.Windows.Rect(minX-.5,minY-.5,maxX-minX+1,maxY-minY+1);
                // Preserve the detector's outer region, translated to the
                // current centroid, and include the current frame's signal.
                if (FocusStarBoxes.HasBounds(star.Bounds)) {
                    var detected=star.Bounds;
                    detected.Offset(xCenter-star.X,yCenter-star.Y);
                    bounds.Union(detected);
                }
                stars.Add(star with { X=xCenter, Y=yCenter, Bounds=bounds });
            }
            return measurement with { StarSamples=stars.ToArray(), SourceFrameId=frame.FrameId };
        }
    }
}
