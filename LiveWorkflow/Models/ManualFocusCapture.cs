using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Model;
using NINA.Profile.Interfaces;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models {
    public partial class LiveFocusModel {
        private readonly IProfileService profiles;
        public CaptureSequence CreateManualFocusSequence(FilterInfo filter) {
            var settings = profiles.ActiveProfile.FocuserSettings;
            double exposure = filter?.AutoFocusExposureTime > -1 ? filter.AutoFocusExposureTime : settings.AutoFocusExposureTime;
            short binning = settings.AutoFocusBinning;
            return new CaptureSequence(exposure, CaptureSequence.ImageTypes.SNAPSHOT, filter, null, 1) {
                Binning = filter?.AutoFocusBinning ?? new BinningMode(binning, binning),
                EnableSubSample = false,
                Gain = filter?.AutoFocusGain > -1 ? filter.AutoFocusGain : -1,
                Offset = filter?.AutoFocusOffset > -1 ? filter.AutoFocusOffset : -1
            };
        }
        public async Task<FocusRawFrame> CaptureManualFocusAsync(CaptureSequence sequence, CancellationToken token) {
            LastPreviewRawFrame = null;
            token.ThrowIfCancellationRequested();
            var camera = cameraMediator.GetInfo();
            if (camera?.Connected != true) throw new InvalidOperationException("Camera is disconnected.");
            double seconds = sequence.ExposureTime;
            if (!double.IsFinite(seconds) || seconds <= 0 || sequence.Binning.X < 1 || sequence.Binning.Y < 1 ||
                (camera.ExposureMin > 0 && seconds < camera.ExposureMin) || (camera.ExposureMax > 0 && seconds > camera.ExposureMax))
                throw new InvalidOperationException("NINA autofocus exposure or binning is outside the camera range.");
            var timer = Stopwatch.StartNew();
            var exposure = await imagingMediator.CaptureImage(sequence, token, NullPreviewProgress.Instance);
            double captureMs = timer.Elapsed.TotalMilliseconds;
            if (exposure == null) throw new InvalidOperationException("Camera returned no focus image.");
            timer.Restart();
            var image = await exposure.ToImageData(NullPreviewProgress.Instance, token);
            token.ThrowIfCancellationRequested();
            if (image?.Properties == null || image.Data?.FlatArray == null) throw new InvalidOperationException("Focus image contains no pixels.");
            int width = image.Properties.Width, height = image.Properties.Height;
            LastPreviewBitDepth = image.Properties.BitDepth; LastPreviewIsBayered = image.Properties.IsBayered;
            LastPreviewTiming = new(captureMs, cameraMediator.GetInfo().LastDownloadTime * 1000, timer.Elapsed.TotalMilliseconds, 0, width, height);
            // Retain the complete source for display/sync; the detector applies
            // NINA's configured autofocus crop without cropping the source twice.
            return new FocusRawFrame(image.Data.FlatArray, width, 0, 0, width, height, image, useAutofocusRoi: true);
        }
    }
}
