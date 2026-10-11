using System.Reflection;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

internal static partial class PreviewDisplayChecks
{
    private static async Task VerifyInspectorSingleCapture(Action<bool,string> check)
    {
        const int width=800,height=600;
        var pixels=Enumerable.Range(0,width*height).Select(i=>(ushort)(i%60000)).ToArray();
        var original=(ushort[])pixels.Clone();
        var array=Fake.Of<IImageArray>((m,a)=>m.Name=="get_FlatArray"?pixels:Fake.Unexpected(m));
        var image=Fake.Of<IImageData>((m,a)=>m.Name switch {
            "get_Properties"=>new ImageProperties(width,height,16,false,1,1),"get_Data"=>array,_=>Fake.Unexpected(m)});
        var exposure=Fake.Of<IExposureData>((m,a)=>m.Name=="ToImageData"?Task.FromResult(image):Fake.Unexpected(m));
        var cameraInfo=new CameraInfo {Connected=true,DeviceId="Inspector.Snapshot.Test",CanSubSample=true,XSize=width,YSize=height,ExposureMin=.001,ExposureMax=60};
        var camera=Fake.Of<ICameraMediator>((m,a)=>m.Name=="GetInfo"?cameraInfo:Fake.Unexpected(m));
        CaptureSequence last=null;int captures=0;
        var imaging=Fake.Of<IImagingMediator>((m,a)=> {
            if(m.Name!="CaptureImage")return Fake.Unexpected(m);
            last=(CaptureSequence)a[0];captures++;return Task.FromResult(exposure);
        });
        var model=new LiveFocusModel(null,imaging,camera);
        var result=await model.CaptureFocusPreviewAsync(.2,width,50,50,default,roiHeight:height,retainRawPixels:true);
        var raw=model.LastPreviewRawFrame;
        check(captures==1 && last.SubSambleRectangle.X==0 && last.SubSambleRectangle.Y==0 && last.SubSambleRectangle.Width==width && last.SubSambleRectangle.Height==height,
            "Single-frame Inspector fallback makes one full-sensor exposure through the normal host camera path");
        check(result.Pixels==null && raw.Width==width && raw.Height==height && ReferenceEquals(raw.Pixels,pixels),
            "Non-streaming Inspector retains host ushort pixels without a full-sensor double allocation");
        var center=raw.CenterWindow();
        check(center.Width==256 && center.Height==256 && center.Pixels[0]==pixels[172*width+272],
            "Inspector single-frame HFR keeps the original sensor-center 256-pixel window");
        var taken=(FocusRawFrame)typeof(LiveFocusModel).GetMethod("TakePreviewRawFrame",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(model,null);
        check(ReferenceEquals(taken,raw) && model.LastPreviewRawFrame==null,
            "The model releases its raw-frame reference after handing it to the preview consumer");
        var normal=await model.CaptureFocusPreviewAsync(.2,256,50,50,default);
        check(captures==2 && normal.Pixels.Length==256*256 && normal.Pixels[0]==pixels[172*width+272] && model.LastPreviewRawFrame==null,
            "Ordinary single-ROI captures still use their original centered crop after Inspector");
        using var canceled=new CancellationTokenSource();canceled.Cancel();bool rejected=false;
        try{await model.CaptureFocusPreviewAsync(.2,width,50,50,canceled.Token,roiHeight:height,retainRawPixels:true);}catch(OperationCanceledException){rejected=true;}
        check(rejected && captures==2 && pixels.SequenceEqual(original),
            "A canceled Inspector exposure never touches the camera or changes its raw pixels");
    }
}
