using System;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading.Tasks;
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
            ? FocusCameraSupport.FitRoi(CameraInfo?.DeviceId,SelectionSensorWidth,SelectionSensorHeight,PreviewRoiWidth,PreviewRoiHeight,PreviewCenterX,PreviewCenterY) : default;
        public ImageSource RoiSelectionPreview { get; private set; }
        public ICommand SetRoiSizeCommand { get; private set; }
        public ICommand ConfirmRoiCommand { get; private set; }
        public ICommand AutoRoiCommand { get; private set; }
        private void InitializeRoiSelection() {
            AutoRoiCommand=new AsyncCommand<int>(()=>RunGuarded("Auto ROI",SelectAutoRoiAsync),_=>CanStartAssist());
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
        private async Task<int> SelectAutoRoiAsync() {
            if(!CanStartAssist()) return 0;
            double requestedCenterX=PreviewCenterX,requestedCenterY=PreviewCenterY;
            var settings=DataModel.CreateFocusCaptureSettings(PreviewExposureMs/1000,1024,1024,requestedCenterX,requestedCenterY);
            string cameraId=cameraMediator.GetInfo().DeviceId;
            bool mask=FocusMode=="Auto" ? AutofocusMethod=="Bahtinov" : FocusMode=="Live" && AnalyzeBahtinov;
            bool reserved=false;
            void EnsureCamera() {
                var camera=cameraMediator.GetInfo();
                if(camera?.Connected!=true || camera.DeviceId!=cameraId || camera.XSize!=settings.SensorWidth || camera.YSize!=settings.SensorHeight)
                    throw new InvalidOperationException("Camera disconnected or changed. ROI was not applied.");
            }
            try {
                BeginAssist(false);
                reserved=true;
                SetAssistStatus("Auto ROI | Capturing the star area...");
                var token=assistCts.Token;
                EnsureCamera();
                var frame=await DataModel.CaptureFocusPreviewAsync(settings.Seconds,settings.Roi.Width,requestedCenterX,requestedCenterY,token,roiHeight:settings.Roi.Height);
                var diagnostics=CreateDiagnosticSession("AutoROI");
                await diagnostics.SaveAsync(frame.Pixels,frame.Width,frame.Height,new {
                    TimestampUtc=DateTime.UtcNow,Phase="auto-roi",CameraId=cameraId,ExposureSeconds=settings.Seconds,
                    SensorRoi=settings.Roi,Mask=mask,DisplayStretched=false
                });
                double searchX=Math.Clamp(settings.SensorWidth*requestedCenterX/100-settings.Roi.X,0,frame.Width-1);
                double searchY=Math.Clamp(settings.SensorHeight*requestedCenterY/100-settings.Roi.Y,0,frame.Height-1);
                FocusRoi.Rectangle selected=await Task.Run(()=>mask
                    ? BahtinovAutoRoi.Find(frame.Pixels,frame.Width,frame.Height,token,searchX,searchY).Roi
                    : SpikeAutoRoi.Find(frame.Pixels,frame.Width,frame.Height,token,searchX,searchY).Roi,token);
                token.ThrowIfCancellationRequested();
                EnsureCamera();
                double centerX=100.0*(settings.Roi.X+selected.X+selected.Width/2.0)/settings.SensorWidth;
                double centerY=100.0*(settings.Roi.Y+selected.Y+selected.Height/2.0)/settings.SensorHeight;
                var applied=DataModel.CreateFocusCaptureSettings(settings.Seconds,selected.Width,selected.Height,centerX,centerY);
                await diagnostics.EventAsync(new { Selected=selected,AppliedSensorRoi=applied.Roi });
                token.ThrowIfCancellationRequested();
                var crop=BahtinovAutoRoi.Crop(frame.Pixels,frame.Width,frame.Height,selected);
                var preview=await Task.Run(()=>RenderFocusPreview(crop,selected.Width,selected.Height,null),token);
                token.ThrowIfCancellationRequested();EnsureCamera();
                // Commit only after successful detection. Shared selection survives
                // subsequent mode switches/start; failure leaves it untouched.
                if(overviewSensorWidth!=settings.SensorWidth || overviewSensorHeight!=settings.SensorHeight) {
                    overviewPixels=null;overviewImage=null;overviewWidth=overviewHeight=overviewSensorWidth=overviewSensorHeight=0;
                }
                previewRoiWidth=applied.Roi.Width;previewRoiHeight=applied.Roi.Height;
                previewX=100.0*(applied.Roi.X+applied.Roi.Width/2.0)/settings.SensorWidth;
                previewY=100.0*(applied.Roi.Y+applied.Roi.Height/2.0)/settings.SensorHeight;
                ResetSharedFocusMeasurements();IsSelectingRoi=false;
                RaisePropertyChanged(nameof(PreviewRoiWidth));RaisePropertyChanged(nameof(PreviewRoiHeight));
                RaisePropertyChanged(nameof(PreviewRoiPreset));RaisePropertyChanged(nameof(PreviewCenterX));RaisePropertyChanged(nameof(PreviewCenterY));
                UpdateRoiSelection();
                FocusPreviewImage=preview;
                RaisePropertyChanged(nameof(FocusPreviewImage));RaisePropertyChanged(nameof(LiveDisplayImage));
                SetAssistStatus("Star area selected. Ready to start.");
                return 1;
            } finally {if(reserved) EndAssist(false);}
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
