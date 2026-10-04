using Cwseo.NINA.ManualFocuser.Models;
using Cwseo.NINA.ManualFocuser.Tools.SpikeBatch;

int passed = 0;
void Check(bool condition, string message) {
    if (!condition) throw new Exception(message);
    passed++;
    Console.WriteLine("PASS " + message);
}
double[] Profile(Func<double, double> f, int radius = 40) =>
    Enumerable.Range(-radius, 2 * radius + 1).Select(u => f(u)).ToArray();
double G(double x, double sigma) => Math.Exp(-x * x / (2 * sigma * sigma));
var parameters = new SpikeAnalysisParams();

var seeds = Enumerable.Range(0, 100).Select(i => new SpikeSeedStar {
    X = i, Y = i, WidthPx = 1, HeightPx = 1, MaxBrightness = 60000
}).ToList();
seeds.Add(new SpikeSeedStar { X = 100, Y = 100, WidthPx = 20, HeightPx = 20, MaxBrightness = 1000 });
var tracked = SpikeCore.CreateTrackingState(seeds, parameters);
Check(tracked?.TrackedStars.Count == 1 && tracked.TrackedStars[0].X == 100,
    "Bright ineligible detections cannot starve a valid seed");

foreach (double sigma in new[] { 2.0, 3.0, 5.0, 8.0 }) {
    Check(SpikeCore.TryComputeProfileTerms(Profile(x => 1000 * G(x, sigma)), parameters, out var t),
        $"Gaussian sigma={sigma} has a measurable profile");
    Check(Math.Abs(t.Fwhm - 2.35482 * sigma) < 0.6,
        $"FWHM sigma={sigma} agrees with the analytic Gaussian width");
    Check(t.Separation == 0, $"Gaussian sigma={sigma} is not a split");
}
SpikeCore.TryComputeProfileTerms(Profile(x => 1000 * G(x, 3.01)), parameters, out var a);
SpikeCore.TryComputeProfileTerms(Profile(x => 1000 * G(x, 3.02)), parameters, out var b);
Check(b.Fwhm > a.Fwhm && b.Fwhm - a.Fwhm < 0.05,
    "Subpixel width changes survive the half-height measurement");

Check(SpikeCore.TryComputeProfileTerms(Profile(x => 1000 * (G(x - 8, 2) + G(x + 8, 2))), parameters, out var split)
    && Math.Abs(split.Separation - 16) <= 1 && split.DipDepth > 0.8,
    "A resolved double line retains its separation and deep valley");
Check(SpikeCore.TryComputeProfileTerms(Profile(x => 1000 * G(x, 10) * (1 + 0.025 * Math.Cos(2 * x))), parameters, out var ripple)
    && ripple.Separation == 0, "Shallow ripples in one line are not a split");
Check(!SpikeCore.TryComputeProfileTerms(Profile(x => 1000 * G(x, 50), 20), parameters, out _),
    "A truncated envelope without both half-height crossings is invalid");
Check(!SpikeCore.TryComputeProfileTerms(Profile(x => 0), parameters, out _), "An empty profile is invalid");
var invalid = Profile(x => 1000 * G(x, 3)); invalid[10] = double.NaN;
Check(!SpikeCore.TryComputeProfileTerms(invalid, parameters, out _), "Nonfinite profile samples are invalid");
Check(!SpikeCore.TryComputeProfileTerms(Profile(x => Math.Sin(x)), parameters, out _),
    "Low-SNR oscillations cannot produce a valid profile width");

const int size = 128;
var noise = new ushort[size * size];
for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
    noise[y * size + x] = (ushort)(1000 + 5 * ((x + y) % 3 - 1));
parameters.enableCentroidTracking = false;
Check(SpikeCore.TryGetDiagnosticRoi(noise, size, size, 64, 64, 12, parameters,
    out var roi, out _, out _, out _) && roi.Min() < 0 && roi.Max() > 0,
    "Background subtraction retains positive and negative noise samples");
var state = new SpikeTrackingState {
    LastAngleDeg = 90, LastAngleStrength = 2,
    TrackedStars = new List<TrackedStar> { new TrackedStar { X = 64, Y = 64, BaseSizePx = 12 } }
};
var frame = SpikeCore.Evaluate(new ushort[size * size], size, size, parameters, state);
Check(!frame.HasAngleEstimate && frame.AngleStrength == 0 && state.LastAngleDeg == 90,
    "A failed current angle is not presented as a fresh successful detection");

string temporary = Path.Combine(Path.GetTempPath(), "SpikeChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try {
    File.WriteAllText(Path.Combine(temporary, "01_Frame00_Region00_star_detection_result.json"), """
        {"AverageHFR": 2.5, "HFRStdDev": 0.1, "StarList": [
        {"$type":"Ignored.External.Type", "Position":{"X":42.25,"Y":23.5},
        "BoundingBox":"32, 13, 20, 18", "MaxBrightness":0.5}]}
        """);
    var saved = SavedDetection.LoadForFrame(Path.Combine(temporary, "01_Frame00_BitDepth16_Bayered0_Focuser100.fits"));
    Check(saved.Hfr == 2.5 && saved.Stars.Count == 1 && saved.Stars[0].WidthPx == 20 && saved.Stars[0].X == 42.25,
        "Saved autofocus stars and HFR load without the detector's CLR types");
} finally {
    File.Delete(Path.Combine(temporary, "01_Frame00_Region00_star_detection_result.json"));
    Directory.Delete(temporary);
}
Console.WriteLine($"{passed} checks passed");
