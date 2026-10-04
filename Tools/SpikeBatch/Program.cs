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
  --saved-stars           use sibling Hocus Focus Region00 JSON seeds and HFR
  --compare               compare all metric kinds on identical input frames
  --metric <kind>         legacy, sigma, hfw, fwhm, split, or hybrid
  --u-max <px>            half-range of the profile (default 40)
  --dump <folder>         save per-frame ROI images and one-pixel profiles
  --dump-stars <n>        dump this many tracked stars (default 1)
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
        private sealed class Point {
            public int Pos;
            public double J, VarC, Kurt, Angle, Sigma, Hfw, Fwhm, Sep, Dip, Snr, Hfr;
            public int Stars;
        }

        private sealed class Variant {
            public string Name;
            public SpikeAnalysisParams Params;
            public SpikeTrackingState State;
            public List<Point> Points = new List<Point>();
            public List<SpikeFrameResult> Frames = new List<SpikeFrameResult>();
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
                autoSpikeAngle = opt.Flags.Contains("auto-angle"),
                uMaxPx = opt.GetD("u-max", 40),
                splitWeight = opt.GetD("split-weight", 1.0),
                peakThresholdFraction = opt.GetD("peak-threshold", 0.35)
            };

            if (opt.Values.TryGetValue("metric", out var metricName))
                baseParams.metricKind = ParseMetric(metricName);

            var variants = opt.Flags.Contains("compare")
                ? BuildComparisonVariants(baseParams)
                : BuildVariants(baseParams, opt.Sweep);

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
            bool savedStars = opt.Flags.Contains("saved-stars");
            Console.WriteLine("HFR source  : " + (savedStars ? "saved Hocus Focus result (whole field)" : "offline tracked-star reference"));
            int seedFrame = (int)opt.GetD("seed-frame", 0);
            double peakSigma = opt.GetD("peak-sigma", 40);
            int maxSeedStars = (int)opt.GetD("max-seed-stars", 400);

            string dumpDir = opt.GetS("dump", null);
            var dumps = new List<Diagnostics.FrameDump>();

            var csv = new StringBuilder();
            csv.AppendLine("file,focpos,variant,status,usedStars,J,spread,varC,varG,kurtosis,hfr,hfrStdev,measuredAngle,angleStrength,usedAngle,sigma,hfw,fwhm,separation,dipDepth,profileSnr");

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
                var saved = savedStars ? SavedDetection.LoadForFrame(file) : null;

                if (!seeded || reseed) {
                    var seeds = saved?.Stars ?? StarFinder.Find(img.Data, img.Width, img.Height, bg,
                                                peakSigma: peakSigma, maxStars: maxSeedStars);
                    if (seeds.Count == 0) {
                        Console.Error.WriteLine($"  {name}: no seed stars found");
                    }

                    foreach (var v in variants) v.State = SpikeCore.CreateTrackingState(seeds, v.Params);

                    if (!seeded) {
                        seeded = variants.Any(v => v.State != null);
                        ReportSeed(img, bg, seeds, variants[0]);
                    }
                }
                if (!seeded) {
                    Console.Error.WriteLine($"  {name}: no eligible seed stars; retrying on the next frame");
                }

                bool firstEvaluated = processed == 0;

                // ---- HFR reference over the tracked stars ----
                double hfrAvg = double.NaN, hfrStd = 0;
                var refState = variants[0].State;
                if (saved != null) {
                    hfrAvg = saved.Hfr;
                    hfrStd = saved.HfrStdDev;
                    if (double.IsFinite(hfrAvg) && hfrAvg > 0) hfrByPos.Add((pos, hfrAvg));
                } else if (refState?.TrackedStars != null && refState.TrackedStars.Count > 0) {
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
                    v.Frames.Add(r);

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
                       .Append(F(r.UsedAngleDeg)).Append(',')
                       .Append(F(r.MedianSigma)).Append(',').Append(F(r.MedianHfw)).Append(',')
                       .Append(F(r.MedianFwhm)).Append(',').Append(F(r.MedianSeparation)).Append(',')
                       .Append(F(r.MedianDipDepth)).Append(',').Append(F(r.MedianProfileSnr)).AppendLine();

                    if (r.IsValid) {
                        v.Points.Add(new Point {
                            Pos = pos, J = r.Metric, VarC = r.MedianVarC, Kurt = r.MedianKurtosis,
                            Angle = r.MeasuredAngleDeg, Sigma = r.MedianSigma, Hfw = r.MedianHfw,
                            Fwhm = r.MedianFwhm, Sep = r.MedianSeparation, Dip = r.MedianDipDepth,
                            Snr = r.MedianProfileSnr, Hfr = hfrAvg, Stars = r.UsedStars
                        });
                    }

                    if (firstEvaluated && ReferenceEquals(v, variants[0])) {
                        Console.WriteLine(r.HasAngleEstimate
                            ? $"Spike angle : measured {r.MeasuredAngleDeg:F1}deg (x{r.AngleStrength:F2}) " +
                              $"| metric using {r.UsedAngleDeg:F1}deg {(v.Params.autoSpikeAngle ? "(auto)" : "(configured)")}"
                            : "Spike angle : no clear orientation on the seed frame");
                        Console.WriteLine();
                    }
                }

                // Diagnostic dump uses the brightest tracked star and the angle the
                // reference variant actually used on this frame.
                if (dumpDir != null && refState?.TrackedStars != null && refState.TrackedStars.Count > 0) {
                    double dumpAngle = variants[0].Params.autoSpikeAngle && !double.IsNaN(refState.LastAngleDeg)
                        ? refState.LastAngleDeg
                        : variants[0].Params.spikeAngleDeg;

                    int dumpStars = Math.Clamp((int)opt.GetD("dump-stars", 1), 1, refState.TrackedStars.Count);
                    for (int starIndex = 0; starIndex < dumpStars; starIndex++) {
                        var star = refState.TrackedStars[starIndex];
                        string frameName = Path.GetFileNameWithoutExtension(file);
                        if (dumpStars > 1) frameName += $"_star{starIndex:D2}";
                        var d = Diagnostics.Dump(dumpDir, img.Data, img.Width, img.Height, star,
                                                 variants[0].Params, dumpAngle, pos, hfrAvg, writeImages: true,
                                                 frameName: frameName);
                        if (d != null) dumps.Add(d);
                    }
                }

                processed++;
                Console.Write($"\r  processed {processed}/{files.Count}  ({name})            ");
            }

            Console.WriteLine();
            Console.WriteLine($"Elapsed     : {sw.Elapsed.TotalSeconds:F1}s");

            File.WriteAllText(outPath, csv.ToString(), Encoding.UTF8);
            Console.WriteLine($"CSV         : {outPath}");

            if (dumps.Count > 0) {
                Diagnostics.WriteProfileCsv(Path.Combine(dumpDir, "profiles.csv"), dumps);
                Diagnostics.PrintProfiles(dumps);
                Console.WriteLine($"\nDumped {dumps.Count} ROI crops and profiles to {dumpDir}");
            }

            PrintCurves(variants, hfrByPos);
            PrintQuality(variants, hfrByPos);

            return variants.Any(v => v.Points.Count > 0) ? 0 : 3;
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
                var byPos = Collapse(v.Points);
                if (byPos.Count == 0) { Console.WriteLine($"\n[{v.Name}] no valid points"); continue; }

                double jMin = byPos.Min(p => p.J);
                double jMax = byPos.Max(p => p.J);

                Console.WriteLine($"\n[{v.Name}]");
                Console.WriteLine("   pos      HFR       J      sigma    hfw   fwhm    sep   dip   pSNR  ang  n  " + new string('-', 22));
                foreach (var p in byPos) {
                    string hfrs = hfrMap.TryGetValue(p.Pos, out var h) ? h.ToString("F2", Inv).PadLeft(7) : "      -";
                    string ang = double.IsNaN(p.Angle) ? "  -" : p.Angle.ToString("F0", Inv).PadLeft(3);
                    int bar = jMax > jMin ? (int)Math.Round(22 * (p.J - jMin) / (jMax - jMin)) : 0;
                    Console.WriteLine($"  {p.Pos,5}  {hfrs}  {p.J,7:F2} {p.Sigma,7:F2} {p.Hfw,6:F2} {p.Fwhm,6:F1} {p.Sep,6:F1} {p.Dip,5:F2} {p.Snr,6:F1}  {ang} {p.Stars,2}  {new string('#', Math.Max(0, bar))}");
                }
            }
        }

        /// <summary>Collapses repeated visits to a focuser position into one row.</summary>
        private static List<Point> Collapse(List<Point> points) {
            return points.GroupBy(p => p.Pos).OrderBy(g => g.Key).Select(g => new Point {
                Pos = g.Key,
                J = SpikeCore.Median(g.Select(x => x.J).ToList()),
                VarC = SpikeCore.Median(g.Select(x => x.VarC).ToList()),
                Kurt = SpikeCore.Median(g.Select(x => x.Kurt).ToList()),
                Sigma = SpikeCore.Median(g.Select(x => x.Sigma).ToList()),
                Hfw = SpikeCore.Median(g.Select(x => x.Hfw).ToList()),
                Fwhm = SpikeCore.Median(g.Select(x => x.Fwhm).ToList()),
                Sep = SpikeCore.Median(g.Select(x => x.Sep).ToList()),
                Dip = SpikeCore.Median(g.Select(x => x.Dip).ToList()),
                Snr = SpikeCore.Median(g.Select(x => x.Snr).ToList()),
                Hfr = SpikeCore.Median(g.Select(x => x.Hfr).Where(h => !double.IsNaN(h)).ToList()),
                Stars = (int)Math.Round(g.Average(x => x.Stars)),
                Angle = SpikeCore.CircularMedianDeg(g.Select(x => x.Angle).Where(a => !double.IsNaN(a)).ToList())
            }).ToList();
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
                Report(v.Name, v.Points.Select(p => (p.Pos, p.J)).ToList());

            Console.WriteLine();
            PrintAcceptance(variants, hfrByPos);
        }

        // ==========================================================
        // Acceptance tests
        //
        // The four questions the metric has to answer, scored rather than eyeballed:
        //   1. does it move with HFR at all
        //   2. does it still move where HFR has gone flat - the whole point
        //   3. does it see the spike split
        //   4. does it fail visibly when the spike is gone
        // ==========================================================
        private static void PrintAcceptance(List<Variant> variants, List<(int pos, double hfr)> hfrByPos) {
            var hfrCurve = hfrByPos.GroupBy(p => p.pos).OrderBy(g => g.Key)
                                   .Select(g => (pos: g.Key, hfr: SpikeCore.Median(g.Select(x => x.hfr).ToList())))
                                   .ToList();
            if (hfrCurve.Count < 4) {
                Console.WriteLine("=== acceptance: not enough HFR points ===");
                return;
            }

            double hfrMin = hfrCurve.Min(c => c.hfr);
            // "near focus" = where HFR has flattened out and stops discriminating
            var nearPositions = hfrCurve.Where(c => c.hfr <= hfrMin * 1.5).Select(c => c.pos).ToHashSet();

            Console.WriteLine("=== acceptance ===");
            Console.WriteLine($"  near-focus band: HFR <= {hfrMin * 1.5:F2} ({nearPositions.Count} of {hfrCurve.Count} positions)");
            Console.WriteLine();
            Console.WriteLine($"  {"metric",-12} {"rho(HFR)",9} {"nearGain",9} {"nearSNRx",9} {"splitOn",9} {"failFrac",9}");

            var hfrPoints = hfrCurve.Select(c => new Point { Pos = c.pos, J = c.hfr, Hfr = c.hfr }).ToList();
            var hfrRepeats = hfrByPos.Select(p => (p.pos, value: p.hfr)).ToList();
            ReportAcceptance("HFR (ref)", hfrPoints, hfrCurve, nearPositions, hfrRepeats, hfrRepeats, 0, 0);

            foreach (var v in variants)
                ReportAcceptance(v.Name, Collapse(v.Points), hfrCurve, nearPositions,
                    v.Points.Select(p => (p.Pos, p.J)).ToList(), hfrRepeats,
                    v.Frames.Count == 0 ? double.NaN : v.Frames.Count(r => r.IsValid && r.MedianSeparation > 0) / (double)v.Frames.Count,
                    v.Frames.Count == 0 ? double.NaN : v.Frames.Count(r => !r.IsValid) / (double)v.Frames.Count);

            Console.WriteLine();
            Console.WriteLine("  rho(HFR)  Spearman rank correlation against HFR over the whole sweep (want ~ +1)");
            Console.WriteLine("  nearGain  near-focus contrast of this metric divided by HFR's (want > 1)");
            Console.WriteLine("            amplitude only; zero-median or fewer than 3 positions gives '-'");
            Console.WriteLine("  nearSNRx  near-focus range/repeat-noise divided by HFR's, on paired repeated positions");
            Console.WriteLine("  splitOn   fraction of frames where a split was detected");
            Console.WriteLine("  failFrac  invalid evaluations / all evaluated frames (including dropped frames)");
            Console.WriteLine("  A valid width or orientation alone does not prove the ROI contains diffraction spikes.");
            Console.WriteLine();
        }

        private static void ReportAcceptance(string label, List<Point> curve, List<(int pos, double hfr)> hfrCurve, HashSet<int> near,
            List<(int pos, double value)> raw, List<(int pos, double value)> hfrRaw, double splitOn, double failFrac) {
            if (curve.Count < 4) {
                Console.WriteLine($"  {label,-12} {"-",9} {"-",9} {"-",9} {Fmt(splitOn * 100, "F0") + "%",9} {Fmt(failFrac * 100, "F0") + "%",9}");
                return;
            }

            var hfrByPos = hfrCurve.ToDictionary(c => c.pos, c => c.hfr);
            var paired = curve.Where(p => hfrByPos.ContainsKey(p.Pos)).ToList();

            double rho = Spearman(paired.Select(p => p.J).ToList(), paired.Select(p => hfrByPos[p.Pos]).ToList());

            // near-focus contrast, relative to HFR's own contrast over the same band
            double nearGain = double.NaN;
            var nearPts = paired.Where(p => near.Contains(p.Pos)).ToList();
            if (nearPts.Count >= 3) {
                double mContrast = RelativeSpread(nearPts.Select(p => p.J).ToList());
                double hContrast = RelativeSpread(nearPts.Select(p => hfrByPos[p.Pos]).ToList());
                if (hContrast > 0) nearGain = mContrast / hContrast;
            }

            var repeated = raw.GroupBy(p => p.pos).Where(g => g.Count() >= 2).Select(g => g.Key).ToHashSet();
            repeated.IntersectWith(hfrRaw.GroupBy(p => p.pos).Where(g => g.Count() >= 2).Select(g => g.Key));
            repeated.IntersectWith(near);
            double mSnr = NearSignalToNoise(raw, repeated);
            double hSnr = NearSignalToNoise(hfrRaw, repeated);
            double snrRatio = double.IsFinite(hSnr) && hSnr > 0 ? mSnr / hSnr : double.NaN;

            Console.WriteLine($"  {label,-12} {Fmt(rho, "F3"),9} {Fmt(nearGain, "F2"),9} {Fmt(snrRatio, "F2"),9} {splitOn,9:P0} {failFrac,9:P0}");
        }

        private static double NearSignalToNoise(List<(int pos, double value)> points, HashSet<int> positions) {
            var groups = points.Where(p => positions.Contains(p.pos)).GroupBy(p => p.pos).ToList();
            if (groups.Count < 3) return double.NaN;
            double ss = 0; int dof = 0;
            var medians = new List<double>();
            foreach (var group in groups) {
                double mean = group.Average(p => p.value);
                ss += group.Sum(p => (p.value - mean) * (p.value - mean));
                dof += group.Count() - 1;
                medians.Add(SpikeCore.Median(group.Select(p => p.value).ToList()));
            }
            double noise = dof > 0 ? Math.Sqrt(ss / dof) : 0;
            return noise > 0 ? (medians.Max() - medians.Min()) / noise : double.NaN;
        }

        /// <summary>(max - min) / median. Scale free, so metrics in different units compare.</summary>
        private static double RelativeSpread(List<double> values) {
            var v = values.Where(x => !double.IsNaN(x)).ToList();
            if (v.Count < 2) return 0;
            double med = SpikeCore.Median(v);
            if (Math.Abs(med) < 1e-12) return double.NaN;
            return (v.Max() - v.Min()) / Math.Abs(med);
        }

        private static double Spearman(List<double> a, List<double> b) {
            if (a.Count != b.Count || a.Count < 3) return double.NaN;
            var ra = Rank(a);
            var rb = Rank(b);
            double ma = ra.Average(), mb = rb.Average();
            double num = 0, da = 0, db = 0;
            for (int i = 0; i < ra.Length; i++) {
                num += (ra[i] - ma) * (rb[i] - mb);
                da += (ra[i] - ma) * (ra[i] - ma);
                db += (rb[i] - mb) * (rb[i] - mb);
            }
            return da > 0 && db > 0 ? num / Math.Sqrt(da * db) : double.NaN;
        }

        private static double[] Rank(List<double> values) {
            var idx = Enumerable.Range(0, values.Count).OrderBy(i => values[i]).ToArray();
            var rank = new double[values.Count];
            int j = 0;
            while (j < idx.Length) {
                int k = j;
                while (k + 1 < idx.Length && values[idx[k + 1]] == values[idx[j]]) k++;
                double avg = (j + k) / 2.0 + 1;
                for (int m = j; m <= k; m++) rank[idx[m]] = avg;
                j = k + 1;
            }
            return rank;
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
            double vertex = x0 - b / (2 * a);
            return double.IsFinite(vertex) && vertex >= sel.Min(p => p.pos) && vertex <= sel.Max(p => p.pos)
                ? vertex : double.NaN;
        }

        // ==========================================================
        private static SpikeMetricKind ParseMetric(string name) {
            foreach (SpikeMetricKind k in Enum.GetValues(typeof(SpikeMetricKind)))
                if (k.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)) return k;
            throw new ArgumentException($"Unknown metric '{name}'. Valid: {string.Join(", ", Enum.GetNames(typeof(SpikeMetricKind)))}");
        }

        /// <summary>One variant per metric kind, so every candidate is scored against
        /// the same frames, the same stars and the same angle in a single pass.</summary>
        private static List<Variant> BuildComparisonVariants(SpikeAnalysisParams baseParams) {
            var list = new List<Variant>();
            foreach (SpikeMetricKind k in Enum.GetValues(typeof(SpikeMetricKind))) {
                var p = baseParams.Clone();
                p.metricKind = k;
                list.Add(new Variant { Name = k.ToString().ToLowerInvariant(), Params = p });
            }
            return list;
        }

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
                case "metric": p.metricKind = (SpikeMetricKind)(int)v; break;
                case "u-max": p.uMaxPx = v; break;
                case "split-weight": p.splitWeight = v; break;
                case "peak-threshold": p.peakThresholdFraction = v; break;
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

        private static readonly HashSet<string> KnownFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "reseed", "auto-angle", "compare", "saved-stars" };

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
