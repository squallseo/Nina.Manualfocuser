## Interactive Manual Focuser for N.I.N.A.

In N.I.N.A., the default focuser controls in the Imaging tab use the relative step size defined in the Autofocus settings.
This means that when you want to change the focuser movement amount, you have to leave the Imaging tab and go into the Autofocus configuration.

When fine-tuning focus manually — especially when making small, incremental adjustments while checking star shapes — this workflow is inconvenient and slows down the process.

This plugin was created to solve that problem.

Manual Focuser allows you to:
 - Enter focuser increment step values directly in the Imaging tab
 - Move the focuser immediately using those values
 - Automatically captures images after each focus move, computes the average HFR, and plots it on a graph
 - Fine-adjust focus while visually inspecting stars and HFR changes without switching tabs or changing Autofocus settings

The goal is to make manual focus adjustment faster, simpler, and more intuitive during imaging sessions.

# Spike-Based Focus Metric (Variance + Kurtosis)

## Overview

This document describes a spike-based focus metric designed for
reflector telescopes with diffraction spikes (spider vanes).

The metric is designed to:

-   Keep values high while diffraction spikes are still split (bimodal)
-   Drop sharply when the spike merges into a single line
-   Remain continuous and differentiable
-   Be suitable for parabolic curve fitting and autofocus optimization

This version removes auxiliary structural penalties (lambda term) to
keep the formulation minimal and stable.

**Status.** Sections 1 – 10 are the original design. Sections 11 – 14 record what
measurement has since shown, including where the design intent and the data
disagree, and describe the profile based formulation that replaced the pixel
moment one. Read section 11.1 first: it says which of the two development
datasets a given number came from, and one of them turned out to have no
diffraction spikes at all.

The central claim — that a spike based metric stays sensitive near focus where
HFR flattens out — is **still unverified**. See section 13.1 for the sweep needed
to settle it.

------------------------------------------------------------------------

# 1. Coordinate System

Given user-provided spike angle θ:

Spike-axis direction:

    s = x cosθ + y sinθ

Perpendicular direction (analysis axis):

    u = -x sinθ + y cosθ

All distribution analysis is performed along the u-axis.

------------------------------------------------------------------------

# 2. Weighting Model

Each pixel intensity I(x,y) contributes with:

    w = I · w_core(r) · w_axis(s)

## Core Suppression

Reduces central saturation influence:

    w_core(r) = 1 - exp( -r² / (2 r0²) )

## Axis Windowing

Focuses on spike region while rejecting center:

    w_axis(s) =
        exp( -s² / (2 σs²) ) ·
        (1 - exp( -s² / (2 σrej²) ))

------------------------------------------------------------------------

# 3. Moment Computation

Total weight:

    W = Σ w_i

Weighted mean:

    μ = (1/W) Σ w_i u_i

Global variance (pixel²):

    σ_g² = (1/W) Σ w_i (u_i - μ)²

Local variance (core-focused, pixel²):

    σ_c² =
        [ Σ w_i exp( -(u_i-μ)² / (2 τ²) ) (u_i-μ)² ]
        /
        [ Σ w_i exp( -(u_i-μ)² / (2 τ²) ) ]

Fourth central moment:

    m4 = (1/W) Σ w_i (u_i - μ)⁴

------------------------------------------------------------------------

# 4. Kurtosis

    κ = m4 / (σ_g²)²

Behavior:

  State             κ value
  ----------------- ---------
  Single spike      Large
  Split (bimodal)   Small

------------------------------------------------------------------------

# 5. Split Penalty (Enhanced)

Base form:

    P_split = 1 / (κ + ε)

Enhanced for sharper drop:

    P_split = ( 1 / (κ + ε) )^p

Recommended:

    p = 2

------------------------------------------------------------------------

# 6. Final Focus Metric (Simplified)

Variance-based version --- no sqrt conversion and no lambda term.

    J =
        β_var · σ_c²
        + β_split · P_split

This minimal formulation improves stability and emphasizes the
split-to-single transition.

------------------------------------------------------------------------

# 7. Units

  Term      Unit
  --------- ------------------------
  σ_c²      pixel²
  σ_g²      pixel²
  κ         dimensionless
  P_split   dimensionless
  J         pixel² + dimensionless

Note: Square-root (pixel conversion) is intentionally NOT applied, as
variance form provides stronger curvature for autofocus fitting.

------------------------------------------------------------------------

# 8. Recommended Initial Parameters

    betaVar = 1.0
    betaSplit = 3.0 ~ 6.0
    splitPower (p) = 2.0
    kurtosisEps = 1e-6

Window parameters:

    coreRejectSigmaPx = 4 ~ 8
    axisRejectSigmaPx = 6 ~ 12
    axisSigmaPx = 20 ~ 40
    coreSigmaPx (τ) = 1.0 ~ 2.0

Note on τ: see section 11.3. Values in this range pin σ_c² at τ², which is
almost certainly not what was intended.

Profile parameters (section 12):

    metricKind = Legacy
    uMaxPx = 40                  must be wide enough to contain the split
    profileBaselineFraction = 0.15
    peakThresholdFraction = 0.35
    splitWeight = 1.0

Keep the ROI comfortably larger than axisSigmaPx, or the axis window is
truncated by the ROI edge and stops doing anything.

------------------------------------------------------------------------

# 9. Expected Behavior (design intent)

  Condition        σ²           κ        P_split   J
  ---------------- ------------ -------- --------- ------------
  Heavy defocus    High         Medium   Medium    High
  Split persists   Decreasing   Low      High      Still High
  Spike merges     Low          High     Low       Sharp Drop

------------------------------------------------------------------------

# 10. Key Properties

-   Fully continuous and differentiable
-   Split-sensitive
-   Suitable for parabolic curve fitting
-   Independent from HFR
-   Minimal parameter design for stability

------------------------------------------------------------------------

# 11. Measured Behaviour

`Tools/SpikeBatch` replays recorded FITS frames through the same metric code the
plugin runs, and can dump the star ROI as a log stretched PNG plus the flux
profile across the spike.

## 11.1 Know which dataset a number came from

Two focus datasets were used while developing this, and only one of them is
valid for a spike based metric.

  Dataset                                  Spikes visible   Usable
  ---------------------------------------- ---------------- --------
  e-130D / ATR2600M, 34 frame sweep        no               no
  FDK200 / QHY600M, 4 frames               yes              partly

The 34 frame sweep has no diffraction spikes at any stretch. At best focus the
star is a bare dot; at full defocus it is a plain donut from the central
obstruction. Anything measured on it describes blob and donut shape, not spike
behaviour. An earlier revision of this document quoted focus-estimate and
angle-detection results from that sweep; they have been removed rather than
corrected, because the quantity they measured was never the intended one.

Checking the ROI dump before trusting a result is not optional:

    dotnet run -c Release -- "<folder>" --dump out/ --roi-scale 3.5

## 11.2 What the spike actually does (FDK200 / QHY600M)

This telescope shows a clean four vane pattern. Across increasing defocus the
profile perpendicular to the spike goes from a single narrow peak, to a wide
one, to clearly bimodal — the two-lines-merging-into-one behaviour this metric
was started from.

  FOCPOS    HFR     sigma   hfw    fwhm   separation   dip depth
  --------- ------- ------- ------ ------ ------------ ----------
  99222     6.75    8.44    5.61   9      0            0.00
  100000    10.11   8.43    5.59   10     0            0.00
  110000    13.83   7.98    6.55   21     12           0.28
  120000    17.18   12.45   10.92  36     22           0.51

The orientation estimator reports 89.9° at strength ×1.97 against a configured
90°, which is the first confirmation on real spike data that angle detection
works.

Caveat: all four frames sit on one side of focus and the closest is still well
defocused at HFR 6.75. Nothing here says anything about behaviour near focus.

## 11.3 σ_c² saturates at τ²

This one is analytic rather than measured. `σ_c²` is a gaussian-weighted second
moment of width τ; when the underlying u-distribution is wider than τ the ratio
converges to exactly τ². With the default `coreSigmaPx = 1.5` the term is
therefore pinned near 2.25 whenever the star is at all spread out, and cannot
behave as "high on heavy defocus" the way section 9 assumes.

------------------------------------------------------------------------

# 12. Profile Based Formulation

The original formulation accumulates weighted moments over ROI pixels. The
current one builds `p(u)` — flux projected onto the axis perpendicular to the
spike, using the same s-window and core suppression — and derives everything
from it. Every quantity is then in pixels, directly interpretable, and all
candidates can be scored from one pass over the data.

  Kind      Definition
  --------- ---------------------------------------------------------
  Legacy    β_var·σ_c² + β_split·(1/κ)^p, unchanged
  Sigma     RMS width of p(u)
  Hfw       flux weighted mean |u − mean|, the 1D analogue of HFR
  Fwhm      full width at half maximum of the envelope
  Split     separation of the outer peaks, zero while single
  Hybrid    Hfw + splitWeight · Separation

Two details the profile needs to be correct at all:

- Bins must be one pixel. With a spike near 0° or 90°, u lands on integers, so
  any bin width that is not a divisor of one pixel leaves every other bin empty.
- The baseline must be estimated from the profile wings and subtracted. Clipping
  negative background residuals to zero leaves a positive pedestal that dominates
  any moment taken over a wide u range.

Legacy remains the default. Nothing is switched over on the evidence available.

------------------------------------------------------------------------

# 13. Acceptance Tests

`--compare` scores every candidate against the same frames, stars and angle, and
`SpikeBatch` reports four numbers rather than leaving the judgement to the eye:

  Name       Question
  ---------- ---------------------------------------------------------
  rho(HFR)   does it move with HFR at all (Spearman, want ≈ +1)
  nearGain   does it still move where HFR has gone flat (want > 1)
  splitOn    fraction of frames where a split was detected
  failFrac   fraction that fail outright rather than guessing

Measured on the four FDK200 frames:

  metric    rho(HFR)   note
  --------- ---------- --------------------------------------------
  fwhm      1.000      9 → 10 → 21 → 36 px, widest dynamic range
  legacy    1.000      2.02 → 2.59, correct direction, tiny range
  split     0.949      0 until the spike splits, then tracks it
  hfw       0.800
  hybrid    0.800
  sigma     0.200      non-monotonic, drop it

## 13.1 Open question

`nearGain` cannot be computed from any dataset here, because none of them cover
the region near focus. That test is the entire justification for the project —
HFR flattens into a quadratic minimum near focus while a split separation should
fall roughly linearly to zero, giving a sharper vertex — and it remains
unverified.

Settling it needs a through-focus sweep from an instrument that shows spikes:

- both sides of focus, best focus near the middle
- out to roughly twice the minimum HFR at each end
- closely spaced near focus, which is the region under test
- two or three positions revisited, for a repeatability figure
- exposure long enough that the spikes are visible in the ROI dump

Then:

    dotnet run -c Release -- "<folder>" --auto-angle --compare --roi-scale 3.5 --u-max 60

------------------------------------------------------------------------

# 14. Automatic Spike Angle

The orientation is measured on every frame by integrating background subtracted
flux along rays through each tracked star, in both directions, over an annulus
that excludes the core. The direction collecting the most flux is the spike axis.
It is measured whether or not auto mode is enabled, and shown on the Manual
Focuser panel, so a typed value can be checked against reality.

Two properties of the problem shape the implementation:

- The result is only defined modulo 180°, and a four vane spider produces two
  axes 90° apart with similar strength. Without a tie-break the reported angle
  hops between them frame to frame. Candidate peaks within 35° of the running
  estimate win if they reach 75 % of the strongest peak.
- The median is taken across stars within a frame, not across frames. Averaging
  over time was implemented and measured worse: a sweep spans states where the
  orientation is crisp and states where it is not, so a time window mixes good
  estimates with bad ones rather than averaging repeats of one measurement.

The reported strength — peak over mean of the directional profile — is the guard.
Below roughly 1.2 there is no usable spike and the measurement is discarded. On a
dataset with no spikes at all the estimator still returns an angle, driven by
whatever asymmetry the blob happens to have, which is why the strength figure and
the ROI dump both matter before trusting it.

------------------------------------------------------------------------

End of Document


## Screenshots
### Tool icon
![Manual Focuser Icon](Images/icon.png)

### Overall View
![Manual Focuser – Overall](Images/screenshot.png)

### Main Dock
![Manual Focuser – Main Dock](Images/screenshot_alt.png)

![Manual Focuser](Images/logo.png)
