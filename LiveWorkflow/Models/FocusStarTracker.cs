using System;
using System.Collections.Generic;
using System.Linq;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models {
    // One session's identities. A missing star stays missing instead of being
    // replaced by an unrelated newly bright object; it can reappear later.
    public sealed class FocusStarTracker {
        private FocusStarSample[] reference;
        private (int Left, int Top, int Width, int Height) region;
        public void Reset() => reference = null;
        public bool HasReference(FocusRawFrame frame) => reference != null && region == (frame.Left, frame.Top, frame.Width, frame.Height);
        public FocusHfrMeasurement Match(FocusHfrMeasurement measurement) {
            if (measurement == null) return null;
            var currentRegion = (measurement.Left, measurement.Top, measurement.Width, measurement.Height);
            if (measurement.Width > 0 && region != currentRegion) { Reset(); region = currentRegion; }
            var samples = measurement.StarSamples.Where(s => double.IsFinite(s.X) && double.IsFinite(s.Y) && double.IsFinite(s.Hfr) && s.Hfr > 0).ToArray();
            if (reference == null) {
                if (samples.Length == 0) return measurement;
                reference = samples.ToArray();
                return Aggregate(measurement, samples);
            }
            var matched = new List<FocusStarSample>();
            var used = new bool[samples.Length];
            for (int i = 0; i < reference.Length; i++) {
                var previous = reference[i];
                double radius = Math.Clamp(previous.Hfr * 2 + 4, 8, 24);
                double closest = radius * radius;
                int index = -1;
                for (int j = 0; j < samples.Length; j++) {
                    if (used[j]) continue;
                    double dx = samples[j].X - previous.X, dy = samples[j].Y - previous.Y;
                    double distance = dx * dx + dy * dy;
                    if (distance < closest) { closest = distance; index = j; }
                }
                if (index < 0) continue;
                used[index] = true;
                reference[i] = samples[index];
                matched.Add(samples[index]);
            }
            return Aggregate(measurement, matched.ToArray());
        }
        private static FocusHfrMeasurement Aggregate(FocusHfrMeasurement measurement, FocusStarSample[] stars) {
            if (stars.Length == 0) return measurement with { Hfr = double.NaN, Spread = double.NaN, Stars = 0, StarSamples = stars };
            var values = stars.Select(s => s.Hfr).OrderBy(h => h).ToArray();
            double median = values.Length % 2 == 0 ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2 : values[values.Length / 2];
            double mean = values.Average();
            double spread = values.Length > 1 ? Math.Sqrt(values.Sum(h => (h - mean) * (h - mean)) / (values.Length - 1)) : 0;
            return measurement with { Hfr = median, Spread = spread, Stars = stars.Length, StarSamples = stars };
        }
    }
}
