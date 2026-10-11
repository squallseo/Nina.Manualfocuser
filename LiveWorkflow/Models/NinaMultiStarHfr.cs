using System;
using System.Linq;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.Image.ImageData;
using NINA.Profile.Interfaces;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models {
    public readonly record struct FocusStarSample(double X, double Y, double Hfr) {
        public System.Windows.Rect Bounds { get; init; }
    }
    public sealed record FocusHfrMeasurement(double Hfr, double Spread, int Stars) {
        public FocusStarSample[] StarSamples { get; init; } = Array.Empty<FocusStarSample>();
        public int Left { get; init; }
        public int Top { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public long SourceFrameId { get; init; }
        public int Generation { get; init; }
        public static readonly FocusHfrMeasurement Empty = new(double.NaN, double.NaN, 0);
    }
    public interface IFocusHfrAnalyzer {
        Task<FocusHfrMeasurement> AnalyzeAsync(FocusRawFrame frame, CancellationToken token);
        Task<FocusHfrMeasurement> AnalyzeAsync(FocusRawFrame frame, bool detectAllStars, CancellationToken token) => AnalyzeAsync(frame, token);
    }
    [Export(typeof(IFocusHfrAnalyzer))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public sealed class NinaMultiStarHfr : IFocusHfrAnalyzer {
        private readonly IProfileService profiles;
        private readonly IImageDataFactory factory;
        private readonly IPluggableBehaviorSelector<IStarDetection> detectors;
        private readonly ICameraMediator camera;
        private sealed class SilentProgress : IProgress<ApplicationStatus> { public void Report(ApplicationStatus value) { } }
        [ImportingConstructor]
        public NinaMultiStarHfr(IProfileService profiles, IImageDataFactory factory,
            IPluggableBehaviorSelector<IStarDetection> detectors, ICameraMediator camera) {
            this.profiles = profiles; this.factory = factory; this.detectors = detectors; this.camera = camera;
        }
        public Task<FocusHfrMeasurement> AnalyzeAsync(FocusRawFrame frame, CancellationToken token) => AnalyzeAsync(frame, false, token);
        public async Task<FocusHfrMeasurement> AnalyzeAsync(FocusRawFrame frame, bool detectAllStars, CancellationToken token) {
            if (frame == null) return FocusHfrMeasurement.Empty;
            token.ThrowIfCancellationRequested();
            var source = frame.SourceImage;
            bool bayered = source?.Properties.IsBayered == true;
            int left = frame.Left, top = frame.Top, width = frame.Width, height = frame.Height;
            // Preserve the source Bayer phase by extending odd software-crop
            // origins to even coordinates. The source used for sync is untouched.
            if (bayered) { width += left % 2; height += top % 2; left -= left % 2; top -= top % 2; }
            var pixels = new ushort[width * height];
            for (int row = 0; row < height; row++) {
                token.ThrowIfCancellationRequested();
                Array.Copy(frame.Pixels,(top+row)*frame.Stride+left,pixels,row*width,width);
            }
            var profile = profiles.ActiveProfile;
            var settings = profile.ImageSettings;
            var data = factory.CreateBaseImageData(pixels,width,height,source?.Properties.BitDepth ?? 16,bayered,source?.MetaData ?? new ImageMetaData());
            var rendered = data.RenderImage();
            PixelFormat format = PixelFormats.Gray16;
            if (bayered && settings.DebayerImage) {
                var pattern = camera.GetInfo().SensorType;
                if (profile.CameraSettings.BayerPattern != BayerPatternEnum.Auto) pattern = (SensorType)profile.CameraSettings.BayerPattern;
                rendered = rendered.Debayer(saveColorChannels: settings.UnlinkedStretch, saveLumChannel: settings.DebayeredHFR, bayerPattern: pattern);
                format = PixelFormats.Rgb48;
            }
            rendered = await rendered.Stretch(settings.AutoStretchFactor,settings.BlackClipping,
                bayered && settings.DebayerImage && settings.UnlinkedStretch).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var parameters = new StarDetectionParams {
                IsAutoFocus = true, Sensitivity = settings.StarSensitivity, NoiseReduction = settings.NoiseReduction,
                NumberOfAFStars = detectAllStars ? 0 : profile.FocuserSettings.AutoFocusUseBrightestStars
            };
            if (frame.UseAutofocusRoi) {
                var focus = profile.FocuserSettings;
                parameters.UseROI = focus.AutoFocusInnerCropRatio < 1 || focus.AutoFocusOuterCropRatio < 1;
                parameters.InnerCropRatio = focus.AutoFocusInnerCropRatio;
                parameters.OuterCropRatio = focus.AutoFocusOuterCropRatio;
            }
            // The explicit focus ROI is the analysis region; applying the AF crop
            // again here would crop that selection a second time.
            var result = await detectors.GetBehavior().Detect(rendered,format,parameters,new SilentProgress(),token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            int stars = result?.StarList?.Count ?? result?.DetectedStars ?? 0;
            return stars <= 0 || !double.IsFinite(result.AverageHFR) || result.AverageHFR <= 0
                ? FocusHfrMeasurement.Empty : new(result.AverageHFR,result.HFRStdDev,stars) {
                    // Detector positions are in the Bayer-aligned analysis crop.
                    // Convert back to the exact displayed ROI's pixel coordinates.
                    StarSamples = result.StarList?.Where(s => double.IsFinite(s.HFR) && s.HFR > 0)
                        .Select(s => new FocusStarSample(s.Position.X + left - frame.Left,
                            s.Position.Y + top - frame.Top, s.HFR) {
                            Bounds = new System.Windows.Rect(s.BoundingBox.X + left - frame.Left,
                                s.BoundingBox.Y + top - frame.Top, Math.Max(0,s.BoundingBox.Width), Math.Max(0,s.BoundingBox.Height))
                        }).ToArray() ?? Array.Empty<FocusStarSample>(),
                    Left = frame.Left, Top = frame.Top, Width = frame.Width, Height = frame.Height, SourceFrameId = frame.FrameId
                };
        }
    }
}
