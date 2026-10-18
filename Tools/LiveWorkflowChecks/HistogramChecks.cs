using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

internal static class HistogramChecks {
    public static void Run(Action<bool,string> check) {
        var values=Enumerable.Range(0,65536).Select(i=>(ushort)i).ToArray();
        var frame=new FocusRawFrame(values,256,0,0,256,256);
        FocusHistogram captured=null;
        var rendered=FocusDisplayStretch.RenderRaw(frame,histogram:h=>captured=h);
        var statistics=FocusDisplayStretch.GetHistogram(frame);
        check(ReferenceEquals(statistics,captured) && ReferenceEquals(statistics,FocusDisplayStretch.GetHistogram(frame)),"Rendering and plotting share the exact cached histogram without another image pass");
        check(statistics.PixelCount==65536 && statistics.OccupiedLevels==65536,"Sparse histogram retains every occupied 16-bit input level exactly");
        var plot=FocusDisplayStretch.PlotHistogram(statistics);
        var counts=new long[256];foreach(byte value in rendered)counts[value]++;
        check(Enumerable.Range(0,256).All(i=>plot.DisplayAt(i)==counts[i]),"Projected output counts exactly match all 65536 displayed pixels");
        check(Enumerable.Range(0,256).Sum(i=>plot.InputAt(i))==values.Length && Enumerable.Range(0,256).Sum(i=>plot.DisplayAt(i))==values.Length,"Both plot curves preserve total pixel counts");
        var linear=FocusDisplayStretch.PlotHistogram(statistics,autoStretch:false);
        var reference=FocusDisplayStretch.RenderRaw(frame,autoStretch:false);
        Array.Clear(counts);foreach(byte value in reference)counts[value]++;
        check(Enumerable.Range(0,256).All(i=>linear.DisplayAt(i)==counts[i]),"Linear-mode histogram matches the sensor-bit-depth display");
        var clipped=FocusDisplayStretch.PlotHistogram(statistics,levels:new(0,.5,.2));
        check(clipped.ShadowsPercent>49 && clipped.HighlightsPercent>49,"Shadow and highlight readouts count actual transfer endpoints");
        var narrow=new FocusRawFrame(Enumerable.Range(0,65536).Select(i=>(ushort)(500+i%8)).ToArray(),256,0,0,256,256);
        var retained=FocusDisplayStretch.GetHistogram(narrow);
        check(retained.OccupiedLevels==8 && retained.StorageBytes==48,"A narrow focusing exposure retains a few exact count pairs instead of a 256 KiB histogram");
        var retainedPlot=FocusDisplayStretch.PlotHistogram(retained);
        for(int i=0;i<12;i++)_ = FocusDisplayStretch.GetHistogram(new FocusRawFrame(Enumerable.Repeat((ushort)(i*1000),4096).ToArray(),64,0,0,64,64));
        var afterPoolReuse=FocusDisplayStretch.PlotHistogram(retained);
        check(Enumerable.Range(0,256).All(i=>retainedPlot.DisplayAt(i)==afterPoolReuse.DisplayAt(i)),"Returning and reusing worker arrays cannot corrupt a retained histogram");
        var pads=Enumerable.Repeat((ushort)65535,60*70).ToArray();
        for(int y=11;y<43;y++)for(int x=7;x<23;x++)pads[y*60+x]=500;
        var roi=FocusDisplayStretch.GetHistogram(new FocusRawFrame(pads,60,7,11,16,32));
        check(roi.OccupiedLevels==1 && roi.PixelCount==512 && roi.Median==500,"Raw histogram uses only the ROI, excluding padded rows and columns");
        var build=typeof(FocusHistogramView).GetMethod("BuildCurve",BindingFlags.NonPublic|BindingFlags.Static);
        foreach(bool logarithmic in new[]{false,true})foreach(bool output in new[]{false,true}) {
            var geometry=(StreamGeometry)build.Invoke(null,new object[]{retainedPlot,new Size(340,112),logarithmic,output});
            check(geometry.IsFrozen && !geometry.Bounds.IsEmpty && geometry.Bounds.Top>=0 && geometry.Bounds.Bottom<=112 && geometry.Bounds.Right<=340,
                $"Histogram curve is a bounded frozen vector path (log={logarithmic}, display={output})");
        }
    }
}
