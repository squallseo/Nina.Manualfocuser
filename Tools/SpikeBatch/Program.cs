using Cwseo.NINA.ManualFocuser.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Cwseo.NINA.ManualFocuser.Tools.SpikeBatch {

    internal static class Program {

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static int Main(string[] args) {
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help") {
                PrintUsage();
                return 1;
            }

            try {
                return Run(args);
            } catch (Exception e) {
                Console.Error.WriteLine("ERROR: " + e.Message);
                Console.Error.WriteLine(e.StackTrace);
                return 2;
            }
        }

        private static void PrintUsage() {
            Console.WriteLine(@"
SpikeBatch - offline evaluation of the spike focus metric over a FITS focus sweep

  SpikeBatch <folder> [options]

The x axis comes from the FOCPOS / FOCUSPOS header keyword. Stars are seeded once
from the seed frame and then tracked through the sweep, exactly as the plugin does
inside N.I.N.A.

Options
  --out <path>            CSV output (default: <folder>/spike-batch.csv)
  --limit <n>             only process the first n frames
  --seed-frame <n>        frame index used to seed the star list (default 0)
  --reseed                re-detect stars on every frame instead of tracking
  --auto-angle            measure the spike orientation per frame and use it
                          instead of --angle (it is measured and reported either way)
  --peak-sigma <v>        star detection threshold in sigma above background (default 40)
  --max-seed-stars <n>    candidates handed to the selector (default 400)

  --angle <deg>           spike angle            (default 90)
  --tau <px>              core sigma             (default 1.5)
  --core-reject <px>      radial core rejection  (default 4)
  --axis-sigma <px>       along-axis window      (default 25)
  --axis-reject <px>      along-axis rejection   (default 6)
  --beta-var <v>          variance weight        (default 1)
  --beta-split <v>        split penalty weight   (default 4)
  --split-power <v>       split penalty power    (default 2)
  --roi-scale <v>         ROI half size factor   (default 2)
  --bg-ring <v>           background ring        (default 0.7)
  --min-star <px>         minimum seed star size (default 6)
  --max-stars <n>         stars used per frame   (default 5)

  --sweep <name>=<v1,v2,..>   evaluate several values of one parameter in one pass.
                              name is one of: angle, tau, core-reject, axis-sigma,
                              axis-reject, beta-var, beta-split, split-power,
                              roi-scale, bg-ring, max-stars

Example
  SpikeBatch ""C:\StellaC\QHY600M\Snapshot\2026-03-21_dkkim"" --angle 90 --sweep tau=1.5,4,8
");
        }

        // ==========================================================
        private sealed class Variant {
            public string Name;
            public SpikeAnalysisParams Params;
            public SpikeTrackingState State;
            public List<(int pos, double j, double varC, double varG, double kurt, int stars, double angle)> Points
                = new List<(int, double, double, double, double, int, double)>();
        }

        private static int Run(string[] args) {
            string folder = args[0];
            if (!Directory.Exists(folder)) {
                Console.Error.WriteLine($"Not a folder: {folder}");
                return 1;
            }

            var opt = ParseOptions(args);

            var baseParams = new SpikeAnalysisParams {
                spikeAngleDeg = opt.GetD("angle", 90),
                coreSigmaPx = opt.GetD("tau", 1.5),
                coreRejectSigmaPx = opt.GetD("core-reject", 4),
                axisSigmaPx = opt.GetD("axis-sigma", 25),
                axisRejectSigmaPx = opt.GetD("axis-reject", 6),
                betaVar = opt.GetD("beta-var", 1),
                betaSplit = opt.GetD("beta-split", 4),
                splitPower = opt.GetD("split-power", 2),
                roiScale = opt.GetD("roi-scale", 2),
                bgRingFraction = opt.GetD("bg-ring", 0.7),
                minStarSizePx = opt.GetD("min-star", 6),
                maxStarS = (int)opt.GetD("max-stars", 5),
                minUsedStarsForValidFrame = 1,
                autoSpikeAngle = opt.Flags.Contains("auto-angle")
            };

            var variants = BuildVariants(baseParams, opt.Sweep);

            // ---- enumerate frames ----
            var files = Directory.GetFiles(folder, "*.fit*", SearchOption.TopDirectoryOnly)
                                 .Where(f => f.EndsWith(".fit", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".fits", StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            int limit = (int)opt.GetD("limit", 0);
            if (limit > 0 && files.Count > limit) files = files.Take(limit).ToList();

            if (files.Count == 0) {
                Console.Error.WriteLine("No FITS files found");
                return 1;
            }

            Console.WriteLine($"Frames      : {files.Count}");
            Console.WriteLine($"Variants    : {variants.Count} ({string.Join(", ", variants.Select(v => v.Name))})");

            string outPath = opt.GetS("out", Path.Combine(folder, "spike-batch.csv"));
            bool reseed = opt.Flags.Contains("reseed");
            int seedFrame = (int)opt.GetD("seed-frame", 0);
            double peakSigma = opt.GetD("peak-sigma", 40);
            int maxSeedStars = (int)opt.GetD("max-seed-stars", 400);

            var csv = new StringBuilder();
            csv.AppendLine("file,focpos,variant,status,usedStars,J,spread,varC,varG,kurtosis,hfr,hfrStdev,measuredAngle,angleStrength,usedAngle");

            var hfrByPos = new List<(int pos, double hfr)>();
            var sw = Stopwatch.StartNew();

            // seed frame first so tracking starts from a known state
            var order = Enumerable.Range(0, files.Count).ToList();
            if (seedFrame > 0 && seedFrame < files.Count) {
                order.Remove(seedFrame);
                order.Insert(0, seedFrame);
            }

            bool seeded = false;
            int processed = 0;

            foreach (var idx in order) {
                string file = files[idx];
                string name = Path.GetFileName(file);

                FitsImage img;
                try {
                    img = FitsImage.Load(file);
                } catch (Exception e) {
                    Console.Error.WriteLine($"  {name}: skipped ({e.Message})");
                    continue;
                }

                int pos = FitsImage.FocuserPosition(img.Header) ?? idx;
                var bg = StarFinder.EstimateBackground(img.Data);

                if (!seeded || reseed) {
                    var seeds = StarFinder.Find(img.Data, img.Width, img.Height, bg,
                                                peakSigma: peakSigma, maxStars: maxSeedStars);
                    if (seeds.Count == 0) {
                        Console.Error.WriteLine($"  {name}: no seed stars found");
                        continue;
                    }

                    foreach (var v in variants) v.State = SpikeCore.CreateTrackingState(seeds, v.Params);

                    if (!seeded) {
                        seeded = true;
                        ReportSeed(img, bg, seeds, variants[0]);
                    }
                }

                bool firstEvaluated = processed == 0;

                // ---- HFR reference over the tracked stars ----
                double hfrAvg = double.NaN, hfrStd = 0;
                var refState = variants[0].State;
                if (refState?.TrackedStars != null && refState.TrackedStars.Count > 0) {
                    var hfrs = new List<double>();
                    foreach (var t in refState.TrackedStars) {
                        double h = StarFinder.ComputeHFR(img.Data, img.Width, img.Height, t.X, t.Y,
                                                         Math.Max(8, t.BaseSizePx), bg.Median);
                        if (!double.IsNaN(h)) hfrs.Add(h);
                    }
                    if (hfrs.Count > 0) {
                        hfrAvg = SpikeCore.Median(hfrs);
                        hfrStd = SpikeCore.StdDevSample(hfrs);
                        hfrByPos.Add((pos, hfrAvg));
                    }
                }

                foreach (var v in variants) {
                    var r = SpikeCore.Evaluate(img.Data, img.Width, img.Height, v.Params, v.State);

                    csv.Append(Csv(name)).Append(',')
                       .Append(pos.ToString(Inv)).Append(',')
                       .Append(Csv(v.Name)).Append(',')
                       .Append(r.Status).Append(',')
                       .Append(r.UsedStars.ToString(Inv)).Append(',')
                       .Append(F(r.Metric)).Append(',')
                       .Append(F(r.Spread)).Append(',')
                       .Append(F(r.MedianVarC)).Append(',')
                       .Append(F(r.MedianVarG)).Append(',')
                       .Append(F(r.MedianKurtosis)).Append(',')
                       .Append(F(hfrAvg)).Append(',')
                       .Append(F(hfrStd)).Append(',')
                       .Append(F(r.MeasuredAngleDeg)).Append(',')
                       .Append(F(r.AngleStrength)).Append(',')
                       .Append(F(r.UsedAngleDeg)).AppendLine();

                    if (r.IsValid)
                        v.Points.Add((pos, r.Metric, r.MedianVarC, r.MedianVarG, r.MedianKurtosis, r.UsedStars, r.MeasuredAngleDeg));

                    if (firstEvaluated && ReferenceEquals(v, variants[0])) {
                        Console.WriteLine(r.HasAngleEstimate
                            ? $"Spike angle : measured {r.MeasuredAngleDeg:F1}deg (x{r.AngleStrength:F2}) " +
                              $"| metric using {r.UsedAngleDeg:F1}deg {(v.Params.autoSpikeAngle ? "(auto)" : "(configured)")}"
                            : "Spike angle : no clear orientation on the seed frame");
                        Console.WriteLine();
                    }
                }

                processed++;
                Console.Write($"\r  processed {processed}/{files.Count}  ({name})            ");
            }

            Console.WriteLine();
            Console.WriteLine($"Elapsed     : {sw.Elapsed.TotalSeconds:F1}s");

            File.WriteAllText(outPath, csv.ToString(), Encoding.UTF8);
            Console.WriteLine($"CSV         : {outPath}");

            PrintCurves(variants, hfrByPos);
            PrintQuality(variants, hfrByPos);

            return 0;
        }

        // ==========================================================
        private static void ReportSeed(FitsImage img, BackgroundStats bg, List<SpikeSeedStar> seeds, Variant v) {
            Console.WriteLine($"Image       : {img.Width} x {img.Height}, BITPIX {img.GetInt("BITPIX", 0)}");
            Console.WriteLine($"Background  : median {bg.Median:F1}, sigma {bg.Sigma:F2}");
            Console.WriteLine($"Seed stars  : {seeds.Count} detected");

            var selected = SpikeCore.CreateTrackingState(seeds, v.Params);
            if (selected == null) {
                Console.WriteLine("             -> selector rejected all of them");
                return;
            }

            Console.WriteLine($"             -> {selected.TrackedStars.Count} used: " +
                string.Join(", ", selected.TrackedStars.Select(t => $"({t.X:F0},{t.Y:F0}) {t.BaseSizePx}px")));
        }

        private static void PrintCurves(List<Variant> variants, List<(int pos, double hfr)> hfrByPos) {
            Console.WriteLine();
            Console.WriteLine("=== curves (median per focuser position) ===");

            var hfrMap = hfrByPos.GroupBy(p => p.pos)
                                 .ToDictionary(g => g.Key, g => SpikeCore.Median(g.Select(x => x.hfr).ToList()));

            foreach (var v in variants) {
                var byPos = v.Points.GroupBy(p => p.pos)
                                    .OrderBy(g => g.Key)
                                    .Select(g => (pos: g.Key,
                                                  j: SpikeCore.Median(g.Select(x => x.j).ToList()),
                                                  varC: SpikeCore.Median(g.Select(x => x.varC).ToList()),
                                                  kurt: SpikeCore.Median(g.Select(x => x.kurt).ToList()),
                                                  stars: (int)Math.Round(g.Average(x => x.stars)),
                                                  angle: SpikeCore.CircularMedianDeg(
                                                      g.Select(x => x.angle).Where(a => !double.IsNaN(a)).ToList())))
                                    .ToList();

                if (byPos.Count == 0) { Console.WriteLine($"\n[{v.Name}] no valid points"); continue; }

                double jMin = byPos.Min(p => p.j);
                double jMax = byPos.Max(p => p.j);

                Console.WriteLine($"\n[{v.Name}]");
                Console.WriteLine("   pos      HFR       J        varC     kurt   ang  n   " + new string('-', 30));
                foreach (var p in byPos) {
                    string hfrs = hfrMap.TryGetValue(p.pos, out var h) ? h.ToString("F2", Inv).PadLeft(7) : "      -";
                    string ang = double.IsNaN(p.angle) ? "  -" : p.angle.ToString("F0", Inv).PadLeft(3);
                    int bar = jMax > jMin ? (int)Math.Round(30 * (p.j - jMin) / (jMax - jMin)) : 0;
                    Console.WriteLine($"  {p.pos,5}  {hfrs}  {p.j,8:F3} {p.varC,8:F3} {p.kurt,6:F2}  {ang}  {p.stars,2}   {new string('#', Math.Max(0, bar))}");
                }
            }
        }

        // ==========================================================
        // Quality: a metric is only useful if the change across the sweep is
        // large compared to the scatter at a repeated position.
        // ==========================================================
        private static void PrintQuality(List<Variant> variants, List<(int pos, double hfr)> hfrByPos) {
            Console.WriteLine();
            Console.WriteLine("=== quality (range / repeat-scatter) ===");
            Console.WriteLine("  higher SNR is better; vertex is the parabola minimum over the lowest 60% of the curve");
            Console.WriteLine();
            Console.WriteLine($"  {"series",-28} {"n",3} {"argmin",8} {"vertex",9} {"range",10} {"repeatSd",9} {"SNR",7}");

            Report("HFR (reference)", hfrByPos.Select(p => (p.pos, p.hfr)).ToList());
            foreach (var v in variants)
                Report(v.Name, v.Points.Select(p => (p.pos, p.j)).ToList());

            Console.WriteLine();
        }

        private static void Report(string label, List<(int pos, double val)> points) {
            if (points.Count == 0) {
                Console.WriteLine($"  {label,-28} {"-",3} {"-",8} {"-",9} {"-",10} {"-",9} {"-",7}");
                return;
            }

            var byPos = points.GroupBy(p => p.pos)
                              .OrderBy(g => g.Key)
                              .Select(g => (pos: g.Key, vals: g.Select(x => x.val).ToList()))
                              .ToList();

            var curve = byPos.Select(g => (pos: (double)g.pos, val: SpikeCore.Median(g.vals))).ToList();

            double min = curve.Min(c => c.val);
            double max = curve.Max(c => c.val);
            double range = max - min;
            double argmin = curve.First(c => c.val == min).pos;

            // pooled scatter from positions that were visited more than once
            var repeats = byPos.Where(g => g.vals.Count >= 2).ToList();
            double repeatSd = double.NaN;
            if (repeats.Count > 0) {
                double sumSq = 0;
                int dof = 0;
                foreach (var g in repeats) {
                    double m = g.vals.Average();
                    sumSq += g.vals.Sum(x => (x - m) * (x - m));
                    dof += g.vals.Count - 1;
                }
                if (dof > 0) repeatSd = Math.Sqrt(sumSq / dof);
            }

            double snr = (!double.IsNaN(repeatSd) && repeatSd > 0) ? range / repeatSd : double.NaN;
            double vertex = ParabolaVertex(curve, min, range);

            Console.WriteLine($"  {label,-28} {curve.Count,3} {argmin,8:F0} {Fmt(vertex, "F0"),9} {range,10:F3} {Fmt(repeatSd, "F4"),9} {Fmt(snr, "F1"),7}");
        }

        // Fits a parabola to the bottom part of the curve, where the minimum is.
        private static double ParabolaVertex(List<(double pos, double val)> curve, double min, double range) {
            if (range <= 0) return double.NaN;
            var sel = curve.Where(c => c.val <= min + 0.6 * range).ToList();
            if (sel.Count < 3) return double.NaN;

            double sx = 0, sx2 = 0, sx3 = 0, sx4 = 0, sy = 0, sxy = 0, sx2y = 0;
            double x0 = sel.Average(c => c.pos);
            foreach (var (pos, val) in sel) {
                double x = pos - x0;
                double x2 = x * x;
                sx += x; sx2 += x2; sx3 += x2 * x; sx4 += x2 * x2;
                sy += val; sxy += x * val; sx2y += x2 * val;
            }
            int n = sel.Count;

            // normal equations for y = a x^2 + b x + c
            double[,] m = {
                { sx4, sx3, sx2, sx2y },
                { sx3, sx2, sx,  sxy  },
                { sx2, sx,  n,   sy   }
            };

            for (int i = 0; i < 3; i++) {
                int piv = i;
                for (int r = i + 1; r < 3; r++) if (Math.Abs(m[r, i]) > Math.Abs(m[piv, i])) piv = r;
                if (Math.Abs(m[piv, i]) < 1e-12) return double.NaN;
                if (piv != i) for (int c = 0; c < 4; c++) (m[i, c], m[piv, c]) = (m[piv, c], m[i, c]);
                for (int r = 0; r < 3; r++) {
                    if (r == i) continue;
                    double f = m[r, i] / m[i, i];
                    for (int c = i; c < 4; c++) m[r, c] -= f * m[i, c];
                }
            }

            double a = m[0, 3] / m[0, 0];
            double b = m[1, 3] / m[1, 1];
            if (Math.Abs(a) < 1e-15 || a <= 0) return double.NaN;
            return x0 - b / (2 * a);
        }

        // ==========================================================
        private static List<Variant> BuildVariants(SpikeAnalysisParams baseParams, (string name, List<double> values)? sweep) {
            var list = new List<Variant>();

            if (sweep == null) {
                list.Add(new Variant { Name = "default", Params = baseParams });
                return list;
            }

            var (name, values) = sweep.Value;
            foreach (var value in values) {
                var p = baseParams.Clone();
                Apply(p, name, value);
                list.Add(new Variant { Name = $"{name}={value.ToString(Inv)}", Params = p });
            }
            return list;
        }

        private static void Apply(SpikeAnalysisParams p, string name, double v) {
            switch (name) {
                case "angle": p.spikeAngleDeg = v; break;
                case "tau": p.coreSigmaPx = v; break;
                case "core-reject": p.coreRejectSigmaPx = v; break;
                case "axis-sigma": p.axisSigmaPx = v; break;
                case "axis-reject": p.axisRejectSigmaPx = v; break;
                case "beta-var": p.betaVar = v; break;
                case "beta-split": p.betaSplit = v; break;
                case "split-power": p.splitPower = v; break;
                case "roi-scale": p.roiScale = v; break;
                case "bg-ring": p.bgRingFraction = v; break;
                case "max-stars": p.maxStarS = (int)v; break;
                default: throw new ArgumentException($"Unknown sweep parameter '{name}'");
            }
        }

        // ==========================================================
        private sealed class Options {
            public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public (string name, List<double> values)? Sweep;

            public double GetD(string key, double fallback)
                => Values.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, Inv, out var r) ? r : fallback;

            public string GetS(string key, string fallback)
                => Values.TryGetValue(key, out var v) ? v : fallback;
        }

        private static readonly HashSet<string> KnownFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "reseed", "auto-angle" };

        private static Options ParseOptions(string[] args) {
            var opt = new Options();
            for (int i = 1; i < args.Length; i++) {
                string a = args[i];
                if (!a.StartsWith("--")) continue;
                string key = a.Substring(2);

                if (KnownFlags.Contains(key)) { opt.Flags.Add(key); continue; }

                if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for --{key}");
                string value = args[++i];

                if (key.Equals("sweep", StringComparison.OrdinalIgnoreCase)) {
                    int eq = value.IndexOf('=');
                    if (eq <= 0) throw new ArgumentException("--sweep expects name=v1,v2,...");
                    var vals = value.Substring(eq + 1)
                                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => double.Parse(s.Trim(), NumberStyles.Float, Inv))
                                    .ToList();
                    opt.Sweep = (value.Substring(0, eq).Trim(), vals);
                    continue;
                }

                opt.Values[key] = value;
            }
            return opt;
        }

        private static string F(double v) => double.IsNaN(v) ? "" : v.ToString("F6", Inv);
        private static string Fmt(double v, string fmt) => double.IsNaN(v) ? "-" : v.ToString(fmt, Inv);
        private static string Csv(string s) => s.Contains(',') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
