using Accord.Statistics.Moving;
using Grpc.Core;
using Newtonsoft.Json.Linq;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Core.Locale;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.ImageAnalysis;
using NINA.Profile.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.ViewModel;
using NINA.WPF.Base.ViewModel.AutoFocus;
using OxyPlot;
using OxyPlot.Series;
using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Cwseo.NINA.ManualFocuser.Models;

namespace Cwseo.NINA.ManualFocuser.Dockables {

    internal sealed class NullProgress<T> : IProgress<T> {
        public static readonly NullProgress<T> Instance = new NullProgress<T>();
        private NullProgress() { }
        public void Report(T value) { }
    }

    /// <summary>
    /// Imaging-tab panel that drives the focuser in explicit increments and plots
    /// HFR (and the experimental spike metric) against focuser position.
    /// </summary>
    [Export(typeof(IDockableVM))]
    public partial class ManualFocuserDockableVM : DockableVM, IFocuserConsumer, ITelescopeConsumer, ICameraConsumer, IFilterWheelConsumer, IGuiderConsumer, IDisposable {
        private readonly ICameraMediator cameraMediator;
        private readonly IApplicationStatusMediator applicationStatusMediator;
        private readonly IFocuserMediator focuserMediator;
        private readonly IFilterWheelMediator filterWheelMediator;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IGuiderMediator guiderMediator;
        private readonly IImagingMediator imagingMediator;
        private readonly IDomeMediator domeMediator;
        private readonly IDomeFollower domeFollower;
        private readonly IPlateSolverFactory plateSolverFactory;
        private readonly ManualFocuserModel DataModel;
        // Add a field to hold the handler so we can unsubscribe
        private readonly Func<Task> linearAfHandler;
        public FocuserInfo FocuserInfo { get; private set; }
        public TelescopeInfo TelescopeInfo { get; private set; }
        public CameraInfo CameraInfo { get; private set; }
        public FilterWheelInfo FilterwheelInfo { get; private set; }
        public GuiderInfo GuiderInfo { get; private set; }

        private CancellationTokenSource moveCts;
        private CancellationTokenSource captureCts;
        private bool _moving = false;
        private bool _capturing = false;
        private bool disposed = false;

        // Re-query commands on relevant device transitions, rather than every tick.
        private bool lastFocuserConnected = false;
        private string lastGotoUnavailableReason;

        public int TargetPosition {
            get => Properties.Settings.Default.TargetPosition;
            set {
                Properties.Settings.Default.TargetPosition = value;
                Properties.Settings.Default.Save();
                RaisePropertyChanged(nameof(TargetPosition));
            }
        }

        public int UserStep {
            get => Properties.Settings.Default.UserStep;
            set {
                Properties.Settings.Default.UserStep = value;
                Properties.Settings.Default.Save();
                RaisePropertyChanged(nameof(UserStep));
            }
        }

        public bool TakeShootAfterMove {
            get => Properties.Settings.Default.TakeShootAfterMove;
            set {
                Properties.Settings.Default.TakeShootAfterMove = value;
                Properties.Settings.Default.Save();
                RaisePropertyChanged(nameof(TakeShootAfterMove));
            }
        }
        public bool UseOnePass {
            get {
                return Properties.Settings.Default.UseOnePass;
            }
            set {
                Properties.Settings.Default.UseOnePass = value; // Settings에 저장
                Properties.Settings.Default.Save(); // 저장 반영
                RaisePropertyChanged(nameof(UseOnePass));
            }
        }

        public bool IsMoving {
            get => _moving;
            set {
                _moving = value;
                RaisePropertyChanged(nameof(CanConfigureLive));
                RaisePropertyChanged(nameof(IsMoving));
                RefreshGotoAvailability();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsCapturing {
            get => _capturing;
            set {
                _capturing = value;
                RaisePropertyChanged(nameof(CanConfigureLive));
                RaisePropertyChanged(nameof(IsCapturing));
                RefreshGotoAvailability();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public double MinHFR {
            get => this.DataModel.MinHFR;
            set {
                this.DataModel.MinHFR = value;
                RaisePropertyChanged(nameof(MinHFR));
            }
        }
        public double MaxHFR {
            get => this.DataModel.MaxHFR;
            set {
                this.DataModel.MaxHFR = value;
                RaisePropertyChanged(nameof(MaxHFR));
            }
        }
        public double MinStep {
            get => this.DataModel.MinStep;
            set {
                this.DataModel.MinStep = value;
                RaisePropertyChanged(nameof(MinStep));
            }
        }

        public double MinSpike => this.DataModel.MinSpike;
        public double MinSpikeStep => this.DataModel.MinSpikeStep;
        public bool HasSpikePoints => IsSpikeMetricEnabled && this.DataModel.HasClearSpikes && this.DataModel.SpikeFocusPoints.Count > 0;
        public string SpikeDisplayStatus => this.DataModel.SpikeDisplayStatus;

        public bool HasSpikeAngle => double.IsFinite(DisplaySpikeAngle);

        public bool IsSpikeMetricEnabled => Properties.Settings.Default.EnableSpikeMetric;

        /// <summary>
        /// The angle is the parameter a wrong value hurts most, so it lives on the
        /// panel rather than behind the options tab.
        /// </summary>
        public double SpikeAngle {
            get => Properties.Settings.Default.spikeAngleDeg;
            set {
                Properties.Settings.Default.spikeAngleDeg = double.IsNaN(value) ? 0 : Math.Clamp(value, -360.0, 360.0);
                Properties.Settings.Default.Save();
                RaisePropertyChanged(nameof(SpikeAngle));
            }
        }

        public bool AutoSpikeAngle {
            get => Properties.Settings.Default.AutoSpikeAngle;
            set {
                Properties.Settings.Default.AutoSpikeAngle = value;
                Properties.Settings.Default.Save();
                RaisePropertyChanged(nameof(AutoSpikeAngle));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>
        /// Orientation measured from the last frame. Reported whether or not auto
        /// mode is on, so a typed value can be checked against reality. The
        /// tooltip reports the directional profile's peak-over-mean strength.
        /// Live and AF publish their private detector results here too.
        /// </summary>
        public string MeasuredAngleText {
            get {
                return HasSpikeAngle ? $"Detected: {DisplaySpikeAngle:F1}°" : "Not detected";
            }
        }

        public double MaxStep {
            get => this.DataModel.MaxStep;
            set {
                this.DataModel.MaxStep = value;
                RaisePropertyChanged(nameof(MaxStep));
            }
        }
        public double StepDelta {
            get => this.DataModel.StepDelta;
            set {
                this.DataModel.StepDelta = value;
                RaisePropertyChanged(nameof(StepDelta));
            }
        }

        public double HFRDelta {
            get => this.DataModel.HFRDelta;
            set {
                this.DataModel.HFRDelta = value;
                RaisePropertyChanged(nameof(HFRDelta));
            }
        }
        // Expose per-pass collections for the view to bind separate series
        public AsyncObservableCollection<DataPoint> PlotFocusPointsPrimary => this.DataModel.PlotFocusPointsPrimary;
        public AsyncObservableCollection<DataPoint> PlotFocusPointsSecondary => this.DataModel.PlotFocusPointsSecondary;
        public AsyncObservableCollection<DataPoint> FitCurvePointsPrimary => this.DataModel.FitCurvePointsPrimary;
        public AsyncObservableCollection<DataPoint> FitCurvePointsSecondary => this.DataModel.FitCurvePointsSecondary;

        public AsyncObservableCollection<ScatterErrorPoint> HFRFocusPoints => this.DataModel.HFRFocusPoints;
        public AsyncObservableCollection<ScatterErrorPoint> SpikeFocusPoints => this.DataModel.SpikeFocusPoints;
        public AsyncObservableCollection<DataPoint> PlotFocusPoints => this.DataModel.PlotFocusPoints;
        public AsyncObservableCollection<DataPoint> ArrowPoint => this.DataModel.ArrowPoint;

        public ICommand UseMeasuredAngleCommand { get; private set; }
        public ICommand ClearChartCommand { get; private set; }
        public ICommand InputResetCommand { get; private set; }
        public ICommand LinearAFCommand { get; private set; }
        public ICommand HaltFocuserCommand { get; private set; }
        public ICommand MoveToPositionCommand { get; private set; }
        public ICommand MoveINCommand { get; private set; }
        public ICommand MoveOUTCommand { get; private set; }

        [ImportingConstructor]
        public ManualFocuserDockableVM(
            IProfileService profileService,
            ICameraMediator cameraMediator, IImagingMediator imagingMediator, IFilterWheelMediator filterWheelMediator, IFocuserMediator focuserMediator, ITelescopeMediator telescopeMediator,
            IGuiderMediator guiderMediator, IDomeMediator domeMediator, IDomeFollower domeFollower, IPlateSolverFactory plateSolverFactory,
            IPluggableBehaviorSelector<IStarDetection> starDetectionSelector,
            IPluggableBehaviorSelector<IStarAnnotator> starAnnotatorSelector,
            IApplicationStatusMediator applicationStatusMediator) : base(profileService) {

            // This will reference the resource dictionary to import the SVG graphic and assign it as the icon for the header bar
            var dict = new ResourceDictionary();
            dict.Source = new Uri("Cwseo.NINA.ManualFocuser;component/Dockables/ManualFocuserDockableTemplates.xaml", UriKind.RelativeOrAbsolute);
            ImageGeometry = (System.Windows.Media.GeometryGroup)dict["Cwseo.NINA.ManualFocuser_SVG"];
            ImageGeometry.Freeze();

            this.cameraMediator = cameraMediator;
            this.applicationStatusMediator = applicationStatusMediator;
            this.focuserMediator = focuserMediator;
            this.telescopeMediator = telescopeMediator;
            this.filterWheelMediator = filterWheelMediator;
            this.guiderMediator = guiderMediator;
            this.imagingMediator = imagingMediator;
            this.domeMediator = domeMediator;
            this.domeFollower = domeFollower;
            this.plateSolverFactory = plateSolverFactory;

            // Ensure static plugin helpers have the mediators so static calls won't NRE
            Cwseo.NINA.ManualFocuser.ManualFocuser.Camera = cameraMediator;
            Cwseo.NINA.ManualFocuser.ManualFocuser.Focuser = focuserMediator;

            Title = "Manual Focuser";

            this.DataModel = new ManualFocuserModel(profileService, imagingMediator, cameraMediator, starDetectionSelector, starAnnotatorSelector);
            InitializeFocusTargets(profileService);
            InitializeFocusAssist();
            ObservePreparedImages(imagingMediator);

            // Commands are created before consumer registration so that no device
            // callback can fire a CanExecute against half-initialised state.
            // Freeze the measurement into the manual value: measure with auto on,
            // adopt it, then run fixed. Useful once the orientation is known good.
            UseMeasuredAngleCommand = new RelayCommand(
                _ => Guard("Use measured angle", () => {
                    var measured = DisplaySpikeAngle;
                    if (double.IsNaN(measured)) return;
                    SpikeAngle = measured;
                    AutoSpikeAngle = false;
                }),
                _ => HasSpikeAngle);

            ClearChartCommand = new RelayCommand(_ => Guard("Clear chart", () => {
                this.DataModel.ResetPlotData();
                RaiseMeasurementProperties();
            }));

            InputResetCommand = new RelayCommand(_ => Guard("Input reset", () => {
                var focuser = FocuserInfo;
                if (this.DataModel.GetFocusPointSize() > 0) {
                    TargetPosition = Convert.ToInt32(MinStep);
                } else if (focuser != null) {
                    TargetPosition = focuser.Position;
                }
                if (focuser != null) {
                    UserStep = Convert.ToInt32(focuser.StepSize);
                }
            }), _ => CanMove() && HasMeasuredBest);

            HaltFocuserCommand = new RelayCommand(_ => Guard("Halt", () => {
                try { assistCts?.Cancel(); } catch { }
                try { moveCts?.Cancel(); } catch { }
                try { captureCts?.Cancel(); } catch { }
            }));

            MoveToPositionCommand = new AsyncCommand<int>(() => RunGuarded("Move to position", ExecuteMoveToAsync), o => CanMove());
            MoveINCommand = new AsyncCommand<int>(() => RunGuarded("Move in", ExecuteMoveInAsync), o => CanMove());
            MoveOUTCommand = new AsyncCommand<int>(() => RunGuarded("Move out", ExecuteMoveOutAsync), o => CanMove());

            this.focuserMediator.RegisterConsumer(this);
            this.telescopeMediator.RegisterConsumer(this);
            this.cameraMediator.RegisterConsumer(this);
            this.filterWheelMediator.RegisterConsumer(this);
            this.guiderMediator.RegisterConsumer(this);
            this.linearAfHandler = () => RunGuarded("HFR AF", () => RunCurveAutofocusAsync("Linear",monitor:false));
            ManualFocuser.LinearAFRequested += this.linearAfHandler;
            LinearAFCommand = new AsyncCommand<int>(() => RunGuarded("HFR AF", ExecuteLinearAFAsync), o => CanMove() && CameraInfo?.Connected == true);
        }

        public void Dispose() {
            stretchRefresh?.Cancel();
            if (disposed) return;
            disposed = true;
            StopObservingPreparedImages();
            try { assistCts?.Cancel(); } catch { }
            try { gotoCts?.Cancel(); } catch { }

            // On shutdown cleanup
            try { Cwseo.NINA.ManualFocuser.ManualFocuser.LinearAFRequested -= this.linearAfHandler; } catch { }
            try { this.moveCts?.Cancel(); } catch { }
            try { this.moveCts?.Dispose(); } catch { }
            try { this.captureCts?.Cancel(); } catch { }
            try { this.captureCts?.Dispose(); } catch { }
            try { this.focuserMediator?.RemoveConsumer(this); } catch { }
            try { this.telescopeMediator?.RemoveConsumer(this); } catch { }
            try { this.cameraMediator?.RemoveConsumer(this); } catch { }
            try { this.filterWheelMediator?.RemoveConsumer(this); } catch { }
            try { this.guiderMediator?.RemoveConsumer(this); } catch { }
            GC.SuppressFinalize(this);
        }

        public override bool IsTool { get; } = true;

        // ==============================================================
        // Failure containment
        //
        // N.I.N.A.'s AsyncCommand routes the task through NotifyTaskCompletion,
        // which swallows exceptions - a failed move used to leave no trace at
        // all. Catch explicitly so the user and the log both learn about it.
        // ==============================================================
        private void Guard(string what, Action action) {
            try {
                action();
            } catch (Exception e) {
                Logger.Error($"[ManualFocuser] {what} failed", e);
            }
        }

        private async Task<int> RunGuarded(string what, Func<Task<int>> action) {
            try {
                return await action();
            } catch (OperationCanceledException) {
                Logger.Info($"[ManualFocuser] {what} cancelled");
                return 0;
            } catch (Exception e) {
                Logger.Error($"[ManualFocuser] {what} failed", e);
                Notification.ShowError($"Manual Focuser: {what} failed - {e.Message}");
                return 0;
            }
        }

        // ==============================================================
        // Device info
        // ==============================================================
        private void ApplyOnUiThread(Action apply) {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess()) {
                try { apply(); } catch (Exception e) { Logger.Error("[ManualFocuser] Device update failed", e); }
            } else {
                dispatcher.BeginInvoke((Action)(() => {
                    try { apply(); } catch (Exception e) { Logger.Error("[ManualFocuser] Device update failed", e); }
                }));
            }
        }

        public void UpdateDeviceInfo(FocuserInfo deviceInfo) {
            if (deviceInfo == null) return;
            ApplyOnUiThread(() => {
                FocuserInfo = deviceInfo;
                RaisePropertyChanged(nameof(FocuserInfo));
                RaisePropertyChanged(nameof(GraphCurrentPosition));
                RaisePropertyChanged(nameof(GraphPositionMarkersVisible));
                RaisePropertyChanged(nameof(CanMoveFromGraph));
                if (deviceInfo.Connected != lastFocuserConnected) {
                    lastFocuserConnected = deviceInfo.Connected;
                    CommandManager.InvalidateRequerySuggested();
                }
            });
        }

        public void UpdateDeviceInfo(TelescopeInfo deviceInfo) {
            if (deviceInfo == null) return;
            ApplyOnUiThread(() => {
                TelescopeInfo = deviceInfo;
                RaisePropertyChanged(nameof(TelescopeInfo));
                RefreshGotoAvailability();
            });
        }

        public void UpdateDeviceInfo(CameraInfo deviceInfo) {
            if (deviceInfo == null) return;
            ApplyOnUiThread(() => {
                CameraInfo = deviceInfo;
                RaisePropertyChanged(nameof(CameraInfo));
                RaisePropertyChanged(nameof(RoiLocationText));
                RaisePropertyChanged(nameof(PreviewRoiRectangle));
                RaisePropertyChanged(nameof(CanUseFocusStreaming));
                RaisePropertyChanged(nameof(FocusStreamAvailability));
                RefreshGotoAvailability();
            });
        }

        public void UpdateDeviceInfo(FilterWheelInfo deviceInfo) {
            if (deviceInfo == null) return;
            ApplyOnUiThread(() => {
                FilterwheelInfo = deviceInfo;
                RaisePropertyChanged(nameof(FilterwheelInfo));
            });
        }

        public void UpdateDeviceInfo(GuiderInfo deviceInfo) {
            if (deviceInfo == null) return;
            ApplyOnUiThread(() => {
                GuiderInfo = deviceInfo;
                RaisePropertyChanged(nameof(GuiderInfo));
                RefreshGotoAvailability();
            });
        }

        // FocuserInfo is a reference type and stays null until the focuser VM has
        // registered itself with the mediator. CanExecute runs on the dispatcher,
        // so dereferencing it unguarded throws straight into the WPF message loop.
        private bool CanMove() {
            return !disposed && FocuserInfo?.Connected == true && !IsMoving && !IsCapturing && !IsGoingToFocusTarget;
        }

        private void ResetCts() {
            try { moveCts?.Cancel(); } catch { }
            try { moveCts?.Dispose(); } catch { }
            moveCts = new CancellationTokenSource();
        }

        private void ResetCaptureCts() {
            try { captureCts?.Cancel(); } catch { }
            try { captureCts?.Dispose(); } catch { }
            captureCts = moveCts == null ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(moveCts.Token);
        }

        // ==============================================================
        // Focuser moves
        // ==============================================================
        private async Task<int> ExecuteMoveToAsync() {
            ResetCts();
            IsMoving = true;
            try {
                await CaptureFirstPoint();
                await focuserMediator.MoveFocuser(Properties.Settings.Default.TargetPosition, moveCts.Token);
                return await ExecuteShootAsync();
            } finally {
                IsMoving = false;
            }
        }

        private Task<int> ExecuteLinearAFAsync() => RunCurveAutofocusAsync("Linear");

        private async Task<int> ExecuteMoveInAsync() {
            ResetCts();
            IsMoving = true;
            try {
                await CaptureFirstPoint();
                await focuserMediator.MoveFocuserRelative(-Math.Abs(Properties.Settings.Default.UserStep), moveCts.Token);
                return await ExecuteShootAsync();
            } finally {
                IsMoving = false;
            }
        }

        private async Task<int> ExecuteMoveOutAsync() {
            ResetCts();
            IsMoving = true;
            try {
                await CaptureFirstPoint();
                await focuserMediator.MoveFocuserRelative(+Math.Abs(Properties.Settings.Default.UserStep), moveCts.Token);
                return await ExecuteShootAsync();
            } finally {
                IsMoving = false;
            }
        }

        private async Task<int> CaptureFirstPoint() {
            if (this.DataModel.GetFocusPointSize() > 0) return 0;
            return await ExecuteShootAsync();
        }

        private async Task<int> ExecuteShootAsync() {
            if (!Properties.Settings.Default.TakeShootAfterMove) return 0;

            var camera = CameraInfo;
            if (camera?.Connected != true) return 0;

            var focuser = FocuserInfo;
            if (focuser == null) return 0;

            // Do not fight the sequencer, N.I.N.A.'s own autofocus, live view or a
            // flat wizard for the camera. Two overlapping capture/download cycles on
            // the same driver is how a native camera SDK takes the whole process down.
            if (!cameraMediator.IsFreeToCapture(this)) {
                Logger.Warning("[ManualFocuser] Camera is busy - skipping exposure");
                Notification.ShowWarning("Manual Focuser: camera is busy, exposure skipped");
                return 0;
            }

            ResetCaptureCts();
            IsSelectingRoi = false;
            IsCapturing = true;
            bool ownsCaptureBlock = true;
            if (ownsCaptureBlock) cameraMediator.RegisterCaptureBlock(this);
            try {
                // N.I.N.A. calls progress.Report unconditionally in places, so a null
                // progress is an NRE waiting to happen. Progress<T> would marshal every
                // report onto the dispatcher, so use a sink that simply discards.
                IProgress<ApplicationStatus> progress = NullProgress<ApplicationStatus>.Instance;
                var focusCapture = DataModel.CreateFocusCaptureSettings(PreviewExposureMs / 1000,
                    PreviewRoiWidth, PreviewRoiHeight, PreviewCenterX, PreviewCenterY);
                applicationStatusMediator.StatusUpdate(new ApplicationStatus { Source = "Manual Focuser",
                    Status = $"Focus exposure {focusCapture.Seconds:F3} s | ROI {focusCapture.Roi.Width}×{focusCapture.Roi.Height} px" });

                var autofocusFilter = await SetAutofocusFilter(null, captureCts.Token, progress);

                var (hfrMeasurement, spike) = await this.DataModel.GetAverageMeasurement(
                    autofocusFilter,
                    profileService.ActiveProfile.FocuserSettings.AutoFocusNumberOfFramesPerPoint,
                    focuser.Position,
                    captureCts.Token,
                    progress, focusCapture);

                //If star Measurement is 0, we didn't detect any stars or shapes, and want this point to be ignored by the fitting as much as possible. Setting a very high Stdev will do the trick.
                if (hfrMeasurement.Measure == 0) {
                    Logger.Warning($"No stars detected. Setting a high stddev to ignore the point.");
                    hfrMeasurement.Stdev = 1000;
                }

                this.DataModel.AddHFRPoint(focuser.Position, hfrMeasurement);
                this.DataModel.AddSpikePoint(focuser.Position, spike);
                RaiseMeasurementProperties();
                return 1;
            } finally {
                IsCapturing = false;
                if (ownsCaptureBlock) {
                    try { cameraMediator.ReleaseCaptureBlock(this); } catch { }
                    applicationStatusMediator.StatusUpdate(new ApplicationStatus { Source = "Manual Focuser", Status = string.Empty });
                }
            }
        }

        public bool HasMeasuredBest => DataModel.HFRFocusPoints.Any(point =>
            double.IsFinite(point.Y) && point.Y > 0 && point.Y == MinHFR &&
            point.X == MinStep && point.X >= 0 && point.X <= int.MaxValue);

        private void RaiseMeasurementProperties() {
            RaisePropertyChanged(nameof(HasMeasuredBest));
            CommandManager.InvalidateRequerySuggested();
            RaisePropertyChanged(nameof(MaxStep));
            RaisePropertyChanged(nameof(MaxHFR));
            RaisePropertyChanged(nameof(MinStep));
            RaisePropertyChanged(nameof(MinHFR));
            RaisePropertyChanged(nameof(StepDelta));
            RaisePropertyChanged(nameof(HFRDelta));
            RaisePropertyChanged(nameof(MinSpike));
            RaisePropertyChanged(nameof(MinSpikeStep));
            RaisePropertyChanged(nameof(HasSpikePoints));
            RaisePropertyChanged(nameof(SpikeDisplayStatus));
            RaisePropertyChanged(nameof(HasSpikeAngle));
            RaisePropertyChanged(nameof(MeasuredAngleText));
            RaisePropertyChanged(nameof(MeasuredAngleTooltip));
            RaisePropertyChanged(nameof(IsSpikeMetricEnabled));
            RaisePropertyChanged(nameof(SpikeAngle));
            RaisePropertyChanged(nameof(AutoSpikeAngle));
        }

        /// <summary>
        /// Returns the filter to expose through, or null to leave the wheel where it is.
        /// Returning an empty FilterInfo would make N.I.N.A. drive the wheel to slot 0
        /// before every single frame.
        /// </summary>
        private async Task<FilterInfo> SetAutofocusFilter(FilterInfo imagingFilter, CancellationToken token, IProgress<ApplicationStatus> progress) {
            if (!profileService.ActiveProfile.FocuserSettings.UseFilterWheelOffsets) {
                return imagingFilter;
            }

            var filter = profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(f => f.AutoFocusFilter == true);
            if (filter == null) {
                return imagingFilter;
            }

            //Set the filter to the autofocus filter if necessary, and move to it so autofocus X indexing works properly when invoking GetFocusPoints()
            try {
                return await filterWheelMediator.ChangeFilter(filter, token, progress);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception e) {
                Logger.Error("Failed to change filter during AutoFocus", e);
                Notification.ShowWarning(String.Format(Loc.Instance["LblFailedToChangeFilter"], e.Message));
                return imagingFilter;
            }
        }

        // ---- IFocuserConsumer 나머지 메서드 ----
        public void UpdateEndAutoFocusRun(AutoFocusInfo info) { }
        public void UpdateUserFocused(FocuserInfo info) { }
        public void NewAutoFocusPoint(OxyPlot.DataPoint dataPoint) { }
        public void AutoFocusRunStarting() { }
    }
}
