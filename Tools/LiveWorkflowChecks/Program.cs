using System.Reflection;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;

int passed = 0;
if(args.Contains("--histogram-checks")) { HistogramChecks.Run(Check); Console.WriteLine($"{passed} histogram checks passed."); return; }
if(args.Contains("--histogram-files")) { await HistogramPerformance.Run(args[Array.IndexOf(args,"--histogram-files")+1]); return; }
if(args.Contains("--levels-ui")) { ReadmeScreenshots.Run(args[Array.IndexOf(args,"--levels-ui")+1],"bin/levels-ui",true); return; }
if(args.Contains("--stretch-files")) { await StretchComparison.Run(args[Array.IndexOf(args,"--stretch-files")+1]); return; }
if(args.Contains("--stretch-checks")) { StretchLevelsChecks.Run(Check); Console.WriteLine($"{passed} stretch levels checks passed."); return; }
if(args.Contains("--readme-screenshots")) {
    int index=Array.IndexOf(args,"--readme-screenshots");
    ReadmeScreenshots.Run(args[index+1],args[index+2]);return;
}
if (args.Contains("--star-tracking")) {
    StarTrackingChecks.Run(Check);
    Console.WriteLine($"{passed} star tracking checks passed; no hardware operated.");
    return;
}
if (args.Contains("--mosaic-layout-stress")) {
    await MosaicLayoutChecks.Run(Check);
    return;
}
if(args.Contains("--inspector-cycle")){await InspectorSourceChecks.Run(args[Array.IndexOf(args,"--inspector-cycle")+1],true);return;}
if(args.Contains("--inspector-files") && !args.Contains("--focus-workspace")){await InspectorSourceChecks.Run(args[Array.IndexOf(args,"--inspector-files")+1]);return;}
if(args.Contains("--audit-focus")){AutofocusCurveAnalysis.RunAudit(args[Array.IndexOf(args,"--audit-focus")+1]);return;}
if(args.Contains("--compare-focus")){AutofocusCurveAnalysis.Run(args[Array.IndexOf(args,"--compare-focus")+1]);return;}
if (args.Contains("--host-image-perf")) { HostImagePerformance.Run(); return; }
if (args.Contains("--host-image-files")) { HostImagePerformance.Run(args[Array.IndexOf(args,"--host-image-files")+1]); return; }
if (args.Contains("--host-image-verify-files")) { HostImagePerformance.Run(args[Array.IndexOf(args,"--host-image-verify-files")+1],true); return; }
void Check(bool condition, string description) {
    if (!condition) throw new Exception(description);
    passed++; Console.WriteLine("PASS " + description);
}
if (args.Contains("--identity")) {
    var assembly = typeof(Cwseo.NINA.ManualFocuser.Dockables.ManualFocuserDockableVM).Assembly;
    Check(assembly.GetName().Name == "Cwseo.NINA.ManualFocuser", "Existing Manual Focuser assembly identity retained");
    Check(assembly.GetCustomAttribute<System.Runtime.InteropServices.GuidAttribute>()?.Value == "56e3434f-95de-49fe-bb59-2034ea457afb", "Existing plugin GUID retained for updates");
    Check(assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title == "Manual Focuser", "Existing plugin title retained");
    var exports = assembly.GetTypes().SelectMany(t => t.GetCustomAttributes<System.ComponentModel.Composition.ExportAttribute>().Select(a => (Type: t, Contract: a.ContractType))).ToArray();
    Check(exports.Count(e => e.Contract == typeof(NINA.Plugin.Interfaces.IPluginManifest)) == 1, "Only one plugin manifest exported");
    Check(exports.Count(e => e.Contract == typeof(Cwseo.NINA.ManualFocuser.Dockables.ManualFocuserDockableVM).GetInterfaces().Single(t => t.Name == "IDockableVM")) == 3 &&
        exports.Any(e => e.Contract == typeof(Cwseo.NINA.ManualFocuser.Dockables.ManualFocuserDockableVM).GetInterfaces().Single(t => t.Name == "IDockableVM") && e.Type == typeof(Cwseo.NINA.ManualFocuser.Dockables.ManualFocuserDockableVM)), "One plugin exports three focus panels");
    Check(exports.Any(e => e.Contract == typeof(NINA.Sequencer.SequenceItem.ISequenceItem)), "Existing sequencer export preserved");
    Console.WriteLine($"{passed} migration identity checks passed; no hardware operated.");
    return;
}
async Task Reject<T>(Func<Task> action, string description) where T : Exception {
    bool rejected = false;
    try { await action(); } catch (T) { rejected = true; }
    Check(rejected, description);
}
if(args.Contains("--multi-star")){await MultiStarHfrChecks.Run(Check,args.Length>1?args[1]:null);Console.WriteLine($"{passed} multi-star checks passed; no hardware operated.");return;}
if(args.Contains("--live-move")){LiveMoveChecks.Run(Check,args.Contains("--asi"),args.Contains("--inspector"));Console.WriteLine($"{passed} live movement checks passed; no hardware operated.");return;}
if(args.Contains("--auto-roi")){AutoRoiVmChecks.Run(Check);Console.WriteLine($"{passed} explicit ROI checks passed; no hardware operated.");return;}
if(args.Contains("--preview-display")){PreviewDisplayChecks.Run(Check);Console.WriteLine($"{passed} preview display checks passed; no hardware operated.");return;}
if(args.Contains("--focus-workspace")){FocusWorkspaceChecks.Run(Check);Console.WriteLine($"{passed} focus workspace checks passed; no hardware operated.");return;}
if(args.Contains("--focuser")){FocuserChecks.Run(Check);Console.WriteLine($"{passed} manual focuser checks passed; no hardware operated.");return;}
if(args.Contains("--preview-perf")){PreviewPerformance.Run();return;}
if(args.Contains("--targets")){TargetPickerChecks.Run(Check);Console.WriteLine($"{passed} target picker checks passed; no hardware operated.");return;}
var settings = new Dictionary<string, object> {
    ["ExposureTime"] = 3.0, ["Gain"] = 42, ["Binning"] = (short)2,
    ["Threshold"] = .5, ["NumberOfAttempts"] = 2, ["ReattemptDelay"] = .1,
    ["DownSampleFactor"] = 2, ["MaxObjects"] = 500, ["Regions"] = 4,
    ["SearchRadius"] = 10.0, ["BlindFailoverEnabled"] = true,
    ["Filter"] = new FilterInfo { Name = "must not change filter" }
};
var telescopeSettings = new Dictionary<string, object> { ["FocalLength"] = 600.0, ["NoSync"] = true };
var cameraSettings = new Dictionary<string, object> { ["PixelSize"] = 3.76 };
var ps = Fake.Properties<IPlateSolveSettings>(settings);
var ts = Fake.Properties<ITelescopeSettings>(telescopeSettings);
var cs = Fake.Properties<ICameraSettings>(cameraSettings);
var profile = Fake.Of<IProfile>((m, a) => m.Name switch {
    "get_PlateSolveSettings" => ps, "get_TelescopeSettings" => ts, "get_CameraSettings" => cs,
    _ => Fake.Unexpected(m)
});
var camera = new CameraInfo { Connected = true, DeviceId = "fake", ExposureMin = .001, ExposureMax = 60, Offset = 15 };
Coordinates Point(double dec) => new Coordinates(180, dec, Epoch.J2000, Coordinates.RAType.Degrees);
var target = Point(30);
var plan = new FocusTargetCentering(profile, camera, target);
Check(plan.Sequence.ExposureTime == 3 && !plan.Sequence.EnableSubSample && plan.Sequence.Binning.X == 2 && plan.Sequence.Binning.Y == 2,
    "Solve capture uses configured exposure/binning and the full sensor, not focus ROI");
Check(plan.Sequence.FilterType == null && plan.Sequence.Offset == 15 && plan.Sequence.Gain == 42,
    "Current filter/offset are retained; configured plate-solve gain is honored");
Check(plan.Parameter.Attempts == 2 && plan.Parameter.ReattemptDelay == TimeSpan.FromSeconds(6) && plan.Parameter.Threshold == .5,
    "Configured retries and arcminute tolerance retain NINA's units");
Check(plan.Parameter.NoSync && plan.Parameter.BlindFailoverEnabled && plan.Parameter.FocalLength == 600 && plan.Parameter.PixelSize == 7.52,
    "Mount NoSync, blind fallback and binned image scale are honored");
settings["ExposureTime"] = .25; settings["Threshold"] = 10.0;
Check(plan.Sequence.ExposureTime == 3 && plan.Parameter.Threshold == .5, "A running operation retains its settings snapshot");
settings["Threshold"] = .5;
foreach (double invalid in new[] { 0, double.NaN, 100.0 }) {
    settings["ExposureTime"] = invalid;
    await Reject<InvalidOperationException>(() => { _ = new FocusTargetCentering(profile, camera, target); return Task.CompletedTask; }, "Invalid exposure rejected before slew: " + invalid);
}
settings["ExposureTime"] = 3.0;
camera.Connected = false;
await Reject<InvalidOperationException>(() => { _ = new FocusTargetCentering(profile, camera, target); return Task.CompletedTask; }, "Disconnected camera rejected before slew");
camera.Connected = true;
telescopeSettings["FocalLength"] = 0.0;
await Reject<InvalidOperationException>(() => { _ = new FocusTargetCentering(profile, camera, target); return Task.CompletedTask; }, "Missing focal length rejected before slew");
telescopeSettings["FocalLength"] = 600.0;
settings["Threshold"] = double.NaN;
await Reject<InvalidOperationException>(() => { _ = new FocusTargetCentering(profile, camera, target); return Task.CompletedTask; }, "Invalid tolerance rejected before slew");
settings["Threshold"] = .5;

// Exercise NINA's real correction/repeat algorithm with a deterministic camera
// and pointing offset. No solver executable, native driver or mount is invoked.
int captures = 0, slews = 0, syncs = 0;
Coordinates position = target;
Coordinates pointingError = Point(30.1); // mount says 30 deg, image is 6 arcmin high
bool solveFails = false, noConvergence = false, cancelAfterCapture = false, disconnectAfterCapture = false;
using var cts = new CancellationTokenSource();
var capture = Fake.Of<ICaptureSolver>((m, a) => {
    if (m.Name != "Solve") return Fake.Unexpected(m);
    ((CancellationToken)a[4]).ThrowIfCancellationRequested();
    captures++;
    var seq = (CaptureSequence)a[0];
    Check(!seq.EnableSubSample && seq.FilterType == null && a[2] != null && a[3] != null, "Center iteration has full sensor capture and non-null progress");
    if (cancelAfterCapture) cts.Cancel();
    if (disconnectAfterCapture) camera.Connected = false;
    var solved = noConvergence ? pointingError : new Coordinates(position.RA, position.Dec + .1, Epoch.J2000, Coordinates.RAType.Hours);
    return Task.FromResult(new PlateSolveResult { Success = !solveFails, Coordinates = solved });
});
var guardedCapture = new GuardedFocusCaptureSolver(capture, () => {
    if (!camera.Connected) throw new InvalidOperationException("camera disconnected");
});
var mount = Fake.Of<ITelescopeMediator>((m, a) => m.Name switch {
    "GetCurrentPosition" => position,
    "SlewToCoordinatesAsync" => Slew((Coordinates)a[0], (CancellationToken)a[1]),
    "Sync" => Sync((Coordinates)a[0]),
    _ => Fake.Unexpected(m)
});
Task<bool> Slew(Coordinates coords, CancellationToken token) {
    token.ThrowIfCancellationRequested(); slews++; position = coords; return Task.FromResult(true);
}
Task<bool> Sync(Coordinates coords) { syncs++; position = coords; return Task.FromResult(true); }
var dome = Fake.Of<IDomeMediator>((m, a) => m.Name == "GetInfo" ? new DomeInfo { Connected = false } : Fake.Unexpected(m));
// Null dependencies here are never touched: current filter is kept and dome is
// disconnected. Unexpected mount calls above fail the harness immediately.
var hostSolver = new CenteringSolver(null, null, null, mount, null, dome, null) { CaptureSolver = guardedCapture };
var solveProgress = new Sink<PlateSolveProgress>();
var progress = new Sink<ApplicationStatus>();
double error = await plan.CenterAsync(hostSolver, solveProgress, progress, default);
Check(captures == 2 && slews == 1 && syncs == 0 && error < .001, "NoSync mode corrects the measured pointing offset and verifies a second image");
Check(Math.Abs(position.Dec - 29.9) < .0001, "NoSync correction compensates in the correct direction");

position = target; captures = slews = 0; solveFails = true;
await Reject<InvalidOperationException>(() => plan.CenterAsync(hostSolver, solveProgress, progress, default), "Solve failure is not reported as centered");
Check(captures == 1 && slews == 0 && syncs == 0, "Failed solve performs no corrective motion/sync");
solveFails = false; noConvergence = true; captures = slews = 0;
await Reject<InvalidOperationException>(() => plan.CenterAsync(hostSolver, solveProgress, progress, default), "Repeated failure to converge terminates instead of claiming success");
Check(captures == 10 && slews == 10, "NINA's correction limit bounds unsuccessful centering");
noConvergence = false; cancelAfterCapture = true; captures = slews = 0;
await Reject<OperationCanceledException>(() => plan.CenterAsync(hostSolver, solveProgress, progress, cts.Token), "Stop after capture prevents a corrective slew");
Check(captures == 1 && slews == 0, "Canceled capture result cannot trigger mount correction");
cancelAfterCapture = false; disconnectAfterCapture = true; captures = slews = 0;
await Reject<InvalidOperationException>(() => plan.CenterAsync(hostSolver, solveProgress, progress, default), "Device change after capture prevents correction");
Check(captures == 1 && slews == 0, "Disconnected camera result cannot trigger mount correction");
disconnectAfterCapture = false; camera.Connected = true;

// A provider can mark a distant solution as successful or omit its separation.
// Independently validate the actual final solution against the chosen star.
PlateSolveResult providerResult = new() { Success = true, Coordinates = pointingError, Separation = target - target };
int providerCalls = 0;
var provider = Fake.Of<ICenteringSolver>((m, a) => {
    if (m.Name != "Center") return Fake.Unexpected(m);
    providerCalls++; return Task.FromResult(providerResult);
});
await Reject<InvalidOperationException>(() => plan.CenterAsync(provider, solveProgress, progress, default), "Provider success with stale zero separation still rejects an off-center image");
providerResult = new PlateSolveResult { Success = true, Coordinates = null };
await Reject<InvalidOperationException>(() => plan.CenterAsync(provider, solveProgress, progress, default), "Provider success without image coordinates rejects");
providerResult = new PlateSolveResult { Success = true, Coordinates = Point(30.005) };
Check(Math.Abs(await plan.CenterAsync(provider, solveProgress, progress, default) - 18) < .01, "Final residual is measured from image coordinates and reported in arcseconds");
int before = providerCalls;
await Reject<OperationCanceledException>(() => plan.CenterAsync(provider, solveProgress, progress, cts.Token), "Pre-canceled center rejects before solver capture");
Check(providerCalls == before, "Pre-canceled operation performs no solver call");

VmChecks.Run(Check);
Console.WriteLine($"{passed} centering checks passed; no hardware operated.");

public sealed class Sink<T> : IProgress<T> { public void Report(T value) { } }
public class Fake : DispatchProxy {
    public Func<MethodInfo, object[], object> Handler;
    protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
    public static T Of<T>(Func<MethodInfo, object[], object> handler) where T : class {
        var proxy = Create<T, Fake>(); ((Fake)(object)proxy).Handler = handler; return proxy;
    }
    public static T Properties<T>(Dictionary<string, object> values) where T : class => Of<T>((m, a) => {
        if (m.Name.StartsWith("get_") && values.TryGetValue(m.Name[4..], out var value)) return value;
        if (m.Name.StartsWith("set_") && values.ContainsKey(m.Name[4..])) { values[m.Name[4..]] = a[0]; return null; }
        return Unexpected(m);
    });
    public static object Unexpected(MethodInfo method) => throw new Exception("Unexpected API call: " + method.Name);
}
