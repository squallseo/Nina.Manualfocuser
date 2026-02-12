using Accord.Imaging.Filters;
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
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Model;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Mediator;
using NINA.WPF.Base.Utility.AutoFocus;
using NINA.WPF.Base.ViewModel;
using NINA.WPF.Base.ViewModel.AutoFocus;
using OxyPlot;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Cwseo.NINA.ManualFocuser.Models;
using static NINA.Image.FileFormat.XISF.XISFImageProperty.Instrument;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    /// <summary>
    /// This Class shows the basic principle on how to add a new panel to N.I.N.A. Imaging tab via the plugin interface
    /// In this example an altitude chart is added to the imaging tab that shows the altitude chart based on the position of the telescope    
    /// </summary>
    [Export(typeof(IDockableVM))]
    public class ManualFocuserDockableVM : DockableVM, IFocuserConsumer, ITelescopeConsumer, ICameraConsumer, IFilterWheelConsumer, IGuiderConsumer {
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
        public CameraControl cameraControl { get; private set; }
        private CancellationTokenSource moveCts;
        private CancellationTokenSource captureCts;
        private bool _moving = false;
        private bool _capturing = false;
        public int TargetPosition {
            get {
                // 저장된 값 가져오기
                return Properties.Settings.Default.TargetPosition;
            }
            set {
                Properties.Settings.Default.TargetPosition = value; // Settings에 저장
                Properties.Settings.Default.Save(); // 저장 반영
                RaisePropertyChanged(nameof(TargetPosition));
            }
        }
        public int UserStep {
            get {
                return Properties.Settings.Default.UserStep;
            }
            set {
                Properties.Settings.Default.UserStep = value; // Settings에 저장
                Properties.Settings.Default.Save(); // 저장 반영
                RaisePropertyChanged(nameof(UserStep));
            }
        }
        public bool TakeShootAfterMove {
            get {
                return Properties.Settings.Default.TakeShootAfterMove;
            }
            set {
                Properties.Settings.Default.TakeShootAfterMove = value; // Settings에 저장
                Properties.Settings.Default.Save(); // 저장 반영
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
        public AsyncObservableCollection<ScatterErrorPoint> HFRFocusPoints {
            get => this.DataModel.HFRFocusPoints;
        }
        public AsyncObservableCollection<ScatterPoint> SpikeFocusPoints {
            get => this.DataModel.SpikeFocusPoints;
        }
        public AsyncObservableCollection<DataPoint> PlotFocusPoints {
            get => this.DataModel.PlotFocusPoints;
        }
        public AsyncObservableCollection<DataPoint> ArrowPoint {
            get => this.DataModel.ArrowPoint;
        }

        // ✅ NINA Core.Utility 커맨드만 사용 (모호성 제거)
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

            TargetPosition = Properties.Settings.Default.TargetPosition;
            UserStep = Properties.Settings.Default.UserStep;
            
            this.focuserMediator.RegisterConsumer(this);
            this.telescopeMediator.RegisterConsumer(this);
            this.cameraMediator.RegisterConsumer(this);
            this.filterWheelMediator.RegisterConsumer(this);
            this.guiderMediator.RegisterConsumer(this);

            this.DataModel = new ManualFocuserModel(profileService, imagingMediator, cameraMediator, starDetectionSelector, starAnnotatorSelector);


            ClearChartCommand = new global::NINA.Core.Utility.RelayCommand(_ => {
                try {
                    this.DataModel.ResetPlotData();
                } catch { }
            });
            InputResetCommand = new global::NINA.Core.Utility.RelayCommand(_ => {
                try {
                    if (this.DataModel.GetFocusPointSize() > 0) {
                        TargetPosition = Convert.ToInt32(MinStep);
                    } else {
                        TargetPosition = FocuserInfo.Position;
                    }
                    UserStep = Convert.ToInt32(FocuserInfo.StepSize);
                } catch { }
            });
            HaltFocuserCommand = new global::NINA.Core.Utility.RelayCommand(_ => {
                try { moveCts?.Cancel(); } catch { }
                try { captureCts?.Cancel(); } catch { }
            });

            // ✅ AsyncCommand는 named arg 없이 "원본 방식"으로
            MoveToPositionCommand = new AsyncCommand<int>(
                () => ExecuteMoveToAsync(),
                o => CanMove()
            );

            MoveINCommand = new AsyncCommand<int>(
                () => ExecuteMoveInAsync(),
                o => CanMove()
            );

            MoveOUTCommand = new AsyncCommand<int>(
                () => ExecuteMoveOutAsync(),
                o => CanMove()
            );
        }


        public void Dispose() {
            // On shutdown cleanup
            try { this.moveCts?.Cancel(); } catch { }
            try { this.moveCts?.Dispose(); } catch { }
            try { this.captureCts?.Cancel(); } catch { }
            try { this.captureCts?.Dispose(); } catch { }
            try { this.focuserMediator?.RemoveConsumer(this); } catch { }
            try { this.telescopeMediator?.RemoveConsumer(this); } catch { }
            try { this.cameraMediator?.RemoveConsumer(this); } catch { }
            try { this.filterWheelMediator?.RemoveConsumer(this); } catch { }
            try { this.guiderMediator?.RemoveConsumer(this); } catch { }
        }

        public override bool IsTool { get; } = true;

        public void UpdateDeviceInfo(FocuserInfo deviceInfo) {
            if (deviceInfo == null) return;

            void Apply() {
                FocuserInfo = deviceInfo;
                RaisePropertyChanged(nameof(FocuserInfo));
                // Connected 변경으로 CanExecute 재평가가 필요함 (클릭 전에도 즉시 반영)
                CommandManager.InvalidateRequerySuggested();
            }

            if (Application.Current?.Dispatcher?.CheckAccess() == true) {
                Apply();
            } else {
                Application.Current?.Dispatcher?.BeginInvoke((Action)Apply);
            }
        }
        public void UpdateDeviceInfo(TelescopeInfo deviceInfo) {
            if (deviceInfo == null) return;

            void Apply() {
                TelescopeInfo = deviceInfo;
                RaisePropertyChanged(nameof(TelescopeInfo));
                CommandManager.InvalidateRequerySuggested();
            }

            if (Application.Current?.Dispatcher?.CheckAccess() == true) {
                Apply();
            } else {
                Application.Current?.Dispatcher?.BeginInvoke((Action)Apply);
            }
        }
        public void UpdateDeviceInfo(CameraInfo deviceInfo) {
            if (deviceInfo == null) return;

            void Apply() {
                CameraInfo = deviceInfo;
                RaisePropertyChanged(nameof(CameraInfo));
                CommandManager.InvalidateRequerySuggested();
            }

            if (Application.Current?.Dispatcher?.CheckAccess() == true) {
                Apply();
            } else {
                Application.Current?.Dispatcher?.BeginInvoke((Action)Apply);
            }
        }

        public void UpdateDeviceInfo(FilterWheelInfo deviceInfo) {
            if (deviceInfo == null) return;

            void Apply() {
                FilterwheelInfo = deviceInfo;
                RaisePropertyChanged(nameof(FilterWheelInfo));
                CommandManager.InvalidateRequerySuggested(); 
            }

            if (Application.Current?.Dispatcher?.CheckAccess() == true) {
                Apply();
            } else {
                Application.Current?.Dispatcher?.BeginInvoke((Action)Apply);
            }
        }

        public void UpdateDeviceInfo(GuiderInfo deviceInfo) {
            if (deviceInfo == null) return;

            void Apply() {
                GuiderInfo = deviceInfo;
                RaisePropertyChanged(nameof(GuiderInfo));
                CommandManager.InvalidateRequerySuggested();
            }

            if (Application.Current?.Dispatcher?.CheckAccess() == true) {
                Apply();
            } else {
                Application.Current?.Dispatcher?.BeginInvoke((Action)Apply);
            }
        }

        private bool CanMove() {
            return FocuserInfo.Connected && !IsMoving;
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
            var idx = this.DataModel.GetFocusPointSize();
            if (idx > 0) return 0;
            return await ExecuteShootAsync();
        }
        private async Task<int> ExecuteShootAsync() {
            if (!Properties.Settings.Default.TakeShootAfterMove || !CameraInfo.Connected) return 0;
            ResetCaptureCts();
            IsCapturing = true;
            try {
                IProgress<ApplicationStatus> progress = null;
                var filterCts = new CancellationTokenSource();
                FilterInfo autofocusFilter = await SetAutofocusFilter(new FilterInfo(), filterCts.Token, progress);
                Task<(MeasureAndError,double)> measurementTask = await this.DataModel.GetAverageMeasurementTask(autofocusFilter, profileService.ActiveProfile.FocuserSettings.AutoFocusNumberOfFramesPerPoint, captureCts.Token, progress);
                (MeasureAndError HFRmeasurement, double spike) = await measurementTask;

                //If star Measurement is 0, we didn't detect any stars or shapes, and want this point to be ignored by the fitting as much as possible. Setting a very high Stdev will do the trick.
                if (HFRmeasurement.Measure == 0) {
                    Logger.Warning($"No stars detected. Setting a high stddev to ignore the point.");
                    HFRmeasurement.Stdev = 1000;
                }
                this.DataModel.AddHFRPoint(FocuserInfo.Position, HFRmeasurement);
                this.DataModel.AddSpikePoint(FocuserInfo.Position, spike); 
                RaisePropertyChanged(nameof(MinStep));
                RaisePropertyChanged(nameof(MinHFR));
                RaisePropertyChanged(nameof(StepDelta));
                RaisePropertyChanged(nameof(HFRDelta));
                return 1;
            } finally {
                IsCapturing = false;
            }
        }

        private async Task<FilterInfo> SetAutofocusFilter(FilterInfo imagingFilter, CancellationToken token, IProgress<ApplicationStatus> progress) {
            if (profileService.ActiveProfile.FocuserSettings.UseFilterWheelOffsets) {
                var filter = profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Where(f => f.AutoFocusFilter == true).FirstOrDefault();
                if (filter == null) {
                    return imagingFilter;
                }

                //Set the filter to the autofocus filter if necessary, and move to it so autofocus X indexing works properly when invoking GetFocusPoints()
                try {
                    return await filterWheelMediator.ChangeFilter(filter, token, progress);
                } catch (Exception e) {
                    Logger.Error("Failed to change filter during AutoFocus", e);
                    Notification.ShowWarning(String.Format(Loc.Instance["LblFailedToChangeFilter"], e.Message));
                    return imagingFilter;
                }
            } else {
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
