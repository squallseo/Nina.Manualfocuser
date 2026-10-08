using System;
using System.Collections.Generic;
using System.Linq;
using OxyPlot.Series;

namespace Cwseo.NINA.ManualFocuser.Models {
    public readonly record struct FocusFrameMetric(double Value, double StarScatter = 0, int Stars = 1);
    public sealed record FocusPointStatistics(int Position, double Median, double Uncertainty,
        double[] Values, double[] Inliers, int Attempted, BoxPlotItem Box) {
        public static FocusPointStatistics Create(int position, IReadOnlyList<FocusFrameMetric> frames, bool signed) {
            var valid = frames.Where(f => double.IsFinite(f.Value) && (signed || f.Value > 0)).ToArray();
            int minimum = Math.Max(3, (int)Math.Ceiling(frames.Count * .6));
            if (valid.Length < minimum) throw new InvalidOperationException($"Position {position}: only {valid.Length}/{frames.Count} valid frames. Check ROI, exposure and focus.");
            double[] values = valid.Select(f => f.Value).OrderBy(v => v).ToArray();
            double median = Quantile(values, .5), mad = Quantile(values.Select(v => Math.Abs(v-median)).OrderBy(v=>v).ToArray(), .5);
            double limit = Math.Max(.02, 3.5 * 1.4826 * mad);
            var accepted = valid.Where(f => Math.Abs(f.Value-median) <= limit).ToArray();
            if (accepted.Length < minimum) throw new InvalidOperationException($"Position {position}: measurements do not agree ({accepted.Length}/{frames.Count} inliers).");
            double[] inliers = accepted.Select(f=>f.Value).OrderBy(v=>v).ToArray();
            median = Quantile(inliers, .5);
            double scatter = 1.4826 * Quantile(inliers.Select(v=>Math.Abs(v-median)).OrderBy(v=>v).ToArray(), .5);
            double spatialVariance = accepted.Average(f => double.IsFinite(f.StarScatter) && f.StarScatter >= 0 ? f.StarScatter*f.StarScatter/Math.Max(1,f.Stars) : 0);
            double uncertainty = Math.Max(.001, Math.Sqrt((scatter*scatter + spatialVariance)/accepted.Length));
            double q1 = Quantile(values,.25), q3 = Quantile(values,.75), iqr = q3-q1;
            double[] whiskers = values.Where(v=>v>=q1-1.5*iqr && v<=q3+1.5*iqr).ToArray();
            var box = new BoxPlotItem(position,whiskers.First(),q1,Quantile(values,.5),q3,whiskers.Last()) {
                Mean = inliers.Average(), Outliers = values.Where(v=>v<whiskers.First() || v>whiskers.Last()).ToList(),
                Tag = $"{valid.Length}/{frames.Count} valid, {accepted.Length} inliers"
            };
            return new(position,median,uncertainty,values,inliers,frames.Count,box);
        }
        private static double Quantile(double[] sorted,double p) {
            double index=(sorted.Length-1)*p; int lo=(int)index,hi=(int)Math.Ceiling(index);
            return sorted[lo]+(sorted[hi]-sorted[lo])*(index-lo);
        }
    }
}
