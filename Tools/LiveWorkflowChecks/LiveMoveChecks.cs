using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
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

internal static class LiveMoveChecks {
    private static TaskCompletionSource<T> Signal<T>()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static void Run(Action<bool,string> check,bool nativeAsi=false,bool inspector=false) {
        Exception failure=null;
        var thread=new Thread(()=> {try{RunAsync(check,nativeAsi,inspector).GetAwaiter().GetResult();}catch(Exception e){failure=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)throw new Exception("Live movement integration failed",failure);
    }
    private static async Task RunAsync(Action<bool,string> check,bool nativeAsi,bool inspector) {
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"Database","Migration"));
        var profile=Fake.Of<IProfile>((m,a)=>m.Name=="get_TelescopeSettings"?Fake.Properties<ITelescopeSettings>(new(){["NoSync"]=true}):Fake.Unexpected(m));
        var profiles=Fake.Of<IProfileService>((m,a)=>m.Name=="get_ActiveProfile"?profile:
            m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")?null:Fake.Unexpected(m));
        var cameraInfo=new CameraInfo{Connected=true,DeviceId=nativeAsi?"ZWOptical_ASI2600MM Pro_test":"Test.LiveView",CanShowLiveView=!nativeAsi,CanSubSample=true,
            XSize=128,YSize=128,ExposureMin=.001,ExposureMax=60,BinX=1,BinY=1};
        var focuserInfo=new FocuserInfo{Connected=true,Position=10000};
        var firstFrame=Signal<bool>();var movingFrames=Signal<bool>();var resumedFrame=Signal<bool>();
        var moveStarted=Signal<bool>();var moveCanceled=Signal<bool>();var motorFinished=Signal<int>();
        var cameraClosing=Signal<bool>();var cameraFinished=Signal<bool>();
        int starts=0,closes=0,frames=0,duringMove=0,releases=0,moves=0,imageWrites=0,movingImageWrites=0;
        object owner=null; ManualFocuserDockableVM vm=null;
        var pixels=Enumerable.Range(0,128*128).Select(i=> {
            double x=i%128-64,y=i/128-64;
            return (ushort)(300+40000*Math.Exp(-(x*x+y*y)/18));
        }).ToArray();
        var array=Fake.Of<IImageArray>((m,a)=>m.Name=="get_FlatArray"?pixels:Fake.Unexpected(m));
        var image=Fake.Of<IImageData>((m,a)=>m.Name switch{
            "get_Properties"=>new ImageProperties(128,128,16,false,1,1),"get_Data"=>array,_=>Fake.Unexpected(m)});
        var exposure=Fake.Of<IExposureData>((m,a)=>m.Name=="ToImageData"?Task.FromResult(image):Fake.Unexpected(m));
        async IAsyncEnumerable<IExposureData> HostStream([EnumeratorCancellation] CancellationToken token) {
            try {while(true){await Task.Delay(20,token);yield return exposure;}}
            finally {Interlocked.Increment(ref closes);cameraClosing.TrySetResult(true);await cameraFinished.Task;}
        }
        var nativeCamera=Fake.Of<NINA.Equipment.Interfaces.ICamera>((m,a)=>m.Name switch {
            "get_Id"=>cameraInfo.DeviceId,"get_Connected"=>cameraInfo.Connected,_=>Fake.Unexpected(m)});
        var camera=Fake.Of<ICameraMediator>((m,a)=>m.Name switch {
            "GetDevice"=>nativeCamera,
            "GetInfo"=>cameraInfo,"IsFreeToCapture"=>owner==null || owner==a[0],
            "RegisterCaptureBlock"=>Block(a[0]),"ReleaseCaptureBlock"=>Release(a[0]),
            "LiveView"=>Stream((CaptureSequence)a[0],(CancellationToken)a[1]),
            "SetReadoutMode" or "SetBinning" or "SetSubSambleRectangle" or "RegisterConsumer" or "RemoveConsumer"=>null,
            _=>Fake.Unexpected(m)});
        object Block(object value){check(owner==null,"Live preview holds a single camera reservation");owner=value;return null;}
        object Release(object value){check(owner==value,"Cleanup releases only its own reservation");owner=null;releases++;return null;}
        IAsyncEnumerable<IExposureData> Stream(CaptureSequence sequence,CancellationToken token){
            if(inspector)check(sequence.SubSambleRectangle.X==0 && sequence.SubSambleRectangle.Y==0 && sequence.SubSambleRectangle.Width==128 && sequence.SubSambleRectangle.Height==128,
                "Inspector acquires the entire sensor instead of its saved single-star ROI");
            starts++;return HostStream(token);
        }
        async Task<int> Move(int relative,CancellationToken token) {
            check(owner==vm && vm.IsMoving && starts>0 && closes==0,"Motor starts with the existing stream and camera reservation intact");
            moves++;moveStarted.TrySetResult(true);
            using var registration=token.Register(()=>moveCanceled.TrySetResult(true));
            int actual=await motorFinished.Task;
            token.ThrowIfCancellationRequested();focuserInfo.Position=actual;return actual;
        }
        var focuser=Fake.Of<IFocuserMediator>((m,a)=>m.Name switch {
            "GetInfo"=>focuserInfo,"MoveFocuserRelative"=>Move((int)a[0],(CancellationToken)a[1]),
            "RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var mount=Fake.Of<ITelescopeMediator>((m,a)=>m.Name switch {
            "GetInfo"=>new TelescopeInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var guider=Fake.Of<IGuiderMediator>((m,a)=>m.Name switch {
            "GetInfo"=>new GuiderInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var wheel=Fake.Of<IFilterWheelMediator>((m,a)=>m.Name is "RegisterConsumer" or "RemoveConsumer"?null:Fake.Unexpected(m));
        object Display(BitmapSource bitmap) {
            var plan=Cwseo.NINA.ManualFocuser.LiveWorkflow.Models.FocusAberrationMosaic.Plan(128,128);
            if(!bitmap.IsFrozen || bitmap.PixelWidth!=(inspector?plan.Width:128) || bitmap.PixelHeight!=(inspector?plan.Height:128) ||
                inspector && bitmap.Format!=System.Windows.Media.PixelFormats.Bgr32 || owner!=vm)
                throw new Exception("Main Image must receive a frozen ROI while Live Focus owns capture");
            Interlocked.Increment(ref imageWrites);
            if(vm.IsMoving)Interlocked.Increment(ref movingImageWrites);
            return null;
        }
        var imaging=Fake.Of<IImagingMediator>((m,a)=>m.Name switch {
            "add_ImagePrepared" or "remove_ImagePrepared"=>null,"SetImage"=>Display((BitmapSource)a[0]),_=>Fake.Unexpected(m)});
        var status=Fake.Of<IApplicationStatusMediator>((m,a)=>m.Name=="StatusUpdate"?null:Fake.Unexpected(m));
        var settingsType=typeof(ManualFocuserDockableVM).Assembly.GetType("Cwseo.NINA.ManualFocuser.Properties.Settings");
        var settings=settingsType.GetProperty("Default").GetValue(null);
        var streamingSetting=settingsType.GetProperty("UseFocusStreaming");
        object oldStreaming=streamingSetting.GetValue(settings);streamingSetting.SetValue(settings,true);
        try {
            using(vm=new ManualFocuserDockableVM(profiles,camera,imaging,wheel,focuser,mount,guider,null,null,null,status)) {
                typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.CameraInfo)).SetValue(vm,cameraInfo);
                typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.FocuserInfo)).SetValue(vm,focuserInfo);
                vm.UserStep=600;
                if(inspector){vm.PreviewRoiWidth=32;vm.PreviewRoiHeight=32;}
                else vm.PreviewRoiPreset="512";
                vm.ShowInNinaImage=true;
                vm.PropertyChanged+=(_,e)=> {
                    if(e.PropertyName!=nameof(vm.FocusPreviewImage))return;
                    Interlocked.Increment(ref frames);firstFrame.TrySetResult(true);
                    if(vm.IsMoving && Interlocked.Increment(ref duringMove)>=3)movingFrames.TrySetResult(true);
                    if(moves>0 && !vm.IsMoving)resumedFrame.TrySetResult(true);
                };
                Task<int> Start()=> (Task<int>)typeof(ManualFocuserDockableVM).GetMethod("RunFocusPreviewAsync",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(vm,null);
                var live=Start();await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
                check(vm.PreviewMoveOutCommand.CanExecute(null),"Live movement is available while preview is running");
                vm.PreviewMoveOutCommand.Execute(null);await moveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await movingFrames.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(starts==1 && closes==0 && frames>=4,"Preview displays multiple frames during motor travel without camera stop/restart");
                check(!vm.PreviewMoveInCommand.CanExecute(null) && !vm.CanConfigureLive,"A second move and ROI changes remain locked during travel");
                vm.PreviewStretchStrength=.5;await Task.Delay(200);
                check(starts==1 && closes==0 && owner==vm && vm.IsMoving && vm.PreviewStretchStrength==.5,
                    "Display stretch changes during travel keep the same camera stream and motor task");
                check(imageWrites>0 && movingImageWrites>0 && vm.ShowInNinaImage,
                    "Main Image mirrors live ROI frames during focuser travel without extra captures or restarting video");
                motorFinished.SetResult(10600);await resumedFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(starts==1 && closes==0 && vm.PreviewMoveInCommand.CanExecute(null),"Movement completion re-enables controls with the same stream");
                moveStarted=Signal<bool>();motorFinished=Signal<int>();
                vm.PreviewMoveInCommand.Execute(null);await moveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                vm.StopFocusPreviewCommand.Execute(null);
                await moveCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));await cameraClosing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(!live.IsCompleted && owner==vm && releases==0,"Stop cancels both devices and holds ownership during delayed native cleanup");
                cameraFinished.SetResult(true);await Task.Delay(40);
                check(!live.IsCompleted && owner==vm && vm.IsMoving,"Camera cleanup alone cannot finish while motor cleanup is pending");
                motorFinished.SetResult(10000);check(await live.WaitAsync(TimeSpan.FromSeconds(5))==0,"Stop returns a canceled preview after motor cleanup");
                check(owner==null && releases==1 && !vm.IsMoving && !vm.IsFocusAssistRunning && vm.CanConfigureLive,
                    "Both cleanups finish before reservation and UI locks are released");
                int stoppedWrites=imageWrites;await Task.Delay(150);
                check(imageWrites==stoppedWrites,"Stopped live focus cannot overwrite the main viewer after releasing capture ownership");
                // A failed motor task must also be observed, cancel the stream,
                // and release ownership without another movement or capture.
                firstFrame=Signal<bool>();moveStarted=Signal<bool>();motorFinished=Signal<int>();closes=0;
                live=Start();await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
                vm.PreviewMoveOutCommand.Execute(null);await moveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                motorFinished.SetException(new InvalidOperationException("synthetic focuser failure"));
                bool rejected=false;
                try{await live.WaitAsync(TimeSpan.FromSeconds(5));}catch(InvalidOperationException e){rejected=e.Message=="synthetic focuser failure";}
                check(rejected && closes==1 && owner==null && !vm.IsMoving,"Motor errors terminate preview with completed stream cleanup and released locks");
                firstFrame=Signal<bool>();closes=0;int previousMoves=moves;
                live=Start();await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cameraInfo.Connected=false;vm.PreviewMoveOutCommand.Execute(null);
                rejected=false;
                try{await live.WaitAsync(TimeSpan.FromSeconds(5));}catch(InvalidOperationException e){rejected=e.Message.Contains("camera disconnected");}
                check(rejected && moves==previousMoves && owner==null && closes==1,
                    "Camera disconnection prevents a queued motor move and cleans up preview");
            }
        } finally {streamingSetting.SetValue(settings,oldStreaming);}
    }
}
