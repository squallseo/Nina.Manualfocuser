using System;
using System.Linq;
using NINA.Equipment.Equipment.MyCamera;

namespace Cwseo.NINA.ManualFocuser.Models {
    // Discover normal cameras from their capability. Overrides refer to native
    // NINA driver categories, never display names or a list of ASI models.
    public static class FocusCameraSupport {
        public static bool IsNativeAsi(string id) => id?.StartsWith("ZWOptical_",StringComparison.OrdinalIgnoreCase)==true;
        public static bool IsNativeToupTek(string id) => id?.StartsWith("ToupTek_",StringComparison.OrdinalIgnoreCase)==true;
        public static bool SupportsStreaming(CameraInfo camera) {
            if(camera?.Connected!=true) return false;
            if(IsNativeAsi(camera.DeviceId) || IsNativeToupTek(camera.DeviceId)) return true;
            if(camera.DeviceId?.StartsWith("QHY600M-",StringComparison.OrdinalIgnoreCase)==true) {
                var modes=camera.ReadoutModes?.ToArray();
                string mode=modes!=null && camera.ReadoutMode>=0 && camera.ReadoutMode<modes.Length ? modes[camera.ReadoutMode] : null;
                var normalized=new string((mode??"").Replace("*","x").Replace("×","x").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                return !normalized.Contains("bin3x3") && !normalized.Contains("3x3bin");
            }
            return camera.CanShowLiveView;
        }
        public static FocusRoi.Rectangle FitRoi(string id,int sensorWidth,int sensorHeight,int width,int height,double centerX,double centerY) {
            if(IsNativeAsi(id)) return FocusRoi.Fit(sensorWidth,sensorHeight,width,height,centerX,centerY,8,2);
            int alignment=id?.StartsWith("QHY600M-",StringComparison.OrdinalIgnoreCase)==true ? 4 : 2;
            return FocusRoi.Fit(sensorWidth,sensorHeight,width,height,centerX,centerY,alignment);
        }
    }
}
