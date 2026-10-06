using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Cwseo.NINA.ManualFocuser.Models;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using OxyPlot;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        private bool autoFocusPreviewVisible;
        public bool AutoFocusPreviewVisible { get => autoFocusPreviewVisible; private set {
            autoFocusPreviewVisible = value; RaisePropertyChanged();
            RaisePropertyChanged(nameof(PreviewMetricText)); RaisePropertyChanged(nameof(PreviewAxisTitle)); RaisePropertyChanged(nameof(PreviewGraphPoints));
        } }
        public string PreviewMetricText => AutoFocusPreviewVisible ? AutoFocusMetricText : LiveHfrText;
        public string PreviewAxisTitle => AutoFocusPreviewVisible ? AutoFocusMetricAxis : "Local HFR (px)";
        public AsyncObservableCollection<DataPoint> PreviewGraphPoints => AutoFocusPreviewVisible ? AutoFocusMetricPoints : LiveHfrPoints;
        public string AutoFocusMetricText { get; private set; } = "Autofocus preview";
        public string AutoFocusMetricAxis { get; private set; } = "Focus metric (px)";
        public AsyncObservableCollection<DataPoint> AutoFocusMetricPoints { get; } = new();
        public ICommand SpikeAFCommand { get; private set; }

        private async Task<int> RunLiveAutofocusAsync(bool mask) {
            if (!CanStartAssist() || FocuserInfo?.Connected != true) return 0;
            int origin = FocuserInfo.Position;
            long configuredStep = Math.Abs((long)DataModel.AFStepSize);
            if (configuredStep < (mask ? 1 : 4) || configuredStep > 10000)
                throw new InvalidOperationException(mask ? "Bahtinov step must be 1–10000." : "Spike autofocus step must be 4–10000.");
            int step = (int)configuredStep, offsets = Math.Clamp(DataModel.NumInitialSteps, 1, 12);
            var settings = DataModel.CreateFocusCaptureSettings(PreviewExposureMs / 1000, PreviewRoiWidth, PreviewRoiHeight, PreviewCenterX, PreviewCenterY);
            string cameraId = cameraMediator.GetInfo().DeviceId;
            void EnsureCamera() {
                var camera = cameraMediator.GetInfo();
                if (camera?.Connected != true || camera.DeviceId != cameraId)
                    throw new InvalidOperationException("Autofocus camera disconnected or changed. No further moves.");
            }
            // Snapshot the chosen center too: neither UI edits nor profile changes alter this run.
            double centerX = PreviewCenterX, centerY = PreviewCenterY;
            LatestFrameStream<ManualFocuserModel.StreamPreviewFrame> stream = null;
            bool streaming = UseFocusStreaming && DataModel.SupportsFocusStreaming;
            var clock = Stopwatch.StartNew();
            bool verified = false;
            BahtinovLine[] reference = null;
            SpikeTrackingState tracking = null;
            var spikeParams = ManualFocuserModel.BuildSpikeParams();
            spikeParams.metricKind = SpikeMetricKind.Fwhm;
            spikeParams.minUsedStarsForValidFrame = 1;
            spikeParams.autoSpikeAngle = true;
            async Task CloseStream() { if (stream != null) { await stream.DisposeAsync(); stream = null; } }
            try {
                BeginAssist(true); IsSelectingRoi = false; AutoFocusPreviewVisible = true;
                AutoFocusMetricPoints.Clear(); LiveStarProfile = Array.Empty<DataPoint>();
                AutoFocusMetricText = "Starting autofocus...";
                RaisePropertyChanged(nameof(PreviewMetricText));
                RaisePropertyChanged(nameof(LiveStarProfile));
                AutoFocusMetricAxis = mask ? "Bahtinov error (px)" : "Spike FWHM (px)";
                RaisePropertyChanged(nameof(AutoFocusMetricAxis));
                RaisePropertyChanged(nameof(PreviewAxisTitle));
                var token = assistCts.Token;
                async Task<(double[] Pixels, int Width, int Height)> ReadFrame() {
                    token.ThrowIfCancellationRequested();
                    EnsureCamera();
                    if (streaming) {
                        stream ??= DataModel.StartFocusStreaming(settings.Seconds, settings.Roi.Width, centerX, centerY, token, settings.Roi.Height);
                        var frame = await stream.ReadAsync(token);
                        return (frame.Pixels, frame.Width, frame.Height);
                    }
                    var single = await DataModel.CaptureFocusPreviewAsync(settings.Seconds, settings.Roi.Width, centerX, centerY, token, roiHeight: settings.Roi.Height);
                    return (single.Pixels, single.Width, single.Height);
                }
                async Task Show(double[] pixels, int width, int height, BahtinovMeasurement overlay, double metric, int position) {
                    FocusPreviewImage = await Task.Run(() => RenderFocusPreview(pixels, width, height, overlay), token);
                    token.ThrowIfCancellationRequested();
                    RaisePropertyChanged(nameof(FocusPreviewImage)); RaisePropertyChanged(nameof(LiveDisplayImage));
                    AutoFocusMetricText = $"{(mask ? "Bahtinov error" : "Spike FWHM")}: {(double.IsFinite(metric) ? metric.ToString("F2") + " px" : "not detected")} | Position {position}";
                    if (verified) AutoFocusMetricText += " | Monitoring";
                    AutoFocusMetricPoints.Add(new DataPoint(clock.Elapsed.TotalSeconds, metric));
                    while (AutoFocusMetricPoints.Count > 600) AutoFocusMetricPoints.RemoveAt(0);
                    RaisePropertyChanged(nameof(AutoFocusMetricText));
                    RaisePropertyChanged(nameof(PreviewMetricText));
                    SetAssistStatus($"{(mask ? "Bahtinov" : "Spike")} AF | {AutoFocusMetricText}");
                }
                async Task<double> Measure(int position, System.Threading.CancellationToken cancellation) {
                    await ReadFrame(); // Discard the first frame after movement/restart.
                    var values = new double[3];
                    for (int i = 0; i < values.Length; i++) {
                        var frame = await ReadFrame();
                        if (frame.Pixels.Max() >= 65500) throw new InvalidOperationException("Selected star is saturated. Lower Exposure.");
                        BahtinovMeasurement overlay = null;
                        if (mask) {
                            overlay = await Task.Run(() => BahtinovAnalyzer.Analyze(frame.Pixels, frame.Width, frame.Height), token);
                            if (!overlay.IsValid) throw new InvalidOperationException("Mask detection lost: " + overlay.FailureReason);
                            reference ??= overlay.Lines;
                            double alignment = overlay.Lines[1].Nx * reference[1].Nx + overlay.Lines[1].Ny * reference[1].Ny;
                            double tolerance = Math.Cos(3 * Math.PI / 180);
                            if (Math.Abs(alignment) < tolerance || reference.Any(a => !overlay.Lines.Any(b => Math.Abs(a.Nx*b.Nx+a.Ny*b.Ny) >= tolerance)))
                                throw new InvalidOperationException("Mask orientation changed.");
                            values[i] = overlay.SignedErrorPixels * (alignment < 0 ? -1 : 1);
                        } else {
                            var local = FocusRoi.CenterWindow(frame.Pixels, frame.Width, frame.Height);
                            double hfr = await Task.Run(() => QuickFocusMetrics.HalfFluxRadius(local.Pixels, local.Width, local.Height), token);
                            if (!double.IsFinite(hfr)) throw new InvalidOperationException("No isolated star in the center of the ROI.");
                            if (tracking == null) {
                                int peak = Array.IndexOf(local.Pixels, local.Pixels.Max());
                                tracking = SpikeCore.CreateTrackingState(new[] { new SpikeSeedStar {
                                    X = peak % local.Width + (frame.Width-local.Width)/2,
                                    Y = peak / local.Width + (frame.Height-local.Height)/2,
                                    WidthPx = Math.Clamp((int)Math.Ceiling(hfr*4), 6, 60), HeightPx = Math.Clamp((int)Math.Ceiling(hfr*4),6,60),
                                    MaxBrightness = local.Pixels[peak] } }, spikeParams);
                            }
                            var pixels = frame.Pixels.Select(p => (ushort)Math.Clamp(p,0,65535)).ToArray();
                            var result = await Task.Run(() => SpikeCore.Evaluate(pixels,frame.Width,frame.Height,spikeParams,tracking),token);
                            if (!result.IsValid || !result.HasClearSpikes || !double.IsFinite(result.Metric))
                                throw new InvalidOperationException("Clear diffraction spikes were not detected. No further moves.");
                            values[i] = result.Metric;
                        }
                        await Show(frame.Pixels,frame.Width,frame.Height,overlay,values[i],position);
                    }
                    Array.Sort(values);
                    if (values[2]-values[0] > (mask ? 1 : Math.Max(.25, values[1]*.20))) throw new InvalidOperationException("Focus measurement is unstable. Adjust exposure or ROI.");
                    return values[1];
                }
                async Task<int> Move(int position, System.Threading.CancellationToken cancellation) {
                    await CloseStream(); cancellation.ThrowIfCancellationRequested();
                    EnsureCamera();
                    if (focuserMediator.GetInfo()?.Connected != true) throw new InvalidOperationException("Focuser disconnected.");
                    return await focuserMediator.MoveFocuser(position,cancellation);
                }
                if (mask) {
                    var result = await BahtinovFocusRunner.RunAsync(origin,step,offsets,Move,Measure,token);
                    TargetPosition = result.Position;
                } else {
                    var result = await SpikeFocusRunner.RunAsync(origin,step,offsets,Move,Measure,token);
                    TargetPosition = result.Position;
                }
                AutoFocusMetricText += " | Verified"; RaisePropertyChanged(nameof(AutoFocusMetricText));
                RaisePropertyChanged(nameof(PreviewMetricText));
                verified = true;
                Notification.ShowSuccess(mask ? "Bahtinov focus verified. Remove the mask before imaging." : "Spike-width focus minimum verified.");
                // Keep the verified star visible until Stop is pressed, without more motor moves.
                IsMoving = false;
                while (!token.IsCancellationRequested) {
                    var frame = await ReadFrame();
                    var overlay = mask ? await Task.Run(() => BahtinovAnalyzer.Analyze(frame.Pixels,frame.Width,frame.Height),token) : null;
                    double metric = double.NaN;
                    if (mask && overlay.IsValid) {
                        double alignment = overlay.Lines[1].Nx * reference[1].Nx + overlay.Lines[1].Ny * reference[1].Ny;
                        metric = overlay.SignedErrorPixels * (alignment < 0 ? -1 : 1);
                    } else if (!mask) {
                        var pixels = frame.Pixels.Select(p => (ushort)Math.Clamp(p,0,65535)).ToArray();
                        var result = await Task.Run(() => SpikeCore.Evaluate(pixels,frame.Width,frame.Height,spikeParams,tracking),token);
                        if (result.IsValid && result.HasClearSpikes) metric = result.Metric;
                    }
                    await Show(frame.Pixels,frame.Width,frame.Height,overlay,metric,TargetPosition);
                    SetAssistStatus("Autofocus verified | Live preview · press Stop to finish");
                }
                return 1;
            } catch (OperationCanceledException) {
                AutoFocusMetricText = verified ? "Preview stopped · focus was verified" : "Autofocus stopped · focus was not verified";
                RaisePropertyChanged(nameof(PreviewMetricText));
                SetAssistStatus(AutoFocusMetricText); return verified ? 1 : 0;
            } catch {
                AutoFocusMetricText = verified ? "Preview failed · last autofocus was verified" : "Autofocus failed · focus was not verified";
                RaisePropertyChanged(nameof(PreviewMetricText));
                SetAssistStatus(AutoFocusMetricText); throw;
            }
            finally { try { await CloseStream(); } finally { EndAssist(true); } }
        }
    }
}
