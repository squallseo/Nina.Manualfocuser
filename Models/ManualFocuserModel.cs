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
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RTG.ManualFocuser.Models {
    public class ManualFocuserModel {

        private IProfileService profileService;
        private IImagingMediator imagingMediator;
        private ICameraMediator cameraMediator;
        private IPluggableBehaviorSelector<IStarDetection> starDetectionSelector;
        private IPluggableBehaviorSelector<IStarAnnotator> starAnnotatorSelector;
        public double HFRDelta { get; set; }
        public double StepDelta { get; set; }
        public double MinStep { get; set; }
        public double MaxStep { get; set; }
        public double MinHFR { get; set; }
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

        public AsyncObservableCollection<ScatterErrorPoint> ManualFocusPoints { get; } = new AsyncObservableCollection<ScatterErrorPoint>();
        public AsyncObservableCollection<DataPoint> PlotFocusPoints { get; } = new AsyncObservableCollection<DataPoint>();
        public AsyncObservableCollection<DataPoint> ArrowPoint { get; } = new AsyncObservableCollection<DataPoint>();

        // Per-pass collections (primary = coarse/pass 0, secondary = fine/pass 1)
        public int CurrentPass { get; set; } = 0;
        public AsyncObservableCollection<ScatterErrorPoint> ManualFocusPointsPrimary { get; } = new AsyncObservableCollection<ScatterErrorPoint>();
        public AsyncObservableCollection<ScatterErrorPoint> ManualFocusPointsSecondary { get; } = new AsyncObservableCollection<ScatterErrorPoint>();
        public AsyncObservableCollection<DataPoint> PlotFocusPointsPrimary { get; } = new AsyncObservableCollection<DataPoint>();
        public AsyncObservableCollection<DataPoint> PlotFocusPointsSecondary { get; } = new AsyncObservableCollection<DataPoint>();
        public AsyncObservableCollection<DataPoint> FitCurvePointsPrimary { get; } = new AsyncObservableCollection<DataPoint>();
        public AsyncObservableCollection<DataPoint> FitCurvePointsSecondary { get; } = new AsyncObservableCollection<DataPoint>();

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
            return ManualFocusPoints.Count();
        }
        public void AddFocusPoint(int position, MeasureAndError measurement) {
            var idx = ManualFocusPoints.Count();

            var step = Convert.ToDouble(position);
            var hfr = measurement.Measure;
            var errorY = Math.Max(0.001, measurement.Stdev);

            if (idx > 0) {
                var lastpoint = ManualFocusPoints[idx - 1];
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
                if(hfr > 0.0) {
                    MinHFR = hfr;
                }else MinHFR = double.MaxValue;
                MaxHFR = hfr;
                MaxStep = position;
            }

            var scatter = new ScatterErrorPoint(position, hfr, 0, errorY);
            ManualFocusPoints.Add(scatter);
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
        public void ResetPlotData() {
            ManualFocusPoints.Clear();
            PlotFocusPoints.Clear();
            HFRDelta = 0.0;
            StepDelta = 0.0;
            MinStep = 0.0;
            MinHFR = 0.0;
            MaxHFR = 0.0;

            // Clear per-pass plotting and fit collections automatically
            ManualFocusPointsPrimary.Clear();
            ManualFocusPointsSecondary.Clear();
            PlotFocusPointsPrimary.Clear();
            PlotFocusPointsSecondary.Clear();
            FitCurvePointsPrimary.Clear();
            FitCurvePointsSecondary.Clear();

            ArrowPoint.Clear();
            ArrowPoint.Add(new DataPoint(0, 0));
            ArrowPoint.Add(new DataPoint(0, 0));
        }



        public async Task<Task<MeasureAndError>> GetAverageMeasurementTask(FilterInfo filter, int exposuresPerFocusPoint, CancellationToken token, IProgress<ApplicationStatus> progress) {
            List<Task<MeasureAndError>> measurements = new List<Task<MeasureAndError>>();

            for (int i = 0; i < exposuresPerFocusPoint; i++) {
                var image = await TakeExposure(filter, token, progress);

                measurements.Add(EvaluateExposure(image, token, progress));

                token.ThrowIfCancellationRequested();
            }

            return EvaluateAllExposures(measurements, exposuresPerFocusPoint, token);
        }

        private async Task<MeasureAndError> EvaluateAllExposures(List<Task<MeasureAndError>> measureTasks, int exposuresPerFocusPoint, CancellationToken token) {
            var measures = await Task.WhenAll(measureTasks);

            //Average HFR  of multiple exposures (if configured this way)
            double sumMeasure = 0;
            double sumVariances = 0;
            foreach (var partialMeasurement in measures) {
                sumMeasure += partialMeasurement.Measure;
                sumVariances += partialMeasurement.Stdev * partialMeasurement.Stdev;
            }
            return new MeasureAndError() { Measure = sumMeasure / exposuresPerFocusPoint, Stdev = Math.Sqrt(sumVariances / exposuresPerFocusPoint) };
        }
        private async Task<IExposureData> TakeExposure(FilterInfo filter, CancellationToken token, IProgress<ApplicationStatus> progress) {
            IExposureData image;
            var retries = 0;
            do {
                Logger.Trace("Starting Exposure for autofocus");
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

        private async Task<MeasureAndError> EvaluateExposure(IExposureData exposureData, CancellationToken token, IProgress<ApplicationStatus> progress) {
            Logger.Trace("Evaluating Exposure");

            try {
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
                    return new MeasureAndError() { Measure = 100 * imageStatistics.StDev / imageStatistics.Mean, Stdev = 0.01 };
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
                    image.UpdateAnalysis(analysisParams, analysisResult);

                    if (profileService.ActiveProfile.ImageSettings.AnnotateImage) {
                        token.ThrowIfCancellationRequested();
                        var starAnnotator = starAnnotatorSelector.GetBehavior();
                        var annotatedImage = await starAnnotator.GetAnnotatedImage(analysisParams, analysisResult, image.Image, token: token);
                        imagingMediator.SetImage(annotatedImage);
                    }

                    var stdev = double.IsNaN(analysisResult.HFRStdDev) ? 0 : analysisResult.HFRStdDev;
                    return new MeasureAndError() { Measure = analysisResult.AverageHFR, Stdev = stdev };
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
                    return ContrastMeasurement;
                }
            } catch (Exception e) {
                token.ThrowIfCancellationRequested();
                MeasureAndError errorMeasurement = new MeasureAndError() { Measure = 0, Stdev = 0 };
                Logger.Error(e);
                return errorMeasurement;
            }
        }

        /// <summary>
        /// Try to fit a weighted parabola y = a*x^2 + b*x + c to the provided focus points.
        /// Weights are taken from the scatter point Y error (attempt property names YError, ErrorY, Stdev).
        /// </summary>
        public bool TryFitParabolaWeighted(IEnumerable<ScatterErrorPoint> sourceEnumerable, out double a, out double b, out double c) {
            a = 0.0;
            b = 0.0;
            c = 0.0;

            var source = sourceEnumerable?.ToList() ?? new List<ScatterErrorPoint>();
            int count = source.Count;
            if (count < 3) {
                return false;
            }

            double S_w = 0.0;
            double S_wx = 0.0;
            double S_wx2 = 0.0;
            double S_wx3 = 0.0;
            double S_wx4 = 0.0;
            double S_wy = 0.0;
            double S_wxy = 0.0;
            double S_wx2y = 0.0;

            for (int i = 0; i < count; i++) {
                var p = source[i];
                double x = p.X;
                double y = p.Y;

                // Read Y error using reflection to support different ScatterErrorPoint implementations
                double yErr = 0.0;
                var pi = p.GetType().GetProperty("YError") ?? p.GetType().GetProperty("ErrorY") ?? p.GetType().GetProperty("Stdev");
                if (pi != null) {
                    try {
                        object val = pi.GetValue(p);
                        if (val != null) yErr = Convert.ToDouble(val);
                    } catch {
                        yErr = 0.0;
                    }
                }
                double w = 1.0;
                if (yErr > 0.0) {
                    w = 1.0 / (yErr * yErr);
                }

                double x2 = x * x;
                double x3 = x2 * x;
                double x4 = x2 * x2;

                S_w += w;
                S_wx += w * x;
                S_wx2 += w * x2;
                S_wx3 += w * x3;
                S_wx4 += w * x4;
                S_wy += w * y;
                S_wxy += w * x * y;
                S_wx2y += w * x2 * y;
            }

            double[,] A = new double[3, 3] {
                { S_wx4, S_wx3, S_wx2 },
                { S_wx3, S_wx2, S_wx },
                { S_wx2, S_wx,  S_w }
            };
            double[] B = new double[3] { S_wx2y, S_wxy, S_wy };

            double[] coeffs = Solve3x3(A, B);
            if (coeffs == null) {
                return false;
            }

            a = coeffs[0];
            b = coeffs[1];
            c = coeffs[2];
            return true;
        }

        /// <summary>
        /// Generate sampled curve points for the active pass and populate the matching FitCurvePoints collection.
        /// Returns true if the curve was generated.
        /// </summary>
        public bool GenerateFitCurveForCurrentPass(out bool max, out double xvalue, out double yvalue, int samplePoints = 100) {
            var source = CurrentPass == 0 ? (IEnumerable<ScatterErrorPoint>)ManualFocusPointsPrimary : ManualFocusPointsSecondary;
            var target = CurrentPass == 0 ? FitCurvePointsPrimary : FitCurvePointsSecondary;

            target.Clear();

            max= false;
            xvalue = 0.0;
            yvalue = 0.0;

            if (source == null || source.Count() < 3) {
                return false;
            }

            double a, b, c;
            if (!TryFitParabolaWeighted(source, out a, out b, out c)) {
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
                double y = a * x * x + b * x + c;
                target.Add(new DataPoint(x, y));
            }

            xvalue = -b / (2 * a);
            yvalue = a * xvalue * xvalue + b * xvalue + c;

            if(a<0) {
                max = true;
            } else {
                max = false;
            }

            return true;
        }

        /// <summary>
        /// Solve a 3x3 linear system A * x = B using Gaussian elimination with partial pivoting.
        /// Returns null if singular.
        /// </summary>
        private double[] Solve3x3(double[,] A, double[] B) {
            double[,] M = new double[3, 4];
            for (int i = 0; i < 3; i++) {
                for (int j = 0; j < 3; j++) {
                    M[i, j] = A[i, j];
                }
                M[i, 3] = B[i];
            }

            // Forward elimination with partial pivoting
            for (int k = 0; k < 3; k++) {
                int pivot = k;
                double max = Math.Abs(M[k, k]);
                for (int r = k + 1; r < 3; r++) {
                    double absv = Math.Abs(M[r, k]);
                    if (absv > max) {
                        max = absv;
                        pivot = r;
                    }
                }
                if (Math.Abs(M[pivot, k]) < 1e-18) {
                    return null;
                }
                if (pivot != k) {
                    for (int c = k; c < 4; c++) {
                        double tmp = M[k, c];
                        M[k, c] = M[pivot, c];
                        M[pivot, c] = tmp;
                    }
                }

                for (int i = k + 1; i < 3; i++) {
                    double factor = M[i, k] / M[k, k];
                    for (int j = k; j < 4; j++) {
                        M[i, j] -= factor * M[k, j];
                    }
                }
            }

            double[] x = new double[3];
            for (int i = 2; i >= 0; i--) {
                double sum = M[i, 3];
                for (int j = i + 1; j < 3; j++) {
                    sum -= M[i, j] * x[j];
                }
                x[i] = sum / M[i, i];
            }
            return x;
        }
    }
}
