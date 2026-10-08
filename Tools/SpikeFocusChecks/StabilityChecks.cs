using Cwseo.NINA.ManualFocuser.Models;
internal static class StabilityChecks {
    public static async Task Run(Action<bool,string> check) {
        async Task Reject<T>(Func<Task> action,string label) where T:Exception {
            bool rejected=false;try{await action();}catch(T){rejected=true;}check(rejected,label);
        }
        int reads=0,extended=0;
        var values=new[]{8.0,8.1,8.2};
        var batch=await SpikeMeasurementBatch.CollectAsync(t=>Task.FromResult(values[reads++]),()=>extended++,default);
        check(batch.IsStable && batch.Median==8.1 && reads==3 && extended==0,"Stable widths retain the fast three-frame path");
        values=new[]{7.55,8.20,10.25,8.1,8.0,8.3,8.2}; reads=extended=0;
        batch=await SpikeMeasurementBatch.CollectAsync(t=>Task.FromResult(values[reads++]),()=>extended++,default);
        check(batch.IsStable && reads==7 && extended==1 && batch.InlierCount==6 && batch.Median==8.2,
            "One excursion triggers four additional stationary frames and requires a consistent majority");
        check(batch.Spread>batch.Limit && batch.InlierSpread<=batch.Limit,"Outliers stay in diagnostics without weakening the width tolerance");
        values=new[]{8.0,10,8,10,8,10,8}; reads=0;
        batch=await SpikeMeasurementBatch.CollectAsync(t=>Task.FromResult(values[reads++]),()=>{},default);
        check(!batch.IsStable && batch.InlierCount==4 && reads==7,"Persistently alternating widths do not become a false convergence");
        values=new[]{8.0,10,12,14,16,18,20}; reads=0;
        batch=await SpikeMeasurementBatch.CollectAsync(t=>Task.FromResult(values[reads++]),()=>{},default);
        check(!batch.IsStable,"Drifting stationary widths remain an error");
        using var stop=new CancellationTokenSource();reads=0;
        await Reject<OperationCanceledException>(()=>SpikeMeasurementBatch.CollectAsync(t=>Task.FromResult(values[reads++]),()=>stop.Cancel(),stop.Token),
            "Stop during extension prevents further captures");
        check(reads==3,"Stop cannot continue the four additional frames");
        foreach(double invalid in new[]{0.0,-1,double.NaN,double.PositiveInfinity}) {
            reads=0;
            await Reject<InvalidOperationException>(()=>SpikeMeasurementBatch.CollectAsync(t=>{reads++;return Task.FromResult(invalid);},()=>{},default),
                "Invalid width is never treated as a noisy but valid frame: "+invalid);
            check(reads==1,"Invalid geometry/width does not trigger a measurement retry");
        }
        // Even when a stronger orthogonal line appears, the same physical width
        // must be measured; a lost acquired line must fail the original gates.
        const int n=256;
        var parameters=new SpikeAnalysisParams {metricKind=SpikeMetricKind.Fwhm,minUsedStarsForValidFrame=1,
            autoSpikeAngle=true,minimumRoiSizePx=128,resolveParallelSpikes=true};
        ushort[] Image(double horizontal,double vertical,bool noise=false) => Enumerable.Range(0,n*n).Select(i=> {
            double x=i%n-128,y=i/n-128;
            double G(double z,double s)=>Math.Exp(-z*z/(2*s*s));
            double value=300+3*Math.Sin(i*1.23);
            if(!noise)value+=40000*G(Math.Sqrt(x*x+y*y),2)+horizontal*G(y,2)*G(x,70)+vertical*G(x,4)*G(y,70);
            return (ushort)Math.Clamp(value,0,65535);
        }).ToArray();
        var seeds=new[]{new SpikeSeedStar{X=128,Y=128,WidthPx=12,HeightPx=12,MaxBrightness=43000}};
        var tracker=SpikeCore.CreateTrackingState(seeds,parameters);
        var analysis=new SpikeAutofocusAnalysis(parameters);
        var acquisition=analysis.Evaluate(Image(2500,500),n,n,tracker);
        check(acquisition.IsValid && acquisition.HasClearSpikes && double.IsFinite(analysis.LockedAngleDeg),"A valid acquisition establishes the autofocus measurement axis");
        var after=analysis.Evaluate(Image(2500,18000),n,n,tracker);
        check(after.HasClearSpikes && after.UsedAngleDeg==acquisition.UsedAngleDeg,"A stronger perpendicular line cannot change the acquired width axis");
        double angle=analysis.LockedAngleDeg;
        tracker=SpikeCore.CreateTrackingState(seeds,parameters);
        var restarted=analysis.Evaluate(Image(2500,18000),n,n,tracker);
        check(restarted.HasClearSpikes && restarted.UsedAngleDeg==angle,"Tracking-only reset retains the current scan's physical measurement axis");
        var missing=analysis.Evaluate(Image(0,18000),n,n,tracker);
        check(!missing.HasClearSpikes,"Loss of the locked diffraction line still fails even with a strong perpendicular line");
        analysis.RestartScan();
        check(double.IsNaN(analysis.LockedAngleDeg),"Whole-curve exposure restart clears the old acquisition axis");
        var fresh=analysis.Evaluate(Image(0,18000),n,n,SpikeCore.CreateTrackingState(seeds,parameters));
        check(fresh.HasClearSpikes && Math.Abs(fresh.UsedAngleDeg-angle)>45,
            "A discarded-curve restart acquires the visible axis at the new exposure");
        var empty=new SpikeAutofocusAnalysis(parameters);
        var blank=empty.Evaluate(Image(0,0,true),n,n,SpikeCore.CreateTrackingState(seeds,parameters));
        check(!blank.HasClearSpikes && double.IsNaN(empty.LockedAngleDeg),"Noise cannot acquire a focusing axis");
        int measurements=0,captures=0,extensions=0;
        var moves=new List<int>();
        var focused=await SpikeFocusRunner.RunAsync(10000,600,3,(position,t)=> {
            moves.Add(position); return Task.FromResult(position);
        },async(position,t)=> {
            measurements++;int localFrame=0;
            double width=2+Math.Pow((position-10137)/1000.0,2);
            var collected=await SpikeMeasurementBatch.CollectAsync(cancellation=> {
                captures++;localFrame++;
                // One occasional high excursion; extra frames have the same
                // physical focus position and the motor must not move for them.
                return Task.FromResult(localFrame==2?width*1.4:width+.005*Math.Sin(localFrame));
            },()=>extensions++,t);
            if(!collected.IsStable)throw new InvalidOperationException("unstable synthetic batch");
            return collected.Median;
        },default);
        check(Math.Abs(focused.Position-10137)<=37,"Adaptive focus still converges and verifies with an outlier at every position");
        check(extensions==measurements && captures==measurements*7,"Additional captures are bounded to seven per position");
        check(moves.All(p=>p>=8200 && p<=11800) && moves[^1]==focused.Position,"Robust width sampling preserves motion bounds and final position verification");
    }
}
