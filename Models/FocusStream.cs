using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Model;

namespace Cwseo.NINA.ManualFocuser.Models {
    public partial class ManualFocuserModel {
        public sealed record StreamPreviewFrame(double[] Pixels, int Width, int Height, bool HardwareRoi, PreviewTiming Timing);
        public bool SupportsFocusStreaming {
            get {
                var camera = cameraMediator.GetInfo();
                if (camera?.Connected != true) return false;
                if (camera.DeviceId?.StartsWith("QHY600M-", StringComparison.OrdinalIgnoreCase) == true) {
                    var modes = camera.ReadoutModes?.ToArray();
                    var mode = modes != null && camera.ReadoutMode >= 0 && camera.ReadoutMode < modes.Length
                        ? modes[camera.ReadoutMode] : null;
                    var normalized = new string((mode ?? "").Replace("*", "x").Replace("×", "x")
                        .Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                    return !normalized.Contains("bin3x3") && !normalized.Contains("3x3bin");
                }
                return camera.CanShowLiveView && camera.DeviceId?.StartsWith("QHY", StringComparison.OrdinalIgnoreCase) != true;
            }
        }
        public LatestFrameStream<StreamPreviewFrame> StartFocusStreaming(double seconds, int roiSize, double centerX, double centerY, CancellationToken token, int? roiHeight = null) {
            token.ThrowIfCancellationRequested();
            var camera = cameraMediator.GetInfo();
            if (!SupportsFocusStreaming) throw new InvalidOperationException("This camera's NINA LiveView path is unavailable. Use single-frame preview.");
            if (!double.IsFinite(seconds) || seconds <= 0 || !double.IsFinite(centerX) || !double.IsFinite(centerY) || roiSize < 32)
                throw new ArgumentException("Invalid streaming exposure or ROI.");
            if ((camera.ExposureMin > 0 && seconds < camera.ExposureMin) || (camera.ExposureMax > 0 && seconds > camera.ExposureMax))
                throw new InvalidOperationException("Streaming exposure is outside the camera range.");
            var roi = FocusRoi.Fit(camera.XSize, camera.YSize, roiSize, roiHeight ?? roiSize, centerX, centerY,
                camera.DeviceId?.StartsWith("QHY600M-", StringComparison.OrdinalIgnoreCase) == true ? 4 : 2);
            int x = roi.X, y = roi.Y, size = roi.Width, sizeY = roi.Height;
            var sequence = new CaptureSequence(seconds, CaptureSequence.ImageTypes.SNAPSHOT, null, null, 1) {
                Binning = new BinningMode(1, 1), EnableSubSample = camera.CanSubSample,
                Gain = camera.Gain, Offset = camera.Offset,
                SubSambleRectangle = new ObservableRectangle(x, y, size, sizeY)
            };
            // CameraInfo is mutable: copy settings before the host reconnects for video mode.
            string id = camera.DeviceId;
            short binX = camera.BinX, binY = camera.BinY, readMode = camera.ReadoutMode;
            var rectangle = camera.IsSubSampleEnabled
                ? new ObservableRectangle(camera.SubSampleX, camera.SubSampleY, camera.SubSampleWidth, camera.SubSampleHeight)
                : new ObservableRectangle(0, 0, camera.XSize, camera.YSize);
            return new LatestFrameStream<StreamPreviewFrame>(ct => Receive(ct), TimeSpan.FromSeconds(Math.Max(15, seconds * 3 + 5)), token);

            async IAsyncEnumerable<StreamPreviewFrame> Receive([EnumeratorCancellation] CancellationToken ct) {
                try {
                    await using var reader = cameraMediator.LiveView(sequence, ct).GetAsyncEnumerator(ct);
                    while (true) {
                        var timer = Stopwatch.StartNew();
                        if (!await reader.MoveNextAsync().ConfigureAwait(false)) yield break;
                        ct.ThrowIfCancellationRequested(); // Some QHY versions create an empty frame on cancellation.
                        double receiveMs = timer.Elapsed.TotalMilliseconds;
                        var exposure = reader.Current;
                        if (exposure == null) throw new InvalidOperationException("Camera returned no stream frame.");
                        timer.Restart();
                        var image = await exposure.ToImageData(NullPreviewProgress.Instance, ct).ConfigureAwait(false);
                        double convertMs = timer.Elapsed.TotalMilliseconds;
                        timer.Restart();
                        if (image?.Properties == null || image.Data?.FlatArray == null) throw new InvalidOperationException("Stream frame contains no pixels.");
                        int width = image.Properties.Width, height = image.Properties.Height;
                        var raw = image.Data.FlatArray;
                        if (width < 1 || height < 1 || (long)width * height != raw.Length) throw new InvalidOperationException("Invalid stream frame dimensions.");
                        bool hardware = sequence.EnableSubSample && width <= size && height <= sizeY;
                        int left = hardware ? 0 : Math.Clamp(x, 0, Math.Max(0, width - size));
                        int top = hardware ? 0 : Math.Clamp(y, 0, Math.Max(0, height - sizeY));
                        int cw = Math.Min(size, width), ch = Math.Min(sizeY, height);
                        var pixels = new double[cw * ch];
                        for (int row = 0; row < ch; row++) {
                            ct.ThrowIfCancellationRequested();
                            for (int col = 0; col < cw; col++) pixels[row * cw + col] = raw[(top + row) * width + left + col];
                        }
                        yield return new StreamPreviewFrame(pixels, cw, ch, hardware, new PreviewTiming(receiveMs, 0, convertMs, timer.Elapsed.TotalMilliseconds, width, height));
                    }
                } finally {
                    // Enumerator disposal calls host StopLiveView first. No parallel SDK connection or forced reflection.
                    var current = cameraMediator.GetInfo();
                    if (current?.Connected == true && current.DeviceId == id) {
                        cameraMediator.SetReadoutMode(readMode);
                        cameraMediator.SetBinning(Math.Max((short)1, binX), Math.Max((short)1, binY));
                        if (current.CanSubSample) cameraMediator.SetSubSambleRectangle(rectangle);
                    }
                }
            }
        }
    }
}
