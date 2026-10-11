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
        internal bool CanPublishNinaHfrHistory => !disposed && liveImageOutputActive &&
            IsFocusAssistRunning && !IsStoppingFocusPreview && !IsSelectingRoi;
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
            ninaHfrHistory?.UpdateFrame();
        }
        private void ClearLiveHistory()
        {
            liveHistory.Clear(); lastGraphTime = double.NegativeInfinity;
            LiveGraphPoints = Array.Empty<DataPoint>(); RaisePropertyChanged(nameof(LiveGraphPoints));
            ninaHfrHistory?.UpdateFrame();
        }
        private void UpdateNinaHfrHistory()
        {
            if (CanPublishNinaHfrHistory)
            {
                ninaHfrHistory ??= new NinaHfrHistoryOutput(this);
                ninaHfrHistory.Start();
            }
            else StopNinaHfrHistory();
        }
        private void StopNinaHfrHistory()
        {
            ninaHfrHistory?.Dispose(); ninaHfrHistory = null;
        }
    }
}
