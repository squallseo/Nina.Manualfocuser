using System.Reflection;
using System.IO;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.Models;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

internal static class VmChecks {
    public static void Run(Action<bool, string> check) {
        Exception failure = null;
        var thread = new Thread(() => {
            try { RunOnSta(check); } catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new Exception("GOTO integration checks failed", failure);
    }

    private static void RunOnSta(Action<bool, string> check) {
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Database", "Migration"));
        // Strict fakes: all capture, movement and MEF factory calls stay in this
        // process; no NINA instance, SDK handle or equipment connection is opened.
        var ps = Fake.Properties<IPlateSolveSettings>(new() {
            ["ExposureTime"] = 2.0, ["Gain"] = -1, ["Binning"] = (short)1,
            ["Threshold"] = .5, ["NumberOfAttempts"] = 1, ["ReattemptDelay"] = 0.0,
            ["DownSampleFactor"] = 0, ["MaxObjects"] = 500, ["Regions"] = 5000,
            ["SearchRadius"] = 30.0, ["BlindFailoverEnabled"] = true
        });
        var ts = Fake.Properties<ITelescopeSettings>(new() { ["FocalLength"] = 600.0, ["NoSync"] = true });
        var cs = Fake.Properties<ICameraSettings>(new() { ["PixelSize"] = 3.76 });
        var astrometry = Fake.Properties<IAstrometrySettings>(new() {
            ["Latitude"] = 37.0, ["Longitude"] = 128.0, ["Elevation"] = 100.0, ["Horizon"] = null
        });
        var profile = Fake.Of<IProfile>((m, a) => m.Name switch {
            "get_PlateSolveSettings" => ps, "get_TelescopeSettings" => ts, "get_CameraSettings" => cs,
            "get_AstrometrySettings" => astrometry, _ => Fake.Unexpected(m)
        });
        var profiles = Fake.Of<IProfileService>((m, a) => m.Name == "get_ActiveProfile" ? profile :
            m.Name.StartsWith("add_") || m.Name.StartsWith("remove_") ? null : Fake.Unexpected(m));
        var cameraInfo = new CameraInfo { Connected = true, DeviceId = "fake", ExposureMin = .001, ExposureMax = 60, XSize = 2048, YSize = 2048 };
        var mountInfo = new TelescopeInfo { Connected = true };
        var guiderInfo = new GuiderInfo { Connected = false };
        object owner = null;
        int blocks = 0, releases = 0, slews = 0, centers = 0, solvers = 0, blindSolvers = 0, guidingStops = 0;
        bool denyReservation = false, slewFails = false, solveFails = false, stopInSolve = false, stopInSlew = false;
        bool stopGuidingFails = false, stopDuringGuiding = false, restartGuidingInSolve = false, resumeDuringCapture = false;
        string guiderState = "Guiding";
        Func<object, EventArgs, Task> guidingStarted = null;
        ManualFocuserDockableVM vm = null;
        var camera = Fake.Of<ICameraMediator>((m, a) => m.Name switch {
            "GetInfo" => cameraInfo,
            "IsFreeToCapture" => owner == null || owner == a[0],
            "RegisterCaptureBlock" => Block(a[0]),
            "ReleaseCaptureBlock" => Release(a[0]),
            "RegisterConsumer" or "RemoveConsumer" => null,
            _ => Fake.Unexpected(m)
        });
        object Block(object value) {
            if (denyReservation) throw new InvalidOperationException("another consumer reserved the camera");
            if (owner != null) throw new Exception("overlapping capture");
            owner = value; blocks++; return null;
        }
        object Release(object value) { check(owner == value, "GOTO releases only its own camera reservation"); owner = null; releases++; return null; }
        var mount = Fake.Of<ITelescopeMediator>((m, a) => m.Name switch {
            "GetInfo" => mountInfo,
            "SlewToCoordinatesAsync" => Slew(),
            "RegisterConsumer" or "RemoveConsumer" => null,
            _ => Fake.Unexpected(m)
        });
        Task<bool> Slew() {
            check(owner == vm && vm.IsGoingToFocusTarget && !vm.CanConfigureLive, "Reservation and UI locks cover the initial mount slew");
            check(!vm.MoveINCommand.CanExecute(null) && !vm.StartFocusPreviewCommand.CanExecute(null), "Focus movement and live captures cannot overlap GOTO");
            check(!guiderInfo.Connected || guiderState is "Stopped" or "Looping" or "Selected", "Mount motion starts only after guiding stops or is already idle");
            slews++;
            if (stopInSlew) vm.CancelGotoCommand.Execute(null);
            return Task.FromResult(!slewFails);
        }
        var guiderDevice = Fake.Of<IGuider>((m,a) => m.Name == "get_State" ? guiderState : Fake.Unexpected(m));
        var guider = Fake.Of<IGuiderMediator>((m, a) => m.Name switch {
            "GetInfo" => guiderInfo, "GetDevice" => guiderDevice, "StopGuiding" => StopGuiding(),
            "add_GuidingStarted" => ObserveGuiding((Func<object,EventArgs,Task>)a[0], true),
            "remove_GuidingStarted" => ObserveGuiding((Func<object,EventArgs,Task>)a[0], false),
            "RegisterConsumer" or "RemoveConsumer" => null, _ => Fake.Unexpected(m)
        });
        object ObserveGuiding(Func<object,EventArgs,Task> handler, bool add) {
            if(add) guidingStarted += handler; else guidingStarted -= handler;
            return null;
        }
        Task<bool> StopGuiding() {
            guidingStops++;
            check(owner == vm && vm.IsGoingToFocusTarget, "Guiding stops within the GOTO camera reservation and cancellation scope");
            if(stopDuringGuiding) vm.CancelGotoCommand.Execute(null);
            if(stopGuidingFails || guiderState is "Stopped" or "Looping" or "Selected") return Task.FromResult(false);
            guiderState = "Stopped";
            return Task.FromResult(true);
        }
        var dome = Fake.Of<IDomeMediator>((m, a) => m.Name == "GetInfo" ? new DomeInfo { Connected = false } : Fake.Unexpected(m));
        var follower = Fake.Of<IDomeFollower>((m, a) => Fake.Unexpected(m));
        var focuser = Fake.Of<IFocuserMediator>((m, a) => m.Name is "RegisterConsumer" or "RemoveConsumer" ? null : Fake.Unexpected(m));
        var wheel = Fake.Of<IFilterWheelMediator>((m, a) => m.Name is "RegisterConsumer" or "RemoveConsumer" ? null : Fake.Unexpected(m));
        var imaging = Fake.Of<IImagingMediator>((m, a) => m.Name is "add_ImagePrepared" or "remove_ImagePrepared" ? null : Fake.Unexpected(m));
        var statuses = new List<string>();
        var status = Fake.Of<IApplicationStatusMediator>((m, a) => {
            if (m.Name != "StatusUpdate") return Fake.Unexpected(m);
            statuses.Add(((ApplicationStatus)a[0]).Status); return null;
        });
        ICaptureSolver wrappedCapture = Fake.Of<ICaptureSolver>((m, a) => {
            if(m.Name != "Solve") return Fake.Unexpected(m);
            if(resumeDuringCapture) guiderState = "Guiding";
            return Task.FromResult(new PlateSolveResult { Success = true, Coordinates = vm.SelectedFocusTarget.Coordinates });
        });
        var originalCapture = wrappedCapture;
        var center = Fake.Of<ICenteringSolver>((m, a) => m.Name switch {
            "get_CaptureSolver" => wrappedCapture, "set_CaptureSolver" => SetCapture((ICaptureSolver)a[0]),
            "Center" => Center(a), _ => Fake.Unexpected(m)
        });
        object SetCapture(ICaptureSolver value) { wrappedCapture = value; return null; }
        Task<PlateSolveResult> Center(object[] args) {
            check(owner == vm && vm.IsGoingToFocusTarget && !vm.SelectRoiCommand.CanExecute(null), "Camera reservation and ROI lock cover the entire solver run");
            centers++;
            if (stopInSolve) vm.CancelGotoCommand.Execute(null);
            if (restartGuidingInSolve) { guiderState = "Guiding"; guidingStarted(null,EventArgs.Empty).GetAwaiter().GetResult(); }
            var parameter = (CenterSolveParameter)args[1];
            if(resumeDuringCapture) return wrappedCapture.Solve((NINA.Equipment.Model.CaptureSequence)args[0], parameter,
                (IProgress<PlateSolveProgress>)args[2], (IProgress<ApplicationStatus>)args[3], (CancellationToken)args[4]);
            return Task.FromResult(new PlateSolveResult { Success = !solveFails, Coordinates = parameter.Coordinates });
        }
        var solver = Fake.Of<IPlateSolver>((m, a) => Fake.Unexpected(m));
        var factory = Fake.Of<IPlateSolverFactory>((m, a) => m.Name switch {
            "GetPlateSolver" => Configured(false), "GetBlindSolver" => Configured(true), "GetCenteringSolver" => center,
            _ => Fake.Unexpected(m)
        });
        IPlateSolver Configured(bool blind) { if (blind) blindSolvers++; else solvers++; return solver; }
        using (vm = new ManualFocuserDockableVM(profiles, camera, imaging, wheel, focuser, mount, guider, dome, follower, factory, null, null, status)) {
            // Device consumers normally update these through Application.Dispatcher.
            // The harness has no host Application, so supply the device snapshots.
            void Info(string name, object value) => typeof(ManualFocuserDockableVM).GetProperty(name).SetValue(vm, value);
            Info(nameof(vm.CameraInfo), cameraInfo); Info(nameof(vm.TelescopeInfo), mountInfo); Info(nameof(vm.GuiderInfo), guiderInfo);
            vm.MinimumFocusAltitude = 15;
            vm.SelectedFocusTarget = new FocusStarSuggestion { Name = "synthetic pole", Coordinates = new Coordinates(0, 89, Epoch.J2000, Coordinates.RAType.Degrees), Magnitude = 2 };
            vm.PreviewCenterX = 25; vm.PreviewCenterY = 75;
            var method = typeof(ManualFocuserDockableVM).GetMethod("GotoFocusTargetAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            int Goto() => ((Task<int>)method.Invoke(vm, null)).GetAwaiter().GetResult();
            void Idle() => check(owner == null && !vm.IsGoingToFocusTarget && vm.CanConfigureLive && statuses[^1] == "" && guidingStarted == null,
                "Completion restores UI, clears progress and removes temporary guider observation");
            check(vm.GotoFocusTargetCommand.CanExecute(null), "Connected idle camera/mount enable centering GOTO");
            check(Goto() == 1 && centers == 1 && slews == 1 && vm.PreviewCenterX == 50 && vm.PreviewCenterY == 50,
                "GOTO invokes solving after slew and recenters shared ROI only after verification");
            check(solvers == 1 && blindSolvers == 1 && vm.FocusTargetStatus.Contains("centered"), "GOTO obtains both NINA-configured solvers and reports verified completion");
            Idle();
            guiderInfo.Connected = true; guiderState = "Guiding";
            check(vm.GotoFocusTargetCommand.CanExecute(null), "A connected guider no longer disables GOTO");
            int previousSlews = slews;
            check(Goto() == 1 && guidingStops == 1 && slews == previousSlews + 1 && guiderState == "Stopped", "Connected guiding is stopped before slew and stays stopped after centering"); Idle();
            foreach(string state in new[]{"Stopped", "Looping", "Selected"}) {
                guiderState = state;
                check(Goto() == 1, "PHD2's false StopGuiding result is accepted only for an already idle state: " + state); Idle();
            }
            stopGuidingFails = true;
            foreach(string state in new[]{"Guiding", "Calibrating", "LostLock", "Paused", ""}) {
                guiderState = state; previousSlews = slews; int previousCenters = centers;
                try { Goto(); throw new Exception("expected guiding stop failure"); } catch(InvalidOperationException) { }
                check(slews == previousSlews && centers == previousCenters && vm.FocusTargetStatus.Contains("Could not stop guiding"),
                    "Unconfirmed guider stop prevents slew and solving: " + state); Idle();
            }
            stopGuidingFails = false; stopDuringGuiding = true; guiderState = "Guiding"; previousSlews = slews;
            check(Goto() == 0 && slews == previousSlews, "Cancel while stopping guiding prevents any mount movement"); Idle();
            stopDuringGuiding = false; restartGuidingInSolve = true; vm.PreviewCenterX = 25;
            check(Goto() == 0 && vm.FocusTargetStatus.Contains("Guiding restarted") && vm.PreviewCenterX == 25,
                "A new NINA guiding operation cancels centering without a false success or ROI reset"); Idle();
            restartGuidingInSolve = false; resumeDuringCapture = true; wrappedCapture = originalCapture;
            try { Goto(); throw new Exception("expected externally resumed guiding failure"); } catch(InvalidOperationException) { }
            check(vm.PreviewCenterX == 25 && vm.FocusTargetStatus.Contains("Guiding resumed"),
                "PHD2 state changed during capture rejects the result before centering can correct or report success"); Idle();
            resumeDuringCapture = false; guiderInfo.Connected = false;
            solveFails = true; vm.PreviewCenterX = 25;
            try { Goto(); throw new Exception("expected solve failure"); } catch (InvalidOperationException) { }
            check(vm.FocusTargetStatus.Contains("centering failed") && vm.PreviewCenterX == 25, "Solve failure preserves ROI selection and distinguishes completed slew from centering"); Idle();
            solveFails = false; stopInSolve = true;
            check(Goto() == 0 && vm.FocusTargetStatus.Contains("not verified"), "Stop in solver returns a canceled outcome without false centering success"); Idle();
            stopInSolve = false; stopInSlew = true; int before = centers;
            check(Goto() == 0 && centers == before, "Stop during initial slew skips plate-solve capture"); Idle();
            stopInSlew = false; slewFails = true; before = centers;
            try { Goto(); throw new Exception("expected slew failure"); } catch (InvalidOperationException) { }
            check(centers == before && vm.FocusTargetStatus.StartsWith("GOTO did not complete"), "Failed initial slew performs no solver capture"); Idle();
            slewFails = false; denyReservation = true; before = slews;
            int previousReleases = releases;
            try { Goto(); throw new Exception("expected reservation failure"); } catch (InvalidOperationException) { }
            check(slews == before && releases == previousReleases, "Reservation rejection performs no motion and does not release another consumer's block"); Idle();
            denyReservation = false; cameraInfo.Connected = false;
            check(!vm.GotoFocusTargetCommand.CanExecute(null) && Goto() == 0, "Disconnected camera disables/guards GOTO");
            check(vm.GotoFocusTargetTooltip.Contains("Connect the camera"), "Disabled GOTO explains the missing plate-solve camera");
            cameraInfo.Connected = true; mountInfo.AtPark = true;
            check(!vm.GotoFocusTargetCommand.CanExecute(null) && vm.GotoFocusTargetTooltip.Contains("Unpark"), "Parked mount stays blocked with actionable tooltip");
            mountInfo.AtPark = false; mountInfo.Slewing = true;
            check(!vm.GotoFocusTargetCommand.CanExecute(null) && vm.GotoFocusTargetTooltip.Contains("slew"), "An ongoing mount slew stays blocked");
            mountInfo.Slewing = false; vm.IsCapturing = true;
            check(!vm.GotoFocusTargetCommand.CanExecute(null) && vm.GotoFocusTargetTooltip.Contains("Stop live focus"), "Live capture stays blocked with a stop-live explanation");
            vm.IsCapturing = false; vm.IsMoving = true;
            check(!vm.GotoFocusTargetCommand.CanExecute(null) && vm.GotoFocusTargetTooltip.Contains("focus movement"), "Focus movement stays blocked");
            vm.IsMoving = false; owner = new object();
            check(!vm.GotoFocusTargetCommand.CanExecute(null) && vm.GotoFocusTargetTooltip.Contains("another NINA"), "A camera reserved by another consumer stays blocked");
            int tooltipUpdates = 0;
            vm.PropertyChanged += (_,e)=>{if(e.PropertyName == nameof(vm.GotoFocusTargetTooltip)) tooltipUpdates++;};
            void RefreshAvailability()=>typeof(ManualFocuserDockableVM).GetMethod("RefreshGotoAvailability", BindingFlags.Instance|BindingFlags.NonPublic).Invoke(vm,null);
            RefreshAvailability(); int notifications = tooltipUpdates;
            RefreshAvailability();
            check(tooltipUpdates == notifications, "Unchanged device polls do not repeatedly notify GOTO state");
            owner = null; RefreshAvailability();
            check(tooltipUpdates == notifications + 1 && vm.GotoFocusTargetCommand.CanExecute(null), "Capture ownership changes refresh GOTO even when device connection flags stay the same");
            vm.SelectedFocusTarget = null;
            check(!vm.GotoFocusTargetCommand.CanExecute(null) && vm.GotoFocusTargetTooltip.Contains("Select a focus star"), "Empty star selection provides the Refresh instruction");
            check(blocks == releases, "Every acquired GOTO capture reservation is released across all outcomes");
        }
    }
}
