using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;

namespace Cwseo.NINA.ManualFocuser.Dockables
{
    public partial class ManualFocuserDockableVM
    {
        private bool showInNinaImage = true;
        private bool liveImageOutputActive;
        private int imageOutputQueued;
        private long lastImageOutput;
        public bool ShowInNinaImage
        {
            get => showInNinaImage;
            set
            {
                if (showInNinaImage == value) return;
                showInNinaImage = value;
                lastImageOutput = 0;
                RaisePropertyChanged();
                UpdateMainRoiEditor();
                QueueMainImageOutput();
            }
        }

        private bool CanOutputLiveImage => ShowInNinaImage && !disposed && assistRunning && liveImageOutputActive &&
            !IsSelectingRoi && !isStoppingFocusPreview && assistCts?.IsCancellationRequested != true && FocusPreviewImage != null;

        private void QueueMainImageOutput()
        {
            if (!CanOutputLiveImage) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) OutputCurrentLiveImage();
            else if (Interlocked.CompareExchange(ref imageOutputQueued, 1, 0) == 0)
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    Interlocked.Exchange(ref imageOutputQueued, 0);
                    // Read the latest preview, never a captured older frame. Recheck
                    // output/Stop/disposal before touching the host's shared viewer.
                    OutputCurrentLiveImage();
                }));
        }

        private void OutputCurrentLiveImage()
        {
            if (!CanOutputLiveImage || !previewPixels.TryGetValue(FocusPreviewImage, out var raw)) return;
            long now = Stopwatch.GetTimestamp();
            // Updating NINA's larger viewer must not flood the UI during fast video.
            if (lastImageOutput != 0 && Stopwatch.GetElapsedTime(lastImageOutput, now).TotalMilliseconds < 100) return;
            try
            {
                BitmapSource image = raw.Bitmap;
                if (raw.Overlay?.IsValid == true)
                {
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen())
                        drawing.DrawImage(FocusPreviewImage, new Rect(0, 0, raw.Width, raw.Height));
                    var rendered = new RenderTargetBitmap(raw.Width, raw.Height, 96, 96, PixelFormats.Pbgra32);
                    rendered.Render(visual);
                    rendered.Freeze();
                    image = rendered;
                }
                // Display only: no PrepareImage event, history, save, capture or
                // star-analysis work, and no change to the cached full-frame ROI source.
                imagingMediator.SetImage(image);
                lastImageOutput = now;
            }
            catch (Exception error)
            {
                ShowInNinaImage = false;
                Logger.Error("Live Focus image output failed", error);
                Notification.ShowWarning("Live Focus: NINA Image output disabled. The local live preview remains available.");
            }
        }
    }
}
