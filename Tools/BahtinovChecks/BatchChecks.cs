using Cwseo.NINA.ManualFocuser.Models;
using System.Text.Json;

internal static class BatchChecks {
    public static async Task Run(Action<bool, string> check) {
        async Task<(BahtinovBatchResult Result, int Reads, int Extensions)> Collect(double[] input) {
            int reads=0, extensions=0;
            var result=await BahtinovMeasurementBatch.CollectAsync(t=>Task.FromResult(input[reads++]),()=>extensions++,default);
            return (result,reads,extensions);
        }
        var fast=await Collect(new[]{-4.33,-5.09,-4.16});
        check(fast.Result.IsStable && fast.Reads==3 && fast.Extensions==0 && fast.Result.Median==-4.33,
            "Stable mask batches keep the three-frame fast path");
        double[] fieldTriplet={.25999325113225447,-.8153571950610692,.20788010662375728};
        check(!BahtinovMeasurementBatch.Evaluate(fieldTriplet).IsStable,
            "Recorded near-focus outlier requests more evidence rather than accepting two frames");
        // Only the first three numbers were observed; the follow-up frames here are a controlled test.
        var near=await Collect(fieldTriplet.Concat(new[]{.19,.22,.18,.23}).ToArray());
        check(near.Reads==7 && near.Extensions==1 && near.Result.IsStable && near.Result.InlierCount==6,
            "Consistent stationary follow-up frames tolerate one recorded outlier");
        check(Math.Abs(near.Result.Median-.20788010662375728)<1e-12 && near.Result.Spread>1,
            "Confirmed batch uses signed median and retains the raw excursion for diagnostics");
        int motorMoves=0, frameReads=0;
        var confirmingFrames=fieldTriplet.Concat(new[]{.19,.22,.18,.23,.21,.22,.2}).ToArray();
        var focused=await BahtinovFocusRunner.RunAsync(97438,2500,2,
            (p,t)=> { motorMoves++; return Task.FromResult(p); },
            async(p,t)=> {
                var batch=await BahtinovMeasurementBatch.CollectAsync(c=>Task.FromResult(confirmingFrames[frameReads++]),null,t);
                if(!batch.IsStable) throw new InvalidOperationException("unstable");
                return batch.Median;
            },default);
        check(motorMoves==0 && frameReads==10 && focused.Position==97438 && Math.Abs(focused.Error)<=.25,
            "Runner confirms a near-zero robust batch independently without unnecessary motor motion");
        var negative=await Collect(fieldTriplet.Select(v=>-v).Concat(new[]{-.19,-.22,-.18,-.23}).ToArray());
        check(negative.Result.IsStable && Math.Abs(negative.Result.Median+near.Result.Median)<1e-12,
            "Robust mask batching preserves opposite focus polarity");
        var twoOutliers=await Collect(new[]{.1,-5.0,4.0,.15,.2,.12,.18});
        check(twoOutliers.Result.IsStable && twoOutliers.Result.InlierCount==5 && twoOutliers.Reads==7,
            "Two excursions require five agreeing measurements before accepting a batch");
        var broad=await Collect(new[]{-3.0,-2,-1,0,1,2,3});
        check(!broad.Result.IsStable && broad.Reads==7,
            "Persistent broad fluctuations remain rejected after the bounded seven-frame batch");
        var minority=await Collect(new[]{-8.0,-7,-6,0,.1,.2,.3});
        check(!minority.Result.IsStable && minority.Result.InlierCount==4,
            "Four agreeing measurements cannot outweigh three incompatible frames");
        var far=await Collect(new[]{2.0,9,1.9,2.1,2.05,1.95,2.02});
        check(far.Result.IsStable && far.Result.Median>1.9,
            "Outlier filtering does not invent zero focus for an unfocused star");
        bool invalid=false; int invalidReads=0;
        try { await BahtinovMeasurementBatch.CollectAsync(t=>Task.FromResult(++invalidReads==2?double.NaN:.1),null,default); }
        catch(InvalidOperationException) { invalid=true; }
        check(invalid && invalidReads==2,"Invalid geometry cannot be hidden as a rejected statistical outlier");
        using var cancel=new CancellationTokenSource(); int reads=0; bool canceled=false;
        try {
            await BahtinovMeasurementBatch.CollectAsync(t=>Task.FromResult(new[]{-2.0,0,2}[reads++]),
                ()=>cancel.Cancel(),cancel.Token);
        } catch(OperationCanceledException) { canceled=true; }
        check(canceled && reads==3,"Stop prevents all additional measurements after an unstable first batch");
        using var preCanceled=new CancellationTokenSource(); preCanceled.Cancel(); reads=0; canceled=false;
        try { await BahtinovMeasurementBatch.CollectAsync(t=> { reads++; return Task.FromResult(.1); },null,preCanceled.Token); }
        catch(OperationCanceledException) { canceled=true; }
        check(canceled && reads==0,"Canceled batch issues no frame request");
    }

    public static void Replay(string archive, Action<bool,string> check) {
        int frames=0; BahtinovLine[] reference=null;
        foreach(string line in File.ReadLines(Path.Combine(archive,"measurements.jsonl"))) {
            using var document=JsonDocument.Parse(line);
            var root=document.RootElement;
            if(!root.TryGetProperty("Mask",out var recorded) || !root.TryGetProperty("Frame",out var id)) continue;
            var image=Cwseo.NINA.ManualFocuser.Tools.SpikeBatch.FitsImage.Load(Path.Combine(archive,id.GetString()+".fits"));
            var actual=BahtinovAnalyzer.Analyze(image.Data.Select(v=>(double)v).ToArray(),image.Width,image.Height);
            reference??=actual.Lines;
            double alignment=actual.Lines[1].Nx*reference[1].Nx+actual.Lines[1].Ny*reference[1].Ny;
            double error=actual.SignedErrorPixels*(alignment<0?-1:1);
            double original=recorded.GetProperty("SignedErrorPixels").GetDouble();
            check(actual.IsValid && Math.Abs(error-original)<1e-9,$"Archived raw {id.GetString()} reproduces its recorded mask error {error:F3} px");
            frames++;
        }
        check(frames>0,"Field replay analyzed actual archived FITS frames");
    }

    public static void ReplaySaturation(string archive, Action<bool,string> check) {
        string file=Directory.GetFiles(archive,"*.json").OrderBy(p=>p).First(p=> {
            using var metadata=JsonDocument.Parse(File.ReadAllText(p));
            return metadata.RootElement.GetProperty("Phase").GetString()=="measure";
        });
        var image=Cwseo.NINA.ManualFocuser.Tools.SpikeBatch.FitsImage.Load(Path.ChangeExtension(file,"fits"));
        var pixels=image.Data.Select(v=>(double)v).ToArray();
        check(FocusSaturation.HasClippedAnalysisArea(pixels,image.Width,image.Height,false),
            "Actual Spike failure FITS contains a connected clipped plateau in the star analysis area");
    }
}
