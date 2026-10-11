using System;
using System.Threading;
using System.Threading.Tasks;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        private CancellationTokenSource hfrAnalysisCts;
        private int hfrGeneration;
        private bool hfrWasEnabled;
        private bool trackFocusStars = true;
        private readonly FocusStarTracker starTracker = new();
        public bool TrackFocusStars {
            get => trackFocusStars;
            set {
                if (trackFocusStars == value) return;
                trackFocusStars = value; RaisePropertyChanged(); ResetStarTracking();
            }
        }
        private void ResetStarTracking() {
            hfrGeneration++; hfrAnalysisCts?.Cancel(); starTracker.Reset();
            PublishStarMeasurement(null);
        }
        private void HfrVisibilityChanged() {
            bool enabled = HfrAnalysisEnabled;
            if (enabled != hfrWasEnabled) {
                hfrWasEnabled = enabled;
                hfrGeneration++;
                hfrAnalysisCts?.Cancel();
                starTracker.Reset();
                LiveHfr = double.NaN; LiveHfrStarCount = 0;
                PublishStarMeasurement(null);
                RaisePropertyChanged(nameof(LiveHfr));
            }
            RaisePropertyChanged(nameof(LiveHfrText));
        }
        private async Task<FocusHfrMeasurement> AnalyzeFocusHfrAsync(FocusRawFrame frame, CancellationToken token) {
            if (!HfrAnalysisEnabled) return null;
            if (focusHfrAnalyzer == null) throw new InvalidOperationException("NINA multi-star HFR analyzer is unavailable.");
            int generation = hfrGeneration;
            bool tracking = TrackFocusStars;
            bool detectAllStars = tracking && starTracker.HasReference(frame);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            hfrAnalysisCts = cancellation;
            try {
                var measurement = await Task.Run(() => focusHfrAnalyzer.AnalyzeAsync(frame, detectAllStars, cancellation.Token), cancellation.Token);
                if (!HfrAnalysisEnabled || generation != hfrGeneration || measurement == null) return null;
                if (tracking) measurement = starTracker.Match(measurement);
                return measurement with { SourceFrameId = frame.FrameId, Generation = generation };
            } catch (OperationCanceledException) when (!token.IsCancellationRequested && cancellation.IsCancellationRequested) {
                // Hiding HFR cancels analysis, not the ongoing camera capture.
                return null;
            } finally {
                if (ReferenceEquals(hfrAnalysisCts,cancellation)) hfrAnalysisCts = null;
            }
        }
    }
}
