using System;
using System.Threading;
using NINA.Image.Interfaces;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models
{
    /// <summary>A read-only ROI over a host-owned 16-bit frame. No full-frame double expansion.</summary>
    public sealed class FocusRawFrame
    {
        private static long nextFrameId;
        public long FrameId { get; } = Interlocked.Increment(ref nextFrameId);
        public ushort[] Pixels { get; }
        public int Stride { get; }
        public int Left { get; }
        public int Top { get; }
        public int Width { get; }
        public int Height { get; }
        public IImageData SourceImage { get; }
        public bool UseAutofocusRoi { get; }
        public FocusRawFrame(ushort[] pixels, int stride, int left, int top, int width, int height, IImageData sourceImage = null, bool useAutofocusRoi = false)
        {
            if (pixels == null || stride < 1 || left < 0 || top < 0 || width < 1 || height < 1 ||
                (long)left + width > stride || ((long)top + height - 1) * stride + left + width > pixels.Length)
                throw new ArgumentException("Invalid raw preview ROI.");
            Pixels = pixels; Stride = stride; Left = left; Top = top; Width = width; Height = height;
            SourceImage = sourceImage;
            UseAutofocusRoi = useAutofocusRoi;
        }
        public ushort Sample(int index)
        {
            if ((uint)index >= (long)Width * Height) throw new ArgumentOutOfRangeException(nameof(index));
            return Pixels[(Top + index / Width) * Stride + Left + index % Width];
        }
        public double[] ToDoubles() => Window(0, 0, Width, Height);
        public double[] ToDoubles(CancellationToken token) => Window(0, 0, Width, Height, token);
        public (double[] Pixels, int Width, int Height) CenterWindow(int size = 256)
        {
            if (size < 1) throw new ArgumentOutOfRangeException(nameof(size));
            int w = Math.Min(size, Width), h = Math.Min(size, Height);
            return (Window((Width - w) / 2, (Height - h) / 2, w, h), w, h);
        }
        private double[] Window(int x, int y, int width, int height, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var result = new double[width * height];
            for (int row = 0; row < height; row++)
            {
                token.ThrowIfCancellationRequested();
                int offset = (Top + y + row) * Stride + Left + x;
                for (int col = 0; col < width; col++) result[row * width + col] = Pixels[offset + col];
            }
            return result;
        }
    }
}
