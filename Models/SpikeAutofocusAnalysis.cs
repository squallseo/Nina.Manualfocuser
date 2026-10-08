using System;
using System.Linq;

namespace Cwseo.NINA.ManualFocuser.Models {
    /// <summary>Compare widths on the same physical diffraction axis for the entire AF run.</summary>
    public sealed class SpikeAutofocusAnalysis {
        private readonly SpikeAnalysisParams parameters;
        public double LockedAngleDeg { get; private set; } = double.NaN;
        public SpikeAutofocusAnalysis(SpikeAnalysisParams parameters) {
            this.parameters = parameters.Clone();
            this.parameters.autoSpikeAngle = true;
            this.parameters.adaptiveCentroidWindow = true;
        }
        // Call only when the caller discards the entire old focus curve (exposure
        // recovery). Tracking-only resets keep the current scan's acquired axis.
        public void RestartScan() {
            LockedAngleDeg = double.NaN;
            parameters.autoSpikeAngle = true;
        }
        public SpikeFrameResult Evaluate(ushort[] pixels, int width, int height, SpikeTrackingState tracking, double currentHfr = double.NaN) {
            if (pixels != null && width>0 && height>0 && pixels.Length==(long)width*height) {
                if (!double.IsFinite(currentHfr)) {
                    var local=FocusRoi.CenterWindow(pixels.Select(v=>(double)v).ToArray(),width,height);
                    currentHfr=QuickFocusMetrics.HalfFluxRadius(local.Pixels,local.Width,local.Height);
                }
            }
            // Focus shrinks the footprint. Inspect the currently supported radial
            // range rather than the faint outskirts of the initial defocused star.
            // Keep tracking, axis and the width aperture fixed for comparable metrics.
            parameters.parallelSpikeCoreRadiusPx=double.IsFinite(currentHfr) && currentHfr>0
                ? Math.Clamp(Math.Ceiling(currentHfr*4),6,60) : double.NaN;
            var result = SpikeCore.Evaluate(pixels, width, height, parameters, tracking);
            // A four-vane pattern can have two valid perpendicular axes. Changing
            // to the stronger one mid-scan changes the quantity being minimized.
            // Only acquire from a valid frame; geometry/SNR gates still run on
            // the locked axis in every later frame, including final verification.
            if (!double.IsFinite(LockedAngleDeg) && result.IsValid && result.HasClearSpikes &&
                double.IsFinite(result.Metric) && result.Metric > 0 && double.IsFinite(result.UsedAngleDeg)) {
                LockedAngleDeg = result.UsedAngleDeg;
                parameters.spikeAngleDeg = LockedAngleDeg;
                parameters.autoSpikeAngle = false;
            }
            return result;
        }
    }
}
