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

        private int targetPosition = 100;
        private int userStep = 5;

        private bool isBusy;
        private bool _moving;
        public FocuserInfo FocuserInfo { get; private set; } 

        public int TargetPosition {
            get => targetPosition;
            set { targetPosition = value; RaisePropertyChanged(nameof(TargetPosition)); }
        }

        public int UserStep {
            get => userStep;
            set { userStep = value; RaisePropertyChanged(nameof(UserStep)); }
        }

        public bool IsBusy {
            get => isBusy;
            private set {
                isBusy = value;
                RaisePropertyChanged(nameof(IsBusy));
                RaisePropertyChanged(nameof(IsNotBusy));

                // ✅ RaiseCanExecuteChanged() 대신 이걸로 갱신
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsMoving {
            get => _moving;
            set {
                _moving = value;
                RaisePropertyChanged(nameof(IsMoving));
            }
        }

        public bool IsNotBusy => !IsBusy;

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

            focuserMediator = focuser;
            focuserMediator.RegisterConsumer(this);

            // Cancel
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

                FocuserInfo = deviceInfo;
                RaisePropertyChanged(nameof(FocuserInfo));
        }

        // ---- IFocuserConsumer 나머지 메서드(필요없으면 비워도 됨) ----
        public void UpdateEndAutoFocusRun(AutoFocusInfo info) { }
        public void UpdateUserFocused(FocuserInfo info) { }
        public void NewAutoFocusPoint(OxyPlot.DataPoint dataPoint) { }
        public void AutoFocusRunStarting() { }

        private bool CanMove() {
            return FocuserInfo.Connected && !IsBusy;
        }

        private void ResetCts() {
            try { moveCts?.Cancel(); } catch { }
            try { moveCts?.Dispose(); } catch { }
            moveCts = new CancellationTokenSource();
        }

        private async Task<int> ExecuteMoveToAsync() {
            ResetCts();
            IsBusy = true;
            IsMoving = true;
            try {
                return await focuserMediator.MoveFocuser(TargetPosition, moveCts.Token);
            } finally {
                IsBusy = false;
                IsMoving = false;
            }
        }

        private async Task<int> ExecuteMoveInAsync() {
            ResetCts();
            IsBusy = true;
            IsMoving = true;
            try {
                return await focuserMediator.MoveFocuserRelative(-Math.Abs(UserStep), moveCts.Token);
            } finally {
                IsBusy = false;
                IsMoving = false;
            }
        }

        private async Task<int> ExecuteMoveOutAsync() {
            ResetCts();
            IsBusy = true;
            IsMoving = true;
            try {
                return await focuserMediator.MoveFocuserRelative(+Math.Abs(UserStep), moveCts.Token);
            } finally {
                IsBusy = false;
                IsMoving = false;
            }
        }
    }
}
