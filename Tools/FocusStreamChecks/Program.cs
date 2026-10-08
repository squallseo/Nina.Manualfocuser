using System.Reflection;
using System.Runtime.CompilerServices;
using Cwseo.NINA.ManualFocuser.Models;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Equipment.Interfaces;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

int passed = 0;
void Check(bool condition, string message) {
    if (!condition) throw new Exception(message);
    passed++; Console.WriteLine("PASS " + message);
}
async Task Reject<T>(Func<Task> action, string message) where T : Exception {
    bool rejected = false;
    try { await action(); } catch (T) { rejected = true; }
    Check(rejected, message);
}
TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
var produced = Signal(); var cleanupEntered = Signal(); var finishCleanup = Signal();
async IAsyncEnumerable<int> FastFrames([EnumeratorCancellation] CancellationToken ct) {
    try {
        for (int i = 1; i <= 5; i++) yield return i;
        produced.SetResult();
        await Task.Delay(Timeout.Infinite, ct);
    } finally { cleanupEntered.SetResult(); await finishCleanup.Task; }
}
var latest = new LatestFrameStream<int>(FastFrames, TimeSpan.FromSeconds(10), default);
await produced.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(await latest.ReadAsync(default) == 5, "Slow consumer receives the latest frame, not an accumulated backlog");
var disposal = latest.DisposeAsync().AsTask();
await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(!disposal.IsCompleted, "Stop waits for host enumerator cleanup before completing");
finishCleanup.SetResult(); await disposal;
await latest.DisposeAsync();
Check(true, "Disposal is idempotent");

async IAsyncEnumerable<int> NoFrames([EnumeratorCancellation] CancellationToken ct) {
    await Task.Delay(Timeout.Infinite, ct); yield return 1;
}
var stalled = new LatestFrameStream<int>(NoFrames, TimeSpan.FromMilliseconds(50), default);
await Reject<TimeoutException>(async () => await stalled.ReadAsync(default), "Missing first frame times out by cancelling the source");
await Reject<TimeoutException>(async () => await stalled.DisposeAsync(), "Timeout is also reported during disposal");

var camera = new CameraInfo { Connected = true, DeviceId = "QHY600M-test", Name = "QHY600M", XSize = 128, YSize = 96,
    CanSubSample = true, ExposureMin = .01, ExposureMax = 30, BinX = 2, BinY = 2, ReadoutMode = 1, Gain = 30, Offset = 30 };
ushort[] pixels = Enumerable.Range(0, 1024).Select(i => (ushort)i).ToArray();
int width = 32, height = 32;
int bitDepth=16;bool bayered=false;
var array = Fake.Of<IImageArray>((m, a) => m.Name == "get_FlatArray" ? pixels : Fake.Unexpected(m));
var image = Fake.Of<IImageData>((m, a) => m.Name switch {
    "get_Properties" => new ImageProperties(width, height, bitDepth, bayered, 1, 1),
    "get_Data" => array, _ => Fake.Unexpected(m)
});
var exposure = Fake.Of<IExposureData>((m, a) => m.Name == "ToImageData" ? Task.FromResult(image) : Fake.Unexpected(m));
CaptureSequence sequence = null;
bool sourceClosed = false, invalid = false;
int starts = 0, restores = 0;
bool nativeLive = false, nativeConnected = true;
string nativeId="ToupTek_test";
bool delayAsiDownload=false;
var asiDownloadEntered=Signal();var asiDownloadFinished=Signal();
var nativeCamera = Fake.Of<ICamera>((m,a)=>m.Name switch {
    "get_Id" => nativeId,
    "get_Connected" => nativeConnected,
    "get_LiveViewEnabled" => nativeLive,
    _ => Fake.Unexpected(m)
});
async IAsyncEnumerable<IExposureData> HostStream([EnumeratorCancellation] CancellationToken ct) {
    bool toup = camera.DeviceId == "ToupTek_test";
    if(toup) nativeLive = true;
    try {
        yield return invalid ? null : exposure;
        if(delayAsiDownload && FocusCameraSupport.IsNativeAsi(camera.DeviceId)) {
            asiDownloadEntered.TrySetResult();
            await asiDownloadFinished.Task; // Installed ASI download does not observe cancellation while in the SDK.
            yield return exposure;
        }
        await Task.Delay(Timeout.Infinite, ct);
    }
    finally {
        sourceClosed = true;
        if(toup) _ = Task.Run(async()=> { await Task.Delay(100); nativeLive = false; });
    }
}
var mediator = Fake.Of<ICameraMediator>((m, a) => {
    switch (m.Name) {
        case "GetInfo": return camera;
        case "GetDevice": return nativeCamera;
        case "LiveView": starts++; sequence = (CaptureSequence)a[0]; return HostStream((CancellationToken)a[1]);
        case "SetReadoutMode": Check(sourceClosed && !nativeLive && (short)a[0] == 1, "Read mode restoration follows completed native stream close"); restores++; return null;
        case "SetBinning": Check((short)a[0] == 2 && (short)a[1] == 2, "Original binning is restored"); restores++; return null;
        case "SetSubSambleRectangle": restores++; return null;
        default: return Fake.Unexpected(m);
    }
});
var model = new ManualFocuserModel(null, null, mediator, null, null);
Check(model.SupportsFocusStreaming, "Native QHY allows unspecified mode");
foreach (var mode in new[] { "Bin3*3Mode (hardware)", "3x3 bin mode", "Bin 3×3 Mode" }) {
    camera.ReadoutModes = new[] { "PhotoGraphic DSO 16BIT", mode };
    Check(!model.SupportsFocusStreaming, $"Streaming blocked for {mode}");
    await Reject<InvalidOperationException>(() => { model.StartFocusStreaming(.1, 32, 75, 25, default); return Task.CompletedTask; }, "Bin3 rejects before host call");
}
foreach (var mode in new[] { "PhotoGraphic DSO 16BIT", "High Gain Mode 16BIT", "Extend Fullwell", "2CMS-0", "2CMS-1" }) {
    camera.ReadoutModes = new[] { "PhotoGraphic DSO 16BIT", mode };
    Check(model.SupportsFocusStreaming, $"Streaming allowed for {mode}");
}
camera.CanShowLiveView = true;
camera.DeviceId="QHY268M-test";
Check(model.SupportsFocusStreaming,"A QHY driver explicitly advertising LiveView is not blocked by its brand or model");
camera.CanShowLiveView=false;
Check(!model.SupportsFocusStreaming,"An unreviewed native driver with no advertised LiveView retains single-frame fallback");
camera.CanShowLiveView=true;
camera.DeviceId = "Test.LiveView";
var stream = model.StartFocusStreaming(.1, 32, 75, 25, default);
var frame = await stream.ReadAsync(default);
Check(sequence.ExposureTime == .1 && sequence.Gain == 30 && sequence.Offset == 30 && sequence.FilterType == null,
    "Public host LiveView receives explicit exposure/gain/offset without moving a filter");
Check(sequence.Binning.X == 1 && sequence.EnableSubSample && sequence.SubSambleRectangle.X == 80 && sequence.SubSambleRectangle.Y == 8,
    "Streaming requests aligned ROI and 1x1 binning");
Check(frame.HardwareRoi && frame.Pixels[0] == 0 && frame.Pixels[^1] == 1023, "Stream ROI pixels are preserved");
await stream.DisposeAsync();
Check(sourceClosed && restores == 3, "Stop closes source and restores camera settings");

camera.DeviceId = "ASCOM.QHY.Camera";
camera.CanShowLiveView = false;
Check(!model.SupportsFocusStreaming, "QHY ASCOM connection is not mistaken for native QHY");
await Reject<InvalidOperationException>(() => { model.StartFocusStreaming(.1, 32, 50, 50, default); return Task.CompletedTask; }, "Unsupported streaming rejects before touching the host");
Check(starts == 1, "Unsupported camera never starts a stream");
camera.DeviceId = "ToupTek_test";
Check(model.SupportsFocusStreaming, "Native ToupTek uses public LiveView despite the disabled UI capability");
sourceClosed = false;
stream = model.StartFocusStreaming(.1,32,50,50,default);
frame = await stream.ReadAsync(default);
Check(frame.Width == 32 && nativeLive, "Native ToupTek streaming receives ROI frames");
var toupStop = stream.DisposeAsync().AsTask();
await Task.Delay(25);
Check(!toupStop.IsCompleted && nativeLive, "ToupTek Stop awaits delayed native mode restoration even with stale CameraInfo");
await toupStop;
Check(!nativeLive && sourceClosed, "ToupTek native video mode stops before disposal completes");
sourceClosed = false;
stream = model.StartFocusStreaming(.1,32,50,50,default);
await stream.ReadAsync(default);
Check(nativeLive,"ToupTek streaming can restart after completed shutdown");
await stream.DisposeAsync();
Check(!nativeLive,"Repeated ToupTek start/stop restores capture mode each time");
nativeLive = true;
await Reject<TimeoutException>(()=>ManualFocuserModel.WaitForFocusStreamStopAsync(nativeCamera,TimeSpan.FromMilliseconds(50)),
    "ToupTek cleanup timeout fails explicitly instead of claiming successful shutdown");
nativeConnected = false;
await ManualFocuserModel.WaitForFocusStreamStopAsync(nativeCamera,TimeSpan.FromMilliseconds(50));
Check(true,"Disconnected ToupTek exits cleanup without touching another device");
nativeConnected = true; nativeLive = false;
camera.DeviceId = "ASCOM.ToupTek.Camera";
Check(!model.SupportsFocusStreaming,"ToupTek ASCOM is not mistaken for a native SDK camera");
camera.DeviceId = "ToupTek_named_but_different";
await Reject<InvalidOperationException>(()=>{ model.StartFocusStreaming(.1,32,50,50,default);return Task.CompletedTask; },
    "ToupTek native device identity must match the requested connection");

foreach(string asiId in new[]{"ZWOptical_ASI2600MM Pro_","ZWOptical_ASI6200MC Pro_(main)","ZWOptical_ASI120MM Mini_guide"}) {
    camera.DeviceId=asiId;camera.Name="Unrelated display name";
    Check(model.SupportsFocusStreaming,"Native ASI driver family streams without a per-model allowlist: "+asiId);
}
camera.DeviceId="ASCOM.ASICamera2.Camera";camera.Name="ZWO ASI6200MC Pro";
Check(!model.SupportsFocusStreaming,"An ASI display name does not enable an unsupported ASCOM LiveView path");
camera.CanShowLiveView=true;
Check(model.SupportsFocusStreaming,"Any driver advertising LiveView is evaluated by its public capability");
camera.CanShowLiveView=false;camera.DeviceId="Other.ASI.Driver";
Check(!model.SupportsFocusStreaming,"Unknown non-streaming drivers are not enabled by the ASI name");
camera.DeviceId="ZWOptical_ASI6200MC Pro_main";nativeId=camera.DeviceId;
nativeConnected=false;int priorAsiStarts=starts;
await Reject<InvalidOperationException>(()=>{model.StartFocusStreaming(.1,50,100,100,default,roiHeight:35);return Task.CompletedTask;},
    "Disconnected native ASI rejects before starting host LiveView");
nativeConnected=true;nativeId="ZWOptical_ASI6200MC Pro_other";
await Reject<InvalidOperationException>(()=>{model.StartFocusStreaming(.1,50,100,100,default,roiHeight:35);return Task.CompletedTask;},
    "Changed native ASI identity rejects before starting host LiveView");
Check(starts==priorAsiStarts,"Rejected ASI devices issue no capture command");
nativeId=camera.DeviceId;camera.XSize=137;camera.YSize=99;
width=48;height=34;pixels=Enumerable.Range(0,width*height).Select(i=>(ushort)i).ToArray();bitDepth=12;bayered=true;
sourceClosed=false;
var asiSettings=model.CreateFocusCaptureSettings(.1,50,35,100,100);
stream=model.StartFocusStreaming(.1,50,100,100,default,roiHeight:35);
frame=await stream.ReadAsync(default);
Check(sequence.SubSambleRectangle.Width==48 && sequence.SubSambleRectangle.Height==34 && sequence.SubSambleRectangle.X==88 && sequence.SubSambleRectangle.Y==64,
    "Native ASI ROI uses 8-pixel width and 2-pixel height alignment at the sensor edge");
Check(asiSettings.Roi==new FocusRoi.Rectangle(88,64,48,34),"Single capture and streaming use the same ASI-aligned sensor ROI");
Check(frame.HardwareRoi && frame.Width==48 && frame.Height==34 && frame.BitDepth==12 && frame.IsBayered==true && frame.Pixels[^1]==pixels[^1],
    "Color ASI streaming preserves raw pixel values, bit depth, Bayer metadata and rectangular ROI dimensions");
await stream.DisposeAsync();
Check(sourceClosed && !nativeLive,"Native ASI Stop drains the host enumerator before restoring settings");
sourceClosed=false;
stream=model.StartFocusStreaming(.1,50,100,100,default,roiHeight:35);await stream.ReadAsync(default);await stream.DisposeAsync();
Check(sourceClosed && starts==priorAsiStarts+2,"Native ASI supports repeated stream start/stop through the same public host path");
sourceClosed=false;delayAsiDownload=true;
stream=model.StartFocusStreaming(.1,50,100,100,default,roiHeight:35);await stream.ReadAsync(default);
await asiDownloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
var asiStop=stream.DisposeAsync().AsTask();await Task.Delay(25);
Check(!asiStop.IsCompleted && !sourceClosed,"ASI Stop waits for an in-flight native download instead of restoring settings concurrently");
asiDownloadFinished.SetResult();await asiStop;
Check(sourceClosed,"ASI host Stop and settings restoration complete after the in-flight download returns");
delayAsiDownload=false;
camera.XSize=128;camera.YSize=96;width=32;height=32;bitDepth=16;bayered=false;
camera.DeviceId = "Test.LiveView";
camera.CanShowLiveView = true;
using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
await Reject<OperationCanceledException>(() => { model.StartFocusStreaming(.1, 32, 50, 50, cancellation.Token); return Task.CompletedTask; }, "Pre-cancelled streaming does not start the host");

// Driver ignores ROI: crop the full sensor at the same coordinates requested above.
width = 128; height = 96; pixels = Enumerable.Range(0, width * height).Select(i => (ushort)i).ToArray();
sourceClosed = false;
stream = model.StartFocusStreaming(.1, 32, 75, 25, default);
frame = await stream.ReadAsync(default);
Check(!frame.HardwareRoi && frame.Pixels[0] == 8 * 128 + 80 && frame.Pixels[^1] == 39 * 128 + 111, "Full frames from a driver ignoring ROI are cropped safely");
await stream.DisposeAsync();
sourceClosed = false; invalid = true;
stream = model.StartFocusStreaming(.1, 32, 50, 50, default);
await Reject<InvalidOperationException>(async () => await stream.ReadAsync(default), "Null host stream frames fail explicitly");
await Reject<InvalidOperationException>(async () => await stream.DisposeAsync(), "Stream errors survive disposal instead of triggering automatic recapture");
Check(sourceClosed, "Invalid frame still closes the host stream");
invalid = false; sourceClosed = false;
stream = model.StartFocusStreaming(.1, 64, 75, 25, default, roiHeight: 32);
frame = await stream.ReadAsync(default);
Check(sequence.SubSambleRectangle.Width == 64 && sequence.SubSambleRectangle.Height == 32,
    "Rectangular stream ROI preserves separate width and height");
Check(frame.Width == 64 && frame.Height == 32 && frame.Pixels[0] == 8 * 128 + 64 && frame.Pixels[^1] == 39 * 128 + 127,
    "Rectangular software crop uses the correct row stride and bounds");
await stream.DisposeAsync();

var fitted = FocusRoi.Fit(9600, 6422, 512, 1024, 100, 100, 4);
Check(fitted.X % 4 == 0 && fitted.Y % 4 == 0 && fitted.Width == 512 && fitted.Height == 1024
    && fitted.X + fitted.Width <= 9600 && fitted.Y + fitted.Height <= 6422,
    "Rectangular QHY ROI stays aligned and inside the sensor at its edge");
var center = FocusRoi.CenterWindow(Enumerable.Range(0, 512 * 320).Select(i => (double)i).ToArray(), 512, 320);
Check(center.Width == 256 && center.Height == 256 && center.Pixels[0] == 32 * 512 + 128
    && center.Pixels[^1] == 287 * 512 + 383, "HFR window remains centered independently of acquisition ROI size");
Console.WriteLine($"{passed} stream checks passed; no hardware accessed.");

public class Fake : DispatchProxy {
    public Func<MethodInfo, object[], object> Handler;
    protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
    public static T Of<T>(Func<MethodInfo, object[], object> handler) where T : class {
        T proxy = Create<T, Fake>(); ((Fake)(object)proxy).Handler = handler; return proxy;
    }
    public static object Unexpected(MethodInfo method) => throw new Exception("Unexpected camera/image API: " + method.Name);
}
