using System.Windows;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

internal static class StarBoxChecks {
    public static void Run(Action<bool,string> check) {
        var frame=new FocusRawFrame(new ushort[1536*1536],1536,0,0,1536,1536);
        var measurement=new FocusHfrMeasurement(3,.2,3) {
            Width=1536,Height=1536,
            StarSamples=new[]{new FocusStarSample(50,50,2){Bounds=new Rect(44,45,12,10)},
                new FocusStarSample(768,768,3){Bounds=new Rect(760,762,16,12)},
                new FocusStarSample(1480,1480,4){Bounds=new Rect(1470,1473,20,14)}}
        };
        var boxes=FocusStarBoxes.Project(measurement,frame,false);
        check(boxes.Length==3 && boxes[0]==new Rect(43,43,14,14) && boxes[2]==new Rect(1469,1469,22,22) &&
            boxes.Zip(measurement.StarSamples).All(pair=>pair.First.Contains(pair.Second.Bounds)),
            "Ordinary view squares enclose the detector's full star bounds with clearance, rather than only the half-flux core");
        boxes=FocusStarBoxes.Project(measurement,frame,true);
        check(boxes.Length==3 && boxes[1]==new Rect(767,767,18,18) && boxes[2]==new Rect(1485,1485,22,22),
            "3x3 overlays map the middle and right-bottom stars into the correct tiles, including gutters");
        var changed=new FocusRawFrame(frame.Pixels,1536,32,32,1024,1024);
        check(FocusStarBoxes.Project(measurement,changed,false).Length==0,"Changing ROI cannot paint stale star positions from an incompatible analysis crop");
        var invalid=measurement with {StarSamples=new[]{new FocusStarSample(0,0,2){Bounds=new Rect(-3,-2,6,4)},
            new FocusStarSample(-1,50,2),new FocusStarSample(50,50,double.NaN),new FocusStarSample(50,50,2)}};
        boxes=FocusStarBoxes.Project(invalid,frame,false);
        check(boxes.Length==1 && boxes[0]==new Rect(0,0,4,4),"Edge boxes are clipped and invalid, missing bounds or out-of-ROI star measurements are omitted");
    }
}
