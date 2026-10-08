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
using NINA.Core.Model;
using NINA.Core.Utility.Notification;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        private CancellationTokenSource assistCts;
        private bool assistRunning;
        private bool assistCaptureReserved;
        private bool isStoppingFocusPreview;
        public bool IsStoppingFocusPreview => isStoppingFocusPreview;
        private double previewExposureMs = 250, previewX = 50, previewY = 50;
        private bool analyzeMask;
        private int pendingPreviewMove;
        private string focusMode = "Live";
        private string autofocusMethod = "Spike";
        public string AutofocusMethod {
            get => autofocusMethod;
            set {
                if(assistRunning || IsMoving || (value!="Spike" && value!="Bahtinov" && value!="Linear") || value==autofocusMethod) return;
                autofocusMethod=value; RaisePropertyChanged();
            }
        }
        public string FocusMode {
            get => focusMode;
            set {
                if (focusMode == value || IsMoving || IsGoingToFocusTarget) return;
                focusMode = value;
                assistCts?.Cancel();
                IsSelectingRoi = false;
                AutoFocusPreviewVisible = false;
                CancelGraphSelection();RaisePropertyChanged(nameof(GraphPositionMarkersVisible));
                PublishPreviewSpikeAngle(null);
                RaisePropertyChanged();
            }
        }
        public AsyncObservableCollection<OxyPlot.DataPoint> LiveHfrPoints { get; } = new();
        public OxyPlot.DataPoint[] LiveStarProfile { get; private set; } = Array.Empty<OxyPlot.DataPoint>();
        public double LiveHfr { get; private set; } = double.NaN;
        public string LiveHfrText => double.IsFinite(LiveHfr) ? $"Local HFR: {LiveHfr:F2} px" : "Local HFR: no star detected";
        public string LiveTimingText { get; private set; } = "Timing will appear after the first live frame.";
        private ImageSource overviewImage;
        private bool isSelectingRoi;
        public bool IsSelectingRoi { get => isSelectingRoi; private set { isSelectingRoi = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(LiveDisplayImage)); RaisePropertyChanged(nameof(RoiLocationText)); RaisePropertyChanged(nameof(GraphPositionMarkersVisible)); } }
        public ImageSource LiveDisplayImage => IsSelectingRoi ? overviewImage : FocusPreviewImage;
        public string RoiLocationText => $"{(IsSelectingRoi ? "Full frame: click a star or drag a rectangle. " : "")}ROI {PreviewRoiRectangle.Width}×{PreviewRoiRectangle.Height} px | X {PreviewCenterX:F1}%, Y {PreviewCenterY:F1}% | HFR: central 256 px";
        public ICommand SelectRoiCommand { get; private set; }
        public ICommand RefreshRoiImageCommand { get; private set; }
        public ICommand ClearLiveGraphCommand { get; private set; }
        public bool IsFocusAssistRunning => assistRunning;
        public bool CanConfigureLive => !assistRunning && !IsCapturing && !IsMoving && !IsGoingToFocusTarget;
        public bool CanUseFocusStreaming => CanConfigureLive && DataModel.SupportsFocusStreaming;
        public string FocusStreamAvailability => DataModel.SupportsFocusStreaming ? "Continuous streaming (experimental)" : "Single-frame preview · streaming unavailable for this camera or 3×3 bin mode";
        public bool UseFocusStreaming => Properties.Settings.Default.UseFocusStreaming;
        public double PreviewExposureMs { get => previewExposureMs; set { if (double.IsFinite(value)) previewExposureMs = Math.Clamp(value, 1, 5000); ResetSharedFocusMeasurements(); RaisePropertyChanged(); } }
        public double PreviewCenterX { get => previewX; set { if (double.IsFinite(value)) previewX = Math.Clamp(value, 0, 100); ResetSharedFocusMeasurements(); RaisePropertyChanged(); UpdateRoiSelection(); } }
        public double PreviewCenterY { get => previewY; set { if (double.IsFinite(value)) previewY = Math.Clamp(value, 0, 100); ResetSharedFocusMeasurements(); RaisePropertyChanged(); UpdateRoiSelection(); } }
        private void ResetSharedFocusMeasurements() {
            PublishPreviewSpikeAngle(null);
            LiveHfrPoints.Clear();
            ClearAutofocusStatistics();
            AutoFocusMetricText = "Autofocus preview";
            RaisePropertyChanged(nameof(AutoFocusMetricText));
            DataModel.ResetPlotData();
            RaiseMeasurementProperties();
            LiveHfr = double.NaN;
            LiveStarProfile = Array.Empty<OxyPlot.DataPoint>();
            RaisePropertyChanged(nameof(LiveHfrText)); RaisePropertyChanged(nameof(PreviewMetricText)); RaisePropertyChanged(nameof(LiveStarProfile));
        }
        public bool AnalyzeBahtinov { get => analyzeMask; set { analyzeMask = value; RaisePropertyChanged(); } }
        private ImageSource focusPreviewImage;
        public ImageSource FocusPreviewImage {
            get => focusPreviewImage;
            private set {
                focusPreviewImage = value;
                if (value != null && previewPixels.TryGetValue(value, out var raw) && raw.Strength != PreviewStretchStrength) {
                    stretchRefresh?.Cancel(); _ = RefreshPreviewStretchAsync();
                }
            }
        }
        public string FocusAssistStatus { get; private set; } = "Preview: center a star in the ROI. Exposure is not the delivered frame interval.";
        public ICommand StartFocusPreviewCommand { get; private set; }
        public ICommand StopFocusPreviewCommand { get; private set; }
        public ICommand BahtinovAFCommand { get; private set; }
        public ICommand PreviewMoveInCommand { get; private set; }
        public ICommand PreviewMoveOutCommand { get; private set; }
        private void InitializeFocusAssist() {
            ResetPreviewStretchCommand = new RelayCommand(_ => PreviewStretchStrength = 1);
            InitializeRoiSelection();
            StartFocusPreviewCommand = new AsyncCommand<int>(() => RunGuarded("Focus preview", RunFocusPreviewAsync), _ => CanStartAssist());
            SelectRoiCommand = new AsyncCommand<int>(() => RunGuarded("Select ROI", CaptureOverviewAsync), _ => CanStartAssist());
            RefreshRoiImageCommand = new AsyncCommand<int>(() => RunGuarded("Refresh ROI image", () => CaptureOverviewCoreAsync(true)), _ => CanStartAssist());
            StopFocusPreviewCommand = new RelayCommand(_ => {
                isStoppingFocusPreview = true;
                RaisePropertyChanged(nameof(IsStoppingFocusPreview));
                SetAssistStatus("Stopping; waiting for the camera...");
                assistCts?.Cancel();
            }, _ => assistRunning && !isStoppingFocusPreview);
            ClearLiveGraphCommand = new RelayCommand(_ => { LiveHfrPoints.Clear(); ClearAutofocusStatistics(); }, _ => !IsMoving);
            BahtinovAFCommand = new AsyncCommand<int>(() => RunGuarded("Bahtinov AF", RunBahtinovAfAsync), _ => CanStartAssist() && FocuserInfo?.Connected == true);
            SpikeAFCommand = new AsyncCommand<int>(() => RunGuarded("Spike AF", () => RunLiveAutofocusAsync(false)), _ => CanStartAssist() && FocuserInfo?.Connected == true);
            PreviewMoveInCommand = new RelayCommand(_ => pendingPreviewMove = -(int)Math.Clamp(Math.Abs((long)UserStep), 1, 10000),
                _ => assistRunning && !isStoppingFocusPreview && !IsMoving && FocuserInfo?.Connected == true && pendingPreviewMove == 0);
            PreviewMoveOutCommand = new RelayCommand(_ => pendingPreviewMove = (int)Math.Clamp(Math.Abs((long)UserStep), 1, 10000),
                _ => assistRunning && !isStoppingFocusPreview && !IsMoving && FocuserInfo?.Connected == true && pendingPreviewMove == 0);
        }
        private bool CanStartAssist() => !disposed && !assistRunning && !IsMoving && !IsCapturing && !IsGoingToFocusTarget && CameraInfo?.Connected == true
            && !CameraInfo.IsExposing && !CameraInfo.LiveViewEnabled && cameraMediator.IsFreeToCapture(this);
        private Task<int> CaptureOverviewAsync() => CaptureOverviewCoreAsync(false);
        private async Task<int> CaptureOverviewCoreAsync(bool forceNew) {
            if (!CanStartAssist()) return 0;
            try {
                BeginAssist(false);
                SetAssistStatus("Capturing full frame for ROI selection...");
                var cached = forceNew ? null : GetPreparedOverview();
                var frame = cached != null
                    ? await Task.Run(() => ManualFocuserModel.CreateFocusOverview(cached, assistCts.Token), assistCts.Token)
                    : await DataModel.CaptureFocusPreviewAsync(PreviewExposureMs / 1000, 256, 50, 50, assistCts.Token, overview: true);
                assistCts.Token.ThrowIfCancellationRequested();
                overviewImage = RenderFocusPreview(frame.Pixels, frame.Width, frame.Height, null);
                overviewPixels = frame.Pixels; overviewWidth = frame.Width; overviewHeight = frame.Height;
                overviewSensorWidth = cached?.Properties.Width ?? DataModel.LastPreviewTiming.SourceWidth;
                overviewSensorHeight = cached?.Properties.Height ?? DataModel.LastPreviewTiming.SourceHeight;
                IsSelectingRoi = true;
                UpdateRoiSelection();
                RaisePropertyChanged(nameof(LiveDisplayImage)); RaisePropertyChanged(nameof(RoiLocationText));
                SetAssistStatus("Click a star, or drag an area around it.");
                Logger.Info($"[ManualFocuser/ROI] source={(cached != null ? "prepared image" : "new full capture")} sensor={overviewSensorWidth}x{overviewSensorHeight}");
                return 1;
            } finally { EndAssist(false); }
        }
        private long lastLiveStatusTimestamp;
        private void SetAssistStatus(string status) {
            FocusAssistStatus = status;
            RaisePropertyChanged(nameof(FocusAssistStatus));
            if (!assistRunning) return;
            long timestamp = Stopwatch.GetTimestamp();
            bool liveFrame = status.StartsWith("Streaming |") || status.StartsWith("Single frames |");
            if (liveFrame && Stopwatch.GetElapsedTime(lastLiveStatusTimestamp, timestamp).TotalMilliseconds < 500) return;
            lastLiveStatusTimestamp = timestamp;
            applicationStatusMediator.StatusUpdate(new ApplicationStatus { Source = "Manual Focuser", Status = status });
        }
        private void BeginAssist(bool moving) {
            PublishPreviewSpikeAngle(null);
            cameraMediator.RegisterCaptureBlock(this);
            assistCaptureReserved=true;
            isStoppingFocusPreview = false;
            RaisePropertyChanged(nameof(IsStoppingFocusPreview));
            assistCts?.Dispose(); assistCts = new CancellationTokenSource();
            pendingPreviewMove = 0;
            pendingPreviewTarget=null;graphPreviewMovesEnabled=false;CancelGraphSelection();
            assistRunning = true; IsCapturing = true; if (moving) IsMoving = true;
            RaisePropertyChanged(nameof(IsFocusAssistRunning));
            RaisePropertyChanged(nameof(CanConfigureLive));
            RaisePropertyChanged(nameof(CanUseFocusStreaming));
        }
        private void EndAssist(bool moving) {
            if(assistCaptureReserved) {
                assistCaptureReserved=false;
                try { cameraMediator.ReleaseCaptureBlock(this); } catch (Exception e) { Logger.Error("Preview camera reservation release failed", e); }
            }
            applicationStatusMediator.StatusUpdate(new ApplicationStatus { Source = "Manual Focuser", Status = string.Empty });
            assistRunning = false; IsCapturing = false; if (moving) IsMoving = false;
            pendingPreviewTarget=null;graphPreviewMovesEnabled=false;
            isStoppingFocusPreview = false;
            RaisePropertyChanged(nameof(IsStoppingFocusPreview));
            RaisePropertyChanged(nameof(IsFocusAssistRunning));
            RaisePropertyChanged(nameof(CanConfigureLive));
            RaisePropertyChanged(nameof(CanUseFocusStreaming));
        }
        private async Task<int> RunFocusPreviewAsync() {
            if (!CanStartAssist()) return 0;
            LatestFrameStream<ManualFocuserModel.StreamPreviewFrame> stream = null;
            Task<int> previewMove = null;
            async Task FinishMove() {
                var movement=previewMove; previewMove=null;
                try { if(await movement<0) throw new InvalidOperationException("Live focuser movement failed or the focuser disconnected."); }
                finally { IsMoving=false; }
            }
            try {
                BeginAssist(false);
                IsSelectingRoi = false;
                RaisePropertyChanged(nameof(RoiLocationText));
                LiveHfrPoints.Clear();
                var clock = Stopwatch.StartNew(); long previous = 0;
                var diagnostics = new FocusDiagnosticSession("LiveFocus");
                long lastSaved = -5000;
                Logger.Info("[ManualFocuser/Diagnostics] " + diagnostics.DirectoryPath);
                int frames = 0;
                long lastSpikeAnalysis = -1000;
                bool streaming = UseFocusStreaming && DataModel.SupportsFocusStreaming;
                double requestedExposureMs = PreviewExposureMs, centerX = PreviewCenterX, centerY = PreviewCenterY;
                int roiWidth = PreviewRoiWidth, roiHeight = PreviewRoiHeight;
                SetAssistStatus(streaming ? "Starting camera stream..." : "Starting single-frame preview...");
                Logger.Info($"[ManualFocuser/LiveStream] starting mode={(streaming ? "stream" : "single")} readMode={CameraInfo?.ReadoutMode} exposureMs={requestedExposureMs} roiCenter={centerX},{centerY}");
                while (true) {
                    assistCts.Token.ThrowIfCancellationRequested();
                    if(previewMove?.IsCompleted==true) await FinishMove();
                    if (pendingPreviewMove != 0) {
                        if(previewMove!=null) throw new InvalidOperationException("A live focuser move is already running.");
                        if(cameraMediator.GetInfo()?.Connected!=true) throw new InvalidOperationException("Live camera disconnected. No focuser move.");
                        if(focuserMediator.GetInfo()?.Connected!=true) throw new InvalidOperationException("Focuser disconnected.");
                        int relative = pendingPreviewMove; pendingPreviewMove = 0;
                        IsMoving = true;
                        Logger.Info($"[ManualFocuser/LiveMove] relative={relative} keepStream={streaming}");
                        // The existing reader remains the sole owner of the camera SDK.
                        // The separate focuser task changes no camera/exposure/ROI settings.
                        previewMove=focuserMediator.MoveFocuserRelative(relative,assistCts.Token);
                        if(!streaming) await FinishMove();
                    }
                    (double[] Pixels, int Width, int Height, bool HardwareRoi) frame;
                    ManualFocuserModel.PreviewTiming timing;
                    if (streaming) {
                        stream ??= DataModel.StartFocusStreaming(requestedExposureMs / 1000, roiWidth, centerX, centerY, assistCts.Token, roiHeight);
                        var received = await stream.ReadAsync(assistCts.Token);
                        frame = (received.Pixels, received.Width, received.Height, received.HardwareRoi);
                        timing = received.Timing;
                    } else {
                        frame = await DataModel.CaptureFocusPreviewAsync(requestedExposureMs / 1000, roiWidth, centerX, centerY, assistCts.Token, roiHeight: roiHeight);
                        timing = DataModel.LastPreviewTiming;
                    }
                    var processingTimer = Stopwatch.StartNew();
                    bool mask = AnalyzeBahtinov;
                    var measurement = mask ? await Task.Run(() => BahtinovAnalyzer.Analyze(frame.Pixels, frame.Width, frame.Height), assistCts.Token) : null;
                    var analysisFrame = FocusRoi.CenterWindow(frame.Pixels, frame.Width, frame.Height);
                    double hfr = await Task.Run(() => QuickFocusMetrics.HalfFluxRadius(analysisFrame.Pixels, analysisFrame.Width, analysisFrame.Height), assistCts.Token);
                    if (clock.ElapsedMilliseconds - lastSpikeAnalysis >= 1000) {
                        var spikes = await Task.Run(() => AnalyzePreviewSpikes(frame.Pixels, frame.Width, frame.Height), assistCts.Token);
                        assistCts.Token.ThrowIfCancellationRequested();
                        PublishPreviewSpikeAngle(spikes);
                        lastSpikeAnalysis = clock.ElapsedMilliseconds;
                    }
                    if (clock.ElapsedMilliseconds-lastSaved>=5000) {
                        await diagnostics.SaveAsync(frame.Pixels,frame.Width,frame.Height,new {
                            TimestampUtc=DateTime.UtcNow,CameraId=CameraInfo?.DeviceId,ExposureMs=requestedExposureMs,
                            Gain=CameraInfo?.Gain,Offset=CameraInfo?.Offset,ReadoutMode=CameraInfo?.ReadoutMode,
                            FocuserPosition=FocuserInfo?.Position,FocuserMoving=IsMoving,RoiWidth=roiWidth,RoiHeight=roiHeight,
                            CenterXPercent=centerX,CenterYPercent=centerY,Hfr=hfr,Mask=measurement,DisplayStretched=false
                        });
                        lastSaved=clock.ElapsedMilliseconds;
                    }
                    assistCts.Token.ThrowIfCancellationRequested();
                    double analysisMs = processingTimer.Elapsed.TotalMilliseconds;
                    processingTimer.Restart();
                    FocusPreviewImage = await Task.Run(() => RenderFocusPreview(frame.Pixels, frame.Width, frame.Height, measurement), assistCts.Token);
                    assistCts.Token.ThrowIfCancellationRequested();
                    RaisePropertyChanged(nameof(FocusPreviewImage));
                    RaisePropertyChanged(nameof(LiveDisplayImage));
                    long now = clock.ElapsedMilliseconds;
                    LiveHfr = hfr;
                    // A single gap separates valid runs; missing frames are not measurements.
                    if (double.IsFinite(hfr))
                        LiveHfrPoints.Add(new OxyPlot.DataPoint(now / 1000.0, hfr));
                    else if (LiveHfrPoints.Count > 0 && double.IsFinite(LiveHfrPoints[^1].Y))
                        LiveHfrPoints.Add(new OxyPlot.DataPoint(now / 1000.0, double.NaN));
                    while (LiveHfrPoints.Count > 600 || (LiveHfrPoints.Count > 1 && LiveHfrPoints[0].X < now / 1000.0 - 120)) LiveHfrPoints.RemoveAt(0);
                    int peak = Array.IndexOf(analysisFrame.Pixels, analysisFrame.Pixels.Max());
                    int px = peak % analysisFrame.Width, py = peak / analysisFrame.Width;
                    var profileSorted = (double[])analysisFrame.Pixels.Clone(); Array.Sort(profileSorted);
                    double background = profileSorted[profileSorted.Length / 2];
                    double amplitude = Math.Max(1, analysisFrame.Pixels[peak] - background);
                    LiveStarProfile = double.IsFinite(hfr)
                        ? Enumerable.Range(Math.Max(0, px - 64), Math.Min(analysisFrame.Width - 1, px + 64) - Math.Max(0, px - 64) + 1)
                            .Select(x => new OxyPlot.DataPoint(x - px, Math.Max(0, analysisFrame.Pixels[py * analysisFrame.Width + x] - background) / amplitude)).ToArray()
                        : Array.Empty<OxyPlot.DataPoint>();
                    RaisePropertyChanged(nameof(LiveHfr));
                    RaisePropertyChanged(nameof(LiveHfrText)); RaisePropertyChanged(nameof(PreviewMetricText));
                    RaisePropertyChanged(nameof(LiveStarProfile));
                    string metric = mask ? measurement.IsValid ? $"Mask error {measurement.SignedErrorPixels:+0.00;-0.00;0.00} px" : $"HFR fallback: {measurement.FailureReason}" : double.IsFinite(hfr) ? $"Local HFR {hfr:F2} px" : "No isolated star detected";
                    if (mask && !measurement.IsValid) {
                        if (double.IsFinite(hfr)) metric = $"Local HFR {hfr:F2} px | mask not detected";
                    }
                    double previewMs = processingTimer.Elapsed.TotalMilliseconds;
                    now = clock.ElapsedMilliseconds;
                    LiveTimingText = streaming
                        ? $"Stream receive {timing.CaptureAndDownloadMs:F0} ms | Convert {timing.ConversionMs:F0} | Crop {timing.CropMs:F0} | Analyze {analysisMs:F0} | Preview/graph {previewMs:F0} ms"
                        : $"Capture+download {timing.CaptureAndDownloadMs:F0} ms (host download {timing.HostDownloadMs:F0}) | Convert {timing.ConversionMs:F0} | Crop {timing.CropMs:F0} | Analyze {analysisMs:F0} | Preview/graph {previewMs:F0} ms";
                    RaisePropertyChanged(nameof(LiveTimingText));
                    if (++frames == 1 || frames % 20 == 0)
                        Logger.Info($"[ManualFocuser/LiveTiming] mode={(streaming ? "stream" : "single")} frame={frames} exposureRequestedMs={requestedExposureMs:F0} intervalMs={now-previous} source={timing.SourceWidth}x{timing.SourceHeight} roi={frame.Width}x{frame.Height} hardwareRoi={frame.HardwareRoi} mask={mask} {LiveTimingText}");
                    SetAssistStatus($"{(streaming ? "Streaming" : "Single frames")} | {(IsMoving?"Focuser moving | ":"")}{metric} | {now - previous} ms/frame | {(frame.HardwareRoi ? "camera ROI" : "software crop / full download")}");
                    previous = now;
                }
            } catch (OperationCanceledException) { SetAssistStatus("Preview stopped."); return 0; }
            catch { SetAssistStatus("Preview failed; camera reservation released."); throw; }
            finally {
                // A capture error/Stop also cancels the motor. Hold reservation/UI
                // ownership until both native stream cleanup and movement finish.
                assistCts?.Cancel();
                try { if (stream != null) await stream.DisposeAsync(); }
                catch { SetAssistStatus("Camera stream stop failed. Reconnect the camera before retrying."); throw; }
                finally {
                    try {
                        if(previewMove!=null) await FinishMove();
                    } catch(OperationCanceledException) when(assistCts?.IsCancellationRequested==true) { }
                    catch(Exception error) { Logger.Error("Live focuser movement cleanup failed",error); }
                    finally { IsMoving=false; EndAssist(false); }
                }
            }
        }
        private Task<int> RunBahtinovAfAsync() => RunLiveAutofocusAsync(true);
        private ImageSource RenderFocusPreview(double[] pixels, int width, int height, BahtinovMeasurement measurement, double? strength = null) {
            double level = strength ?? PreviewStretchStrength;
            var bytes = FocusDisplayStretch.Render(pixels, level);
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
            var result = new DrawingImage(group); result.Freeze();
            previewPixels.Add(result, new PreviewPixels(pixels, width, height, measurement, level));
            return result;
        }
    }
}
