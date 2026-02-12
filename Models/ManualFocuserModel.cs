using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Mediator;
using NINA.WPF.Base.ViewModel.AutoFocus;
using OxyPlot;
using OxyPlot.Series;
using OxyPlot.Wpf;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cwseo.NINA.ManualFocuser.Models {
    public class ManualFocuserModel {

        private IProfileService profileService;
        private IImagingMediator imagingMediator;
        private ICameraMediator cameraMediator;
        private IPluggableBehaviorSelector<IStarDetection> starDetectionSelector;
        private IPluggableBehaviorSelector<IStarAnnotator> starAnnotatorSelector;
        public double HFRDelta { get; set; }
        public double StepDelta { get; set; }
        public double MinStep { get; set; }
        public double MinHFR { get; set; }

        public AsyncObservableCollection<ScatterErrorPoint> HFRFocusPoints { get; } = new AsyncObservableCollection<ScatterErrorPoint>();
        public AsyncObservableCollection<ScatterPoint> SpikeFocusPoints { get; } = new AsyncObservableCollection<ScatterPoint>();
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
            return HFRFocusPoints.Count();
        }
        public void AddHFRPoint(int position, MeasureAndError measurement) {
            var idx = HFRFocusPoints.Count();

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

            if(idx > 0) {
                ArrowPoint[0] = PlotFocusPoints[idx - 1];
                ArrowPoint[1] = PlotFocusPoints[idx];
            }
        }

        public void AddSpikePoint(int position, double measure) {
            var step = Convert.ToDouble(position);
            SpikeFocusPoints.Add(new ScatterPoint(step, measure));
        }

        public void ResetPlotData() {
            HFRFocusPoints.Clear();
            PlotFocusPoints.Clear();
            SpikeFocusPoints.Clear();
            HFRDelta = 0.0;
            StepDelta = 0.0;
            MinStep = 0.0;
            MinHFR = 0.0;

            ArrowPoint.Clear();
            ArrowPoint.Add(new DataPoint(0, 0));
            ArrowPoint.Add(new DataPoint(0, 0));
        }



        public async Task<Task<(MeasureAndError,double)>> GetAverageMeasurementTask(FilterInfo filter, int exposuresPerFocusPoint, CancellationToken token, IProgress<ApplicationStatus> progress) {
            List<Task<(MeasureAndError, double)>> measurements = new List<Task<(MeasureAndError, double)>>();

            for (int i = 0; i < exposuresPerFocusPoint; i++) {
                var image = await TakeExposure(filter, token, progress);

                measurements.Add(EvaluateExposure(image, token, progress));

                token.ThrowIfCancellationRequested();
            }

            return EvaluateAllExposures(measurements, exposuresPerFocusPoint, token);
        }

        private async Task<(MeasureAndError, double)> EvaluateAllExposures(List<Task<(MeasureAndError,double)>> measureTasks, int exposuresPerFocusPoint, CancellationToken token) {
            var measures = await Task.WhenAll(measureTasks);

            //Average HFR  of multiple exposures (if configured this way)
            double sumMeasure = 0;
            double sumVariances = 0;
            var spikeValues = new List<double>();
            foreach (var partialMeasurement in measures) {
                (MeasureAndError hfr, double spike) = partialMeasurement;
                sumMeasure += hfr.Measure;
                sumVariances += hfr.Stdev * hfr.Stdev;
                spikeValues.Add(spike);
            }
            double medianSpike = SpikeAnalyzer.Median(spikeValues);
            return (new MeasureAndError() { Measure = sumMeasure / exposuresPerFocusPoint, Stdev = Math.Sqrt(sumVariances / exposuresPerFocusPoint) }, medianSpike);
        }
        private async Task<IExposureData> TakeExposure(FilterInfo filter, CancellationToken token, IProgress<ApplicationStatus> progress) {
            IExposureData image;
            var retries = 0;
            do {
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

        private async Task<(MeasureAndError hfr, double spike)> EvaluateExposure(IExposureData exposureData, CancellationToken token, IProgress<ApplicationStatus> progress) {
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
                return (new MeasureAndError() { Measure = 100 * imageStatistics.StDev / imageStatistics.Mean, Stdev = 0.01 }, 0.0);
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
                double spikeintensity = SpikeAnalyzer.TryCalculateSigmaSquare(imageData, analysisResult);
                image.UpdateAnalysis(analysisParams, analysisResult);

                if (profileService.ActiveProfile.ImageSettings.AnnotateImage) {
                    token.ThrowIfCancellationRequested();
                    var starAnnotator = starAnnotatorSelector.GetBehavior();
                    var annotatedImage = await starAnnotator.GetAnnotatedImage(analysisParams, analysisResult, image.Image, token: token);
                    imagingMediator.SetImage(annotatedImage);
                }

                var stdev = double.IsNaN(analysisResult.HFRStdDev) ? 0 : analysisResult.HFRStdDev;
                return (new MeasureAndError() { Measure = analysisResult.AverageHFR, Stdev = stdev }, spikeintensity);
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
                MeasureAndError ContrastMeasurement = new MeasureAndError() { Measure = analysisResult.AverageContrast, Stdev = stdev };
                return (ContrastMeasurement, 0.0);
            }
        }
    }
}
