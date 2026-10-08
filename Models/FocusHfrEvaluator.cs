using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;

namespace Cwseo.NINA.ManualFocuser.Models {
    public partial class ManualFocuserModel {
        public Func<StreamPreviewFrame,CancellationToken,Task<FocusFrameMetric>> CreateFocusHfrEvaluator(string filterName = null) {
            // Pin the selected pluggable detector and analysis settings for this run.
            // Hocus Focus is called through IStarDetection, without an assembly dependency.
            var detector=starDetectionSelector.GetBehavior();
            var settings=profileService.ActiveProfile.ImageSettings;
            var sensitivity=settings.StarSensitivity;var noise=settings.NoiseReduction;
            int brightest=profileService.ActiveProfile.FocuserSettings.AutoFocusUseBrightestStars;
            double stretch=settings.AutoStretchFactor, clipping=settings.BlackClipping;
            bool unlinked=settings.UnlinkedStretch, debayer=settings.DebayerImage;
            var camera=cameraMediator.GetInfo();var sensor=camera.SensorType;
            int gain=camera.Gain,offset=camera.Offset;string id=camera.DeviceId,name=camera.Name;
            double pixelSize=profileService.ActiveProfile.CameraSettings.PixelSize;
            double focalLength=profileService.ActiveProfile.TelescopeSettings.FocalLength;
            bool bayered=sensor is SensorType.RGGB or SensorType.BGGR or SensorType.GRBG or SensorType.GBRG;
            return async (frame,token)=> {
                token.ThrowIfCancellationRequested();
                ushort[] raw=new ushort[frame.Pixels.Length];
                for(int i=0;i<raw.Length;i++) raw[i]=(ushort)Math.Clamp(frame.Pixels[i],0,65535);
                bool frameBayered=frame.IsBayered ?? bayered;
                var metadata=new ImageMetaData();
                metadata.Camera.Id=id;metadata.Camera.Name=name;metadata.Camera.Gain=gain;metadata.Camera.Offset=offset;
                metadata.Camera.BinX=metadata.Camera.BinY=1;metadata.Camera.SensorType=sensor;metadata.Camera.PixelSize=pixelSize;
                metadata.Telescope.FocalLength=focalLength;metadata.FilterWheel.Filter=filterName;
                metadata.Focuser.Position=frame.FocuserPosition;metadata.Image.ExposureTime=frame.ExposureSeconds;
                var data=new BaseImageData(raw,frame.Width,frame.Height,frame.BitDepth,frameBayered,metadata,profileService,detector,null);
                var image=data.RenderImage();
                PixelFormat format=PixelFormats.Gray16;
                if(frameBayered && debayer) { image=image.Debayer(bayerPattern:sensor);format=PixelFormats.Rgb48; }
                image=await image.Stretch(stretch,clipping,unlinked);
                token.ThrowIfCancellationRequested();
                var parameters=new StarDetectionParams { IsAutoFocus=true,Sensitivity=sensitivity,
                    NoiseReduction=noise,NumberOfAFStars=brightest };
                var result=await detector.Detect(image,format,parameters,NullPreviewProgress.Instance,token);
                token.ThrowIfCancellationRequested();
                // Never mutate the detector-owned result or publish derived AF values
                // through UpdateAnalysis/SetImage (host sequence triggers watch them).
                return new FocusFrameMetric(result.AverageHFR,result.HFRStdDev,result.StarList?.Count ?? 0);
            };
        }
    }
}
