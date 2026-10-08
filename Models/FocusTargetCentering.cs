using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Model;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;

namespace Cwseo.NINA.ManualFocuser.Models {
    // Match NINA's Center sequence settings, but retain the user's current filter.
    // Solving needs a full sensor image, independent of the focusing exposure/ROI.
    public sealed class FocusTargetCentering {
        public CaptureSequence Sequence { get; }
        public CenterSolveParameter Parameter { get; }

        public FocusTargetCentering(IProfile profile, CameraInfo camera, Coordinates target) {
            var settings = profile.PlateSolveSettings;
            if (camera?.Connected != true) throw new InvalidOperationException("Connect the camera before centering a focus star.");
            if (target == null) throw new ArgumentException("Select a focus star.", nameof(target));
            if (!double.IsFinite(settings.ExposureTime) || settings.ExposureTime <= 0 ||
                (camera.ExposureMin > 0 && settings.ExposureTime < camera.ExposureMin) ||
                (camera.ExposureMax > 0 && settings.ExposureTime > camera.ExposureMax))
                throw new InvalidOperationException("Set a valid exposure in NINA Options > Plate Solving before GOTO.");
            if (!double.IsFinite(profile.TelescopeSettings.FocalLength) || profile.TelescopeSettings.FocalLength <= 0 ||
                !double.IsFinite(profile.CameraSettings.PixelSize) || profile.CameraSettings.PixelSize <= 0)
                throw new InvalidOperationException("Set the telescope focal length and camera pixel size in the NINA profile before GOTO.");
            if (settings.Binning < 1 || !double.IsFinite(settings.Threshold) || settings.Threshold <= 0 ||
                settings.NumberOfAttempts < 1 || !double.IsFinite(settings.ReattemptDelay) || settings.ReattemptDelay < 0)
                throw new InvalidOperationException("Check binning, centering tolerance and retry settings in NINA Options > Plate Solving.");

            Sequence = new CaptureSequence(settings.ExposureTime, CaptureSequence.ImageTypes.SNAPSHOT, null,
                new BinningMode(settings.Binning, settings.Binning), 1) {
                Gain = settings.Gain, Offset = camera.Offset, EnableSubSample = false
            };
            Parameter = new CenterSolveParameter {
                Coordinates = target, Attempts = settings.NumberOfAttempts, Binning = settings.Binning,
                DownSampleFactor = settings.DownSampleFactor, FocalLength = profile.TelescopeSettings.FocalLength,
                MaxObjects = settings.MaxObjects, PixelSize = profile.CameraSettings.PixelSize,
                ReattemptDelay = TimeSpan.FromMinutes(settings.ReattemptDelay), Regions = settings.Regions,
                SearchRadius = settings.SearchRadius, Threshold = settings.Threshold,
                NoSync = profile.TelescopeSettings.NoSync, BlindFailoverEnabled = settings.BlindFailoverEnabled
            };
        }

        public async Task<double> CenterAsync(ICenteringSolver solver, IProgress<PlateSolveProgress> solveProgress,
            IProgress<ApplicationStatus> progress, CancellationToken cancellation) {
            cancellation.ThrowIfCancellationRequested();
            var result = await solver.Center(Sequence, Parameter, solveProgress, progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (result?.Success != true || result.Coordinates == null)
                throw new InvalidOperationException("Plate solving could not center the star. Check NINA's solver settings, exposure and focus; remove the Bahtinov mask while solving.");
            // Verify the final image coordinates rather than trusting a success flag
            // or a previous attempt's separation. Tolerance is in arcminutes.
            double arcseconds = Math.Abs((Parameter.Coordinates - result.Coordinates).Distance.ArcSeconds);
            if (!double.IsFinite(arcseconds) || arcseconds > Parameter.Threshold * 60)
                throw new InvalidOperationException("The solved star position is still outside NINA's centering tolerance.");
            return arcseconds;
        }
    }

    // A host solver may finish a capture just as cancellation/device changes arrive.
    // Check before returning its result so CenteringSolver cannot sync/reslew then.
    public sealed class GuardedFocusCaptureSolver : ICaptureSolver {
        private readonly ICaptureSolver inner;
        private readonly Action checkDevices;
        public GuardedFocusCaptureSolver(ICaptureSolver inner, Action checkDevices) {
            this.inner = inner; this.checkDevices = checkDevices;
        }
        public IImageSolver ImageSolver { get => inner.ImageSolver; set => inner.ImageSolver = value; }
        public async Task<PlateSolveResult> Solve(CaptureSequence seq, CaptureSolverParameter parameter,
            IProgress<PlateSolveProgress> solveProgress, IProgress<ApplicationStatus> progress, CancellationToken ct) {
            ct.ThrowIfCancellationRequested(); checkDevices();
            var result = await inner.Solve(seq, parameter, solveProgress, progress, ct);
            ct.ThrowIfCancellationRequested(); checkDevices();
            return result;
        }
    }
}
