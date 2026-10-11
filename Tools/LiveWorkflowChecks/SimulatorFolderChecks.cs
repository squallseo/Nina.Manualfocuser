using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Model.Equipment.MyCamera.Simulator;

internal static class SimulatorFolderChecks {
    private static object Default(MethodInfo method) => method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    public static async Task Run(ManualFocuserDockableVM vm, IProfileService profiles, ITelescopeMediator telescope, string folder, Action<bool,string> check) {
        var detection=new StarDetection();
        var detectors=Fake.Of<IPluggableBehaviorSelector<IStarDetection>>((m,a)=>m.Name=="GetBehavior"?detection:Default(m));
        var annotator=Fake.Of<IStarAnnotator>((m,a)=>Default(m));
        var annotators=Fake.Of<IPluggableBehaviorSelector<IStarAnnotator>>((m,a)=>m.Name=="GetBehavior"?annotator:Default(m));
        var factory=new ImageDataFactory(profiles,detectors,annotators);
        var exposureFactory=Fake.Of<IExposureDataFactory>((m,a)=>m.Name=="CreateCachedExposureData"
            ?new CachedExposureData((IImageData)a[0],factory):throw new Exception(m.Name));
        using var simulator=new SimulatorCamera(profiles,telescope,exposureFactory,factory);
        simulator.Settings.Type=NINA.Core.Enum.CameraType.DIRECTORY;
        simulator.Settings.DirectorySettings.DirectoryPath=folder;
        simulator.Settings.ImageSettings.IsBayered=false;
        await simulator.DownloadExposure(default);
        check(await simulator.Connect(default),"Native NINA directory simulator connects using the actual FITS files");
        var info=new CameraInfo {Connected=true,DeviceId="Simulator",CanShowLiveView=true,CanSubSample=true,
            XSize=simulator.CameraXSize,YSize=simulator.CameraYSize,BinX=1,BinY=1,ExposureMin=.001,ExposureMax=60};
        var camera=Fake.Of<ICameraMediator>((m,a)=>m.Name switch {
            "GetInfo"=>info,"LiveView"=>Stream((CaptureSequence)a[0],(CancellationToken)a[1]),_=>Default(m)
        });
        var model=new LiveFocusModel(profiles,null,camera);
        async IAsyncEnumerable<IExposureData> Stream(CaptureSequence sequence,[EnumeratorCancellation] CancellationToken token) {
            simulator.EnableSubSample=sequence.EnableSubSample;
            simulator.SubSampleX=(int)sequence.SubSambleRectangle.X;simulator.SubSampleY=(int)sequence.SubSambleRectangle.Y;
            simulator.SubSampleWidth=(int)sequence.SubSambleRectangle.Width;simulator.SubSampleHeight=(int)sequence.SubSambleRectangle.Height;
            simulator.StartLiveView(sequence);
            try { while(true) { token.ThrowIfCancellationRequested(); yield return await simulator.DownloadLiveView(token); } }
            finally { simulator.StopLiveView(); }
        }
        var original=vm.FocusPreviewImage;
        bool oldInspector=vm.IsAberrationInspector;
        var oldCamera=vm.CameraInfo;var oldMode=vm.FocusMode;
        int oldWidth=vm.PreviewRoiWidth,oldHeight=vm.PreviewRoiHeight;
        var render=vm.GetType().GetMethod("RenderFocusPresentation",BindingFlags.NonPublic|BindingFlags.Instance);
        var setImage=vm.GetType().GetProperty(nameof(vm.FocusPreviewImage));
        vm.UpdateDeviceInfo(info);vm.FocusMode="Live";
        vm.PreviewRoiWidth=1536;vm.PreviewRoiHeight=1536;
        var view=new ManualFocuserDockableView {DataContext=vm,Width=920,Height=890};
        var size=new Size(920,890);view.Measure(size);view.Arrange(new Rect(size));view.UpdateLayout();
        int fileCount=Directory.GetFiles(folder,"*",SearchOption.AllDirectories).Count(BaseImageData.FileIsSupported);
        var analyzer=new NinaMultiStarHfr(profiles,factory,detectors,camera);
        Task<FocusHfrMeasurement> hfr=null;
        using var stop=new CancellationTokenSource();
        try {
            await using(var smallStream=model.StartFocusStreaming(.25,1024,50,50,stop.Token,1024,retainRawPixels:true)) {
                vm.PreviewRoiWidth=1024;vm.PreviewRoiHeight=1024;
                for(int frame=0;frame<fileCount;frame++) {
                    var received=await smallStream.ReadAsync(stop.Token);
                    setImage.SetValue(vm,await Task.Run(()=>(ImageSource)render.Invoke(vm,new object[]{received.RawFrame,null})));
                    for(int attempt=0;attempt<4;attempt++){vm.IsAberrationInspector=true;vm.IsAberrationInspector=false;}
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();
                    check(!vm.CanInspectFocusImage&&!vm.IsAberrationInspector&&vm.LiveDisplayInspectorTiles==null&&vm.LiveDisplayBitmap.PixelWidth==1024,
                        $"Native simulator 1024 ROI frame {frame+1}/{fileCount}: repeated Inspector requests are rejected");
                }
            }
            vm.PreviewRoiWidth=1536;vm.PreviewRoiHeight=1536;
            await using var stream=model.StartFocusStreaming(.25,1536,50,50,stop.Token,1536,retainRawPixels:true);
            for(int frame=0;frame<fileCount*3;frame++) {
                var received=await stream.ReadAsync(stop.Token);
                var raw=received.RawFrame;
                if(hfr?.IsCompleted==true){await hfr;hfr=null;}
                if(hfr==null)hfr=Task.Run(()=>analyzer.AnalyzeAsync(new FocusRawFrame(raw.Pixels,raw.Stride,(raw.Stride-512)/2,(raw.Height-512)/2,512,512,raw.SourceImage),stop.Token));
                vm.IsAberrationInspector=false;
                var normal=await Task.Run(()=>(ImageSource)render.Invoke(vm,new object[]{raw,null}));
                setImage.SetValue(vm,normal);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();
                var native=((Grid)view.FindName("PreviewCard")).Children.OfType<NINA.WPF.Base.View.ImageView>().Single();
                if(frame%3==0) {
                    var header=(Grid)((Grid)native.Content).Children[0];
                    var zoomIn=header.Children.OfType<Button>().Single(b=>Grid.GetColumn(b)==0);
                    var zoomOut=header.Children.OfType<Button>().Single(b=>Grid.GetColumn(b)==1);
                    for(int step=0;step<14;step++) {
                        (step<6?zoomIn:zoomOut).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();
                    }
                }
                vm.IsAberrationInspector=true;
                check(vm.IsAberrationInspector,"Inspector remains available for a 1536-pixel live ROI");
                var preview=await Task.Run(()=>(ImageSource)render.Invoke(vm,new object[]{raw,null}));
                setImage.SetValue(vm,preview);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();
                if(frame==0) {
                    var header=(Grid)((Grid)native.Content).Children[0];
                    header.Children.OfType<Button>().Single(b=>Grid.GetColumn(b)==2).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();
                }
                var surface=(Grid)((FocusRoiOverlay)native.ImageAreaContent).FindName("PreviewSurface");
                var screenshot=new RenderTargetBitmap(920,890,96,96,PixelFormats.Pbgra32);screenshot.Render(view);
                var pixels=new byte[920*890*4];screenshot.CopyPixels(pixels,920*4,0);
                bool good=vm.LiveDisplayInspectorTiles?.Length==9;
                for(int i=0;i<vm.LiveDisplayInspectorTiles.Length;i++) {
                    var tile=vm.LiveDisplayInspectorTiles[i];
                    int expectedX=i%3*520,expectedY=i/3*520;
                    good &= tile.X==expectedX && tile.Y==expectedY;
                    var point=surface.TransformToAncestor(view).Transform(new Point(expectedX+256,expectedY+256));
                    if(point.X<8 || point.X>=912 || point.Y<8 || point.Y>=882) { good=false;Console.WriteLine($"Tile outside viewport: {point}");continue; }
                    long signal=0;
                    for(int y=-8;y<=8;y++)for(int x=-8;x<=8;x++)signal+=pixels[(((int)point.Y+y)*920+(int)point.X+x)*4];
                    good &= signal>1000;
                }
                if(!good) {
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(screenshot));
                    using var output=File.Create("bin/simulator-cycle-failure.png");encoder.Save(output);
                }
                var canvas=(Canvas)native.FindName("PART_Canvas");
                check(good&&canvas.ActualWidth==vm.LiveDisplayBitmap.PixelWidth&&canvas.ActualHeight==vm.LiveDisplayBitmap.PixelHeight,
                    $"Native simulator toggle frame {frame+1}/{fileCount*3}: all nine tiles remain visible with current bitmap bounds");
            }
        } finally {
            stop.Cancel();
            if(hfr!=null)try{await hfr;}catch(OperationCanceledException){}
            vm.IsAberrationInspector=false;vm.FocusMode=oldMode;vm.UpdateDeviceInfo(oldCamera);
            vm.PreviewRoiWidth=oldWidth;vm.PreviewRoiHeight=oldHeight;
            setImage.SetValue(vm,original);vm.IsAberrationInspector=oldInspector;
        }
        Console.WriteLine($"Native simulator: {fileCount} files, one 1024-ROI cycle and three Inspector-toggle cycles, concurrent native multi-star HFR analysis.");
    }
}
