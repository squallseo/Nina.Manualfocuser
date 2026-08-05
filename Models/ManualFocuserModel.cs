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
    public class ManualFocuserModel {
        private readonly IProfileService profileService;
        private readonly IImagingMediator imagingMediator;
        private readonly ICameraMediator cameraMediator;
        private readonly IPluggableBehaviorSelector<IStarDetection> starDetectionSelector;
        private readonly IPluggableBehaviorSelector<IStarAnnotator> starAnnotatorSelector;

        private SpikeTrackingState trackingState = null;

        public double HFRDelta { get; set; }
        public double StepDelta { get; set; }
        public double MinStep { get; set; }
        public double MinHFR { get; set; }

        // Spike minimum, tracked separately - J is a different quantity from HFR
        // and its minimum is the thing an autofocus run would eventually search for.
        public double MinSpikeStep { get; set; }
        public double MinSpike { get; set; }

        /// <summary>Spike orientation measured on the most recent frame, NaN if unknown.</summary>
        public double MeasuredSpikeAngle { get; private set; } = double.NaN;
        public double MeasuredSpikeAngleStrength { get; private set; }
        public bool SpikeAngleIsAuto { get; private set; }

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
            var idx = HFRFocusPoints.Count;

            var step = Convert.ToDouble(position);
            var hfr = measurement.Measure;
            var errorY = Math.Max(0.001, measurement.Stdev);

            if (idx > 0) {
                var lastpoint = HFRFocusPoints[idx - 1];
                StepDelta = step - lastpoint.X;
                HFRDelta = hfr - lastpoint.Y;
                if (hfr < MinHFR) {
                    MinStep = step;
                    MinHFR = hfr;
                }
            } else {
                MinStep = step;
                MinHFR = hfr;
            }

            HFRFocusPoints.Add(new ScatterErrorPoint(step, hfr, 0, errorY));
            PlotFocusPoints.Add(new DataPoint(step, hfr));

            if (idx > 0) {
                ArrowPoint[0] = PlotFocusPoints[idx - 1];
                ArrowPoint[1] = PlotFocusPoints[idx];
            }
        }

        /// <summary>
        /// Adds a spike point. Invalid frames carry NaN and are dropped rather than
        /// plotted - a sentinel value on the axis wrecks the scale and hides the curve.
        /// </summary>
        public void AddSpikePoint(int position, MeasureAndError measurement) {
            if (double.IsNaN(measurement.Measure) || double.IsInfinity(measurement.Measure)) {
                Logger.Debug($"[ManualFocuser] Skipping invalid spike point at position {position}");
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

            // Star tracking must restart with the plot, otherwise a new run keeps
            // chasing stars seeded in the previous one.
            trackingState = null;

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
            IProgress<ApplicationStatus> progress) {

            int frames = Math.Max(1, exposuresPerFocusPoint);
            var measures = new List<(MeasureAndError hfr, MeasureAndError spike)>(frames);

            for (int i = 0; i < frames; i++) {
                token.ThrowIfCancellationRequested();

                var image = await TakeExposure(filter, token, progress);
                if (image == null) {
                    Logger.Warning("[ManualFocuser] Exposure returned no image - skipping frame");
                    continue;
                }

                measures.Add(await EvaluateExposure(image, focuserPosition, token, progress));
            }

            if (measures.Count == 0) {
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

            var spikeResult = spikeValues.Count == 0
                ? new MeasureAndError { Measure = double.NaN, Stdev = 0 }
                : new MeasureAndError {
                    Measure = SpikeCore.Median(spikeValues),
                    // Combine the per-frame star scatter with the frame-to-frame scatter
                    // so the error bar means something when multiple frames are averaged.
                    Stdev = Math.Sqrt(spikeVariances.Average() + Math.Pow(SpikeCore.StdDevSample(spikeValues), 2))
                };

            return (hfrResult, spikeResult);
        }

        private async Task<IExposureData> TakeExposure(FilterInfo filter, CancellationToken token, IProgress<ApplicationStatus> progress) {
            IExposureData image;
            var retries = 0;
            do {
                token.ThrowIfCancellationRequested();
                Logger.Trace("Starting Exposure for manual focus");
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
            return new SpikeAnalysisParams {
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
            CancellationToken token,
            IProgress<ApplicationStatus> progress) {

            Logger.Trace("Evaluating Exposure");

            var imageData = await exposureData.ToImageData(progress, token);

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

                if (profileService.ActiveProfile.FocuserSettings.AutoFocusInnerCropRatio < 1 && !IsSubSampleEnabled()) {
                    analysisParams.UseROI = true;
                    analysisParams.InnerCropRatio = profileService.ActiveProfile.FocuserSettings.AutoFocusInnerCropRatio;
                }
                if (profileService.ActiveProfile.FocuserSettings.AutoFocusOuterCropRatio < 1) {
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
                if (Properties.Settings.Default.EnableSpikeMetric) {
                    try {
                        var spikeParam = BuildSpikeParams();
                        spikeResult = SpikeAnalyzer.Evaluate(imageData, spikeParam, analysisResult, ref trackingState);

                        if (spikeResult.HasAngleEstimate) {
                            MeasuredSpikeAngle = spikeResult.MeasuredAngleDeg;
                            MeasuredSpikeAngleStrength = spikeResult.AngleStrength;
                        }
                        SpikeAngleIsAuto = spikeParam.autoSpikeAngle;

                        SpikeAnalyzer.LogFrame("frame", focuserPosition, spikeResult);
                        WriteDiagnosticsRow(focuserPosition, hfrAvg, hfrStdev, spikeResult);
                    } catch (Exception e) {
                        // A failure in the experimental metric must never take down a
                        // focus run, let alone the host application.
                        Logger.Error("[ManualFocuser] Spike metric failed", e);
                        spikeResult = SpikeFrameResult.Failed(SpikeStatus.NoValidStars);
                    }
                }

                // Hand the untouched, detector-owned result back to N.I.N.A.
                image.UpdateAnalysis(analysisParams, analysisResult);

                if (profileService.ActiveProfile.ImageSettings.AnnotateImage) {
                    token.ThrowIfCancellationRequested();
                    var starAnnotator = starAnnotatorSelector.GetBehavior();
                    var annotatedImage = await starAnnotator.GetAnnotatedImage(analysisParams, analysisResult, image.Image, token: token);
                    imagingMediator.SetImage(annotatedImage);
                }

                return (new MeasureAndError() { Measure = hfrAvg, Stdev = hfrStdev },
                        new MeasureAndError() { Measure = spikeResult.Metric, Stdev = spikeResult.Spread });
            } else {
                var analysis = new ContrastDetection();
                var analysisParams = new ContrastDetectionParams() {
                    Sensitivity = profileService.ActiveProfile.ImageSettings.StarSensitivity,
                    NoiseReduction = profileService.ActiveProfile.ImageSettings.NoiseReduction,
                    Method = profileService.ActiveProfile.FocuserSettings.ContrastDetectionMethod
                };
                if (profileService.ActiveProfile.FocuserSettings.AutoFocusInnerCropRatio < 1 && !IsSubSampleEnabled()) {
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

        private void WriteDiagnosticsRow(int focuserPosition, double hfr, double hfrStdev, SpikeFrameResult r) {
            if (!Properties.Settings.Default.WriteSpikeDiagnostics) return;

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
                            "spikeAngleDeg,autoAngle,tau,coreReject,axisSigma,axisReject,betaVar,betaSplit,splitPower,roiScale,bgRing\n",
                            Encoding.UTF8);
                    }

                    var s = Properties.Settings.Default;
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
                    sb.Append(s.spikeAngleDeg.ToString(inv)).Append(',');
                    sb.Append(s.AutoSpikeAngle ? "1" : "0").Append(',');
                    sb.Append(s.CoreSigmaPx.ToString(inv)).Append(',');
                    sb.Append(s.CoreRejectSigmaPx.ToString(inv)).Append(',');
                    sb.Append(s.AxisSigmaPx.ToString(inv)).Append(',');
                    sb.Append(s.AxisRejectSigmaPx.ToString(inv)).Append(',');
                    sb.Append(s.BetaVar.ToString(inv)).Append(',');
                    sb.Append(s.BetaSplit.ToString(inv)).Append(',');
                    sb.Append(s.SplitPower.ToString(inv)).Append(',');
                    sb.Append(s.RoiScale.ToString(inv)).Append(',');
                    sb.Append(s.BgRingFraction.ToString(inv)).Append('\n');

                    File.AppendAllText(diagPath, sb.ToString(), Encoding.UTF8);
                }
            } catch (Exception e) {
                Logger.Debug($"[ManualFocuser] Could not write spike diagnostics: {e.Message}");
            }
        }
    }
}
