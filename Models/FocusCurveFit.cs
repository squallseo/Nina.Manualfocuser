using System;
using System.Collections.Generic;
using System.Linq;

namespace Cwseo.NINA.ManualFocuser.Models {
    public readonly record struct FocusFitSample(double Position, double Value, double Error);

    /// <summary>Weighted quadratic in normalized position coordinates.</summary>
    public sealed class FocusCurveFit {
        public double Center { get; private init; }
        public double Scale { get; private init; }
        public double A { get; private init; }
        public double B { get; private init; }
        public double C { get; private init; }
        public double Evaluate(double position) {
            double u = (position - Center) / Scale;
            return (A * u + B) * u + C;
        }
        public double Vertex => Math.Abs(A) <= 1e-10 * Math.Max(1, Math.Abs(C)) ? double.NaN : Center - Scale * B / (2 * A);

        public static bool TryFit(IEnumerable<FocusFitSample> samples, out FocusCurveFit fit) {
            fit = null;
            var data = samples?.Where(p => double.IsFinite(p.Position) && double.IsFinite(p.Value) && double.IsFinite(p.Error)).ToList();
            if (data == null || data.Select(p => p.Position).Distinct().Count() < 3) return false;
            double min = data.Min(p => p.Position), max = data.Max(p => p.Position);
            double center = min + (max - min) / 2, scale = (max - min) / 2;
            if (!double.IsFinite(scale) || scale <= 0) return false;
            var weights = data.Select(p => p.Error > 0 ? 1 / Math.Pow(Math.Max(p.Error, 1e-6), 2) : 1).ToArray();
            double maxWeight = weights.Max();
            var matrix = new double[3, 4];
            for (int n = 0; n < data.Count; n++) {
                double u = (data[n].Position - center) / scale, w = weights[n] / maxWeight;
                double[] terms = { u * u, u, 1 };
                for (int i = 0; i < 3; i++) {
                    for (int j = 0; j < 3; j++) matrix[i, j] += w * terms[i] * terms[j];
                    matrix[i, 3] += w * terms[i] * data[n].Value;
                }
            }
            for (int col = 0; col < 3; col++) {
                int pivot = col;
                for (int row = col + 1; row < 3; row++) if (Math.Abs(matrix[row, col]) > Math.Abs(matrix[pivot, col])) pivot = row;
                if (Math.Abs(matrix[pivot, col]) < 1e-12) return false;
                for (int j = col; j < 4; j++) (matrix[col, j], matrix[pivot, j]) = (matrix[pivot, j], matrix[col, j]);
                for (int row = col + 1; row < 3; row++) {
                    double factor = matrix[row, col] / matrix[col, col];
                    for (int j = col; j < 4; j++) matrix[row, j] -= factor * matrix[col, j];
                }
            }
            var coefficients = new double[3];
            for (int row = 2; row >= 0; row--) {
                double sum = matrix[row, 3];
                for (int col = row + 1; col < 3; col++) sum -= matrix[row, col] * coefficients[col];
                coefficients[row] = sum / matrix[row, row];
            }
            if (coefficients.Any(v => !double.IsFinite(v))) return false;
            fit = new FocusCurveFit { Center = center, Scale = scale, A = coefficients[0], B = coefficients[1], C = coefficients[2] };
            return true;
        }
    }
}
