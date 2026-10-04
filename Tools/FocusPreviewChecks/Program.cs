using System.Reflection;
using Cwseo.NINA.ManualFocuser.Models;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

int passed = 0;
void Check(bool condition, string message) {
    if (!condition) throw new Exception(message);
    passed++;
    Console.WriteLine("PASS " + message);
}
async Task Reject<T>(Func<Task> action, string message) where T : Exception {
    bool rejected = false;
    try { await action(); } catch (T) { rejected = true; }
    Check(rejected, message);
}

var camera = new CameraInfo { Connected = true, XSize = 128, YSize = 96,
    CanSubSample = true, ExposureMin = 0.01, ExposureMax = 30 };
int captures = 0;
CaptureSequence captured = null;
int frameWidth = 32, frameHeight = 32;
ushort[] pixels = Enumerable.Range(0, 1024).Select(i => (ushort)i).ToArray();
bool noExposure = false, noImage = false, throwCapture = false;
var array = Fake.Of<IImageArray>((m, a) => m.Name == "get_FlatArray" ? pixels : Fake.Unexpected(m));
var image = Fake.Of<IImageData>((m, a) => m.Name switch {
    "get_Properties" => new ImageProperties(frameWidth, frameHeight, 16, false, 1, 1),
    "get_Data" => array,
    _ => Fake.Unexpected(m)
});
var exposure = Fake.Of<IExposureData>((m, a) => m.Name == "ToImageData"
    ? Task.FromResult(noImage ? null : image) : Fake.Unexpected(m));
var cameraMediator = Fake.Of<ICameraMediator>((m, a) => m.Name == "GetInfo" ? camera : Fake.Unexpected(m));
var imagingMediator = Fake.Of<IImagingMediator>((m, a) => {
    if (m.Name != "CaptureImage") return Fake.Unexpected(m);
    captures++;
    captured = (CaptureSequence)a[0];
    if (throwCapture) throw new InvalidOperationException("driver capture failure");
    return Task.FromResult(noExposure ? null : exposure);
});
var model = new ManualFocuserModel(null, imagingMediator, cameraMediator, null, null);
Task<(double[] Pixels, int Width, int Height, bool HardwareRoi)> Capture(CancellationToken token = default)
    => model.CaptureFocusPreviewAsync(0.2, 32, 75, 25, token);

var roi = await Capture();
Check(captures == 1 && captured.ExposureTime == 0.2 && captured.Binning.X == 1 && captured.Binning.Y == 1,
    "Preview captures once using explicit exposure and 1x1 binning");
Check(captured.EnableSubSample && captured.SubSambleRectangle.X == 80 && captured.SubSambleRectangle.Y == 8
    && captured.SubSambleRectangle.Width == 32 && captured.SubSambleRectangle.Height == 32,
    "Supported camera receives requested hardware ROI coordinates");
Check(roi.HardwareRoi && roi.Width == 32 && roi.Height == 32 && roi.Pixels[0] == 0 && roi.Pixels[^1] == 1023,
    "Hardware ROI pixels are preserved without applying full-frame offsets twice");

frameWidth = 128; frameHeight = 96;
pixels = Enumerable.Range(0, frameWidth * frameHeight).Select(i => (ushort)i).ToArray();
var overview = await model.CaptureFocusPreviewAsync(.2, 32, 75, 25, default, overview: true);
Check(!captured.EnableSubSample && overview.Width == 128 && overview.Height == 96 && overview.Pixels[^1] == pixels[^1],
    "ROI selection overview disables camera crop and preserves entire sensor frame");
var ignoredRoi = await Capture();
Check(!ignoredRoi.HardwareRoi && ignoredRoi.Pixels[0] == 8 * 128 + 80
    && ignoredRoi.Pixels[^1] == 39 * 128 + 111,
    "Driver returning full frame despite hardware ROI is cropped at requested coordinates");
camera.CanSubSample = false;
var softwareRoi = await Capture();
Check(!captured.EnableSubSample && !softwareRoi.HardwareRoi
    && softwareRoi.Pixels.SequenceEqual(ignoredRoi.Pixels),
    "Camera without hardware ROI uses equivalent software crop");
var edgeRoi = await model.CaptureFocusPreviewAsync(0.1, 32, 100, 100, default);
Check(edgeRoi.Pixels[0] == 64 * 128 + 96 && edgeRoi.Pixels[^1] == pixels[^1],
    "ROI near sensor boundary clamps safely to image bounds");

int before = captures;
camera.ExposureMin = 0.5;
await Reject<InvalidOperationException>(async () => await Capture(), "Below-minimum exposure rejects before capture");
Check(captures == before, "Invalid exposure performs no camera capture");
camera.ExposureMin = 0.01;
using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
await Reject<OperationCanceledException>(async () => await Capture(cancellation.Token), "Canceled preview rejects before capture");
Check(captures == before, "Pre-canceled preview performs no camera capture");
camera.Connected = false;
await Reject<InvalidOperationException>(async () => await Capture(), "Disconnected camera rejects preview");
Check(captures == before, "Disconnected camera performs no capture");
camera.Connected = true;
noExposure = true;
await Reject<InvalidOperationException>(async () => await Capture(), "Null exposure fails explicitly");
Check(captures == before + 1, "Null exposure never retries blindly");
noExposure = false; noImage = true;
await Reject<InvalidOperationException>(async () => await Capture(), "Null converted image fails explicitly");
Check(captures == before + 2, "Null converted image never retries blindly");
noImage = false; throwCapture = true;
await Reject<InvalidOperationException>(async () => await Capture(), "Native capture failure propagates");
Check(captures == before + 3, "Native capture failure never retries blindly");
throwCapture = false;
pixels = new ushort[3];
await Reject<InvalidOperationException>(async () => await Capture(), "Inconsistent image dimensions reject instead of reading out of bounds");
Console.WriteLine($"{passed} integration checks passed; no hardware accessed.");

public class Fake : DispatchProxy {
    public Func<MethodInfo, object[], object> Handler;
    protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
    public static T Of<T>(Func<MethodInfo, object[], object> handler) where T : class {
        T proxy = Create<T, Fake>();
        ((Fake)(object)proxy).Handler = handler;
        return proxy;
    }
    public static object Unexpected(MethodInfo method) => throw new Exception("Unexpected mediator/image API call: " + method.Name);
}
