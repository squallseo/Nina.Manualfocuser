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
        private bool missingInBucket;
        public double LatestTime { get; private set; }
        public LiveHfrHistory(IList<DataPoint> points) => this.points = points;
        public void Clear()
        {
            points.Clear(); bucket = -1; missingInBucket = false; LatestTime = 0;
        }
        public void Record(double seconds, double hfr)
        {
            if (!double.IsFinite(seconds) || seconds < LatestTime || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            LatestTime = seconds;
            while (points.Count > 0 && points[0].X < seconds - WindowSeconds) points.RemoveAt(0);
            long next = (long)Math.Floor(seconds / SampleSeconds);
            if (next != bucket || points.Count == 0)
            {
                bucket = next; missingInBucket = !double.IsFinite(hfr);
                points.Add(new DataPoint(seconds, missingInBucket ? double.NaN : hfr));
            }
            else
            {
                // Preserve a break if detection was lost within this 200 ms bin.
                // The instantaneous HFR still updates separately on every frame.
                missingInBucket |= !double.IsFinite(hfr);
                points[points.Count - 1] = new DataPoint(seconds, missingInBucket ? double.NaN : hfr);
            }
        }
        public DataPoint[] DisplayPoints() => points.Select(p => new DataPoint(p.X - LatestTime, p.Y)).ToArray();
    }
}
