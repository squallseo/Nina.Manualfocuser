using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using Cwseo.NINA.ManualFocuser.Models;
using NINA.Core.Utility;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        // The frozen image retains its own raw preview for display adjustments.
        // Weak keys release that buffer when a frame is replaced; no image history.
        private sealed record PreviewPixels(double[] Pixels, int Width, int Height, BahtinovMeasurement Overlay, double Strength);
        private readonly ConditionalWeakTable<ImageSource, PreviewPixels> previewPixels = new();
        private double previewStretchStrength = 1;
        private CancellationTokenSource stretchRefresh;
        private readonly SemaphoreSlim stretchRenderGate = new(1, 1);
        public double PreviewStretchStrength {
            get => previewStretchStrength;
            set {
                if (!double.IsFinite(value)) return;
                value = Math.Clamp(value, .25, 2.5);
                if (value == previewStretchStrength) return;
                previewStretchStrength = value;
                RaisePropertyChanged(); RaisePropertyChanged(nameof(PreviewStretchText));
                stretchRefresh?.Cancel();
                _ = RefreshPreviewStretchAsync();
            }
        }
        public string PreviewStretchText => $"{PreviewStretchStrength:F2}×";
        public ICommand ResetPreviewStretchCommand { get; private set; }
        private async Task RefreshPreviewStretchAsync() {
            using var cancellation = new CancellationTokenSource();
            stretchRefresh = cancellation;
            try {
                // Debounce slider drags, including the larger full-frame ROI image.
                await Task.Delay(80, cancellation.Token);
                double strength = PreviewStretchStrength;
                var focus = FocusPreviewImage; var overview = overviewImage; var selection = RoiSelectionPreview;
                async Task<ImageSource> Render(ImageSource source) {
                    if (source == null || !previewPixels.TryGetValue(source, out var raw)) return source;
                    if (raw.Strength == strength) return source;
                    await stretchRenderGate.WaitAsync(cancellation.Token);
                    try { return await Task.Run(() => RenderFocusPreview(raw.Pixels, raw.Width, raw.Height, raw.Overlay, strength), cancellation.Token); }
                    finally { stretchRenderGate.Release(); }
                }
                var newFocus = await Render(focus);
                var newOverview = await Render(overview);
                var newSelection = ReferenceEquals(selection, focus) ? newFocus : await Render(selection);
                cancellation.Token.ThrowIfCancellationRequested();
                if (disposed || PreviewStretchStrength != strength) return;
                // Never replace a newer camera frame or a newly selected ROI.
                if (ReferenceEquals(FocusPreviewImage, focus)) FocusPreviewImage = newFocus;
                if (ReferenceEquals(overviewImage, overview)) overviewImage = newOverview;
                if (ReferenceEquals(RoiSelectionPreview, selection)) RoiSelectionPreview = newSelection;
                RaisePropertyChanged(nameof(FocusPreviewImage)); RaisePropertyChanged(nameof(LiveDisplayImage));
                RaisePropertyChanged(nameof(RoiSelectionPreview));
            } catch (OperationCanceledException) { }
            catch (Exception error) { Logger.Error("Focus preview stretch refresh failed", error); }
            finally { if (ReferenceEquals(stretchRefresh, cancellation)) stretchRefresh = null; }
        }

        private double previewSpikeAngle = double.NaN, previewSpikeStrength;
        private double DisplaySpikeAngle => FocusMode == "Manual" ?
            (DataModel.HasClearSpikes ? DataModel.MeasuredSpikeAngle : double.NaN) : previewSpikeAngle;
        private double DisplaySpikeStrength => FocusMode == "Manual" ? DataModel.MeasuredSpikeAngleStrength : previewSpikeStrength;
        public string MeasuredAngleTooltip => HasSpikeAngle
            ? $"Orientation in the image; directional peak/mean {DisplaySpikeStrength:F2}. The AF measurement axis stays fixed during a scan."
            : "No clear diffraction spike in the latest analyzed frame. Live preview updates the angle about once per second.";
        private void PublishPreviewSpikeAngle(SpikeFrameResult result) {
            bool previouslyAvailable = HasSpikeAngle;
            previewSpikeAngle = result?.HasClearSpikes == true && double.IsFinite(result.MeasuredAngleDeg)
                ? result.MeasuredAngleDeg : double.NaN;
            previewSpikeStrength = result?.AngleStrength ?? 0;
            RaisePropertyChanged(nameof(MeasuredAngleText)); RaisePropertyChanged(nameof(MeasuredAngleTooltip));
            RaisePropertyChanged(nameof(HasSpikeAngle));
            if (previouslyAvailable != HasSpikeAngle) CommandManager.InvalidateRequerySuggested();
        }
        private SpikeFrameResult AnalyzePreviewSpikes(double[] pixels, int width, int height) {
            // Angle detection examines the selected central star, without converting
            // an entire large sensor ROI to another full-size buffer every second.
            var window = FocusRoi.CenterWindow(pixels, width, height, 1024);
            pixels = window.Pixels; width = window.Width; height = window.Height;
            var local = FocusRoi.CenterWindow(pixels, width, height);
            double hfr = QuickFocusMetrics.HalfFluxRadius(local.Pixels, local.Width, local.Height);
            if (!double.IsFinite(hfr)) return null;
            int peak = Array.IndexOf(local.Pixels, local.Pixels.Max());
            int footprint = Math.Clamp((int)Math.Ceiling(hfr * 4), 6, 60);
            var parameters = ManualFocuserModel.BuildSpikeParams();
            parameters.autoSpikeAngle = true; parameters.minimumRoiSizePx = 128;
            parameters.minUsedStarsForValidFrame = 1; parameters.resolveParallelSpikes = true;
            parameters.parallelSpikeCoreRadiusPx = footprint;
            var tracking = SpikeCore.CreateTrackingState(new[] {new SpikeSeedStar {
                X = peak % local.Width + (width - local.Width) / 2,
                Y = peak / local.Width + (height - local.Height) / 2,
                WidthPx = footprint, HeightPx = footprint, MaxBrightness = local.Pixels[peak]
            }}, parameters);
            return SpikeCore.Evaluate(pixels.Select(p => (ushort)Math.Clamp(p, 0, 65535)).ToArray(), width, height, parameters, tracking);
        }
    }
}
