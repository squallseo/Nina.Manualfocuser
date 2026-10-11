using System;
using System.Collections.Generic;
using System.Linq;
using OxyPlot;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models
{
    /// <summary>Fixed-duration live history, independent of camera frame rate.</summary>
    public sealed class LiveHfrHistory
    {
        public const double WindowSeconds = 120;
        public const double SampleSeconds = .2;
        private readonly IList<DataPoint> points;
        private long bucket = -1;
        private double? timeOrigin;
        private double lastSourceTime;
        public double LatestTime { get; private set; }
        public double AxisMinimum => Math.Max(0, LatestTime - WindowSeconds);
        public double AxisMaximum => Math.Max(WindowSeconds, LatestTime);
        public LiveHfrHistory(IList<DataPoint> points) => this.points = points;
        public void Clear()
        {
            points.Clear(); bucket = -1; LatestTime = 0; timeOrigin = null; lastSourceTime = 0;
        }
        public void Record(double seconds, double hfr)
        {
            if (!double.IsFinite(seconds) || seconds < lastSourceTime || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            lastSourceTime = seconds;
            timeOrigin ??= seconds;
            seconds -= timeOrigin.Value;
            LatestTime = seconds;
            while (points.Count > 0 && points[0].X < seconds - WindowSeconds) points.RemoveAt(0);
            // Missing detections advance the window but add no artificial value
            // or NaN separator. Connect the actual valid measurements instead.
            if (!double.IsFinite(hfr) || hfr <= 0) return;
            long next = (long)Math.Floor(seconds / SampleSeconds);
            if (next != bucket || points.Count == 0)
            {
                bucket = next;
                points.Add(new DataPoint(seconds, hfr));
            }
            else
            {
                points[points.Count - 1] = new DataPoint(seconds, hfr);
            }
        }
        public DataPoint[] DisplayPoints() => points.ToArray();
    }
}
