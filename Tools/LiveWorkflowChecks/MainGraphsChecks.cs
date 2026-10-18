using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cwseo.NINA.ManualFocuser.Dockables;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.Image.ImageData;
using NINA.Profile.Interfaces;
using NINA.View;
using NINA.ViewModel.ImageHistory;
using NINA.WPF.Base.Interfaces.Mediator;
using Plot = OxyPlot.Wpf.Plot;
using LineSeries = OxyPlot.Wpf.LineSeries;

internal static partial class PreviewDisplayChecks
{
    private static ImageHistoryVM CreateNativeHistory()
    {
        var settings = Fake.Properties<IImageHistorySettings>(new() {
            ["ImageHistoryLeftSelected"] = ImageHistoryEnum.HFR,
            ["ImageHistoryRightSelected"] = ImageHistoryEnum.Stars });
        var profile = Fake.Of<IProfile>((m,a) => m.Name == "get_ImageHistorySettings" ? settings : Fake.Unexpected(m));
        var profiles = Fake.Of<IProfileService>((m,a) => m.Name == "get_ActiveProfile" ? profile :
            m.Name.StartsWith("add_") || m.Name.StartsWith("remove_") ? null : Fake.Unexpected(m));
        var saves = Fake.Of<IImageSaveMediator>((m,a) => m.Name.StartsWith("add_") || m.Name.StartsWith("remove_") ? null : Fake.Unexpected(m));
        return new ImageHistoryVM(profiles,saves);
    }
    private static void AddNativeCapture(ImageHistoryVM history, int id)
    {
        history.Add(id,CaptureSequence.ImageTypes.LIGHT);
        var metadata = new ImageMetaData(); metadata.Image.Id = id;
        history.AppendImageProperties(new ImageSavedEventArgs {MetaData = metadata, Filter = "L", Duration = 2,
            StarDetectionAnalysis = Fake.Of<IStarDetectionAnalysis>((m,a) => m.Name switch {
                "get_HFR" => 2.5, "get_DetectedStars" => 40, _ => Fake.Unexpected(m) }) });
    }
    private static void LiveState(ManualFocuserDockableVM vm, bool running)
    {
        var type = typeof(ManualFocuserDockableVM);
        foreach (string field in new[] {"assistRunning","liveImageOutputActive"})
            type.GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(vm,running);
        type.GetMethod("UpdateNinaHfrHistory",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(vm,null);
    }
    private static object HistoryOutput(ManualFocuserDockableVM vm) =>
        typeof(ManualFocuserDockableVM).GetField("ninaHfrHistory",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(vm);
    private static bool AttachHistory(ManualFocuserDockableVM vm, DependencyObject root)
    {
        var manager = HistoryOutput(vm);
        return manager != null && (bool)manager.GetType().GetMethod("TryAttach",BindingFlags.Instance|BindingFlags.NonPublic)
            .Invoke(manager,new object[] {root});
    }
    private static async Task VerifyMainGraphs(ManualFocuserDockableVM vm, Action<bool,string> check)
    {
        var history = CreateNativeHistory(); AddNativeCapture(history,1);
        var view = new AnchorableImageHistoryView {DataContext = history};
        var root = new Grid {Background = new SolidColorBrush(Color.FromRgb(27,29,32))}; root.Children.Add(view);
        using var surface = new HwndSource(new HwndSourceParameters("Native HFR History verification") {
            Width = 650, Height = 320, WindowStyle = unchecked((int)0x80000000) });
        surface.RootVisual = root;
        async Task Layout() {root.Measure(new Size(650,320));root.Arrange(new Rect(0,0,650,320));root.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);root.UpdateLayout();}
        await Layout();
        var plot = Visuals(view).OfType<Plot>().Single();
        var originalSeries = plot.Series.ToArray(); var originalAxes = plot.Axes.ToArray();
        var seriesBinding = BindingOperations.GetBindingBase(originalSeries[0],LineSeries.ItemsSourceProperty);
        var grid = (Grid)((ScrollViewer)view.Content).Content;
        int rows = grid.RowDefinitions.Count; var children = grid.Children.Cast<UIElement>().ToArray();
        var clear = Visuals(view).OfType<Button>().Single(b => ReferenceEquals(b.Command,history.PlotClearCommand));
        var save = Visuals(view).OfType<Button>().Single(b => ReferenceEquals(b.Command,history.PlotSaveCommand));
        var clearBinding = BindingOperations.GetBindingBase(clear,Button.CommandProperty);
        var saveEnabled = save.ReadLocalValue(UIElement.IsEnabledProperty);
        vm.ShowLiveGraphs = false; vm.ShowInNinaImage = true;
        check(HistoryOutput(vm) == null && plot.Series.SequenceEqual(originalSeries), "Idle focus leaves native captured-image HFR History unchanged");
        LiveState(vm,true);
        view.DataContext = new object();
        check(!AttachHistory(vm,root), "An unrelated plot cannot receive live HFR measurements");
        view.DataContext = history; await Layout();
        view.Visibility = Visibility.Collapsed;
        check(!AttachHistory(vm,root), "Hidden native history does not receive a live trace");
        view.Visibility = Visibility.Visible; await Layout();
        check(AttachHistory(vm,root), "Live focus reuses the real NINA HFR History panel with no Graphs toggle");
        await Layout();
        var live = plot.Series.OfType<LineSeries>().Single();
        check(live.Title == "Live Focus HFR (ROI)" && ReferenceEquals(plot,Visuals(view).OfType<Plot>().Single()),
            "The existing plot clearly labels local ROI HFR and is not replaced");
        check(grid.RowDefinitions.Count == rows && grid.Children.Cast<UIElement>().SequenceEqual(children),
            "Native dock, settings, rows and children stay in place without an extra Image footer");
        var x = plot.Axes.Single(a => a.Position == OxyPlot.Axes.AxisPosition.Bottom);
        check(x.Minimum == -120 && x.Maximum == 0 && !x.IsPanEnabled && !x.IsZoomEnabled,
            "Live native history uses a fixed 120-second window with the latest sample on the right");
        var record = typeof(ManualFocuserDockableVM).GetMethod("RecordLiveHfr",BindingFlags.Instance|BindingFlags.NonPublic);
        vm.ClearLiveGraphCommand.Execute(null);
        for(int i=0;i<=700;i++) record.Invoke(vm,new object[] {i*.25,2+.4*Math.Sin(i*.07)});
        await Layout();
        check(ReferenceEquals(live.ItemsSource,vm.LiveGraphPoints) && vm.LiveGraphPoints[^1].X == 0 && vm.LiveGraphPoints[0].X >= -120,
            "Several minutes of measurements scroll in the existing host plot instead of accumulating stale ranges");
        check(history.ImageHistory.Count == 1 && history.ObservableImageHistory.Count == 1 && history.AutoFocusPoints.Count == 0 && history.ImageHistory[0].HFR == 2.5,
            "Live measurements leave captured-image history, HFR and autofocus baselines untouched");
        check(ReferenceEquals(clear.Command,vm.ClearLiveGraphCommand) && !save.IsEnabled,
            "Native Clear affects the live trace and captured-image CSV export is disabled during live focus");
        clear.Command.Execute(null); await Layout();
        check(vm.LiveGraphPoints.Length == 0 && history.ObservableImageHistory.Count == 1,
            "Clear during live focus preserves the original saved-image history");
        for(int i=0;i<=100;i++) record.Invoke(vm,new object[] {(double)i,3+.35*Math.Sin(i*.18)});
        record.Invoke(vm,new object[] {100.25,double.NaN}); record.Invoke(vm,new object[] {100.5,2.9});
        await Layout();
        check(vm.LiveGraphPoints.Any(p=>double.IsNaN(p.Y)), "Lost star detection leaves a gap in native live history");
        var shot = new RenderTargetBitmap(650,320,96,96,PixelFormats.Pbgra32);shot.Render(root);
        var pixels = new byte[650*320*4]; shot.CopyPixels(pixels,650*4,0);
        check(Enumerable.Range(0,650*320).Count(i=>pixels[i*4+2]>180 && pixels[i*4+1]>140 && pixels[i*4]<130)>100,
            "The real native history panel renders the yellow live HFR trace");
        var png = new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(shot));
        using(var file=File.Create("bin/live-focus-native-hfr-history.png")) png.Save(file);
        vm.ShowInNinaImage = false; vm.ShowLiveGraphs = true; vm.ShowLiveGraphs = false;
        check(ReferenceEquals(plot.Series.Single(),live), "Native history works with local or main image output independently of the star-profile toggle");
        history.ImageHistoryLeftSelected = ImageHistoryEnum.Median; await Layout();
        check(((LineSeries)originalSeries[0]).DataFieldY == "Median" && live.DataFieldY == "Y",
            "Native statistic preferences remain live bindings without changing the ROI trace");
        // The compact shared level track keeps one neutral gradient surface.
        var controls = new ManualFocuserDockableView {DataContext=vm,Width=350,Height=180};root.Children.Add(controls);await Layout();
        var levels=Visuals(controls).OfType<FocusLevelsView>().Single();
        var track=(FocusLevelsTrack)levels.FindName("LevelsTrack");
        var trackSurface=(Grid)track.Content;
        var gradient=((Border)trackSurface.Children[0]).Background;
        track.MoveHandle(FocusLevelHandle.Midtone,.4);await Layout();
        check(ReferenceEquals(((Border)trackSurface.Children[0]).Background,gradient),
            "Focused level adjustment keeps the shared gradient track unchanged");
        vm.ResetPreviewStretchCommand.Execute(null);await Layout();
        check(track.Midtone==.5 && ReferenceEquals(((Border)trackSurface.Children[0]).Background,gradient), "Auto reset keeps the same gradient surface");
        root.Children.Remove(controls);
        typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,true);
        check(plot.Series.SequenceEqual(originalSeries) && plot.Axes.SequenceEqual(originalAxes), "ROI editing restores native captured-image axes and series");
        check(ReferenceEquals(BindingOperations.GetBindingBase(clear,Button.CommandProperty),clearBinding) && ReferenceEquals(clear.Command,history.PlotClearCommand) && save.IsEnabled && save.ReadLocalValue(UIElement.IsEnabledProperty)==saveEnabled,
            "Native Clear binding, CSV enabled state and button values are restored exactly");
        check(ReferenceEquals(BindingOperations.GetBindingBase(originalSeries[0],LineSeries.ItemsSourceProperty),seriesBinding),
            "Original captured-image series retain their ItemsSource bindings");
        typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,false);AttachHistory(vm,root);
        view.Visibility=Visibility.Collapsed;
        var manager=HistoryOutput(vm);manager.GetType().GetMethod("OnTick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(manager,new object[]{null,EventArgs.Empty});
        check(plot.Series.SequenceEqual(originalSeries), "Hiding the native dock restores the normal graph and releases the live attachment");
        view.Visibility=Visibility.Visible;await Layout();check(AttachHistory(vm,root), "The existing native dock can be reattached after reopening");
        vm.StopFocusPreviewCommand.Execute(null);
        check(plot.Series.SequenceEqual(originalSeries) && plot.Axes.SequenceEqual(originalAxes), "Requesting Stop restores native history immediately while the camera drains");
        typeof(ManualFocuserDockableVM).GetField("isStoppingFocusPreview",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(vm,false);
        LiveState(vm,false); vm.PreviewRoiPreset="Inspector";
        LiveState(vm,true);AttachHistory(vm,root);
        check(((LineSeries)plot.Series.Single()).Title=="Live Focus HFR (sensor center)",
            "Inspector native HFR History identifies the sensor center instead of implying nine-tile statistics");
        var foreign=new LineSeries {Title="Other extension"};plot.Series.Add(foreign);
        LiveState(vm,false);
        vm.PreviewRoiPreset="512";
        check(originalSeries.All(s=>plot.Series.Contains(s)) && plot.Series.Contains(foreign) && plot.Series.Count==originalSeries.Length+1,
            "Stopping preserves unrelated graph contributions added by another extension");
        plot.Series.Remove(foreign); AddNativeCapture(history,2);await Layout();
        check(history.ObservableImageHistoryView.Count==2 && ReferenceEquals(((LineSeries)originalSeries[0]).ItemsSource,history.ObservableImageHistoryView),
            "Normal capture history resumes through its original bindings after live focus stops");
        check(HistoryOutput(vm)==null && plot.Series.SequenceEqual(originalSeries) && plot.Axes.SequenceEqual(originalAxes),
            "Ending a run releases the native graph bridge and restores all original objects");
        var profile=new LiveFocusGraphsView {DataContext=vm};
        root.Children.Add(profile);await Layout();
        check(Visuals(profile).OfType<Plot>().Count()==1 && profile.FindName("PreviewFocusPlot")==null,
            "Local preview contains only star profile and no duplicate HFR chart");
        root.Children.Remove(profile);
        vm.ShowLiveGraphs=false;vm.ShowInNinaImage=false;
    }
    private static async Task VerifyGraphVmDisposal(ManualFocuserDockableVM vm, Action<bool,string> check)
    {
        var history=CreateNativeHistory();AddNativeCapture(history,3);
        var view=new AnchorableImageHistoryView {DataContext=history};var root=new Grid();root.Children.Add(view);
        using var surface=new HwndSource(new HwndSourceParameters("Native HFR disposal verification") {
            Width=650,Height=320,WindowStyle=unchecked((int)0x80000000)});
        surface.RootVisual=root;root.Measure(new Size(650,320));root.Arrange(new Rect(0,0,650,320));root.UpdateLayout();
        var plot=Visuals(view).OfType<Plot>().Single();var series=plot.Series.ToArray();var axes=plot.Axes.ToArray();
        LiveState(vm,true);
        check(AttachHistory(vm,root), "VM disposal verification begins with a real attached native HFR History");
        vm.Dispose();await Dispatcher.Yield(DispatcherPriority.Background);
        check(plot.Series.SequenceEqual(series) && plot.Axes.SequenceEqual(axes) && history.ObservableImageHistory.Count==1 && HistoryOutput(vm)==null,
            "VM disposal restores the native graph and history despite the disposed update guard");
    }
}
