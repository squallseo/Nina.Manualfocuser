using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NINA.Core.Utility;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        public double BoundWidth { get; set; }
        public double BoundHeight { get; set; }
        public double ImageRotation { get; private set; }
        private bool imageFlip;
        public bool ImageFlip {
            get => imageFlip;
            set { if (imageFlip==value) return; imageFlip=value; RaisePropertyChanged(); RaisePropertyChanged(nameof(ImageFlipValue)); }
        }
        public double ImageFlipValue => ImageFlip ? -1 : 1;
        public ICommand RotateImageCommand { get; private set; }
        private void InitializeImageTools() => RotateImageCommand = new RelayCommand(_ => {
            ImageRotation=(ImageRotation+90)%360; RaisePropertyChanged(nameof(ImageRotation));
        });
        public BitmapSource LiveDisplayBitmap => LiveDisplayImage is BitmapSource bitmap ? bitmap :
            LiveDisplayImage != null && previewPixels.TryGetValue(LiveDisplayImage,out var entry) ? entry.Bitmap : null;
        public ImageSource LiveDisplayOverlay => LiveDisplayImage is DrawingImage ? LiveDisplayImage : null;
        public InspectorTile[] LiveDisplayInspectorTiles => LiveDisplayImage != null && previewPixels.TryGetValue(LiveDisplayImage, out var entry) ? entry.Tiles : null;
        public bool HasDisplayInspectorTiles => LiveDisplayInspectorTiles != null;
        public bool CanInspectFocusImage => !IsSelectingRoi && FocusPreviewImage != null &&
            previewPixels.TryGetValue(FocusPreviewImage,out var entry) && CanInspectFrame(entry.Raw);
        private bool CanInspectFrame(FocusRawFrame frame) {
            // A software ROI may retain a full sensor source. Do not enable
            // inspection on that source while the live capture ROI is small.
            if (frame == null || IsLiveFocus && (PreviewRoiWidth <= 1024 || PreviewRoiHeight <= 1024)) return false;
            var properties = frame.SourceImage?.Properties;
            return (properties?.Width ?? frame.Width) > 1024 && (properties?.Height ?? frame.Height) > 1024;
        }
        private void UpdateInspectorAvailability() {
            if (IsAberrationInspector && !CanInspectFocusImage) IsAberrationInspector = false;
            RaisePropertyChanged(nameof(CanInspectFocusImage));
        }
        private ImageSource RenderFocusPresentation(FocusRawFrame frame, BahtinovMeasurement overlay) =>
            IsAberrationInspector && CanInspectFrame(frame) ? RenderAberrationPreview(frame) : RenderRawFocusPreview(frame,overlay);
    }
}
