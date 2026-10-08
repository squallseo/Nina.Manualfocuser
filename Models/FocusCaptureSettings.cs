using System;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace Cwseo.NINA.ManualFocuser.Models {
    public partial class ManualFocuserModel {
        public sealed record FocusCaptureSettings(double Seconds, FocusRoi.Rectangle Roi, int SensorWidth, int SensorHeight);
        public FocusCaptureSettings CreateFocusCaptureSettings(double seconds, int width, int height, double x, double y) {
            var camera = cameraMediator.GetInfo();
            if (camera?.Connected != true) throw new InvalidOperationException("Camera disconnected.");
            if (!double.IsFinite(seconds) || seconds <= 0 ||
                (camera.ExposureMin > 0 && seconds < camera.ExposureMin) || (camera.ExposureMax > 0 && seconds > camera.ExposureMax))
                throw new ArgumentException("Focus exposure is outside the camera range.");
            var roi = FocusCameraSupport.FitRoi(camera.DeviceId,camera.XSize,camera.YSize,width,height,x,y);
            return new FocusCaptureSettings(seconds, roi, camera.XSize, camera.YSize);
        }
        public static ushort[] CropFocusPixels(ushort[] pixels, int width, int height, FocusCaptureSettings settings) {
            if (pixels == null || pixels.Length != (long)width * height) throw new InvalidOperationException("Invalid focus image dimensions.");
            var roi = settings.Roi;
            if (roi.X < 0 || roi.Y < 0 || roi.Width < 32 || roi.Height < 32 ||
                (long)roi.X + roi.Width > settings.SensorWidth || (long)roi.Y + roi.Height > settings.SensorHeight)
                throw new ArgumentException("Invalid focus ROI.");
            if (width == roi.Width && height == roi.Height) return pixels;
            if (width != settings.SensorWidth || height != settings.SensorHeight) throw new InvalidOperationException("Unexpected focus image dimensions.");
            var cropped = new ushort[checked(roi.Width * roi.Height)];
            for (int row = 0; row < roi.Height; row++)
                Array.Copy(pixels, (roi.Y + row) * width + roi.X, cropped, row * roi.Width, roi.Width);
            return cropped;
        }
        private IImageData CropFocusImage(IImageData image, FocusCaptureSettings settings) {
            var props = image.Properties;
            var cropped = CropFocusPixels(image.Data.FlatArray, props.Width, props.Height, settings);
            if (ReferenceEquals(cropped, image.Data.FlatArray)) return image;
            return new BaseImageData(cropped, settings.Roi.Width, settings.Roi.Height, props.BitDepth, props.IsBayered,
                image.MetaData, profileService, starDetectionSelector.GetBehavior(), starAnnotatorSelector.GetBehavior());
        }
    }
}
