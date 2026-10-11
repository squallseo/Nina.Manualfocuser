using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        public bool TakeShootAfterMove {
            get => Properties.Settings.Default.TakeShootAfterMove;
            set { Properties.Settings.Default.TakeShootAfterMove = value; Properties.Settings.Default.Save(); RaisePropertyChanged(); }
        }
        private async Task<int> CaptureManualFocusAsync(bool alreadyReserved = false) {
            if (!alreadyReserved && !CanStartAssist()) return 0;
            bool completed = false;
            if (!alreadyReserved) BeginAssist(false);
            try {
                var token = assistCts.Token;
                token.ThrowIfCancellationRequested();
                IsSelectingRoi = false;
                var profile = profileService.ActiveProfile;
                FilterInfo filter = null;
                if (profile.FocuserSettings.UseFilterWheelOffsets) {
                    filter = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(f => f.AutoFocusFilter);
                    if (filter != null) filter = await filterWheelMediator.ChangeFilter(filter, token, NullProgress<ApplicationStatus>.Instance);
                }
                token.ThrowIfCancellationRequested();
                var sequence = DataModel.CreateManualFocusSequence(filter);
                int count = Math.Max(1, profile.FocuserSettings.AutoFocusNumberOfFramesPerPoint);
                var before = focuserMediator.GetInfo();
                if (before?.Connected == true && before.IsMoving) throw new InvalidOperationException("Wait for the focuser to stop before capture.");
                string device = before?.DeviceId;
                int? position = before?.Connected == true ? before.Position : null;
                bool stationary = before?.Connected == true && !before.IsMoving;
                string cameraId = CameraInfo.DeviceId;
                var measurements = new List<FocusHfrMeasurement>();
                int measurementGeneration = hfrGeneration;
                LiveHfr = double.NaN; LiveHfrStarCount = 0; RaisePropertyChanged(nameof(LiveHfrText));
                for (int i = 0; i < count; i++) {
                    token.ThrowIfCancellationRequested();
                    if (cameraMediator.GetInfo()?.DeviceId != cameraId) throw new InvalidOperationException("Focus camera changed.");
                    SetAssistStatus($"Capturing focus measurement {i + 1}/{count}...");
                    var frame = await DataModel.CaptureManualFocusAsync(sequence, token);
                    if (measurementGeneration != hfrGeneration) { measurements.Clear(); measurementGeneration = hfrGeneration; }
                    var measurement = await AnalyzeFocusHfrAsync(frame, token);
                    if (measurement?.Stars > 0 && double.IsFinite(measurement.Hfr)) measurements.Add(measurement);
                    PublishStarMeasurement(HfrAnalysisEnabled && measurementGeneration == hfrGeneration ? measurement : null);
                    FocusPreviewImage = await Task.Run(() => RenderFocusPresentation(frame, null), token);
                    LiveStarProfile = ShowLiveGraphs ? await Task.Run(() => CreateManualStarProfile(frame), token) : Array.Empty<OxyPlot.DataPoint>();
                    RaisePropertyChanged(nameof(LiveStarProfile));
                    token.ThrowIfCancellationRequested();
                    if (!HfrAnalysisEnabled || measurementGeneration != hfrGeneration) measurements.Clear();
                    LiveHfr = measurements.Count > 0 ? measurements.Average(m => m.Hfr) : double.NaN;
                    LiveHfrStarCount = HfrAnalysisEnabled && measurementGeneration == hfrGeneration ? measurement?.Stars ?? 0 : 0;
                    RaisePropertyChanged(nameof(LiveHfr)); RaisePropertyChanged(nameof(LiveHfrText));
                    LiveFrameDetails = $"NINA autofocus settings | Exposure {sequence.ExposureTime:F2} s | Binning {sequence.Binning.X}x{sequence.Binning.Y} | Frame {i+1}/{count} | Source {frame.Width}x{frame.Height}";
                    RaisePropertyChanged(nameof(LiveFrameDetails));
                }
                token.ThrowIfCancellationRequested();
                if (HistoryPanelEnabled && measurementGeneration == hfrGeneration) RecordPositionHfr(device, position, stationary, LiveHfr);
                completed = true;
                SetAssistStatus($"Focus measurement captured. {LiveHfrText}");
                return 1;
            } catch (OperationCanceledException) { SetAssistStatus("Focus capture stopped."); return 0; }
            finally {
                assistCts?.Cancel(); EndAssist(false);
                if (completed && SyncImageOnStop && !disposed) await RunGuarded("Sync image", SyncSourceImageAsync);
            }
        }
        private static OxyPlot.DataPoint[] CreateManualStarProfile(FocusRawFrame frame) {
            var center = frame.CenterWindow();
            int peak = Array.IndexOf(center.Pixels, center.Pixels.Max());
            int x = peak % center.Width, y = peak / center.Width;
            var sorted = (double[])center.Pixels.Clone(); Array.Sort(sorted);
            double background = sorted[sorted.Length/2];
            double amplitude = Math.Max(1, center.Pixels[peak]-background);
            int first = Math.Max(0,x-64), last = Math.Min(center.Width-1,x+64);
            return Enumerable.Range(first,last-first+1).Select(col => new OxyPlot.DataPoint(col-x,
                Math.Max(0,center.Pixels[y*center.Width+col]-background)/amplitude)).ToArray();
        }
    }
}
