using System.Reflection;
using System.Runtime.CompilerServices;
using Cwseo.NINA.ManualFocuser.Models;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
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
var array = Fake.Of<IImageArray>((m, a) => m.Name == "get_FlatArray" ? pixels : Fake.Unexpected(m));
var image = Fake.Of<IImageData>((m, a) => m.Name switch {
    "get_Properties" => new ImageProperties(width, height, 16, false, 1, 1),
    "get_Data" => array, _ => Fake.Unexpected(m)
});
var exposure = Fake.Of<IExposureData>((m, a) => m.Name == "ToImageData" ? Task.FromResult(image) : Fake.Unexpected(m));
CaptureSequence sequence = null;
bool sourceClosed = false, invalid = false;
int starts = 0, restores = 0;
async IAsyncEnumerable<IExposureData> HostStream([EnumeratorCancellation] CancellationToken ct) {
    try { yield return invalid ? null : exposure; await Task.Delay(Timeout.Infinite, ct); }
    finally { sourceClosed = true; }
}
var mediator = Fake.Of<ICameraMediator>((m, a) => {
    switch (m.Name) {
        case "GetInfo": return camera;
        case "LiveView": starts++; sequence = (CaptureSequence)a[0]; return HostStream((CancellationToken)a[1]);
        case "SetReadoutMode": Check(sourceClosed && (short)a[0] == 1, "Read mode restoration follows stream close"); restores++; return null;
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
camera.DeviceId = "ToupTek-test";
Check(!model.SupportsFocusStreaming, "Native ToupTek follows NINA's disabled LiveView capability");
await Reject<InvalidOperationException>(() => { model.StartFocusStreaming(.1,32,50,50,default); return Task.CompletedTask; },
    "ToupTek without host LiveView support rejects before entering the native video path");
Check(starts == 1, "ToupTek capability rejection makes no host streaming call");
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
