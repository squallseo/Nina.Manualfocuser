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
            check(shown.Length>0 && shown.All(p=>p.X>=-120 && p.X<=0) && shown[^1].X==0,
                "Fixed 120-second graph follows the latest frame at "+fps+" FPS");
            check(points.Count<=601 && points[0].X<=480.21,
                "Fast frames retain the full time window with bounded history at "+fps+" FPS");
            check(shown.Zip(shown.Skip(1)).All(p=>p.First.X<p.Second.X),
                "Bucket updates never duplicate or reverse graph timestamps at "+fps+" FPS");
            history.Record(1000,double.NaN);
            check(history.DisplayPoints().Length==1 && double.IsNaN(history.DisplayPoints()[0].Y),
                "Long missing-frame interval removes all stale star measurements at "+fps+" FPS");
            history.Clear();history.Record(0,1.2);
            check(points.Count==1 && history.DisplayPoints()[0].X==0 && history.DisplayPoints()[0].Y==1.2,
                "A restarted run starts at the right edge with no previous session at "+fps+" FPS");
        }
        var gapPoints=new List<DataPoint>();var gaps=new LiveHfrHistory(gapPoints);
        gaps.Record(0,2);gaps.Record(.05,double.NaN);gaps.Record(.15,2.1);gaps.Record(.21,2.2);
        check(double.IsNaN(gaps.DisplayPoints()[0].Y) && gaps.DisplayPoints()[^1].Y==2.2,
            "Brief star loss remains a line break when detection recovers within the graph sampling bin");
        gaps.Record(121,double.NaN);
        check(gapPoints.All(p=>p.X>=1),"Missing-star frames advance retention even without a valid HFR");
    }
}
