using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

internal static class Program {
    [STAThread]
    public static void Main() {
        var config=JsonSerializer.Deserialize<Configuration>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"test-config.json")));
        AssemblyLoadContext.Default.Resolving+=(_,name)=> {
            string file=Path.Combine(config.Host,name.Name+".dll");
            return File.Exists(file)?AssemblyLoadContext.Default.LoadFromAssemblyPath(file):null;
        };
        try { Run(config); }
        catch(Exception error) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"startup-error.txt"),error.ToString()); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(Configuration config) {
        var application=new Application();
        application.DispatcherUnhandledException+=(_,e)=> {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"startup-error.txt"),e.Exception+Environment.NewLine);
            e.Handled=true;
        };
        application.Resources["ProfileService"]=new{ActiveProfile=new{ColorSchemaSettings=new{ColorSchema=new{
            PrimaryColor=Colors.White,SecondaryColor=Color.FromRgb(85,145,215),BackgroundColor=Color.FromRgb(27,29,32),
            SecondaryBackgroundColor=Color.FromRgb(55,58,64),TertiaryBackgroundColor=Color.FromRgb(27,29,32),BorderColor=Colors.Gray,
            ButtonBackgroundColor=Color.FromRgb(50,54,60),ButtonBackgroundSelectedColor=Color.FromRgb(71,81,96),ButtonForegroundColor=Colors.White,
            ButtonForegroundDisabledColor=Colors.Gray}}}};
        foreach(string name in new[]{"SVGDictionary","Brushes","Converters"})
            application.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri($"/NINA.WPF.Base;component/Resources/StaticResources/{name}.xaml",UriKind.Relative)});
        foreach(string name in new[]{"Button","ToggleButton","Slider","TextBox","ComboBox","ScrollViewer","CancellableButton","StepperControl"})
            application.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri($"/NINA.WPF.Base;component/Resources/Styles/{name}.xaml",UriKind.Relative)});
        application.Resources["zero"]=0d;
        var images=Proxy.Of<IImageSettings>(new(){["AutoStretch"]=false,["DetectStars"]=false,["AutoStretchFactor"]=.2,["BlackClipping"]=-2.8,["StarSensitivity"]=StarSensitivityEnum.Normal});
        var focuser=Proxy.Of<IFocuserSettings>(new(){["AutoFocusInnerCropRatio"]=1d,["AutoFocusOuterCropRatio"]=1d});
        var profile=Proxy.Of<IProfile>(new(){["ImageSettings"]=images,["FocuserSettings"]=focuser,
            ["PluginSettings"]=new NINA.Profile.PluginSettings(),["TelescopeSettings"]=Proxy.Of<ITelescopeSettings>(new(){["NoSync"]=true}),
            ["CameraSettings"]=Proxy.Of<ICameraSettings>(new(){["BitDepth"]=16d}),
            ["AstrometrySettings"]=Proxy.Of<IAstrometrySettings>(new(){["Latitude"]=35d,["Longitude"]=0d,["Elevation"]=0d})});
        var profiles=Proxy.Of<IProfileService>(new(){["ActiveProfile"]=profile});
        var camera=Proxy.Of<ICameraMediator>();var telescope=Proxy.Of<ITelescopeMediator>();
        var imaging=Proxy.Of<IImagingMediator>();var status=Proxy.Of<IApplicationStatusMediator>();
        var starDetector=new StarDetection();
        var detectorSelector=Proxy.Of<IPluggableBehaviorSelector<IStarDetection>>(handler:(m,a)=>m.Name=="GetBehavior"?starDetector:Proxy.Default(m));
        var annotationSelector=Proxy.Of<IPluggableBehaviorSelector<IStarAnnotator>>(handler:(m,a)=>m.Name=="GetBehavior"?Proxy.Of<IStarAnnotator>():Proxy.Default(m));
        var factory=new ImageDataFactory(profiles,detectorSelector,annotationSelector);
        var hostAssembly=Assembly.Load("NINA");
        var hostType=hostAssembly.GetType("NINA.ViewModel.ImageControlVM",true);
        var host=Activator.CreateInstance(hostType,profiles,camera,telescope,imaging,status);
        var thumbnails=hostAssembly.GetType("NINA.ViewModel.ThumbnailVM",true);
        _=Activator.CreateInstance(thumbnails,profiles,imaging,Proxy.Of<IImageSaveMediator>(),factory);
        object session=null;Assembly plugin=null;
        if(config.Plugin!=null) {
            plugin=AssemblyLoadContext.Default.LoadFromAssemblyPath(config.Plugin);
            var vmType=plugin.GetType("Cwseo.NINA.ManualFocuser.Dockables.ManualFocuserDockableVM",true);
            var services=new Dictionary<Type,object>{{typeof(IProfileService),profiles},{typeof(ICameraMediator),camera},
                {typeof(ITelescopeMediator),telescope},{typeof(IImagingMediator),imaging},{typeof(IApplicationStatusMediator),status}};
            var constructor=vmType.GetConstructors().Single(c=>c.CustomAttributes.Any(a=>a.AttributeType.Name=="ImportingConstructorAttribute"));
            session=constructor.Invoke(constructor.GetParameters().Select(p=>services.TryGetValue(p.ParameterType,out var value)?value:
                p.ParameterType.IsInterface?Proxy.Create(p.ParameterType):null).ToArray());
        }
        var nativeView=(FrameworkElement)Activator.CreateInstance(hostAssembly.GetType("NINA.View.ImageControlView",true));
        nativeView.DataContext=host;
        FrameworkElement mfView=plugin==null?new TextBlock{Text="No plugin"}:(FrameworkElement)Activator.CreateInstance(plugin.GetType("Cwseo.NINA.ManualFocuser.Dockables.ManualFocuserDockableView",true));
        mfView.DataContext=session;
        var pane=new LayoutAnchorablePane();
        var imageTab=new LayoutAnchorable{Title="Image",Content=nativeView,CanClose=false};
        var mfTab=new LayoutAnchorable{Title="MF Image",Content=mfView,CanClose=false};
        pane.Children.Add(imageTab);pane.Children.Add(mfTab);imageTab.IsSelected=true;
        var manager=new DockingManager{Layout=new LayoutRoot{RootPanel=new LayoutPanel(pane)}};
        var label=new TextBlock{Foreground=Brushes.White,FontSize=22,Margin=new Thickness(10),Text="Ready"};
        var buttons=new StackPanel{Orientation=Orientation.Horizontal};
        var root=new DockPanel{Background=new SolidColorBrush(Color.FromRgb(27,29,32))};
        DockPanel.SetDock(label,Dock.Top);root.Children.Add(label);
        DockPanel.SetDock(buttons,Dock.Top);root.Children.Add(buttons);root.Children.Add(manager);
        var window=new Window{Title="MF Issue 2 | "+config.Name+" | NINA 3.3.0.1067",Width=1300,Height=850,Left=40,Top=40,Content=root};
        int frame=0;bool prepareBusy=false;bool nativeProcessing=false;string runId=Guid.NewGuid().ToString("N");
        void Log(object state)=>File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"frames.jsonl"),JsonSerializer.Serialize(state)+Environment.NewLine);
        async Task Next() {
            if(prepareBusy)return;prepareBusy=true;
            try {
                int number=++frame;var pixels=FramePixels(number);
                var data=factory.CreateBaseImageData(pixels,640,480,16,false,new ImageMetaData());
                var options=new PrepareImageParameters(nativeProcessing,nativeProcessing);
                var rendered=await (Task<IRenderedImage>)hostType.GetMethod("PrepareImage").Invoke(host,new object[]{data,options,CancellationToken.None});
                if(plugin!=null) {
                    var rawType=plugin.GetType("Cwseo.NINA.ManualFocuser.LiveWorkflow.Models.FocusRawFrame",true);
                    var raw=Activator.CreateInstance(rawType,pixels,640,0,0,640,480,data,false);
                    var preview=session.GetType().GetMethod("RenderRawFocusPreview",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(session,new object[]{raw,null,null});
                    session.GetType().GetProperty("FocusPreviewImage").SetValue(session,preview);
                }
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                label.Text=$"{config.Name} | expected image number {number:D2} | native stretch/detection: {nativeProcessing}";
                Log(new{Run=runId,Frame=number,Host=hostAssembly.GetName().Version.ToString(),Plugin=plugin?.GetName().Version.ToString(),
                    Runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    NativeProcessing=nativeProcessing,RawMatches=ReferenceEquals(rendered.RawImageData,data),
                    DisplayMatches=ReferenceEquals(hostType.GetProperty("Image").GetValue(host),rendered.Image),Utc=DateTime.UtcNow});
            } catch(Exception error) { label.Text=error.Message;Log(new{Error=error.ToString()}); }
            finally {prepareBusy=false;}
        }
        void Button(string text,Action action) { var button=new Button{Content=text,Padding=new Thickness(14,8,14,8),Margin=new Thickness(5)};button.Click+=(_,_)=>action();buttons.Children.Add(button); }
        Button("Next frame",async()=>await Next());
        Button("Diagnostic snapshot",()=> {
            if(plugin==null)return;
            var diagnostic=plugin.GetType("Cwseo.NINA.ManualFocuser.Dockables.FocusViewerDiagnostics",true);
            var table=diagnostic.GetField("observers",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            foreach(var pair in (System.Collections.IEnumerable)table) {
                var observer=pair.GetType().GetProperty("Value").GetValue(pair);
                diagnostic.GetField("lastSnapshot",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(observer,DateTime.MinValue);
                diagnostic.GetMethod("Queue",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(observer,new object[]{"issue-2-explicit-snapshot",true});
                diagnostic.GetMethod("Capture",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(observer,null);
                Log(new{Run=runId,Event="snapshot-result",Frame=frame,
                    ViewerContext=pair.GetType().GetProperty("Key").GetValue(pair) is FrameworkElement element?element.DataContext?.GetType().Name:null,
                    Captured= (DateTime)diagnostic.GetField("lastSnapshot",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(observer)!=DateTime.MinValue});
            }
            Log(new{Event="diagnostic-snapshot",Frame=frame});
        });
        Button("Switch MF / Image",async()=>{mfTab.IsSelected=true;await Task.Delay(300);imageTab.IsSelected=true;Log(new{Event="tab-roundtrip",Frame=frame});});
        Button("Toggle native processing",()=>{nativeProcessing=!nativeProcessing;label.Text="Native processing set to "+nativeProcessing+"; prepare next frame";});
        window.Loaded+=async(_,_)=>await Next();
        window.Closed+=(_,_)=>{(session as IDisposable)?.Dispose();};
        application.Run(window);
    }
    private static ushort[] FramePixels(int number) {
        const int w=640,h=480;var pixels=Enumerable.Repeat((ushort)1500,w*h).ToArray();
        void Rect(int x,int y,int width,int height) { for(int row=y;row<y+height;row++)for(int col=x;col<x+width;col++)pixels[row*w+col]=60000; }
        int[] digits={0b0111111,0b0000110,0b1011011,0b1001111,0b1100110,0b1101101,0b1111101,0b0000111,0b1111111,0b1101111};
        void Digit(int value,int x) {
            var segments=new[]{(x+12,80,96,12),(x+108,92,12,112),(x+108,216,12,112),(x+12,328,96,12),(x,216,12,112),(x,92,12,112),(x+12,204,96,12)};
            for(int i=0;i<7;i++)if((digits[value]&(1<<i))!=0){var s=segments[i];Rect(s.Item1,s.Item2,s.Item3,s.Item4);}
        }
        Digit(number/10%10,170);Digit(number%10,350);
        Rect(30+(number%20)*28,400,16,40);
        return pixels;
    }
    private sealed record Configuration(string Name,string Host,string Plugin);
}
public class Proxy:DispatchProxy {
    public Dictionary<string,object> Values=new();public Func<MethodInfo,object[],object> Handler;
    protected override object Invoke(MethodInfo method,object[] args) {
        if(method.Name.StartsWith("get_")&&Values.TryGetValue(method.Name[4..],out var value))return value;
        if(method.Name.StartsWith("set_")){Values[method.Name[4..]]=args[0];return null;}
        return Handler?.Invoke(method,args)??Default(method);
    }
    public static object Default(MethodInfo method) {
        if(method.Name=="GetInfo" && method.ReturnType.GetConstructor(Type.EmptyTypes)!=null)return Activator.CreateInstance(method.ReturnType);
        return method.ReturnType!=typeof(void)&&method.ReturnType.IsValueType?Activator.CreateInstance(method.ReturnType):null;
    }
    public static object Create(Type contract) => DispatchProxy.Create(contract,typeof(Proxy));
    public static T Of<T>(Dictionary<string,object> values=null,Func<MethodInfo,object[],object> handler=null) where T:class {
        var result=DispatchProxy.Create<T,Proxy>();((Proxy)(object)result).Values=values??new();((Proxy)(object)result).Handler=handler;return result;
    }
}
