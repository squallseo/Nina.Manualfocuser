using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Equipment.Equipment.MyCamera;

internal static partial class PreviewDisplayChecks
{
    private static async Task VerifyInspectorDisplay(ManualFocuserDockableVM vm, Func<BitmapSource> output, Action<bool,string> check)
    {
        const int w=1280,h=1200,stride=1301,left=9,top=13;
        var pixels=Enumerable.Repeat((ushort)65535,stride*1230).ToArray();
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)pixels[(y+top)*stride+x+left]=(ushort)(1+(x*13+y*17)%60000);
        var original=(ushort[])pixels.Clone();
        var source=new FocusRawFrame(pixels,stride,left,top,w,h);
        var plan=FocusAberrationMosaic.Plan(w,h);
        var compact=FocusAberrationMosaic.Extract(source,plan);
        check(plan.Tiles.Length==9 && plan.Size==400 && plan.Tiles[0].X==0 && plan.Tiles[0].Y==0 &&
            plan.Tiles[4].X==440 && plan.Tiles[4].Y==400 && plan.Tiles[8].X==880 && plan.Tiles[8].Y==800,
            "Inspector samples sensor corners, edge centers and exact center at one pixel scale");
        check(compact.Width==1200 && compact.Height==1200 && !compact.Pixels.Contains((ushort)65535),
            "Only the nine scientific crops are copied, excluding padded host rows and columns");
        for(int i=0;i<9;i++) {
            var tile=plan.Tiles[i];int x=i%3*plan.Size,y=i/3*plan.Size;
            check(compact.Sample(y*compact.Width+x)==source.Sample(tile.Y*w+tile.X) &&
                compact.Sample((y+plan.Size-1)*compact.Width+x+plan.Size-1)==source.Sample((tile.Y+plan.Size-1)*w+tile.X+plan.Size-1),
                "Inspector tile "+i+" preserves original sensor pixels without resampling");
        }
        var common=FocusDisplayStretch.RenderRaw(compact);
        var display=FocusAberrationMosaic.Display(source,plan);
        check(plan.Tiles.Select((t,i)=>display[(t.DisplayY+100)*plan.Width+t.DisplayX+100]==common[(i/3*plan.Size+100)*compact.Width+i%3*plan.Size+100]).All(v=>v),
            "All nine tiles use a common automatic stretch instead of independent contrast estimates");
        check(Enumerable.Range(0,plan.Height).All(y=>display.Skip(y*plan.Width+plan.Size).Take(FocusAberrationMosaic.Gap).All(v=>v==0)) && pixels.SequenceEqual(original),
            "Mosaic gutters do not enter stretch statistics or modify raw camera data");
        var large=FocusAberrationMosaic.Plan(9576,6388);
        check(large.Size==512 && large.Width==1552 && large.Height==1552 && large.Tiles.All(t=>t.X+512<=9576 && t.Y+512<=6388),
            "Full QHY600 sensor output remains bounded to nine 512-pixel crops and a small mosaic");
        using(var canceled=new CancellationTokenSource()) {
            canceled.Cancel();bool stopped=false;
            try{FocusAberrationMosaic.Extract(source,plan,canceled.Token);}catch(OperationCanceledException){stopped=true;}
            check(stopped,"Inspector crop extraction observes cancellation");
        }
        var camera=vm.CameraInfo;var type=typeof(ManualFocuserDockableVM);
        vm.UpdateDeviceInfo(new CameraInfo {Connected=true,DeviceId="Inspector.Display.Test",XSize=w,YSize=h});
        vm.PreviewCenterX=70;vm.PreviewCenterY=30;vm.PreviewRoiPreset="512";vm.AnalyzeBahtinov=true;
        var renderRaw=type.GetMethod("RenderRawFocusPreview",BindingFlags.Instance|BindingFlags.NonPublic);
        type.GetProperty(nameof(vm.FocusPreviewImage)).SetValue(vm,renderRaw.Invoke(vm,new object[]{source,null,null}));
        var roiBefore=vm.PreviewRoiRectangle;
        string hfrStatusBefore=vm.LiveHfrText;
        vm.IsAberrationInspector=true;
        check(vm.IsAberrationInspector && vm.PreviewRoiPreset=="512" && vm.PreviewRoiWidth==512 && vm.PreviewRoiHeight==512 &&
            vm.PreviewRoiRectangle==roiBefore && vm.PreviewCenterX==70 && vm.PreviewCenterY==30,
            "Inspector display leaves the capture ROI and anchor unchanged");
        check(!vm.CanUseBahtinovOverlay && vm.LiveHfrText==hfrStatusBefore,
            "Inspector preserves the HFR status and disables its display-only Bahtinov overlay");
        var render=type.GetMethod("RenderAberrationPreview",BindingFlags.Instance|BindingFlags.NonPublic);
        var preview=(ImageSource)render.Invoke(vm,new object[]{source,null});
        check(preview.IsFrozen && preview.Width==plan.Width && preview.Height==plan.Height && preview is BitmapSource bitmap && bitmap.Format==PixelFormats.Pbgra32,
            "Inspector is a compact square bitmap with no text overlays or label rows");
        type.GetField("assistRunning",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(vm,true);
        type.GetProperty(nameof(vm.FocusPreviewImage)).SetValue(vm,preview);
        var published=(BitmapSource)vm.FocusPreviewImage;
        var inspectorHistogram=await vm.GetPreviewHistogramAsync(default);
        check(inspectorHistogram.PixelCount==9*plan.Size*plan.Size,"Inspector histogram uses the same nine scientific tiles and excludes black gutters");
        check(published.IsFrozen && published.PixelWidth==plan.Width && published.PixelHeight==plan.Height && published.Format==PixelFormats.Pbgra32 && ReferenceEquals(published,preview),
            "Private inspector shares its bitmap without extra overlay rasterization");
        check(output() == null, "Inspector never replaces the native captured image");
        var channels=new byte[plan.Width*plan.Height*4];published.CopyPixels(channels,plan.Width*4,0);
        check(Enumerable.Range(0,display.Length).All(i=>channels[i*4]==display[i] && channels[i*4+1]==display[i] && channels[i*4+2]==display[i] && channels[i*4+3]==255),
            "Every published RGB channel preserves all nine stretched tiles including the entire right column");
        vm.PreviewRoiPreset="50%";
        check(vm.IsAberrationInspector,"Capture ROI remains locked during live capture without changing inspector display");
        type.GetProperty(nameof(vm.LiveHfr)).SetValue(vm,2.2);
        vm.PreviewStretchStrength=.5;await Task.Delay(300);
        check(!ReferenceEquals(vm.FocusPreviewImage,preview) && vm.FocusPreviewImage.Width==plan.Width && vm.FocusPreviewImage.Height==plan.Height &&
            vm.LiveHfr==2.2 && pixels.SequenceEqual(original),
            "Inspector stretch rerenders only the mosaic and leaves HFR/raw data unchanged");
        type.GetField("assistRunning",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(vm,false);
        vm.PreviewRoiPreset="512";
        check(vm.IsAberrationInspector,"Selecting a capture ROI does not change the independent inspector display toggle");
        vm.IsAberrationInspector=false; await Task.Delay(250);
        check(!vm.IsAberrationInspector && vm.CanUseBahtinovOverlay && vm.FocusPreviewImage.Width==w && vm.FocusPreviewImage.Height==h && vm.PreviewCenterX==70 && vm.PreviewCenterY==30,
            "Turning inspector off restores the original frame without recapture or changing its ROI");
        vm.IsAberrationInspector=true;
        vm.FocusMode="Live";
        check(!vm.IsAberrationInspector && !vm.CanInspectFocusImage,"Entering Live mode with a 512-pixel ROI automatically disables Inspector");
        vm.PreviewRoiPreset="1024";vm.IsAberrationInspector=true;
        check(!vm.IsAberrationInspector && !vm.CanInspectFocusImage,"The 1024-pixel ROI boundary cannot enable Inspector even with a larger cached source");
        vm.PreviewRoiPreset="100%";vm.IsAberrationInspector=true;
        check(vm.CanInspectFocusImage && vm.IsAberrationInspector,"A live ROI larger than 1024 in both dimensions enables Inspector");
        await Task.Delay(250);
        vm.PreviewRoiPreset="1024";
        await Task.Delay(250);
        check(!vm.IsAberrationInspector && vm.FocusPreviewImage.Width==w,"Reducing the ROI turns Inspector off and restores the normal frame");
        vm.FocusMode="Manual";
        var small=new FocusRawFrame(new ushort[1024*1024],1024,0,0,1024,1024);
        type.GetProperty(nameof(vm.FocusPreviewImage)).SetValue(vm,renderRaw.Invoke(vm,new object[]{small,null,null}));
        vm.IsAberrationInspector=true;
        check(!vm.CanInspectFocusImage && !vm.IsAberrationInspector,"An actual 1024-pixel source also disables Inspector in Manual mode");
        vm.AnalyzeBahtinov=false;vm.UpdateDeviceInfo(camera);vm.PreviewCenterX=50;vm.PreviewCenterY=50;
        vm.ResetPreviewStretchCommand.Execute(null);await Task.Delay(250);
        SaveInspectorExample(vm,render);
        await VerifyInspectorSingleCapture(check);
    }

    private static void SaveInspectorExample(ManualFocuserDockableVM vm,MethodInfo render)
    {
        const int w=2048,h=1536;var pixels=new ushort[w*h];var random=new Random(633);
        for(int i=0;i<pixels.Length;i++)pixels[i]=(ushort)(300+random.Next(30));
        var plan=FocusAberrationMosaic.Plan(w,h);
        for(int i=0;i<9;i++)foreach(int cx in new[]{90,256,420})foreach(int cy in new[]{90,256,420})
            for(int y=-12;y<=12;y++)for(int x=-12;x<=12;x++) {
                double sx=i==4?1.5:2.5,sy=i==4?1.5:1.5+i*.12;
                int p=(plan.Tiles[i].Y+cy+y)*w+plan.Tiles[i].X+cx+x;
                pixels[p]+=(ushort)(35000*Math.Exp(-.5*(x*x/(sx*sx)+y*y/(sy*sy))));
            }
        var image=(ImageSource)render.Invoke(vm,new object[]{new FocusRawFrame(pixels,w,0,0,w,h),1.0});
        var visual=new DrawingVisual();using(var d=visual.RenderOpen())d.DrawImage(image,new Rect(0,0,image.Width,image.Height));
        var shot=new RenderTargetBitmap((int)image.Width,(int)image.Height,96,96,PixelFormats.Pbgra32);shot.Render(visual);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(shot));
        using var file=File.Create("bin/live-focus-inspector.png");png.Save(file);
    }
}
