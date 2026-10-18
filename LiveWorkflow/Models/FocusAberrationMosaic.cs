using System;
using System.Threading;

namespace Cwseo.NINA.ManualFocuser.LiveWorkflow.Models
{
    /// <summary>Nine unscaled sensor crops; only the displayed tiles are copied.</summary>
    public static class FocusAberrationMosaic
    {
        public const int TileSize = 512, Gap = 8;
        public readonly record struct Tile(int X, int Y, int DisplayX, int DisplayY);
        public sealed record Layout(int Size, int Width, int Height, Tile[] Tiles);

        public static Layout Plan(int width, int height, int tileSize = TileSize)
        {
            if (width < 3 || height < 3 || tileSize < 1) throw new ArgumentException("Invalid inspector image dimensions.");
            int size = Math.Min(tileSize, Math.Min(width, height) / 3);
            int centerX = (width - size) / 2, rightX = width - size;
            int centerY = (height - size) / 2, bottomY = height - size;
            int middle = checked(size + Gap), end = checked(middle * 2);
            // This fixed 3x3 grid deliberately avoids nested coordinate loops.
            // After optimized compilation on .NET 8.0.21/8.0.24 the loop's right
            // display X changed from 1040 to 536 after warm-up (512px tiles),
            // overlapping the middle column and leaving the right column black.
            var tiles = new[] {
                new Tile(0,       0,       0,      0),
                new Tile(centerX, 0,       middle, 0),
                new Tile(rightX,  0,       end,    0),
                new Tile(0,       centerY, 0,      middle),
                new Tile(centerX, centerY, middle, middle),
                new Tile(rightX,  centerY, end,    middle),
                new Tile(0,       bottomY, 0,      end),
                new Tile(centerX, bottomY, middle, end),
                new Tile(rightX,  bottomY, end,    end)
            };
            return new Layout(size, size * 3 + Gap * 2, size * 3 + Gap * 2, tiles);
        }

        public static FocusRawFrame Extract(FocusRawFrame source, Layout layout, CancellationToken token = default)
        {
            int size = layout.Size, width = checked(size * 3);
            var pixels = new ushort[checked(width * width)];
            for (int i = 0; i < layout.Tiles.Length; i++)
            {
                var tile = layout.Tiles[i];
                for (int row = 0; row < size; row++)
                {
                    token.ThrowIfCancellationRequested();
                    Array.Copy(source.Pixels, (source.Top + tile.Y + row) * source.Stride + source.Left + tile.X,
                        pixels, (i / 3 * size + row) * width + i % 3 * size, size);
                }
            }
            return new FocusRawFrame(pixels, width, 0, 0, width, width, source.SourceImage);
        }

        public static byte[] Display(FocusRawFrame source, Layout layout, double strength = 1, bool autoStretch = true, FocusStretchLevels? levels = null, Action<FocusHistogram> histogram = null)
        {
            // Stretch all nine scientific crops together. Gutters do
            // not bias background/noise estimation, and tiles keep one pixel scale.
            var bytes = FocusDisplayStretch.RenderRaw(Extract(source, layout), strength, autoStretch, levels, histogram);
            var output = new byte[checked(layout.Width * layout.Height)];
            int size = layout.Size, width = size * 3;
            for (int i = 0; i < layout.Tiles.Length; i++)
                for (int row = 0; row < size; row++)
                    Array.Copy(bytes, (i / 3 * size + row) * width + i % 3 * size,
                        output, (layout.Tiles[i].DisplayY + row) * layout.Width + layout.Tiles[i].DisplayX, size);
            return output;
        }
    }
}
