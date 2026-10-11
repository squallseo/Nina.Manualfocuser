using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Core.Utility;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;

namespace Cwseo.NINA.ManualFocuser.Dockables
{
    internal sealed class NullProgress<T> : IProgress<T>
    {
        public static readonly NullProgress<T> Instance = new();
        public void Report(T value) { }
    }
    [Export(typeof(IDockableVM))]
    public partial class ManualFocuserDockableVM : DockableVM, IDisposable,
        ICameraConsumer, IFocuserConsumer, ITelescopeConsumer, IFilterWheelConsumer, IGuiderConsumer
    {
        private readonly ICameraMediator cameraMediator;
        private readonly IImagingMediator imagingMediator;
        private readonly IFilterWheelMediator filterWheelMediator;
        private readonly IFocuserMediator focuserMediator;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IGuiderMediator guiderMediator;
        private readonly IDomeMediator domeMediator;
        private readonly IDomeFollower domeFollower;
        private readonly IPlateSolverFactory plateSolverFactory;
        private readonly IApplicationStatusMediator applicationStatusMediator;
        private readonly LiveFocusModel DataModel;
        private CancellationTokenSource moveCts;
        private bool disposed, moving, capturing, lastFocuserConnected;
        private string lastGotoUnavailableReason;
        public FocuserInfo FocuserInfo { get; private set; }
        public CameraInfo CameraInfo { get; private set; }
        public bool IsCameraConnected => CameraInfo?.Connected == true;
        public string CameraConnectionText => IsCameraConnected ? "Connected" : "Disconnected";
        public string LiveFrameDetails { get; private set; } = "Camera connection. Frame timing will appear after starting live focus.";
        public TelescopeInfo TelescopeInfo { get; private set; }
        public FilterWheelInfo FilterwheelInfo { get; private set; }
        public GuiderInfo GuiderInfo { get; private set; }
        public int TargetPosition
        {
            get => Properties.Settings.Default.TargetPosition;
            set { Properties.Settings.Default.TargetPosition = Math.Max(0, value); Properties.Settings.Default.Save(); RaisePropertyChanged(); }
        }
        public int UserStep
        {
            get => Properties.Settings.Default.UserStep;
            set { Properties.Settings.Default.UserStep = Math.Clamp(value, 1, 10000); Properties.Settings.Default.Save(); RaisePropertyChanged(); }
        }
        public bool IsMoving
        {
            get => moving;
            set { if (moving == value) return; moving = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(CanConfigureLive)); RefreshGotoAvailability(); CommandManager.InvalidateRequerySuggested(); }
        }
        public bool IsCapturing
        {
            get => capturing;
            set { if (capturing == value) return; capturing = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(CanConfigureLive)); RefreshGotoAvailability(); CommandManager.InvalidateRequerySuggested(); }
        }
        public ICommand MoveToPositionCommand { get; }
        public ICommand MoveINCommand { get; }
        public ICommand MoveOUTCommand { get; }
        public ICommand HaltFocuserCommand { get; }
        public override bool IsTool { get; } = true;
        [ImportingConstructor]
        public ManualFocuserDockableVM(IProfileService profiles, ICameraMediator camera, IImagingMediator imaging,
            IFilterWheelMediator wheel, IFocuserMediator focuser, ITelescopeMediator telescope, IGuiderMediator guider,
            IDomeMediator dome, IDomeFollower follower, IPlateSolverFactory solver, IApplicationStatusMediator status)
            : this(profiles, camera, imaging, wheel, focuser, telescope, guider, dome, follower, solver, status,
                new NinaFocusTargetCatalogue(), () => DateTime.UtcNow) { }

        public ManualFocuserDockableVM(IProfileService profiles, ICameraMediator camera, IImagingMediator imaging,
            IFilterWheelMediator wheel, IFocuserMediator focuser, ITelescopeMediator telescope, IGuiderMediator guider,
            IDomeMediator dome, IDomeFollower follower, IPlateSolverFactory solver, IApplicationStatusMediator status,
            IFocusTargetCatalogue catalogue, Func<DateTime> utcNow) : base(profiles)
        {
            targetCatalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
            targetUtcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            cameraMediator = camera; imagingMediator = imaging; filterWheelMediator = wheel; focuserMediator = focuser;
            telescopeMediator = telescope; guiderMediator = guider; domeMediator = dome; domeFollower = follower;
            plateSolverFactory = solver; applicationStatusMediator = status;
            Title = "Manual Focuser";
            var dictionary = new ResourceDictionary { Source = new Uri("Cwseo.NINA.ManualFocuser;component/LiveWorkflow/Dockables/ManualFocuserDockableTemplates.xaml", UriKind.RelativeOrAbsolute) };
            ImageGeometry = (System.Windows.Media.GeometryGroup)dictionary["Cwseo.NINA.ManualFocuser_SVG"]; ImageGeometry.Freeze();
            DataModel = new LiveFocusModel(profiles, imaging, camera);
            InitializeFocusTargets(profiles); InitializeFocusAssist(); ObservePreparedImages(imaging);
            MoveToPositionCommand = new AsyncCommand<int>(() => RunGuarded("Move to position", () => MoveStandaloneAsync(null)), _ => CanMove());
            MoveINCommand = new AsyncCommand<int>(() => RunGuarded("Move in", () => MoveStandaloneAsync(-UserStep)), _ => CanMove());
            MoveOUTCommand = new AsyncCommand<int>(() => RunGuarded("Move out", () => MoveStandaloneAsync(UserStep)), _ => CanMove());
            HaltFocuserCommand = new RelayCommand(_ => { assistCts?.Cancel(); moveCts?.Cancel(); });
            camera.RegisterConsumer(this); focuser.RegisterConsumer(this); telescope.RegisterConsumer(this); wheel.RegisterConsumer(this); guider.RegisterConsumer(this);
        }
        private bool CanMove() => !disposed && FocuserInfo?.Connected == true && !FocuserInfo.IsMoving && !IsMoving && !IsCapturing && !IsGoingToFocusTarget && !IsSelectingRoi;
        private async Task<int> MoveStandaloneAsync(int? relative)
        {
            if (!CanMove()) return 0;
            string id = FocuserInfo.DeviceId;
            long requested = relative is int distance ? (long)FocuserInfo.Position + distance : TargetPosition;
            int maximum = (focuserMediator.GetDevice() as IFocuser)?.MaxStep ?? int.MaxValue;
            if (requested < 0 || requested > (maximum > 0 ? maximum : int.MaxValue)) throw new InvalidOperationException("Target is outside the focuser travel range.");
            moveCts?.Dispose(); moveCts = new CancellationTokenSource(); IsMoving = true;
            try
            {
                var device = focuserMediator.GetInfo();
                if (device?.Connected != true || device.DeviceId != id || device.IsMoving) throw new InvalidOperationException("Focuser disconnected, changed or busy before movement.");
                int actual = relative is int amount ? await focuserMediator.MoveFocuserRelative(amount, moveCts.Token) : await focuserMediator.MoveFocuser((int)requested, moveCts.Token);
                moveCts.Token.ThrowIfCancellationRequested();
                var current = focuserMediator.GetInfo();
                if (current?.Connected != true || current.DeviceId != id || actual != requested) throw new InvalidOperationException("Focuser did not reach the requested position.");
                FocuserInfo = current; RaisePropertyChanged(nameof(FocuserInfo)); return 1;
            }
            finally { IsMoving = false; }
        }
        private async Task<int> RunGuarded(string operation, Func<Task<int>> action)
        {
            try { return await action(); }
            catch (OperationCanceledException) { return 0; }
            catch (Exception error) { Logger.Error($"[LiveFocus] {operation} failed", error); Notification.ShowError($"Live Focus: {operation} failed - {error.Message}"); return 0; }
        }
        private void ApplyOnUiThread(Action action)
        {
            if (disposed) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action);
        }
        public void UpdateDeviceInfo(FocuserInfo info) { if (info == null) return; ApplyOnUiThread(() => { FocuserInfo = info; RaisePropertyChanged(nameof(FocuserInfo)); if (info.Connected != lastFocuserConnected) { lastFocuserConnected = info.Connected; CommandManager.InvalidateRequerySuggested(); } }); }
        public void UpdateDeviceInfo(CameraInfo info) { if (info == null) return; ApplyOnUiThread(() => { CameraInfo = info; RaisePropertyChanged(nameof(CameraInfo)); RaisePropertyChanged(nameof(IsCameraConnected)); RaisePropertyChanged(nameof(CameraConnectionText)); RaisePropertyChanged(nameof(PreviewRoiRectangle)); RaisePropertyChanged(nameof(PreviewRoiPreset)); RaisePropertyChanged(nameof(HasRoiSensorDimensions)); RaisePropertyChanged(nameof(CanUseFocusStreaming)); RefreshGotoAvailability(); }); }
        public void UpdateDeviceInfo(TelescopeInfo info) { if (info == null) return; ApplyOnUiThread(() => { TelescopeInfo = info; RaisePropertyChanged(nameof(TelescopeInfo)); RefreshGotoAvailability(); }); }
        public void UpdateDeviceInfo(FilterWheelInfo info) { if (info == null) return; ApplyOnUiThread(() => { FilterwheelInfo = info; RaisePropertyChanged(nameof(FilterwheelInfo)); }); }
        public void UpdateDeviceInfo(GuiderInfo info) { if (info == null) return; ApplyOnUiThread(() => { GuiderInfo = info; RaisePropertyChanged(nameof(GuiderInfo)); RefreshGotoAvailability(); }); }
        public void UpdateEndAutoFocusRun(AutoFocusInfo info) { }
        public void UpdateUserFocused(FocuserInfo info) { UpdateDeviceInfo(info); }
        public void Dispose()
        {
            if (disposed) return; disposed = true; targetSearchCts?.Cancel(); stretchRefresh?.Cancel(); assistCts?.Cancel(); moveCts?.Cancel(); gotoCts?.Cancel(); StopObservingPreparedImages();
            // Disposal already sets disposed: bypass the update guard for UI cleanup.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            void CleanViews() { StopMainRoiEditor(); StopNinaHfrHistory(); }
            if (dispatcher == null || dispatcher.CheckAccess()) CleanViews();
            else dispatcher.BeginInvoke(new Action(CleanViews));
            try { cameraMediator.RemoveConsumer(this); } catch (Exception e) { Logger.Error("Live Focus camera consumer cleanup failed", e); }
            try { focuserMediator.RemoveConsumer(this); } catch (Exception e) { Logger.Error("Live Focus focuser consumer cleanup failed", e); }
            try { telescopeMediator.RemoveConsumer(this); } catch (Exception e) { Logger.Error("Live Focus telescope consumer cleanup failed", e); }
            try { filterWheelMediator.RemoveConsumer(this); } catch (Exception e) { Logger.Error("Live Focus filter consumer cleanup failed", e); }
            try { guiderMediator.RemoveConsumer(this); } catch (Exception e) { Logger.Error("Live Focus guider consumer cleanup failed", e); }
            GC.SuppressFinalize(this);
        }
    }
}
