using System.Threading;
using System.Threading.Tasks;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

internal sealed class CheckHfrAnalyzer : IFocusHfrAnalyzer {
    private readonly int delayMs;
    private int active;
    private int calls, cancellations;
    public int Calls => Volatile.Read(ref calls);
    public int Cancellations => Volatile.Read(ref cancellations);
    public int MaximumConcurrent { get; private set; }
    public int Active => Volatile.Read(ref active);
    public CheckHfrAnalyzer(int delayMs = 0) { this.delayMs = delayMs; }
    public async Task<FocusHfrMeasurement> AnalyzeAsync(FocusRawFrame frame, CancellationToken token) {
        Interlocked.Increment(ref calls);
        int count=Interlocked.Increment(ref active);
        MaximumConcurrent=Math.Max(MaximumConcurrent,count);
        try {
            token.ThrowIfCancellationRequested();
            if(delayMs>0)await Task.Delay(delayMs,token);
            return new FocusHfrMeasurement(3,.2,5) {
                Left=frame.Left, Top=frame.Top, Width=frame.Width, Height=frame.Height,
                StarSamples=Enumerable.Range(0,5).Select(i=>new FocusStarSample(frame.Width/2.0+(i-2)*24,frame.Height/2.0,3)).ToArray()
            };
        } catch (OperationCanceledException) { Interlocked.Increment(ref cancellations); throw; }
        finally { Interlocked.Decrement(ref active); }
    }
}
