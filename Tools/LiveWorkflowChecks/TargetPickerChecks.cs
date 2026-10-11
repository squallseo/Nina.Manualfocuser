using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

internal static class TargetPickerChecks
{
    private sealed class Catalogue : IFocusTargetCatalogue
    {
        public Func<FocusTargetKind, string, CancellationToken, Task<IReadOnlyList<FocusStarSuggestion>>> Search;
        public Task<IReadOnlyList<FocusStarSuggestion>> SearchAsync(FocusTargetKind kind, string query, CancellationToken token) => Search(kind, query, token);
    }

    public static void Run(Action<bool, string> check)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try { await Verify(check); } catch (Exception e) { failure = e; }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new Exception("Target picker checks failed", failure);
    }

    private static async Task Verify(Action<bool, string> check)
    {
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Database", "Migration"));
        DateTime utc = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var astroValues = new Dictionary<string, object> { ["Latitude"] = 60.0, ["Longitude"] = 0.0, ["Elevation"] = 100.0, ["Horizon"] = null };
        var astro = Fake.Properties<IAstrometrySettings>(astroValues);
        var ps = Fake.Properties<IPlateSolveSettings>(new() { ["ExposureTime"] = 2.0, ["Gain"] = -1, ["Binning"] = (short)1,
            ["Threshold"] = .5, ["NumberOfAttempts"] = 1, ["ReattemptDelay"] = 0.0, ["DownSampleFactor"] = 0,
            ["MaxObjects"] = 500, ["Regions"] = 5000, ["SearchRadius"] = 30.0, ["BlindFailoverEnabled"] = true });
        var ts = Fake.Properties<ITelescopeSettings>(new() { ["FocalLength"] = 600.0, ["NoSync"] = true });
        var cs = Fake.Properties<ICameraSettings>(new() { ["PixelSize"] = 3.76 });
        var profile = Fake.Of<IProfile>((m, a) => m.Name switch {
            "get_AstrometrySettings" => astro, "get_PlateSolveSettings" => ps, "get_TelescopeSettings" => ts,
            "get_CameraSettings" => cs, _ => Fake.Unexpected(m) });
        var profiles = Fake.Of<IProfileService>((m, a) => m.Name == "get_ActiveProfile" ? profile :
            m.Name.StartsWith("add_") || m.Name.StartsWith("remove_") ? null : Fake.Unexpected(m));
        var cameraInfo = new CameraInfo { Connected = true, DeviceId = "synthetic", ExposureMin = .001, ExposureMax = 60 };
        var mountInfo = new TelescopeInfo { Connected = true };
        var guiderInfo = new GuiderInfo { Connected = false };
        string guiderState = "Guiding";
        int moves = 0, centers = 0, reservations = 0;
        Coordinates movedTo = null;
        object owner = null;
        TaskCompletionSource<bool> stopStarted = null, finishStop = null;
        ManualFocuserDockableVM vm = null;
        var camera = Fake.Of<ICameraMediator>((m, a) => m.Name switch {
            "GetInfo" => cameraInfo, "IsFreeToCapture" => owner == null || owner == a[0],
            "RegisterCaptureBlock" => Block(a[0]), "ReleaseCaptureBlock" => Release(a[0]),
            "RegisterConsumer" or "RemoveConsumer" => null, _ => Fake.Unexpected(m) });
        object Block(object value) { check(owner == null, "Target movement has one capture owner"); owner = value; reservations++; return null; }
        object Release(object value) { check(owner == value, "Target movement releases its capture owner"); owner = null; return null; }
        var mount = Fake.Of<ITelescopeMediator>((m, a) => m.Name switch {
            "GetInfo" => mountInfo, "SlewToCoordinatesAsync" => Slew((Coordinates)a[0]),
            "RegisterConsumer" or "RemoveConsumer" => null, _ => Fake.Unexpected(m) });
        Task<bool> Slew(Coordinates coordinates) { moves++; movedTo = coordinates; return Task.FromResult(true); }
        var guiderDevice = Fake.Of<IGuider>((m, a) => m.Name == "get_State" ? guiderState : Fake.Unexpected(m));
        var guider = Fake.Of<IGuiderMediator>((m, a) => m.Name switch {
            "GetInfo" => guiderInfo, "GetDevice" => guiderDevice, "StopGuiding" => StopGuiding(),
            "RegisterConsumer" or "RemoveConsumer" or "add_GuidingStarted" or "remove_GuidingStarted" => null,
            _ => Fake.Unexpected(m) });
        async Task<bool> StopGuiding() { stopStarted?.TrySetResult(true); if (finishStop != null) await finishStop.Task; utc = utc.AddMinutes(10); guiderState = "Stopped"; return true; }
        var motor = Fake.Of<IFocuserMediator>((m, a) => m.Name is "RegisterConsumer" or "RemoveConsumer" ? null : Fake.Unexpected(m));
        var wheel = Fake.Of<IFilterWheelMediator>((m, a) => m.Name is "RegisterConsumer" or "RemoveConsumer" ? null : Fake.Unexpected(m));
        var imaging = Fake.Of<IImagingMediator>((m, a) => m.Name is "add_ImagePrepared" or "remove_ImagePrepared" ? null : Fake.Unexpected(m));
        var dome = Fake.Of<IDomeMediator>((m, a) => m.Name == "GetInfo" ? new DomeInfo() : Fake.Unexpected(m));
        var status = Fake.Of<IApplicationStatusMediator>((m, a) => m.Name == "StatusUpdate" ? null : Fake.Unexpected(m));
        ICaptureSolver capture = Fake.Of<ICaptureSolver>((m, a) => Fake.Unexpected(m));
        var center = Fake.Of<ICenteringSolver>((m, a) => m.Name switch {
            "get_CaptureSolver" => capture, "set_CaptureSolver" => SetCapture((ICaptureSolver)a[0]),
            "Center" => Center((CenterSolveParameter)a[1]), _ => Fake.Unexpected(m) });
        object SetCapture(ICaptureSolver value) { capture = value; return null; }
        Task<PlateSolveResult> Center(CenterSolveParameter parameter) { centers++; return Task.FromResult(new PlateSolveResult { Success = true, Coordinates = parameter.Coordinates }); }
        var solver = Fake.Of<IPlateSolver>((m, a) => Fake.Unexpected(m));
        var factory = Fake.Of<IPlateSolverFactory>((m, a) => m.Name switch {
            "GetPlateSolver" or "GetBlindSolver" => solver, "GetCenteringSolver" => center, _ => Fake.Unexpected(m) });
        FocusStarSuggestion Fixed(FocusTargetKind kind, string id, double magnitude = 2, double dec = 89, string name = null) => new()
        { Kind = kind, Id = id, Name = name ?? id, Magnitude = magnitude, Coordinates = new Coordinates(0, dec, Epoch.J2000, Coordinates.RAType.Degrees) };
        var stars = new[] { Fixed(FocusTargetKind.Stars, "Bright"), Fixed(FocusTargetKind.Stars, "Faint", 8), Fixed(FocusTargetKind.Stars, "Below", 1, -89) };
        var dsos = new[] { Fixed(FocusTargetKind.DeepSky, "DSO.1", 12, name: "M31"), Fixed(FocusTargetKind.DeepSky, "DSO.2", double.NaN, name: "Unknown magnitude"), Fixed(FocusTargetKind.DeepSky, "below", 14, -89) };
        var catalogue = new Catalogue();
        Task<IReadOnlyList<FocusStarSuggestion>> DefaultSearch(FocusTargetKind kind, string query, CancellationToken token) => Task.FromResult<IReadOnlyList<FocusStarSuggestion>>(
            kind == FocusTargetKind.Stars ? stars : kind == FocusTargetKind.DeepSky ? dsos.Where(t => FocusTargetPlanner.Matches(t, query)).ToArray() : FocusTargetPlanner.SolarSystemTargets);
        catalogue.Search = DefaultSearch;
        using (vm = new ManualFocuserDockableVM(profiles, camera, imaging, wheel, motor, mount, guider, dome, null, factory, status, catalogue, () => utc))
        {
            vm.UpdateDeviceInfo(cameraInfo); vm.UpdateDeviceInfo(mountInfo); vm.UpdateDeviceInfo(guiderInfo);
            Task<int> Pending() => (Task<int>)typeof(ManualFocuserDockableVM).GetField("pendingTargetSearch", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(vm);
            Task<int> Move(bool centerTarget) => (Task<int>)typeof(ManualFocuserDockableVM).GetMethod(centerTarget ? "GotoFocusTargetAsync" : "SlewFocusTargetAsync", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(vm, null);
            check(await vm.RefreshFocusTargetsAsync() == 1 && vm.FocusTargets.Single().Name == "Bright", "Stars retain both brightness and altitude filtering");
            vm.FocusTargetQuery = "no matching name"; await Pending();
            check(vm.FocusTargetQuery == "" && vm.FocusTargets.Single().Name == "Bright", "Stars use altitude and magnitude without a hidden name-search filter");
            vm.SelectedTargetKind = FocusTargetKind.DeepSky; await Pending();
            check(vm.FocusTargetQuery == "" && vm.FocusTargets.Count == 3 && !vm.IsStarTargetCategory,
                "Category changes clear stale search text; faint/unknown-magnitude and low DSO targets remain findable");
            vm.MinimumFocusAltitude = 85; vm.MaximumFocusMagnitude = -2;
            check(vm.FocusTargets.Count == 3 && await Move(true) == 1 && centers == 1 && moves == 1,
                "Deep-sky GOTO uses NINA centering without applying star filters");
            vm.SelectedFocusTarget = vm.FocusTargets.Single(t => t.Id == "DSO.2");
            await vm.RefreshFocusTargetsAsync();
            check(vm.SelectedFocusTarget.Id == "DSO.2", "Refresh preserves the target's catalogue identity");
            vm.SelectedFocusTarget = vm.FocusTargets.Single(t => t.Id == "below");
            bool blocked = false;
            try { await Move(false); } catch (InvalidOperationException e) { blocked = e.Message.Contains("horizon"); }
            check(blocked && moves == 1, "A below-horizon DSO is rejected before equipment movement");

            var slowStarted = new TaskCompletionSource<bool>();
            var releaseSlow = new TaskCompletionSource<IReadOnlyList<FocusStarSuggestion>>();
            catalogue.Search = (kind, query, token) => query == "slow" ? SlowSearch() : DefaultSearch(kind, query, token);
            Task<IReadOnlyList<FocusStarSuggestion>> SlowSearch() { slowStarted.TrySetResult(true); return releaseSlow.Task; }
            vm.FocusTargetQuery = "slow";
            Task<int> slowRequest = Pending();
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(vm.SelectedFocusTarget == null && !vm.SlewFocusTargetCommand.CanExecute(null), "Searching immediately clears the previous GOTO target");
            vm.FocusTargetQuery = "M31"; await Pending();
            releaseSlow.SetResult(new[] { dsos[1] }); await slowRequest;
            check(vm.FocusTargets.Count == 1 && vm.SelectedFocusTarget.Name == "M31" && !vm.IsSearchingTargets,
                "A late cancelled search cannot replace newer results or their busy state");
            catalogue.Search = DefaultSearch;

            vm.SelectedTargetKind = FocusTargetKind.SolarSystem; await Pending();
            check(vm.FocusTargets.Count == 8 && vm.FocusTargets.All(t => t.Kind == FocusTargetKind.SolarSystem), "Solar system lists every Moon/planet target independently of star filters");
            vm.FocusTargetQuery = "달"; await Pending();
            check(vm.FocusTargetQuery == "" && vm.FocusTargets.Count == 8 && !vm.CanSearchFocusTargets,"Solar system ignores search input and keeps the complete target list");
            vm.SelectedFocusTarget = vm.FocusTargets.Single(t => t.SolarBody == NOVAS.Body.Moon);
            check(!vm.GotoFocusTargetCommand.CanExecute(null) &&
                vm.GotoFocusTargetTooltip.Contains("disk"), "The Moon supports Slew and explains why stellar centering is unavailable");
            var moon = FocusTargetPlanner.SolarSystemTargets.Single(t => t.SolarBody == NOVAS.Body.Moon);
            utc = Enumerable.Range(0, 24).Select(hour => new DateTime(2026, 10, 9, hour, 0, 0, DateTimeKind.Utc))
                .First(time => FocusTargetPlanner.At(moon, 60, 0, 100, time).Altitude > 15);
            await vm.RefreshFocusTargetsAsync();
            Coordinates oldPosition = vm.SelectedFocusTarget.Coordinates;
            guiderInfo.Connected = true; stopStarted = new(); finishStop = new();
            var move = Move(false); await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.SelectedTargetKind = FocusTargetKind.DeepSky; vm.FocusTargetQuery = "M31"; vm.SelectedFocusTarget = dsos[0];
            check(!vm.CanEditTargets && vm.SelectedTargetKind == FocusTargetKind.SolarSystem && vm.FocusTargetQuery == "" && vm.SelectedFocusTarget.SolarBody == NOVAS.Body.Moon,
                "An active GOTO locks category, query and selected target");
            finishStop.SetResult(true);
            int moveResult = await move;
            double oldPositionDistance = Math.Abs((movedTo - oldPosition).Distance.ArcSeconds);
            var refreshedPosition = FocusTargetPlanner.At(moon, 60, 0, 100, utc).Coordinates;
            // The host's angular-distance calculation can return NaN for identical
            // coordinates due to floating-point rounding. Compare RA/Dec directly.
            double raDifference = Math.Abs(movedTo.RA - refreshedPosition.RA);
            raDifference = Math.Min(raDifference, 24 - raDifference) * 15 * 3600;
            double decDifference = Math.Abs(movedTo.Dec - refreshedPosition.Dec) * 3600;
            check(moveResult == 1 && centers == 1 && oldPositionDistance > 20 && raDifference < .01 && decDifference < .01,
                "Moon Slew recalculates coordinates after guiding stops, immediately before movement");
            check(vm.CanEditTargets && owner == null && reservations == 2, "Target editing and capture ownership recover after the slew");
            guiderInfo.Connected = false;
            vm.SelectedTargetKind = FocusTargetKind.DeepSky; await Pending();
            catalogue.Search = (_, _, _) => throw new InvalidOperationException("synthetic catalogue failure");
            bool searchFailed = false;
            try { await vm.RefreshFocusTargetsAsync(); } catch (InvalidOperationException) { searchFailed = true; }
            check(searchFailed && vm.SelectedFocusTarget == null && !vm.IsSearchingTargets && vm.FocusTargetStatus.Contains("Could not load"),
                "Catalogue failure clears stale targets and releases the search busy state");
            catalogue.Search = DefaultSearch; await vm.RefreshFocusTargetsAsync();
            check(vm.FocusTargets.Count == 3, "Refresh can recover after a catalogue failure");
            await VerifyUi(vm, check);
            var disposingStarted = new TaskCompletionSource<bool>();
            var finishDisposed = new TaskCompletionSource<IReadOnlyList<FocusStarSuggestion>>();
            catalogue.Search = (_, _, _) => { disposingStarted.TrySetResult(true); return finishDisposed.Task; };
            var request = vm.RefreshFocusTargetsAsync(); await disposingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.Dispose(); finishDisposed.SetResult(dsos); await request;
            check(vm.FocusTargets.Count == 0 && !vm.SlewFocusTargetCommand.CanExecute(null), "Disposal cancels a pending lookup and ignores late results");
        }
    }

    private sealed class PluginOptionsPreview
    {
        public bool UseFocusStreaming { get; set; } = true;
        public bool EnableFocusDiagnostics { get; set; }
    }

    private static void RenderPluginOptions()
    {
        var resources = new ResourceDictionary { Source = new Uri("/Cwseo.NINA.ManualFocuser;component/Options.xaml", UriKind.Relative) };
        var template = (DataTemplate)resources["Manual Focuser_Options"];
        foreach (int width in new[] { 350, 480 })
        {
            var content = (FrameworkElement)template.LoadContent();
            content.DataContext = new PluginOptionsPreview();
            var surface = new Border { Background = (System.Windows.Media.Brush)Application.Current.Resources["BackgroundBrush"], Child = content };
            surface.Measure(new Size(width, 180)); surface.Arrange(new Rect(0, 0, width, 180)); surface.UpdateLayout();
            surface.Dispatcher.Invoke(() => {}, DispatcherPriority.DataBind); surface.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, 180, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create($"bin/plugin-options-{width}.png"); png.Save(file);
        }
    }

    private static async Task VerifyUi(ManualFocuserDockableVM vm, Action<bool, string> check)
    {
        Application.Current.Resources["ProfileService"] = new { ActiveProfile = new { ColorSchemaSettings = new { ColorSchema = new {
            PrimaryColor = System.Windows.Media.Colors.White, SecondaryColor = System.Windows.Media.Colors.Gray,
            SecondaryBackgroundColor = System.Windows.Media.Color.FromRgb(90,96,104),
            BackgroundColor = System.Windows.Media.Color.FromRgb(27,29,32), BorderColor = System.Windows.Media.Colors.Gray,
            ButtonBackgroundColor = System.Windows.Media.Color.FromRgb(50,54,60), ButtonBackgroundSelectedColor = System.Windows.Media.Color.FromRgb(71,81,96),
            ButtonForegroundColor = System.Windows.Media.Colors.White,
            ButtonForegroundDisabledColor = System.Windows.Media.Colors.Gray,
            TertiaryBackgroundColor = System.Windows.Media.Color.FromRgb(27,29,32) } } } };
        foreach (string name in new[] { "Brushes", "SVGDictionary", "Converters" })
            Application.Current.Resources.MergedDictionaries.Add(new NINA.WPF.Base.Utility.SharedResourceDictionary { Source = new Uri($"/NINA.WPF.Base;component/Resources/StaticResources/{name}.xaml", UriKind.Relative) });
        _ = Application.Current.FindResource("PrimaryBrush");
        _ = Application.Current.FindResource("BorderBrush");
        // PluginLoader composes exported dictionaries and merges them into the
        // application's resources on a worker, after host UI resources exist.
        // Top-level shared brushes/styles must not cross that thread boundary.
        var exports = typeof(ManualFocuserDockableVM).Assembly.GetTypes().Where(t =>
            t.GetCustomAttributes<System.ComponentModel.Composition.ExportAttribute>()
                .Any(e => e.ContractType == typeof(ResourceDictionary))).ToArray();
        using (var composition = new System.ComponentModel.Composition.Hosting.CompositionContainer(
            new System.ComponentModel.Composition.Hosting.TypeCatalog(exports)))
        {
            var loading = Task.Run(() => {
                var dictionaries = composition.GetExportedValues<ResourceDictionary>().ToArray();
                foreach (var dictionary in dictionaries) Application.Current.Resources.MergedDictionaries.Add(dictionary);
                return dictionaries.Length;
            });
            check(loading.Wait(TimeSpan.FromSeconds(10)), "Exported plugin resources load without waiting for the UI dispatcher");
            check(await loading == exports.Length && exports.Length > 0,
                "Every exported resource dictionary composes and merges on the host loader's background thread");
        }
        RenderPluginOptions();
        var view = new FocusControlsView { DataContext = vm };
        ((Expander)view.FindName("SetupExpander")).IsExpanded = true;
        foreach (int width in new[] { 350, 650, 950 })
        {
            view.Measure(new Size(width, 700)); view.Arrange(new Rect(0,0,width,700)); view.UpdateLayout();
            view.Dispatcher.Invoke(() => {}, DispatcherPriority.DataBind); view.UpdateLayout();
            var category = (ComboBox)view.FindName("TargetCategoryPicker");
            var search = (TextBox)view.FindName("TargetSearchInput");
            var card = (FrameworkElement)view.FindName("StarSetupCard");
            var categoryBounds = category.TransformToAncestor(card).TransformBounds(new Rect(category.RenderSize));
            var searchBounds = search.TransformToAncestor(card).TransformBounds(new Rect(search.RenderSize));
            check(category.Items.Count == 3 && category.ActualWidth >= 96 && search.ActualWidth >= 100 &&
                categoryBounds.Right <= searchBounds.Left && searchBounds.Right <= card.ActualWidth,
                "Category and search controls fit without overlap at width " + width);
            check(((TextBox)view.FindName("MaximumMagnitudeInput")).IsVisible == false &&
                ((ComboBox)view.FindName("FocusStarPicker")).Items.Count == 3,
                "Deep sky hides star filters and shares the target list at width " + width);
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap(width, 700, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(view);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var file = File.Create($"bin/live-focus-targets-{width}.png"); encoder.Save(file);
        }
        var picker = (ComboBox)view.FindName("TargetCategoryPicker");
        var input = (TextBox)view.FindName("TargetSearchInput");
        foreach (var kind in new[] { FocusTargetKind.Stars, FocusTargetKind.SolarSystem, FocusTargetKind.DeepSky })
        {
            picker.SelectedItem = kind;
            await ((Task<int>)typeof(ManualFocuserDockableVM).GetField("pendingTargetSearch", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(vm));
            view.Dispatcher.Invoke(() => {}, DispatcherPriority.DataBind); view.UpdateLayout();
            var filters = (FrameworkElement)((FrameworkElement)((TextBox)view.FindName("MaximumMagnitudeInput")).Parent).Parent;
            check(vm.SelectedTargetKind == kind && filters.Visibility == (kind == FocusTargetKind.Stars ? Visibility.Visible : Visibility.Collapsed) &&
                ((FrameworkElement)view.FindName("TargetSearchPanel")).Visibility == (kind == FocusTargetKind.DeepSky ? Visibility.Visible : Visibility.Collapsed),
                kind + " category shows search only for Deep sky and Alt/Mag only for Stars");
        }
        input.Text = "M31";
        await ((Task<int>)typeof(ManualFocuserDockableVM).GetField("pendingTargetSearch", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(vm));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(vm.FocusTargetQuery == "M31" && ((ComboBox)view.FindName("FocusStarPicker")).Items.Count == 1,
            "Typing into the visible search box updates the actual target list");
        check(((Button)view.FindName("SlewButton")).IsEnabled && ((Button)view.FindName("GotoButton")).IsEnabled,
            "Shared Slew and Center buttons become enabled after visible target search completes");
        var imageBytes=new byte[1024*768];
        for(int y=0;y<768;y++) for(int x=0;x<1024;x++) imageBytes[y*1024+x]=(byte)(25+(x*17+y*11)%15);
        for(int i=0;i<70;i++) {
            int sx=20+(i*197)%980,sy=20+(i*113)%720;
            for(int dy=-3;dy<=3;dy++) for(int dx=-3;dx<=3;dx++) imageBytes[(sy+dy)*1024+sx+dx]=(byte)(35+210*Math.Exp(-(dx*dx+dy*dy)/3.0));
        }
        var annotationFrame=new FocusRawFrame(imageBytes.Select(b=>(ushort)(b*200)).ToArray(),1024,0,0,1024,768);
        var previewBitmap=typeof(ManualFocuserDockableVM).GetMethod("RenderRawFocusPreview",BindingFlags.Instance|BindingFlags.NonPublic)
            .Invoke(vm,new object[]{annotationFrame,null,null});
        vm.ShowInNinaImage=true;
        check(!vm.ShowInNinaImage,"Private live workspace cannot publish bitmap-only images to the host");
        typeof(ManualFocuserDockableVM).GetProperty("FocusPreviewImage").SetValue(vm,previewBitmap);
        typeof(ManualFocuserDockableVM).GetProperty("LiveGraphPoints").SetValue(vm,Enumerable.Range(0,120).Select(i=>new OxyPlot.DataPoint(i,3+.4*Math.Sin(i*.13))).ToArray());
        vm.FocusMode = "Live";
        vm.ShowHfrStarBoxes=true;
        typeof(ManualFocuserDockableVM).GetMethod("PublishStarMeasurement",BindingFlags.Instance|BindingFlags.NonPublic)
            .Invoke(vm,new object[]{new FocusHfrMeasurement(4,.2,70){Width=1024,Height=768,SourceFrameId=annotationFrame.FrameId,
                Generation=(int)typeof(ManualFocuserDockableVM).GetField("hfrGeneration",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(vm),
                StarSamples=Enumerable.Range(0,70).Select(i=>new FocusStarSample(20+(i*197)%980,20+(i*113)%720,4)).ToArray()}});
        view.DataContext = null;
        view.DataContext = vm;
        var imageView = new ManualFocuserDockableView { DataContext = vm };
        var historyView = new LiveFocusHistoryView { DataContext = vm };
        var layout = new Grid { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(27,29,32)) };
        layout.ColumnDefinitions.Add(new ColumnDefinition()); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
        layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(180) });
        Grid.SetColumn(view,1); Grid.SetRowSpan(view,2); Grid.SetRow(historyView,1);
        layout.Children.Add(imageView);layout.Children.Add(view);layout.Children.Add(historyView);
        layout.Measure(new Size(1725,1000)); layout.Arrange(new Rect(0,0,1725,1000)); layout.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);layout.UpdateLayout();
        var workspace=(Grid)imageView.FindName("PreviewWorkspace");
        var controls=(StackPanel)view.FindName("ControlsPanel");
        check(workspace.ActualWidth>1200 && controls.ActualWidth<400,"Independent image and control views fit the requested layout");
        check(((FrameworkElement)view.FindName("LiveCameraControls")).Visibility==Visibility.Visible &&
            ((FrameworkElement)view.FindName("RoiControls")).Visibility==Visibility.Visible &&
            ((FrameworkElement)view.FindName("ManualCaptureControls")).Visibility==Visibility.Collapsed,"Live mode shows camera/ROI controls and hides manual capture controls");
        check(historyView.ActualHeight>=150,"Independent HFR panel fits below the image");
        var nativeViewer=imageView.FindName("FocusImageView") as NINA.WPF.Base.View.ImageView ??
            ((Grid)imageView.FindName("PreviewCard")).Children.OfType<NINA.WPF.Base.View.ImageView>().Single();
        var annotationView=(FocusStarOverlay)((FocusRoiOverlay)nativeViewer.ImageAreaContent).FindName("StarBoxes");
        check(annotationView.Boxes.Length==70 && annotationView.ActualWidth==1024 && annotationView.ActualHeight==768,
            "Actual MF Image paints shared measurement boxes on the full pixel-space overlay");
        var nativeZoom=(System.Windows.Media.ScaleTransform)nativeViewer.FindName("PART_ScaleTransform");
        nativeZoom.ScaleX=.5; nativeZoom.ScaleY=.5;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(annotationView.Zoom==.5,"Star-box stroke follows the native zoom without moving measured pixel coordinates");
        nativeZoom.ScaleX=1; nativeZoom.ScaleY=1;
        check(typeof(ManualFocuserDockableVM).GetField("ninaHfrHistory",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(vm)==null,"Private history does not replace native captured-image history");
        var screenshot = new System.Windows.Media.Imaging.RenderTargetBitmap(1725,1000,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        screenshot.Render(layout);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(screenshot));
        using var output = File.Create("bin/private-live-workspace.png"); png.Save(output);
        var positionHistory = (PositionHfrHistory)typeof(ManualFocuserDockableVM).GetField("positionHistory",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(vm);
        for (int i=0;i<11;i++) positionHistory.Record(9800+i*40,2.2+Math.Pow((i-5)*.3,2));
        vm.FocusMode = "Manual";
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);layout.UpdateLayout();
        check(((FrameworkElement)view.FindName("LiveCameraControls")).Visibility==Visibility.Collapsed &&
            ((FrameworkElement)view.FindName("RoiControls")).Visibility==Visibility.Collapsed &&
            ((FrameworkElement)view.FindName("ManualCaptureControls")).Visibility==Visibility.Visible,"Manual mode hides live camera/ROI controls and exposes capture-after-move workflow");
        var captureToggle=(System.Windows.Controls.Primitives.ToggleButton)view.FindName("CaptureAfterMoveToggle");
        bool captureAfterMove=vm.TakeShootAfterMove;
        try {
            captureToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,!captureAfterMove);
            check(vm.TakeShootAfterMove==!captureAfterMove && view.FindName("ManualCaptureButton")==null,
                "Manual capture-after-move toggle controls the real setting without a separate Capture button");
        } finally { vm.TakeShootAfterMove=captureAfterMove; }
        var historyPlot = (OxyPlot.Wpf.Plot)historyView.FindName("HistoryPlot");
        var bottomAxis = historyPlot.Axes.Single(a=>a.Position==OxyPlot.Axes.AxisPosition.Bottom);
        check(bottomAxis.Title=="Focus position (steps)" && double.IsNaN(bottomAxis.Minimum) && double.IsNaN(bottomAxis.Maximum),"Visible history switches from fixed time to automatic position range");
        var movement=historyPlot.Annotations.OfType<OxyPlot.Wpf.ArrowAnnotation>().Single();
        var minimum=historyPlot.Annotations.OfType<OxyPlot.Wpf.PointAnnotation>().Single();
        check(movement.StartPoint.X==10160 && movement.EndPoint.X==10200,"Manual arrow points from the previous measured focus position to the latest one");
        check(minimum.X==10000 && minimum.Y==2.2 && minimum.Shape==OxyPlot.MarkerType.Triangle && minimum.Fill==System.Windows.Media.Colors.Red && minimum.Text.Contains("10000"),
            "Manual minimum HFR has a red triangle and its actual focus position label");
        var manualShot = new System.Windows.Media.Imaging.RenderTargetBitmap(1725,1000,96,96,System.Windows.Media.PixelFormats.Pbgra32);manualShot.Render(layout);
        var manualPng = new System.Windows.Media.Imaging.PngBitmapEncoder();manualPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(manualShot));
        using var manualOutput = File.Create("bin/private-manual-workspace.png");manualPng.Save(manualOutput);
        var model=((OxyPlot.IPlotView)historyPlot).ActualModel;
        check(model.Annotations.OfType<OxyPlot.Annotations.ArrowAnnotation>().Count()==1 && model.Annotations.OfType<OxyPlot.Annotations.PointAnnotation>().Count()==1,
            "The rendered OxyPlot model includes both manual history annotations");
        vm.ClearLiveGraphCommand.Execute(null);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);layout.UpdateLayout();
        check(historyPlot.Annotations.Count==0 && vm.FocusGraphPoints.Length==0,"Clear removes both the measurements and manual arrow/minimum labels");
        vm.FocusMode="Live";
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);layout.UpdateLayout();
        check(historyPlot.Annotations.Count==0,"Live history never displays manual position annotations");
        vm.SettingsVisible=true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); layout.UpdateLayout();
        var settingsPanel=(ScrollViewer)view.FindName("FocusSettingsPanel");
        check(controls.Visibility==Visibility.Collapsed && settingsPanel.Visibility==Visibility.Visible && settingsPanel.ActualHeight>0,
            "Title-bar settings replace the normal Controls content without reserving permanent screen space");
        var settingsShot=new System.Windows.Media.Imaging.RenderTargetBitmap(1725,1000,96,96,System.Windows.Media.PixelFormats.Pbgra32);settingsShot.Render(layout);
        var settingsPng=new System.Windows.Media.Imaging.PngBitmapEncoder();settingsPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(settingsShot));
        using(var settingsOutput=File.Create("bin/private-focus-settings.png"))settingsPng.Save(settingsOutput);
        vm.CloseFocusSettingsCommand.Execute(null);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); layout.UpdateLayout();
        check(controls.Visibility==Visibility.Visible && settingsPanel.Visibility==Visibility.Collapsed,
            "Done restores the capture and target controls and removes the settings panel from layout");
    }
}
