using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using Cwseo.NINA.ManualFocuser.Models;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        private IProfileService focusProfileService;
        private CancellationTokenSource gotoCts;
        private bool isGoingToFocusTarget;
        private bool refreshingFocusTargets;
        private FocusStarSuggestion selectedFocusTarget;
        private double minimumFocusAltitude = 45;
        public AsyncObservableCollection<FocusStarSuggestion> FocusTargets { get; } = new();
        public ICommand RefreshFocusTargetsCommand { get; private set; }
        public ICommand GotoFocusTargetCommand { get; private set; }
        public ICommand CancelGotoCommand { get; private set; }
        public string FocusTargetStatus { get; private set; } = "Refresh to find bright stars using the NINA profile location.";
        public bool IsGoingToFocusTarget => isGoingToFocusTarget;
        public double MinimumFocusAltitude {
            get => minimumFocusAltitude;
            set { if (!double.IsFinite(value)) return; minimumFocusAltitude = Math.Clamp(value, 15, 85); RaisePropertyChanged(); }
        }
        public FocusStarSuggestion SelectedFocusTarget {
            get => selectedFocusTarget;
            set { selectedFocusTarget = value; RaisePropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
        }

        private void InitializeFocusTargets(IProfileService service) {
            focusProfileService = service;
            RefreshFocusTargetsCommand = new AsyncCommand<int>(() => RunGuarded("Find focus stars", RefreshFocusTargetsAsync), _ => !refreshingFocusTargets && !isGoingToFocusTarget);
            GotoFocusTargetCommand = new AsyncCommand<int>(() => RunGuarded("Goto focus star", GotoFocusTargetAsync), _ => CanGotoFocusTarget());
            CancelGotoCommand = new RelayCommand(_ => gotoCts?.Cancel(), _ => isGoingToFocusTarget);
        }

        private bool CanGotoFocusTarget() => !disposed && SelectedFocusTarget != null &&
            TelescopeInfo?.Connected == true && !TelescopeInfo.Slewing && !TelescopeInfo.AtPark && !IsMoving && !IsCapturing &&
            !isGoingToFocusTarget && GuiderInfo?.Connected != true && cameraMediator.IsFreeToCapture(this);

        private void RefreshGotoAvailability() {
            int state = (TelescopeInfo?.Connected == true ? 1 : 0) |
                (TelescopeInfo?.Slewing == true ? 2 : 0) | (TelescopeInfo?.AtPark == true ? 4 : 0) |
                (CameraInfo?.Connected == true ? 8 : 0) | (GuiderInfo?.Connected == true ? 16 : 0);
            if (state == lastGotoDeviceState) return;
            lastGotoDeviceState = state;
            CommandManager.InvalidateRequerySuggested();
        }

        private FocusStarSuggestion CalculateSuggestion(string name, Coordinates coordinates, double magnitude) {
            var site = focusProfileService.ActiveProfile.AstrometrySettings;
            return FocusStarPlanner.Calculate(name, coordinates, magnitude, site.Latitude, site.Longitude, site.Elevation, DateTime.UtcNow);
        }

        private bool AboveFocusHorizon(FocusStarSuggestion star) {
            var horizon = focusProfileService.ActiveProfile.AstrometrySettings.Horizon;
            return FocusStarPlanner.IsAboveHorizon(star, MinimumFocusAltitude, horizon?.GetAltitude(star.Azimuth) ?? 0);
        }

        private async Task<int> RefreshFocusTargetsAsync() {
            refreshingFocusTargets = true;
            try {
                var stars = await new DatabaseInteraction().GetBrightStars();
                if (disposed) return 0;
                var suggestions = stars.Where(s => s.Coordinates != null && double.IsFinite(s.Magnitude) && s.Magnitude <= 4)
                    .Select(s => CalculateSuggestion(s.Name, s.Coordinates, s.Magnitude))
                    .Where(AboveFocusHorizon).OrderByDescending(s => s.Altitude).ThenBy(s => s.Magnitude).Take(20).ToList();
                FocusTargets.Clear();
                foreach (var star in suggestions) FocusTargets.Add(star);
                SelectedFocusTarget = suggestions.FirstOrDefault();
                var site = focusProfileService.ActiveProfile.AstrometrySettings;
                FocusTargetStatus = $"{suggestions.Count} stars · site {site.Latitude:F4}, {site.Longitude:F4} · {DateTimeOffset.Now:HH:mm:ss zzz}. Check exposure saturation after GOTO.";
                RaisePropertyChanged(nameof(FocusTargetStatus));
                return suggestions.Count;
            } finally { refreshingFocusTargets = false; CommandManager.InvalidateRequerySuggested(); }
        }

        private async Task<int> GotoFocusTargetAsync() {
            if (!CanGotoFocusTarget()) return 0;
            var target = CalculateSuggestion(SelectedFocusTarget.Name, SelectedFocusTarget.Coordinates, SelectedFocusTarget.Magnitude);
            if (!AboveFocusHorizon(target)) throw new InvalidOperationException("Star is below the current altitude/horizon limit. Refresh the list.");
            gotoCts = new CancellationTokenSource();
            isGoingToFocusTarget = true;
            RaisePropertyChanged(nameof(IsGoingToFocusTarget));
            CommandManager.InvalidateRequerySuggested();
            // Hold the camera reservation so a plugin focus exposure cannot start
            // during the slew. The GOTO itself runs only after the user's button press.
            try {
                cameraMediator.RegisterCaptureBlock(this);
                FocusTargetStatus = $"Moving to {target.Name}…";
                RaisePropertyChanged(nameof(FocusTargetStatus));
                bool success = await telescopeMediator.SlewToCoordinatesAsync(target.Coordinates, gotoCts.Token);
                if (!success) throw new InvalidOperationException("NINA reported that the slew did not complete.");
                DataModel.ResetPlotData();
                RaiseMeasurementProperties();
                FocusTargetStatus = $"At {target.Name}. Take a short exposure and check saturation/spike detection.";
                return 1;
            } catch {
                FocusTargetStatus = "GOTO did not complete. Check mount status before capturing.";
                throw;
            } finally {
                try { cameraMediator.ReleaseCaptureBlock(this); } catch (Exception e) { Logger.Error("[ManualFocuser] Could not release GOTO camera reservation", e); }
                gotoCts.Dispose(); gotoCts = null;
                isGoingToFocusTarget = false;
                RaisePropertyChanged(nameof(IsGoingToFocusTarget));
                RaisePropertyChanged(nameof(FocusTargetStatus));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
