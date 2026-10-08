using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    public static void Run(Action<bool,string> check,bool nativeAsi=false) {
        Exception failure=null;
        var thread=new Thread(()=> {try{RunAsync(check,nativeAsi).GetAwaiter().GetResult();}catch(Exception e){failure=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)throw new Exception("Live movement integration failed",failure);
    }
    private static async Task RunAsync(Action<bool,string> check,bool nativeAsi) {
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"Database","Migration"));
        var profile=Fake.Of<IProfile>((m,a)=>Fake.Unexpected(m));
        var profiles=Fake.Of<IProfileService>((m,a)=>m.Name=="get_ActiveProfile"?profile:
            m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")?null:Fake.Unexpected(m));
        var cameraInfo=new CameraInfo{Connected=true,DeviceId=nativeAsi?"ZWOptical_ASI2600MM Pro_test":"Test.LiveView",CanShowLiveView=!nativeAsi,CanSubSample=true,
            XSize=128,YSize=128,ExposureMin=.001,ExposureMax=60,BinX=1,BinY=1};
        var focuserInfo=new FocuserInfo{Connected=true,Position=10000};
        var firstFrame=Signal<bool>();var movingFrames=Signal<bool>();var resumedFrame=Signal<bool>();
        var moveStarted=Signal<bool>();var moveCanceled=Signal<bool>();var motorFinished=Signal<int>();
        var cameraClosing=Signal<bool>();var cameraFinished=Signal<bool>();
        int starts=0,closes=0,frames=0,duringMove=0,releases=0,moves=0;
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
            "LiveView"=>Stream((CancellationToken)a[1]),
            "SetReadoutMode" or "SetBinning" or "SetSubSambleRectangle" or "RegisterConsumer" or "RemoveConsumer"=>null,
            _=>Fake.Unexpected(m)});
        object Block(object value){check(owner==null,"Live preview holds a single camera reservation");owner=value;return null;}
        object Release(object value){check(owner==value,"Cleanup releases only its own reservation");owner=null;releases++;return null;}
        IAsyncEnumerable<IExposureData> Stream(CancellationToken token){starts++;return HostStream(token);}
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
        var imaging=Fake.Of<IImagingMediator>((m,a)=>m.Name is "add_ImagePrepared" or "remove_ImagePrepared"?null:Fake.Unexpected(m));
        var status=Fake.Of<IApplicationStatusMediator>((m,a)=>m.Name=="StatusUpdate"?null:Fake.Unexpected(m));
        var settingsType=typeof(ManualFocuserDockableVM).Assembly.GetType("Cwseo.NINA.ManualFocuser.Properties.Settings");
        var settings=settingsType.GetProperty("Default").GetValue(null);
        var streamingSetting=settingsType.GetProperty("UseFocusStreaming");
        object oldStreaming=streamingSetting.GetValue(settings);streamingSetting.SetValue(settings,true);
        try {
            using(vm=new ManualFocuserDockableVM(profiles,camera,imaging,wheel,focuser,mount,guider,null,null,null,null,null,status)) {
                typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.CameraInfo)).SetValue(vm,cameraInfo);
                typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.FocuserInfo)).SetValue(vm,focuserInfo);
                vm.FocusMode="Live";vm.UserStep=600;
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
