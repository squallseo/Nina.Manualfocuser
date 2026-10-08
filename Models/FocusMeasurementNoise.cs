using System;
using System.Linq;

namespace Cwseo.NINA.ManualFocuser.Models {
    public static class FocusMeasurementNoise {
        /// <summary>Robust uncertainty of a stationary batch median, in image pixels.</summary>
        public static double Estimate(double[] values) {
            if(values==null || values.Length<3 || values.Any(v=>!double.IsFinite(v)))
                throw new ArgumentException("At least three finite measurements are required.");
            var sorted=values.OrderBy(v=>v).ToArray();
            double median=sorted[sorted.Length/2];
            var deviations=sorted.Select(v=>Math.Abs(v-median)).OrderBy(v=>v).ToArray();
            return Math.Max(.05,1.253*1.4826*deviations[deviations.Length/2]/Math.Sqrt(values.Length));
        }
        public static double DifferenceThreshold(double first,double second) {
            if(!double.IsFinite(first) || !double.IsFinite(second) || first<0 || second<0)
                throw new InvalidOperationException("Invalid focus measurement uncertainty. No further moves.");
            return 2*Math.Sqrt(first*first+second*second);
        }
    }
}
