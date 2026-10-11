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
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Core.Utility;
using NINA.Core.Model;
using NINA.Core.Utility.Notification;

namespace Cwseo.NINA.ManualFocuser.Dockables
{
    public partial class ManualFocuserDockableVM
    {
        private static FocusDiagnosticSession CreateDiagnosticSession(string mode) => new(mode,
            enabled: Properties.Settings.Default.EnableFocusDiagnostics,
            stillEnabled: () => Properties.Settings.Default.EnableFocusDiagnostics);
        private CancellationTokenSource assistCts;
        private bool assistRunning;
        private bool assistCaptureReserved;
        private bool isStoppingFocusPreview;
        public bool IsStoppingFocusPreview => isStoppingFocusPreview;
        private double previewExposureMs = 250, previewX = 50, previewY = 50;
        private bool analyzeMask;
        private int pendingPreviewMove;
        public AsyncObservableCollection<OxyPlot.DataPoint> LiveHfrPoints { get; } = new();
        public OxyPlot.DataPoint[] LiveStarProfile { get; private set; } = Array.Empty<OxyPlot.DataPoint>();
        public double LiveHfr { get; private set; } = double.NaN;
        public int LiveHfrStarCount { get; private set; }
        public string LiveHfrText => !HfrAnalysisEnabled ? "HFR disabled" : "Multi-star HFR: " +
            (double.IsFinite(LiveHfr) ? $"{LiveHfr:F2} px · {LiveHfrStarCount} stars" : "no valid stars");
        public string LiveTimingText { get; private set; } = "Timing will appear after the first live frame.";
        private ImageSource overviewImage;
        private bool isSelectingRoi;
        public bool IsSelectingRoi { get => isSelectingRoi; private set { isSelectingRoi = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(LiveDisplayImage)); RaisePropertyChanged(nameof(LiveDisplayBitmap)); RaisePropertyChanged(nameof(LiveDisplayOverlay)); RaisePropertyChanged(nameof(CanInspectFocusImage)); RaisePropertyChanged(nameof(RoiLocationText)); RaisePropertyChanged(nameof(LiveDisplayInspectorTiles)); RaisePropertyChanged(nameof(HasDisplayInspectorTiles)); RefreshStarBoxes(); UpdateNinaHfrHistory(); UpdateMainRoiEditor(); } }
        public ImageSource LiveDisplayImage => IsSelectingRoi ? overviewImage : FocusPreviewImage;
        public string RoiLocationText => HasRoiSensorDimensions ? $"{(IsSelectingRoi ? "Click a star or drag to move / resize. " : "")}ROI {PreviewRoiRectangle.Width}×{PreviewRoiRectangle.Height} px | X {PreviewCenterX:F1}%, Y {PreviewCenterY:F1}%" : "Connect a camera to choose the capture ROI.";
        public ICommand SelectRoiCommand { get; private set; }
        public ICommand RefreshRoiImageCommand { get; private set; }
        public ICommand ClearLiveGraphCommand { get; private set; }
        public bool IsFocusAssistRunning => assistRunning;
        public bool CanConfigureLive => !assistRunning && !IsCapturing && !IsMoving && !IsGoingToFocusTarget;
        public bool CanUseFocusStreaming => CanConfigureLive && DataModel.SupportsFocusStreaming;
        public bool UseFocusStreaming => Properties.Settings.Default.UseFocusStreaming;
        public double PreviewExposureMs { get => previewExposureMs; set { if (double.IsFinite(value)) previewExposureMs = Math.Clamp(value, 1, 5000); ResetSharedFocusMeasurements(); RaisePropertyChanged(); } }
        public double PreviewCenterX { get => previewX; set { if (double.IsFinite(value)) previewX = Math.Clamp(value, 0, 100); ResetSharedFocusMeasurements(); RaisePropertyChanged(); UpdateRoiSelection(); } }
        public double PreviewCenterY { get => previewY; set { if (double.IsFinite(value)) previewY = Math.Clamp(value, 0, 100); ResetSharedFocusMeasurements(); RaisePropertyChanged(); UpdateRoiSelection(); } }
        private void ResetSharedFocusMeasurements()
        {
            ClearLiveHistory(); LiveHfr = double.NaN; LiveHfrStarCount = 0;
            ResetStarTracking();
            positionHistory.Clear(); RefreshHistoryPresentation();
            LiveStarProfile = Array.Empty<OxyPlot.DataPoint>();
            RaisePropertyChanged(nameof(LiveHfrText)); RaisePropertyChanged(nameof(LiveStarProfile));
        }
        public bool AnalyzeBahtinov { get => analyzeMask; set { analyzeMask = value; RaisePropertyChanged(); } }
        private ImageSource focusPreviewImage;
        public ImageSource FocusPreviewImage
        {
            get => focusPreviewImage;
            private set
            {
                focusPreviewImage = value;
                var raw = value != null && previewPixels.TryGetValue(value, out var entry) ? entry : null;
                if (IsAberrationInspector && !CanInspectFrame(raw?.Raw)) SetAberrationInspector(false);
                RaisePropertyChanged(nameof(LiveDisplayImage)); RaisePropertyChanged(nameof(LiveDisplayBitmap)); RaisePropertyChanged(nameof(LiveDisplayOverlay));
                RaisePropertyChanged(nameof(CanInspectFocusImage));
                RaisePropertyChanged(nameof(LiveDisplayInspectorTiles)); RaisePropertyChanged(nameof(HasDisplayInspectorTiles));
                RefreshStarBoxes();
                SetSyncSource(raw?.Raw?.SourceImage);
                if (raw != null && (raw.Strength != PreviewStretchStrength || raw.AutoStretch != PreviewAutoStretch || raw.Inspector != IsAberrationInspector))
                {
                    stretchRefresh?.Cancel(); _ = RefreshPreviewStretchAsync();
                }
            }
        }
        public string FocusAssistStatus { get; private set; } = "Preview: center a star in the ROI. Exposure is not the delivered frame interval.";
        public ICommand StartFocusPreviewCommand { get; private set; }
        public ICommand StopFocusPreviewCommand { get; private set; }
        public ICommand PreviewMoveInCommand { get; private set; }
        public ICommand PreviewMoveOutCommand { get; private set; }
        private void InitializeFocusAssist()
        {
            liveHistory = new LiveHfrHistory(LiveHfrPoints);
            InitializeImageSync();
            ResetPreviewStretchCommand = new RelayCommand(_ => PreviewStretchStrength = 1);
            InitializeRoiSelection();
            StartFocusPreviewCommand = new AsyncCommand<int>(() => RunGuarded("Focus preview", RunFocusPreviewAsync), _ => CanStartAssist());
            SelectRoiCommand = new AsyncCommand<int>(() => RunGuarded("Select ROI", CaptureOverviewAsync), _ => CanStartAssist());
            RefreshRoiImageCommand = new AsyncCommand<int>(() => RunGuarded("Refresh ROI image", () => CaptureOverviewCoreAsync(true)), _ => CanStartAssist());
            StopFocusPreviewCommand = new RelayCommand(_ =>
            {
                isStoppingFocusPreview = true;
                RaisePropertyChanged(nameof(IsStoppingFocusPreview));
                UpdateNinaHfrHistory();
                SetAssistStatus("Stopping; waiting for the camera...");
                assistCts?.Cancel();
            }, _ => assistRunning && !isStoppingFocusPreview);
            ClearLiveGraphCommand = new RelayCommand(_ => ClearFocusHistory(), _ => !IsMoving);
            PreviewMoveInCommand = new RelayCommand(_ => pendingPreviewMove = -(int)Math.Clamp(Math.Abs((long)UserStep), 1, 10000),
                _ => !IsManualFocus && assistRunning && !isStoppingFocusPreview && !IsMoving && FocuserInfo?.Connected == true && pendingPreviewMove == 0);
            PreviewMoveOutCommand = new RelayCommand(_ => pendingPreviewMove = (int)Math.Clamp(Math.Abs((long)UserStep), 1, 10000),
                _ => !IsManualFocus && assistRunning && !isStoppingFocusPreview && !IsMoving && FocuserInfo?.Connected == true && pendingPreviewMove == 0);
        }
        private bool CanStartAssist() => !disposed && !assistRunning && !IsMoving && !IsCapturing && !IsGoingToFocusTarget && CameraInfo?.Connected == true
            && !CameraInfo.IsExposing && !CameraInfo.LiveViewEnabled && cameraMediator.IsFreeToCapture(this);
        private Task<int> CaptureOverviewAsync() => CaptureOverviewCoreAsync(false);
        private async Task<int> CaptureOverviewCoreAsync(bool forceNew)
        {
            if (!CanStartAssist()) return 0;
            try
            {
                BeginAssist(false);
                SetAssistStatus("Capturing full frame for ROI selection...");
                var cached = forceNew ? null : GetPreparedOverview();
                var frame = cached != null
                    ? await Task.Run(() => LiveFocusModel.CreateFocusOverview(cached, assistCts.Token), assistCts.Token)
                    : await DataModel.CaptureFocusPreviewAsync(PreviewExposureMs / 1000, 256, 50, 50, assistCts.Token, overview: true);
                assistCts.Token.ThrowIfCancellationRequested();
                overviewImage = RenderFocusPreview(frame.Pixels, frame.Width, frame.Height, null);
                overviewPixels = frame.Pixels; overviewWidth = frame.Width; overviewHeight = frame.Height;
                overviewSensorWidth = cached?.Properties.Width ?? DataModel.LastPreviewTiming.SourceWidth;
                overviewSensorHeight = cached?.Properties.Height ?? DataModel.LastPreviewTiming.SourceHeight;
                RaisePropertyChanged(nameof(HasRoiSensorDimensions)); RaisePropertyChanged(nameof(PreviewRoiPreset));
                SetAberrationInspector(false);
                IsSelectingRoi = true;
                ShowFocusImagePanel();
                UpdateRoiSelection();
                RaisePropertyChanged(nameof(LiveDisplayImage)); RaisePropertyChanged(nameof(RoiLocationText));
                SetAssistStatus("Click a star, or drag an area around it.");
                if (Properties.Settings.Default.EnableFocusDiagnostics) Logger.Info($"[LiveFocus/ROI] source={(cached != null ? "prepared image" : "new full capture")} sensor={overviewSensorWidth}x{overviewSensorHeight}");
                return 1;
            }
            finally { EndAssist(false); }
        }
        private long lastLiveStatusTimestamp;
        private void SetAssistStatus(string status)
        {
            FocusAssistStatus = status;
            RaisePropertyChanged(nameof(FocusAssistStatus));
            if (!assistRunning) return;
            long timestamp = Stopwatch.GetTimestamp();
            bool liveFrame = status.StartsWith("Streaming |") || status.StartsWith("Single frames |");
            if (liveFrame && Stopwatch.GetElapsedTime(lastLiveStatusTimestamp, timestamp).TotalMilliseconds < 500) return;
            lastLiveStatusTimestamp = timestamp;
            applicationStatusMediator.StatusUpdate(new ApplicationStatus { Source = "Live Focus", Status = status });
        }
        private void BeginAssist(bool moving)
        {
            cameraMediator.RegisterCaptureBlock(this);
            assistCaptureReserved = true;
            isStoppingFocusPreview = false;
            RaisePropertyChanged(nameof(IsStoppingFocusPreview));
            assistCts?.Dispose(); assistCts = new CancellationTokenSource();
            pendingPreviewMove = 0;
            assistRunning = true; IsCapturing = true; if (moving) IsMoving = true;
            RaisePropertyChanged(nameof(IsFocusAssistRunning));
            RaisePropertyChanged(nameof(CanConfigureLive));
            RaisePropertyChanged(nameof(CanUseFocusStreaming));
        }
        private void EndAssist(bool moving)
        {
            if (assistCaptureReserved)
            {
                assistCaptureReserved = false;
                try { cameraMediator.ReleaseCaptureBlock(this); } catch (Exception e) { Logger.Error("Preview camera reservation release failed", e); }
            }
            applicationStatusMediator.StatusUpdate(new ApplicationStatus { Source = "Live Focus", Status = string.Empty });
            assistRunning = false; IsCapturing = false; if (moving) IsMoving = false;
            UpdateNinaHfrHistory();
            isStoppingFocusPreview = false;
            RaisePropertyChanged(nameof(IsStoppingFocusPreview));
            RaisePropertyChanged(nameof(IsFocusAssistRunning));
            RaisePropertyChanged(nameof(CanConfigureLive));
            RaisePropertyChanged(nameof(CanUseFocusStreaming));
        }
        private async Task<int> RunFocusPreviewAsync()
        {
            if (!CanStartAssist()) return 0;
            if (HfrAnalysisEnabled && focusHfrAnalyzer == null) throw new InvalidOperationException("NINA multi-star HFR analyzer is unavailable.");
            if (IsManualFocus) return await CaptureManualFocusAsync();
            bool singleMeasurement = IsManualFocus;
            bool completedFrame = false;
            bool allowFinalSync = false;
            LatestFrameStream<LiveFocusModel.StreamPreviewFrame> stream = null;
            Task<int> previewMove = null;
            Task<FocusHfrMeasurement> hfrTask = null;
            int hfrTaskGeneration = 0;
            double nextHfrStartMs = 0;
            async Task FinishMove()
            {
                var movement = previewMove; previewMove = null;
                try { if (await movement < 0) throw new InvalidOperationException("Live focuser movement failed or the focuser disconnected."); }
                finally { IsMoving = false; }
            }
            try
            {
                BeginAssist(false);
                IsSelectingRoi = false;
                UpdateNinaHfrHistory();
                RaisePropertyChanged(nameof(RoiLocationText));
                if (!singleMeasurement) ClearLiveHistory();
                ResetStarTracking();
                LiveHfr = double.NaN; LiveHfrStarCount = 0; RaisePropertyChanged(nameof(LiveHfrText));
                var clock = Stopwatch.StartNew(); long previous = 0;
                var diagnostics = CreateDiagnosticSession("LiveFocus");
                long lastSaved = -5000;
                if (Properties.Settings.Default.EnableFocusDiagnostics) Logger.Info("[LiveFocus/Diagnostics] " + diagnostics.DirectoryPath);
                int frames = 0;
                bool streaming = !singleMeasurement && UseFocusStreaming && DataModel.SupportsFocusStreaming;
                double requestedExposureMs = PreviewExposureMs, centerX = PreviewCenterX, centerY = PreviewCenterY;
                int roiWidth = PreviewRoiWidth, roiHeight = PreviewRoiHeight;
                string cameraId = CameraInfo.DeviceId, focuserId = FocuserInfo?.DeviceId;
                SetAssistStatus(streaming ? "Starting camera stream..." : "Starting single-frame preview...");
                if (Properties.Settings.Default.EnableFocusDiagnostics) Logger.Info($"[LiveFocus/LiveStream] starting mode={(streaming ? "stream" : "single")} readMode={CameraInfo?.ReadoutMode} exposureMs={requestedExposureMs} roiCenter={centerX},{centerY}");
                while (true)
                {
                    assistCts.Token.ThrowIfCancellationRequested();
                    var camera = cameraMediator.GetInfo();
                    if (camera?.Connected != true || camera.DeviceId != cameraId) throw new InvalidOperationException("Live camera disconnected or changed.");
                    if (previewMove?.IsCompleted == true) await FinishMove();
                    if (pendingPreviewMove != 0)
                    {
                        if (previewMove != null) throw new InvalidOperationException("A live focuser move is already running.");
                        if (cameraMediator.GetInfo()?.Connected != true) throw new InvalidOperationException("Live camera disconnected. No focuser move.");
                        var focuser = focuserMediator.GetInfo();
                        if (focuser?.Connected != true || focuser.DeviceId != focuserId || focuser.IsMoving) throw new InvalidOperationException("Focuser disconnected, changed or busy.");
                        int relative = pendingPreviewMove; pendingPreviewMove = 0;
                        IsMoving = true;
                        if (Properties.Settings.Default.EnableFocusDiagnostics) Logger.Info($"[LiveFocus/LiveMove] relative={relative} keepStream={streaming}");
                        // The existing reader remains the sole owner of the camera SDK.
                        // The separate focuser task changes no camera/exposure/ROI settings.
                        previewMove = focuserMediator.MoveFocuserRelative(relative, assistCts.Token);
                        if (!streaming) await FinishMove();
                    }
                    (double[] Pixels, int Width, int Height, bool HardwareRoi) frame;
                    var beforeFocuser = singleMeasurement ? focuserMediator.GetInfo() : null;
                    int? positionBefore = beforeFocuser?.Connected == true ? beforeFocuser.Position : null;
                    string measurementFocuserId = beforeFocuser?.DeviceId;
                    bool stationaryBefore = beforeFocuser?.Connected == true && !beforeFocuser.IsMoving && !IsMoving;
                    FocusRawFrame rawFrame = null;
                    LiveFocusModel.PreviewTiming timing;
                    if (streaming)
                    {
                        stream ??= DataModel.StartFocusStreaming(requestedExposureMs / 1000, roiWidth, centerX, centerY, assistCts.Token, roiHeight, retainRawPixels: true);
                        var received = await stream.ReadAsync(assistCts.Token);
                        frame = (received.Pixels, received.Width, received.Height, received.HardwareRoi);
                        timing = received.Timing;
                        rawFrame = received.RawFrame;
                    }
                    else
                    {
                        frame = await DataModel.CaptureFocusPreviewAsync(requestedExposureMs / 1000, roiWidth, centerX, centerY, assistCts.Token, roiHeight: roiHeight, retainRawPixels: true);
                        rawFrame = DataModel.TakePreviewRawFrame();
                        timing = DataModel.LastPreviewTiming;
                    }
                    var processingTimer = Stopwatch.StartNew();
                    bool mask = AnalyzeBahtinov && !IsAberrationInspector;
                    if (rawFrame != null && (mask || diagnostics.IsEnabled)) frame.Pixels = await Task.Run(() => rawFrame.ToDoubles(assistCts.Token), assistCts.Token);
                    var measurement = mask ? await Task.Run(() => BahtinovAnalyzer.Analyze(frame.Pixels, frame.Width, frame.Height), assistCts.Token) : null;
                    FocusHfrMeasurement hfrMeasurement = null;
                    int measurementGeneration = hfrGeneration;
                    if (singleMeasurement) {
                        var token = assistCts.Token;
                        hfrMeasurement = await AnalyzeFocusHfrAsync(rawFrame,token);
                    } else {
                        if (hfrTask?.IsCompleted == true) {
                            hfrMeasurement = await hfrTask; hfrTask = null;
                            if (hfrTaskGeneration != hfrGeneration) hfrMeasurement = null;
                        }
                        if (HfrAnalysisEnabled && hfrTask == null && clock.Elapsed.TotalMilliseconds >= nextHfrStartMs) {
                            var token = assistCts.Token; var analysisSource = rawFrame;
                            hfrTaskGeneration = hfrGeneration;
                            hfrTask = AnalyzeFocusHfrAsync(analysisSource,token);
                            nextHfrStartMs = clock.Elapsed.TotalMilliseconds + 200;
                        }
                    }
                    double hfr = hfrMeasurement?.Hfr ?? LiveHfr;
                    if (diagnostics.IsEnabled && clock.ElapsedMilliseconds - lastSaved >= 5000)
                    {
                        await diagnostics.SaveAsync(frame.Pixels, frame.Width, frame.Height, new
                        {
                            TimestampUtc = DateTime.UtcNow,
                            CameraId = CameraInfo?.DeviceId,
                            ExposureMs = requestedExposureMs,
                            Gain = CameraInfo?.Gain,
                            Offset = CameraInfo?.Offset,
                            ReadoutMode = CameraInfo?.ReadoutMode,
                            FocuserPosition = FocuserInfo?.Position,
                            FocuserMoving = IsMoving,
                            RoiWidth = roiWidth,
                            RoiHeight = roiHeight,
                            CenterXPercent = centerX,
                            CenterYPercent = centerY,
                            Hfr = hfr,
                            Mask = measurement,
                            DisplayStretched = false
                        });
                        lastSaved = clock.ElapsedMilliseconds;
                    }
                    assistCts.Token.ThrowIfCancellationRequested();
                    var cameraAfterCapture = cameraMediator.GetInfo();
                    if (cameraAfterCapture?.Connected != true || cameraAfterCapture.DeviceId != cameraId)
                        throw new InvalidOperationException("Camera disconnected or changed during focus capture.");
                    double analysisMs = processingTimer.Elapsed.TotalMilliseconds;
                    processingTimer.Restart();
                    if (hfrMeasurement != null && HfrAnalysisEnabled && measurementGeneration == hfrGeneration)
                        PublishStarMeasurement(hfrMeasurement);
                    FocusPreviewImage = await Task.Run(() => rawFrame != null
                        ? RenderFocusPresentation(rawFrame, measurement)
                        : RenderFocusPreview(frame.Pixels, frame.Width, frame.Height, measurement), assistCts.Token);
                    assistCts.Token.ThrowIfCancellationRequested();
                    RaisePropertyChanged(nameof(FocusPreviewImage));
                    RaisePropertyChanged(nameof(LiveDisplayImage));
                    completedFrame = true;
                    long now = clock.ElapsedMilliseconds;
                    if (!HfrAnalysisEnabled || measurementGeneration != hfrGeneration) { hfrMeasurement = null; hfr = double.NaN; }
                    LiveHfr = hfr;
                    if (hfrMeasurement != null) {
                        LiveHfrStarCount = hfrMeasurement.Stars;
                        if (HistoryPanelEnabled) {
                            if (singleMeasurement) RecordPositionHfr(measurementFocuserId, positionBefore, stationaryBefore, hfr);
                            else RecordLiveHfr(now / 1000.0, hfr);
                        }
                    }
                    if (ShowLiveGraphs) {
                        // This optional single-star intensity profile is separate
                        // from the multi-star focus metric. Skip its copy/sort
                        // completely when the profile is hidden.
                        var analysisFrame = rawFrame != null ? rawFrame.CenterWindow() : FocusRoi.CenterWindow(frame.Pixels, frame.Width, frame.Height);
                        int peak = Array.IndexOf(analysisFrame.Pixels, analysisFrame.Pixels.Max());
                        int px = peak % analysisFrame.Width, py = peak / analysisFrame.Width;
                        var profileSorted = (double[])analysisFrame.Pixels.Clone(); Array.Sort(profileSorted);
                        double background = profileSorted[profileSorted.Length / 2];
                        double amplitude = Math.Max(1, analysisFrame.Pixels[peak] - background);
                        LiveStarProfile = Enumerable.Range(Math.Max(0, px - 64), Math.Min(analysisFrame.Width - 1, px + 64) - Math.Max(0, px - 64) + 1)
                            .Select(x => new OxyPlot.DataPoint(x - px, Math.Max(0, analysisFrame.Pixels[py * analysisFrame.Width + x] - background) / amplitude)).ToArray();
                    } else LiveStarProfile = Array.Empty<OxyPlot.DataPoint>();
                    RaisePropertyChanged(nameof(LiveHfr));
                    RaisePropertyChanged(nameof(LiveHfrText));
                    RaisePropertyChanged(nameof(LiveStarProfile));
                    string metric = mask ? measurement.IsValid ? $"Mask error {measurement.SignedErrorPixels:+0.00;-0.00;0.00} px" : $"HFR fallback: {measurement.FailureReason}" : LiveHfrText;
                    if (mask && !measurement.IsValid)
                    {
                        if (double.IsFinite(hfr)) metric = $"{LiveHfrText} | mask not detected";
                    }
                    double previewMs = processingTimer.Elapsed.TotalMilliseconds;
                    now = clock.ElapsedMilliseconds;
                    LiveTimingText = streaming
                        ? $"Stream receive {timing.CaptureAndDownloadMs:F0} ms | Convert {timing.ConversionMs:F0} | Crop {timing.CropMs:F0} | Analyze {analysisMs:F0} | Preview/graph {previewMs:F0} ms"
                        : $"Capture+download {timing.CaptureAndDownloadMs:F0} ms (host download {timing.HostDownloadMs:F0}) | Convert {timing.ConversionMs:F0} | Crop {timing.CropMs:F0} | Analyze {analysisMs:F0} | Preview/graph {previewMs:F0} ms";
                    RaisePropertyChanged(nameof(LiveTimingText));
                    LiveFrameDetails = $"Camera: {CameraInfo?.Name}\nExposure {requestedExposureMs:F0} ms | ROI {frame.Width}×{frame.Height} | Source {timing.SourceWidth}×{timing.SourceHeight}\n{LiveTimingText}";
                    RaisePropertyChanged(nameof(LiveFrameDetails));
                    if (++frames == 1 || frames % 20 == 0)
                        if (Properties.Settings.Default.EnableFocusDiagnostics) Logger.Info($"[LiveFocus/LiveTiming] mode={(streaming ? "stream" : "single")} frame={frames} exposureRequestedMs={requestedExposureMs:F0} intervalMs={now - previous} source={timing.SourceWidth}x{timing.SourceHeight} roi={frame.Width}x{frame.Height} hardwareRoi={frame.HardwareRoi} mask={mask} {LiveTimingText}");
                    SetAssistStatus($"{(streaming ? "Streaming" : "Single frames")} | {(IsMoving ? "Focuser moving | " : "")}{metric} | {now - previous} ms/frame | {(frame.HardwareRoi ? "camera ROI" : "software crop / full download")}");
                    previous = now;
                    if (singleMeasurement) { allowFinalSync = true; SetAssistStatus("Focus measurement captured."); return 1; }
                }
            }
            catch (OperationCanceledException) { allowFinalSync = isStoppingFocusPreview; SetAssistStatus("Preview stopped."); return 0; }
            catch { SetAssistStatus("Preview failed; camera reservation released."); throw; }
            finally
            {
                // A capture error/Stop also cancels the motor. Hold reservation/UI
                // ownership until both native stream cleanup and movement finish.
                UpdateNinaHfrHistory();
                assistCts?.Cancel();
                try { if (stream != null) await stream.DisposeAsync(); }
                catch { allowFinalSync = false; SetAssistStatus("Camera stream stop failed. Reconnect the camera before retrying."); throw; }
                finally
                {
                    try
                    {
                        if (previewMove != null) await FinishMove();
                    }
                    catch (OperationCanceledException) when (assistCts?.IsCancellationRequested == true) { }
                    catch (Exception error) { Logger.Error("Live focuser movement cleanup failed", error); }
                    finally {
                        if (hfrTask != null) {
                            try { await hfrTask; }
                            catch (OperationCanceledException) when (assistCts.IsCancellationRequested) { }
                            catch (Exception error) { Logger.Error("Multi-star HFR cleanup failed",error); }
                        }
                        IsMoving = false; EndAssist(false);
                        if (completedFrame && allowFinalSync && SyncImageOnStop && !disposed)
                            await RunGuarded("Sync image", SyncSourceImageAsync);
                    }
                }
            }
        }
        private ImageSource RenderFocusPreview(double[] pixels, int width, int height, BahtinovMeasurement measurement, double? strength = null)
        {
            double level = strength ?? PreviewStretchStrength;
            bool autoStretch = PreviewAutoStretch;
            var bytes = FocusDisplayStretch.Render(pixels, level, autoStretch);
            return CreateFocusPreview(bytes, width, height, measurement, level, pixels, null, autoStretch);
        }
        private ImageSource RenderRawFocusPreview(FocusRawFrame frame, BahtinovMeasurement measurement, double? strength = null)
        {
            double level = strength ?? PreviewStretchStrength;
            bool autoStretch = PreviewAutoStretch;
            return CreateFocusPreview(FocusDisplayStretch.RenderRaw(frame, level, autoStretch), frame.Width, frame.Height, measurement, level, null, frame, autoStretch);
        }
        private ImageSource CreateFocusPreview(byte[] bytes, int width, int height, BahtinovMeasurement measurement, double level, double[] pixels, FocusRawFrame raw, bool autoStretch)
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, bytes, width);
            bitmap.Freeze();
            if (measurement?.IsValid != true)
            {
                // Normal video uses this same bitmap in the chosen viewer: no
                // DrawingImage wrapper, second pixel buffer or extra rasterization.
                previewPixels.Add(bitmap, new PreviewPixels(pixels, width, height, measurement, level, bitmap, raw, AutoStretch: autoStretch) { StarAnnotation = CreateStarAnnotation(raw) });
                return bitmap;
            }
            var group = new DrawingGroup();
            using (var drawing = group.Open())
            {
                drawing.DrawImage(bitmap, new Rect(0, 0, width, height));
                if (measurement?.IsValid == true)
                {
                    var colors = new[] { Brushes.Cyan, Brushes.Lime, Brushes.Orange };
                    for (int i = 0; i < measurement.Lines.Length; i++)
                    {
                        var line = measurement.Lines[i];
                        double x = (width - 1) / 2.0 + line.Nx * line.Offset, y = (height - 1) / 2.0 + line.Ny * line.Offset;
                        double length = Math.Min(width, height) * .42;
                        drawing.DrawLine(new Pen(colors[i], .7), new Point(x - line.Ny * length, y + line.Nx * length), new Point(x + line.Ny * length, y - line.Nx * length));
                    }
                }
            }
            var result = new DrawingImage(group); result.Freeze();
            previewPixels.Add(result, new PreviewPixels(pixels, width, height, measurement, level, bitmap, raw, AutoStretch: autoStretch) { StarAnnotation = CreateStarAnnotation(raw) });
            return result;
        }
    }
}
