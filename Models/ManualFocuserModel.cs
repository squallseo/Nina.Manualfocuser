using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel.AutoFocus;
using OxyPlot;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cwseo.NINA.ManualFocuser.Models {
    public partial class ManualFocuserModel {
        public sealed record PreviewTiming(double CaptureAndDownloadMs, double HostDownloadMs, double ConversionMs, double CropMs, int SourceWidth, int SourceHeight);
        public PreviewTiming LastPreviewTiming { get; private set; }
        public int LastPreviewBitDepth { get; private set; } = 16;
        public bool LastPreviewIsBayered { get; private set; }
        private readonly IProfileService profileService;
        private readonly IImagingMediator imagingMediator;
        private readonly ICameraMediator cameraMediator;
        private readonly IPluggableBehaviorSelector<IStarDetection> starDetectionSelector;
        private readonly IPluggableBehaviorSelector<IStarAnnotator> starAnnotatorSelector;

        private SpikeTrackingState trackingState = null;
        private SpikeMetricKind? plottedSpikeMetric;

        public double HFRDelta { get; set; }
        public double StepDelta { get; set; }
        public double MinStep { get; set; }
        public double MinHFR { get; set; }
        public double MaxStep { get; set; }
        public double MaxHFR { get; set; }
        public double XPoly { get; set; }
        public double YPoly { get; set; }
        public bool MaxPolyTrue { get; set; }
        public int NumInitialSteps {
            get {
                return profileService.ActiveProfile.FocuserSettings.AutoFocusInitialOffsetSteps;
            }
        }
        public int AFStepSize {
            get {
                return profileService.ActiveProfile.FocuserSettings.AutoFocusStepSize;
            }
        }

        // Per-pass collections (primary = coarse/pass 0, secondary = fine/pass 1)
        public int CurrentPass { get; set; } = 0;
        public AsyncObservableCollection<ScatterErrorPoint> ManualFocusPointsPrimary { get; } = new AsyncObservableCollection<ScatterErrorPoint>();
        public AsyncObservableCollection<ScatterErrorPoint> ManualFocusPointsSecondary { get; } = new AsyncObservableCollection<ScatterErrorPoint>();
        public AsyncObservableCollection<DataPoint> PlotFocusPointsPrimary { get; } = new AsyncObservableCollection<DataPoint>();
        public AsyncObservableCollection<DataPoint> PlotFocusPointsSecondary { get; } = new AsyncObservableCollection<DataPoint>();
        public AsyncObservableCollection<DataPoint> FitCurvePointsPrimary { get; } = new AsyncObservableCollection<DataPoint>();
        public AsyncObservableCollection<DataPoint> FitCurvePointsSecondary { get; } = new AsyncObservableCollection<DataPoint>();



        // Spike minimum, tracked separately - J is a different quantity from HFR
        // and its minimum is the thing an autofocus run would eventually search for.
        public double MinSpikeStep { get; set; }
        public double MinSpike { get; set; }

        /// <summary>Spike orientation measured on the most recent frame, NaN if unknown.</summary>
        public double MeasuredSpikeAngle { get; private set; } = double.NaN;
        public double MeasuredSpikeAngleStrength { get; private set; }
        public bool SpikeAngleIsAuto { get; private set; }
        public bool HasClearSpikes { get; private set; }
        public string SpikeDisplayStatus { get; private set; } = "HFR · awaiting measurement";

        public AsyncObservableCollection<ScatterErrorPoint> HFRFocusPoints { get; } = new AsyncObservableCollection<ScatterErrorPoint>();
        public AsyncObservableCollection<ScatterErrorPoint> SpikeFocusPoints { get; } = new AsyncObservableCollection<ScatterErrorPoint>();
        public AsyncObservableCollection<DataPoint> PlotFocusPoints { get; } = new AsyncObservableCollection<DataPoint>();
        public AsyncObservableCollection<DataPoint> ArrowPoint { get; } = new AsyncObservableCollection<DataPoint>();

        public ManualFocuserModel(IProfileService profileService,
            IImagingMediator imagingMediator,
            ICameraMediator cameraMediator,
            IPluggableBehaviorSelector<IStarDetection> starDetectionSelector,
            IPluggableBehaviorSelector<IStarAnnotator> starAnnotatorSelector) {
            this.profileService = profileService;
            this.imagingMediator = imagingMediator;
            this.cameraMediator = cameraMediator;
            this.starDetectionSelector = starDetectionSelector;
            this.starAnnotatorSelector = starAnnotatorSelector;
            ResetPlotData();
        }

        public int GetFocusPointSize() {
            return HFRFocusPoints.Count;
        }

        public void AddHFRPoint(int position, MeasureAndError measurement) {
            // Failed detections are not measurements. In particular, never send a
            // double.MaxValue sentinel to the plot's best-point annotation/axes.
            if (!double.IsFinite(measurement.Measure) || measurement.Measure <= 0 || measurement.Measure == double.MaxValue ||
                !double.IsFinite(measurement.Stdev)) {
                Logger.Warning($"[ManualFocuser] Skipping invalid HFR at position {position}");
                return;
            }
            var idx = HFRFocusPoints.Count();

            var step = Convert.ToDouble(position);
            var hfr = measurement.Measure;
            var errorY = Math.Max(0.001, measurement.Stdev);

            if (idx > 0) {
                var lastpoint = HFRFocusPoints[idx - 1];
                StepDelta = step - lastpoint.X;
                HFRDelta = hfr - lastpoint.Y;
                if (hfr < MinHFR && hfr > 0.0) {
                    MinStep = step;
                    MinHFR = hfr;
                }
                if (hfr > MaxHFR) {
                    MaxStep = step;
                    MaxHFR = hfr;
                }
            } else {
                MinStep = position;
                MinHFR = hfr;
                MaxHFR = hfr;
                MaxStep = position;
            }

            var scatter = new ScatterErrorPoint(position, hfr, 0, errorY);
            HFRFocusPoints.Add(scatter);
            PlotFocusPoints.Add(new DataPoint(position, hfr));

            // populate per-pass collections automatically
            if (CurrentPass == 0) {
                ManualFocusPointsPrimary.Add(scatter);
                PlotFocusPointsPrimary.Add(new DataPoint(position, hfr));
            } else {
                ManualFocusPointsSecondary.Add(scatter);
                PlotFocusPointsSecondary.Add(new DataPoint(position, hfr));
            }

            if(idx > 0) {
                ArrowPoint[0] = PlotFocusPoints[idx - 1];
                ArrowPoint[1] = PlotFocusPoints[idx];
            }

            bool mpolytrue = false;
            double xpoly = 0.0;
            double ypoly = 0.0;

            // Automatically generate/update the fitted curve for the active pass
            bool v = GenerateFitCurveForCurrentPass(out mpolytrue, out xpoly, out ypoly);
            MaxPolyTrue = mpolytrue;
            XPoly = xpoly;
            YPoly = ypoly;
        }
        /// <summary>
        /// Adds a spike point. Invalid frames carry NaN and are dropped rather than
        /// plotted - a sentinel value on the axis wrecks the scale and hides the curve.
        /// </summary>
        public void AddSpikePoint(int position, MeasureAndError measurement) {
            if (double.IsNaN(measurement.Measure) || double.IsInfinity(measurement.Measure)) {
                if (Properties.Settings.Default.EnableFocusDiagnostics) Logger.Debug($"[ManualFocuser] Skipping invalid spike point at position {position}");
                return;
            }

            var step = Convert.ToDouble(position);
            var stdev = double.IsNaN(measurement.Stdev) ? 0 : measurement.Stdev;

            if (SpikeFocusPoints.Count == 0 || measurement.Measure < MinSpike) {
                MinSpikeStep = step;
                MinSpike = measurement.Measure;
            }

            SpikeFocusPoints.Add(new ScatterErrorPoint(step, measurement.Measure, 0, stdev));
        }

        public void ResetPlotData() {
            MaxStep = 0;
            MaxHFR = 0;
            CurrentPass = 0;
            ManualFocusPointsPrimary.Clear();
            ManualFocusPointsSecondary.Clear();
            PlotFocusPointsPrimary.Clear();
            PlotFocusPointsSecondary.Clear();
            FitCurvePointsPrimary.Clear();
            FitCurvePointsSecondary.Clear();
            HFRFocusPoints.Clear();
            PlotFocusPoints.Clear();
            SpikeFocusPoints.Clear();
            HFRDelta = 0.0;
            StepDelta = 0.0;
            MinStep = 0.0;
            MinHFR = 0.0;
            MinSpikeStep = 0.0;
            MinSpike = 0.0;
            MeasuredSpikeAngle = double.NaN;
            MeasuredSpikeAngleStrength = 0.0;
            HasClearSpikes = false;
            SpikeDisplayStatus = "HFR · awaiting measurement";

            // Star tracking must restart with the plot, otherwise a new run keeps
            // chasing stars seeded in the previous one.
            trackingState = null;
            plottedSpikeMetric = null;

            ArrowPoint.Clear();
            ArrowPoint.Add(new DataPoint(0, 0));
            ArrowPoint.Add(new DataPoint(0, 0));
        }

        /// <summary>
        /// Captures and evaluates <paramref name="exposuresPerFocusPoint"/> frames.
        /// Evaluation is sequential on purpose: the spike tracking state is shared
        /// mutable state, and overlapping evaluations corrupt it.
        /// </summary>
        public async Task<(MeasureAndError hfr, MeasureAndError spike)> GetAverageMeasurement(
            FilterInfo filter,
            int exposuresPerFocusPoint,
            int focuserPosition,
            CancellationToken token,
            IProgress<ApplicationStatus> progress, FocusCaptureSettings focusCapture = null) {

            int frames = Math.Max(1, exposuresPerFocusPoint);
            var measures = new List<(MeasureAndError hfr, MeasureAndError spike)>(frames);
            var spikeParams = BuildSpikeParams();
            bool spikeEnabled = Properties.Settings.Default.EnableSpikeMetric;
            HasClearSpikes = false;
            SpikeDisplayStatus = spikeEnabled ? "Spike detection insufficient · using HFR" : "HFR";
            if (plottedSpikeMetric != spikeParams.metricKind) {
                // Different metrics have different units; do not combine them
                // into one curve when the user changes the selection.
                SpikeFocusPoints.Clear();
                MinSpike = MinSpikeStep = 0;
                plottedSpikeMetric = spikeParams.metricKind;
            }

            for (int i = 0; i < frames; i++) {
                token.ThrowIfCancellationRequested();

                var image = await TakeExposure(filter, token, progress, focusCapture);
                if (image == null) {
                    Logger.Warning("[ManualFocuser] Exposure returned no image - skipping frame");
                    continue;
                }

                measures.Add(await EvaluateExposure(image, focuserPosition, spikeParams, spikeEnabled, token, progress, focusCapture));
            }

            if (measures.Count == 0) {
                SpikeFocusPoints.Clear();
                MinSpike = MinSpikeStep = 0;
                return (new MeasureAndError { Measure = 0, Stdev = 1000 },
                        new MeasureAndError { Measure = double.NaN, Stdev = 0 });
            }

            double sumMeasure = 0;
            double sumVariances = 0;
            var spikeValues = new List<double>();
            var spikeVariances = new List<double>();

            foreach (var (hfr, spike) in measures) {
                sumMeasure += hfr.Measure;
                sumVariances += hfr.Stdev * hfr.Stdev;

                if (!double.IsNaN(spike.Measure)) {
                    spikeValues.Add(spike.Measure);
                    spikeVariances.Add(spike.Stdev * spike.Stdev);
                }
            }

            var hfrResult = new MeasureAndError {
                Measure = sumMeasure / measures.Count,
                Stdev = Math.Sqrt(sumVariances / measures.Count)
            };

            HasClearSpikes = spikeEnabled && spikeValues.Count == measures.Count;
            if (!HasClearSpikes) {
                SpikeFocusPoints.Clear();
                MinSpike = MinSpikeStep = 0;
            }
            SpikeDisplayStatus = HasClearSpikes ? "HFR + detected spikes" : spikeEnabled ? "Spike detection insufficient · using HFR" : "HFR";
            var spikeResult = !HasClearSpikes
                ? new MeasureAndError { Measure = double.NaN, Stdev = 0 }
                : new MeasureAndError {
                    Measure = SpikeCore.Median(spikeValues),
                    // Combine the per-frame star scatter with the frame-to-frame scatter
                    // so the error bar means something when multiple frames are averaged.
                    Stdev = Math.Sqrt(spikeVariances.Average() + Math.Pow(SpikeCore.StdDevSample(spikeValues), 2))
                };

            return (hfrResult, spikeResult);
        }

        public async Task<(double[] Pixels, int Width, int Height, bool HardwareRoi)> CaptureFocusPreviewAsync(
            double seconds, int roiSize, double centerXPercent, double centerYPercent, CancellationToken token, bool overview = false, int? roiHeight = null) {
            var camera = cameraMediator.GetInfo();
            if (camera?.Connected != true) throw new InvalidOperationException("Camera is disconnected.");
            token.ThrowIfCancellationRequested();
            if (!double.IsFinite(seconds) || seconds <= 0 || !double.IsFinite(centerXPercent) || !double.IsFinite(centerYPercent) || roiSize < 32)
                throw new ArgumentException("Invalid preview exposure or ROI.");
            if ((camera.ExposureMin > 0 && seconds < camera.ExposureMin) || (camera.ExposureMax > 0 && seconds > camera.ExposureMax))
                throw new InvalidOperationException($"Preview exposure is outside the camera range ({camera.ExposureMin}–{camera.ExposureMax} seconds).");
            var roi = FocusCameraSupport.FitRoi(camera.DeviceId,camera.XSize,camera.YSize,roiSize,roiHeight ?? roiSize,centerXPercent,centerYPercent);
            int x = roi.X, y = roi.Y, size = roi.Width, sizeY = roi.Height;
            var seq = new CaptureSequence(seconds, CaptureSequence.ImageTypes.SNAPSHOT, null, null, 1) {
                Binning = new BinningMode(1, 1), EnableSubSample = camera.CanSubSample && !overview
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
            LastPreviewBitDepth=image.Properties.BitDepth;LastPreviewIsBayered=image.Properties.IsBayered;
            int width = image.Properties.Width, height = image.Properties.Height;
            var raw = image.Data.FlatArray;
            if (width < 1 || height < 1 || (long)width * height != raw.Length) throw new InvalidOperationException("Invalid preview image dimensions.");
            if (overview) {
                int stride = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / 1024.0));
                int outWidth = (width + stride - 1) / stride, outHeight = (height + stride - 1) / stride;
                var fullPreview = new double[outWidth * outHeight];
                for (int row = 0; row < outHeight; row++) {
                    token.ThrowIfCancellationRequested();
                    for (int col = 0; col < outWidth; col++) {
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
            var pixels = new double[cropWidth * cropHeight];
            for (int row = 0; row < cropHeight; row++) {
                token.ThrowIfCancellationRequested();
                for (int col = 0; col < cropWidth; col++) pixels[row * cropWidth + col] = raw[(top + row) * width + left + col];
            }
            LastPreviewTiming = new(captureMs, hostDownloadMs, conversionMs, timer.Elapsed.TotalMilliseconds, width, height);
            return (pixels, cropWidth, cropHeight, hardwareRoi);
        }

        private sealed class NullPreviewProgress : IProgress<ApplicationStatus> {
            public static readonly NullPreviewProgress Instance = new();
            public void Report(ApplicationStatus value) { }
        }

        private async Task<IExposureData> TakeExposure(FilterInfo filter, CancellationToken token, IProgress<ApplicationStatus> progress, FocusCaptureSettings focusCapture = null) {
                if (focusCapture != null) {
                    var camera = cameraMediator.GetInfo();
                    if (camera?.Connected != true || camera.XSize != focusCapture.SensorWidth || camera.YSize != focusCapture.SensorHeight)
                        throw new InvalidOperationException("Camera changed during focus capture.");
                    var seq = new CaptureSequence(focusCapture.Seconds, CaptureSequence.ImageTypes.SNAPSHOT, filter, null, 1);
                    seq.Binning = new BinningMode(1, 1);
                    seq.Gain = camera.Gain; seq.Offset = camera.Offset;
                    seq.EnableSubSample = camera.CanSubSample;
                    var roi = focusCapture.Roi;
                    seq.SubSambleRectangle = camera.CanSubSample ? new ObservableRectangle(roi.X, roi.Y, roi.Width, roi.Height) : null;
                    token.ThrowIfCancellationRequested();
                    return await imagingMediator.CaptureImage(seq, token, progress);
                }

            IExposureData image;
            var retries = 0;
            do {
                token.ThrowIfCancellationRequested();
                if (Properties.Settings.Default.EnableFocusDiagnostics) Logger.Trace("Starting Exposure for manual focus");
                double expTime = profileService.ActiveProfile.FocuserSettings.AutoFocusExposureTime;
                if (filter != null && filter.AutoFocusExposureTime > -1) {
                    expTime = filter.AutoFocusExposureTime;
                }
                var seq = new CaptureSequence(expTime, CaptureSequence.ImageTypes.SNAPSHOT, filter, null, 1);

                var subSampleRectangle = GetSubSampleRectangle();
                if (subSampleRectangle != null) {
                    seq.EnableSubSample = true;
                    seq.SubSambleRectangle = subSampleRectangle;
                }

                if (filter?.AutoFocusBinning != null) {
                    seq.Binning = filter.AutoFocusBinning;
                } else {
                    seq.Binning = new BinningMode(profileService.ActiveProfile.FocuserSettings.AutoFocusBinning, profileService.ActiveProfile.FocuserSettings.AutoFocusBinning);
                }

                if (filter?.AutoFocusOffset > -1) {
                    seq.Offset = filter.AutoFocusOffset;
                }

                if (filter?.AutoFocusGain > -1) {
                    seq.Gain = filter.AutoFocusGain;
                }
                try {
                    image = await imagingMediator.CaptureImage(seq, token, progress);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception e) {
                    if (!IsSubSampleEnabled()) {
                        throw;
                    }

                    Logger.Warning("Camera error, trying without subsample");
                    Logger.Error(e);
                    seq.EnableSubSample = false;
                    seq.SubSambleRectangle = null;
                    image = await imagingMediator.CaptureImage(seq, token, progress);
                }
                retries++;
                if (image == null && retries < 3) {
                    Logger.Warning($"Image acquisition failed - Retrying {retries}/2");
                }
            } while (image == null && retries < 3);

            return image;
        }

        private bool IsSubSampleEnabled() {
            var cameraInfo = cameraMediator.GetInfo();
            return (profileService.ActiveProfile.FocuserSettings.AutoFocusInnerCropRatio < 1 || profileService.ActiveProfile.FocuserSettings.AutoFocusOuterCropRatio < 1) && cameraInfo.CanSubSample;
        }

        private ObservableRectangle GetSubSampleRectangle() {
            var cameraInfo = cameraMediator.GetInfo();
            var innerCropRatio = profileService.ActiveProfile.FocuserSettings.AutoFocusInnerCropRatio;
            var outerCropRatio = profileService.ActiveProfile.FocuserSettings.AutoFocusOuterCropRatio;
            if (innerCropRatio < 1 || outerCropRatio < 1 && cameraInfo.CanSubSample) {
                // if only inner crop is set, then it is the outer boundary. Otherwise we use the outer crop
                var outsideCropRatio = outerCropRatio >= 1.0 ? innerCropRatio : outerCropRatio;

                int subSampleWidth = (int)Math.Round(cameraInfo.XSize * outsideCropRatio);
                int subSampleHeight = (int)Math.Round(cameraInfo.YSize * outsideCropRatio);
                int subSampleX = (int)Math.Round((cameraInfo.XSize - subSampleWidth) / 2.0d);
                int subSampleY = (int)Math.Round((cameraInfo.YSize - subSampleHeight) / 2.0d);
                return new ObservableRectangle(subSampleX, subSampleY, subSampleWidth, subSampleHeight);
            }
            return null;
        }

        public static SpikeAnalysisParams BuildSpikeParams() {
            var s = Properties.Settings.Default;
            var kind = Enum.TryParse<SpikeMetricKind>(s.SpikeMetric, out var parsed) && Enum.IsDefined(parsed)
                ? parsed : SpikeMetricKind.Legacy;
            return new SpikeAnalysisParams {
                metricKind = kind,
                roiScale = s.RoiScale,
                bgRingFraction = s.BgRingFraction,
                minStarSizePx = s.MinStarSizePx,
                maxStarS = s.MaxStars,
                spikeAngleDeg = s.spikeAngleDeg,
                autoSpikeAngle = s.AutoSpikeAngle,

                coreSigmaPx = s.CoreSigmaPx,
                coreRejectSigmaPx = s.CoreRejectSigmaPx,
                axisSigmaPx = s.AxisSigmaPx,
                axisRejectSigmaPx = s.AxisRejectSigmaPx,

                betaVar = s.BetaVar,
                betaSplit = s.BetaSplit,
                splitPower = s.SplitPower,

                minUsedStarsForValidFrame = s.MinUsedStars
            };
        }

        private async Task<(MeasureAndError hfr, MeasureAndError spike)> EvaluateExposure(
            IExposureData exposureData,
            int focuserPosition,
            SpikeAnalysisParams spikeParam,
            bool spikeEnabled,
            CancellationToken token,
            IProgress<ApplicationStatus> progress, FocusCaptureSettings focusCapture = null) {

            if (Properties.Settings.Default.EnableFocusDiagnostics) Logger.Trace("Evaluating Exposure");
            MeasuredSpikeAngle = double.NaN;
            MeasuredSpikeAngleStrength = 0;
            SpikeAngleIsAuto = spikeParam.autoSpikeAngle;

            var imageData = await exposureData.ToImageData(progress, token);
            if (focusCapture != null) imageData = CropFocusImage(imageData, focusCapture);

            bool autoStretch = true;
            //If using contrast based statistics, no need to stretch
            if (profileService.ActiveProfile.FocuserSettings.AutoFocusMethod == AFMethodEnum.CONTRASTDETECTION && profileService.ActiveProfile.FocuserSettings.ContrastDetectionMethod == ContrastDetectionMethodEnum.Statistics) {
                autoStretch = false;
            }
            var image = await imagingMediator.PrepareImage(imageData, new PrepareImageParameters(autoStretch, false), token);

            var imageProperties = image.RawImageData.Properties;
            var imageStatistics = await image.RawImageData.Statistics.Task;

            //Very simple to directly provide result if we use statistics based contrast detection
            if (profileService.ActiveProfile.FocuserSettings.AutoFocusMethod == AFMethodEnum.CONTRASTDETECTION && profileService.ActiveProfile.FocuserSettings.ContrastDetectionMethod == ContrastDetectionMethodEnum.Statistics) {
                return (new MeasureAndError() { Measure = 100 * imageStatistics.StDev / imageStatistics.Mean, Stdev = 0.01 },
                        new MeasureAndError() { Measure = double.NaN, Stdev = 0 });
            }

            System.Windows.Media.PixelFormat pixelFormat;

            if (imageProperties.IsBayered && profileService.ActiveProfile.ImageSettings.DebayerImage) {
                pixelFormat = System.Windows.Media.PixelFormats.Rgb48;
            } else {
                pixelFormat = System.Windows.Media.PixelFormats.Gray16;
            }

            if (profileService.ActiveProfile.FocuserSettings.AutoFocusMethod == AFMethodEnum.STARHFR) {
                var analysisParams = new StarDetectionParams() {
                    IsAutoFocus = true,
                    Sensitivity = profileService.ActiveProfile.ImageSettings.StarSensitivity,
                    NoiseReduction = profileService.ActiveProfile.ImageSettings.NoiseReduction,
                    NumberOfAFStars = profileService.ActiveProfile.FocuserSettings.AutoFocusUseBrightestStars
                };

                if (focusCapture == null && profileService.ActiveProfile.FocuserSettings.AutoFocusInnerCropRatio < 1 && !IsSubSampleEnabled()) {
                    analysisParams.UseROI = true;
                    analysisParams.InnerCropRatio = profileService.ActiveProfile.FocuserSettings.AutoFocusInnerCropRatio;
                }
                if (focusCapture == null && profileService.ActiveProfile.FocuserSettings.AutoFocusOuterCropRatio < 1) {
                    analysisParams.UseROI = true;
                    if (IsSubSampleEnabled() && profileService.ActiveProfile.FocuserSettings.AutoFocusOuterCropRatio < 1.0) {
                        // We have subsampled already. Since outer crop is set, the user wants a donut shape
                        // OuterCrop of 0 activates the donut logic without any outside clipping, and we scale the inner ratio accordingly
                        analysisParams.OuterCropRatio = 0.0;
                        analysisParams.InnerCropRatio = profileService.ActiveProfile.FocuserSettings.AutoFocusInnerCropRatio / profileService.ActiveProfile.FocuserSettings.AutoFocusOuterCropRatio;
                    } else {
                        analysisParams.OuterCropRatio = profileService.ActiveProfile.FocuserSettings.AutoFocusOuterCropRatio;
                    }
                }

                var starDetection = starDetectionSelector.GetBehavior();
                var analysisResult = await starDetection.Detect(image, pixelFormat, analysisParams, progress, token);

                // Read the host's HFR BEFORE anything else touches the result object.
                double hfrAvg = analysisResult.AverageHFR;
                double hfrStdev = double.IsNaN(analysisResult.HFRStdDev) ? 0 : analysisResult.HFRStdDev;

                // Spike analysis is read-only with respect to analysisResult.
                var spikeResult = SpikeFrameResult.Failed(SpikeStatus.Disabled);
                if (spikeEnabled) {
                    try {
                        spikeResult = SpikeAnalyzer.Evaluate(imageData, spikeParam, analysisResult, ref trackingState);

                        MeasuredSpikeAngle = spikeResult.MeasuredAngleDeg;
                        MeasuredSpikeAngleStrength = spikeResult.AngleStrength;

                        SpikeAnalyzer.LogFrame("frame", focuserPosition, spikeResult);
                        WriteDiagnosticsRow(focuserPosition, hfrAvg, hfrStdev, spikeResult, spikeParam);
                    } catch (Exception e) {
                        // A failure in the experimental metric must never take down a
                        // focus run, let alone the host application.
                        Logger.Error("[ManualFocuser] Spike metric failed", e);
                        spikeResult = SpikeFrameResult.Failed(SpikeStatus.NoValidStars);
                    }
                }

                // Hand the untouched, detector-owned result back to N.I.N.A.
                if (focusCapture == null) image.UpdateAnalysis(analysisParams, analysisResult);

                if (focusCapture == null && profileService.ActiveProfile.ImageSettings.AnnotateImage) {
                    token.ThrowIfCancellationRequested();
                    var starAnnotator = starAnnotatorSelector.GetBehavior();
                    var annotatedImage = await starAnnotator.GetAnnotatedImage(analysisParams, analysisResult, image.Image, token: token);
                    imagingMediator.SetImage(annotatedImage);
                }

                return (new MeasureAndError() { Measure = hfrAvg, Stdev = hfrStdev },
                        new MeasureAndError() { Measure = spikeResult.HasClearSpikes ? spikeResult.Metric : double.NaN, Stdev = spikeResult.Spread });
            } else {
                var analysis = new ContrastDetection();
                var analysisParams = new ContrastDetectionParams() {
                    Sensitivity = profileService.ActiveProfile.ImageSettings.StarSensitivity,
                    NoiseReduction = profileService.ActiveProfile.ImageSettings.NoiseReduction,
                    Method = profileService.ActiveProfile.FocuserSettings.ContrastDetectionMethod
                };
                if (focusCapture == null && profileService.ActiveProfile.FocuserSettings.AutoFocusInnerCropRatio < 1 && !IsSubSampleEnabled()) {
                    analysisParams.UseROI = true;
                    analysisParams.InnerCropRatio = profileService.ActiveProfile.FocuserSettings.AutoFocusInnerCropRatio;
                }
                var analysisResult = await analysis.Measure(image, analysisParams, progress, token);

                var stdev = double.IsNaN(analysisResult.ContrastStdev) ? 0 : analysisResult.ContrastStdev;
                return (new MeasureAndError() { Measure = analysisResult.AverageContrast, Stdev = stdev },
                        new MeasureAndError() { Measure = double.NaN, Stdev = 0 });
            }
        }

        // ====================================================
        // Diagnostics CSV
        //
        // J alone cannot tell you why a point jumped. Logging the
        // individual terms turns a night of observing into a tuning
        // dataset that can be replayed offline.
        // ====================================================
        private static readonly object diagLock = new object();
        private string diagPath;

        private void WriteDiagnosticsRow(int focuserPosition, double hfr, double hfrStdev, SpikeFrameResult r, SpikeAnalysisParams p) {
            if (!Properties.Settings.Default.EnableFocusDiagnostics) return;

            try {
                lock (diagLock) {
                    if (diagPath == null) {
                        var dir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "NINA", "Logs");
                        Directory.CreateDirectory(dir);
                        diagPath = Path.Combine(dir, $"ManualFocuser-spike-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                        File.AppendAllText(diagPath,
                            "utc,focuserPosition,hfr,hfrStdev,status,usedStars,J,spread,varC,varG,kurtosis," +
                            "measuredAngle,angleStrength,usedAngle," +
                            "spikeAngleDeg,autoAngle,tau,coreReject,axisSigma,axisReject,betaVar,betaSplit,splitPower,roiScale,bgRing," +
                            "metricKind,uMax,fwhm,separation,dipDepth,profileSnr,clearSpikes\n",
                            Encoding.UTF8);
                    }

                    var inv = CultureInfo.InvariantCulture;
                    var sb = new StringBuilder();
                    sb.Append(DateTime.UtcNow.ToString("o", inv)).Append(',');
                    sb.Append(focuserPosition.ToString(inv)).Append(',');
                    sb.Append(hfr.ToString("F4", inv)).Append(',');
                    sb.Append(hfrStdev.ToString("F4", inv)).Append(',');
                    sb.Append(r.Status).Append(',');
                    sb.Append(r.UsedStars.ToString(inv)).Append(',');
                    sb.Append(r.Metric.ToString("F6", inv)).Append(',');
                    sb.Append(r.Spread.ToString("F6", inv)).Append(',');
                    sb.Append(r.MedianVarC.ToString("F6", inv)).Append(',');
                    sb.Append(r.MedianVarG.ToString("F6", inv)).Append(',');
                    sb.Append(r.MedianKurtosis.ToString("F6", inv)).Append(',');
                    sb.Append(r.MeasuredAngleDeg.ToString("F2", inv)).Append(',');
                    sb.Append(r.AngleStrength.ToString("F3", inv)).Append(',');
                    sb.Append(r.UsedAngleDeg.ToString("F2", inv)).Append(',');
                    sb.Append(p.spikeAngleDeg.ToString(inv)).Append(',');
                    sb.Append(p.autoSpikeAngle ? "1" : "0").Append(',');
                    sb.Append(p.coreSigmaPx.ToString(inv)).Append(',');
                    sb.Append(p.coreRejectSigmaPx.ToString(inv)).Append(',');
                    sb.Append(p.axisSigmaPx.ToString(inv)).Append(',');
                    sb.Append(p.axisRejectSigmaPx.ToString(inv)).Append(',');
                    sb.Append(p.betaVar.ToString(inv)).Append(',');
                    sb.Append(p.betaSplit.ToString(inv)).Append(',');
                    sb.Append(p.splitPower.ToString(inv)).Append(',');
                    sb.Append(p.roiScale.ToString(inv)).Append(',');
                    sb.Append(p.bgRingFraction.ToString(inv)).Append(',');
                    sb.Append(p.metricKind).Append(',');
                    sb.Append(p.uMaxPx.ToString(inv)).Append(',');
                    sb.Append(r.MedianFwhm.ToString("F6", inv)).Append(',');
                    sb.Append(r.MedianSeparation.ToString("F6", inv)).Append(',');
                    sb.Append(r.MedianDipDepth.ToString("F6", inv)).Append(',');
                    sb.Append(r.MedianProfileSnr.ToString("F6", inv)).Append(',');
                    sb.Append(r.HasClearSpikes ? "1" : "0").Append('\n');

                    File.AppendAllText(diagPath, sb.ToString(), Encoding.UTF8);
                }
            } catch (Exception e) {
                Logger.Warning($"[ManualFocuser] Could not write spike diagnostics: {e.Message}");
            }
        }
        private static IEnumerable<FocusFitSample> ToFitSamples(IEnumerable<ScatterErrorPoint> source) {
            foreach (var p in source ?? Enumerable.Empty<ScatterErrorPoint>()) {
                double error = 0;
                var property = p.GetType().GetProperty("YError") ?? p.GetType().GetProperty("ErrorY") ?? p.GetType().GetProperty("Stdev");
                if (property != null) error = Convert.ToDouble(property.GetValue(p));
                yield return new FocusFitSample(p.X, p.Y, error);
            }
        }

        public bool TryFitParabolaWeighted(IEnumerable<ScatterErrorPoint> sourceEnumerable, out double a, out double b, out double c) {
            a = b = c = 0;
            if (!FocusCurveFit.TryFit(ToFitSamples(sourceEnumerable), out var fit)) return false;
            a = fit.A / (fit.Scale * fit.Scale);
            b = fit.B / fit.Scale - 2 * a * fit.Center;
            c = fit.C - fit.B * fit.Center / fit.Scale + a * fit.Center * fit.Center;
            return true;
        }
        /// <summary>
        /// Generate sampled curve points for the active pass and populate the matching FitCurvePoints collection.
        /// Returns true if the curve was generated.
        /// </summary>
        public bool GenerateFitCurveForCurrentPass(out bool max, out double xvalue, out double yvalue, int samplePoints = 100) {
            var source = (CurrentPass == 0 ? ManualFocusPointsPrimary : ManualFocusPointsSecondary)
                .Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToList();
            var target = CurrentPass == 0 ? FitCurvePointsPrimary : FitCurvePointsSecondary;

            target.Clear();

            max= false;
            xvalue = 0.0;
            yvalue = 0.0;

            if (source == null || source.Count() < 3) {
                return false;
            }

            if (!FocusCurveFit.TryFit(ToFitSamples(source), out var fit)) {
                return false;
            }

            // Determine x-range
            double minX = double.MaxValue;
            double maxX = double.MinValue;
            foreach (var p in source) {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
            }

            double span = Math.Max(1.0, maxX - minX);
            double left = minX - span * 0.05;
            double right = maxX + span * 0.05;

            int n = Math.Max(2, samplePoints);
            double step = (right - left) / (n - 1);
            for (int i = 0; i < n; i++) {
                double x = left + step * i;
                double y = fit.Evaluate(x);
                target.Add(new DataPoint(x, y));
            }

            xvalue = fit.Vertex;
            yvalue = double.IsFinite(xvalue) ? fit.Evaluate(xvalue) : double.NaN;

            if(fit.A<0) {
                max = true;
            } else {
                max = false;
            }

            return true;
        }

    }
}
