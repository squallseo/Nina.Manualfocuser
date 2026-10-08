using Cwseo.NINA.ManualFocuser.Models;
using Cwseo.NINA.ManualFocuser.Tools.SpikeBatch;
internal static class DefocusChecks {
    private static SpikeAnalysisParams Parameters() => new() {metricKind=SpikeMetricKind.Fwhm,minUsedStarsForValidFrame=1,
        autoSpikeAngle=true,minimumRoiSizePx=128,resolveParallelSpikes=true};
    private static SpikeTrackingState Seed(FitsImage image,SpikeAnalysisParams parameters) {
        var local=FocusRoi.CenterWindow(image.Data.Select(v=>(double)v).ToArray(),image.Width,image.Height);
        double hfr=QuickFocusMetrics.HalfFluxRadius(local.Pixels,local.Width,local.Height);
        int peak=Array.IndexOf(local.Pixels,local.Pixels.Max()),size=Math.Clamp((int)Math.Ceiling(hfr*4),6,60);
        return SpikeCore.CreateTrackingState(new[]{new SpikeSeedStar {
            X=peak%local.Width+(image.Width-local.Width)/2,Y=peak/local.Width+(image.Height-local.Height)/2,
            WidthPx=size,HeightPx=size,MaxBrightness=local.Pixels[peak]}},parameters);
    }
    public static void Replay(string directory,Action<bool,string> check) {
        var image=FitsImage.Load(Path.Combine(directory,"000008.fits"));
        var parameters=Parameters();
        var oldTracking=Seed(image,parameters);
        var original=SpikeCore.Evaluate(image.Data,image.Width,image.Height,parameters,oldTracking);
        check(original.IsValid,"Archived defocused image retains its measurable raw profile with compact centroid tracking");
        var analysis=new SpikeAutofocusAnalysis(parameters);
        var corrected=analysis.Evaluate(image.Data,image.Width,image.Height,Seed(image,parameters));
        Console.WriteLine($"DEFOCUS old center={oldTracking.TrackedStars[0].X:F2},{oldTracking.TrackedStars[0].Y:F2}; corrected center={corrected.StarPoints[0].X:F2},{corrected.StarPoints[0].Y:F2} angle={corrected.UsedAngleDeg:F2} width={corrected.Metric:F2}");
        check(corrected.IsValid && corrected.HasClearSpikes && double.IsFinite(corrected.Metric),
            "Actual defocused diffraction frame passes geometry/SNR gates with footprint-sized centroid tracking");
        check(Math.Abs(corrected.StarPoints[0].Y-oldTracking.TrackedStars[0].Y)>5,
            "Corrected tracking moves away from the bright rim toward the stellar footprint center");
        var raw=(ushort[])image.Data.Clone();
        var oldExposure=FitsImage.Load(Path.Combine(directory,"000004.fits"));
        var resumed=new SpikeAutofocusAnalysis(parameters);
        var initial=resumed.Evaluate(oldExposure.Data,oldExposure.Width,oldExposure.Height,Seed(oldExposure,parameters));
        Console.WriteLine($"DEFOCUS initial high-exposure angle={initial.UsedAngleDeg:F2} clear={initial.HasClearSpikes}");
        resumed.RestartScan();
        var restarted=resumed.Evaluate(image.Data,image.Width,image.Height,Seed(image,parameters));
        check(restarted.HasClearSpikes && restarted.UsedAngleDeg==resumed.LockedAngleDeg,
            "Discarded exposure curve reacquires a valid axis from the actual low-exposure image");
        check(image.Data.SequenceEqual(raw),"Defocus recovery preserves the raw measurement pixels");
    }
    public static void ReplayFrame(string path,Action<bool,string> check) {
        var image=FitsImage.Load(path);
        var raw=(ushort[])image.Data.Clone();
        var parameters=Parameters();
        var analysis=new SpikeAutofocusAnalysis(parameters);
        var result=analysis.Evaluate(image.Data,image.Width,image.Height,Seed(image,parameters));
        Console.WriteLine($"FIELD {Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)} center={result.StarPoints[0].X:F2},{result.StarPoints[0].Y:F2} angle={result.UsedAngleDeg:F2} width={result.Metric:F2} clear={result.HasClearSpikes}");
        check(result.IsValid && result.HasClearSpikes && double.IsFinite(result.Metric) && result.Metric>0,
            "Archived low-exposure failure frame establishes a valid diffraction width");
        check(double.IsFinite(analysis.LockedAngleDeg) && analysis.LockedAngleDeg==result.UsedAngleDeg,
            "Recovered diffraction geometry acquires the physical measurement axis");
        check(image.Data.SequenceEqual(raw),"Recovered field classification leaves raw measurement samples unchanged");
    }
    public static void Synthetic(Action<bool,string> check) {
        const int n=512;
        double G(double z,double sigma)=>Math.Exp(-z*z/(2*sigma*sigma));
        foreach(double angle in new[]{0.0,37,86})foreach(bool spikes in new[]{true,false}) {
            double a=angle*Math.PI/180;
            var image=Enumerable.Range(0,n*n).Select(i=> {
                double x=i%n-256,y=i/n-256,s=x*Math.Cos(a)+y*Math.Sin(a),u=-x*Math.Sin(a)+y*Math.Cos(a);
                double value=300+4*Math.Sin(i*1.232)+35000*G(Math.Sqrt(x*x+y*y)-25,3);
                if(spikes)value+=2000*(G(u-12,2)+G(u+12,2))*G(s,100);
                return (ushort)Math.Clamp(value,0,65535);
            }).ToArray();
            var parameters=Parameters();
            var tracking=SpikeCore.CreateTrackingState(new[]{new SpikeSeedStar {X=256,Y=231,WidthPx=60,HeightPx=60,MaxBrightness=37000}},parameters);
            var result=new SpikeAutofocusAnalysis(parameters).Evaluate(image,n,n,tracking);
            Console.WriteLine($"SYNTHETIC defocus angle={angle} spikes={spikes} center={tracking.TrackedStars[0].X:F2},{tracking.TrackedStars[0].Y:F2} used={result.UsedAngleDeg:F2} strength={result.AngleStrength:F2} width={result.Metric:F2} clear={result.HasClearSpikes}");
            check(Math.Abs(tracking.TrackedStars[0].Y-256)<3,"Wide defocused footprint centroid recovers from a seed on the bright rim");
            check(result.HasClearSpikes==spikes,$"Defocused ring at {angle} degrees requires real bilateral diffraction lines");
        }
    }
}
