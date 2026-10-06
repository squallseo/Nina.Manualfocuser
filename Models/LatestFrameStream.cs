using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;

namespace Cwseo.NINA.ManualFocuser.Models {
    // One reader owns the host enumerator. Never dispose it concurrently with a native download.
    public sealed class LatestFrameStream<T> : IAsyncDisposable {
        private readonly CancellationTokenSource stop;
        private readonly Channel<T> frames = Channel.CreateBounded<T>(new BoundedChannelOptions(1) {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true
        });
        private readonly Task producer;
        private Exception terminalFailure;
        private readonly object disposalLock = new();
        private Task disposal;
        public LatestFrameStream(Func<CancellationToken, IAsyncEnumerable<T>> source, TimeSpan frameTimeout, CancellationToken token) {
            stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            producer = Task.Run(async () => {
                Exception failure = null;
                try {
                    await using var reader = source(stop.Token).GetAsyncEnumerator(stop.Token);
                    while (true) {
                        stop.Token.ThrowIfCancellationRequested();
                        stop.CancelAfter(frameTimeout);
                        bool available = await reader.MoveNextAsync().ConfigureAwait(false);
                        bool expired = stop.IsCancellationRequested;
                        stop.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
                        if (expired) {
                            if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
                            throw new OperationCanceledException(stop.Token);
                        }
                        if (!available) throw new InvalidOperationException("Camera stream ended before it was stopped.");
                        frames.Writer.TryWrite(reader.Current);
                    }
                } catch (OperationCanceledException) when (stop.IsCancellationRequested) {
                    if (!token.IsCancellationRequested && !disposing)
                        failure = new TimeoutException("No live frame arrived within the camera timeout. Stop and reconnect the camera before retrying.");
                } catch (Exception e) { failure = e; }
                finally { terminalFailure = failure; frames.Writer.TryComplete(failure); }
            });
        }
        private volatile bool disposing;
        public async Task<T> ReadAsync(CancellationToken token) {
            while (await frames.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                if (frames.Reader.TryRead(out var frame)) return frame;
            // Propagate producer/cleanup failure instead of silently reverting to another SDK capture.
            await frames.Reader.Completion.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Camera stream stopped.");
        }
        public ValueTask DisposeAsync() {
            lock (disposalLock) return new ValueTask(disposal ??= StopAsync());
        }
        private async Task StopAsync() {
            disposing = true;
            stop.Cancel();
            await producer.ConfigureAwait(false); // Host finally/StopLiveView finishes before capture reservation release.
            stop.Dispose();
            if (terminalFailure != null) ExceptionDispatchInfo.Capture(terminalFailure).Throw();
        }
    }
}
