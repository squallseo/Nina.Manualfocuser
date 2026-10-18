using System;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models {
    /// <summary>Immutable exact value/count pairs, shared by a frame's levels and plot.</summary>
    public sealed class FocusHistogram {
        private readonly ushort[] values;
        private readonly int[] counts;
        public int OccupiedLevels => values.Length;
        public int PixelCount { get; }
        public int Maximum { get; }
        public double Median { get; }
        public double Mad { get; }
        internal int ValueAt(int index) => values[index];
        internal int CountAt(int index) => counts[index];
        public long StorageBytes => (long)values.Length * (sizeof(ushort) + sizeof(int));

        internal FocusHistogram(int[] histogram, int pixelCount, int maximum, double median, double mad) {
            int occupied = 0;
            for (int i = 0; i < 65536; i++) if (histogram[i] != 0) occupied++;
            values = new ushort[occupied]; counts = new int[occupied];
            int index = 0;
            for (int i = 0; i < 65536; i++) if (histogram[i] != 0) {
                values[index] = (ushort)i; counts[index++] = histogram[i];
            }
            PixelCount = pixelCount; Maximum = maximum; Median = median; Mad = mad;
        }
    }

    /// <summary>256 bins per curve; contains no image, raw buffer or WPF objects.</summary>
    public sealed class FocusHistogramPlot {
        private readonly long[] input, display;
        public const int Bins = 256;
        public long InputAt(int index) => input[index];
        public long DisplayAt(int index) => display[index];
        public int PixelCount { get; }
        public double ShadowsPercent { get; }
        public double HighlightsPercent { get; }
        internal FocusHistogramPlot(long[] input, long[] display, int count, long shadows, long highlights) {
            this.input = input; this.display = display; PixelCount = count;
            ShadowsPercent = 100.0 * shadows / count; HighlightsPercent = 100.0 * highlights / count;
        }
    }
}
