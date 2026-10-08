using Cwseo.NINA.ManualFocuser.Models;
using Cwseo.NINA.ManualFocuser.Tools.SpikeBatch;
internal static class FieldChecks {
    public static void ReplaySession(string directory,Action<bool,string> check) {
        var parameters=new SpikeAnalysisParams {metricKind=SpikeMetricKind.Fwhm,minUsedStarsForValidFrame=1,
            autoSpikeAngle=true,minimumRoiSizePx=128,resolveParallelSpikes=true};
        var fixedAxis=new SpikeAutofocusAnalysis(parameters);
        SpikeTrackingState originalTracking=null,fixedTracking=null;
        var groups=new Dictionary<int,List<double>>();
        int changedAxes=0,valid=0,invalid=0;
        var originalGroups=new Dictionary<int,List<double>>();
        foreach(string path in Directory.GetFiles(directory,"*.fits").OrderBy(p=>p)) {
            using var meta=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path,".json")));
            var root=meta.RootElement;
            if(root.GetProperty("Phase").GetString()!="measure" || root.GetProperty("ExposureSeconds").GetDouble()!=.0625)continue;
            var image=FitsImage.Load(path);
            if(originalTracking==null) {
                var local=FocusRoi.CenterWindow(image.Data.Select(v=>(double)v).ToArray(),image.Width,image.Height);
                double hfr=QuickFocusMetrics.HalfFluxRadius(local.Pixels,local.Width,local.Height);
                int peak=Array.IndexOf(local.Pixels,local.Pixels.Max()),size=Math.Clamp((int)Math.Ceiling(hfr*4),6,60);
                var seeds=new[]{new SpikeSeedStar{X=peak%local.Width+(image.Width-local.Width)/2,
                    Y=peak/local.Width+(image.Height-local.Height)/2,WidthPx=size,HeightPx=size,MaxBrightness=local.Pixels[peak]}};
                originalTracking=SpikeCore.CreateTrackingState(seeds,parameters);
                fixedTracking=SpikeCore.CreateTrackingState(seeds,parameters);
            }
            var original=SpikeCore.Evaluate(image.Data,image.Width,image.Height,parameters,originalTracking);
            var corrected=fixedAxis.Evaluate(image.Data,image.Width,image.Height,fixedTracking);
            int position=root.GetProperty("FocuserPosition").GetInt32();
            if(!originalGroups.TryGetValue(position,out var oldWidths))originalGroups[position]=oldWidths=new();
            oldWidths.Add(original.Metric);
            Console.WriteLine($"SESSION {Path.GetFileName(path)} pos={position} autoAxis={original.UsedAngleDeg:F2} autoWidth={original.Metric:F2} fixedAxis={corrected.UsedAngleDeg:F2} fixedWidth={corrected.Metric:F2} clear={corrected.HasClearSpikes}");
            if(Math.Abs(original.UsedAngleDeg-fixedAxis.LockedAngleDeg)>45)changedAxes++;
            if(!corrected.IsValid || !corrected.HasClearSpikes)invalid++;
            else {
                valid++;
                if(!groups.TryGetValue(position,out var widths))groups[position]=widths=new();
                widths.Add(corrected.Metric);
            }
            check(Math.Abs(corrected.UsedAngleDeg-fixedAxis.LockedAngleDeg)<1e-9,"Actual field frame keeps the acquisition axis despite changes in the strongest detected line");
        }
        check(changedAxes>0 && valid>0,"Field session reproduces the original perpendicular-axis switching");
        check(invalid==0,"Locked field axis retains the existing diffraction geometry gates on all archived measurement frames");
        if(originalGroups.TryGetValue(99348,out var failedWidths)) {
            check(Math.Abs(failedWidths.Max()-failedWidths.Min()-2.699618383856425)<1e-6,
                "Raw field replay reproduces the exact 2.70 px instability that aborted the original scan");
            check(!SpikeMeasurementBatch.Evaluate(groups[99348].ToArray()).IsStable,
                "Axis locking alone does not pretend the failed three-frame batch is stable; it requires new stationary frames");
        }
        foreach(var pair in groups.Where(p=>p.Value.Count==3)) {
            var batch=SpikeMeasurementBatch.Evaluate(pair.Value.ToArray());
            Console.WriteLine($"SESSION batch pos={pair.Key} median={batch.Median:F3} spread={batch.Spread:F3} limit={batch.Limit:F3} stable={batch.IsStable}");
        }
    }
    public static void Replay(string path,Action<bool,string> check) {
        var image=FitsImage.Load(path);
        var local=FocusRoi.CenterWindow(image.Data.Select(v=>(double)v).ToArray(),image.Width,image.Height);
        double hfr=QuickFocusMetrics.HalfFluxRadius(local.Pixels,local.Width,local.Height);
        int peak=Array.IndexOf(local.Pixels,local.Pixels.Max());
        int size=Math.Clamp((int)Math.Ceiling(hfr*4),6,60);
        var seed=new SpikeSeedStar { X=peak%local.Width+(image.Width-local.Width)/2,
            Y=peak/local.Width+(image.Height-local.Height)/2,WidthPx=size,HeightPx=size,MaxBrightness=local.Pixels[peak] };
        var parameters=new SpikeAnalysisParams {metricKind=SpikeMetricKind.Fwhm,minUsedStarsForValidFrame=1,autoSpikeAngle=true};
        var result=SpikeCore.Evaluate(image.Data,image.Width,image.Height,parameters,SpikeCore.CreateTrackingState(new[]{seed},parameters));
        Console.WriteLine($"FIELD hfr={hfr:F3} seed={seed.X},{seed.Y} size={size} angle={result.MeasuredAngleDeg:F3} strength={result.AngleStrength:F3} valid={result.IsValid} clear={result.HasClearSpikes} width={result.Metric:F3}");
        foreach(var p in result.StarPoints)Console.WriteLine($"FIELD centroid={p.X:F2},{p.Y:F2} FWHM={p.Fwhm:F3} split={p.Separation:F3} SNR={p.ProfileSnr:F2}");
        parameters.minimumRoiSizePx=128; parameters.resolveParallelSpikes=true;
        var corrected=SpikeCore.Evaluate(image.Data,image.Width,image.Height,parameters,SpikeCore.CreateTrackingState(new[]{seed},parameters));
        Console.WriteLine($"FIELD corrected angle={corrected.MeasuredAngleDeg:F3} strength={corrected.AngleStrength:F3} valid={corrected.IsValid} clear={corrected.HasClearSpikes} width={corrected.Metric:F3}");
        foreach(var p in corrected.StarPoints)Console.WriteLine($"FIELD corrected centroid={p.X:F2},{p.Y:F2}");
        check(corrected.IsValid && corrected.HasClearSpikes && double.IsFinite(corrected.Metric),"Archived raw parallel diffraction spikes pass the corrected autofocus geometry check");
        var automatic=SpikeAutoRoi.Find(image.Data.Select(v=>(double)v).ToArray(),image.Width,image.Height,default);
        check(Math.Abs(automatic.Roi.X+automatic.Roi.Width/2.0-automatic.Core.X)<=.5
            && Math.Abs(automatic.Roi.Y+automatic.Roi.Height/2.0-automatic.Core.Y)<=.5,
            "Actual archived star receives a centered automatic acquisition ROI");
        var cropped=new ushort[automatic.Roi.Width*automatic.Roi.Height];
        for(int y=0;y<automatic.Roi.Height;y++)Array.Copy(image.Data,(automatic.Roi.Y+y)*image.Width+automatic.Roi.X,
            cropped,y*automatic.Roi.Width,automatic.Roi.Width);
        var centered=FocusRoi.CenterWindow(cropped.Select(v=>(double)v).ToArray(),automatic.Roi.Width,automatic.Roi.Height);
        double centeredHfr=QuickFocusMetrics.HalfFluxRadius(centered.Pixels,centered.Width,centered.Height);
        int centeredPeak=Array.IndexOf(centered.Pixels,centered.Pixels.Max());
        int centeredSize=Math.Clamp((int)Math.Ceiling(centeredHfr*4),6,60);
        var centeredSeed=new SpikeSeedStar {X=centeredPeak%centered.Width+(automatic.Roi.Width-centered.Width)/2,
            Y=centeredPeak/centered.Width+(automatic.Roi.Height-centered.Height)/2,WidthPx=centeredSize,HeightPx=centeredSize,
            MaxBrightness=centered.Pixels[centeredPeak]};
        var centeredResult=SpikeCore.Evaluate(cropped,automatic.Roi.Width,automatic.Roi.Height,parameters,
            SpikeCore.CreateTrackingState(new[]{centeredSeed},parameters));
        check(double.IsFinite(centeredHfr) && centeredResult.IsValid && centeredResult.HasClearSpikes,
            "Automatic crop retains usable raw diffraction geometry on the actual field frame");
    }
}
