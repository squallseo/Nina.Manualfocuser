using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using OxyPlot;

internal static class LiveHistoryChecks
{
    public static void Run(Action<bool,string> check)
    {
        foreach(int fps in new[]{1,4,20,100})
        {
            var points=new List<DataPoint>();var history=new LiveHfrHistory(points);
            for(int i=0;i<=600*fps;i++)history.Record(i/(double)fps,2+.1*Math.Sin(i));
            var shown=history.DisplayPoints();
            check(shown.Length>0 && shown.All(p=>p.X>=480 && p.X<=600) && shown[^1].X==600 && history.AxisMinimum==480 && history.AxisMaximum==600,
                "Fixed 120-second graph follows the latest frame at "+fps+" FPS");
            check(points.Count<=601 && points[0].X<=480.21,
                "Fast frames retain the full time window with bounded history at "+fps+" FPS");
            check(shown.Zip(shown.Skip(1)).All(p=>p.First.X<p.Second.X),
                "Bucket updates never duplicate or reverse graph timestamps at "+fps+" FPS");
            history.Record(1000,double.NaN);
            check(history.DisplayPoints().Length==0 && history.AxisMinimum==880 && history.AxisMaximum==1000,
                "Long missing-frame interval removes all stale star measurements at "+fps+" FPS");
            history.Clear();history.Record(0,1.2);
            check(points.Count==1 && history.DisplayPoints()[0].X==0 && history.DisplayPoints()[0].Y==1.2,
                "A restarted run starts at zero with no previous session at "+fps+" FPS");
        }
        var gapPoints=new List<DataPoint>();var gaps=new LiveHfrHistory(gapPoints);
        gaps.Record(0,2);gaps.Record(.05,double.NaN);gaps.Record(.15,2.1);gaps.Record(.21,2.2);
        check(gaps.DisplayPoints().Length==2 && gaps.DisplayPoints()[0].Y==2.1 && gaps.DisplayPoints()[^1].Y==2.2,
            "Brief star loss keeps valid measurements connected, including recovery within a sampling bin");
        gaps.Record(.25,double.NaN);
        check(gaps.DisplayPoints()[^1].X==.21 && gaps.DisplayPoints()[^1].Y==2.2,
            "A missing detection cannot erase the previous valid point in the same sampling bin");
        gaps.Record(.4,double.NaN);gaps.Record(.6,double.PositiveInfinity);gaps.Record(.8,0);gaps.Record(.9,-1);gaps.Record(1,2.5);
        check(gaps.DisplayPoints().Length==3 && gaps.DisplayPoints().All(p=>double.IsFinite(p.Y) && p.Y>0) && gaps.DisplayPoints()[^1].X==1,
            "An extended detection gap connects measured endpoints without zero, NaN or fabricated HFR values");
        gaps.Record(121,double.NaN);
        check(gapPoints.All(p=>p.X>=1),"Missing-star frames advance retention even without a valid HFR");
        var startup=new LiveHfrHistory(new List<DataPoint>());
        check(startup.AxisMinimum==0 && startup.AxisMaximum==120,"Empty live history starts with a nonnegative 0-to-120 second axis");
        startup.Record(1,2);startup.Record(61,2.1);
        check(startup.DisplayPoints()[0].X==0 && startup.DisplayPoints()[^1].X==60 && startup.AxisMinimum==0 && startup.AxisMaximum==120,
            "The first 120 seconds fill the graph from left to right");
        startup.Record(121,2);startup.Record(121.2,2.1);
        check(Math.Abs(startup.AxisMinimum-.2)<1e-8 && Math.Abs(startup.AxisMaximum-120.2)<1e-8 && startup.DisplayPoints().All(p=>p.X>=0),
            "The filled time window advances smoothly while timestamps stay positive");
        startup.Clear();startup.Record(500,2);
        check(startup.LatestTime==0 && startup.AxisMinimum==0 && startup.AxisMaximum==120,"Clearing during a stream resets the plotted time origin");
    }
}
