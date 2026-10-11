using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

// Render the shipping WPF views with user-supplied FITS files. No equipment
// commands run and no synthetic pixels or graph measurements are introduced.
internal static class ReadmeScreenshots {
    public static void Run(string folder, string output) {
        Exception failure=null;
        var thread=new Thread(()=> {
            _=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            Dispatcher.CurrentDispatcher.BeginInvoke(async()=> {
                try {await Render(folder,output);}catch(Exception error){failure=error;}
                finally{Dispatcher.CurrentDispatcher.InvokeShutdown();}
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)throw new Exception("README image rendering failed",failure);
    }
    private static object Default(MethodInfo method)=>method.ReturnType!=typeof(void)&&method.ReturnType.IsValueType?Activator.CreateInstance(method.ReturnType):null;
    private static T Equipment<T>(Func<MethodInfo,object[],object> callback=null) where T:class =>
        Fake.Of<T>((m,a)=>callback?.Invoke(m,a)??Default(m));
    private static async Task Render(string folder,string output) {
        Directory.CreateDirectory(output);
        Application.Current.Resources["ProfileService"]=new{ActiveProfile=new{ColorSchemaSettings=new{ColorSchema=new{
            PrimaryColor=Colors.White,SecondaryColor=Color.FromRgb(85,145,215),
            BackgroundColor=Color.FromRgb(27,29,32),SecondaryBackgroundColor=Color.FromRgb(55,58,64),
            TertiaryBackgroundColor=Color.FromRgb(27,29,32),BorderColor=Color.FromRgb(70,73,80),
            ButtonBackgroundColor=Color.FromRgb(50,54,60),ButtonBackgroundSelectedColor=Color.FromRgb(71,81,96),
            ButtonForegroundColor=Colors.White,ButtonForegroundDisabledColor=Colors.Gray}}}};
        foreach(string name in new[]{"Brushes","SVGDictionary","Converters"})
            Application.Current.Resources.MergedDictionaries.Add(new NINA.WPF.Base.Utility.SharedResourceDictionary{
                Source=new Uri($"/NINA.WPF.Base;component/Resources/StaticResources/{name}.xaml",UriKind.Relative)});
        var imageSettings=Fake.Of<IImageSettings>((m,a)=>m.Name switch{
            "get_AutoStretchFactor"=>.2,"get_BlackClipping"=>-2.8,"get_StarSensitivity"=>StarSensitivityEnum.Normal,_=>Default(m)});
        var focusSettings=Fake.Of<IFocuserSettings>((m,a)=>m.Name switch{
            "get_AutoFocusInnerCropRatio" or "get_AutoFocusOuterCropRatio"=>1.0,_=>Default(m)});
        var astro=Fake.Properties<IAstrometrySettings>(new(){["Latitude"]=35.0,["Longitude"]=0.0,["Elevation"]=0.0,["Horizon"]=null});
        var telescopeSettings=Fake.Properties<ITelescopeSettings>(new(){["NoSync"]=true});
        var profile=Fake.Of<IProfile>((m,a)=>m.Name switch{
            "get_ImageSettings"=>imageSettings,"get_FocuserSettings"=>focusSettings,"get_AstrometrySettings"=>astro,
            "get_TelescopeSettings"=>telescopeSettings,"get_PluginSettings"=>new NINA.Profile.PluginSettings(),_=>Default(m)});
        var profiles=Fake.Of<IProfileService>((m,a)=>m.Name=="get_ActiveProfile"?profile:Default(m));
        var detector=new StarDetection();
        var detectors=Fake.Of<IPluggableBehaviorSelector<IStarDetection>>((m,a)=>m.Name=="GetBehavior"?detector:Default(m));
        var annotator=Equipment<IStarAnnotator>();
        var annotators=Fake.Of<IPluggableBehaviorSelector<IStarAnnotator>>((m,a)=>m.Name=="GetBehavior"?annotator:Default(m));
        var factory=new ImageDataFactory(profiles,detectors,annotators);
        var cameraInfo=new CameraInfo{DeviceId="FITS preview",Connected=true,CanShowLiveView=true,CanSubSample=true,
            XSize=9576,YSize=6388,ExposureMin=.001,ExposureMax=60};
        var motorInfo=new FocuserInfo{DeviceId="FITS focus position",Connected=true};
        var camera=Equipment<ICameraMediator>((m,a)=>m.Name switch{"GetInfo"=>cameraInfo,"IsFreeToCapture"=>true,_=>Default(m)});
        var motor=Equipment<IFocuserMediator>((m,a)=>m.Name=="GetInfo"?motorInfo:Default(m));
        var telescope=Equipment<ITelescopeMediator>((m,a)=>m.Name=="GetInfo"?new TelescopeInfo():Default(m));
        using var vm=new ManualFocuserDockableVM(profiles,camera,Equipment<IImagingMediator>(),Equipment<IFilterWheelMediator>(),motor,
            telescope,Equipment<IGuiderMediator>((m,a)=>m.Name=="GetInfo"?new GuiderInfo():Default(m)),null,null,null,Equipment<IApplicationStatusMediator>(),
            new NinaMultiStarHfr(profiles,factory,detectors,camera));
        var analyzer=new NinaMultiStarHfr(profiles,factory,detectors,camera);
        var records=new List<(DateTime Time,int Position,double Hfr,int Stars)>();
        IImageData selected=null;
        foreach(string file in Directory.GetFiles(folder,"*.fits").OrderBy(File.GetLastWriteTimeUtc)) {
            var data=await NINA.Image.FileFormat.FITS.FITS.Load(new Uri(file),false,factory,default);
            const int crop=1024;
            var raw=new FocusRawFrame(data.Data.FlatArray,data.Properties.Width,(data.Properties.Width-crop)/2,
                (data.Properties.Height-crop)/2,crop,crop,data);
            var hfr=await analyzer.AnalyzeAsync(raw,default);
            if(data.MetaData.Focuser.Position.HasValue)
                records.Add((data.MetaData.Image.ExposureStart==DateTime.MinValue?File.GetLastWriteTimeUtc(file):data.MetaData.Image.ExposureStart,
                    data.MetaData.Focuser.Position.Value,hfr.Hfr,hfr.Stars));
            Console.WriteLine($"Source {Path.GetFileName(file)}; focus {data.MetaData.Focuser.Position}; HFR {hfr.Hfr:F3}; stars {hfr.Stars}");
            if(selected==null||data.MetaData.Focuser.Position<selected.MetaData.Focuser.Position)selected=data;
        }
        if(selected==null)throw new InvalidOperationException("No FITS captures found.");
        motorInfo.Position=selected.MetaData.Focuser.Position??0;
        vm.UpdateDeviceInfo(cameraInfo);vm.UpdateDeviceInfo(motorInfo);
        vm.PreviewExposureMs=selected.MetaData.Image.ExposureTime*1000;
        await vm.EnsureFocusTargetsLoadedAsync();
        var source=new FocusRawFrame(selected.Data.FlatArray,selected.Properties.Width,0,0,selected.Properties.Width,selected.Properties.Height,selected);
        var render=typeof(ManualFocuserDockableVM).GetMethod("RenderRawFocusPreview",BindingFlags.Instance|BindingFlags.NonPublic);
        var imageProperty=typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.FocusPreviewImage));
        imageProperty.SetValue(vm,await Task.Run(()=>render.Invoke(vm,new object[]{source,null,null})));
        var imageView=new ManualFocuserDockableView{DataContext=vm};
        var controlView=new FocusControlsView{DataContext=vm};
        ((Expander)controlView.FindName("SetupExpander")).IsExpanded=true;
        var historyView=new FocusHistoryPanelView{DataContext=new FocusHistoryDockableVM(profiles,vm)};
        var root=new Grid{Background=(Brush)Application.Current.Resources["BackgroundBrush"]};
        root.ColumnDefinitions.Add(new ColumnDefinition());root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(390)});
        root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(0)});
        Grid.SetColumn(controlView,1);Grid.SetRowSpan(controlView,2);Grid.SetRow(historyView,1);
        root.Children.Add(Panel("MF Image",imageView));root.Children.Add(Panel("MF Controls",controlView,1,2));
        root.Children.Add(Panel("MF HFR",historyView,0,1,1));
        async Task Save(string name,string controlsName=null) {
            root.Measure(new Size(1725,1050));root.Arrange(new Rect(0,0,1725,1050));root.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);root.UpdateLayout();
            var viewer=imageView.FindName("FocusImageView") as NINA.WPF.Base.View.ImageView ??
                ((Grid)imageView.FindName("PreviewCard")).Children.OfType<NINA.WPF.Base.View.ImageView>().Single();
            viewer.GetType().GetMethod("ButtonZoomReset_Click",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(viewer,new object[]{null,new RoutedEventArgs()});
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);root.UpdateLayout();
            var screenshot=new RenderTargetBitmap(1725,1050,96,96,PixelFormats.Pbgra32);screenshot.Render(root);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(screenshot));
            using var stream=File.Create(Path.Combine(output,name));encoder.Save(stream);
            Console.WriteLine("Saved "+Path.Combine(output,name));
            if(controlsName!=null) {
                var controls=(StackPanel)controlView.FindName("ControlsPanel");
                var bottom=controls.TransformToAncestor(root).Transform(new Point(0,controls.DesiredSize.Height)).Y;
                var cropped=new CroppedBitmap(screenshot,new Int32Rect(1335,0,390,Math.Min(1050,(int)Math.Ceiling(bottom+8))));
                var controlEncoder=new PngBitmapEncoder();controlEncoder.Frames.Add(BitmapFrame.Create(cropped));
                using var controlStream=File.Create(Path.Combine(output,controlsName));controlEncoder.Save(controlStream);
                Console.WriteLine("Saved "+Path.Combine(output,controlsName));
            }
        }
        await Save("manual-focuser-live.png","manual-focuser-controls-live.png");
        vm.IsAberrationInspector=true;await Task.Delay(300);
        await Save("manual-focuser-inspector.png");
        vm.IsAberrationInspector=false;vm.FocusMode="Manual";vm.TrackFocusStars=false;vm.ShowFocusHfr=true;
        root.RowDefinitions[1].Height=new GridLength(190);
        var record=typeof(ManualFocuserDockableVM).GetMethod("RecordPositionHfr",BindingFlags.Instance|BindingFlags.NonPublic);
        foreach(var point in records.OrderBy(p=>p.Time)) {
            motorInfo.Position=point.Position;
            record.Invoke(vm,new object[]{motorInfo.DeviceId,(int?)point.Position,true,point.Hfr});
        }
        motorInfo.Position=selected.MetaData.Focuser.Position??0;vm.UpdateDeviceInfo(motorInfo);
        var selectedMetric=records.Single(p=>p.Position==motorInfo.Position);
        typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.LiveHfr)).SetValue(vm,selectedMetric.Hfr);
        typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.LiveHfrStarCount)).SetValue(vm,selectedMetric.Stars);
        typeof(ManualFocuserDockableVM).GetMethod("RaisePropertyChanged",BindingFlags.Instance|BindingFlags.NonPublic)
            .Invoke(vm,new object[]{nameof(vm.LiveHfrText)});
        await Task.Delay(300);await Save("manual-focuser-manual.png","manual-focuser-controls-manual.png");
    }
    private static DockPanel Panel(string title,FrameworkElement view,int column=0,int rowSpan=1,int row=0) {
        var panel=new DockPanel{LastChildFill=true,Margin=new Thickness(2)};
        var label=new TextBlock{Text=title,Foreground=new SolidColorBrush(Color.FromRgb(85,145,215)),FontSize=11,
            Background=new SolidColorBrush(Color.FromRgb(37,40,44)),Padding=new Thickness(5,4,5,4)};
        DockPanel.SetDock(label,Dock.Top);panel.Children.Add(label);panel.Children.Add(view);
        Grid.SetColumn(panel,column);Grid.SetRowSpan(panel,rowSpan);Grid.SetRow(panel,row);return panel;
    }
}
