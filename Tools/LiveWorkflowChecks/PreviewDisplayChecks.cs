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
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

internal static partial class PreviewDisplayChecks {
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
        var telescopeSettings=Fake.Properties<ITelescopeSettings>(new(){["NoSync"]=true});
        var imageSettings=Fake.Of<IImageSettings>((m,a)=>m.Name switch {"get_AutoStretchFactor"=>.2,"get_BlackClipping"=>-2.8,_=>Fake.Unexpected(m)});
        var profile=Fake.Of<IProfile>((n,b)=>n.Name switch {"get_TelescopeSettings"=>telescopeSettings,"get_ImageSettings"=>imageSettings,_=>Fake.Unexpected(n)});
        var profiles=Fake.Of<IProfileService>((m,a)=>m.Name=="get_ActiveProfile"?profile:
            m.Name.StartsWith("add_")||m.Name.StartsWith("remove_")?null:Fake.Unexpected(m));
        var camera=Fake.Of<ICameraMediator>((m,a)=>m.Name switch {"GetInfo"=>new CameraInfo(),"IsFreeToCapture"=>true,"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var motor=Fake.Of<IFocuserMediator>((m,a)=>m.Name switch {"GetInfo"=>new FocuserInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var mount=Fake.Of<ITelescopeMediator>((m,a)=>m.Name switch {"GetInfo"=>new TelescopeInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var guider=Fake.Of<IGuiderMediator>((m,a)=>m.Name switch {"GetInfo"=>new GuiderInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var wheel=Fake.Of<IFilterWheelMediator>((m,a)=>m.Name is "RegisterConsumer" or "RemoveConsumer"?null:Fake.Unexpected(m));
        int imageWrites=0,imageThread=0;BitmapSource mainImage=null;Action<BitmapSource> hostDisplay=null;
        object Display(BitmapSource image) {mainImage=image;imageWrites++;imageThread=Environment.CurrentManagedThreadId;hostDisplay?.Invoke(image);return null;}
        var imaging=Fake.Of<IImagingMediator>((m,a)=>m.Name switch {
            "add_ImagePrepared" or "remove_ImagePrepared"=>null,"SetImage"=>Display((BitmapSource)a[0]),_=>Fake.Unexpected(m)});
        var status=Fake.Of<IApplicationStatusMediator>((m,a)=>m.Name=="StatusUpdate"?null:Fake.Unexpected(m));
        using var vm=new ManualFocuserDockableVM(profiles,camera,imaging,wheel,motor,mount,guider,null,null,null,status);
        vm.FocusMode="Manual";
        LiveHistoryChecks.Run(check);
        await VerifyRawPreview(vm,check);
        await VerifyInspectorDisplay(vm,()=>mainImage,check);
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
        int stretchThread=0;
        void StretchChanged(object sender,System.ComponentModel.PropertyChangedEventArgs e) {
            if(e.PropertyName==nameof(vm.FocusPreviewImage)) stretchThread=Environment.CurrentManagedThreadId;
        }
        vm.PropertyChanged+=StretchChanged;
        vm.PreviewStretchStrength=.5;await Task.Delay(250);
        await Task.Run(()=>type.GetProperty(nameof(vm.FocusPreviewImage)).SetValue(vm,preview));
        await Task.Delay(250);
        check(stretchThread==Environment.CurrentManagedThreadId && !ReferenceEquals(vm.FocusPreviewImage,preview),
            "A background frame with outdated stretch rerenders and notifies viewers on the UI thread");
        vm.PropertyChanged-=StretchChanged;
        vm.ResetPreviewStretchCommand.Execute(null);await Task.Delay(250);
        type.GetField("overviewImage",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(vm,vm.FocusPreviewImage);
        type.GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,true);
        var full=vm.LiveDisplayImage;vm.PreviewStretchStrength=.5;await Task.Delay(250);
        check(!ReferenceEquals(full,vm.LiveDisplayImage) && vm.IsSelectingRoi,
            "Full-frame ROI selection rerenders without recapture or leaving mouse-selection mode");
        type.GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,false);
        vm.ResetPreviewStretchCommand.Execute(null);await Task.Delay(250);
        // The private workspace layout is exercised by --targets; the former
        // standalone/main-image layout assertions no longer describe this panel.
        // Loaded fixtures below use an existing synthetic target. Target search
        // has its own harness and must not read a real observer profile here.
        type.GetField("initialFocusTargetsRequested",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(vm,true);
        vm.ShowInNinaImage = true;
        check(!vm.ShowInNinaImage && !(bool)typeof(ManualFocuserDockableVM).GetProperty("CanPublishNinaHfrHistory", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vm),
            "Private live workspace cannot take over native image or HFR history");
        check(imageWrites == 0, "Private preview leaves the native image and backing data untouched");
    }

}
