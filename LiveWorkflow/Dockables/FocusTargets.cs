using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Core.Model;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Equipment.MyGuider.PHD2;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

namespace Cwseo.NINA.ManualFocuser.Dockables
{
    public partial class ManualFocuserDockableVM
    {
        private IProfileService focusProfileService;
        private CancellationTokenSource gotoCts;
        private bool isGoingToFocusTarget;
        private bool refreshingFocusTargets;
        private bool initialFocusTargetsRequested;
        private readonly IFocusTargetCatalogue targetCatalogue;
        private readonly Func<DateTime> targetUtcNow;
        private CancellationTokenSource targetSearchCts;
        private FocusTargetKind selectedTargetKind;
        private string focusTargetQuery = "";
        private long targetSearchVersion;
        private string rememberedTargetId;
        private Task<int> pendingTargetSearch = Task.FromResult(0);
        public IReadOnlyList<FocusTargetKind> TargetKinds { get; } = Enum.GetValues<FocusTargetKind>();
        public bool CanEditTargets => !disposed && !isGoingToFocusTarget;
        public bool IsStarTargetCategory => SelectedTargetKind == FocusTargetKind.Stars;
        public bool IsSearchingTargets => refreshingFocusTargets;
        public string TargetSearchHint => SelectedTargetKind switch
        {
            FocusTargetKind.Stars => "Choose a bright star using the altitude and magnitude filters. Smaller magnitude values select brighter stars.",
            FocusTargetKind.DeepSky => "Search NINA Sky Atlas by name or ID: M31, NGC 7000, IC 1805. Up to 100 matches; refine the search if needed.",
            _ => "Moon and planets; English or Korean names. Coordinates use the profile location and are refreshed just before Slew. Use Slew; plate solving does not center a planetary disk."
        };
        public FocusTargetKind SelectedTargetKind
        {
            get => selectedTargetKind;
            set
            {
                if (!CanEditTargets || !Enum.IsDefined(value) || selectedTargetKind == value) return;
                selectedTargetKind = value; focusTargetQuery = ""; rememberedTargetId = null;
                RaisePropertyChanged(); RaisePropertyChanged(nameof(IsStarTargetCategory));
                RaisePropertyChanged(nameof(TargetSearchHint)); RaisePropertyChanged(nameof(FocusTargetQuery));
                QueueTargetSearch(false);
            }
        }
        public string FocusTargetQuery
        {
            get => focusTargetQuery;
            set
            {
                if (!CanEditTargets || IsStarTargetCategory || focusTargetQuery == (value ?? "")) return;
                focusTargetQuery = value ?? ""; RaisePropertyChanged(); QueueTargetSearch(true);
            }
        }

        private void QueueTargetSearch(bool debounce)
        {
            initialFocusTargetsRequested = true;
            pendingTargetSearch = RunGuarded("Find targets", () => SearchFocusTargetsAsync(debounce));
        }

        public async Task EnsureFocusTargetsLoadedAsync()
        {
            if (initialFocusTargetsRequested || disposed) return;
            initialFocusTargetsRequested = true;
            await RunGuarded("Find targets", RefreshFocusTargetsAsync);
        }
        private FocusStarSuggestion selectedFocusTarget;
        private double minimumFocusAltitude = 45;
        private double maximumFocusMagnitude = 4;
        public AsyncObservableCollection<FocusStarSuggestion> FocusTargets { get; } = new();
        public ICommand RefreshFocusTargetsCommand { get; private set; }
        public ICommand GotoFocusTargetCommand { get; private set; }
        public ICommand SlewFocusTargetCommand { get; private set; }
        public ICommand CancelGotoCommand { get; private set; }
        public string FocusTargetStatus { get; private set; } = "Choose Stars, Deep sky or Solar system. Targets use the NINA profile location.";
        public bool IsGoingToFocusTarget => isGoingToFocusTarget;
        // The same profile setting as Options > Equipment > Telescope > No Sync.
        // Use a positive label here; do not keep an independent overriding value.
        public bool SyncMountOnCentering
        {
            get => !focusProfileService.ActiveProfile.TelescopeSettings.NoSync;
            set
            {
                if (disposed || !CanConfigureLive || SyncMountOnCentering == value) return;
                focusProfileService.ActiveProfile.TelescopeSettings.NoSync = !value;
                RaisePropertyChanged();
            }
        }
        private bool? lastSyncMountOnCentering;
        public double MinimumFocusAltitude
        {
            get => minimumFocusAltitude;
            set { if (!double.IsFinite(value)) return; minimumFocusAltitude = Math.Clamp(value, 15, 85); RaisePropertyChanged(); if (initialFocusTargetsRequested && IsStarTargetCategory && CanEditTargets) QueueTargetSearch(true); }
        }
        public double MaximumFocusMagnitude
        {
            get => maximumFocusMagnitude;
            set { if (!double.IsFinite(value)) return; maximumFocusMagnitude = Math.Clamp(value, -2, 20); RaisePropertyChanged(); if (initialFocusTargetsRequested && IsStarTargetCategory && CanEditTargets) QueueTargetSearch(true); }
        }
        public FocusStarSuggestion SelectedFocusTarget
        {
            get => selectedFocusTarget;
            set { if (!CanEditTargets) return; selectedFocusTarget = value; if (value != null) rememberedTargetId = value.Id ?? value.Name; RaisePropertyChanged(); RefreshGotoAvailability(); }
        }

        private void InitializeFocusTargets(IProfileService service)
        {
            focusProfileService = service;
            RefreshFocusTargetsCommand = new AsyncCommand<int>(() => RunGuarded("Find targets", RefreshFocusTargetsAsync), _ => CanEditTargets);
            GotoFocusTargetCommand = new AsyncCommand<int>(() => RunGuarded("Slew and center target", GotoFocusTargetAsync), _ => CanGotoFocusTarget());
            SlewFocusTargetCommand = new AsyncCommand<int>(() => RunGuarded("Slew to target", SlewFocusTargetAsync), _ => GotoUnavailableReason(false) == null);
            CancelGotoCommand = new RelayCommand(_ => gotoCts?.Cancel(), _ => isGoingToFocusTarget);
        }

        private string GotoUnavailableReason(bool center)
        {
            if (disposed) return "The focus panel is closed.";
            if (isGoingToFocusTarget) return "Target movement is running. Use Cancel to stop.";
            if (refreshingFocusTargets) return "Wait for the target search to finish.";
            if (SelectedFocusTarget == null) return "Select a target. Search by name or use Refresh if the list is empty.";
            if (center && SelectedFocusTarget.Kind == FocusTargetKind.SolarSystem)
                return "Use Slew for the Moon and planets. Stellar plate solving does not center the visible disk.";
            if (TelescopeInfo?.Connected != true) return "Connect the mount.";
            if (TelescopeInfo.AtPark) return "Unpark the mount in NINA's telescope controls.";
            if (TelescopeInfo.Slewing) return "Wait for the mount slew to finish.";
            if (IsMoving) return "Wait for focus movement to finish.";
            if (IsCapturing) return "Stop live focus or wait for the current capture to finish.";
            if (center && CameraInfo?.Connected != true) return "Connect the camera for plate-solve centering.";
            if (CameraInfo?.Connected == true && !cameraMediator.IsFreeToCapture(this))
                return "The camera is in use by another NINA operation. Stop it or wait for completion.";
            return null;
        }

        public string GotoFocusTargetTooltip => GotoUnavailableReason(true) ??
            "Slew to the selected star or deep-sky object, then plate solve and center using NINA's Plate Solving settings. " +
            "A connected guider is stopped before the slew and stays stopped for focusing. Remove the Bahtinov mask for solving.";
        public string SlewFocusTargetTooltip => GotoUnavailableReason(false) ??
            "Slew to the selected coordinates without capturing, plate solving or syncing. A connected guider is stopped first. Centering is not verified.";

        private bool CanGotoFocusTarget() => GotoUnavailableReason(true) == null;

        // Idle/disconnected guiders need no stop request. StopGuiding returning
        // false can also mean a disconnect or an already-idle state, not failure.
        private bool GuiderIsIdle()
        {
            string state = (guiderMediator.GetDevice() as IGuider)?.State;
            return state == PhdAppState.STOPPED || state == PhdAppState.LOOPING || state == PhdAppState.SELECTED;
        }

        private async Task PrepareGuiderForSlewAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (guiderMediator.GetInfo()?.Connected != true || GuiderIsIdle()) return;
            bool stopped;
            try { stopped = await guiderMediator.StopGuiding(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                token.ThrowIfCancellationRequested();
                if (guiderMediator.GetInfo()?.Connected != true || GuiderIsIdle()) return;
                throw new InvalidOperationException("Could not stop guiding. Stop guiding in NINA/PHD2 and retry GOTO.", e);
            }
            token.ThrowIfCancellationRequested();
            if (!stopped && guiderMediator.GetInfo()?.Connected == true && !GuiderIsIdle())
                throw new InvalidOperationException("Could not stop guiding. Stop guiding in NINA/PHD2 and retry GOTO.");
        }

        private void RefreshGotoAvailability()
        {
            if (focusProfileService != null && lastSyncMountOnCentering != SyncMountOnCentering)
            {
                lastSyncMountOnCentering = SyncMountOnCentering;
                RaisePropertyChanged(nameof(SyncMountOnCentering));
            }
            string reason = GotoUnavailableReason(true) + "|" + GotoUnavailableReason(false);
            if (reason == lastGotoUnavailableReason) return;
            lastGotoUnavailableReason = reason;
            RaisePropertyChanged(nameof(GotoFocusTargetTooltip));
            RaisePropertyChanged(nameof(SlewFocusTargetTooltip));
            CommandManager.InvalidateRequerySuggested();
        }

        private FocusStarSuggestion CalculateSuggestion(FocusStarSuggestion target)
        {
            var site = focusProfileService.ActiveProfile.AstrometrySettings;
            return FocusTargetPlanner.At(target, site.Latitude, site.Longitude, site.Elevation, targetUtcNow());
        }

        private bool AboveFocusHorizon(FocusStarSuggestion star)
        {
            var horizon = focusProfileService.ActiveProfile.AstrometrySettings.Horizon;
            return FocusTargetPlanner.IsAboveHorizon(star, MinimumFocusAltitude, horizon?.GetAltitude(star.Azimuth) ?? 0);
        }

        public Task<int> RefreshFocusTargetsAsync() => SearchFocusTargetsAsync(false);

        private async Task<int> SearchFocusTargetsAsync(bool debounce)
        {
            if (!CanEditTargets) return 0;
            targetSearchCts?.Cancel();
            var request = new CancellationTokenSource();
            targetSearchCts = request;
            long version = ++targetSearchVersion;
            var kind = SelectedTargetKind;
            string query = IsStarTargetCategory ? "" : FocusTargetQuery;
            string selectedId = SelectedFocusTarget?.Id ?? SelectedFocusTarget?.Name ?? rememberedTargetId;
            double altitude = MinimumFocusAltitude, magnitude = MaximumFocusMagnitude;
            var site = focusProfileService.ActiveProfile.AstrometrySettings;
            double latitude = site.Latitude, longitude = site.Longitude, elevation = site.Elevation;
            var horizon = site.Horizon;
            refreshingFocusTargets = true;
            SelectedFocusTarget = null;
            FocusTargets.Clear();
            FocusTargetStatus = "Finding targets…";
            RaisePropertyChanged(nameof(IsSearchingTargets));
            RaisePropertyChanged(nameof(FocusTargetStatus));
            RefreshGotoAvailability();
            try
            {
                if (debounce) await Task.Delay(350, request.Token);
                var result = await Task.Run(async () =>
                {
                    var catalogue = await targetCatalogue.SearchAsync(kind, query, request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    DateTime utc = targetUtcNow();
                    var matching = catalogue.Where(t => t.Kind == kind &&
                        (kind == FocusTargetKind.DeepSky || FocusTargetPlanner.Matches(t, query))).Select(t =>
                    {
                        request.Token.ThrowIfCancellationRequested();
                        return FocusTargetPlanner.At(t, latitude, longitude, elevation, utc);
                    }).ToArray();
                    var visible = kind == FocusTargetKind.Stars
                        ? FocusStarPlanner.SelectVisibleStars(matching, altitude, magnitude, az => horizon?.GetAltitude(az) ?? 0)
                        : matching.OrderByDescending(t => t.Altitude).ThenBy(t => t.Name).ToList();
                    return (Targets: visible, Count: catalogue.Count);
                }, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (disposed || version != targetSearchVersion) return 0;
                foreach (var target in result.Targets) FocusTargets.Add(target);
                SelectedFocusTarget = result.Targets.FirstOrDefault(t => (t.Id ?? t.Name) == selectedId) ?? result.Targets.FirstOrDefault();
                FocusTargetStatus = kind switch
                {
                    FocusTargetKind.Stars => $"{result.Targets.Count}/{result.Count} catalogue stars · alt ≥ {altitude:F0}° · mag ≤ {magnitude:F1} · horizon +5°.",
                    FocusTargetKind.DeepSky => $"{result.Targets.Count} Sky Atlas matches" + (result.Count >= NinaFocusTargetCatalogue.DeepSkyLimit ? " · limit 100; refine search" : "") + ". No star brightness filter; GOTO requires horizon +5°.",
                    _ => $"{result.Targets.Count} Moon/planet targets · current topocentric coordinates. Slew requires horizon +5°."
                };
                if (result.Targets.Count == 0) FocusTargetStatus = "No matching targets. " + (kind == FocusTargetKind.Stars ? "Adjust search, altitude or magnitude and Refresh." : "Try another name or catalogue ID.");
                RaisePropertyChanged(nameof(FocusTargetStatus));
                return result.Targets.Count;
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { return 0; }
            catch
            {
                if (!disposed && version == targetSearchVersion)
                {
                    FocusTargetStatus = "Could not load targets. Check NINA's profile location, catalogue and ephemeris, then Refresh.";
                    RaisePropertyChanged(nameof(FocusTargetStatus));
                    throw;
                }
                return 0;
            }
            finally
            {
                if (ReferenceEquals(targetSearchCts, request)) targetSearchCts = null;
                request.Dispose();
                if (!disposed && version == targetSearchVersion)
                {
                    refreshingFocusTargets = false;
                    RaisePropertyChanged(nameof(IsSearchingTargets));
                    RefreshGotoAvailability(); CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private Task<int> GotoFocusTargetAsync() => MoveFocusTargetAsync(true);
        private Task<int> SlewFocusTargetAsync() => MoveFocusTargetAsync(false);
        private async Task<int> MoveFocusTargetAsync(bool center)
        {
            if (GotoUnavailableReason(center) != null) return 0;
            var selectedTarget = SelectedFocusTarget;
            var target = CalculateSuggestion(selectedTarget);
            if (!AboveFocusHorizon(target)) throw new InvalidOperationException("Target is below the current altitude/horizon limit. Refresh the list.");
            if (target.Kind == FocusTargetKind.Stars && (!double.IsFinite(target.Magnitude) || target.Magnitude > MaximumFocusMagnitude))
                throw new InvalidOperationException("Star is outside the current magnitude limit. Refresh the list.");
            // Snapshot all capture/solver settings and resolve the configured solver
            // before moving the mount. These are NINA's own Center services/exports.
            FocusTargetCentering centering = null;
            ICenteringSolver solver = null;
            string cameraId = CameraInfo?.DeviceId;
            if (center)
            {
                var profile = focusProfileService.ActiveProfile;
                centering = new FocusTargetCentering(profile, cameraMediator.GetInfo(), target.Coordinates);
                solver = plateSolverFactory.GetCenteringSolver(
                    plateSolverFactory.GetPlateSolver(profile.PlateSolveSettings),
                    plateSolverFactory.GetBlindSolver(profile.PlateSolveSettings),
                    imagingMediator, telescopeMediator, filterWheelMediator, domeMediator, domeFollower);
            }
            bool guidingPrepared = false;
            void CheckDevices()
            {
                var camera = center ? cameraMediator.GetInfo() : null;
                var mount = telescopeMediator.GetInfo();
                if (disposed || (center && (camera?.Connected != true || camera.DeviceId != cameraId)) ||
                    mount?.Connected != true || mount.AtPark)
                    throw new InvalidOperationException("Camera/mount state changed during target movement. GOTO stopped.");
                if (guidingPrepared && guiderMediator.GetInfo()?.Connected == true)
                {
                    string state = (guiderMediator.GetDevice() as IGuider)?.State;
                    if (state == PhdAppState.GUIDING || state == PhdAppState.CALIBRATING || state == PhdAppState.LOSTLOCK || state == PhdAppState.PAUSED)
                        throw new InvalidOperationException("Guiding resumed during target movement. Stop guiding and retry GOTO.");
                }
            }
            if (center) solver.CaptureSolver = new GuardedFocusCaptureSolver(solver.CaptureSolver, CheckDevices);
            gotoCts = new CancellationTokenSource();
            var runCts = gotoCts;
            isGoingToFocusTarget = true;
            RaisePropertyChanged(nameof(IsGoingToFocusTarget));
            RaisePropertyChanged(nameof(CanEditTargets));
            RefreshGotoAvailability();
            RaisePropertyChanged(nameof(CanConfigureLive)); RaisePropertyChanged(nameof(CanUseFocusStreaming));
            CommandManager.InvalidateRequerySuggested();
            bool ownsCaptureBlock = false;
            bool slewCompleted = false;
            int guidingRestarted = 0;
            Func<object, EventArgs, Task> onGuidingStarted = (_, _) =>
            {
                Interlocked.Exchange(ref guidingRestarted, 1);
                try { runCts.Cancel(); } catch (ObjectDisposedException) { }
                return Task.CompletedTask;
            };
            bool observingGuiding = false;
            void ReportStatus(string status)
            {
                FocusTargetStatus = status;
                RaisePropertyChanged(nameof(FocusTargetStatus));
                applicationStatusMediator.StatusUpdate(new ApplicationStatus { Source = "Live Focus", Status = status });
            }
            var progress = new Progress<ApplicationStatus>(status =>
            {
                // Progress<T> reports can still be queued after completion/cancel.
                if (!disposed && ReferenceEquals(gotoCts, runCts) && !runCts.IsCancellationRequested && !string.IsNullOrWhiteSpace(status.Status))
                    ReportStatus($"Centering {target.Name} | {status.Status}");
            });
            try
            {
                if (center || CameraInfo?.Connected == true)
                {
                    cameraMediator.RegisterCaptureBlock(this);
                    ownsCaptureBlock = true;
                }
                CheckDevices(); runCts.Token.ThrowIfCancellationRequested();
                guiderMediator.GuidingStarted += onGuidingStarted;
                observingGuiding = true;
                if (guiderMediator.GetInfo()?.Connected == true)
                {
                    ReportStatus("Stopping guiding before target GOTO…");
                    await PrepareGuiderForSlewAsync(runCts.Token);
                }
                guidingPrepared = true;
                runCts.Token.ThrowIfCancellationRequested(); CheckDevices();
                // Discard images from the previous field so ROI selection cannot
                // accidentally reuse a pre-slew star image (also on failed solves).
                lock (preparedOverviewLock) { preparedOverview = null; preparedOverviewCamera = null; }
                overviewPixels = null; overviewImage = null; RoiSelectionPreview = null; FocusPreviewImage = null;
                IsSelectingRoi = false;
                RaisePropertyChanged(nameof(RoiSelectionPreview)); RaisePropertyChanged(nameof(FocusPreviewImage)); RaisePropertyChanged(nameof(LiveDisplayImage));
                ResetSharedFocusMeasurements();
                // Guiding preparation can take time. Refresh moving-body coordinates
                // and all target horizon checks immediately before the actual slew.
                target = CalculateSuggestion(selectedTarget);
                if (!AboveFocusHorizon(target)) throw new InvalidOperationException("Target moved below the current altitude/horizon limit. Refresh the list.");
                if (center) centering.Parameter.Coordinates = target.Coordinates;
                ReportStatus($"Moving to {target.Name}…");
                bool success = await telescopeMediator.SlewToCoordinatesAsync(target.Coordinates, gotoCts.Token);
                if (!success) throw new InvalidOperationException("NINA reported that the slew did not complete.");
                slewCompleted = true;
                runCts.Token.ThrowIfCancellationRequested(); CheckDevices();
                var dome = domeMediator.GetInfo();
                if (dome?.Connected == true && dome.CanSetAzimuth && !domeFollower.IsFollowing)
                {
                    ReportStatus(center ? "Synchronizing dome before plate solving…" : "Synchronizing dome…");
                    if (!await domeFollower.TriggerTelescopeSync()) throw new InvalidOperationException("Dome synchronization did not complete.");
                }
                runCts.Token.ThrowIfCancellationRequested(); CheckDevices();
                if (!center)
                {
                    ReportStatus($"Slewed to {target.Name}. Centering was not requested or verified.");
                    return 1;
                }
                ReportStatus($"Centering {target.Name} | Full-frame plate solving ({centering.Sequence.ExposureTime:F1} s)…");
                double error = await centering.CenterAsync(solver, NullProgress<PlateSolveProgress>.Instance, progress, runCts.Token);
                CheckDevices();
                PreviewCenterX = 50; PreviewCenterY = 50;
                if (Properties.Settings.Default.EnableFocusDiagnostics) Logger.Info($"[LiveFocus/Center] {target.Name} centered; error {error:F1} arcsec, tolerance {centering.Parameter.Threshold * 60:F1} arcsec.");
                ReportStatus($"{target.Name} centered · error {error:F1}″ (tolerance {centering.Parameter.Threshold * 60:F1}″). Focus ROI moved to image center.");
                return 1;
            }
            catch (OperationCanceledException)
            {
                ReportStatus(Volatile.Read(ref guidingRestarted) != 0 ?
                    "Guiding restarted; GOTO/centering stopped. Target centering was not verified." :
                    "GOTO/centering stopped. Target centering was not verified.");
                return 0;
            }
            catch (Exception e)
            {
                ReportStatus((slewCompleted ? center ? "Slew completed; centering failed. " : "Slew completed; post-slew operation failed. " : "GOTO did not complete. ") + e.Message);
                throw;
            }
            finally
            {
                if (observingGuiding) guiderMediator.GuidingStarted -= onGuidingStarted;
                if (ownsCaptureBlock)
                {
                    try { cameraMediator.ReleaseCaptureBlock(this); } catch (Exception e) { Logger.Error("[LiveFocus] Could not release GOTO camera reservation", e); }
                }
                gotoCts.Dispose(); gotoCts = null;
                isGoingToFocusTarget = false;
                RefreshGotoAvailability();
                applicationStatusMediator.StatusUpdate(new ApplicationStatus { Source = "Live Focus", Status = string.Empty });
                RaisePropertyChanged(nameof(IsGoingToFocusTarget));
                RaisePropertyChanged(nameof(CanEditTargets));
                RaisePropertyChanged(nameof(CanConfigureLive)); RaisePropertyChanged(nameof(CanUseFocusStreaming));
                RaisePropertyChanged(nameof(FocusTargetStatus));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
