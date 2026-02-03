using Accord.Statistics.Moving;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Cwseo.NINA.Focuser.FocuserDockables {
    [Export(typeof(IDockableVM))]
    public class FocuserDockable : DockableVM, IFocuserConsumer {
        private readonly IFocuserMediator focuserMediator;
        private CancellationTokenSource moveCts = new CancellationTokenSource();

        private bool _moving;
        public FocuserInfo FocuserInfo { get; private set; }

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

        public bool IsMoving {
            get => _moving;
            set {
                _moving = value;
                RaisePropertyChanged(nameof(IsMoving));
            }
        }

        // ✅ NINA Core.Utility 커맨드만 사용 (모호성 제거)
        public ICommand HaltFocuserCommand { get; private set; }
        public ICommand MoveToPositionCommand { get; private set; }
        public ICommand MoveINCommand { get; private set; }
        public ICommand MoveOUTCommand { get; private set; }

        public override bool IsTool { get; } = true;

        [ImportingConstructor]
        public FocuserDockable(IProfileService profileService, IFocuserMediator focuser)
            : base(profileService) {

            // This will reference the resource dictionary to import the SVG graphic and assign it as the icon for the header bar
            var dict = new ResourceDictionary();
            dict.Source = new Uri("Cwseo.NINA.Focuser;component/FocuserDockables/FocuserDockableTemplates.xaml", UriKind.RelativeOrAbsolute);
            ImageGeometry = (System.Windows.Media.GeometryGroup)dict["Cwseo.NINA.Manualfocuser_SVG"];
            ImageGeometry.Freeze();

            Title = "Manual Focuser";
            TargetPosition = Properties.Settings.Default.TargetPosition;
            UserStep = Properties.Settings.Default.UserStep;
            InitializeSettings();
            focuserMediator = focuser;
            focuserMediator.RegisterConsumer(this);

            // Cancel
            HaltFocuserCommand = new global::NINA.Core.Utility.RelayCommand(_ =>
            {
                try { moveCts?.Cancel(); } catch { }
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
            try { moveCts?.Cancel(); } catch { }
            try { moveCts?.Dispose(); } catch { }
            try { focuserMediator?.RemoveConsumer(this); } catch { }
        }

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
        public void InitializeSettings() {
            if (FocuserInfo == null) {
                if (Properties.Settings.Default.TargetPosition == 0) {
                    TargetPosition = 1000;
                }
                if (Properties.Settings.Default.UserStep == 0) {
                    UserStep = 10;
                }
            } else {
                // 설정값이 0 또는 비어있는 경우
                if (Properties.Settings.Default.TargetPosition == 0) {
                    // 디바이스에서 값 가져오기
                    var devicePosition = FocuserInfo.Position;  // 디바이스에서 위치 가져오는 메서드
                    Properties.Settings.Default.TargetPosition = devicePosition; // 디바이스에서 받은 값으로 설정
                    RaisePropertyChanged(nameof(TargetPosition));
                }

                if (Properties.Settings.Default.UserStep == 0) {
                    // 디바이스에서 UserStep 값 가져오기 (예시)
                    var deviceStep = FocuserInfo.StepSize;  // 디바이스에서 step값 가져오는 메서드
                    Properties.Settings.Default.UserStep = (int)deviceStep;
                    RaisePropertyChanged(nameof(UserStep));
                }
                // 변경된 값을 저장
                Properties.Settings.Default.Save();
            }
        }

        // ---- IFocuserConsumer 나머지 메서드(필요없으면 비워도 됨) ----
        public void UpdateEndAutoFocusRun(AutoFocusInfo info) { }
        public void UpdateUserFocused(FocuserInfo info) { }
        public void NewAutoFocusPoint(OxyPlot.DataPoint dataPoint) { }
        public void AutoFocusRunStarting() { }

        private bool CanMove() {
            return FocuserInfo.Connected && !IsMoving;
        }

        private void ResetCts() {
            try { moveCts?.Cancel(); } catch { }
            try { moveCts?.Dispose(); } catch { }
            moveCts = new CancellationTokenSource();
        }

        private async Task<int> ExecuteMoveToAsync() {
            ResetCts();
            IsMoving = true;
            try {
                return await focuserMediator.MoveFocuser(Properties.Settings.Default.TargetPosition, moveCts.Token);
            } finally {
                IsMoving = false;
            }
        }

        private async Task<int> ExecuteMoveInAsync() {
            ResetCts();
            IsMoving = true;
            try {
                return await focuserMediator.MoveFocuserRelative(-Math.Abs(Properties.Settings.Default.UserStep), moveCts.Token);
            } finally {
                IsMoving = false;
            }
        }

        private async Task<int> ExecuteMoveOutAsync() {
            ResetCts();
            IsMoving = true;
            try {
                return await focuserMediator.MoveFocuserRelative(+Math.Abs(Properties.Settings.Default.UserStep), moveCts.Token);
            } finally {
                IsMoving = false;
            }
        }
    }
}
