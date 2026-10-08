using Cwseo.NINA.ManualFocuser.Models;

internal static class AutoRoiChecks {
    public static void Run(Action<bool,string> check) {
        const int n=1024;
        double[] Scout(double x,double y,double spikeLength=0,bool saturated=false) {
            var random=new Random(156);
            return Enumerable.Range(0,n*n).Select(i=> {
                double dx=i%n-x,dy=i/n-y;
                double v=300+random.NextDouble()*20-10+ (saturated?200000:30000)*Math.Exp(-(dx*dx+dy*dy)/8);
                if(spikeLength>0) v+=1800*(Math.Exp(-dy*dy/4)*Math.Exp(-dx*dx/(2*spikeLength*spikeLength))
                    +Math.Exp(-dx*dx/4)*Math.Exp(-dy*dy/(2*spikeLength*spikeLength)));
                return Math.Clamp(Math.Round(v),0,65535);
            }).ToArray();
        }
        bool Rejected(Action action) { try { action();return false; } catch(InvalidOperationException) { return true; } }
        var compact=Scout(550.3,485.8);
        compact[520*n+720]=65535; // Isolated hot pixel within the search and radius checks.
        var original=(double[])compact.Clone();
        var selected=SpikeAutoRoi.Find(compact,n,n,default);
        check(selected.Roi.Width==256 && selected.Roi.Height==256,"Compact core receives a compact automatic acquisition ROI");
        check(Math.Abs(selected.Core.X-550.3)<1 && Math.Abs(selected.Core.Y-485.8)<1,
            "Automatic Spike ROI centers the supported star rather than the brighter isolated defect");
        check(Math.Abs(selected.Roi.X+selected.Roi.Width/2.0-selected.Core.X)<=.5
            && Math.Abs(selected.Roi.Y+selected.Roi.Height/2.0-selected.Core.Y)<=.5,
            "Selected core sits at the actual crop center without position typing");
        check(compact.SequenceEqual(original),"Automatic ROI selection leaves all raw analysis pixels unchanged");
        var shortSpike=SpikeAutoRoi.Find(Scout(550.3,485.8,45),n,n,default);
        var longSpike=SpikeAutoRoi.Find(Scout(550.3,485.8,80),n,n,default);
        check(shortSpike.Roi.Width==384 && longSpike.Roi.Width==512,
            "Outer supported diffraction extent expands the automatic ROI from 384 to 512 pixels");
        check(longSpike.SupportedRadius>shortSpike.SupportedRadius && shortSpike.SupportedRadius>selected.SupportedRadius,
            "Acquisition size follows supported outer light instead of the user's previous ROI size");
        var clipped=SpikeAutoRoi.Find(Scout(550,486,80,saturated:true),n,n,default);
        check(Math.Abs(clipped.Core.X-550)<2 && Math.Abs(clipped.Core.Y-486)<2,
            "Clipped scout can be centered before the existing automatic exposure recovery");
        var edge=SpikeAutoRoi.Find(Scout(170,510,80),n,n,default,170,510);
        check(edge.Roi.Width==256 && edge.PreferredSize==512 && edge.Roi.X>=0,
            "Sensor-edge scout searches near the original selection and fits a smaller centered ROI");
        check(Math.Abs(edge.Roi.X+edge.Roi.Width/2.0-edge.Core.X)<=.5,
            "Edge handling never shifts the crop away from the chosen star");
        check(Rejected(()=>SpikeAutoRoi.Find(Scout(60,510,80),n,n,default,60,510)),
            "Unusable sensor-edge star fails before inventing an off-center autofocus ROI");
        check(Rejected(()=>SpikeAutoRoi.Find(new double[n*n],n,n,default)),"Blank scout never invents a star or ROI");
        var defect=new double[n*n];defect[512*n+512]=65535;
        check(Rejected(()=>SpikeAutoRoi.Find(defect,n,n,default)),"Isolated hot pixel cannot establish an autofocus ROI");
        using var canceled=new CancellationTokenSource();canceled.Cancel();bool stopped=false;
        try { SpikeAutoRoi.Find(compact,n,n,canceled.Token); } catch(OperationCanceledException) { stopped=true; }
        check(stopped,"Stop cancels automatic ROI selection before accepting any crop");
        bool bad=false; compact[0]=double.NaN;
        try { SpikeAutoRoi.Find(compact,n,n,default); } catch(ArgumentException) { bad=true; }
        check(bad,"Nonfinite scout data rejects rather than changing the camera ROI");
    }
}
