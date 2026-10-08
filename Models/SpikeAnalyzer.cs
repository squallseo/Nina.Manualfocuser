using NINA.Core.Utility;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using System;
using System.Collections.Generic;

namespace Cwseo.NINA.ManualFocuser.Models {

    // ========================================================
    // Thin N.I.N.A. adapter around SpikeCore.
    //
    // IMPORTANT DESIGN RULE
    // --------------------
    // This class must NEVER mutate the StarDetectionResult it is
    // handed. That object is owned by whichever star detector is
    // plugged in (N.I.N.A. built-in *or* Hocus Focus, which uses
    // its own HocusFocusStarDetectionResult / HocusFocusDetectedStar
    // subclasses) and it is published to the rest of the
    // application through IRenderedImage.UpdateAnalysis.
    //
    // Overwriting AverageHFR / replacing StarList with foreign
    // DetectedStar instances corrupts the host's HFR history and
    // makes the other plugin's annotator operate on objects it
    // cannot down-cast. Read from it, never write to it.
    // ========================================================
    public static class SpikeAnalyzer {

        public static SpikeFrameResult Evaluate(
            IImageData imageData,
            SpikeAnalysisParams param,
            StarDetectionResult analysisResult,
            ref SpikeTrackingState trackingState) {

            param ??= new SpikeAnalysisParams();

            if (imageData?.Data?.FlatArray == null || imageData.Properties == null)
                return SpikeFrameResult.Failed(SpikeStatus.NoImage);

            var data = imageData.Data.FlatArray;
            int width = imageData.Properties.Width;
            int height = imageData.Properties.Height;

            var seeds = ExtractSeeds(analysisResult);

            if (trackingState == null || trackingState.TrackedStars == null || trackingState.TrackedStars.Count == 0) {
                trackingState = SpikeCore.CreateTrackingState(seeds, param);
                if (trackingState == null)
                    return SpikeFrameResult.Failed(SpikeStatus.NoSeedStars);
            }

            var result = SpikeCore.Evaluate(data, width, height, param, trackingState);

            // One reseed attempt if tracking has decayed.
            if (!result.IsValid && seeds.Count > 0) {
                var reseed = SpikeCore.CreateTrackingState(seeds, param);
                if (reseed != null) {
                    trackingState = reseed;
                    result = SpikeCore.Evaluate(data, width, height, param, trackingState);
                }
            }

            return result;
        }

        private static List<SpikeSeedStar> ExtractSeeds(StarDetectionResult analysisResult) {
            var seeds = new List<SpikeSeedStar>();
            var list = analysisResult?.StarList;
            if (list == null) return seeds;

            foreach (var s in list) {
                if (s == null) continue;
                seeds.Add(new SpikeSeedStar {
                    X = s.Position.X,
                    Y = s.Position.Y,
                    WidthPx = s.BoundingBox.Width,
                    HeightPx = s.BoundingBox.Height,
                    MaxBrightness = s.MaxBrightness
                });
            }
            return seeds;
        }

        public static void LogFrame(string context, int focuserPosition, SpikeFrameResult r) {
            if (!Properties.Settings.Default.EnableFocusDiagnostics) return;
            if (r.Status != SpikeStatus.Ok) {
                Logger.Debug($"[ManualFocuser/Spike] {context} pos={focuserPosition} status={r.Status} usedStars={r.UsedStars}");
                return;
            }

            Logger.Debug(
                $"[ManualFocuser/Spike] {context} pos={focuserPosition} " +
                $"J={r.Metric:F4} spread={r.Spread:F4} stars={r.UsedStars} " +
                $"varC={r.MedianVarC:F4} varG={r.MedianVarG:F4} kurt={r.MedianKurtosis:F4} " +
                $"angle={r.MeasuredAngleDeg:F1}(x{r.AngleStrength:F2}) used={r.UsedAngleDeg:F1}");
        }
    }
}
