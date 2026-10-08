using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Model;
using NINA.Equipment.Interfaces;

namespace Cwseo.NINA.ManualFocuser.Models {
    public partial class ManualFocuserModel {
        public sealed record StreamPreviewFrame(double[] Pixels, int Width, int Height, bool HardwareRoi, PreviewTiming Timing,
            long AcquisitionStarted = 0, long AvailableAt = 0, int BitDepth = 16, bool? IsBayered = null,
            int? FocuserPosition = null, double ExposureSeconds = double.NaN);
        public bool SupportsFocusStreaming => FocusCameraSupport.SupportsStreaming(cameraMediator.GetInfo());
        public LatestFrameStream<StreamPreviewFrame> StartFocusStreaming(double seconds, int roiSize, double centerX, double centerY, CancellationToken token, int? roiHeight = null) {
            token.ThrowIfCancellationRequested();
            var camera = cameraMediator.GetInfo();
            if (!SupportsFocusStreaming) throw new InvalidOperationException("This camera's NINA LiveView path is unavailable. Use single-frame preview.");
            if (!double.IsFinite(seconds) || seconds <= 0 || !double.IsFinite(centerX) || !double.IsFinite(centerY) || roiSize < 32)
                throw new ArgumentException("Invalid streaming exposure or ROI.");
            if ((camera.ExposureMin > 0 && seconds < camera.ExposureMin) || (camera.ExposureMax > 0 && seconds > camera.ExposureMax))
                throw new InvalidOperationException("Streaming exposure is outside the camera range.");
            var roi = FocusCameraSupport.FitRoi(camera.DeviceId,camera.XSize,camera.YSize,roiSize,roiHeight ?? roiSize,centerX,centerY);
            int x = roi.X, y = roi.Y, size = roi.Width, sizeY = roi.Height;
            var sequence = new CaptureSequence(seconds, CaptureSequence.ImageTypes.SNAPSHOT, null, null, 1) {
                Binning = new BinningMode(1, 1), EnableSubSample = camera.CanSubSample,
                Gain = camera.Gain, Offset = camera.Offset,
                SubSambleRectangle = new ObservableRectangle(x, y, size, sizeY)
            };
            // CameraInfo is mutable: copy settings before the host reconnects for video mode.
            string id = camera.DeviceId;
            bool toupTek = FocusCameraSupport.IsNativeToupTek(id), asi=FocusCameraSupport.IsNativeAsi(id);
            var nativeCamera = toupTek || asi ? cameraMediator.GetDevice() as ICamera : null;
            if ((toupTek || asi) && (nativeCamera == null || !nativeCamera.Connected || nativeCamera.Id != id))
                throw new InvalidOperationException("The selected native camera is unavailable or changed.");
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
                        long acquisitionStarted = Stopwatch.GetTimestamp();
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
                        yield return new StreamPreviewFrame(pixels, cw, ch, hardware, new PreviewTiming(receiveMs, 0, convertMs, timer.Elapsed.TotalMilliseconds, width, height),
                            acquisitionStarted, Stopwatch.GetTimestamp(),image.Properties.BitDepth,image.Properties.IsBayered);
                    }
                } finally {
                    // Drain the host's in-flight download, then dispose its enumerator
                    // (StopLiveView). ASI's blocking GetVideoData does not observe ct;
                    // do not restore settings or start a second capture while it runs.
                    // No parallel SDK connection or forced reflection.
                    // ToupTek schedules mode restoration asynchronously; wait on the public
                    // device flag rather than the host's periodically refreshed CameraInfo.
                    if (toupTek) await WaitForFocusStreamStopAsync(nativeCamera, TimeSpan.FromSeconds(Math.Max(15, seconds * 3 + 5))).ConfigureAwait(false);
                    var current = cameraMediator.GetInfo();
                    if (current?.Connected == true && current.DeviceId == id) {
                        cameraMediator.SetReadoutMode(readMode);
                        cameraMediator.SetBinning(Math.Max((short)1, binX), Math.Max((short)1, binY));
                        if (current.CanSubSample) cameraMediator.SetSubSambleRectangle(rectangle);
                    }
                }
            }
        }
        public static async Task WaitForFocusStreamStopAsync(ICamera camera, TimeSpan timeout) {
            var timer = Stopwatch.StartNew();
            while (camera.Connected && camera.LiveViewEnabled) {
                if (timer.Elapsed >= timeout)
                    throw new TimeoutException("ToupTek video mode did not stop. Reconnect the camera before capturing again.");
                // Cleanup must finish even after the caller presses Stop.
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
    }
}
