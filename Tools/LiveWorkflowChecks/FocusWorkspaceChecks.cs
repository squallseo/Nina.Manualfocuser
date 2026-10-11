using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Model;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

internal static class FocusWorkspaceChecks {
    public static void Run(Action<bool,string> check) {
        Exception failure = null;
        var thread = new Thread(() => {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Dispatcher.CurrentDispatcher.BeginInvoke(async () => {
                try { await Verify(check); } catch (Exception e) { failure = e; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new Exception("Focus workspace checks failed", failure);
    }
    private static object Default(MethodInfo method) => method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    private static async Task Verify(Action<bool,string> check) {
        await MosaicLayoutChecks.Run(check);
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(AppContext.BaseDirectory,"Database","Migration"));
        Application.Current.Resources["PictureSVG"] = new GeometryGroup();
        Application.Current.Resources["HistogramSVG"] = new GeometryGroup();
        var imageSettings = Fake.Of<IImageSettings>((m,a) => m.Name switch { "get_AutoStretchFactor" => .2, "get_BlackClipping" => -2.8, _ => Default(m) });
        var ts = Fake.Properties<ITelescopeSettings>(new() { ["NoSync"] = true });
        int framesPerPoint = 1;
        var focusSettings = Fake.Of<IFocuserSettings>((m,a) => m.Name switch {
            "get_AutoFocusExposureTime" => 3.0, "get_AutoFocusBinning" => (short)2,
            "get_AutoFocusNumberOfFramesPerPoint" => framesPerPoint,
            "get_AutoFocusInnerCropRatio" or "get_AutoFocusOuterCropRatio" => 1.0, _ => Default(m)
        });
        var dockSettings = new NINA.Profile.PluginSettings();
        var cameraSettings=Fake.Properties<ICameraSettings>(new(){["BitDepth"]=16.0,["RawConverter"]=NINA.Core.Enum.RawConverterEnum.DCRAW});
        var profile = Fake.Of<IProfile>((m,a) => m.Name switch { "get_ImageSettings" => imageSettings, "get_TelescopeSettings" => ts, "get_FocuserSettings" => focusSettings, "get_PluginSettings" => dockSettings, "get_CameraSettings"=>cameraSettings, _ => Default(m) });
        var profiles = Fake.Of<IProfileService>((m,a) => m.Name == "get_ActiveProfile" ? profile : Default(m));
        var cameraInfo = new CameraInfo { Connected = true, DeviceId = "Workspace.Test", XSize = 256, YSize = 256, ExposureMin = .001, ExposureMax = 60, CanShowLiveView = true, BinX = 1, BinY = 1 };
        var motorInfo = new FocuserInfo { Connected = true, DeviceId = "Motor.Test", Position = 10000 };
        object owner = null;
        int captures = 0, prepares = 0, bitmapWrites = 0;
        Action duringCapture = null;
        CaptureSequence lastSequence = null;
        TaskCompletionSource<int> pendingMove = null;
        bool slowMove = false, missMove = false;
        int moves = 0;
        CancellationToken moveToken = default;
        TaskCompletionSource<bool> prepareStarted = null, finishPrepare = null;
        IImageData source = null;
        var camera = Fake.Of<ICameraMediator>((m,a) => m.Name switch {
            "GetInfo" => cameraInfo, "IsFreeToCapture" => owner == null || owner == a[0],
            "RegisterCaptureBlock" => Block(a[0]), "ReleaseCaptureBlock" => Release(a[0]),
            "LiveView" => Stream((CancellationToken)a[1]), _ => Default(m)
        });
        object Block(object value) { check(owner == null,"One shared camera owner"); owner = value; return null; }
        object Release(object value) { check(owner == value,"Capture releases its own camera owner"); owner = null; return null; }
        var motor = Fake.Of<IFocuserMediator>((m,a) => m.Name switch {
            "GetInfo" => motorInfo, "MoveFocuserRelative" => Move(motorInfo.Position+(int)a[0],(CancellationToken)a[1]),
            "MoveFocuser" => Move((int)a[0],(CancellationToken)a[1]), _ => Default(m)
        });
        Task<int> Move(int position, CancellationToken token) {
            moves++; moveToken = token;
            if (slowMove) { motorInfo.IsMoving = true; pendingMove = new(); return pendingMove.Task; }
            motorInfo.Position = position; return Task.FromResult(missMove ? position-1 : position);
        }
        var telescope = Fake.Of<ITelescopeMediator>((m,a) => m.Name == "GetInfo" ? new TelescopeInfo() : Default(m));
        var wheel = Fake.Of<IFilterWheelMediator>((m,a) => Default(m));
        var guider = Fake.Of<IGuiderMediator>((m,a) => m.Name == "GetInfo" ? new GuiderInfo() : Default(m));
        var status = Fake.Of<IApplicationStatusMediator>((m,a) => Default(m));
        var analysis = Fake.Of<IStarDetectionAnalysis>((m,a) => Default(m));
        var detection = Fake.Of<IStarDetection>((m,a) => m.Name == "CreateAnalysis" ? analysis : throw new Exception("Star detection was unexpectedly run"));
        var annotator = Fake.Of<IStarAnnotator>((m,a) => throw new Exception("Annotation was unexpectedly run"));
        IImageData Data(int shift) {
            var pixels = Enumerable.Range(0,256*256).Select(i => {
                double x = i%256-128, y = i/256-128;
                double flux=0;
                for(int star=0;star<5;star++) { double dx=x-(star-2)*24; flux+=35000*Math.Exp(-(dx*dx+y*y)/18); }
                return (ushort)(400 + (i*13)%15 + shift + flux);
            }).ToArray();
            return new BaseImageData(pixels,256,256,16,false,new ImageMetaData(),profiles,detection,annotator);
        }
        source = Data(0);
        IExposureData Exposure(IImageData data) => Fake.Of<IExposureData>((m,a) => m.Name == "ToImageData" ? Task.FromResult(data) : Default(m));
        async IAsyncEnumerable<IExposureData> Stream([EnumeratorCancellation] CancellationToken token) {
            while (true) { await Task.Delay(20,token); yield return Exposure(source); }
        }
        object host = null; Type hostType = null;
        var statistics = new NINA.ViewModel.ImageStatisticsVM(profiles);
        var imaging = Fake.Of<IImagingMediator>((m,a) => m.Name switch {
            "CaptureImage" => Capture((CaptureSequence)a[0]), "PrepareImage" => Prepare((IImageData)a[0],(PrepareImageParameters)a[1],(CancellationToken)a[2]),
            "SetImage" => WriteBitmap(), _ => Default(m)
        });
        Task<IExposureData> Capture(CaptureSequence sequence) { captures++; lastSequence=sequence; duringCapture?.Invoke(); return Task.FromResult(Exposure(source)); }
        object WriteBitmap() { bitmapWrites++; return null; }
        async Task<IRenderedImage> Prepare(IImageData data, PrepareImageParameters options, CancellationToken token) {
            check(!options.AutoStretch.HasValue && !options.DetectStars.HasValue,"Sync respects native stretch and star-detection settings");
            prepares++; prepareStarted?.TrySetResult(true);
            if (finishPrepare != null) await finishPrepare.Task;
            var rendered = await (Task<IRenderedImage>)hostType.GetMethod("PrepareImage").Invoke(host,new object[] { data, options, token });
            await statistics.UpdateStatistics(data); return rendered;
        }
        hostType = typeof(NINA.ViewModel.ImageHistory.ImageHistoryVM).Assembly.GetType("NINA.ViewModel.ImageControlVM",true);
        host = Activator.CreateInstance(hostType,profiles,camera,telescope,imaging,status);
        IRenderedImage Current() => (IRenderedImage)hostType.GetProperty("RenderedImage").GetValue(host);
        var hfrAnalyzer = new CheckHfrAnalyzer(80);
        // NINA composes plugin exports on a worker while its WPF Application
        // belongs to the UI thread. Exercise the real importing constructors
        // before any UI-thread construction can initialize diagnostic handlers.
        using (var startup = new CompositionContainer(new TypeCatalog(typeof(ManualFocuserDockableVM),typeof(FocusImageDockableVM),typeof(FocusHistoryDockableVM)))) {
            startup.ComposeExportedValue(profiles); startup.ComposeExportedValue(camera); startup.ComposeExportedValue(imaging);
            startup.ComposeExportedValue(wheel); startup.ComposeExportedValue(motor); startup.ComposeExportedValue(telescope);
            startup.ComposeExportedValue(guider); startup.ComposeExportedValue(status);
            startup.ComposeExportedValue(Fake.Of<IDomeMediator>((m,a) => Default(m)));
            startup.ComposeExportedValue(Fake.Of<NINA.Equipment.Interfaces.IDomeFollower>((m,a) => Default(m)));
            startup.ComposeExportedValue(Fake.Of<NINA.PlateSolving.Interfaces.IPlateSolverFactory>((m,a) => Default(m)));
            startup.ComposeExportedValue<IFocusHfrAnalyzer>(hfrAnalyzer);
            var loading = Task.Run(() => {
                check(!Application.Current.Dispatcher.CheckAccess(),"Plugin MEF loading runs on a background thread");
                return startup.GetExportedValues<IDockableVM>().ToArray();
            });
            // The loader must complete even while the UI dispatcher is occupied.
            // Synchronous UI invocation from a constructor would deadlock startup.
            check(loading.Wait(TimeSpan.FromSeconds(10)),"Background plugin composition does not wait for the UI dispatcher");
            var loaded = await loading;
            check(loaded.Length == 3,"Background MEF composition loads all three MF dockable exports");
            var session = loaded.OfType<ManualFocuserDockableVM>().Single();
            check(loaded.OfType<FocusImageDockableVM>().Single().Session == session && loaded.OfType<FocusHistoryDockableVM>().Single().Session == session,
                "Background MEF composition preserves one shared MF session");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var trace = typeof(ManualFocuserDockableVM).Assembly.GetType("Cwseo.NINA.ManualFocuser.Dockables.FocusViewerDiagnostics");
            check((bool)trace.GetField("initialized",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null),
                "Viewer diagnostics finish initialization on the UI dispatcher after background loading");
        }
        using var vm = new ManualFocuserDockableVM(profiles,camera,imaging,wheel,motor,telescope,guider,null,null,null,status,hfrAnalyzer);
        check(!vm.ShowFocusHfr && !vm.ShowHfrStarBoxes && vm.TrackFocusStars && vm.LiveHfrText=="HFR disabled", "New sessions default to tracking on, but no graph, boxes or HFR analysis");
        vm.UpdateDeviceInfo(cameraInfo); vm.UpdateDeviceInfo(motorInfo);
        Task<int> Invoke(string name) => (Task<int>)typeof(ManualFocuserDockableVM).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(vm,null);
        using (var container = new CompositionContainer(new TypeCatalog(typeof(FocusImageDockableVM),typeof(FocusHistoryDockableVM)))) {
            container.ComposeExportedValue<IProfileService>(profiles); container.ComposeExportedValue(vm);
            var panels = container.GetExportedValues<IDockableVM>().ToArray();
            var imagePanel = panels.OfType<FocusImageDockableVM>().Single(); var historyPanel = panels.OfType<FocusHistoryDockableVM>().Single();
            await FocusDockLayoutChecks.Run(vm, imagePanel, historyPanel, dockSettings, check);
            vm.ShowFocusImage=true; vm.ShowFocusHfr=true;
            vm.IsVisible = false;
            vm.HideCommand.Execute(null);
            check(ReferenceEquals(imagePanel.Session,vm) && ReferenceEquals(historyPanel.Session,vm),"Both dock panels share the control session");
            check(new[] { vm.ContentId,imagePanel.ContentId,historyPanel.ContentId }.Distinct().Count() == 3,"Three fixed, distinct docking identifiers");
            check(typeof(ManualFocuserDockableVM).GetCustomAttribute<PartCreationPolicyAttribute>().CreationPolicy == CreationPolicy.Shared,"MEF keeps the capture session shared");
            var actualToolbar=(System.Windows.Controls.ItemsControl)System.Windows.Markup.XamlReader.Parse("""
                <ItemsControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" ItemsSource="{Binding}">
                    <ItemsControl.ItemsPanel><ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
                    <ItemsControl.ItemTemplate><DataTemplate>
                        <ToggleButton Width="30" Height="30" Margin="2.5" Command="{Binding HideCommand}" IsChecked="{Binding IsVisible, Mode=OneWay}"/>
                    </DataTemplate></ItemsControl.ItemTemplate>
                </ItemsControl>
                """);
            var toolbarWindow=new Window { Content=actualToolbar, Width=200, Height=100, Opacity=0, ShowActivated=false, ShowInTaskbar=false, Left=-10000, Top=-10000 };
            try {
                var allPanels=new List<IDockableVM>{vm,imagePanel,historyPanel};
                var tools=allPanels.ToList();
                actualToolbar.DataContext=tools;
                toolbarWindow.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);actualToolbar.UpdateLayout();
                var toolbarHost=Fake.Of<NINA.WPF.Base.Interfaces.ViewModel.IDockManagerVM>((m,a)=>m.Name switch {
                    "get_AnchorableTools"=>tools,"get_Anchorables"=>allPanels,_=>Default(m)
                });
                var configure=typeof(ManualFocuserDockableVM).Assembly.GetType("Cwseo.NINA.ManualFocuser.Dockables.FocusToolbar").GetMethod("Configure",BindingFlags.Static|BindingFlags.NonPublic);
                configure.Invoke(null,new object[]{toolbarHost});
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);actualToolbar.UpdateLayout();
                var realized=new List<ToggleButton>();
                void FindButtons(DependencyObject parent) {
                    if(parent is ToggleButton button) realized.Add(button);
                    for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) FindButtons(VisualTreeHelper.GetChild(parent,i));
                }
                FindButtons(actualToolbar);
                check(realized.Count==1 && realized.Count(b=>b.Visibility==Visibility.Visible)==1 && realized.Single(b=>b.Visibility==Visibility.Visible).DataContext==vm,
                    "Real WPF ItemsControl loading shows exactly one MF toolbar icon for three dockables");
                check(tools.Count==3 && allPanels.Count==3,
                    "Filtering toolbar entries preserves every dockable and its independent commands");
            } finally { toolbarWindow.Close(); }
            check(vm.IsVisible && imagePanel.IsVisible && historyPanel.IsVisible,"Shared toolbar icon opens all three panels");
            check(vm.IsLiveFocus && vm.HistoryAxisMinimum == 0 && vm.HistoryAxisMaximum == 120 && vm.PreviewRoiPreset=="67%",
                "New workspace defaults to Live Focus with its time axis and a 67-percent sensor ROI");
            vm.FocusMode = "Manual";
            check(vm.IsManualFocus && double.IsNaN(vm.HistoryAxisMinimum),"Selecting Manual mode switches to a position axis");
            check(await Invoke("RunFocusPreviewAsync") == 1 && captures == 1 && owner == null,"Manual mode captures exactly one frame and returns idle");
            check(imagePanel.IsVisible && historyPanel.IsVisible,"Capture preserves the workspace panel visibility");
            check(lastSequence.ExposureTime==3 && lastSequence.Binning.X==2 && !lastSequence.EnableSubSample,"Manual capture uses NINA autofocus exposure/binning and full source, ignoring live ROI settings");
            check(vm.Title=="MF Controls" && imagePanel.Title=="MF Image" && historyPanel.Title=="MF HFR","All three panel titles retain the shared MF prefix");
            await FocusImageToolbarChecks.Run(vm,check);
            var runArgs=Environment.GetCommandLineArgs();
            if(runArgs.Contains("--inspector-window")) await InspectorSourceChecks.Show(vm,runArgs[Array.IndexOf(runArgs,"--inspector-window")+1]);
            if(runArgs.Contains("--inspector-files")) await InspectorSourceChecks.Show(vm,runArgs[Array.IndexOf(runArgs,"--inspector-files")+1],check);
            historyPanel.HideCommand.Execute(null);
            int hfrCalls = hfrAnalyzer.Calls;
            vm.ShowLiveGraphs=true;
            await Invoke("RunFocusPreviewAsync");
            check(!historyPanel.IsVisible && imagePanel.IsVisible && vm.IsVisible,"Capture keeps an independently closed history panel closed");
            check(!vm.ShowFocusHfr && hfrAnalyzer.Calls == hfrCalls && vm.LiveHfrText == "HFR disabled",
                "Closing MF HFR disables manual multi-star detection while image capture continues");
            check(vm.LiveStarProfile.Length>0,"Optional star profile refreshes from the manual capture");
            vm.ShowLiveGraphs=false;
            vm.HideCommand.Execute(null);
            check(!vm.IsVisible && !imagePanel.IsVisible && !historyPanel.IsVisible,"Shared icon hides the workspace even with history disabled");
            vm.HideCommand.Execute(null);
            check(vm.IsVisible && imagePanel.IsVisible && !historyPanel.IsVisible && !vm.ShowFocusHfr,"Shared icon preserves the disabled HFR option when reopening");
            vm.ShowFocusImage=false;
            check(!imagePanel.IsVisible && vm.ShowFocusHfr==false,"Image option hides only the image dock");
            vm.ShowFocusImage=true; vm.ShowFocusHfr=true;
            check(imagePanel.IsVisible && historyPanel.IsVisible,"Controls options reopen both independent docks");
            var manual = Invoke("RunFocusPreviewAsync");
            while(hfrAnalyzer.Active==0 && !manual.IsCompleted) await Task.Delay(5);
            hfrCalls=hfrAnalyzer.Calls;
            vm.ShowFocusHfr=false;
            check(await manual==1 && hfrAnalyzer.Cancellations>0 && hfrAnalyzer.Calls==hfrCalls && double.IsNaN(vm.LiveHfr),
                "Disabling HFR cancels in-flight manual detection without canceling the image capture or publishing stale HFR");
            vm.ShowFocusPanelsCommand.Execute(null);
            check(vm.IsVisible && imagePanel.IsVisible && historyPanel.IsVisible,"Controls can reopen all three panels");
        }
        check(vm.FocusGraphPoints.Length == 1 && vm.FocusGraphPoints[0].X == 10000 && double.IsFinite(vm.FocusGraphPoints[0].Y),"Stationary measurement records the real focuser position");
        motorInfo.Position = 11000; await Invoke("RunFocusPreviewAsync"); await Invoke("RunFocusPreviewAsync");
        check(vm.FocusGraphPoints.Length == 2,"Repeated position measurements share one representative point");
        duringCapture = () => motorInfo.Position += 100; await Invoke("RunFocusPreviewAsync"); duringCapture = null;
        check(vm.FocusGraphPoints.Length == 2,"Travel during exposure cannot enter the position curve");
        var moveMethod = typeof(ManualFocuserDockableVM).GetMethod("MoveStandaloneAsync",BindingFlags.Instance|BindingFlags.NonPublic);
        Task<int> MoveAndCapture(int distance) => (Task<int>)moveMethod.Invoke(vm,new object[] { (int?)distance });
        bool originalCaptureSetting = vm.TakeShootAfterMove;
        var controls=new FocusControlsView { DataContext=vm };
        controls.Measure(new Size(390,1000)); controls.Arrange(new Rect(0,0,390,1000)); controls.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var stop=(System.Windows.Controls.Button)controls.FindName("HaltMoveButton");
        try {
            vm.TakeShootAfterMove = true; framesPerPoint = 2;
            int captureBeforeMove = captures, target = motorInfo.Position + 600;
            duringCapture = () => {
                check(!vm.IsMoving && !motorInfo.IsMoving && motorInfo.Position==target,"Every autofocus frame starts after the move reaches its stationary target");
                check(stop.Visibility==Visibility.Visible && stop.Command==vm.HaltFocuserCommand,
                    "Manual Stop remains accessible during exposure after the motor has stopped");
            };
            check(await MoveAndCapture(600)==1 && captures==captureBeforeMove+2 && owner==null,"Manual move captures configured frames per point and releases the camera");
            check(vm.FocusGraphPoints.Any(p=>p.X==target),"Completed move/capture enters the position HFR curve");
            duringCapture=null; framesPerPoint=1;
            slowMove=true; captureBeforeMove=captures;
            var moving = MoveAndCapture(600);
            check(captures==captureBeforeMove && owner==vm && !vm.MoveOUTCommand.CanExecute(null),"Camera is reserved during movement; capture and duplicate moves wait");
            vm.HaltFocuserCommand.Execute(null);
            check(moveToken.IsCancellationRequested,"Manual stop cancels the shared movement/capture operation");
            motorInfo.IsMoving=false; pendingMove.SetResult(motorInfo.Position+600);
            bool canceled=false; try { await moving; } catch(OperationCanceledException) { canceled=true; }
            check(canceled && captures==captureBeforeMove && owner==null && !vm.IsCapturing,"Canceled movement never captures and releases ownership");
            slowMove=false; missMove=true;
            bool rejected=false; try {await MoveAndCapture(600);} catch(InvalidOperationException) {rejected=true;}
            check(rejected && captures==captureBeforeMove && owner==null,"Failed movement never captures");
            missMove=false;
            var otherOwner=new object(); owner=otherOwner; int beforeMoves=moves;
            check(await MoveAndCapture(600)==0 && moves==beforeMoves && captures==captureBeforeMove,"A busy camera blocks an automatic move/capture before motor motion");
            owner=null;
            vm.TakeShootAfterMove=false;
            check(await MoveAndCapture(600)==1 && captures==captureBeforeMove,"Disabling capture after move retains movement-only behavior");
        } finally {vm.TakeShootAfterMove=originalCaptureSetting;}
        check(prepares == 0 && bitmapWrites == 0,"Private captures never overwrite native image data automatically");
        int capturesBeforeSync = captures;
        await Invoke("SyncSourceImageAsync");
        check(ReferenceEquals(Current().RawImageData,source) && ReferenceEquals(hostType.GetProperty("Image").GetValue(host),Current().Image),"Sync updates both the real native display and raw frame");
        check(statistics.Statistics != null && captures == capturesBeforeSync,"Sync refreshes native statistics without another exposure");
        var originalSource = source;
        vm.FocusMode = "Live";
        var firstFrame = new TaskCompletionSource<bool>();
        vm.PropertyChanged += (_,e) => { if (e.PropertyName == nameof(vm.FocusPreviewImage)) firstFrame.TrySetResult(true); };
        var live = Invoke("RunFocusPreviewAsync"); await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var hfrWait=System.Diagnostics.Stopwatch.StartNew();
        while(hfrAnalyzer.Active==0 && hfrWait.ElapsedMilliseconds<2000) await Task.Delay(5);
        int canceledBefore=hfrAnalyzer.Cancellations;
        vm.ShowFocusHfr=false;
        int callsWhileHidden=hfrAnalyzer.Calls;
        var frameWhileHidden=vm.FocusPreviewImage;
        int pointsWhileHidden=vm.LiveGraphPoints.Length;
        await Task.Delay(250);
        check(hfrAnalyzer.Calls==callsWhileHidden && hfrAnalyzer.Active==0 && hfrAnalyzer.Cancellations>canceledBefore,
            "Hiding HFR during streaming cancels the running detector and schedules no further analysis");
        check(vm.IsFocusAssistRunning && !ReferenceEquals(frameWhileHidden,vm.FocusPreviewImage) && vm.LiveGraphPoints.Length==pointsWhileHidden && double.IsNaN(vm.LiveHfr),
            "Live images continue while disabled HFR publishes neither points nor a stale metric");
        vm.ShowHfrStarBoxes=true;
        hfrWait.Restart();
        while(vm.HfrStarBoxes.Length==0 && hfrWait.ElapsedMilliseconds<2000) await Task.Delay(10);
        check(!vm.ShowFocusHfr && vm.HfrStarBoxes.Length==5 && hfrAnalyzer.Calls>callsWhileHidden && vm.LiveGraphPoints.Length==pointsWhileHidden,
            "Star boxes alone resume the shared detector and live annotations without opening or recording a graph");
        vm.ShowFocusImage=false;
        int callsWithoutView=hfrAnalyzer.Calls;
        await Task.Delay(250);
        check(vm.HfrStarBoxes.Length==0 && hfrAnalyzer.Calls==callsWithoutView && hfrAnalyzer.Active==0,
            "Hiding the image removes the star boxes and stops analysis when its graph is also off");
        vm.ShowFocusImage=true;
        hfrWait.Restart();
        while(vm.HfrStarBoxes.Length==0 && hfrWait.ElapsedMilliseconds<2000) await Task.Delay(10);
        check(vm.HfrStarBoxes.Length==5,"Reopening MF Image resumes fresh star-box measurements");
        vm.ShowHfrStarBoxes=false;
        int callsAfterBoxes=hfrAnalyzer.Calls;
        await Task.Delay(250);
        check(vm.HfrStarBoxes.Length==0 && hfrAnalyzer.Calls==callsAfterBoxes && hfrAnalyzer.Active==0,
            "Turning off both HFR consumers clears annotations and schedules no further detection");
        vm.ShowFocusHfr=true;
        hfrWait.Restart();
        while(!double.IsFinite(vm.LiveHfr) && hfrWait.ElapsedMilliseconds<2000) await Task.Delay(10);
        check(hfrAnalyzer.Calls>callsWhileHidden && double.IsFinite(vm.LiveHfr) && hfrAnalyzer.MaximumConcurrent==1,
            "Reopening HFR resumes fresh live analysis without overlapping detector tasks");
        check(vm.HistoryAxisMinimum == 0 && vm.HistoryAxisMaximum == 120 && !vm.IsManualFocus,"Live mode starts at zero and fills its 120-second window");
        vm.FocusMode = "Manual"; check(vm.FocusMode == "Live","Capture locks mode selection");
        check(ReferenceEquals(Current().RawImageData,originalSource),"Streaming preserves the previous native frame until explicit sync");
        prepareStarted = new(); finishPrepare = new();
        var sync = Invoke("SyncSourceImageAsync"); await prepareStarted.Task;
        source = Data(100); await Task.Delay(150);
        finishPrepare.SetResult(true); await sync; finishPrepare = null; prepareStarted = null;
        check(ReferenceEquals(Current().RawImageData,originalSource),"A requested sync keeps its exact source while newer live frames arrive");
        vm.StopFocusPreviewCommand.Execute(null); await live.WaitAsync(TimeSpan.FromSeconds(10));
        check(!vm.IsFocusAssistRunning && owner == null && ReferenceEquals(Current().RawImageData,originalSource),"Stopping without automatic sync keeps native data consistent");
        await Invoke("SyncSourceImageAsync");
        check(ReferenceEquals(Current().RawImageData,source),"After stop, explicit sync publishes the final frame");
        await (Task<bool>)hostType.GetMethod("ProcessImageHelper",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(host,null);
        check(ReferenceEquals(Current().RawImageData,source) && ReferenceEquals(hostType.GetProperty("Image").GetValue(host),Current().Image),"Native reprocessing after stop retains the synced raw image");
        check(ReferenceEquals(Current().RawImageData.Data.FlatArray,source.Data.FlatArray),"Native plate-solve input points to the synced source pixels");
        source = Data(200); vm.SyncImageOnStop = true; firstFrame = new();
        live = Invoke("RunFocusPreviewAsync"); await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.StopFocusPreviewCommand.Execute(null); await live.WaitAsync(TimeSpan.FromSeconds(10));
        check(ReferenceEquals(Current().RawImageData,source) && owner == null,"Automatic stop sync publishes the final source after camera cleanup");
        vm.FocusMode = "Manual";
        check(vm.FocusGraphPoints.Length == 3,"Switching back to Manual preserves its position measurements");
        check(bitmapWrites == 0,"Every sync uses PrepareImage, never bitmap-only SetImage");
        var beforeInvalidPreview = prepares;
        typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.FocusPreviewImage)).SetValue(vm,new DrawingImage());
        check(await Invoke("SyncSourceImageAsync") == 0 && prepares == beforeInvalidPreview,"A preview without source pixels cannot sync an older frame");
        var history = new PositionHfrHistory(); history.Record(10,3);history.Record(10,1);history.Record(20,double.NaN);history.Record(5,2);
        check(history.DisplayPoints().Select(p=>p.X).SequenceEqual(new double[] {5,10}) && history.DisplayPoints()[1].Y == 2,"Position history sorts positions, takes medians and skips invalid HFR");
        PositionHistoryChecks.Run(check);
        // Simulator changes mode/ROI, which deliberately clear session measurements.
        // Run it after the position-history and main-view synchronization checks.
        var simulatorArgs=Environment.GetCommandLineArgs();
        if(simulatorArgs.Contains("--simulator-cycle")) await SimulatorFolderChecks.Run(vm,profiles,telescope,simulatorArgs[Array.IndexOf(simulatorArgs,"--simulator-cycle")+1],check);
        await NativeViewerZoomChecks.Run(host,profiles,detection,annotator,check);
    }
}
