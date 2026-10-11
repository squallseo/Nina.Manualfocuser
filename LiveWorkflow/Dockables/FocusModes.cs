using System;
using OxyPlot;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        private string focusMode = "Live";
        private readonly PositionHfrHistory positionHistory = new();
        private string positionHistoryDevice;
        public string FocusMode {
            get => focusMode;
            set {
                if (!CanConfigureLive || value is not ("Manual" or "Live") || value == focusMode) return;
                focusMode = value; RaisePropertyChanged();
                ResetStarTracking();
                RaisePropertyChanged(nameof(IsManualFocus)); RaisePropertyChanged(nameof(CaptureActionText));
                RaisePropertyChanged(nameof(IsLiveFocus));
                RaisePropertyChanged(nameof(FocusModeHelp));
                UpdateInspectorAvailability();
                RefreshHistoryPresentation();
            }
        }
        public bool IsManualFocus => FocusMode == "Manual";
        public bool IsLiveFocus => !IsManualFocus;
        public string CaptureActionText => IsManualFocus ? "Capture focus measurement" : "Start live focus";
        public string FocusModeHelp => IsManualFocus ? "Move focus, then capture using NINA autofocus settings." : "Stream frames until Stop; adjust focus while watching HFR.";
        public string HistoryTitle => IsManualFocus ? "HFR by focus position" : "Live HFR history · last 120 seconds";
        public string HistoryAxisTitle => IsManualFocus ? "Focus position (steps)" : "Time (s)";
        public double HistoryAxisMinimum => IsManualFocus ? double.NaN : liveHistory.AxisMinimum;
        public double HistoryAxisMaximum => IsManualFocus ? double.NaN : liveHistory.AxisMaximum;
        public DataPoint[] FocusGraphPoints => IsManualFocus ? positionHistory.DisplayPoints() : LiveGraphPoints;
        public DataPoint[] ManualFocusMovement => IsManualFocus ? positionHistory.MovementPoints() : Array.Empty<DataPoint>();
        public DataPoint ManualFocusMinimum => IsManualFocus ? positionHistory.MinimumPoint() : DataPoint.Undefined;
        private void RefreshHistoryPresentation() {
            RaisePropertyChanged(nameof(FocusGraphPoints)); RaisePropertyChanged(nameof(HistoryTitle));
            RaisePropertyChanged(nameof(HistoryAxisTitle)); RaisePropertyChanged(nameof(HistoryAxisMinimum));
            RaisePropertyChanged(nameof(HistoryAxisMaximum));
        }
        private void RecordPositionHfr(string deviceId, int? before, bool stationaryBefore, double hfr) {
            var after = focuserMediator.GetInfo();
            if (stationaryBefore && before.HasValue && after?.Connected == true && after.DeviceId == deviceId &&
                !after.IsMoving && !IsMoving && after.Position == before.Value) {
                if (positionHistoryDevice != deviceId) { positionHistory.Clear(); positionHistoryDevice = deviceId; }
                positionHistory.Record(before.Value, hfr); RefreshHistoryPresentation();
            }
        }
    }
}
