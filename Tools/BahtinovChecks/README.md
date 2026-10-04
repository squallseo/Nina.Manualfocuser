# Bahtinov synthetic checks

Run `dotnet run --project Tools/BahtinovChecks/BahtinovChecks.csproj -c Release -- bin/bahtinov-samples` from the repository root. The optional argument specifies the output directory; the default is the tool's build directory. Generated FITS and PGM files are explicitly **synthetic**, not captured masked-star data. No telescope commands occur.

The geometric tests construct three Gaussian lines with known offsets and test rotation, near-vertical geometry, opposite signs and invalid round/blank stars. They test measurement geometry, not diffraction physics.

The optical samples independently implement scalar monochromatic Fraunhofer intensity:

`I = |FFT(P(x,y) exp(i d (x²+y²)/R²))|²`.

`P` is a circular pupil of radius90 samples, split into an upper half and two lower quadrants. Binary gratings have a12-sample period,50% duty cycle and orientations0/-25/+25 degrees. A512-square FFT is center-cropped to256 square. Defocus `d` is phase at the pupil edge, in radians, not focuser steps or microns. The modeled mask is illustrative, not a calibrated commercial mask. This follows the Fourier pupil/defocus basis described in [Lendermann et al., Scientific Reports (2018)](https://www.nature.com/articles/s41598-018-34400-z). It excludes atmosphere, color spectrum, finite pixel integration, obstruction, noise and saturation.

The measurement uses angle projections and signed perpendicular distance between the middle line and outer intersection, following the inspectable measurement approach in [BahtiFocus, pinned521f63d](https://github.com/CanardConfit/BahtiFocus/blob/521f63df9a5cc31e1889566ed5f093fc38a36ff3/Bahtinov/Bahtinov.cs). Implementation is independent; no source or numerical dependency was copied. Normal-form lines avoid slope singularities. Bilateral flux, angular local maxima and contrast gates are project quality checks, not thresholds validated by that paper.

Tests assert monotonic signed error across five near-focus physical phase samples, near-zero error at zero phase, bracket-only scan interpolation and an independently generated final verification frame. Scan spacing maps1 radian to100 artificial focuser units solely for testing. This establishes limited synthetic consistency; it does not establish hardware performance or discrimination against every unmasked spider pattern. FITS files use big-endian16-bit data with BZERO32768 and2880-byte blocks, compatible with ordinary FITS readers/NINA simulator.

The scan zero-score method and same-direction final approach are grounded in [SharpCap's official Bahtinov Autofocus documentation](https://docs.sharpcap.co.uk/4.0/17_Focusing.htm). Controller interpolation rejects extrapolation and nonfinite measurements. Camera saturation checks, actual movement limits, settling, repeated-frame verification and cancellation belong to plugin integration. Real through-focus mask data remains necessary.
