using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cwseo.NINA.ManualFocuser.Models;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        private CancellationTokenSource assistCts;
        private bool assistRunning;
        private double previewExposureMs = 250, previewX = 50, previewY = 50;
        private bool analyzeMask, maskConfirmed;
        private int pendingPreviewMove;
        private string focusMode = "Manual";
        public string FocusMode {
            get => focusMode;
            set {
                if (focusMode == value || IsMoving) return;
                focusMode = value;
                assistCts?.Cancel();
                RaisePropertyChanged();
            }
        }
        public AsyncObservableCollection<OxyPlot.DataPoint> LiveHfrPoints { get; } = new();
        public OxyPlot.DataPoint[] LiveStarProfile { get; private set; } = Array.Empty<OxyPlot.DataPoint>();
        public double LiveHfr { get; private set; } = double.NaN;
        public ICommand ClearLiveGraphCommand { get; private set; }
        public bool IsFocusAssistRunning => assistRunning;
        public double PreviewExposureMs { get => previewExposureMs; set { if (double.IsFinite(value)) previewExposureMs = Math.Clamp(value, 1, 5000); LiveHfrPoints.Clear(); RaisePropertyChanged(); } }
        public double PreviewCenterX { get => previewX; set { if (double.IsFinite(value)) previewX = Math.Clamp(value, 0, 100); LiveHfrPoints.Clear(); RaisePropertyChanged(); } }
        public double PreviewCenterY { get => previewY; set { if (double.IsFinite(value)) previewY = Math.Clamp(value, 0, 100); LiveHfrPoints.Clear(); RaisePropertyChanged(); } }
        public bool AnalyzeBahtinov { get => analyzeMask; set { analyzeMask = value; RaisePropertyChanged(); } }
        public bool BahtinovMaskConfirmed { get => maskConfirmed; set { maskConfirmed = value; RaisePropertyChanged(); CommandManager.InvalidateRequerySuggested(); } }
        public ImageSource FocusPreviewImage { get; private set; }
        public string FocusAssistStatus { get; private set; } = "Preview: center a star in the ROI. Exposure is not the delivered frame interval.";
        public ICommand StartFocusPreviewCommand { get; private set; }
        public ICommand StopFocusPreviewCommand { get; private set; }
        public ICommand BahtinovAFCommand { get; private set; }
        public ICommand PreviewMoveInCommand { get; private set; }
        public ICommand PreviewMoveOutCommand { get; private set; }
        private void InitializeFocusAssist() {
            StartFocusPreviewCommand = new AsyncCommand<int>(() => RunGuarded("Focus preview", RunFocusPreviewAsync), _ => CanStartAssist());
            StopFocusPreviewCommand = new RelayCommand(_ => assistCts?.Cancel(), _ => assistRunning);
            ClearLiveGraphCommand = new RelayCommand(_ => LiveHfrPoints.Clear());
            BahtinovAFCommand = new AsyncCommand<int>(() => RunGuarded("Bahtinov AF", RunBahtinovAfAsync), _ => CanStartAssist() && FocuserInfo?.Connected == true && BahtinovMaskConfirmed);
            PreviewMoveInCommand = new RelayCommand(_ => pendingPreviewMove = -(int)Math.Clamp(Math.Abs((long)UserStep), 1, 10000),
                _ => assistRunning && !IsMoving && FocuserInfo?.Connected == true && pendingPreviewMove == 0);
            PreviewMoveOutCommand = new RelayCommand(_ => pendingPreviewMove = (int)Math.Clamp(Math.Abs((long)UserStep), 1, 10000),
                _ => assistRunning && !IsMoving && FocuserInfo?.Connected == true && pendingPreviewMove == 0);
        }
        private bool CanStartAssist() => !disposed && !assistRunning && !IsMoving && !IsCapturing && !IsGoingToFocusTarget && CameraInfo?.Connected == true && cameraMediator.IsFreeToCapture(this);
        private void SetAssistStatus(string status) { FocusAssistStatus = status; RaisePropertyChanged(nameof(FocusAssistStatus)); }
        private void BeginAssist(bool moving) {
            assistCts?.Dispose(); assistCts = new CancellationTokenSource();
            pendingPreviewMove = 0;
            assistRunning = true; IsCapturing = true; if (moving) IsMoving = true;
            RaisePropertyChanged(nameof(IsFocusAssistRunning));
            cameraMediator.RegisterCaptureBlock(this);
        }
        private void EndAssist(bool moving) {
            try { cameraMediator.ReleaseCaptureBlock(this); } catch (Exception e) { Logger.Error("Preview camera reservation release failed", e); }
            assistRunning = false; IsCapturing = false; if (moving) IsMoving = false;
            RaisePropertyChanged(nameof(IsFocusAssistRunning));
        }
        private async Task<int> RunFocusPreviewAsync() {
            if (!CanStartAssist()) return 0;
            try {
                BeginAssist(false);
                LiveHfrPoints.Clear();
                var clock = Stopwatch.StartNew(); long previous = 0;
                while (true) {
                    assistCts.Token.ThrowIfCancellationRequested();
                    if (pendingPreviewMove != 0) {
                        int relative = pendingPreviewMove; pendingPreviewMove = 0;
                        IsMoving = true;
                        try { await focuserMediator.MoveFocuserRelative(relative, assistCts.Token); }
                        finally { IsMoving = false; }
                    }
                    var frame = await DataModel.CaptureFocusPreviewAsync(PreviewExposureMs / 1000, 256, PreviewCenterX, PreviewCenterY, assistCts.Token);
                    bool mask = AnalyzeBahtinov;
                    var measurement = await Task.Run(() => mask ? BahtinovAnalyzer.Analyze(frame.Pixels, frame.Width, frame.Height) : null, assistCts.Token);
                    double hfr = await Task.Run(() => QuickFocusMetrics.HalfFluxRadius(frame.Pixels, frame.Width, frame.Height), assistCts.Token);
                    assistCts.Token.ThrowIfCancellationRequested();
                    FocusPreviewImage = RenderFocusPreview(frame.Pixels, frame.Width, frame.Height, measurement);
                    RaisePropertyChanged(nameof(FocusPreviewImage));
                    long now = clock.ElapsedMilliseconds;
                    LiveHfr = hfr;
                    LiveHfrPoints.Add(new OxyPlot.DataPoint(now / 1000.0, hfr));
                    while (LiveHfrPoints.Count > 600 || (LiveHfrPoints.Count > 1 && LiveHfrPoints[0].X < now / 1000.0 - 120)) LiveHfrPoints.RemoveAt(0);
                    int peak = Array.IndexOf(frame.Pixels, frame.Pixels.Max());
                    int px = peak % frame.Width, py = peak / frame.Width;
                    double background = frame.Pixels.OrderBy(p => p).ElementAt(frame.Pixels.Length / 2);
                    double amplitude = Math.Max(1, frame.Pixels[peak] - background);
                    LiveStarProfile = Enumerable.Range(Math.Max(0, px - 64), Math.Min(frame.Width - 1, px + 64) - Math.Max(0, px - 64) + 1)
                        .Select(x => new OxyPlot.DataPoint(x - px, Math.Max(0, frame.Pixels[py * frame.Width + x] - background) / amplitude)).ToArray();
                    RaisePropertyChanged(nameof(LiveHfr));
                    RaisePropertyChanged(nameof(LiveStarProfile));
                    string metric = mask ? measurement.IsValid ? $"Mask error {measurement.SignedErrorPixels:+0.00;-0.00;0.00} px" : $"HFR fallback: {measurement.FailureReason}" : double.IsFinite(hfr) ? $"Local HFR {hfr:F2} px" : "No isolated star detected";
                    if (mask && !measurement.IsValid) {
                        hfr = QuickFocusMetrics.HalfFluxRadius(frame.Pixels, frame.Width, frame.Height);
                        if (double.IsFinite(hfr)) metric = $"Local HFR {hfr:F2} px | mask not detected";
                    }
                    SetAssistStatus($"{metric} | {now - previous} ms/frame | {(frame.HardwareRoi ? "camera ROI" : "software crop / full download")}");
                    previous = now;
                }
            } catch (OperationCanceledException) { SetAssistStatus("Preview stopped."); return 0; }
            catch { SetAssistStatus("Preview failed; camera reservation released."); throw; }
            finally { EndAssist(false); }
        }
        private async Task<int> RunBahtinovAfAsync() {
            if (!CanStartAssist() || FocuserInfo?.Connected != true || !BahtinovMaskConfirmed) return 0;
            int origin = FocuserInfo.Position;
            long configuredStep = Math.Abs((long)DataModel.AFStepSize);
            if (configuredStep < 1 || configuredStep > 10000) throw new InvalidOperationException("Autofocus step size must be between 1 and 10000.");
            int step = (int)configuredStep;
            int offsets = Math.Clamp(DataModel.NumInitialSteps, 1, 12);
            if (step < 1 || step > 10000 || origin < (long)step * offsets || origin + (long)step * offsets > int.MaxValue)
                throw new InvalidOperationException("Invalid Bahtinov scan range. Check autofocus step size and current position.");
            int lower = origin - step * offsets, upper = origin + step * offsets;
            // Freeze optical ROI/exposure during a scan; changing settings cannot alter calibration halfway through.
            double seconds = PreviewExposureMs / 1000, centerX = PreviewCenterX, centerY = PreviewCenterY;
            var samples = new List<(int Position, double Error)>();
            try {
                BeginAssist(true);
                var token = assistCts.Token;
                BahtinovLine[] referenceLines = null;
                async Task<double> Measure(int position) {
                    // Discard one exposure after each move, following SharpCap's documented settling recommendation.
                    await DataModel.CaptureFocusPreviewAsync(seconds, 256, centerX, centerY, token);
                    var errors = new double[3];
                    for (int i = 0; i < errors.Length; i++) {
                        var frame = await DataModel.CaptureFocusPreviewAsync(seconds, 256, centerX, centerY, token);
                        var result = await Task.Run(() => BahtinovAnalyzer.Analyze(frame.Pixels, frame.Width, frame.Height), token);
                        token.ThrowIfCancellationRequested();
                        FocusPreviewImage = RenderFocusPreview(frame.Pixels, frame.Width, frame.Height, result);
                        RaisePropertyChanged(nameof(FocusPreviewImage));
                        if (!result.IsValid) throw new InvalidOperationException($"Mask detection lost at {position}: {result.FailureReason}. No further moves.");
                        referenceLines ??= result.Lines;
                        double alignment = result.Lines[1].Nx * referenceLines[1].Nx + result.Lines[1].Ny * referenceLines[1].Ny;
                        double minimumAlignment = Math.Cos(3 * Math.PI / 180);
                        if (Math.Abs(alignment) < minimumAlignment || referenceLines.Any(a => !result.Lines.Any(b => Math.Abs(a.Nx * b.Nx + a.Ny * b.Ny) >= minimumAlignment)))
                            throw new InvalidOperationException("Detected mask orientation changed during the scan.");
                        errors[i] = result.SignedErrorPixels * (alignment < 0 ? -1 : 1);
                    }
                    Array.Sort(errors);
                    if (errors[2] - errors[0] > 1.0) throw new InvalidOperationException("Mask error is unstable. Increase exposure or improve star centering.");
                    SetAssistStatus($"Bahtinov scan | {position} | error {errors[1]:+0.00;-0.00;0.00} px");
                    return errors[1];
                }
                var completed = await BahtinovFocusRunner.RunAsync(origin, step, offsets,
                    async (position, cancellation) => {
                        if (focuserMediator.GetInfo()?.Connected != true) throw new InvalidOperationException("Focuser disconnected.");
                        return await focuserMediator.MoveFocuser(position, cancellation);
                    }, (position, cancellation) => Measure(position), token);
                int target = completed.Position;
                double final = completed.Error;
                TargetPosition = target;
                SetAssistStatus($"Bahtinov AF verified | {target} | error {final:+0.00;-0.00;0.00} px. Remove mask and check HFR.");
                Notification.ShowSuccess("Bahtinov autofocus complete. Remove the mask before imaging.");
                return 1;
            } catch (OperationCanceledException) { SetAssistStatus("Bahtinov AF cancelled; focuser remains at current position."); return 0; }
            catch (Exception e) { SetAssistStatus($"Bahtinov AF stopped: {e.Message}"); throw; }
            finally { EndAssist(true); }
        }
        private static ImageSource RenderFocusPreview(double[] pixels, int width, int height, BahtinovMeasurement measurement) {
            var sorted = (double[])pixels.Clone(); Array.Sort(sorted);
            double black = sorted[sorted.Length / 2], white = sorted[(int)(sorted.Length * .998)];
            double scale = Math.Max(1, white - black);
            var bytes = new byte[pixels.Length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(255 * Math.Sqrt(Math.Clamp((pixels[i] - black) / scale, 0, 1)));
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, bytes, width);
            bitmap.Freeze();
            var group = new DrawingGroup();
            using (var drawing = group.Open()) {
                drawing.DrawImage(bitmap, new Rect(0, 0, width, height));
                if (measurement?.IsValid == true) {
                    var colors = new[] { Brushes.Cyan, Brushes.Lime, Brushes.Orange };
                    for (int i = 0; i < measurement.Lines.Length; i++) {
                        var line = measurement.Lines[i];
                        double x = (width - 1) / 2.0 + line.Nx * line.Offset, y = (height - 1) / 2.0 + line.Ny * line.Offset;
                        double length = Math.Min(width, height) * .42;
                        drawing.DrawLine(new Pen(colors[i], .7), new Point(x - line.Ny * length, y + line.Nx * length), new Point(x + line.Ny * length, y - line.Nx * length));
                    }
                }
            }
            var result = new DrawingImage(group); result.Freeze(); return result;
        }
    }
}
