using System;
using System.Linq;
using System.Threading;

namespace Cwseo.NINA.ManualFocuser.Models {
    public static class BahtinovAutoRoi {
        public static (FocusRoi.Rectangle Roi, BahtinovMeasurement Measurement) Find(double[] pixels,int width,int height,CancellationToken token,
            double? searchX=null,double? searchY=null) {
            var core=FocusStarLocator.Find(pixels,width,height,token,searchX,searchY);
            double cx=core.X,cy=core.Y;
            FocusRoi.Rectangle chosen=default; BahtinovMeasurement measurement=null;
            foreach(int size in new[]{128,192,256,384,512}) {
                token.ThrowIfCancellationRequested();
                int x=(int)Math.Round(cx-size/2.0),y=(int)Math.Round(cy-size/2.0);
                // Never shift the crop off the star to fit an edge, or silently clip a line.
                if(x<0||y<0||x+size>width||y+size>height) continue;
                var roi=new FocusRoi.Rectangle(x,y,size,size);
                var cropped=Crop(pixels,width,height,roi);
                var candidate=BahtinovAnalyzer.Analyze(cropped,size,size);
                if(!candidate.IsValid||FocusSaturation.HasClippedAnalysisArea(cropped,size,size,true)) continue;
                if(measurement==null||candidate.Confidence>measurement.Confidence) { chosen=roi;measurement=candidate; }
            }
            if(measurement==null) throw new InvalidOperationException("No centered ROI with three usable unsaturated mask lines. Diagnostic scout saved.");
            return(chosen,measurement);
        }
        public static double[] Crop(double[] pixels,int width,int height,FocusRoi.Rectangle roi) {
            if(pixels==null||(long)width*height!=pixels.Length||roi.X<0||roi.Y<0||roi.Width<1||roi.Height<1||
                (long)roi.X+roi.Width>width||(long)roi.Y+roi.Height>height) throw new ArgumentException("Invalid analysis crop.");
            var cropped=new double[checked(roi.Width*roi.Height)];
            for(int y=0;y<roi.Height;y++) Array.Copy(pixels,(roi.Y+y)*width+roi.X,cropped,y*roi.Width,roi.Width);
            return cropped;
        }
    }
}
