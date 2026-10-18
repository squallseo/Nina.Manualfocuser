using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Core.Utility;

namespace Cwseo.NINA.ManualFocuser.Dockables
{
    public partial class ManualFocuserDockableVM
    {
        // The frozen image retains its own raw preview for display adjustments.
        // Weak keys release that buffer when a frame is replaced; no image history.
        public sealed record InspectorTile(int X, int Y, BitmapSource Bitmap);
        private sealed record PreviewPixels(double[] Pixels, int Width, int Height, BahtinovMeasurement Overlay, double Strength, BitmapSource Bitmap, FocusRawFrame Raw = null, bool Inspector = false, bool AutoStretch = true, InspectorTile[] Tiles = null) {
            public FocusHfrMeasurement StarAnnotation { get; set; }
            public FocusStretchLevels Levels { get; init; } = FocusStretchLevels.Default;
            public FocusHistogram Histogram { get; set; }
        }
        private readonly ConditionalWeakTable<ImageSource, PreviewPixels> previewPixels = new();
        private double previewStretchStrength = 1;
        private bool previewAutoStretch = true;
        private double? previewBlackClipping;
        private double previewWhitePoint = 100;
        private FocusStretchLevels CurrentPreviewLevels => new FocusStretchLevels(PreviewBlackClipping, PreviewWhitePoint / 100,
            profileService.ActiveProfile?.ImageSettings?.AutoStretchFactor is > 0 and var factor ? factor : .2) {
                InputBlack = previewInputBlack, InputMidtone = previewInputMidtone, InputWhite = previewInputWhite
            }.Sanitize();
        private double previewInputBlack, previewInputMidtone = .5, previewInputWhite = 1;
        public double PreviewInputBlack {
            get => previewInputBlack;
            set {
                if (!double.IsFinite(value)) return;
                value = Math.Clamp(value, 0, previewInputWhite - 1d / 65535);
                if (value == previewInputBlack) return;
                previewInputBlack = value; RaisePropertyChanged(); RequestStretchRefresh();
            }
        }
        public double PreviewInputMidtone {
            get => previewInputMidtone;
            set {
                if (!double.IsFinite(value)) return;
                value = Math.Clamp(value, .001, .999);
                if (value == previewInputMidtone) return;
                previewInputMidtone = value; RaisePropertyChanged(); RequestStretchRefresh();
            }
        }
        public double PreviewInputWhite {
            get => previewInputWhite;
            set {
                if (!double.IsFinite(value)) return;
                value = Math.Clamp(value, previewInputBlack + 1d / 65535, 1);
                if (value == previewInputWhite) return;
                previewInputWhite = value; RaisePropertyChanged(); RequestStretchRefresh();
            }
        }
        public double PreviewBlackClipping {
            get => previewBlackClipping ?? Math.Clamp(profileService.ActiveProfile?.ImageSettings?.BlackClipping ?? -2.8, -10, 0);
            set {
                if (!double.IsFinite(value)) return;
                value = Math.Clamp(value, -10, 0);
                if (value == PreviewBlackClipping) return;
                previewBlackClipping = value;
                RaisePropertyChanged(); RaisePropertyChanged(nameof(PreviewBlackText)); RequestStretchRefresh();
            }
        }
        public double PreviewWhitePoint {
            get => previewWhitePoint;
            set {
                if (!double.IsFinite(value)) return;
                value = Math.Clamp(value, 1, 100);
                if (value == previewWhitePoint) return;
                previewWhitePoint = value;
                RaisePropertyChanged(); RaisePropertyChanged(nameof(PreviewWhiteText)); RequestStretchRefresh();
            }
        }
        public string PreviewBlackText => $"{PreviewBlackClipping:F1} σ";
        public string PreviewWhiteText => $"{PreviewWhitePoint:F0}%";
        private void RequestStretchRefresh() { stretchRefresh?.Cancel(); _ = RefreshPreviewStretchAsync(); }
        private void ResetPreviewLevels() {
            previewBlackClipping = null; previewWhitePoint = 100; previewStretchStrength = 1; previewAutoStretch = true;
            previewInputBlack = 0; previewInputMidtone = .5; previewInputWhite = 1;
            foreach (string name in new[] { nameof(PreviewInputBlack), nameof(PreviewInputMidtone), nameof(PreviewInputWhite) }) RaisePropertyChanged(name);
            foreach (string name in new[] { nameof(PreviewBlackClipping), nameof(PreviewBlackText), nameof(PreviewWhitePoint), nameof(PreviewWhiteText), nameof(PreviewStretchStrength), nameof(PreviewStretchText) }) RaisePropertyChanged(name);
            RaisePropertyChanged(nameof(PreviewAutoStretch));
            RequestStretchRefresh();
        }
        public bool PreviewAutoStretch {
            get => previewAutoStretch;
            set { if (previewAutoStretch==value) return; previewAutoStretch=value; RaisePropertyChanged(); stretchRefresh?.Cancel(); _ = RefreshPreviewStretchAsync(); }
        }
        private CancellationTokenSource stretchRefresh;
        private readonly SemaphoreSlim stretchRenderGate = new(1, 1);
        public double PreviewStretchStrength
        {
            get => previewStretchStrength;
            set
            {
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
        public Task<FocusHistogramPlot> GetPreviewHistogramAsync(CancellationToken token) {
            var image = LiveDisplayImage;
            if (image == null || !previewPixels.TryGetValue(image, out var entry)) return Task.FromResult<FocusHistogramPlot>(null);
            return Task.Run(() => {
                token.ThrowIfCancellationRequested();
                var histogram = entry.Histogram;
                if (histogram == null) {
                    var frame = entry.Raw;
                    if (frame != null && entry.Inspector) {
                        var source = frame.SourceImage;
                        var inspection = source?.Properties != null && source.Data?.FlatArray != null
                            ? new FocusRawFrame(source.Data.FlatArray, source.Properties.Width, 0, 0, source.Properties.Width, source.Properties.Height, source) : frame;
                        frame = FocusAberrationMosaic.Extract(inspection, FocusAberrationMosaic.Plan(inspection.Width, inspection.Height), token);
                    }
                    histogram = frame != null ? FocusDisplayStretch.GetHistogram(frame) : FocusDisplayStretch.GetHistogram(entry.Pixels);
                    entry.Histogram = histogram;
                }
                token.ThrowIfCancellationRequested();
                // Use the settings attached to these displayed pixels, even if
                // another slider drag or camera frame is currently in flight.
                return FocusDisplayStretch.PlotHistogram(histogram, entry.Strength, entry.AutoStretch, entry.Levels);
            }, token);
        }
        private async Task RefreshPreviewStretchAsync()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (disposed || dispatcher?.HasShutdownStarted == true) return;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(RefreshPreviewStretchAsync).Task.Unwrap();
                return;
            }
            using var cancellation = new CancellationTokenSource();
            stretchRefresh = cancellation;
            try
            {
                // Debounce slider drags, including the larger full-frame ROI image.
                await Task.Delay(80, cancellation.Token);
                double strength = PreviewStretchStrength;
                bool autoStretch = PreviewAutoStretch;
                var levels = CurrentPreviewLevels;
                var focus = FocusPreviewImage; var overview = overviewImage; var selection = RoiSelectionPreview;
                async Task<ImageSource> Render(ImageSource source)
                {
                    if (source == null || !previewPixels.TryGetValue(source, out var raw)) return source;
                    bool inspector = ReferenceEquals(source,focus) && IsAberrationInspector && !IsSelectingRoi;
                    if (raw.Strength == strength && raw.AutoStretch == autoStretch && raw.Inspector == inspector && raw.Levels == levels) return source;
                    await stretchRenderGate.WaitAsync(cancellation.Token);
                    try { return await Task.Run(() => inspector && raw.Raw != null
                        ? RenderAberrationPreview(raw.Raw, strength) : raw.Raw != null
                        ? RenderRawFocusPreview(raw.Raw, raw.Overlay, strength)
                        : RenderFocusPreview(raw.Pixels, raw.Width, raw.Height, raw.Overlay, strength), cancellation.Token); }
                    finally { stretchRenderGate.Release(); }
                }
                var newFocus = await Render(focus);
                var newOverview = await Render(overview);
                var newSelection = ReferenceEquals(selection, focus) ? newFocus : await Render(selection);
                cancellation.Token.ThrowIfCancellationRequested();
                if (disposed || PreviewStretchStrength != strength || PreviewAutoStretch != autoStretch || CurrentPreviewLevels != levels) return;
                // Never replace a newer camera frame or a newly selected ROI.
                if (ReferenceEquals(FocusPreviewImage, focus)) FocusPreviewImage = newFocus;
                if (ReferenceEquals(overviewImage, overview)) overviewImage = newOverview;
                UpdateMainRoiEditor();
                if (ReferenceEquals(RoiSelectionPreview, selection)) RoiSelectionPreview = newSelection;
                RaisePropertyChanged(nameof(FocusPreviewImage)); RaisePropertyChanged(nameof(LiveDisplayImage));
                RaisePropertyChanged(nameof(LiveDisplayBitmap)); RaisePropertyChanged(nameof(LiveDisplayOverlay));
                RaisePropertyChanged(nameof(LiveDisplayInspectorTiles)); RaisePropertyChanged(nameof(HasDisplayInspectorTiles));
                RaisePropertyChanged(nameof(RoiSelectionPreview));
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Logger.Error("Focus preview stretch refresh failed", error); }
            finally { if (ReferenceEquals(stretchRefresh, cancellation)) stretchRefresh = null; }
        }

    }
}
