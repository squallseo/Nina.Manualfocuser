using System;
using OxyPlot;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

namespace Cwseo.NINA.ManualFocuser.Dockables
{
    public partial class ManualFocuserDockableVM
    {
        private LiveHfrHistory liveHistory;
        private double lastGraphTime = double.NegativeInfinity;
        private bool showLiveGraphs;
        private NinaHfrHistoryOutput ninaHfrHistory;
        internal bool CanPublishNinaHfrHistory => false;
        public DataPoint[] LiveGraphPoints { get; private set; } = Array.Empty<DataPoint>();
        public bool ShowLiveGraphs
        {
            get => showLiveGraphs;
            set
            {
                if (showLiveGraphs == value) return;
                showLiveGraphs = value; RaisePropertyChanged();
            }
        }
        private void RecordLiveHfr(double seconds, double hfr)
        {
            liveHistory.Record(seconds, hfr);
            if (seconds - lastGraphTime < LiveHfrHistory.SampleSeconds) return;
            lastGraphTime = seconds;
            LiveGraphPoints = liveHistory.DisplayPoints();
            RaisePropertyChanged(nameof(LiveGraphPoints));
            if (!IsManualFocus) {
                RaisePropertyChanged(nameof(FocusGraphPoints));
                RaisePropertyChanged(nameof(HistoryAxisMinimum)); RaisePropertyChanged(nameof(HistoryAxisMaximum));
            }
            ninaHfrHistory?.UpdateFrame();
        }
        private void ClearLiveHistory()
        {
            liveHistory.Clear(); lastGraphTime = double.NegativeInfinity;
            LiveGraphPoints = Array.Empty<DataPoint>(); RaisePropertyChanged(nameof(LiveGraphPoints));
            if (!IsManualFocus) RefreshHistoryPresentation();
            ninaHfrHistory?.UpdateFrame();
        }
        private void ClearFocusHistory() {
            if (IsManualFocus) { positionHistory.Clear(); RefreshHistoryPresentation(); }
            else ClearLiveHistory();
        }
        private void UpdateNinaHfrHistory()
        {
            StopNinaHfrHistory();
        }
        private void StopNinaHfrHistory()
        {
            ninaHfrHistory?.Dispose(); ninaHfrHistory = null;
        }
    }
}
