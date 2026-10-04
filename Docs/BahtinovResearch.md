# Bahtinov autofocus: evidence and implementation gate

Research date: 2026-10-04. The initial investigation was followed by an explicitly authorized experimental implementation; see implementation and validation below.

## Recommendation

Evaluate BahtiFocus's signed pixel-error measurement against real masked images first. Its public code provides an inspectable measurement algorithm in a NINA plugin, but its documented workflow is continuous analysis with the user adjusting focus. It is not evidence of a validated autofocus controller. Motor direction, steps per pixel, backlash, convergence and stopping criteria still require separate evidence and equipment calibration.

Do not treat ordinary telescope spider spikes, or the existing spike-width metric, as a Bahtinov mask measurement. Our saved autofocus datasets and the February spider-spike examples do not establish a masked, through-focus validation dataset.

## Sources and what they establish

| Source | Evidence | Scope limit |
| --- | --- | --- |
| Lendermann et al., *Computational Imaging Prediction of Starburst-Effect Diffraction Spikes*, Scientific Reports 8, 16919 (2018), [DOI/full text](https://www.nature.com/articles/s41598-018-34400-z) | Fourier-optics model including focus, aperture geometry and sensor effects, with experimental comparisons. Useful foundation for physically meaningful simulated diffraction images. | Not a demonstrated Bahtinov focuser controller or a source of controller gains. |
| *Demonstration of an imaging technique for the measurement of PSF elongation caused by Atmospheric Dispersion*, MNRAS 512(4), 5812 (2022), [full text](https://academic.oup.com/mnras/article/512/4/5812/6554552) | Uses a classical three-zone Bahtinov-style pupil; fits speckle contours with ellipses and estimates a common line intersection by least squares. | Measures atmospheric dispersion, not autofocus. This alternative estimator would require its own focus validation. |
| [BahtiFocus](https://github.com/CanardConfit/BahtiFocus) | NINA-integrated camera acquisition, ROI analysis and signed focus-error display. Source was inspected, not just its README. | README instructs the user to adjust focus. Numerical quality and failure handling must be tested before reuse. |
| [APT Bahtinov Aid manual](https://astrophotography.app/usersguide/bahtinov_aid.htm) | Documents subpixel analysis licensed from Noordhoek, signed readings, turbulence/stacking and false detection from excessively bright patterns. | Public documentation does not grant permission to copy the licensed algorithm or demonstrate our proposed motor controller. |
| [FocusDream manufacturer's manual, 2015](https://astromagazin.net/ckfinder/userfiles/files/FocusDream_users_manual.pdf) | Documents ASCOM use with Bahtinov Grabber and backlash configuration. Historical evidence that motorized integration existed. | Not an inspectable control algorithm or performance study. Original Grabber source and redistribution permission were not established here. |

The searched literature supports the optical and image-measurement basis. This investigation did not establish a peer-reviewed, directly reusable closed-loop Bahtinov autofocus algorithm. Do not describe an engineering proposal as a published algorithm.

## Inspected code, pinned for reproducibility

BahtiFocus commit `521f63df9a5cc31e1889566ed5f093fc38a36ff3`:

- [CalculateLines](https://github.com/CanardConfit/BahtiFocus/blob/521f63df9a5cc31e1889566ed5f093fc38a36ff3/Bahtinov/Bahtinov.cs): searches 180 orientations across pi radians, rotates/interpolates intensity samples and finds projection peaks; suppresses neighboring angle maxima when selecting three lines. Subsequent frames reuse the detected angles.
- [PeakPosition](https://github.com/CanardConfit/BahtiFocus/blob/521f63df9a5cc31e1889566ed5f093fc38a36ff3/Bahtinov/LSQCalculator.cs): quadratic least-squares refinement around a peak. CalculateLines passes a half-range of two samples on the subsequent-frame path.
- CalculateLines intersects the two outer lines, projects that intersection onto the middle line, and uses the cross-product sign to return signed perpendicular distance as `FocusError`.
- [Acquisition instruction](https://github.com/CanardConfit/BahtiFocus/blob/521f63df9a5cc31e1889566ed5f093fc38a36ff3/Instructions/BahtiAnalyser.cs) invokes that analyzer. No autofocus motor loop was established from the inspected workflow.
- [Repository license](https://github.com/CanardConfit/BahtiFocus/blob/521f63df9a5cc31e1889566ed5f093fc38a36ff3/LICENSE) is MPL-2.0. Before copying any files, audit their individual provenance and bundled numerical dependencies; a root license alone does not settle every dependency's license. No third-party source has been incorporated into the plugin.

Review concerns inferred from the inspected implementation: slope/intercept geometry has singular orientations and nearly parallel-line denominators; quadratic refinement needs finite/curvature/range checks; PeakPosition catches errors and returns zero; cached angles can become stale. Its micron conversion and critical-focus constants need derivation and unit verification before adoption. A signed image-space error does not by itself identify the focuser's inward/outward direction.

[Bahtinov-Collimator](https://github.com/insertnamehere1/Bahtinov-Collimator/tree/07c10df1e0737763c85b65492d451e3d474ac01f) was also located. Repository metadata identifies GPL-3.0. Its processing and calibration files merit a later code review, but no algorithm or controller claim is made from file names alone, and no code has been copied.

## Required validation before motor control

These are proposed project acceptance checks, not claims from a paper:

1. Capture actual masked FITS images of one isolated star through focus on both sides, with recorded focuser positions, repeated frames per position and unchanged optical/filter/binning configuration. Include weak, saturated, clipped and unmasked negative examples.
2. Compare measured lines and signed pixel error against independently marked image geometry and BahtiFocus results. Check rotation, reflection, ROI translation, near-vertical lines and angle wraparound. Invalid detection must be explicit; zero must remain a valid focus measurement, not a failure sentinel.
3. Establish repeatability and the local relationship between signed error and focuser position. Calibrate direction and response from measured moves; do not assume that image sign maps to a fixed motor direction.
4. Document the control method's source or identify it explicitly as an engineering design. Validate backlash handling, settling time, bounded travel, cancellation and loss-of-pattern stopping in simulation before real hardware.
5. Verify final focus with repeated masked measurements, then remove the mask and independently check HFR on ordinary images. Current HFR-only fallback remains appropriate when the diffraction pattern is not trustworthy.

Real masked data remains the next validation milestone. The implemented motor mode is labeled experimental and requires explicit mask confirmation; synthetic validation does not establish real-camera performance.

## Implementation follow-up: documented controller workflow

The user authorized implementation with synthetic or publicly available examples on 2026-10-04. Synthetic checks establish algorithm behavior against a known model; they do not establish real-camera accuracy.

[SharpCap 4.0 official focusing manual](https://docs.sharpcap.co.uk/4.0/17_Focusing.htm), sections *Automatic Focus Scanning*, *Returning to Best Focus* and *Automatic focusing with a Bahtinov Mask*, documents this workflow:

- Start on one side of focus; move through configured steps, bounded by step count and focuser limits.
- Collect multiple measurements per position and skip at least one frame after movement for settling.
- Bahtinov best focus is the zero of the signed score, rather than a peak or valley.
- Return to the estimated best position using the same approach direction as the measurements; a preliminary move past the target allows that approach despite backlash.
- Alternatively, repeat a scan and stop when the desired score is reached.

The manual supports this scan/zero/consistent-approach design. It does not disclose a Bahtinov numerical regression, motor calibration gain, tolerance, rejection threshold or travel guard. Any interpolation and safeguards implemented here must be described as project engineering choices, not copied SharpCap algorithms. A failed pattern detection cannot count as a zero score. After any final move, acquire fresh valid measurements to verify the result. Motorized operation remains opt-in and requires an installed mask and valid measurements.

## Fast manual-focus acquisition

NINA upstream source was inspected at commit `ee69f27c3a6e76eaf4ed2c3518360159bc4ae97a`:

- [IImagingMediator](https://github.com/isbeorn/nina/blob/ee69f27c3a6e76eaf4ed2c3518360159bc4ae97a/NINA.Equipment/Interfaces/Mediator/IImagingMediator.cs) exposes ordinary capture/preparation and StartLiveView.
- [ICameraMediator](https://github.com/isbeorn/nina/blob/ee69f27c3a6e76eaf4ed2c3518360159bc4ae97a/NINA.Equipment/Interfaces/Mediator/ICameraMediator.cs) exposes an asynchronous LiveView exposure stream and ordinary Download.
- [CameraVM](https://github.com/isbeorn/nina/blob/ee69f27c3a6e76eaf4ed2c3518360159bc4ae97a/NINA.WPF.Base/ViewModel/Equipment/Camera/CameraVM.cs) reports CanShowLiveView/CanSubSample and applies a requested hardware subframe to capture/live view when supported.
- [ImagingVM](https://github.com/isbeorn/nina/blob/ee69f27c3a6e76eaf4ed2c3518360159bc4ae97a/NINA/ViewModel/ImagingVM.cs) ordinary capture waits for exposure and download; preparing an image adds processing. The source snapshot is newer than our installed 3.2 package; compilation against 3.2 is required before relying on a particular API.

The proposed manual-focus mode uses independent 0.1–0.5-second preview exposures, continuous cancellation-aware acquisition, one in-flight frame and immediate preview/metric updates. Hardware subframing can reduce readout if supported; cropping only after download cannot. A driver's live-view capability is optional and must not be assumed for QHY600M or the simulator. Display observed frame cadence rather than claiming exposure duration equals update interval. Short exposures may have insufficient signal for HFR or Bahtinov measurement, in which case show the preview with an explicit unavailable metric. Camera reservation, cancellation and stale-result suppression are still required. Exposure/gain/binning or ROI changes invalidate an autofocus scan's comparison data.

## Implemented prototype and validation

The implementation uses sequential SNAPSHOT acquisition compiled against NINA
3.2.0.9001, not an assumed camera-specific video stream. It uses 256-pixel ROI,
1×1 binning, raw pixels for measurement and an independently stretched local
preview. Hardware ROI support and actual delivered cadence appear in the status.
Manual preview allows 100–5000 ms exposures; default is 250 ms. Camera minimum and
maximum exposure are checked before requesting a frame.

`BahtinovAnalyzer` independently implements projection-based line measurement,
normal-form intersection and signed perpendicular error. It searches 360
orientations at 0.5-degree spacing and uses bilateral flux, angular maxima,
contrast and triplet geometry to reject unsuitable images. Project thresholds
are not literature-derived performance guarantees. Perfectly shaped unmasked
spider patterns can still be ambiguous; mask confirmation is required for motors.

`BahtinovFocusRunner` implements the documented scan/zero/same-direction approach.
Bracket-only linear interpolation, three-frame median, 1-pixel spread rejection,
3-degree orientation guard and 0.5-pixel final tolerance are explicit engineering
choices. It validates the original image before movement and bounds the scan to
the configured session range. Driver/mediator movement failures abort; physical
focuser limits remain enforced by the connected driver's movement interface.
Preview exposure and ROI are frozen during the scan. No micron conversion or
claim of critical-focus-zone accuracy is made.

The generator uses scalar monochromatic Fourier-pupil propagation with quadratic
defocus phase, not fabricated line drawings alone. Five pupil-edge phases
−2, −1, 0, +1, +2 rad give pixel errors approximately
+2.647, +1.259, −0.059, −1.345, −2.586. These phases are not calibrated focuser
steps. The optical model omits atmosphere, spectrum, pixel integration and real
instrument aberrations. A separate geometric suite checks known line offsets.

57 analyzer/metric/optical/FITS/workflow checks pass, including stopping on invalid
preflight, absent bracket, failed final verification, position mismatch and
cancellation. 19 mocked NINA acquisition checks cover ROI/fallback, exposure,
invalid images and avoiding blind native-driver retries. Existing 31 spike/fit
checks remain passing. A 256-pixel mask analysis takes about 95 ms on this
development machine; this is CPU timing, not measured camera frame cadence.
Real masked data, physical backlash and QHY600M speed remain unvalidated.
