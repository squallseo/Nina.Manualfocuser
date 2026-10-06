using System;
using System.Windows.Input;
using System.Windows.Media;
using Cwseo.NINA.ManualFocuser.Models;
using NINA.Core.Utility;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        private int previewRoiWidth = 512, previewRoiHeight = 512;
        private int overviewSensorWidth, overviewSensorHeight, overviewWidth, overviewHeight;
        private double[] overviewPixels;
        public int PreviewRoiWidth { get => previewRoiWidth; set { previewRoiWidth = Math.Clamp(value / 4 * 4, 32, SelectionSensorWidth >= 32 ? SelectionSensorWidth : int.MaxValue); ResetSharedFocusMeasurements(); RaisePropertyChanged(); RaisePropertyChanged(nameof(PreviewRoiPreset)); UpdateRoiSelection(); } }
        public int PreviewRoiHeight { get => previewRoiHeight; set { previewRoiHeight = Math.Clamp(value / 4 * 4, 32, SelectionSensorHeight >= 32 ? SelectionSensorHeight : int.MaxValue); ResetSharedFocusMeasurements(); RaisePropertyChanged(); RaisePropertyChanged(nameof(PreviewRoiPreset)); UpdateRoiSelection(); } }
        public string PreviewRoiPreset {
            get => previewRoiWidth == previewRoiHeight && (previewRoiWidth == 256 || previewRoiWidth == 512 || previewRoiWidth == 1024) ? previewRoiWidth.ToString() : "Custom";
            set { if (int.TryParse(value, out int size) && (previewRoiWidth != size || previewRoiHeight != size)) { PreviewRoiWidth = size; PreviewRoiHeight = size; } }
        }
        public int SelectionSensorWidth => overviewSensorWidth > 0 ? overviewSensorWidth : CameraInfo?.XSize ?? 0;
        public int SelectionSensorHeight => overviewSensorHeight > 0 ? overviewSensorHeight : CameraInfo?.YSize ?? 0;
        public FocusRoi.Rectangle PreviewRoiRectangle => SelectionSensorWidth >= 32 && SelectionSensorHeight >= 32
            ? FocusRoi.Fit(SelectionSensorWidth, SelectionSensorHeight, PreviewRoiWidth, PreviewRoiHeight, PreviewCenterX, PreviewCenterY,
                CameraInfo?.DeviceId?.StartsWith("QHY600M-", StringComparison.OrdinalIgnoreCase) == true ? 4 : 2) : default;
        public ImageSource RoiSelectionPreview { get; private set; }
        public ICommand SetRoiSizeCommand { get; private set; }
        public ICommand ConfirmRoiCommand { get; private set; }
        private void InitializeRoiSelection() {
            SetRoiSizeCommand = new RelayCommand(p => {
                if (int.TryParse(p?.ToString(), out int size)) { PreviewRoiWidth = size; PreviewRoiHeight = size; }
            }, _ => CanConfigureLive);
            ConfirmRoiCommand = new RelayCommand(_ => {
                FocusPreviewImage = RoiSelectionPreview;
                IsSelectingRoi = false;
                RaisePropertyChanged(nameof(FocusPreviewImage)); RaisePropertyChanged(nameof(LiveDisplayImage));
                SetAssistStatus("Star area selected. Press the video button to begin.");
            }, _ => IsSelectingRoi && CanConfigureLive);
        }
        public void SelectPreviewRoi(double x, double y) {
            if (!IsSelectingRoi) return;
            PreviewCenterX = x * 100; PreviewCenterY = y * 100;
            SetAssistStatus("Star selected.");
        }
        public void EditPreviewRoi(System.Windows.Rect rectangle) {
            if (!IsSelectingRoi || !CanConfigureLive) return;
            previewRoiWidth = Math.Clamp((int)Math.Round(rectangle.Width) / 4 * 4, 32, SelectionSensorWidth);
            previewRoiHeight = Math.Clamp((int)Math.Round(rectangle.Height) / 4 * 4, 32, SelectionSensorHeight);
            previewX = (rectangle.Left + rectangle.Width / 2) / SelectionSensorWidth * 100;
            previewY = (rectangle.Top + rectangle.Height / 2) / SelectionSensorHeight * 100;
            ResetSharedFocusMeasurements();
            RaisePropertyChanged(nameof(PreviewRoiWidth)); RaisePropertyChanged(nameof(PreviewRoiHeight));
            RaisePropertyChanged(nameof(PreviewRoiPreset)); RaisePropertyChanged(nameof(PreviewCenterX)); RaisePropertyChanged(nameof(PreviewCenterY));
            UpdateRoiSelection();
        }
        public void SelectPreviewRectangle(double x1, double y1, double x2, double y2) {
            if (!IsSelectingRoi || SelectionSensorWidth < 32 || SelectionSensorHeight < 32) return;
            PreviewRoiWidth = (int)Math.Round(Math.Abs(x2 - x1) * SelectionSensorWidth);
            PreviewRoiHeight = (int)Math.Round(Math.Abs(y2 - y1) * SelectionSensorHeight);
            PreviewCenterX = (x1 + x2) * 50; PreviewCenterY = (y1 + y2) * 50;
            SetAssistStatus("Release to use this area.");
        }
        private void UpdateRoiSelection() {
            RaisePropertyChanged(nameof(RoiLocationText)); RaisePropertyChanged(nameof(PreviewRoiRectangle));
            if (overviewPixels == null || overviewWidth < 1) return;
            var roi = PreviewRoiRectangle;
            int x = Math.Clamp((int)((double)roi.X / SelectionSensorWidth * overviewWidth), 0, overviewWidth - 1);
            int y = Math.Clamp((int)((double)roi.Y / SelectionSensorHeight * overviewHeight), 0, overviewHeight - 1);
            int w = Math.Clamp((int)Math.Ceiling((double)roi.Width / SelectionSensorWidth * overviewWidth), 1, overviewWidth - x);
            int h = Math.Clamp((int)Math.Ceiling((double)roi.Height / SelectionSensorHeight * overviewHeight), 1, overviewHeight - y);
            var pixels = new double[w * h];
            for (int row = 0; row < h; row++) Array.Copy(overviewPixels, (y + row) * overviewWidth + x, pixels, row * w, w);
            RoiSelectionPreview = RenderFocusPreview(pixels, w, h, null);
            RaisePropertyChanged(nameof(RoiSelectionPreview));
            if (!IsSelectingRoi && !assistRunning) {
                FocusPreviewImage = RoiSelectionPreview;
                RaisePropertyChanged(nameof(FocusPreviewImage)); RaisePropertyChanged(nameof(LiveDisplayImage));
            }
        }
    }
}
