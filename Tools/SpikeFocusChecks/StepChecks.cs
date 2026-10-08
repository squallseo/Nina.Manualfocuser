using Cwseo.NINA.ManualFocuser.Models;

internal static class StepChecks {
    public static async Task Run(Action<bool,string> check) {
        var moves=new List<int>();
        Task<int> Move(int p,CancellationToken t) { moves.Add(p);return Task.FromResult(p); }
        async Task<bool> Fails(Func<Task> run) {try{await run();return false;}catch(InvalidOperationException){return true;}}
        foreach(int step in new[]{600,2500}) {
            moves.Clear();
            var focused=await SpikeFocusRunner.RunAsync(20000,step,8,Move,
                (p,t)=>Task.FromResult(2+Math.Pow((p-18700)/5000.0,2)),default,()=>.1);
            check(Math.Abs(focused.Position-18700)<=step/16 && focused.Width<2.01,
                $"Spike AF with {step} initial step grows below-noise probes then refines the minimum");
            check(moves.All(p=>p>=20000-step*8 && p<=20000+step*8) && moves[^2]<moves[^1],
                "Noise-aware Spike steps preserve bounds and final approach direction");
            if(step==600)check(moves.Any(p=>Math.Abs(p-20000)>step),"Small spike changes trigger larger calibration movement");
        }
        moves.Clear();
        var noisyDirection=await SpikeFocusRunner.RunAsync(20000,600,8,Move,
            (p,t)=>Task.FromResult(2+Math.Pow((p-22000)/5000.0,2)+(p==20600?.2:0)),default,()=>.1);
        check(Math.Abs(noisyDirection.Position-22000)<=37 && noisyDirection.Width<2.01,
            "A noisy first small probe cannot send Spike calibration along a false downhill direction");
        moves.Clear();
        check(await Fails(async()=>await SpikeFocusRunner.RunAsync(20000,600,4,Move,
            (p,t)=>Task.FromResult(2+.03*Math.Sin(p)),default,()=>.2))
            && moves.All(p=>p>=17600&&p<=22400) && moves.Count<=6,
            "Noise-only width changes cannot become a false focus bracket");
        moves.Clear();
        check(await Fails(async()=>await SpikeFocusRunner.RunAsync(20000,600,4,Move,
            (p,t)=>Task.FromResult(2.0),default,()=>-1)) && moves.Count==0,
            "Invalid Spike uncertainty prevents all movement");
        check(FocusMeasurementNoise.Estimate(new[]{9.9,10.0,10.1})<FocusMeasurementNoise.Estimate(new[]{9.0,10.0,11.0}),
            "Calibration noise tracks measured stationary scatter rather than the configured step");
        check(FocusMeasurementNoise.Estimate(new[]{10.0,10.0,10.0})==.05,
            "Identical frames retain a nonzero uncertainty floor");
        moves.Clear();
        check(await Fails(async()=>await SpikeExposureRunner.RunAsync(20000,600,4,.25,.001,Move,
            (p,e,t)=>Task.FromResult(2+.03*Math.Sin(p)),(e,t)=>Task.CompletedTask,default,()=>.2)),
            "Exposure workflow passes stationary uncertainty through to the bounded Spike scan");
    }
}
