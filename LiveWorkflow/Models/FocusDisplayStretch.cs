using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models
{
    public readonly record struct FocusStretchLevels(double BlackClipping, double WhitePoint, double TargetBackground)
    {
        // Manual three-handle levels operate on the automatic STF result.
        public double InputBlack { get; init; } = 0;
        public double InputMidtone { get; init; } = .5;
        public double InputWhite { get; init; } = 1;
        public static FocusStretchLevels Default => new(-2.8, 1, .2);
        public FocusStretchLevels Sanitize() => new FocusStretchLevels(
            double.IsFinite(BlackClipping) ? Math.Clamp(BlackClipping, -10, 0) : -2.8,
            double.IsFinite(WhitePoint) ? Math.Clamp(WhitePoint, .01, 1) : 1,
            double.IsFinite(TargetBackground) ? Math.Clamp(TargetBackground, .01, .95) : .2) {
                InputBlack = double.IsFinite(InputBlack) ? Math.Clamp(InputBlack, 0, 1 - 1d / 65535) : 0,
                InputMidtone = double.IsFinite(InputMidtone) ? Math.Clamp(InputMidtone, .001, .999) : .5,
                InputWhite = double.IsFinite(InputWhite) ? Math.Clamp(InputWhite,
                    (double.IsFinite(InputBlack) ? Math.Clamp(InputBlack, 0, 1 - 1d / 65535) : 0) + 1d / 65535, 1) : 1
            };
    }

    /// <summary>Display-only, robust automatic levels; an exact lightweight histogram and a 16-bit LUT.</summary>
    public static class FocusDisplayStretch
    {
        // Retain only occupied value/count pairs, never the pooled worker arrays.
        // Weak keys release statistics with the read-only completed frame.
        private static readonly ConditionalWeakTable<FocusRawFrame, FocusHistogram> StatisticsCache = new();
        private static readonly ParallelOptions Workers = new() { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1)) };

        public static byte[] Render(double[] pixels, double strength = 1, bool autoStretch = true, FocusStretchLevels? levels = null, Action<FocusHistogram> histogram = null)
        {
            if (pixels == null || pixels.Length == 0) throw new ArgumentException("Empty preview.");
            var bytes = new byte[pixels.Length];
            var statistics = autoStretch ? GetHistogram(pixels) : null;
            histogram?.Invoke(statistics);
            var map = autoStretch ? CreateMap(statistics, 65535, strength, levels) : new StretchMap(0, 65535, .5);
            for (int i = 0; i < bytes.Length; i++) bytes[i] = map.Apply(double.IsFinite(pixels[i]) ? pixels[i] : 0);
            return bytes;
        }

        public static byte[] RenderRaw(FocusRawFrame frame, double strength = 1, bool autoStretch = true, FocusStretchLevels? levels = null, Action<FocusHistogram> histogram = null)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            int count = checked(frame.Width * frame.Height);
            double maximum = (1 << Math.Clamp(frame.SourceImage?.Properties.BitDepth ?? 16, 1, 16)) - 1;
            var statistics = autoStretch ? GetHistogram(frame) : null;
            histogram?.Invoke(statistics);
            var map = autoStretch
                ? CreateMap(statistics, maximum, strength, levels)
                : new StretchMap(0, maximum, .5);
            var lookup = new byte[65536];
            for (int i = 0; i < lookup.Length; i++) lookup[i] = map.Apply(i);
            // Every destination pixel is written below before publication.
            // Avoid zeroing a second 61 MP surface before the real LUT pass.
            var bytes = GC.AllocateUninitializedArray<byte>(count);
            void Rows(int start, int end) {
                for (int row = start; row < end; row++) {
                    int source = (frame.Top + row) * frame.Stride + frame.Left, dest = row * frame.Width;
                    for (int col = 0; col < frame.Width; col++) bytes[dest + col] = lookup[frame.Pixels[source + col]];
                }
            }
            // Small ROIs stay on one worker. Cap large-frame concurrency to
            // leave CPU capacity available to the host and HFR calculation.
            if (count < 4 * 1024 * 1024 || Workers.MaxDegreeOfParallelism == 1) Rows(0, frame.Height);
            else Parallel.For(0, (frame.Height + 63) / 64, Workers, block => Rows(block * 64, Math.Min(frame.Height, (block + 1) * 64)));
            return bytes;
        }

        private readonly record struct StretchMap(double Black, double Range, double Midtone, double InputBlack = 0, double InputMidtone = .5, double InputWhite = 1)
        {
            public double Automatic(double value) => Mtf(Midtone, Math.Clamp((value - Black) / Range, 0, 1));
            public byte Apply(double value) {
                double automatic = Automatic(value);
                if (InputBlack == 0 && InputMidtone == .5 && InputWhite == 1) return (byte)Math.Round(255 * automatic);
                return (byte)Math.Round(255 * Mtf(InputMidtone, Math.Clamp((automatic - InputBlack) / (InputWhite - InputBlack), 0, 1)));
            }
        }
        private static double Mtf(double m, double x) => x <= 0 ? 0 : x >= 1 ? 1 : (m - 1) * x / ((2 * m - 1) * x - m);

        private static StretchMap CreateMap(FocusHistogram stats, double maximum, double strength, FocusStretchLevels? requested)
        {
            var levels = (requested ?? FocusStretchLevels.Default).Sanitize();
            double noise = stats.Mad * 1.4826;
            bool highBackground = stats.Median > maximum / 2;
            double automaticWhite = highBackground ? stats.Median - levels.BlackClipping * noise : maximum;
            double black = highBackground ? automaticWhite - maximum : stats.Median + levels.BlackClipping * noise;
            double white = automaticWhite;
            white = Math.Max(black + 1, white * levels.WhitePoint);
            double range = Math.Max(1, white - black);
            double background = Math.Clamp((stats.Median - black) / range, 0, 1);
            double target = Math.Clamp(levels.TargetBackground * (double.IsFinite(strength) ? Math.Clamp(strength, .25, 2.5) : 1), .005, .95);
            // NINA's STF uses median, 1.4826*MAD and this midtones transfer.
            // Normalize between explicit black/white endpoints as well, so the
            // white level can be adjusted independently without changing raw data.
            // Reference: isbeorn/nina NINA.Image/ImageAnalysis/ImageUtility.cs
            double midtone = background <= 0 || background >= 1 ? .5 : Math.Clamp(Mtf(target, background), .000001, .999999);
            return new StretchMap(black, range, midtone, levels.InputBlack, levels.InputMidtone, levels.InputWhite);
        }

        public static FocusHistogram GetHistogram(FocusRawFrame frame) => StatisticsCache.GetValue(frame, MeasureRaw);
        public static FocusHistogram GetHistogram(double[] pixels) {
            if (pixels == null || pixels.Length == 0) throw new ArgumentException("Empty preview.");
            return Measure(pixels.Length, i => pixels[i]);
        }
        public static FocusHistogramPlot PlotHistogram(FocusHistogram histogram, double strength = 1, bool autoStretch = true, FocusStretchLevels? levels = null) {
            if (histogram == null) throw new ArgumentNullException(nameof(histogram));
            var map = autoStretch ? CreateMap(histogram, histogram.Maximum, strength, levels) : new StretchMap(0, histogram.Maximum, .5);
            var input = new long[FocusHistogramPlot.Bins]; var display = new long[FocusHistogramPlot.Bins];
            long shadows = 0, highlights = 0;
            for (int i = 0; i < histogram.OccupiedLevels; i++) {
                int value = histogram.ValueAt(i), count = histogram.CountAt(i);
                double automatic = map.Automatic(value);
                input[(int)Math.Round(255 * automatic)] += count;
                display[map.Apply(value)] += count;
                if (automatic <= map.InputBlack) shadows += count;
                if (automatic >= map.InputWhite) highlights += count;
            }
            return new FocusHistogramPlot(input, display, histogram.PixelCount, shadows, highlights);
        }
        private static FocusHistogram MeasureRaw(FocusRawFrame frame)
        {
            int workers = checked(frame.Width * frame.Height) < 4 * 1024 * 1024 ? 1 : Workers.MaxDegreeOfParallelism;
            var histograms = new int[workers][];
            try {
                Parallel.For(0, workers, Workers, worker => {
                    var histogram = ArrayPool<int>.Shared.Rent(65536);
                    histograms[worker] = histogram;
                    Array.Clear(histogram, 0, 65536);
                    int start = (int)((long)frame.Height * worker / workers), end = (int)((long)frame.Height * (worker + 1) / workers);
                    for (int row = start; row < end; row++) {
                        int source = (frame.Top + row) * frame.Stride + frame.Left;
                        for (int col = 0; col < frame.Width; col++) histogram[frame.Pixels[source + col]]++;
                    }
                });
                for (int worker = 1; worker < workers; worker++)
                    for (int bin = 0; bin < 65536; bin++) histograms[0][bin] += histograms[worker][bin];
                return Analyze(histograms[0], checked(frame.Width * frame.Height), (1 << Math.Clamp(frame.SourceImage?.Properties.BitDepth ?? 16, 1, 16)) - 1);
            } finally {
                foreach (var histogram in histograms) if (histogram != null) ArrayPool<int>.Shared.Return(histogram);
            }
        }

        private static FocusHistogram Measure(int count, Func<int, double> valueAt)
        {
            var histogram = ArrayPool<int>.Shared.Rent(65536);
            Array.Clear(histogram, 0, 65536);
            try {
                for (int i = 0; i < count; i++) {
                    double value = valueAt(i);
                    histogram[(int)Math.Clamp(double.IsFinite(value) ? Math.Round(value) : 0, 0, 65535)]++;
                }
                return Analyze(histogram, count);
            } finally { ArrayPool<int>.Shared.Return(histogram); }
        }
        private static FocusHistogram Analyze(int[] histogram, int samples, int maximum = 65535)
        {
            int total = 0, lower = -1, upper = 0;
            for (int i = 0; i < 65536; i++) {
                total += histogram[i];
                if (lower < 0 && total > (samples - 1) / 2) lower = i;
                if (total > samples / 2) {
                    upper = i;
                    break;
                }
            }
            double median = (lower + upper) / 2.0;
            total = 0;
            int down = (int)Math.Floor(median), up = (int)Math.Ceiling(median);
            while (down >= 0 || up <= 65535) {
                if (down >= 0) total += histogram[down];
                if (up <= 65535 && up != down) total += histogram[up];
                if (total > samples / 2) {
                    return new FocusHistogram(histogram, samples, maximum, median, Math.Abs(up - median));
                }
                down--; up++;
            }
            return new FocusHistogram(histogram, samples, maximum, median, 0);
        }
    }
}
