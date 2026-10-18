using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

namespace Cwseo.NINA.ManualFocuser.Dockables
{
    public partial class ManualFocuserDockableVM
    {
        private bool aberrationInspector;
        public bool IsAberrationInspector {
            get => aberrationInspector;
            set { if (value && !CanInspectFocusImage) return; if (SetAberrationInspector(value)) { stretchRefresh?.Cancel(); _ = RefreshPreviewStretchAsync(); } }
        }
        public bool CanUseBahtinovOverlay => !IsAberrationInspector;
        private bool SetAberrationInspector(bool enabled)
        {
            if (aberrationInspector == enabled) return false;
            aberrationInspector = enabled;
            RaisePropertyChanged(nameof(IsAberrationInspector)); RaisePropertyChanged(nameof(CanUseBahtinovOverlay));
            return true;
        }

        private ImageSource RenderAberrationPreview(FocusRawFrame source, double? strength = null)
        {
            double level = strength ?? PreviewStretchStrength;
            bool autoStretch = PreviewAutoStretch;
            var image = source.SourceImage;
            var inspection = image?.Properties != null && image.Data?.FlatArray != null
                ? new FocusRawFrame(image.Data.FlatArray,image.Properties.Width,0,0,image.Properties.Width,image.Properties.Height,image)
                : source;
            var layout = FocusAberrationMosaic.Plan(inspection.Width, inspection.Height);
            var levels = CurrentPreviewLevels;
            FocusHistogram histogram = null;
            var bytes = FocusAberrationMosaic.Display(inspection, layout, level, autoStretch, levels, h => histogram = h);
            // Materialize an opaque, compositor-ready bitmap on the worker. Avoid
            // deferring Gray8 texture conversion to the host's large VisualBrush.
            var displayPixels = new int[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) displayPixels[i] = unchecked((int)0xFF000000) | bytes[i] * 0x010101;
            var bitmap = BitmapSource.Create(layout.Width, layout.Height, 96, 96, PixelFormats.Pbgra32, null,
                displayPixels, checked(layout.Width * 4));
            bitmap.Freeze();
            // Give the compositor nine independent textures rather than one large
            // stretched surface. Sensor crops and the common stretch stay identical.
            var tiles = new InspectorTile[layout.Tiles.Length];
            for (int i = 0; i < tiles.Length; i++) {
                var tile = layout.Tiles[i];
                var pixels = new int[layout.Size * layout.Size];
                for (int row = 0; row < layout.Size; row++)
                    System.Array.Copy(displayPixels, (tile.DisplayY + row) * layout.Width + tile.DisplayX, pixels, row * layout.Size, layout.Size);
                var tileBitmap = BitmapSource.Create(layout.Size, layout.Size, 96, 96, PixelFormats.Pbgra32, null, pixels, layout.Size * 4);
                tileBitmap.Freeze();
                tiles[i] = new InspectorTile(tile.DisplayX, tile.DisplayY, tileBitmap);
            }
            // Keep the original ushort source for stretch changes, but publish
            // mosaic dimensions so the host never rasterizes a full sensor bitmap.
            previewPixels.Add(bitmap, new PreviewPixels(null, layout.Width, layout.Height, null, level, bitmap, source, Inspector: true, AutoStretch: autoStretch, Tiles: tiles) { StarAnnotation = CreateStarAnnotation(source), Levels = levels, Histogram = histogram });
            return bitmap;
        }
    }
}
