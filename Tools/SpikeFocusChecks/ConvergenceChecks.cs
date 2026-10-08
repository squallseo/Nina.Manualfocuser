using Cwseo.NINA.ManualFocuser.Models;
using Cwseo.NINA.ManualFocuser.Tools.SpikeBatch;
using System.Text.Json;

internal static class ConvergenceChecks {
    private static SpikeAnalysisParams Parameters() => new() {
        metricKind=SpikeMetricKind.Fwhm,minUsedStarsForValidFrame=1,
        autoSpikeAngle=true,minimumRoiSizePx=128,resolveParallelSpikes=true,adaptiveCentroidWindow=true
    };
    public static void Replay(string directory,Action<bool,string> check) {
        var baseline=Parameters();
        var adaptive=new SpikeAutofocusAnalysis(baseline);
        SpikeTrackingState originalTracking=null,adaptiveTracking=null;
        double exposure=double.NaN;
        int frames=0,recovered=0,clipped=0;
        foreach(string path in Directory.GetFiles(directory,"*.fits").OrderBy(p=>p)) {
            using var metadata=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path,".json")));
            var root=metadata.RootElement;
            if(root.GetProperty("Phase").GetString()!="measure")continue;
            var image=FitsImage.Load(path);
            if(FocusSaturation.HasClippedAnalysisArea(image.Data.Select(v=>(double)v).ToArray(),image.Width,image.Height,false)) {
                clipped++;
                continue;
            }
            double currentExposure=root.GetProperty("ExposureSeconds").GetDouble();
            var local=FocusRoi.CenterWindow(image.Data.Select(v=>(double)v).ToArray(),image.Width,image.Height);
            double hfr=QuickFocusMetrics.HalfFluxRadius(local.Pixels,local.Width,local.Height);
            if(originalTracking==null || exposure!=currentExposure) {
                exposure=currentExposure;
                baseline.autoSpikeAngle=true;
                adaptive.RestartScan();
                int peak=Array.IndexOf(local.Pixels,local.Pixels.Max()),size=Math.Clamp((int)Math.Ceiling(hfr*4),6,60);
                var seeds=new[]{new SpikeSeedStar{X=peak%local.Width+(image.Width-local.Width)/2,
                    Y=peak/local.Width+(image.Height-local.Height)/2,WidthPx=size,HeightPx=size,MaxBrightness=local.Pixels[peak]}};
                originalTracking=SpikeCore.CreateTrackingState(seeds,baseline);
                adaptiveTracking=SpikeCore.CreateTrackingState(seeds,baseline);
            }
            var raw=(ushort[])image.Data.Clone();
            var original=SpikeCore.Evaluate(image.Data,image.Width,image.Height,baseline,originalTracking);
            if(baseline.autoSpikeAngle && original.IsValid && original.HasClearSpikes) {
                baseline.spikeAngleDeg=original.UsedAngleDeg;
                baseline.autoSpikeAngle=false;
            }
            var corrected=adaptive.Evaluate(image.Data,image.Width,image.Height,adaptiveTracking,hfr);
            int position=root.GetProperty("FocuserPosition").GetInt32();
            Console.WriteLine($"CONVERGENCE {Path.GetFileName(path)} pos={position} footprint={Math.Ceiling(hfr*4)} width={corrected.Metric:F3} fixed={original.HasClearSpikes} adaptive={corrected.HasClearSpikes}");
            check(corrected.IsValid && corrected.HasClearSpikes,"Shrinking field footprint retains bilateral diffraction evidence");
            check(Math.Abs(original.Metric-corrected.Metric)<1e-9 && original.UsedAngleDeg==corrected.UsedAngleDeg,
                "Adaptive evidence aperture preserves the exact original width and physical axis");
            check(originalTracking.TrackedStars[0].BaseSizePx==adaptiveTracking.TrackedStars[0].BaseSizePx && image.Data.SequenceEqual(raw),
                "Evidence adjustment leaves the acquisition crop and raw pixels unchanged");
            if(!original.HasClearSpikes)recovered++;
            frames++;
        }
        check(frames==23 && recovered==1 && clipped==1,"Full field replay reproduces and recovers the failure at position 101848, excluding the clipped exposure");
    }
    public static void Synthetic(Action<bool,string> check) {
        const int n=512;
        double G(double z,double sigma)=>Math.Exp(-z*z/(2*sigma*sigma));
        foreach(double sigma in new[]{2.0,7,12})foreach(bool elliptical in new[]{false,true}) {
            var random=new Random(651);
            var pixels=Enumerable.Range(0,n*n).Select(i=> {
                double x=i%n-256,y=i/n-256;
                return (ushort)(300+random.Next(15)+40000*(elliptical?G(x,sigma)*G(y,sigma/2):G(Math.Sqrt(x*x+y*y),sigma)));
            }).ToArray();
            var parameters=Parameters();
            var tracking=SpikeCore.CreateTrackingState(new[]{new SpikeSeedStar{X=256,Y=256,WidthPx=60,HeightPx=60,MaxBrightness=40000}},parameters);
            var result=new SpikeAutofocusAnalysis(parameters).Evaluate(pixels,n,n,tracking);
            check(!result.HasClearSpikes,$"Shrinking evidence aperture rejects a {(elliptical?"elliptical":"circular")} star, sigma {sigma}");
        }
    }
}
