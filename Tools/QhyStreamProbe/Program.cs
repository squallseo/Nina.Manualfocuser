using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

// Standalone hardware diagnostic. Never load this into the plugin/NINA process.
var host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "N.I.N.A. - Nighttime Imaging 'N' Astronomy");
var sdk = Path.Combine(host, "External", "x64", "QHYCCD", "qhyccd.dll");
if (args.Contains("--host-api")) {
    foreach (var file in Directory.GetFiles(host, "NINA*.dll")) {
        using var pe = new PEReader(File.OpenRead(file));
        var md = pe.GetMetadataReader();
        foreach (var typeHandle in md.TypeDefinitions) {
            var type = md.GetTypeDefinition(typeHandle);
            var name = md.GetString(type.Name);
            if (name is not ("QHYCamera" or "ICameraMediator" or "IImagingMediator" or "ICamera")) continue;
            Console.WriteLine($"{Path.GetFileName(file)} {md.GetString(type.Namespace)}.{name}");
            foreach (var methodHandle in type.GetMethods()) {
                var method = md.GetMethodDefinition(methodHandle);
                var methodName = md.GetString(method.Name);
                if (!methodName.Contains("Live", StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine($"  {methodName} visibility={method.Attributes & MethodAttributes.MemberAccessMask}");
                if (methodName == "get_CanShowLiveView" && method.RelativeVirtualAddress != 0)
                    Console.WriteLine($"  IL={Convert.ToHexString(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!)} (16=ldc.i4.0, 2A=ret)");
            }
        }
    }
    return;
}
if (args.Any(a => a is not ("--stream" or "--cancel-test")) || (args.Contains("--cancel-test") && !args.Contains("--stream")))
    throw new ArgumentException("Options: --host-api or --stream [--cancel-test]; no option queries capabilities only.");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var owners = Process.GetProcesses().Where(p => p.ProcessName.Contains("NINA", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Contains("SharpCap", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Contains("EZCAP", StringComparison.OrdinalIgnoreCase)).Select(p => p.ProcessName).ToArray();
if (owners.Length != 0) throw new InvalidOperationException($"Close camera applications first: {string.Join(", ", owners)}");
NativeLibrary.SetDllImportResolver(typeof(Qhy).Assembly, (name, _, _) => name == "qhyccd" ? NativeLibrary.Load(sdk) : IntPtr.Zero);
Console.WriteLine($"UTC={DateTime.UtcNow:O} sdk={sdk} fileVersion={FileVersionInfo.GetVersionInfo(sdk).FileVersion}");
Check(Qhy.GetQHYCCDSDKVersion(out var year, out var month, out var day, out var subday), "SDK version");
Console.WriteLine($"SDK={year}.{month}.{day}.{subday}");
Check(Qhy.InitQHYCCDResource(), "InitResource");
IntPtr camera = IntPtr.Zero;
bool live = false;
bool initialized = false;
var saved = new Dictionary<int, double>();
uint originalReadMode = 0, fullWidth = 0, fullHeight = 0, chipBits = 0;
try {
    var count = Qhy.ScanQHYCCD();
    Console.WriteLine($"cameraCount={count}");
    if (count != 1) throw new InvalidOperationException("Expected exactly one camera; refusing ambiguous selection.");
    var id = new StringBuilder(256);
    Check(Qhy.GetQHYCCDId(0, id), "CameraId");
    Console.WriteLine($"cameraId={id}");
    if (!id.ToString().StartsWith("QHY600", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Expected QHY600.");
    camera = Qhy.OpenQHYCCD(id.ToString());
    if (camera == IntPtr.Zero) throw new InvalidOperationException("Open camera failed.");
    foreach (var (control, name) in new[] { (57, "singleFrame"), (58, "liveVideo"), (10, "transferBits"), (12, "usbTraffic") })
        Console.WriteLine($"capability.{name}=0x{Qhy.IsQHYCCDControlAvailable(camera, control):X8}");
    Check(Qhy.GetQHYCCDReadMode(camera, out originalReadMode), "ReadMode");
    Check(Qhy.GetQHYCCDNumberOfReadModes(camera, out var modes), "ReadModeCount");
    Console.WriteLine($"readMode={originalReadMode} modes={modes}");
    for (uint i = 0; i < modes; i++) {
        var name = new StringBuilder(256);
        Check(Qhy.GetQHYCCDReadModeName(camera, i, name), "ReadModeName");
        Console.WriteLine($"readMode[{i}]={name}");
    }
    if (!args.Contains("--stream")) return;
    if (Qhy.IsQHYCCDControlAvailable(camera, 58) != 0) throw new InvalidOperationException("SDK reports no live video support.");
    Check(Qhy.SetQHYCCDStreamMode(camera, 0), "Single mode");
    Check(Qhy.InitQHYCCD(camera), "InitCamera(single)");
    initialized = true;
    Check(Qhy.GetQHYCCDChipInfo(camera, out _, out _, out fullWidth, out fullHeight, out _, out _, out chipBits), "ChipInfo");
    Console.WriteLine($"chip={fullWidth}x{fullHeight} nativeBits={chipBits} temperature={Qhy.GetQHYCCDParam(camera, 14)} coolerPWM={Qhy.GetQHYCCDParam(camera, 15)}");
    foreach (var control in new[] { 6, 7, 8, 9, 10, 12 }) {
        if (Qhy.IsQHYCCDControlAvailable(camera, control) != 0) continue;
        var value = Qhy.GetQHYCCDParam(camera, control);
        if (double.IsFinite(value) && value != uint.MaxValue) saved[control] = value;
        Console.WriteLine($"initial.control[{control}]={value}");
    }
    foreach (var exposureMs in args.Contains("--cancel-test") ? new[] { 500 } : new[] { 100, 250, 500 }) {
        Check(Qhy.SetQHYCCDStreamMode(camera, 1), "Stream mode");
        Check(Qhy.InitQHYCCD(camera), "InitCamera(stream)");
        RestoreControls(camera, saved);
        Check(Qhy.SetQHYCCDBitsMode(camera, 16), "16 bits");
        Check(Qhy.SetQHYCCDBinMode(camera, 1, 1), "Bin1");
        uint x = (fullWidth / 2 - 128) / 4 * 4, y = (fullHeight / 2 - 128) / 4 * 4;
        Check(Qhy.SetQHYCCDResolution(camera, x, y, 256, 256), "ROI");
        Check(Qhy.SetQHYCCDParam(camera, 8, exposureMs * 1000.0), "Exposure(us)");
        var memory = Qhy.GetQHYCCDMemLength(camera);
        // Full sensor allocation protects against a driver ignoring ROI; no per-frame allocation.
        var allocation = Math.Max((long)memory, checked((long)fullWidth * fullHeight * 2 + 4096));
        if (memory == uint.MaxValue || allocation > 512L * 1024 * 1024) throw new InvalidOperationException($"Invalid buffer size {memory}/{allocation}");
        var buffer = new byte[checked((int)allocation)];
        Console.WriteLine($"START exposureMs={exposureMs} roi={x},{y},256,256 buffer={buffer.Length}");
        live = true; // Cleanup even when Begin reports a partial failure.
        Check(Qhy.BeginQHYCCDLive(camera), "BeginLive");
        var clock = Stopwatch.StartNew();
        if (args.Contains("--cancel-test")) cancellation.CancelAfter(1500);
        double previous = 0;
        var intervals = new List<double>();
        int frames = 0, misses = 0;
        while (!cancellation.IsCancellationRequested && frames < 23 && clock.Elapsed < TimeSpan.FromSeconds(25)) {
            var result = Qhy.GetQHYCCDLiveFrame(camera, out var w, out var h, out var bits, out var channels, buffer);
            if (result == uint.MaxValue) { misses++; Thread.Sleep(2); continue; }
            Check(result, "LiveFrame");
            var now = clock.Elapsed.TotalMilliseconds;
            if (w != 256 || h != 256 || bits != 16 || channels != 1) throw new InvalidOperationException($"Unexpected frame {w}x{h}/{bits}/{channels}");
            if (frames >= 3) intervals.Add(now - previous);
            if (frames == 0 || frames == 22) {
                var pixels = MemoryMarshal.Cast<byte, ushort>(buffer.AsSpan(0, checked((int)(w * h * 2))));
                ushort min = ushort.MaxValue, max = 0;
                long sum = 0;
                foreach (var pixel in pixels) { min = Math.Min(min, pixel); max = Math.Max(max, pixel); sum += pixel; }
                Console.WriteLine($"FRAME index={frames} ms={now:F1} shape={w}x{h}/{bits}/{channels} min={min} max={max} mean={(double)sum / pixels.Length:F2}");
            }
            frames++; previous = now;
        }
        var stopTimer = Stopwatch.StartNew();
        Check(Qhy.StopQHYCCDLive(camera), "StopLive");
        live = false;
        Console.WriteLine($"STOP elapsedMs={stopTimer.Elapsed.TotalMilliseconds:F2} cancelled={cancellation.IsCancellationRequested} frames={frames}");
        if (!cancellation.IsCancellationRequested && frames != 23) throw new TimeoutException($"Only {frames} frames within 25 sec.");
        if (intervals.Count > 0) {
            intervals.Sort();
            Console.WriteLine($"RESULT exposureMs={exposureMs} frames={frames} measured={intervals.Count} meanIntervalMs={intervals.Average():F2} medianIntervalMs={intervals[intervals.Count / 2]:F2} minIntervalMs={intervals[0]:F2} maxIntervalMs={intervals[^1]:F2} fps={1000 / intervals.Average():F2} pollsNotReady={misses}");
        }
        Check(Qhy.SetQHYCCDStreamMode(camera, 0), "Restore single mode");
        Check(Qhy.InitQHYCCD(camera), "Restore init");
        RestoreControls(camera, saved);
        if (cancellation.IsCancellationRequested) break;
    }
    Check(Qhy.SetQHYCCDBitsMode(camera, 16), "Single bits");
    Check(Qhy.SetQHYCCDBinMode(camera, 1, 1), "Single bin");
    Check(Qhy.SetQHYCCDResolution(camera, 0, 0, 256, 256), "Single ROI");
    Check(Qhy.SetQHYCCDParam(camera, 8, 100000), "Single exposure");
    var single = new byte[checked((int)((long)fullWidth * fullHeight * 2 + 4096))];
    var expResult = Qhy.ExpQHYCCDSingleFrame(camera);
    if (expResult != 0 && expResult != 0x2001) Check(expResult, "ExpSingle");
    Thread.Sleep(150);
    Check(Qhy.GetQHYCCDSingleFrame(camera, out var sw, out var sh, out var sb, out var sc, single), "GetSingle");
    if (sw != 256 || sh != 256 || sb != 16 || sc != 1) throw new InvalidOperationException("Single-frame dimensions invalid.");
    Console.WriteLine($"SINGLE_AFTER_STREAM=PASS {sw}x{sh}/{sb}/{sc}");
} finally {
    if (camera != IntPtr.Zero) {
        if (live) Console.WriteLine($"cleanup.StopLive=0x{Qhy.StopQHYCCDLive(camera):X8}");
        if (initialized) {
            Console.WriteLine($"cleanup.SetReadMode=0x{Qhy.SetQHYCCDReadMode(camera, originalReadMode):X8}");
            Console.WriteLine($"cleanup.SetSingle=0x{Qhy.SetQHYCCDStreamMode(camera, 0):X8}");
            Console.WriteLine($"cleanup.Init=0x{Qhy.InitQHYCCD(camera):X8}");
            foreach (var (control, value) in saved) Console.WriteLine($"cleanup.control[{control}]=0x{Qhy.SetQHYCCDParam(camera, control, value):X8}");
            Console.WriteLine($"cleanup.FullROI=0x{Qhy.SetQHYCCDResolution(camera, 0, 0, fullWidth, fullHeight):X8}");
        }
        Console.WriteLine($"cleanup.Close=0x{Qhy.CloseQHYCCD(camera):X8}");
    }
    Console.WriteLine($"cleanup.Release=0x{Qhy.ReleaseQHYCCDResource():X8}");
}

static void Check(uint result, string operation) {
    if (result != 0) throw new InvalidOperationException($"{operation}: 0x{result:X8}");
}
static void RestoreControls(IntPtr camera, Dictionary<int, double> saved) {
    foreach (var (control, value) in saved) Check(Qhy.SetQHYCCDParam(camera, control, value), $"Restore control {control}");
}

static class Qhy {
    private const string Dll = "qhyccd";
    [DllImport(Dll)] internal static extern uint InitQHYCCDResource();
    [DllImport(Dll)] internal static extern uint ReleaseQHYCCDResource();
    [DllImport(Dll)] internal static extern uint ScanQHYCCD();
    [DllImport(Dll, CharSet = CharSet.Ansi)] internal static extern uint GetQHYCCDId(uint index, StringBuilder id);
    [DllImport(Dll, CharSet = CharSet.Ansi)] internal static extern IntPtr OpenQHYCCD(string id);
    [DllImport(Dll)] internal static extern uint CloseQHYCCD(IntPtr camera);
    [DllImport(Dll)] internal static extern uint IsQHYCCDControlAvailable(IntPtr camera, int control);
    [DllImport(Dll)] internal static extern uint SetQHYCCDStreamMode(IntPtr camera, byte mode);
    [DllImport(Dll)] internal static extern uint InitQHYCCD(IntPtr camera);
    [DllImport(Dll)] internal static extern uint GetQHYCCDSDKVersion(out uint year, out uint month, out uint day, out uint subday);
    [DllImport(Dll)] internal static extern uint GetQHYCCDReadMode(IntPtr camera, out uint mode);
    [DllImport(Dll)] internal static extern uint SetQHYCCDReadMode(IntPtr camera, uint mode);
    [DllImport(Dll)] internal static extern uint GetQHYCCDNumberOfReadModes(IntPtr camera, out uint count);
    [DllImport(Dll, CharSet = CharSet.Ansi)] internal static extern uint GetQHYCCDReadModeName(IntPtr camera, uint mode, StringBuilder name);
    [DllImport(Dll)] internal static extern uint GetQHYCCDChipInfo(IntPtr camera, out double chipW, out double chipH, out uint w, out uint h, out double pixelW, out double pixelH, out uint bits);
    [DllImport(Dll)] internal static extern double GetQHYCCDParam(IntPtr camera, int control);
    [DllImport(Dll)] internal static extern uint SetQHYCCDParam(IntPtr camera, int control, double value);
    [DllImport(Dll)] internal static extern uint SetQHYCCDBitsMode(IntPtr camera, uint bits);
    [DllImport(Dll)] internal static extern uint SetQHYCCDBinMode(IntPtr camera, uint x, uint y);
    [DllImport(Dll)] internal static extern uint SetQHYCCDResolution(IntPtr camera, uint x, uint y, uint w, uint h);
    [DllImport(Dll)] internal static extern uint GetQHYCCDMemLength(IntPtr camera);
    [DllImport(Dll)] internal static extern uint BeginQHYCCDLive(IntPtr camera);
    [DllImport(Dll)] internal static extern uint StopQHYCCDLive(IntPtr camera);
    [DllImport(Dll)] internal static extern uint GetQHYCCDLiveFrame(IntPtr camera, out uint w, out uint h, out uint bits, out uint channels, [Out] byte[] buffer);
    [DllImport(Dll)] internal static extern uint ExpQHYCCDSingleFrame(IntPtr camera);
    [DllImport(Dll)] internal static extern uint GetQHYCCDSingleFrame(IntPtr camera, out uint w, out uint h, out uint bits, out uint channels, [Out] byte[] buffer);
}
