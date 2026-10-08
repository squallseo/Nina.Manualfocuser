using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.Models;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

internal static class PreviewDisplayChecks {
    public static void Run(Action<bool,string> check) {
        Exception failure=null;
        var thread=new Thread(()=>{
            _ = new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () => {
                try {await Verify(check);} catch(Exception e){failure=e;}
                finally {dispatcher.InvokeShutdown();}
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)throw new Exception("Preview display checks failed",failure);
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject parent) {
        foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) {
            yield return child;foreach(var nested in Children(child))yield return nested;
        }
    }
    private static async Task Verify(Action<bool,string> check) {
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"Database","Migration"));
        var profiles=Fake.Of<IProfileService>((m,a)=>m.Name=="get_ActiveProfile"?Fake.Of<IProfile>((n,b)=>Fake.Unexpected(n)):
            m.Name.StartsWith("add_")||m.Name.StartsWith("remove_")?null:Fake.Unexpected(m));
        var camera=Fake.Of<ICameraMediator>((m,a)=>m.Name switch {"GetInfo"=>new CameraInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var motor=Fake.Of<IFocuserMediator>((m,a)=>m.Name switch {"GetInfo"=>new FocuserInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var mount=Fake.Of<ITelescopeMediator>((m,a)=>m.Name switch {"GetInfo"=>new TelescopeInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var guider=Fake.Of<IGuiderMediator>((m,a)=>m.Name switch {"GetInfo"=>new GuiderInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var wheel=Fake.Of<IFilterWheelMediator>((m,a)=>m.Name is "RegisterConsumer" or "RemoveConsumer"?null:Fake.Unexpected(m));
        var imaging=Fake.Of<IImagingMediator>((m,a)=>m.Name is "add_ImagePrepared" or "remove_ImagePrepared"?null:Fake.Unexpected(m));
        var status=Fake.Of<IApplicationStatusMediator>((m,a)=>m.Name=="StatusUpdate"?null:Fake.Unexpected(m));
        using var vm=new ManualFocuserDockableVM(profiles,camera,imaging,wheel,motor,mount,guider,null,null,null,null,null,status);
        const int n=256;var random=new Random(9123);double angle=37*Math.PI/180;
        var raw=Enumerable.Range(0,n*n).Select(i=>{
            double x=i%n-128,y=i/n-128,s=x*Math.Cos(angle)+y*Math.Sin(angle),u=-x*Math.Sin(angle)+y*Math.Cos(angle);
            return 300+random.NextDouble()*20-10+45000*Math.Exp(-(x*x+y*y)/8)+3600*Math.Exp(-u*u/4.5)*Math.Exp(-s*s/9800);
        }).ToArray();
        var copy=(double[])raw.Clone();
        var dark=FocusDisplayStretch.Render(raw,.25);var normal=FocusDisplayStretch.Render(raw);var bright=FocusDisplayStretch.Render(raw,2.5);
        check(dark.Zip(normal).All(p=>p.First<=p.Second) && normal.Zip(bright).All(p=>p.First<=p.Second) && dark.Sum(v=>(long)v)<bright.Sum(v=>(long)v),
            "Stretch slider monotonically darkens/brightens the display");
        check(raw.SequenceEqual(copy) && FocusDisplayStretch.Render(raw,double.NaN).SequenceEqual(normal),
            "Stretch leaves raw measurements unchanged and invalid strength uses the automatic default");
        var type=typeof(ManualFocuserDockableVM);
        var detect=type.GetMethod("AnalyzePreviewSpikes",BindingFlags.NonPublic|BindingFlags.Instance);
        var publish=type.GetMethod("PublishPreviewSpikeAngle",BindingFlags.NonPublic|BindingFlags.Instance);
        var spikes=(SpikeFrameResult)detect.Invoke(vm,new object[]{raw,n,n});
        publish.Invoke(vm,new object[]{spikes});
        check(vm.HasSpikeAngle && Math.Abs(spikes.MeasuredAngleDeg-37)<4 && vm.MeasuredAngleText.Contains("Detected:"),
            "Actual live preview detector publishes the synthetic diffraction angle");
        check(vm.UseMeasuredAngleCommand.CanExecute(null),"Detected preview angle enables Use angle");
        publish.Invoke(vm,new object[]{detect.Invoke(vm,new object[]{Enumerable.Repeat(300.0,n*n).ToArray(),n,n})});
        check(!vm.HasSpikeAngle && vm.MeasuredAngleText=="Not detected" && !vm.UseMeasuredAngleCommand.CanExecute(null),
            "A missing spike clears the previous angle and disables Use angle");
        publish.Invoke(vm,new object[]{spikes});vm.PreviewCenterX=60;
        check(!vm.HasSpikeAngle,"Changing ROI invalidates an old detected angle");
        var render=type.GetMethod("RenderFocusPreview",BindingFlags.NonPublic|BindingFlags.Instance);
        var preview=(ImageSource)render.Invoke(vm,new object[]{raw,n,n,null,null});
        type.GetProperty(nameof(vm.FocusPreviewImage)).SetValue(vm,preview);
        vm.PreviewStretchStrength=.5;
        await Task.Delay(250);
        check(!ReferenceEquals(vm.FocusPreviewImage,preview) && vm.FocusPreviewImage.IsFrozen && raw.SequenceEqual(copy),
            "Idle slider rerenders the cached raw image without any camera or motor call");
        vm.PreviewStretchStrength=.75;vm.PreviewStretchStrength=2;
        await Task.Delay(250);
        check(vm.PreviewStretchStrength==2 && vm.PreviewStretchText.Contains("2.00"),"Rapid slider changes settle at the latest strength");
        vm.ResetPreviewStretchCommand.Execute(null);await Task.Delay(250);
        check(vm.PreviewStretchStrength==1,"Reset restores automatic stretch strength");
        type.GetField("overviewImage",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(vm,vm.FocusPreviewImage);
        type.GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,true);
        var full=vm.LiveDisplayImage;vm.PreviewStretchStrength=.5;await Task.Delay(250);
        check(!ReferenceEquals(full,vm.LiveDisplayImage) && vm.IsSelectingRoi,
            "Full-frame ROI selection rerenders without recapture or leaving mouse-selection mode");
        type.GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,false);
        vm.ResetPreviewStretchCommand.Execute(null);await Task.Delay(250);
        RenderControls(vm,check);
    }
    private static void RenderControls(ManualFocuserDockableVM vm,Action<bool,string> check) {
        XNamespace wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation",x="http://schemas.microsoft.com/winfx/2006/xaml";
        var source=XDocument.Load("Dockables/ManualFocuserDockableView.xaml");
        var section=new XElement(source.Descendants(wpf+"Grid").Single(e=>(string)e.Attribute(x+"Name")=="PreviewSurface").Parent);
        foreach(var attribute in section.Descendants().Attributes().Where(a=>a.Name.LocalName.StartsWith("Mouse")||a.Name.LocalName is "LostMouseCapture" or "SizeChanged").ToArray())attribute.Remove();
        string markup="<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Background=\"#20252D\" Padding=\"8\"><Border.Resources>"+
            "<SolidColorBrush x:Key=\"FocusRoiBrush\" Color=\"#FFFFD54F\"/><Style TargetType=\"TextBlock\"><Setter Property=\"Foreground\" Value=\"#E8EDF4\"/></Style></Border.Resources>"+section+"</Border>";
        var view=(FrameworkElement)XamlReader.Parse(markup);view.DataContext=vm;
        foreach(int width in new[]{240,420}) {
            view.Measure(new Size(width,330));view.Arrange(new Rect(0,0,width,330));view.UpdateLayout();
            view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);
            var slider=Children(view).OfType<Slider>().Single();
            check(slider.Value==vm.PreviewStretchStrength,"Actual image-footer slider binds to preview strength");
            var surface=(FrameworkElement)view.FindName("PreviewSurface");
            var footer=(FrameworkElement)view.FindName("PreviewStretchControls");
            check(slider.ActualWidth>60 && footer.TranslatePoint(new Point(),view).Y>=surface.TranslatePoint(new Point(),view).Y+surface.ActualHeight,
                $"At {width}px width the bound stretch slider fits below the ROI image surface");
            var bitmap=new RenderTargetBitmap(width,330,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create($"bin/preview-stretch-{width}.png"))encoder.Save(file);
        }
    }
}
