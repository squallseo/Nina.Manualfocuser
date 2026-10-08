using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Cwseo.NINA.ManualFocuser.Models {
    public static class FocusFrameBatchPipeline {
        // The returned outer task means all exposures are finished; the inner
        // task means their analysis is finished. One queued raw frame bounds RAM.
        public static async Task<Task<IReadOnlyList<FocusFrameMetric>>> AcquireAsync<T>(int count,
            Func<CancellationToken,Task<T>> capture, Func<T,CancellationToken,Task<FocusFrameMetric>> analyze,
            CancellationToken token) {
            var queue=Channel.CreateBounded<T>(new BoundedChannelOptions(1) { SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait });
            var work=Task.Run(async ()=> {
                var values=new List<FocusFrameMetric>(count);
                try {
                    await foreach(var frame in queue.Reader.ReadAllAsync(token)) values.Add(await analyze(frame,token));
                    return (IReadOnlyList<FocusFrameMetric>)values;
                } catch(Exception e) {queue.Writer.TryComplete(e);throw;}
            },token);
            try {
                for(int i=0;i<count;i++) {
                    token.ThrowIfCancellationRequested();
                    if(work.IsCompleted) await work; // Surface analysis failures before another exposure.
                    var frame=await capture(token);
                    await queue.Writer.WriteAsync(frame,token);
                }
                queue.Writer.TryComplete();
                return work;
            } catch(Exception e) {
                queue.Writer.TryComplete(e);
                await work; // Preserve the original analysis error, including saturation.
                throw;
            }
        }
    }
}
