using Cwseo.NINA.ManualFocuser.Models;

internal static class ExposureChecks {
    public static async Task Run(Action<bool,string> check) {
        var moves=new List<int>(); var exposures=new List<double>(); var events=new List<string>();
        Task<int> Move(int p,CancellationToken t) { moves.Add(p); events.Add($"move:{p}"); return Task.FromResult(p); }
        Task Apply(double seconds,CancellationToken t) { exposures.Add(seconds); events.Add($"apply:{seconds}"); return Task.CompletedTask; }
        double Curve(int p)=>2+Math.Pow((p-1025)/100.0,2);
        async Task<bool> Fails<T>(Func<Task> action) where T:Exception {
            try { await action(); return false; } catch(T) { return true; }
        }
        var result=await SpikeExposureRunner.RunAsync(1000,100,2,.25,.001,Move,(p,s,t)=> {
            if(s>.04) { check(moves.Count==0,"Clipped preflight never commands a focuser move"); throw new FocusSaturationException("clipped"); }
            return Task.FromResult(Curve(p));
        },Apply,default);
        check(exposures.SequenceEqual(new[]{.0625,.015625}) && result.Reductions==2 && result.ExposureSeconds==.015625,
            "Clipped preflight reduces 250 ms to 62.5 then 15.625 ms without manual intervention");
        check(result.Position==1025 && moves.All(p=>p>=800&&p<=1200),
            "Exposure recovery still verifies the minimum inside the original motor bounds");
        moves.Clear(); exposures.Clear(); events.Clear();
        result=await SpikeExposureRunner.RunAsync(1000,100,2,.25,.001,Move,(p,s,t)=>Task.FromResult(Curve(p)),Apply,default);
        check(result.Reductions==0 && exposures.Count==0 && result.ExposureSeconds==.25,
            "Unsaturated autofocus preserves its requested exposure");

        moves.Clear(); exposures.Clear();
        var cleanupStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFinished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int pendingReads=0;
        var pending=SpikeExposureRunner.RunAsync(1000,100,2,.25,.001,Move,(p,s,t)=> {
            pendingReads++;
            if(s==.25) throw new FocusSaturationException("clipped");
            return Task.FromResult(Curve(p));
        },async(s,t)=> { cleanupStarted.SetResult(); await cleanupFinished.Task; exposures.Add(s); },default);
        await cleanupStarted.Task;
        check(pendingReads==1 && moves.Count==0 && !pending.IsCompleted,
            "Pending stream cleanup blocks all new-exposure captures and motor commands");
        cleanupFinished.SetResult(); result=await pending;
        check(result.Position==1025 && result.Reductions==1,
            "Successful asynchronous cleanup resumes autofocus at the reduced exposure");

        moves.Clear(); exposures.Clear(); events.Clear(); int originReads=0;
        result=await SpikeExposureRunner.RunAsync(1000,100,2,.25,.001,Move,(p,s,t)=> {
            events.Add($"read:{p}:{s}"); if(p==1000) originReads++;
            if(p==1100 && s==.25) throw new FocusSaturationException("near focus clipped");
            // Exposure-dependent offsets make mixing old and new cached widths incorrect.
            return Task.FromResult(Curve(p)+100*s);
        },Apply,default);
        int adjustment=events.FindIndex(e=>e.StartsWith("apply:"));
        check(events[adjustment+1]=="read:1100:0.0625" && events[adjustment+2]=="move:1000",
            "Mid-scan clipping checks the new exposure while stationary before returning to origin");
        check(originReads==2 && result.Position==1025 && result.Reductions==1,
            "Exposure changes discard previous width samples and rebuild the focus curve");
        check(moves.All(p=>p>=800&&p<=1200) && moves[^2]<moves[^1],
            "Restarted scan retains motor limits and the final approach direction");

        moves.Clear(); exposures.Clear();
        check(await Fails<InvalidOperationException>(async()=>await SpikeExposureRunner.RunAsync(1000,100,2,.25,.01,Move,
            (p,s,t)=>throw new FocusSaturationException("clipped"),Apply,default)),
            "Persistent clipping at the camera exposure floor remains an explicit failure");
        check(exposures.SequenceEqual(new[]{.0625,.015625,.01}) && moves.Count==0,
            "Auto exposure never crosses the camera minimum or moves for saturated preflight");
        exposures.Clear(); int reads=0;
        check(await Fails<InvalidOperationException>(async()=>await SpikeExposureRunner.RunAsync(1000,100,2,1e8,.001,Move,
            (p,s,t)=> { reads++; throw new FocusSaturationException("clipped"); },Apply,default)),
            "Exposure recovery has a bounded retry limit even before reaching the minimum");
        check(exposures.Count==SpikeExposureRunner.MaximumReductions && reads==exposures.Count+1 && moves.Count==0,
            "Eight reductions is the maximum permitted exposure recovery work");
        exposures.Clear();
        check(await Fails<IOException>(async()=>await SpikeExposureRunner.RunAsync(1000,100,2,.25,.001,Move,
            (p,s,t)=>throw new IOException("native capture failed"),Apply,default)) && exposures.Count==0,
            "Camera failures are never retried as saturation");
        check(await Fails<InvalidOperationException>(async()=>await SpikeExposureRunner.RunAsync(1000,100,2,.25,.001,Move,
            (p,s,t)=>Task.FromResult(double.NaN),Apply,default)) && exposures.Count==0 && moves.Count==0,
            "Invalid spike geometry cannot trigger exposure retries or motor motion");
        reads=0;
        check(await Fails<IOException>(async()=>await SpikeExposureRunner.RunAsync(1000,100,2,.25,.001,Move,
            (p,s,t)=> { reads++; throw new FocusSaturationException("clipped"); },
            (s,t)=>throw new IOException("stream shutdown failed"),default)) && reads==1 && moves.Count==0,
            "Failed stream cleanup prevents changed-exposure frames and all movement");
        using var cancel=new CancellationTokenSource(); reads=0;
        check(await Fails<OperationCanceledException>(async()=>await SpikeExposureRunner.RunAsync(1000,100,2,.25,.001,Move,
            (p,s,t)=> { reads++; throw new FocusSaturationException("clipped"); },
            (s,t)=> { cancel.Cancel(); return Task.CompletedTask; },cancel.Token)) && reads==1 && moves.Count==0,
            "Stop during exposure adjustment prevents another capture or motor move");

        moves.Clear(); exposures.Clear();
        check(await Fails<InvalidOperationException>(async()=>await SpikeExposureRunner.RunAsync(1000,100,2,.25,.001,Move,
            (p,s,t)=> {
                if(s==.25 && p==1100) throw new FocusSaturationException("clipped");
                return Task.FromResult(s<.25?double.NaN:Curve(p));
            },Apply,default)) && moves.SequenceEqual(new[]{1100}),
            "Lost spikes at the reduced exposure prevent the return-to-origin move");
        moves.Clear(); exposures.Clear();
        check(await Fails<InvalidOperationException>(async()=>await SpikeExposureRunner.RunAsync(1000,100,2,.25,.001,
            (p,t)=> { moves.Add(p); return Task.FromResult(p==1000?p+1:p); },
            (p,s,t)=> { if(s==.25 && p==1100) throw new FocusSaturationException("clipped"); return Task.FromResult(Curve(p)); },Apply,default))
            && moves.SequenceEqual(new[]{1100,1000}),"Unreached restart origin aborts without another scan");
        exposures.Clear();
        check(await Fails<ArgumentException>(async()=>await SpikeExposureRunner.RunAsync(1000,100,2,.001,.01,Move,
            (p,s,t)=>Task.FromResult(Curve(p)),Apply,default)) && exposures.Count==0,
            "Invalid exposure bounds reject before capture or adjustment");
        using var preCanceled=new CancellationTokenSource(); preCanceled.Cancel(); reads=0; moves.Clear();
        check(await Fails<OperationCanceledException>(async()=>await SpikeExposureRunner.RunAsync(1000,100,2,.25,.001,Move,
            (p,s,t)=> { reads++; return Task.FromResult(Curve(p)); },Apply,preCanceled.Token)) && reads==0 && moves.Count==0,
            "Pre-canceled exposure workflow performs no capture or movement");
    }
}
