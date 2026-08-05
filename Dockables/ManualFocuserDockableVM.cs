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
using NINA.WPF.Base.Interfaces.ViewModel;
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
    public class ManualFocuserDockableVM : DockableVM, IFocuserConsumer, ITelescopeConsumer, ICameraConsumer, IFilterWheelConsumer, IGuiderConsumer, IDisposable {
        private readonly ICameraMediator cameraMediator;
        private readonly IFocuserMediator focuserMediator;
        private readonly IFilterWheelMediator filterWheelMediator;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IGuiderMediator guiderMediator;
        private readonly ManualFocuserModel DataModel;

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

        // Only the connection state of the focuser affects CanExecute, so that is the
        // only transition worth re-querying on. Calling InvalidateRequerySuggested on
        // every device tick makes WPF re-evaluate every command in the application.
        private bool lastFocuserConnected = false;

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

        public bool IsMoving {
            get => _moving;
            set {
                _moving = value;
                RaisePropertyChanged(nameof(IsMoving));
            }
        }

        public bool IsCapturing {
            get => _capturing;
            set {
                _capturing = value;
                RaisePropertyChanged(nameof(IsCapturing));
            }
        }

        public double MinHFR {
            get => this.DataModel.MinHFR;
            set {
                this.DataModel.MinHFR = value;
                RaisePropertyChanged(nameof(MinHFR));
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
        public bool HasSpikePoints => this.DataModel.SpikeFocusPoints.Count > 0;

        public bool HasSpikeAngle => !double.IsNaN(this.DataModel.MeasuredSpikeAngle);

        /// <summary>
        /// Measured spike orientation, shown on the plot whether or not auto mode is
        /// on, so the configured angle can be sanity checked against reality. The
        /// strength is the peak-over-mean of the directional profile; below roughly
        /// 1.2 the frame has no clear spike and the number should not be trusted.
        /// </summary>
        public string SpikeAngleText {
            get {
                var m = this.DataModel;
                if (double.IsNaN(m.MeasuredSpikeAngle)) return "spike angle: not detected";

                string mode = m.SpikeAngleIsAuto ? "auto" : $"set {Properties.Settings.Default.spikeAngleDeg:F0}°";
                return $"spike {m.MeasuredSpikeAngle:F1}°  (x{m.MeasuredSpikeAngleStrength:F2}, {mode})";
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

        public AsyncObservableCollection<ScatterErrorPoint> HFRFocusPoints => this.DataModel.HFRFocusPoints;
        public AsyncObservableCollection<ScatterErrorPoint> SpikeFocusPoints => this.DataModel.SpikeFocusPoints;
        public AsyncObservableCollection<DataPoint> PlotFocusPoints => this.DataModel.PlotFocusPoints;
        public AsyncObservableCollection<DataPoint> ArrowPoint => this.DataModel.ArrowPoint;

        public ICommand ClearChartCommand { get; private set; }
        public ICommand InputResetCommand { get; private set; }
        public ICommand HaltFocuserCommand { get; private set; }
        public ICommand MoveToPositionCommand { get; private set; }
        public ICommand MoveINCommand { get; private set; }
        public ICommand MoveOUTCommand { get; private set; }

        [ImportingConstructor]
        public ManualFocuserDockableVM(
            IProfileService profileService,
            ICameraMediator cameraMediator, IImagingMediator imagingMediator, IFilterWheelMediator filterWheelMediator, IFocuserMediator focuserMediator, ITelescopeMediator telescopeMediator,
            IGuiderMediator guiderMediator,
            IPluggableBehaviorSelector<IStarDetection> starDetectionSelector,
            IPluggableBehaviorSelector<IStarAnnotator> starAnnotatorSelector) : base(profileService) {

            // This will reference the resource dictionary to import the SVG graphic and assign it as the icon for the header bar
            var dict = new ResourceDictionary();
            dict.Source = new Uri("Cwseo.NINA.ManualFocuser;component/Dockables/ManualFocuserDockableTemplates.xaml", UriKind.RelativeOrAbsolute);
            ImageGeometry = (System.Windows.Media.GeometryGroup)dict["Cwseo.NINA.ManualFocuser_SVG"];
            ImageGeometry.Freeze();

            this.cameraMediator = cameraMediator;
            this.focuserMediator = focuserMediator;
            this.telescopeMediator = telescopeMediator;
            this.filterWheelMediator = filterWheelMediator;
            this.guiderMediator = guiderMediator;

            Title = "Manual Focuser";

            this.DataModel = new ManualFocuserModel(profileService, imagingMediator, cameraMediator, starDetectionSelector, starAnnotatorSelector);

            // Commands are created before consumer registration so that no device
            // callback can fire a CanExecute against half-initialised state.
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
            }));

            HaltFocuserCommand = new RelayCommand(_ => Guard("Halt", () => {
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
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;

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
            });
        }

        public void UpdateDeviceInfo(CameraInfo deviceInfo) {
            if (deviceInfo == null) return;
            ApplyOnUiThread(() => {
                CameraInfo = deviceInfo;
                RaisePropertyChanged(nameof(CameraInfo));
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
            });
        }

        // FocuserInfo is a reference type and stays null until the focuser VM has
        // registered itself with the mediator. CanExecute runs on the dispatcher,
        // so dereferencing it unguarded throws straight into the WPF message loop.
        private bool CanMove() {
            return FocuserInfo?.Connected == true && !IsMoving;
        }

        private void ResetCts() {
            try { moveCts?.Cancel(); } catch { }
            try { moveCts?.Dispose(); } catch { }
            moveCts = new CancellationTokenSource();
        }

        private void ResetCaptureCts() {
            try { captureCts?.Cancel(); } catch { }
            try { captureCts?.Dispose(); } catch { }
            captureCts = new CancellationTokenSource();
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
            IsCapturing = true;
            cameraMediator.RegisterCaptureBlock(this);
            try {
                // N.I.N.A. calls progress.Report unconditionally in places, so a null
                // progress is an NRE waiting to happen. Progress<T> would marshal every
                // report onto the dispatcher, so use a sink that simply discards.
                IProgress<ApplicationStatus> progress = NullProgress<ApplicationStatus>.Instance;

                var autofocusFilter = await SetAutofocusFilter(null, captureCts.Token, progress);

                var (hfrMeasurement, spike) = await this.DataModel.GetAverageMeasurement(
                    autofocusFilter,
                    profileService.ActiveProfile.FocuserSettings.AutoFocusNumberOfFramesPerPoint,
                    focuser.Position,
                    captureCts.Token,
                    progress);

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
                try { cameraMediator.ReleaseCaptureBlock(this); } catch { }
            }
        }

        private void RaiseMeasurementProperties() {
            RaisePropertyChanged(nameof(MinStep));
            RaisePropertyChanged(nameof(MinHFR));
            RaisePropertyChanged(nameof(StepDelta));
            RaisePropertyChanged(nameof(HFRDelta));
            RaisePropertyChanged(nameof(MinSpike));
            RaisePropertyChanged(nameof(MinSpikeStep));
            RaisePropertyChanged(nameof(HasSpikePoints));
            RaisePropertyChanged(nameof(HasSpikeAngle));
            RaisePropertyChanged(nameof(SpikeAngleText));
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
