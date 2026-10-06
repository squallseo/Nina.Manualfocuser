using Cwseo.NINA.ManualFocuser.Models;
int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
async Task Reject<T>(Func<Task> action, string message) where T : Exception {
    bool rejected = false; try { await action(); } catch (T) { rejected = true; } Check(rejected,message);
}
var moves = new List<int>();
Task<int> Move(int position,CancellationToken token) { moves.Add(position); return Task.FromResult(position); }
int adaptiveReads = 0;
var result = await SpikeFocusRunner.RunAsync(10000,600,2,Move,(p,t) => { adaptiveReads++; return Task.FromResult(2+Math.Pow((p-10137)/1000.0,2)); },default);
Check(Math.Abs(result.Position-10137) <= 37,"Adaptive refinement finds the spike-width minimum within step/16 resolution");
Check(adaptiveReads < 16,"Adaptive search uses fewer measurements than the previous 16-measurement coarse/fine scan");
Console.WriteLine($"Adaptive Spike measurements: {adaptiveReads}; previous scan: 16");
Check(moves.All(p => p >= 8800 && p <= 11200),"Every move stays inside the original scan bounds");
Check(moves[^2] < moves[^1] && moves[^1] == result.Position,"Final approach follows the scan direction");
moves.Clear();
await Reject<InvalidOperationException>(async () => await SpikeFocusRunner.RunAsync(1000,100,2,Move,(p,t)=>Task.FromResult(double.NaN),default),"Invalid preflight measurement rejects the run");
Check(moves.Count == 0,"No motor move occurs before valid spike detection");
await Reject<InvalidOperationException>(async () => await SpikeFocusRunner.RunAsync(1000,100,2,Move,(p,t)=>Task.FromResult(2.0),default),"Flat widths do not invent a focus minimum");
moves.Clear();
await Reject<InvalidOperationException>(async () => await SpikeFocusRunner.RunAsync(1000,100,2,Move,(p,t)=>Task.FromResult(p/100.0),default),"A minimum at the scan edge is not accepted");
Check(moves.Count <= 3 && moves.All(p=>p>=800&&p<=1200),"Edge failure stops with fewer moves inside the original bounds");
moves.Clear();
int reads = 0;
await Reject<InvalidOperationException>(async () => await SpikeFocusRunner.RunAsync(1000,100,2,(p,t)=>Task.FromResult(p-1),(p,t)=>{reads++;return Task.FromResult(2.0);},default),"Incomplete focuser movement aborts the run");
Check(reads == 1,"An unreached position is never measured");
using var cancellation = new CancellationTokenSource();
reads = 0;
await Reject<OperationCanceledException>(async () => await SpikeFocusRunner.RunAsync(1000,100,2,(p,t)=>{moves.Add(p);cancellation.Cancel();return Task.FromResult(p);},(p,t)=>{reads++;return Task.FromResult(2.0);},cancellation.Token),"Cancellation after movement stops before measurement");
Check(reads == 1 && moves.Count == 1,"Canceled run performs no further moves or captures");
var visits = new Dictionary<int,int>();
await Reject<InvalidOperationException>(async () => await SpikeFocusRunner.RunAsync(1000,100,2,Move,(p,t)=> {
    visits.TryGetValue(p,out int count); visits[p]=count+1;
    return Task.FromResult(p == 1025 && count > 0 ? 8.0 : 2+Math.Pow((p-1025)/100.0,2));
},default),"Final confirmation must reproduce the minimum");
await Reject<ArgumentException>(async () => await SpikeFocusRunner.RunAsync(100,100,2,Move,(p,t)=>Task.FromResult(2.0),default),"Scan cannot command a negative position");
await Reject<ArgumentException>(async () => await SpikeFocusRunner.RunAsync(int.MaxValue-50,100,2,Move,(p,t)=>Task.FromResult(2.0),default),"Scan overflow is rejected");
int optimum = int.MaxValue-175;
result=await SpikeFocusRunner.RunAsync(int.MaxValue-200,100,2,Move,(p,t)=>Task.FromResult(2+Math.Pow(((double)p-optimum)/100,2)),default);
Check(result.Position==optimum,"A scan ending at Int32.MaxValue terminates and finds its bounded minimum");
foreach (int focus in new[] { 825, 943, 1000, 1049, 1175 }) {
    moves.Clear(); int sampleNumber = 0;
    var adaptive = await SpikeFocusRunner.RunAsync(1000,100,3,Move,(p,t)=> {
        double distance=(p-focus)/100.0;
        // Smooth nonquadratic focus curve with deterministic small measurement noise.
        double noise=.001*Math.Sin(++sampleNumber*1.7);
        return Task.FromResult(2+distance*distance+.05*Math.Pow(distance,4)+noise);
    },default);
    Check(Math.Abs(adaptive.Position-focus)<=6,$"Noisy nonquadratic minimum converges at {focus}");
    Check(moves.All(p=>p>=700&&p<=1300),$"Adaptive expansion remains bounded at {focus}");
}
double G(double x,double sigma)=>Math.Exp(-x*x/(2*sigma*sigma));
var parameters=new SpikeAnalysisParams {metricKind=SpikeMetricKind.Fwhm,minUsedStarsForValidFrame=1,autoSpikeAngle=true};
var seed=new[]{new SpikeSeedStar{X=256,Y=256,WidthPx=12,HeightPx=12,MaxBrightness=6500}};
var tracking=SpikeCore.CreateTrackingState(seed,parameters);
var pixels=Enumerable.Range(0,512*512).Select(i=> {
    double x=i%512-256,y=i/512-256;
    return (ushort)(100+4000*G(Math.Sqrt(x*x+y*y),2)+1500*G(x,1.5)*G(y,60)+1500*G(y,1.5)*G(x,60));
}).ToArray();
var spike=SpikeCore.Evaluate(pixels,512,512,parameters,tracking);
Check(spike.IsValid && spike.HasClearSpikes && double.IsFinite(spike.Metric),"One isolated star with bilateral diffraction spikes yields a usable autofocus FWHM");
tracking=SpikeCore.CreateTrackingState(seed,parameters);
pixels=Enumerable.Range(0,512*512).Select(i=>(ushort)(100+4000*G(Math.Sqrt(Math.Pow(i%512-256,2)+Math.Pow(i/512-256,2)),2))).ToArray();
spike=SpikeCore.Evaluate(pixels,512,512,parameters,tracking);
Check(!spike.HasClearSpikes,"A circular star cannot start spike autofocus");
Console.WriteLine($"{passed} spike autofocus checks passed; no hardware accessed.");
