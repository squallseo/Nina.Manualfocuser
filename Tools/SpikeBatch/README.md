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

The seed report measures the **actual** spike orientation from the image and prints
it next to the configured angle. Getting this wrong silently ruins the metric, so
check it first.

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
