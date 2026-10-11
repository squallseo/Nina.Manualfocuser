using System;
using System.Collections.Generic;
using System.Linq;
using OxyPlot;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models {
    public sealed class PositionHfrHistory {
        private readonly Dictionary<int, Queue<double>> samples = new();
        private readonly Queue<int> positions = new();
        private int? previousPosition, lastPosition;
        public void Clear() { samples.Clear(); positions.Clear(); previousPosition = lastPosition = null; }
        public void Record(int position, double hfr) {
            if (position < 0 || !double.IsFinite(hfr) || hfr <= 0) return;
            if (!samples.TryGetValue(position, out var values)) {
                if (positions.Count == 512) samples.Remove(positions.Dequeue());
                samples.Add(position, values = new Queue<double>()); positions.Enqueue(position);
            }
            if (values.Count == 9) values.Dequeue();
            values.Enqueue(hfr);
            // Repeated captures refine the current position's median without
            // losing the direction of the most recent focus movement.
            if (lastPosition != position) { previousPosition = lastPosition; lastPosition = position; }
        }
        private static double Median(Queue<double> samples) {
            var values = samples.OrderBy(value => value).ToArray();
            int middle = values.Length / 2;
            return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
        }
        public DataPoint[] DisplayPoints() => samples.OrderBy(pair => pair.Key)
            .Select(pair => new DataPoint(pair.Key, Median(pair.Value))).ToArray();
        public DataPoint MinimumPoint() => DisplayPoints().OrderBy(point => point.Y).FirstOrDefault(DataPoint.Undefined);
        public DataPoint[] MovementPoints() => previousPosition is int previous && lastPosition is int last &&
            samples.TryGetValue(previous,out var from) && samples.TryGetValue(last,out var to)
            ? new[] { new DataPoint(previous,Median(from)), new DataPoint(last,Median(to)) } : Array.Empty<DataPoint>();
    }
}
