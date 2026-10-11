using System;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models
{
    /// <summary>Display-only automatic background/noise and midtone normalization.</summary>
    public static class FocusDisplayStretch
    {
        public static byte[] Render(double[] pixels, double strength = 1)
        {
            if (pixels == null || pixels.Length == 0) throw new ArgumentException("Empty preview.");
            var sample = new double[Math.Min(pixels.Length, 65536)];
            for (int i = 0; i < sample.Length; i++)
            {
                double value = pixels[(int)((long)i * pixels.Length / sample.Length)];
                sample[i] = double.IsFinite(value) ? Math.Max(0, value) : 0;
            }
            var map = CreateMap(sample, strength);
            var bytes = new byte[pixels.Length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = map.Apply(double.IsFinite(pixels[i]) ? pixels[i] : 0);
            return bytes;
        }
        public static byte[] RenderRaw(FocusRawFrame frame, double strength = 1)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            int count = checked(frame.Width * frame.Height);
            var sample = new double[Math.Min(count, 65536)];
            for (int i = 0; i < sample.Length; i++) sample[i] = frame.Sample((int)((long)i * count / sample.Length));
            var map = CreateMap(sample, strength);
            var lookup = new byte[65536];
            for (int i = 0; i < lookup.Length; i++) lookup[i] = map.Apply(i);
            var bytes = new byte[count];
            for (int row = 0; row < frame.Height; row++)
            {
                int source = (frame.Top + row) * frame.Stride + frame.Left, dest = row * frame.Width;
                for (int col = 0; col < frame.Width; col++) bytes[dest + col] = lookup[frame.Pixels[source + col]];
            }
            return bytes;
        }
        private readonly record struct StretchMap(double Black, double Range, double Midtone)
        {
            public byte Apply(double value)
            {
                double x = Math.Clamp((value - Black) / Range, 0, 1);
                double y = (Midtone - 1) * x / ((2 * Midtone - 1) * x - Midtone);
                return (byte)Math.Round(255 * Math.Clamp(y, 0, 1));
            }
        }
        private static StretchMap CreateMap(double[] sample, double strength)
        {
            Array.Sort(sample);
            double median = sample[sample.Length / 2];
            double noise = Math.Max(1, (sample[sample.Length * 3 / 4] - sample[sample.Length / 4]) / 1.349);
            double black = Math.Max(0, median - 2.8 * noise);
            double white = Math.Max(sample[(int)((sample.Length - 1) * .9999)], median + 20 * noise);
            double range = Math.Max(1, white - black);
            double background = Math.Clamp((median - black) / range, 0, 1);
            // Keep automatic black/white estimation; adjust only display midtones.
            double targetBackground = .12 * (double.IsFinite(strength) ? Math.Clamp(strength, .25, 2.5) : 1);
            double midtone = background == 0 ? .5 : Math.Clamp(
                background * (1 - targetBackground) / (background + targetBackground - 2 * background * targetBackground), .0001, .9999);
            return new StretchMap(black, range, midtone);
        }
    }
}
