using System.Diagnostics;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

internal static class StarTrackingChecks {
    public static void Run(Action<bool,string> check) {
        const int size=256;
        FocusHfrMeasurement Measurement(params FocusStarSample[] samples) => new(99,1,samples.Length) {
            Width=size,Height=size,StarSamples=samples
        };
        var tracker=new FocusStarTracker();
        var first=tracker.Match(Measurement(new FocusStarSample(40,40,2),new FocusStarSample(100,50,4),new FocusStarSample(180,180,3)));
        check(first.Stars==3 && first.Hfr==3,"Tracking starts from the selected multi-star set and uses its median HFR");
        var next=tracker.Match(Measurement(new FocusStarSample(180,179,3.2),new FocusStarSample(220,220,.5),new FocusStarSample(41,41,2.2),new FocusStarSample(101,49,4.2)));
        check(next.Stars==3 && next.Hfr==3.2 && !next.StarSamples.Any(s=>s.X==220),"A newly bright star does not replace an existing tracked star when detections reorder");
        var lost=tracker.Match(Measurement(new FocusStarSample(42,42,2.4),new FocusStarSample(102,49,4.4),new FocusStarSample(220,220,.4)));
        check(lost.Stars==2 && Math.Abs(lost.Hfr-3.4)<1e-12,"A temporarily lost star is excluded from HFR without substituting a distant detection");
        var recovered=tracker.Match(Measurement(new FocusStarSample(180,180,3.4),new FocusStarSample(42,42,2.4),new FocusStarSample(102,49,4.4)));
        check(recovered.Stars==3,"A temporarily lost tracked star can reappear with its original identity");
        var absent=tracker.Match(Measurement(new FocusStarSample(230,230,1)));
        check(absent.Stars==0 && double.IsNaN(absent.Hfr),"Losing every tracked star produces no valid HFR instead of silently selecting another field");
        tracker.Reset();
        check(tracker.Match(Measurement(new FocusStarSample(230,230,1))).Stars==1,"Resetting tracking explicitly allows a new reference star set");
        tracker.Reset();tracker.Match(Measurement(new FocusStarSample(50,50,2),new FocusStarSample(55,50,2)));
        check(tracker.Match(Measurement(new FocusStarSample(52,50,2))).Stars==1,"One current detection cannot be counted as two reference stars");
        var outside=Measurement(new FocusStarSample(64,64,2)) with {Left=32,Width=128};
        check(tracker.Match(outside).Stars==1,"Changing capture ROI resets incompatible tracking coordinates");
        FocusRawFrame Frame(double x,double y,bool hot=false) {
            var pixels=Enumerable.Repeat((ushort)400,size*size).ToArray();
            if(hot) pixels[(int)y*size+(int)x]=60000;
            else for(int row=Math.Max(0,(int)y-12);row<=Math.Min(size-1,(int)y+12);row++)
                for(int col=Math.Max(0,(int)x-12);col<=Math.Min(size-1,(int)x+12);col++)
                    pixels[row*size+col]+=(ushort)(30000*Math.Exp(-((col-x)*(col-x)+(row-y)*(row-y))/8));
            return new FocusRawFrame(pixels,size,0,0,size,size);
        }
        var oldFrame=Frame(64,64);var current=Frame(69,61);
        var oldMeasurement=Measurement(new FocusStarSample(64,64,2){Bounds=new System.Windows.Rect(56,57,16,14)}) with {SourceFrameId=oldFrame.FrameId};
        var located=FocusStarBoxLocator.Locate(oldMeasurement,current);
        check(located.SourceFrameId==current.FrameId && located.SourceFrameId!=oldMeasurement.SourceFrameId &&
            located.StarSamples.Length==1 && Math.Abs(located.StarSamples[0].X-69)<.1 && Math.Abs(located.StarSamples[0].Y-61)<.1,
            "Delayed HFR boxes use the CURRENT frame's centroid and frame identity, not stale analyzed coordinates");
        check(located.StarSamples[0].Hfr==2,"Centroid refresh reuses the measured HFR without another HFR computation");
        var enclosing=FocusStarBoxes.Project(located,current,false).Single();
        check(enclosing.Width==enclosing.Height && enclosing.Width>oldMeasurement.StarSamples[0].Hfr*2 &&
            enclosing.Contains(new System.Windows.Rect(61,54,16,14)) && enclosing.Contains(new System.Windows.Rect(65,57,9,9)),
            "A delayed-frame square encloses the translated detector bounds and current star pixels, not just the HFR core");
        check(FocusStarBoxLocator.Locate(oldMeasurement,Frame(180,180)).StarSamples.Length==0,
            "An unrelated simulator image hides boxes instead of painting them at empty old star positions");
        check(FocusStarBoxLocator.Locate(oldMeasurement,Frame(64,64,true)).StarSamples.Length==0,
            "An isolated hot pixel cannot attract a star box");
        var copy=(ushort[])current.Pixels.Clone();
        for(int i=0;i<12000;i++) {
            var result=FocusStarBoxLocator.Locate(oldMeasurement,current);
            if(result.StarSamples.Length!=1 || Math.Abs(result.StarSamples[0].X-69)>.1 || Math.Abs(result.StarSamples[0].Y-61)>.1)
                throw new Exception("Hot locator changed star coordinates at iteration " + i);
        }
        check(copy.SequenceEqual(current.Pixels),"Twelve thousand warmed-up centroid updates preserve pixel coordinates and raw image data");
        const int width=1024;
        var manyPixels=Enumerable.Repeat((ushort)400,width*width).ToArray();
        var manyStars=new List<FocusStarSample>();
        for(int y=20;y<1000;y+=30) for(int x=20;x<1000;x+=30) {
            manyStars.Add(new(x,y,2));
            for(int dy=-6;dy<=6;dy++) for(int dx=-6;dx<=6;dx++)
                manyPixels[(y+dy)*width+x+dx]+=(ushort)(30000*Math.Exp(-(dx*dx+dy*dy)/8.0));
        }
        var manyFrame=new FocusRawFrame(manyPixels,width,0,0,width,width);
        var manyMeasurement=new FocusHfrMeasurement(2,.1,manyStars.Count){Width=width,Height=width,StarSamples=manyStars.ToArray()};
        for(int i=0;i<10;i++)FocusStarBoxLocator.Locate(manyMeasurement,manyFrame);
        var times=new List<double>();
        for(int i=0;i<30;i++) {
            var clock=Stopwatch.StartNew();var result=FocusStarBoxLocator.Locate(manyMeasurement,manyFrame);clock.Stop();
            if(result.StarSamples.Length!=manyStars.Count)throw new Exception("Dense centroid test lost stars.");
            times.Add(clock.Elapsed.TotalMilliseconds);
        }
        times.Sort();Console.WriteLine($"Centroid-only refresh: {manyStars.Count} stars, median {times[15]:F2} ms, max {times[^1]:F2} ms; no detection or HFR recalculation.");
        check(true,"Dense-field annotation benchmark locates every current star");
    }
}
