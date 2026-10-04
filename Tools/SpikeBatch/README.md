# SpikeBatch — offline evaluation of the spike focus metric

Replays a folder of FITS frames through the **exact same metric code** the plugin
runs inside N.I.N.A. (`Models/SpikeCore.cs` is compiled into both), so a parameter
change can be judged in seconds instead of waiting for the next clear night.

The focuser position comes from the `FOCPOS` / `FOCUSPOS` header keyword, so any
folder written by N.I.N.A. during a focus sweep works as-is.

## Build & run

```
cd Tools/SpikeBatch
dotnet run -c Release -- "C:\path\to\sweep" --angle 43
```

## What it does

For Hocus Focus autofocus attempts containing sibling Region00 JSON results, use
`--saved-stars` to replay the detector's saved seed list and whole-field HFR. Pass
an `attempt01` folder, rather than mixing initial/final validation frames into the
sweep. JSON `$type` fields are ignored; the tool does not load detector assemblies.
Use `--out` and `--dump` under `bin/analysis/` to leave capture folders untouched.
For example:

```powershell
New-Item -ItemType Directory -Force bin/analysis | Out-Null
dotnet run -c Release -- "C:\StellaC\QHY600M\Autofocus\AutoFocus_20260919_220918\attempt01" --saved-stars --auto-angle --compare --roi-scale 3.5 --u-max 60 --out bin/analysis/sep19.csv --dump bin/analysis/sep19 --dump-stars 5
```

The CSV now includes every profile term. Repeated frames have unique image and
profile-column names. `--dump-stars` controls how many selected stars are inspected.
`failFrac` counts invalid evaluations even when they are absent from the curve;
`nearSNRx` compares near-focus range/repeat-noise with the HFR reference on paired
repeated positions. Missing repeats or a zero-median contrast is reported as `-`.
Exit status 3 means no variant produced a valid point.

`PlotComparison.ps1` uses Windows charting to export a standalone comparison PNG:

```powershell
./PlotComparison.ps1 -Original bin/analysis/original-sep10.csv,bin/analysis/original-sep19.csv -Improved bin/analysis/final-sep10.csv,bin/analysis/final-sep19.csv -Labels 2026-09-10,2026-09-19 -OutputPath bin/analysis/comparison.png
```

Run the shared-core regression checks with `dotnet run --project ../SpikeChecks -c Release`.
See the repository README section 15 for the September 2026 replay results and limits.

1. Detects bright stars on the seed frame and hands them to the same selector the
   plugin uses.
2. Tracks those stars through the whole sweep (centroid refinement, exactly as in
   the plugin) — no re-detection, so the metric is compared on identical stars.
3. Computes, per frame: `J`, `varC`, `varG`, `kurtosis`, used-star count, and a
   reference **HFR** measured over the same stars.
4. Writes a CSV and prints an ASCII curve plus a quality table.

## Options

| Option | Meaning |
| --- | --- |
| `--out <path>` | CSV output (default `<folder>/spike-batch.csv`) |
| `--limit <n>` | process only the first n frames |
| `--seed-frame <n>` | which frame seeds the star list (default 0) |
| `--reseed` | re-detect stars every frame instead of tracking |
| `--auto-angle` | measure the spike orientation per frame and use it instead of `--angle` |
| `--peak-sigma <v>` | star detection threshold, sigma above background (default 40) |
| `--angle <deg>` | spike angle |
| `--tau <px>` | core sigma |
| `--core-reject`, `--axis-sigma`, `--axis-reject` | window parameters |
| `--beta-var`, `--beta-split`, `--split-power` | metric weights (negatives allowed) |
| `--roi-scale`, `--bg-ring`, `--min-star`, `--max-stars` | ROI / selection |
| `--sweep <name>=<v1,v2,...>` | evaluate several values of one parameter in a single pass |

`--sweep` loads each frame once and evaluates every variant against it, each with
its own tracking state. Sweeping is therefore nearly free compared to re-running.

## Reading the output

The run prints the **actual** spike orientation measured from the first frame next
to the angle the metric used. Getting this wrong silently ruins the metric, so check
it first. The `ang` column in the curve table is the orientation measured on each
frame; it should be steady near focus and is expected to wander on heavily
defocused frames.

The quality table reports, for each series:

- `argmin` — position of the lowest measured point
- `vertex` — parabola minimum fitted over the lowest 60 % of the curve; this is
  what an autofocus run would actually return
- `range` — max − min across the sweep
- `repeatSd` — pooled scatter across positions that were visited more than once
- `SNR` — `range / repeatSd`

`repeatSd` only means something when the sweep really did revisit positions, and it
mixes genuine seeing drift with metric noise. Treat `vertex` agreement with the HFR
reference as the primary evidence and `SNR` as secondary.
