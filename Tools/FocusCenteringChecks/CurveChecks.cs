using Cwseo.NINA.ManualFocuser.Models;
using NINA.Core.Enum;

internal static class CurveChecks {
    private static TaskCompletionSource<T> Signal<T>()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task Run(Action<bool,string> check) {
        check(FocusCurveOptions.Resolve("HFR",null,null)==FocusCurveModel.Hyperbolic && FocusCurveOptions.Resolve("Spike",null,null)==FocusCurveModel.Parabolic
            && FocusCurveOptions.Resolve("Bahtinov","Parabolic","Hyperbolic")==FocusCurveModel.LinearZeroCrossing,
            "Plugin defaults are symmetric hyperbolic HFR, parabolic Spike and signed-linear Bahtinov");
        check(FocusCurveOptions.Resolve("Linear","Parabolic","Hyperbolic")==FocusCurveModel.Parabolic &&
            FocusCurveOptions.Resolve("Spike","Parabolic","Hyperbolic")==FocusCurveModel.Hyperbolic,
            "HFR and Spike select their independent plugin overrides");
        check(FocusCurveOptions.Resolve("HFR","Cubic",null)==FocusCurveModel.Hyperbolic &&
            FocusCurveOptions.Resolve("Spike",null,"LinearZeroCrossing")==FocusCurveModel.Parabolic,
            "Invalid saved settings and signed models for width metrics fall back to the recommended model");
        FocusPointStatistics Point(int x,double y,bool signed=false)=>FocusPointStatistics.Create(x,
            Enumerable.Range(0,9).Select(i=>new FocusFrameMetric(y+(i-4)*.002,.01,4)).ToArray(),signed);
        var noisy=FocusPointStatistics.Create(12000,new[]{1.99,2,2.01,2.02,2.03,2.01,1.98,2.02,40}.Select(v=>new FocusFrameMetric(v)).ToArray(),false);
        check(Math.Abs(noisy.Median-2.01)<.02 && noisy.Inliers.Length==8 && noisy.Box.Outliers.Contains(40),"Frame outlier is retained in the box plot but excluded from robust fitting statistics");
        check(noisy.Box.LowerWhisker<=noisy.Box.BoxBottom && noisy.Box.BoxBottom<=noisy.Box.Median && noisy.Box.Median<=noisy.Box.BoxTop && noisy.Box.BoxTop<=noisy.Box.UpperWhisker,"Box plot uses ordered quartiles and Tukey whiskers");
        var small=FocusPointStatistics.Create(0,Enumerable.Range(0,5).Select(i=>new FocusFrameMetric(2+i*.01)).ToArray(),false);
        var large=FocusPointStatistics.Create(0,Enumerable.Range(0,25).Select(i=>new FocusFrameMetric(2+i%5*.01)).ToArray(),false);
        check(large.Uncertainty<small.Uncertainty,"Additional agreeing frames reduce the uncertainty of a position");
        bool invalid=false;try{FocusPointStatistics.Create(0,new[]{new FocusFrameMetric(double.NaN),new FocusFrameMetric(0),new FocusFrameMetric(2),new FocusFrameMetric(2),new FocusFrameMetric(double.NaN)},false);}catch(InvalidOperationException){invalid=true;}
        check(invalid,"Invalid/missing HFR frames cannot become a valid focus point");
        int[] plan=FocusCurveScanRunner.Plan(200000,2500,4);
        check(plan.Length==9 && plan[0]==210000 && plan[^1]==190000 && plan.Zip(plan.Skip(1)).All(p=>p.First-p.Second==2500),"Scan uses NINA's fixed AF step and offset count, including large absolute positions");
        check(FocusCurveScanRunner.Plan(4000,2500,4,20000).All(p=>p>=0 && p<=20000),"Every planned position respects hardware limits");
        var unbracketed=plan.Select(x=>Point(x,2+Math.Pow((x-190000)/3500.0,2))).ToArray();
        check(FocusCurveScanRunner.Extension(unbracketed,false,2500,4,int.MaxValue)==187500,"Unbracketed scan extends the position range by the same AF step, never by multiplying step size");
        check(FocusCurveScanRunner.Extension(plan.Select(x=>Point(x,2+Math.Pow((x-200000)/3500.0,2))).ToArray(),false,2500,4,int.MaxValue)==null,
            "A bracketed curve does not request unnecessary scan positions");
        foreach(var method in Enum.GetValues<AFCurveFittingEnum>()) {
            double optimum=200000;
            var samples=plan.Select(x=>Point(x,method is AFCurveFittingEnum.PARABOLIC or AFCurveFittingEnum.TRENDPARABOLIC ? 2+Math.Pow((x-optimum)/3500,2) : 2*Math.Sqrt(1+Math.Pow((x-optimum)/2500,2)))).ToArray();
            var fit=FocusScanFit.Calculate(samples,false,method,.8);
            check(Math.Abs(fit.Position-optimum)<=1 && fit.RSquared>.8 && double.IsFinite(fit.Evaluate(optimum)),"NINA fitting locates a bracketed minimum: "+method);
        }
        foreach(var option in FocusCurveOptions.Choices) {
            var samples=plan.Select(x=>Point(x,option.Model is FocusCurveModel.Parabolic or FocusCurveModel.TrendParabolic ?
                2+Math.Pow((x-200000)/3500.0,2) : 2*Math.Sqrt(1+Math.Pow((x-200000)/2500.0,2)))).ToArray();
            var fit=FocusScanFit.Calculate(samples,option.Model,.8);
            check(Math.Abs(fit.Position-200000)<=1 && fit.Method==option.Label,"Plugin option resolves to an executable fit: "+option.Label);
        }
        var baht=plan.Select(x=>Point(x,(x-200375)/1000.0,true)).ToArray();
        var zero=FocusScanFit.Calculate(baht,true,AFCurveFittingEnum.HYPERBOLIC,.95);
        check(zero.Position==200375 && Math.Abs(zero.Predicted)<.001,"Bahtinov uses the signed zero crossing rather than minimizing signed error");
        foreach(var samples in new[]{plan.Select(x=>Point(x,2)).ToArray(),plan.Select(x=>Point(x,2+(x-190000)/1000.0)).ToArray(),plan.Select(x=>Point(x,2+Math.Pow((x-220000)/3500.0,2))).ToArray()}) {
            bool rejected=false;try{FocusScanFit.Calculate(samples,false,AFCurveFittingEnum.PARABOLIC,.8);}catch(InvalidOperationException){rejected=true;}
            check(rejected,"Flat, monotonic or out-of-range curves cannot claim a focus minimum");
        }
        // Hold analysis until the next move has started. This fails if capture
        // and analysis are accidentally serialized before motor motion.
        var analyzeStarted=Signal<bool>();var nextMove=Signal<bool>();var finishAnalysis=Signal<bool>();
        int current=0,acquisitions=0;var seen=new List<int>();
        Task<int> Move(int x,CancellationToken ct){current=x;if(acquisitions==1){check(analyzeStarted.Task.IsCompleted,"Analysis started before the next movement");nextMove.TrySetResult(true);finishAnalysis.TrySetResult(true);}return Task.FromResult(x);}
        Task<Task<FocusPointStatistics>> Acquire(int x,CancellationToken ct) {
            check(current==x,"A position is acquired only after that motor move completes");
            acquisitions++;
            if(acquisitions==1){analyzeStarted.TrySetResult(true);return Task.FromResult(Slow());}
            return Task.FromResult(Task.FromResult(Point(x,2)));
            async Task<FocusPointStatistics> Slow(){await finishAnalysis.Task;return Point(x,2);}
        }
        var points=await FocusCurveScanRunner.ScanAsync(new[]{12000,10000},Move,Acquire,p=>seen.Add(p.Position),default).WaitAsync(TimeSpan.FromSeconds(5));
        check(nextMove.Task.IsCompleted && points.Count==2 && seen.SequenceEqual(new[]{12000,10000}),"Analysis overlaps the next focus move and retains the previous position label");
        // Stop/fault must await both moving hardware and outstanding analysis.
        var movePending=Signal<bool>();var cleanup=Signal<bool>();var fail=Signal<FocusPointStatistics>();
        int movements=0;
        async Task<int> HeldMove(int x,CancellationToken ct) {
            if(++movements==1)return x;
            movePending.TrySetResult(true);
            try{await Task.Delay(Timeout.Infinite,ct);}catch(OperationCanceledException){await Task.Delay(10);cleanup.TrySetResult(true);throw;}
            return x;
        }
        var failing=FocusCurveScanRunner.ScanAsync(new[]{10000,9000,8000},HeldMove,(x,ct)=>Task.FromResult(fail.Task),_=>{},default);
        await movePending.Task;fail.SetException(new InvalidOperationException("analysis failed"));
        bool failed=false;try{await failing;}catch(InvalidOperationException){failed=true;}
        check(failed && cleanup.Task.IsCompleted && movements==2,"Analysis failure cancels/awaits the in-progress move and skips further positions");
        var blocked=Signal<bool>();var analyzeGate=Signal<bool>();int captures=0,analyses=0;
        var batch=FocusFrameBatchPipeline.AcquireAsync(5,ct=>Task.FromResult(++captures),async (i,ct)=>{Interlocked.Increment(ref analyses);blocked.TrySetResult(true);await analyzeGate.Task;return new FocusFrameMetric(i);},default);
        await blocked.Task;await Task.Delay(20);
        check(captures<=3 && !batch.IsCompleted,"Slow analysis applies bounded backpressure instead of retaining an entire raw frame batch");
        analyzeGate.SetResult(true);var measurements=await(await batch);
        check(measurements.Count==5 && analyses==5,"Batch pipeline analyzes every captured frame exactly once");
        bool saturation=false;
        try{await(await FocusFrameBatchPipeline.AcquireAsync(5,ct=>Task.FromResult(1),(i,ct)=>Task.FromException<FocusFrameMetric>(new FocusSaturationException("test")),default));}catch(FocusSaturationException){saturation=true;}
        check(saturation,"A worker saturation fault retains its type for safe whole-scan exposure recovery");
    }
}
