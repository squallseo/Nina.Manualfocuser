using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cwseo.NINA.ManualFocuser.Dockables;
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

internal static class CurveVmChecks {
    public static void Run(Action<bool,string> check) {
        Exception failure=null;
        var thread=new Thread(()=>{try{RunAsync(check).GetAwaiter().GetResult();}catch(Exception e){failure=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)throw new Exception("Curve AF VM integration failed",failure);
    }
    private static async Task RunAsync(Action<bool,string> check) {
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"Database","Migration"));
        var focus=Fake.Properties<IFocuserSettings>(new(){["AutoFocusStepSize"]=2500,["AutoFocusInitialOffsetSteps"]=2,
            ["AutoFocusNumberOfFramesPerPoint"]=5,["RSquaredThreshold"]=.9,
            ["AutoFocusTimeoutSeconds"]=0,["AutoFocusUseBrightestStars"]=5});
        var imageSettings=Fake.Properties<IImageSettings>(new(){["StarSensitivity"]=StarSensitivityEnum.Normal,["NoiseReduction"]=NoiseReductionEnum.None,
            ["AutoStretchFactor"]=.2,["BlackClipping"]=-2.8,["UnlinkedStretch"]=false,["DebayerImage"]=false});
        var cameraSettings=Fake.Properties<ICameraSettings>(new(){["PixelSize"]=3.76});
        var telescopeSettings=Fake.Properties<ITelescopeSettings>(new(){["FocalLength"]=600.0});
        var profile=Fake.Of<IProfile>((m,a)=>m.Name switch {"get_FocuserSettings"=>focus,"get_ImageSettings"=>imageSettings,
            "get_CameraSettings"=>cameraSettings,"get_TelescopeSettings"=>telescopeSettings,_=>Fake.Unexpected(m)});
        var profiles=Fake.Of<IProfileService>((m,a)=>m.Name=="get_ActiveProfile"?profile:m.Name.StartsWith("add_")||m.Name.StartsWith("remove_")?null:Fake.Unexpected(m));
        var cameraInfo=new CameraInfo{Connected=true,DeviceId="Curve.Stream.Test",CanShowLiveView=true,CanSubSample=true,XSize=128,YSize=128,
            ExposureMin=.001,ExposureMax=60,BinX=1,BinY=1,SensorType=SensorType.Monochrome};
        var focuserInfo=new FocuserInfo{Connected=true,Position=20000};
        ManualFocuserDockableVM vm=null;object owner=null;
        int starts=0,stops=0,moves=0,detects=0,releases=0,selectorCalls=0,updates=0,inDetector=0;
        bool overlap=false,stopOnVerified=true,blank=false,stopDuringAnalysis=false,badVerification=false;
        bool quadraticData=false;Action changeCurveDuringDetection=null;
        var statusMessages=new List<string>();
        bool graphScenario=false;
        var graphStarted=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var graphCanceled=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var graphMotorCleanup=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int graphMovingPreviews=0;
        int singleCaptures=0;
        var stopRequested=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported=new List<StarDetectionResult>();var reportedLists=new List<List<DetectedStar>>();
        var detector=Fake.Of<IStarDetection>((m,a)=>m.Name switch {
            "CreateAnalysis"=>null,"Detect"=>Detect((IRenderedImage)a[0],(StarDetectionParams)a[2],(CancellationToken)a[4]),
            "UpdateAnalysis"=>Updated(),_=>Fake.Unexpected(m)});
        object Updated(){updates++;throw new Exception("Detector-owned analysis must not be published or mutated");}
        async Task<StarDetectionResult> Detect(IRenderedImage image,StarDetectionParams parameters,CancellationToken ct) {
            int position=image.RawImageData.Data.FlatArray[0];detects++;Interlocked.Increment(ref inDetector);
            var change=changeCurveDuringDetection;changeCurveDuringDetection=null;change?.Invoke();
            if(focuserInfo.IsMoving) overlap=true;
            try {
                check(position!=65000,"Moving/buffered star frames never reach the HFR detector");
                check(parameters.IsAutoFocus && parameters.NumberOfAFStars==5 && !parameters.UseROI,"Pluggable HFR detector receives NINA AF parameters for the selected sensor crop");
                check(image.RawImageData.Properties.BitDepth==16 && image.Image!=null,"NINA renders and automatically stretches a private image before detection");
                check(image.RawImageData.MetaData.FilterWheel.Filter=="L" && image.RawImageData.MetaData.Focuser.Position==position &&
                    image.RawImageData.MetaData.Camera.BinX==1 && image.RawImageData.MetaData.Camera.PixelSize==3.76,
                    "Hocus Focus receives filter, pixel scale/binning and the acquisition position in private metadata");
                if(stopDuringAnalysis) {vm.StopFocusPreviewCommand.Execute(null);stopRequested.TrySetResult(true);}
                await Task.Delay(45,ct);
                var stars=new List<DetectedStar>{new DetectedStar(),new DetectedStar()};
                double value=quadraticData ? 2+Math.Pow((position-20000)/4000.0,2) : 2*Math.Sqrt(1+Math.Pow((position-20000)/4000.0,2));
                var result=new StarDetectionResult{AverageHFR=blank?double.NaN:value+.012*(detects%5-2),HFRStdDev=.02,StarList=stars};
                if(badVerification && vm.AutoFocusFitPoints.Count>0) result.AverageHFR+=3;
                reported.Add(result);reportedLists.Add(stars);return result;
            } finally {Interlocked.Decrement(ref inDetector);}
        }
        var selector=Fake.Of<IPluggableBehaviorSelector<IStarDetection>>((m,a)=>m.Name=="GetBehavior"?Selected():Fake.Unexpected(m));
        object Selected(){selectorCalls++;return detector;}
        IExposureData Exposure() {
            ushort[] raw=new ushort[128*128];
            for(int y=0;y<128;y++)for(int x=0;x<128;x++)raw[y*128+x]=(ushort)(300+30000*Math.Exp(-((x-64)*(x-64)+(y-64)*(y-64))/18.0));
            raw[0]=(ushort)(focuserInfo.IsMoving?65000:focuserInfo.Position);
            var array=Fake.Of<IImageArray>((m,a)=>m.Name=="get_FlatArray"?raw:Fake.Unexpected(m));
            var image=Fake.Of<IImageData>((m,a)=>m.Name switch{"get_Properties"=>new ImageProperties(128,128,16,false,1,1),"get_Data"=>array,_=>Fake.Unexpected(m)});
            return Fake.Of<IExposureData>((m,a)=>m.Name=="ToImageData"?Task.FromResult(image):Fake.Unexpected(m));
        }
        async IAsyncEnumerable<IExposureData> Stream([EnumeratorCancellation]CancellationToken ct) {
            starts++;
            try {while(true){var exposure=Exposure();await Task.Delay(12,ct);yield return exposure;}}
            finally {stops++;}
        }
        var camera=Fake.Of<ICameraMediator>((m,a)=>m.Name switch {
            "GetInfo"=>cameraInfo,"IsFreeToCapture"=>owner==null||owner==a[0],"RegisterCaptureBlock"=>Block(a[0]),"ReleaseCaptureBlock"=>Release(a[0]),
            "LiveView"=>Stream((CancellationToken)a[1]),"SetReadoutMode" or "SetBinning" or "SetSubSambleRectangle"=>null,
            "RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        object Block(object value){check(owner==null,"Curve AF owns one camera reservation throughout the scan");owner=value;return null;}
        object Release(object value){check(owner==value && stops==starts && inDetector==0 && !focuserInfo.IsMoving,"Camera cleanup and all analysis/motor work finish before reservation release");owner=null;releases++;return null;}
        var motor=Fake.Of<IFocuserMediator>((m,a)=>m.Name switch {
            "GetInfo"=>focuserInfo,"GetDevice"=>null,"MoveFocuser"=>Move((int)a[0],(CancellationToken)a[1]),
            "RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        async Task<int> Move(int x,CancellationToken ct) {
            moves++;focuserInfo.IsMoving=true;if(inDetector>0)overlap=true;
            check(owner==vm && !vm.AutoRoiCommand.CanExecute(null),"The camera/ROI remain reserved during scan movement");
            check(!vm.CanMoveFromGraph,"Graph gestures cannot interrupt an AF or motor movement");
            try {
                if(graphScenario && (x==21000 || x==22000)) {
                    graphStarted.TrySetResult(true);
                    using var registration=ct.Register(()=>graphCanceled.TrySetResult(true));
                    await graphMotorCleanup.Task;
                    ct.ThrowIfCancellationRequested();
                } else await Task.Delay(30,ct);
                focuserInfo.Position=x;return x;
            } finally{focuserInfo.IsMoving=false;}
        }
        var mount=Fake.Of<ITelescopeMediator>((m,a)=>m.Name switch{"GetInfo"=>new TelescopeInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var guider=Fake.Of<IGuiderMediator>((m,a)=>m.Name switch{"GetInfo"=>new GuiderInfo(),"RegisterConsumer" or "RemoveConsumer"=>null,_=>Fake.Unexpected(m)});
        var wheel=Fake.Of<IFilterWheelMediator>((m,a)=>m.Name=="GetInfo"?new NINA.Equipment.Equipment.MyFilterWheel.FilterWheelInfo {
            Connected=true,SelectedFilter=new NINA.Core.Model.Equipment.FilterInfo{Name="L"}}:
            m.Name is "RegisterConsumer" or "RemoveConsumer"?null:Fake.Unexpected(m));
        var imaging=Fake.Of<IImagingMediator>((m,a)=>m.Name=="CaptureImage"?SingleCapture():m.Name is "add_ImagePrepared" or "remove_ImagePrepared"?null:Fake.Unexpected(m));
        Task<IExposureData> SingleCapture(){singleCaptures++;check(owner==vm && !focuserInfo.IsMoving,"Single-frame fallback captures only at a settled, reserved focus position");return Task.FromResult(Exposure());}
        object Status(object value){statusMessages.Add(((ApplicationStatus)value).Status);return null;}
        var status=Fake.Of<IApplicationStatusMediator>((m,a)=>m.Name=="StatusUpdate"?Status(a[0]):Fake.Unexpected(m));
        var settingsType=typeof(ManualFocuserDockableVM).Assembly.GetType("Cwseo.NINA.ManualFocuser.Properties.Settings");
        var settings=settingsType.GetProperty("Default").GetValue(null);var streaming=settingsType.GetProperty("UseFocusStreaming");
        var hfrChoice=settingsType.GetProperty("HfrCurveFit");object priorHfr=hfrChoice.GetValue(settings);hfrChoice.SetValue(settings,"Hyperbolic");
        object prior=streaming.GetValue(settings);streaming.SetValue(settings,true);
        try {
            using(vm=new ManualFocuserDockableVM(profiles,camera,imaging,wheel,motor,mount,guider,null,null,null,selector,null,status)) {
                typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.CameraInfo)).SetValue(vm,cameraInfo);
                typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.FocuserInfo)).SetValue(vm,focuserInfo);
                vm.FocusMode="Auto";vm.AutofocusMethod="Linear";vm.PreviewExposureMs=1;
                var roi=vm.PreviewRoiRectangle;
                vm.PropertyChanged+=(_,e)=>{if(stopOnVerified && e.PropertyName==nameof(vm.AutoFocusMetricText) && vm.AutoFocusMetricText.StartsWith("Verified"))vm.StopFocusPreviewCommand.Execute(null);};
                Task<int> Run()=>(Task<int>)typeof(ManualFocuserDockableVM).GetMethod("ExecuteLinearAFAsync",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(vm,null);
                check(await Run().WaitAsync(TimeSpan.FromSeconds(20))==1,"The actual Linear AF VM fits and independently verifies an HFR minimum");
                check(starts==1 && stops==1 && overlap,$"One continuous stream remains open through all moves, with analysis overlapping motor motion (starts={starts}, stops={stops}, overlap={overlap})");
                check(moves==6 && focuserInfo.Position==20000 && vm.TargetPosition==20000,"Fixed five-position scan and one fitted verification move replace incremental searching");
                check(vm.AutoFocusBoxes.Count==5 && vm.AutoFocusVerificationBoxes.Count==1 && vm.AutoFocusFitPoints.Count==201,"AF graph receives position box plots, a fitted curve and independent verification statistics");
                CurveChartPreview.Write(vm.AutoFocusBoxes,vm.AutoFocusVerificationBoxes,vm.AutoFocusFitPoints,vm.AutoFocusBoxWidth,"bin/curve-af-chart.png");
                check(vm.AutoFocusBoxes.All(b=>b.Values.Count>=5) && vm.PreviewRoiRectangle==roi,"Stationary multi-frame statistics retain the selected ROI across the whole scan");
                check(selectorCalls==1 && updates==0 && reported.Zip(reportedLists).All(p=>ReferenceEquals(p.First.StarList,p.Second)),"The selected detector is pinned once and its results/star subtype lists stay untouched");
                check(owner==null && !vm.IsFocusAssistRunning && !vm.IsMoving && releases==1,"Verified preview Stop restores every UI and camera lock");
                check(statusMessages.Any(s=>s!=null && s.Contains("Hyperbolic (symmetric)") && s.Contains("verifying position")),
                    "The real HFR VM uses the plugin's symmetric Hyperbolic choice without reading NINA's curve-model property");
                statusMessages.Clear();quadraticData=true;hfrChoice.SetValue(settings,"Parabolic");
                changeCurveDuringDetection=()=>hfrChoice.SetValue(settings,"Hyperbolic");
                check(await Run()==1 && statusMessages.Any(s=>s!=null && s.Contains("Parabolic R²") && s.Contains("verifying position")),
                    "Changing plugin options during analysis cannot alter the model pinned at AF start");
                quadraticData=false;
                focuserInfo.IsMoving=true;
                check(!vm.SelectGraphPosition(21000),"An external motor movement blocks graph selection");
                focuserInfo.IsMoving=false;focuserInfo.Connected=false;
                check(!vm.SelectGraphPosition(21000),"A disconnected focuser blocks graph selection");
                focuserInfo.Connected=true;
                graphScenario=true;stopOnVerified=false;
                vm.PropertyChanged+=(_,e)=>{if(graphScenario && vm.IsMoving && e.PropertyName==nameof(vm.FocusPreviewImage))Interlocked.Increment(ref graphMovingPreviews);};
                int beforeGraphStreams=starts,beforeGraphStops=stops;
                var graphRun=Run();
                var graphWait=Stopwatch.StartNew();
                while(!vm.CanMoveFromGraph && graphWait.Elapsed<TimeSpan.FromSeconds(15))await Task.Delay(10);
                check(vm.CanMoveFromGraph && vm.AutoFocusMetricText.StartsWith("Verified"),"Graph movement becomes available only after independent AF verification");
                int beforeGraphMoves=moves;
                check(vm.SelectGraphPosition(21000) && vm.GraphSelectionVisible && moves==beforeGraphMoves,"Dragging previews a target without issuing a motor command");
                vm.CancelGraphSelection();
                check(!vm.GraphSelectionVisible && moves==beforeGraphMoves,"Escape/capture loss can discard a selection without movement");
                check(await vm.MoveFocusFromGraphAsync(21000)==1 && !vm.CanMoveFromGraph,"Release queues one absolute graph move and blocks a second command");
                await graphStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                graphWait.Restart();
                while(graphMovingPreviews<3 && graphWait.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(10);
                check(graphMovingPreviews>=3 && starts==beforeGraphStreams+1 && stops==beforeGraphStops,
                    "AF preview keeps the original stream displaying frames during graph motor travel");
                check(vm.AutoFocusMetricText.Contains("historical") && vm.AutoFocusBoxes.Count==5 && vm.AutoFocusVerificationBoxes.Count==1,
                    "Manual adjustment labels the original verified curve as historical without mixing moving frames into statistics");
                graphMotorCleanup.SetResult(21000);
                graphWait.Restart();
                while(!vm.CanMoveFromGraph && graphWait.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(10);
                check(vm.CanMoveFromGraph && vm.GraphCurrentPosition==21000 && vm.TargetPosition==21000 && starts==beforeGraphStreams+1 && stops==beforeGraphStops,
                    "Completed absolute graph movement updates the current position and leaves the same preview stream open");
                graphStarted=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                graphMotorCleanup=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                check(await vm.MoveFocusFromGraphAsync(22000)==1,"A later release can issue another move after the first finishes");
                await graphStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                vm.StopFocusPreviewCommand.Execute(null);
                await graphCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));await Task.Delay(40);
                check(!graphRun.IsCompleted && owner==vm,"Stop holds the reservation until a pending graph motor finishes native cleanup");
                graphMotorCleanup.SetResult(22000);
                check(await graphRun.WaitAsync(TimeSpan.FromSeconds(5))==0 && owner==null && !vm.IsMoving,
                    "Graph Stop drains motor and stream cleanup and releases all locks");
                graphScenario=false;
                stopOnVerified=false;badVerification=true;
                bool unverified=false;try{await Run();}catch(InvalidOperationException){unverified=true;}
                check(unverified && vm.AutoFocusVerificationBoxes.Count==0 && !vm.AutoFocusMetricText.StartsWith("Verified") && owner==null,
                    "An independently worse verification point cannot be reported as successful autofocus");
                badVerification=false;stopOnVerified=true;streaming.SetValue(settings,false);
                int priorStreams=starts;
                check(await Run()==1 && starts==priorStreams && singleCaptures==35 && owner==null,
                    "Unsupported/disabled streaming uses the same HFR scan, statistics and fit with sequential single captures");
                streaming.SetValue(settings,true);
                stopOnVerified=false;blank=true;int priorMoves=moves;
                bool invalid=false;try{await Run();}catch(InvalidOperationException){invalid=true;}
                check(invalid && moves==priorMoves && owner==null,"A missing star in Linear preflight prevents all scan moves and restores reservation");
                blank=false;stopDuringAnalysis=true;
                check(await Run()==0 && owner==null && inDetector==0 && moves==priorMoves,"Stop during queued HFR analysis drains the worker and stream without initiating focus movement");
            }
        } finally {streaming.SetValue(settings,prior);hfrChoice.SetValue(settings,priorHfr);}
    }
}
