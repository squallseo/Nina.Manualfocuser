using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Profile.Interfaces;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models
{
    public partial class LiveFocusModel
    {
        private readonly IImagingMediator imagingMediator;
        private readonly ICameraMediator cameraMediator;
        public sealed record PreviewTiming(double CaptureAndDownloadMs, double HostDownloadMs, double ConversionMs, double CropMs, int SourceWidth, int SourceHeight);
        public PreviewTiming LastPreviewTiming { get; private set; }
        public int LastPreviewBitDepth { get; private set; } = 16;
        public bool LastPreviewIsBayered { get; private set; }
        public FocusRawFrame LastPreviewRawFrame { get; private set; }
        internal FocusRawFrame TakePreviewRawFrame()
        {
            var frame = LastPreviewRawFrame; LastPreviewRawFrame = null; return frame;
        }
        public LiveFocusModel(IProfileService profiles, IImagingMediator imaging, ICameraMediator camera)
        {
            imagingMediator = imaging; cameraMediator = camera;
        }
        private sealed class NullPreviewProgress : IProgress<ApplicationStatus>
        {
            public static readonly NullPreviewProgress Instance = new();
            public void Report(ApplicationStatus value) { }
        }
        public async Task<(double[] Pixels, int Width, int Height, bool HardwareRoi)> CaptureFocusPreviewAsync(
            double seconds, int roiSize, double centerXPercent, double centerYPercent, CancellationToken token, bool overview = false, int? roiHeight = null, bool retainRawPixels = false)
        {
            LastPreviewRawFrame = null;
            var camera = cameraMediator.GetInfo();
            if (camera?.Connected != true) throw new InvalidOperationException("Camera is disconnected.");
            token.ThrowIfCancellationRequested();
            if (!double.IsFinite(seconds) || seconds <= 0 || !double.IsFinite(centerXPercent) || !double.IsFinite(centerYPercent) || roiSize < 32)
                throw new ArgumentException("Invalid preview exposure or ROI.");
            if ((camera.ExposureMin > 0 && seconds < camera.ExposureMin) || (camera.ExposureMax > 0 && seconds > camera.ExposureMax))
                throw new InvalidOperationException($"Preview exposure is outside the camera range ({camera.ExposureMin}–{camera.ExposureMax} seconds).");
            var roi = FocusCameraSupport.FitRoi(camera.DeviceId, camera.XSize, camera.YSize, roiSize, roiHeight ?? roiSize, centerXPercent, centerYPercent);
            int x = roi.X, y = roi.Y, size = roi.Width, sizeY = roi.Height;
            var seq = new CaptureSequence(seconds, CaptureSequence.ImageTypes.SNAPSHOT, null, null, 1)
            {
                Binning = new BinningMode(1, 1),
                EnableSubSample = camera.CanSubSample && !overview
            };
            if (seq.EnableSubSample) seq.SubSambleRectangle = new ObservableRectangle(x, y, size, sizeY);
            // One capture at a time; no speculative retry against a native driver.
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var exposure = await imagingMediator.CaptureImage(seq, token, NullPreviewProgress.Instance);
            double captureMs = timer.Elapsed.TotalMilliseconds;
            double hostDownloadMs = cameraMediator.GetInfo().LastDownloadTime * 1000;
            if (exposure == null) throw new InvalidOperationException("Camera returned no preview image.");
            timer.Restart();
            var image = await exposure.ToImageData(NullPreviewProgress.Instance, token);
            double conversionMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            if (image?.Properties == null || image.Data?.FlatArray == null) throw new InvalidOperationException("Preview image contains no pixels.");
            LastPreviewBitDepth = image.Properties.BitDepth; LastPreviewIsBayered = image.Properties.IsBayered;
            int width = image.Properties.Width, height = image.Properties.Height;
            var raw = image.Data.FlatArray;
            if (width < 1 || height < 1 || (long)width * height != raw.Length) throw new InvalidOperationException("Invalid preview image dimensions.");
            if (overview)
            {
                int stride = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / 1024.0));
                int outWidth = (width + stride - 1) / stride, outHeight = (height + stride - 1) / stride;
                var fullPreview = new double[outWidth * outHeight];
                for (int row = 0; row < outHeight; row++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int col = 0; col < outWidth; col++)
                    {
                        double maximum = 0;
                        for (int sy = row * stride; sy < Math.Min(height, (row + 1) * stride); sy++)
                            for (int sx = col * stride; sx < Math.Min(width, (col + 1) * stride); sx++) maximum = Math.Max(maximum, raw[sy * width + sx]);
                        fullPreview[row * outWidth + col] = maximum;
                    }
                }
                LastPreviewTiming = new(captureMs, hostDownloadMs, conversionMs, timer.Elapsed.TotalMilliseconds, width, height);
                return (fullPreview, outWidth, outHeight, false);
            }
            bool hardwareRoi = seq.EnableSubSample && width <= size && height <= sizeY;
            // Drivers/simulators can return a full frame despite a requested ROI.
            int left = hardwareRoi ? 0 : Math.Clamp(x, 0, Math.Max(0, width - size));
            int top = hardwareRoi ? 0 : Math.Clamp(y, 0, Math.Max(0, height - sizeY));
            int cropWidth = Math.Min(size, width), cropHeight = Math.Min(sizeY, height);
            if (retainRawPixels)
            {
                LastPreviewRawFrame = new FocusRawFrame(raw, width, left, top, cropWidth, cropHeight);
                LastPreviewTiming = new(captureMs, hostDownloadMs, conversionMs, timer.Elapsed.TotalMilliseconds, width, height);
                return (null, cropWidth, cropHeight, hardwareRoi);
            }
            var pixels = new double[cropWidth * cropHeight];
            for (int row = 0; row < cropHeight; row++)
            {
                token.ThrowIfCancellationRequested();
                for (int col = 0; col < cropWidth; col++) pixels[row * cropWidth + col] = raw[(top + row) * width + left + col];
            }
            LastPreviewTiming = new(captureMs, hostDownloadMs, conversionMs, timer.Elapsed.TotalMilliseconds, width, height);
            return (pixels, cropWidth, cropHeight, hardwareRoi);
        }
    }
}
