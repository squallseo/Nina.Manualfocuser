---
name: spike-metric
description: Develop, tune or validate the diffraction-spike focus metric. Use when touching Models/SpikeCore.cs or Tools/SpikeBatch, when asked to change the formula, tune parameters, compare metrics, or interpret a focus curve. Contains the datasets and their validity, the offline evaluation loop, and the measurement discipline that earlier work got wrong.
---

# Working on the spike focus metric

The goal: on a reflector with spider vanes, the diffraction spike goes from two lines,
to one, to progressively thinner as focus is approached. HFR flattens into a quadratic
minimum near focus and stops discriminating; a split separation should fall roughly
linearly to zero, giving a sharper vertex. **That claim is still unverified** — see
"What is open" below.

`Models/SpikeCore.cs` has no N.I.N.A. dependency and is compiled into both the plugin
and `Tools/SpikeBatch`. Keep it that way: it is what makes offline evaluation mean
anything.

## Rule zero: look at the pixels before believing a number

An earlier round of tuning produced a confident set of results — SNR comparisons,
angle-detection scores, parameter sweeps — measured on a 34 frame sweep that turned
out to have **no diffraction spikes at all**. Every one of those numbers described
blob and donut shape. The metric, the angle estimator and the quality harness all
returned plausible values throughout.

Before trusting any result on a new dataset:

```bash
cd Tools/SpikeBatch
dotnet run -c Release -- "<folder>" --dump out/ --roi-scale 3.5
```

Then actually read `out/deep_<pos>.png` (log stretch against the peak — a percentile
stretch of a mostly-background ROI cannot reach the spike). If you cannot see a spike,
no metric result from that folder is about spikes.

The angle estimator returns an angle on spikeless frames too, driven by whatever
asymmetry the blob has. The strength figure (peak over mean, `x1.97` style) is the
guard: below roughly 1.2 there is nothing there.

## Datasets

| Path | Instrument | Frames | Spikes | Use |
| --- | --- | --- | --- | --- |
| `C:\StellaC\QHY600M\Snapshot\2026-02-18\SNAPSHOT` | FDK200 / QHY600M | 4 | **yes**, clean 4-vane, angle 90° | valid, but all one side of focus, best is HFR 6.75 |
| `C:\StellaC\QHY600M\Snapshot\2026-03-21_dkkim` | e-130D / ATR2600M | 34 | **no** | position sweep only; useless for spike work |

The dkkim folder is a proper through-focus sweep (4215 → 3515 step 25, HFR minimum at
3690) and is still useful for exercising tracking, curve fitting and the harness. It
is not evidence about spike behaviour.

`FOCPOS` / `FOCUSPOS` in the FITS header supplies the x axis. The user drives local
testing through N.I.N.A.'s Simulator Camera pointed at the SNAPSHOT folder, so frames
there are replayed as if captured.

## The evaluation loop

```bash
cd Tools/SpikeBatch
dotnet run -c Release -- "<folder>" --auto-angle --compare --roi-scale 3.5 --u-max 60
```

- `--compare` scores every `SpikeMetricKind` against the same frames, stars and angle
  in one pass.
- `--sweep name=v1,v2,...` does the same for one parameter. Frames are loaded once and
  every variant carries its own tracking state, so sweeping is nearly free.
- `--dump <dir>` writes ROI crops and `profiles.csv`, and prints the profiles as ASCII.

Read the output in this order: the measured angle line, then the ROI dump, then the
`ang` and `pSNR` columns, then the metric. A metric column is meaningless if the rows
above it are wrong.

Stars are seeded once and tracked, mirroring the plugin. `--reseed` re-detects per
frame if you need to isolate a tracking problem.

## The formulation

Everything except `Legacy` derives from `p(u)`: flux projected onto the axis
perpendicular to the spike, with the same s-window and core suppression. All in pixels.

| Kind | Definition |
| --- | --- |
| `Legacy` | `betaVar*varC + betaSplit*(1/kurtosis)^p` — the original, still the default |
| `Sigma` | RMS width of p(u) — **measured non-monotonic, do not use** |
| `Hfw` | flux-weighted mean \|u − mean\|, the 1D analogue of HFR |
| `Fwhm` | FWHM of the envelope |
| `Split` | separation of the outer peaks, 0 while single |
| `Hybrid` | `Hfw + splitWeight * Separation` |

Two things the profile needs to be correct at all, both previously wrong:

- **One pixel bins.** With a spike near 0° or 90°, u lands on integers; any bin width
  that is not a divisor of one pixel leaves every other bin empty.
- **Baseline subtracted from the wings.** Clipping negative background residuals to
  zero leaves a positive pedestal that dominates any moment over a wide u range.

Other traps, all confirmed:

- `varC` is a gaussian-weighted second moment of width τ, so it converges to exactly
  τ². With the documented τ of 1.0–2.0 it is pinned near constant and carries almost
  no information. This is analytic, not dataset-dependent.
- Keep the ROI comfortably larger than `axisSigmaPx`, or the axis window is truncated
  by the ROI edge and stops doing anything.
- Spike angle is only defined modulo 180°, and a four-vane spider gives two axes 90°
  apart of similar strength. Continuity against the running estimate is what stops the
  reported angle hopping between them.
- Invalid frames must carry `NaN`, never a negative sentinel. A sentinel plotted on the
  axis destroys the scale and hides the curve.

## Acceptance tests

`SpikeBatch` scores the four questions rather than leaving them to the eye:

| Name | Question | Want |
| --- | --- | --- |
| `rho(HFR)` | does it move with HFR at all (Spearman) | ≈ +1 |
| `nearGain` | does it still move where HFR has gone flat | > 1 |
| `splitOn` | fraction of frames where a split was detected | — |
| `failFrac` | fraction failing outright rather than guessing | — |

`nearGain` is the one that matters. It is the whole justification for the project.

## What is settled

- The spike structure is real on FDK200: profile goes single → wide → bimodal, with
  separation 0, 0, 12, 22 px and dip depth 0, 0, 0.28, 0.51 across the four frames.
- Angle detection works on real spike data: 89.9° at strength ×1.97 against a
  configured 90°.
- `Sigma` is non-monotonic and can be dropped. `Fwhm` is perfectly rank-correlated
  with HFR and has by far the widest dynamic range.

## What is open

The 2026-10-04 replay is documented in README section 15. The two FDK200/QHY600M
attempts under `C:\StellaC\QHY600M\Autofocus` contain 36 and 33 frames, at 2500-step
positions with three exposures each. Selected-star crops show no clearly visible
spikes. Use `--saved-stars` to load sibling Hocus Focus JSON seeds and whole-field
HFR; this is a different HFR reference from the offline tracked-star estimate.
Keep initial/final validation frames separate. Write outputs under `bin/analysis/`.

FWHM now uses interpolated crossings and rejects truncated/low-SNR profiles; split
requires a 10% valley and 3 noise units. Signed ROI residuals are preserved. Legacy
remains the default, with explicit candidate selection available in plugin options.
The original September 10 selector used four stars; the correction uses five, so
report that change when attributing the 47% end-to-end repeat-SD reduction. Holding
four stars fixed yields only about a 3% reduction. September 19 uses five in both
versions and reduces FWHM repeat SD about 24%. Near-focus noise-normalized gains
remain below HFR (0.35 and 0.12), so this is not evidence of a spike advantage.
Automatic angles can still look credible on these spikeless selected-star crops.

`dotnet run --project Tools/SpikeChecks -c Release` exercises the shared core's
analytic widths, split rejection, signed residuals, seed eligibility and stale angle
reporting. `nearSNRx` uses repeated-exposure noise; `failFrac` now includes dropped
evaluations. A zero-median Split contrast is undefined, not zero. ROI exports use
unique frame identifiers and one-pixel profiles; `--dump-stars` inspects more than
the brightest selected star. Inspect crops before drawing optical conclusions.

**A validated near-focus diffraction-spike test has never been run.** The original
February/March datasets do not cover the region where
the two lines merge. Until a through-focus sweep from a spiked instrument exists,
`nearGain` is blank and the project's central claim is unverified.

Required sweep: both sides of focus, best focus near the middle, out to roughly twice
the minimum HFR at each end, closely spaced near focus, two or three positions
revisited for repeatability, exposure long enough that spikes show in the ROI dump.

## Measurement discipline

These were learned by getting them wrong:

- **Do not change a default on one dataset.** Frame-to-frame angle smoothing looked
  physically obvious — the spider cannot rotate mid-run — and measured worse than the
  instantaneous value (SNR 4.8 against 10.3). It was removed rather than left in as a
  tunable. Prefer the simpler thing when the data does not favour the complex one.
- **State which dataset a number came from**, in commits and in the README. The
  retraction above happened because that was not done.
- `repeatSd` in the quality table mixes genuine seeing drift with metric noise, and is
  only meaningful when the sweep actually revisited positions. Treat vertex agreement
  with the HFR reference as primary evidence, SNR as secondary.
- README sections 1–10 are the original design intent; 11–14 are measurement. When the
  two disagree, record the disagreement in 11–14 rather than editing the design away.
