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
            var tiles = new Tile[9];
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    tiles[row * 3 + col] = new Tile(
                        col * (width - size) / 2, row * (height - size) / 2,
                        col * (size + Gap), row * (size + Gap));
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
            return new FocusRawFrame(pixels, width, 0, 0, width, width);
        }

        public static byte[] Display(FocusRawFrame source, Layout layout, double strength = 1)
        {
            // Stretch all nine scientific crops together. Gutters do
            // not bias background/noise estimation, and tiles keep one pixel scale.
            var bytes = FocusDisplayStretch.RenderRaw(Extract(source, layout), strength);
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
