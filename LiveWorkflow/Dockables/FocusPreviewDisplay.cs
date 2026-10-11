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
        private sealed record PreviewPixels(double[] Pixels, int Width, int Height, BahtinovMeasurement Overlay, double Strength, BitmapSource Bitmap, FocusRawFrame Raw = null, bool Inspector = false);
        private readonly ConditionalWeakTable<ImageSource, PreviewPixels> previewPixels = new();
        private double previewStretchStrength = 1;
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
                var focus = FocusPreviewImage; var overview = overviewImage; var selection = RoiSelectionPreview;
                async Task<ImageSource> Render(ImageSource source)
                {
                    if (source == null || !previewPixels.TryGetValue(source, out var raw)) return source;
                    if (raw.Strength == strength) return source;
                    await stretchRenderGate.WaitAsync(cancellation.Token);
                    try { return await Task.Run(() => raw.Inspector
                        ? RenderAberrationPreview(raw.Raw, strength) : raw.Raw != null
                        ? RenderRawFocusPreview(raw.Raw, raw.Overlay, strength)
                        : RenderFocusPreview(raw.Pixels, raw.Width, raw.Height, raw.Overlay, strength), cancellation.Token); }
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
                UpdateMainRoiEditor();
                if (ReferenceEquals(RoiSelectionPreview, selection)) RoiSelectionPreview = newSelection;
                RaisePropertyChanged(nameof(FocusPreviewImage)); RaisePropertyChanged(nameof(LiveDisplayImage));
                RaisePropertyChanged(nameof(RoiSelectionPreview));
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Logger.Error("Focus preview stretch refresh failed", error); }
            finally { if (ReferenceEquals(stretchRefresh, cancellation)) stretchRefresh = null; }
        }

    }
}
