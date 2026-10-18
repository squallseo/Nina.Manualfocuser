# MF display levels

The toolbar has an Auto Stretch toggle and one **Levels** button. The previous inline midtone slider, numeric readout and reset button are consolidated into this popup to preserve image space. The popup contains automatic/display histograms, a logarithmic-height toggle, one track with three handles, clipping percentages and **Auto** reset. The former three separate slider rows are removed.

- **Black:** move right to clip shadows. Its normalized position starts at 0.
- **Midtone:** move left to brighten faint stars, right to darken. The neutral value is 0.5, expressed relative to the black/white interval; moving an endpoint keeps this relative value.
- **White:** move left to clip highlights. Its normalized position starts at 1.

The handles operate on the NINA-style automatic STF result, rather than the nearly-black raw astronomical input. This spreads useful image levels across the track and keeps black/midtone handles distinguishable. Endpoint handles cannot cross. Arrow keys move by 0.01 on the common axis, Shift+arrow by 0.001. The extra transfer is composed into the same 16-bit LUT, without a second image-size pixel pass or intermediate image buffer. With neutral 0 / 0.5 / 1 handles, the extra transfer is bypassed and automatic output bytes remain unchanged.

Auto restores the active NINA profile's black clipping and stretch factor, resets all handles to 0 / 0.5 / 1, and enables Auto Stretch. With Auto Stretch off, the histogram remains accessible but the track is disabled.

These settings affect display only. Source pixels, HFR measurements, star tracking and Sync to Image retain the original data. Auto Stretch off bypasses the levels and uses the source bit depth for linear display. Rotation, ROI selection and the inspector use the same display settings.

This is a focusing display inspired by the distinction between PixInsight's display-only [ScreenTransferFunction](https://github.com/GeorgViehoever/PCL/blob/master/src/modules/processes/IntensityTransformations/ScreenTransferFunctionInstance.cpp) and pixel-changing [HistogramTransformation](https://github.com/GeorgViehoever/PCL/blob/master/src/modules/processes/IntensityTransformations/HistogramTransformationInstance.cpp). The shared bar uses normalized endpoints and a midtone transfer over the automatic STF result. It is not a pixel-editing tool. No PixInsight source code is reused.

## Implementation

The automatic estimate uses median and 1.4826 × MAD, following [NINA's STF implementation](https://github.com/isbeorn/nina/blob/2393eae581145ed5b8114bf07c48ca2580540fd5/NINA.Image/ImageAnalysis/ImageUtility.cs). The default target background is the active NINA stretch factor (normally 0.2), replacing the previous fixed 0.12 background and sampled 99.99th-percentile white endpoint. Explicit black/white normalization allows independent highlight control; output is therefore similar to NINA rather than bit-identical.

Sampling was evaluated and rejected for the final implementation: when MAD is only 3–4 ADU, a one-ADU sample error noticeably changes contrast. Instead, an exact 65536-bin histogram is built over the captured ROI. Large images use at most four workers with separate pooled histograms, followed by a merge. No mean, variance, min/max counts, OxyPlot histogram or host statistics panel is computed.

An immutable snapshot of occupied value/count pairs and median/MAD is cached against the read-only frame with weak keys. Pooled worker arrays are never retained. The real captures below used 1.5-62.7 KB of count data per frame (worst case for all 65536 values: 384 KiB). Pixel mapping uses one 65536-entry LUT, with parallel row blocks for images of at least 4 MP. Each output byte is written before publication; large output arrays skip redundant zero initialization. The existing 80 ms slider debounce, cancellation and newer-frame guards also apply to shadows and highlights. Inspector tiles share one common transfer.

## Histogram

The popup reuses these exact counts, projects them into 256 automatic and display bins, and draws two frozen vector paths. The gray Auto curve shows the input to the shared handles; the blue Display curve shows their output. Display counts use the same mapping as the displayed pixels, including their attached levels and bit depth. Both curves align with the shared 0-to-1 track; logarithmic height reveals low-count star pixels. Clipping percentages count pixels at or beyond the actual transfer endpoints. Inspector histograms include the nine science tiles and exclude gutters.

Updates are capped at about 3 Hz, skip an unchanged image and stop when the popup closes. Closing also releases its last image reference and plot. No additional image scan is needed during Auto Stretch. With Auto Stretch off, the histogram is computed lazily for the displayed frame while the popup is open; closed linear preview continues to skip statistics.

## Auto Stretch measurements (2026-10-18)

Four user-provided QHY600M mono 1-second FITS captures were compared at 512, 1024, 2048 and full 9576 × 6388 resolution. Baseline: published 1.2.0.1 DLL, SHA256 `48DB5C40C4804DE5300A6B40D61114EADB20F8E2BD4F4569CF6C9028E1AD51A1`. Reference: installed NINA 3.2.0.9001, target 0.2, black clipping -2.8. Equipment was not operated.

The table reports the average of each file's p50 CPU processing time in milliseconds, measured before adding the histogram popup. Small sizes used 12 measured calls per stage; full sensor used four. Warm-up preceded each stage. Capture, FITS decoding, HFR, debayering and screen paint are excluded. Native measurements include fresh image statistics; cached-level measurements use an already measured frame. Managed allocation figures in the CSV exclude native bitmap memory.

| Image | Previous | New frame | New cached levels | NINA, fresh statistics |
|---|---:|---:|---:|---:|
| 512 × 512 | 3.51 | 0.92 | 0.60 | 5.02 |
| 1024 × 1024 | 4.01 | 2.48 | 1.78 | 8.43 |
| 2048 × 2048 | 6.77 | 3.34 | 1.79 | 24.36 |
| 9576 × 6388 | 55.33 | 46.58 | 28.27 | 314.84 |

Across all 16 image/size combinations, the mean absolute difference from native 8-bit display was 0.014–0.080 / 255; the 99th-percentile difference was one display level. The background medians agreed (usually 51/255, versus the previous 31/255). These measurements establish agreement for this mono dataset, not all sensors, exposures or color processing.

`Tools/LiveWorkflowChecks/StretchComparison.cs` loads the previous DLL in a separate assembly context and compares it with the current build and the real native stretch. Put the baseline DLL at `bin/stretch-baseline/Cwseo.NINA.ManualFocuser.dll`, build the checks project with `DeployPlugin=false`, then run `FocusCenteringChecks.exe --stretch-files <FITS-folder>` from the repository root. Results and identical-pixel-scale contact sheets (previous | new | NINA) are saved under `bin/stretch-comparison`.

## Histogram overhead measurements (2026-10-18)

The same four captures and 16 image/size combinations were compared against the development DLL immediately before this popup change, SHA256 `091946F52E65991AEC2580B7D7E0940FD9A40AA004D9E1726122ADF9EC9D1DB6`. All rendered bytes remained identical. This isolates histogram overhead from the earlier Auto Stretch algorithm improvement.

Average of each file's p50 CPU milliseconds, after a 150 ms warm-up per stage; 32 measured calls for small images, five for full sensor:

| Image | Before histogram | With histogram data | Project 256 bins | Build two paths |
|---|---:|---:|---:|---:|
| 512 x 512 | 0.766 | 0.774 | 0.006 | 0.032 |
| 1024 x 1024 | 2.248 | 2.041 | 0.016 | 0.041 |
| 2048 x 2048 | 3.026 | 2.891 | 0.046 | 0.023 |
| 9576 x 6388 | 36.142 | 34.781 | 0.102 | 0.024 |

No meaningful frame-processing slowdown was observed. Lower timings in some rows are run variation, not evidence that storing a histogram speeds rendering up. Projection and path construction measure CPU work only; GPU composition, image display, capture, decoding and HFR are excluded. Plot arrays allocate about 4.2 KB and the two paths about 17.8 KB per update.

To reproduce, preserve the pre-popup DLL under `bin/histogram-baseline/Cwseo.NINA.ManualFocuser.dll`, build with `DeployPlugin=false`, then run `FocusCenteringChecks.exe --histogram-files <FITS-folder>`. Results are saved in `bin/histogram-performance.csv`.

## Shared-track follow-up measurements (2026-10-18)

After replacing the three slider rows with one shared track, the same four FITS files and 16 image/size combinations were measured again. Neutral handles still matched the pre-popup baseline byte for byte. Manual levels used black 0.02, midtone 0.3 and white 0.9; these measurements include fresh-frame statistics and LUT mapping, not GPU display or equipment. Average of each file's p50 CPU milliseconds:

| Image | Neutral handles | Manual handles | Histogram projection + paths |
|---|---:|---:|---:|
| 512 x 512 | 0.943 | 1.010 | 0.037 |
| 1024 x 1024 | 2.692 | 2.698 | 0.053 |
| 2048 x 2048 | 3.438 | 3.644 | 0.063 |
| 9576 x 6388 | 41.056 | 39.333 | 0.104 |

The lower full-frame manual timing is run variation, not a speedup claim. The extra transfer operates on the fixed-size LUT, so its cost does not add a second full-frame scan. Results for this run are preserved in `bin/levels-track-performance.csv`; the original popup run is preserved in `bin/histogram-popup-original-performance.csv`.

## Validation

Relevant checks cover all input levels, invalid/constant/high-background data, padded ROIs, concurrent renders, complete parallel output writes, faint-star visibility, idle and rapid slider refresh, native-viewer isolation, common inspector transfer, popup bindings and resetting all levels. Histogram checks cover exact count conservation, rendered-byte agreement, pool reuse, inspector gutters and frozen/bounded log and linear paths. A visible isolated WPF harness is available with `--levels-ui <FITS-folder>`; it uses mocked equipment and does not load a user's NINA profile.

Validation passed for the shared track: 13 histogram checks, 21 levels checks, 87 preview display checks and 386 workspace checks, including actual midpoint canvas positions after either endpoint moves and preservation of a manually adjusted midpoint ratio. The actual popup was inspected through Windows Graphics Capture using a real FITS image. All three handles were dragged, midtone-left brightened the image, Shift+Right moved the midtone by 0.001, and Auto restored 0 / 0.5 / 1 and the image. Earlier checks covered logarithmic/linear switching, disabled adjustments in linear mode, Escape, button close/reopen and outside-click dismissal. Owner-window input handlers exist only while this popup is open and are removed when it closes or unloads; no global/class diagnostic hooks were added.
