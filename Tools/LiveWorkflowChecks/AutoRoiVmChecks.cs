using System.IO;
using System.Reflection;
using Cwseo.NINA.ManualFocuser.Dockables;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

internal static class AutoRoiVmChecks {
    public static void Run(Action<bool,string> check) {
        Exception failure=null;
        var thread=new Thread(()=>{try{RunAsync(check).GetAwaiter().GetResult();}catch(Exception e){failure=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)throw new Exception("Explicit ROI integration failed",failure);
    }
    private static async Task RunAsync(Action<bool,string> check) {
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"Database","Migration"));
        var profile=Fake.Of<IProfile>((m,a)=>m.Name=="get_TelescopeSettings"?Fake.Properties<ITelescopeSettings>(new(){["NoSync"]=true}):Fake.Unexpected(m));
        var profiles=Fake.Of<IProfileService>((m,a)=>m.Name=="get_ActiveProfile"?profile:
            m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")?null:Fake.Unexpected(m));
        var cameraInfo=new CameraInfo{Connected=true,DeviceId="ExplicitROI.Test",CanSubSample=true,XSize=4096,YSize=3072,
            ExposureMin=.001,ExposureMax=60,BinX=1,BinY=1};
        var focuserInfo=new FocuserInfo{Connected=true,Position=20000};
        int captures=0,moves=0,releases=0,frameWidth=1024,frameHeight=1024;
        bool blank=false,cancelCapture=false,denyReservation=false,changeCamera=false;
        object owner=null;ManualFocuserDockableVM vm=null;
        var sequences=new List<CaptureSequence>();ushort[] pixels=null;
        var array=Fake.Of<IImageArray>((m,a)=>m.Name=="get_FlatArray"?pixels:Fake.Unexpected(m));
        var image=Fake.Of<IImageData>((m,a)=>m.Name switch{
            "get_Properties"=>new ImageProperties(frameWidth,frameHeight,16,false,1,1),"get_Data"=>array,_=>Fake.Unexpected(m)});
        var exposure=Fake.Of<IExposureData>((m,a)=>m.Name=="ToImageData"?Task.FromResult(image):Fake.Unexpected(m));
        var camera=Fake.Of<ICameraMediator>((m,a)=>m.Name switch{
            "GetInfo"=>cameraInfo,"IsFreeToCapture"=>owner==null || owner==a[0],
            "RegisterCaptureBlock"=>Block(a[0]),"ReleaseCaptureBlock"=>Release(a[0]),
            "RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        object Block(object value){if(denyReservation)throw new InvalidOperationException("reservation rejected");check(owner==null,"ROI operation owns one camera reservation");owner=value;return null;}
        object Release(object value){check(owner==value,"ROI cleanup releases its own capture reservation");owner=null;releases++;return null;}
        var imaging=Fake.Of<IImagingMediator>((m,a)=>m.Name switch{
            "add_ImagePrepared" or "remove_ImagePrepared"=>null,"CaptureImage"=>Capture((CaptureSequence)a[0]),_=>Fake.Unexpected(m)});
        Task<IExposureData> Capture(CaptureSequence sequence) {
            check(owner==vm && !vm.AutoRoiCommand.CanExecute(null),"Explicit selection prevent overlapping ROI capture");
            captures++;sequences.Add(sequence);
            frameWidth=(int)sequence.SubSambleRectangle.Width;frameHeight=(int)sequence.SubSambleRectangle.Height;
            pixels=new ushort[frameWidth*frameHeight];
            if(!blank)for(int y=0;y<frameHeight;y++)for(int x=0;x<frameWidth;x++) {
                double dx=x-620,dy=y-530;
                pixels[y*frameWidth+x]=(ushort)(300+3*Math.Sin(x+y)+40000*Math.Exp(-(dx*dx+dy*dy)/18));
            }
            if(cancelCapture)vm.StopFocusPreviewCommand.Execute(null);
            if(changeCamera)cameraInfo.DeviceId="Changed.Camera";
            return Task.FromResult<IExposureData>(exposure);
        }
        var focuser=Fake.Of<IFocuserMediator>((m,a)=>m.Name switch{
            "GetInfo"=>focuserInfo,"GetDevice"=>null,"RegisterConsumer" or "RemoveConsumer"=>null,
            "MoveFocuser" or "MoveFocuserRelative"=>Motor(),_=>Fake.Unexpected(m)});
        Task<int> Motor(){moves++;throw new Exception("Unexpected motor command during ROI checks");}
        var mount=Fake.Of<ITelescopeMediator>((m,a)=>m.Name switch{
            "GetInfo"=>new TelescopeInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var guider=Fake.Of<IGuiderMediator>((m,a)=>m.Name switch{
            "GetInfo"=>new GuiderInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var wheel=Fake.Of<IFilterWheelMediator>((m,a)=>m.Name is "RegisterConsumer" or "RemoveConsumer"?null:Fake.Unexpected(m));
        var status=Fake.Of<IApplicationStatusMediator>((m,a)=>m.Name=="StatusUpdate"?null:Fake.Unexpected(m));
        var settingsType=typeof(ManualFocuserDockableVM).Assembly.GetType("Cwseo.NINA.ManualFocuser.Properties.Settings");
        var settings=settingsType.GetProperty("Default").GetValue(null);var streaming=settingsType.GetProperty("UseFocusStreaming");
        object previousStreaming=streaming.GetValue(settings);streaming.SetValue(settings,false);
        try {
            using(vm=new ManualFocuserDockableVM(profiles,camera,imaging,wheel,focuser,mount,guider,null,null,null,status,new CheckHfrAnalyzer())) {
                typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.CameraInfo)).SetValue(vm,cameraInfo);
                typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.FocuserInfo)).SetValue(vm,focuserInfo);
                Task<int> Invoke(string name,params object[] args)=>(Task<int>)typeof(ManualFocuserDockableVM).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(vm,args);
                async Task<bool> Reject(string name,params object[] args){try{await Invoke(name,args);return false;}catch(InvalidOperationException){return true;}}
                vm.PreviewCenterX=25;vm.PreviewCenterY=75;
                vm.FocusMode="Live";
                vm.IsAberrationInspector=true;
                check(await Invoke("SelectAutoRoiAsync")==1 && captures==1 && moves==0,"Auto ROI button selects from one scout exposure without starting focus/motor motion");
                var selected=vm.PreviewRoiRectangle;
                check(!vm.IsAberrationInspector && vm.CanUseBahtinovOverlay,"Successful Auto ROI leaves Inspector and restores single-star ROI controls");
                check(selected.Width>=128 && selected.Height>=128 && Math.Abs(selected.X+selected.Width/2.0-1132)<=2 && Math.Abs(selected.Y+selected.Height/2.0-2322)<=2,
                    "Automatic local selection applies the actual sensor coordinates and shared crop size");
                check(vm.FocusPreviewImage!=null && !vm.IsSelectingRoi && owner==null && vm.CanConfigureLive,
                    "Explicit ROI selection displays its crop and restores idle controls");
                blank=true;cancelCapture=true;
                check(await Invoke("RunFocusPreviewAsync")==0 && vm.PreviewRoiRectangle==selected,"Live focus starts with the selected area and never reselects it");
                cancelCapture=false;
                check(await Reject("SelectAutoRoiAsync") && vm.PreviewRoiRectangle==selected && owner==null,"Missing star leaves the previous ROI untouched");
                blank=false;vm.AnalyzeBahtinov=true;
                check(await Reject("SelectAutoRoiAsync") && vm.PreviewRoiRectangle==selected,"Live Bahtinov overlay uses the same mask-aware ROI button");
                vm.AnalyzeBahtinov=false;changeCamera=true;
                check(await Reject("SelectAutoRoiAsync") && vm.PreviewRoiRectangle==selected && owner==null,
                    "A changed camera cannot apply the previous device's scout coordinates");
                changeCamera=false;cameraInfo.DeviceId="ExplicitROI.Test";
                vm.AnalyzeBahtinov=false;cancelCapture=true;
                bool canceled=false;try{await Invoke("SelectAutoRoiAsync");}catch(OperationCanceledException){canceled=true;}
                check(canceled && vm.PreviewRoiRectangle==selected && owner==null,"Stop during auto selection keeps the prior ROI and releases reservation");
                cancelCapture=false;denyReservation=true;int beforeCaptures=captures,beforeReleases=releases;
                check(await Reject("SelectAutoRoiAsync") && captures==beforeCaptures && releases==beforeReleases && !vm.IsFocusAssistRunning,
                    "Reservation rejection cannot capture, release another owner or leave selection running");
            }
        } finally {streaming.SetValue(settings,previousStreaming);}
    }
}
