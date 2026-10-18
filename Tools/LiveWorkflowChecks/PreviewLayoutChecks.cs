using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Cwseo.NINA.ManualFocuser.Dockables;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFocuser;

internal static partial class PreviewDisplayChecks {
    private static void RenderControls(ManualFocuserDockableVM vm, Action<bool,string> check) {
        var assembly=typeof(ManualFocuserDockableVM).Assembly;
        check(assembly.GetName().Name=="Cwseo.NINA.ManualFocuser" && assembly.GetCustomAttribute<GuidAttribute>().Value=="ae6b70d2-e99d-4931-8d7c-3e89b6aa27b4",
            "Standalone plugin has its own assembly and identity");
        check(!assembly.GetTypes().Any(t=>t.Name.Contains("AutofocusRunner") || t.Name.Contains("LensConfig") || t.Name.Contains("SpikeCore")),
            "Autofocus, lens configuration and spike analysis engines are excluded");
        var settings=assembly.GetType("Cwseo.NINA.ManualFocuser.Properties.Settings");
        var defaults=Activator.CreateInstance(settings,true);
        check((bool)settings.GetProperty("UseFocusStreaming").GetValue(defaults) && !(bool)settings.GetProperty("EnableFocusDiagnostics").GetValue(defaults),
            "New plugin defaults to streaming with diagnostic recording off");
        XNamespace wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var source=XDocument.Load("Dockables/ManualFocuserDockableView.xaml");
        var missing=new List<string>();
        foreach(var attribute in source.Descendants().Attributes().Where(a=>a.Value.StartsWith("{Binding ") && !a.Value.Contains("RelativeSource") && !a.Value.Contains("Source=") && !a.Value.Contains("ElementName=") && !a.Parent.Ancestors(wpf+"DataTemplate").Any())) {
            string property=attribute.Value[9..].Split(new[]{',','}'})[0].Trim().Split('.')[0];
            if(typeof(ManualFocuserDockableVM).GetProperty(property)==null)missing.Add(property);
        }
        check(missing.Count==0,"Refactored UI has no stale view-model bindings: "+string.Join(", ",missing));
        // Supply only the host's theme data, never a real profile service.
        Application.Current.Resources["ProfileService"]=new {ActiveProfile=new {ColorSchemaSettings=new {ColorSchema=new {
            PrimaryColor=Colors.White,SecondaryColor=Colors.Gray,BackgroundColor=Color.FromRgb(27,29,32),
            BorderColor=Color.FromRgb(64,68,73),ButtonBackgroundColor=Color.FromRgb(50,54,60),
            ButtonBackgroundSelectedColor=Color.FromRgb(71,81,96),ButtonForegroundColor=Colors.White
        }}}};
        foreach(string name in new[]{"Brushes","SVGDictionary","Converters"})
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri($"/NINA.WPF.Base;component/Resources/StaticResources/{name}.xaml",UriKind.Relative)});
        var view=new ManualFocuserDockableView {DataContext=vm,Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromRgb(27,29,32))};
        vm.UpdateDeviceInfo(new CameraInfo {Connected=true,DeviceId="Synthetic.UI",XSize=4096,YSize=3072});
        vm.UpdateDeviceInfo(new FocuserInfo {Connected=true,DeviceId="Synthetic.Motor",Position=10000});
        VerifySensorRoiPresets(vm,view,check);
        var star=new Cwseo.NINA.ManualFocuser.LiveWorkflow.Models.FocusStarSuggestion {Name="Example star",Magnitude=2,Altitude=70,Azimuth=135};
        vm.FocusTargets.Add(star);vm.SelectedFocusTarget=star;
        typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.LiveHfr)).SetValue(vm,2.25);
        typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.LiveStarProfile)).SetValue(vm,Enumerable.Range(-32,65).Select(x=>new OxyPlot.DataPoint(x,Math.Exp(-x*x/18.0))).ToArray());
        for(int i=0;i<24;i++)typeof(ManualFocuserDockableVM).GetMethod("RecordLiveHfr",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(vm,new object[]{(double)i,2.25+2*Math.Exp(-i/5.0)+.08*Math.Sin(i)});
        view.DataContext=null;view.DataContext=vm;
        var setup=(Expander)view.FindName("SetupExpander");
        var preview=(FrameworkElement)view.FindName("PreviewSurface");
        var graphs=(FrameworkElement)view.FindName("MeasurementsPanel");
        var workspace=(FrameworkElement)view.FindName("PreviewWorkspace");
        var toggle=(ToggleButton)view.FindName("MeasurementsToggle");
        var metric=(TextBlock)view.FindName("CompactMetric");
        check(!setup.IsExpanded && toggle.IsChecked==false,"Occasional setup and star profile start folded");
        view.Measure(new Size(350,500));view.Arrange(new Rect(0,0,350,500));view.UpdateLayout();
        view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);view.UpdateLayout();
        var cameraIndicator=(FrameworkElement)view.FindName("CameraConnectionIndicator");
        var connectionText=Visuals(cameraIndicator).OfType<TextBlock>().Single();
        var connectionPath=Visuals(cameraIndicator).OfType<System.Windows.Shapes.Path>().Single();
        var connectedGeometry=connectionPath.Data;
        var cameraBefore=vm.CameraInfo;
        vm.UpdateDeviceInfo(new CameraInfo{Connected=false});
        view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);view.UpdateLayout();
        check(connectionText.Text=="Disconnected" && !Equals(connectionPath.Data,connectedGeometry) && cameraIndicator.Visibility==Visibility.Visible,
            "Camera disconnection updates the always-visible label and X indicator");
        vm.UpdateDeviceInfo(cameraBefore);
        view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);view.UpdateLayout();
        check(connectionText.Text=="Connected" && Equals(connectionPath.Data,connectedGeometry),
            "Camera reconnection restores the connected check indicator");
        check(vm.ShowInNinaImage && workspace.Visibility==Visibility.Collapsed && ((FrameworkElement)view.FindName("PreviewCard")).Visibility==Visibility.Collapsed,
            "NINA Image is the default output and the duplicate local view uses no workspace");
        check(((FrameworkElement)view.FindName("EssentialControls")).ActualHeight<202 && metric.Visibility==Visibility.Visible,
            "Image-only mode retains compact ROI, HFR, stretch and motor controls");
        var compactShot=new RenderTargetBitmap(350,220,96,96,PixelFormats.Pbgra32);compactShot.Render(view);
        compactShot.CopyPixels(new byte[350*220*4],350*4,0);
        var compactEncoder=new PngBitmapEncoder();compactEncoder.Frames.Add(BitmapFrame.Create(compactShot));
        using(var file=File.Create("bin/live-focus-image-controls.png"))compactEncoder.Save(file);
        toggle.IsChecked=true;view.UpdateLayout();
        check(graphs.Visibility==Visibility.Collapsed && workspace.Visibility==Visibility.Collapsed && vm.ShowLiveGraphs && toggle.Visibility==Visibility.Collapsed,
            "Main Image output hides the local star-profile toggle and workspace");
        toggle.IsChecked=false;vm.ShowInNinaImage=false;view.UpdateLayout();
        VerifySetupSpace();
        var magnitudeInput=(TextBox)view.FindName("MaximumMagnitudeInput");
        foreach(var size in new[]{(350,500),(350,700),(650,500),(650,700),(950,700)}) {
            int width=size.Item1,height=size.Item2;
            string label=$"{width} x {height}";
            view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));view.UpdateLayout();
            view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);view.UpdateLayout();
            var video=Children(view).OfType<Button>().Single(b=>AutomationProperties.GetName(b)=="Start live focus");
            check(video.Visibility==Visibility.Visible && video.ActualWidth==44,"Start video button stays visible while idle at "+label);
            var step=Children(view).OfType<Button>().Single(b=>Equals(b.Content,"In \u2212"));
            check(step.Visibility==Visibility.Visible && step.ActualWidth>35,"Focuser direction button is readable at "+label);
            check(((SolidColorBrush)step.Foreground).Color.A==255,"Focuser button text uses the visible host theme at "+label);
            var exposure=Children(view).OfType<Slider>().Single(s=>s.Maximum==5000);
            check(exposure.ActualWidth>=30 && exposure.SmallChange==50,"Exposure slider fits with 50ms increments at "+label);
            var levels=Children(view).OfType<FocusLevelsView>().Single();
            check(levels.ActualWidth>=64,"Image Levels popup remains accessible without a duplicate brightness slider at "+label);
            check(preview.ActualHeight>=height-272 && preview.ActualWidth>workspace.ActualWidth*.9 && graphs.Visibility==Visibility.Collapsed && metric.Visibility==Visibility.Visible,
                "Folded layout maximizes the star image and retains HFR at "+label+$" (image {preview.ActualWidth:F0}x{preview.ActualHeight:F0}, workspace {workspace.ActualWidth:F0}, graphs {graphs.Visibility}, metric {metric.Visibility})");
            double fullHeight=preview.ActualHeight,fullWidth=preview.ActualWidth;
            var roi=vm.PreviewRoiRectangle;var selectedStar=vm.SelectedFocusTarget;
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
            var pixels=new byte[width*height*4];bitmap.CopyPixels(pixels,width*4,0);
            check(pixels.Where((v,i)=>i%4==3).Count(v=>v>0)>width*100,"Offscreen screenshot actually contains rendered UI at "+label);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create($"bin/live-focus-{width}-{height}.png"))encoder.Save(file);
            if(height==700) File.Copy($"bin/live-focus-{width}-{height}.png",$"bin/live-focus-{width}.png",true);
            toggle.IsChecked=true;view.UpdateLayout();
            check(graphs.Visibility==Visibility.Visible && (width<616
                ?graphs.TranslatePoint(new Point(),view).Y>=preview.TranslatePoint(new Point(),view).Y+preview.ActualHeight
                :graphs.TranslatePoint(new Point(),view).X>preview.TranslatePoint(new Point(),view).X),
                "Optional star profile appears below narrow images or beside wide images at "+label);
            toggle.IsChecked=false;setup.IsExpanded=true;view.UpdateLayout();
            if(width==350 && height==500) {
                var sync=(ToggleButton)view.FindName("SyncMountToggle");
                sync.IsChecked=true;
                check(vm.SyncMountOnCentering,"The visible Sync mount switch updates the shared profile setting");
                sync.IsChecked=false;
                view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);
                magnitudeInput.Text="1.5";magnitudeInput.GetBindingExpression(TextBox.TextProperty).UpdateSource();
                check(vm.MaximumFocusMagnitude==1.5,"Magnitude input commits the user's brightness limit");
                vm.MaximumFocusMagnitude=4;view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);
                check(view.FindName("MainImageOutputToggle")==null && view.FindName("RoiSetupCard")==null,
                    "Setup contains only Go to target and no display or absolute-position card");
                view.UpdateLayout();
            }
            check(preview.ActualHeight<fullHeight && preview.ActualHeight>=180 && ((Button)view.FindName("EditRoiButton")).ActualHeight>=28,
                "Expanded setup leaves a usable image and exposes ROI controls at "+label+$" (image {preview.ActualHeight}, full {fullHeight}, ROI {((Button)view.FindName("EditRoiButton")).ActualHeight})");
            var starCard=(FrameworkElement)view.FindName("StarSetupCard");
            var altitudeInput=(TextBox)view.FindName("MinimumAltitudeInput");
            var magnitudeBounds=magnitudeInput.TransformToAncestor(starCard).TransformBounds(new Rect(magnitudeInput.RenderSize));
            check(altitudeInput.ActualWidth>=40 && magnitudeInput.ActualWidth>=48 && magnitudeBounds.Left>=0 && magnitudeBounds.Right<=starCard.ActualWidth,
                "Altitude and magnitude filters fit together in compact star setup at "+label);
            var category=(ComboBox)view.FindName("TargetCategoryPicker");
            var categoryBounds=category.TransformToAncestor(starCard).TransformBounds(new Rect(category.RenderSize));
            var altitudeBounds=altitudeInput.TransformToAncestor(starCard).TransformBounds(new Rect(altitudeInput.RenderSize));
            check(((FrameworkElement)view.FindName("TargetSearchPanel")).Visibility==Visibility.Collapsed &&
                altitudeBounds.Left>=categoryBounds.Right && Math.Abs(altitudeBounds.Top-magnitudeBounds.Top)<2 &&
                Math.Abs(categoryBounds.Top+categoryBounds.Height/2-altitudeBounds.Top-altitudeBounds.Height/2)<2,
                "Stars show Alt and Mag on the category row in place of name search at "+label);
            check(starCard.ActualWidth>=((ScrollViewer)view.FindName("ControlsScrollViewer")).ActualWidth-22,
                "Go to target uses the full setup width without an empty second column at "+label);
            var controlsScroll=(ScrollViewer)view.FindName("ControlsScrollViewer");
            var videoY=video.TranslatePoint(new Point(),view).Y;
            controlsScroll.ScrollToEnd();view.UpdateLayout();
            check(video.TranslatePoint(new Point(),view).Y==videoY && videoY<30 && setup.TranslatePoint(new Point(),view).Y<212,
                "Only setup scrolls; live controls and fold header remain visible at "+label);
            controlsScroll.ScrollToTop();view.UpdateLayout();
            if(width==650 && height==700) {
                var starShot=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);starShot.Render(view);
                starShot.CopyPixels(new byte[width*height*4],width*4,0);
                var starEncoder=new PngBitmapEncoder();starEncoder.Frames.Add(BitmapFrame.Create(starShot));
                using(var file=File.Create("bin/live-focus-star-filters.png"))starEncoder.Save(file);
            }
            if(width==350 && height==500) {
                var setupShot=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);setupShot.Render(view);
                setupShot.CopyPixels(new byte[width*height*4],width*4,0);
                var setupEncoder=new PngBitmapEncoder();setupEncoder.Frames.Add(BitmapFrame.Create(setupShot));
                using(var file=File.Create("bin/live-focus-setup.png"))setupEncoder.Save(file);
            }
            setup.IsExpanded=false;view.UpdateLayout();
            check(preview.ActualHeight==fullHeight && preview.ActualWidth==fullWidth && vm.PreviewRoiRectangle.Equals(roi) && vm.SelectedFocusTarget==selectedStar && vm.LiveHfrPoints.Count==24,
                "Folding restores image space without losing ROI, star or measurements at "+label);
        }
        var vmType=typeof(ManualFocuserDockableVM);
        void State(string field,object value){vmType.GetField(field,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(vm,value);view.DataContext=null;view.DataContext=vm;view.UpdateLayout();view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);view.UpdateLayout();}
        var button=(Button)view.FindName("LiveVideoButton");
        State("assistRunning",true);
        check(button.Command==vm.StopFocusPreviewCommand && AutomationProperties.GetName(button)=="Stop live focus","The same video button becomes Stop during preview");
        CheckIcon("cancel");
        State("isStoppingFocusPreview",true);
        check(!button.IsEnabled && AutomationProperties.GetName(button)=="Stopping live focus","Stop waiting shows a disabled pending state");
        CheckIcon("hourglass");
        State("isStoppingFocusPreview",false);State("assistRunning",false);
        State("isGoingToFocusTarget",true);
        var cancelGoto=(Button)view.FindName("CancelGotoButton");
        check(!setup.IsExpanded && cancelGoto.Visibility==Visibility.Visible && cancelGoto.ActualWidth>50 && cancelGoto.Command==vm.CancelGotoCommand,
            "GOTO cancellation stays available while setup is folded");
        setup.IsExpanded=true;view.UpdateLayout();
        var go=(Button)view.FindName("GotoButton");
        var slew=(Button)view.FindName("SlewButton");
        check(go.Command==vm.GotoFocusTargetCommand && Equals(go.Content,"Slew + Center") && slew.Command==vm.SlewFocusTargetCommand && Equals(slew.Content,"Slew"),
            "Slew and Slew + Center remain distinct buttons while the separate cancellation action is available");
        State("isGoingToFocusTarget",false);
        setup.IsExpanded=false;
        vmType.GetProperty(nameof(vm.IsMoving)).SetValue(vm,true);view.UpdateLayout();
        var halt=(Button)view.FindName("HaltMoveButton");
        check(halt.Visibility==Visibility.Visible && halt.ActualWidth>35 && halt.Command==vm.HaltFocuserCommand,"Motor Stop remains available with setup folded");
        vmType.GetProperty(nameof(vm.IsMoving)).SetValue(vm,false);
        setup.IsExpanded=true;toggle.IsChecked=true;vm.ShowInNinaImage=true;
        vmType.GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,true);view.UpdateLayout();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        view.Dispatcher.Invoke(()=>{},DispatcherPriority.Background);view.UpdateLayout();
        var surface=(FrameworkElement)view.FindName("PreviewSurface");
        check(!setup.IsExpanded && ((Button)view.FindName("ConfirmRoiButton")).IsEnabled && ((Button)view.FindName("ConfirmRoiButton")).ActualWidth>35 && ((Button)view.FindName("RetakeRoiButton")).ActualWidth>35,
            "ROI editing folds setup and keeps Retake and Done beside the image");
        check(((FrameworkElement)view.FindName("MeasurementsPanel")).Visibility==Visibility.Collapsed && surface.ActualWidth>workspace.ActualWidth*.9,
            "ROI editing uses the full workspace width and hides plots");
        var outline=(System.Windows.Shapes.Rectangle)view.FindName("RoiOutline");
        var labels=((Canvas)view.FindName("RoiHandles")).Children.OfType<Border>().Where(b=>b.Child is TextBlock).ToArray();
        check(outline.Visibility==Visibility.Visible && labels.Length==1 && ((TextBlock)labels[0].Child).FontSize==13 && Canvas.GetTop(labels[0])+labels[0].DesiredSize.Height<=Canvas.GetTop(outline),
            "Yellow ROI is visible with fixed-size text outside its top edge");
        var roiShot=new RenderTargetBitmap(950,700,96,96,PixelFormats.Pbgra32);roiShot.Render(view);
        roiShot.CopyPixels(new byte[950*700*4],950*4,0);
        var roiEncoder=new PngBitmapEncoder();roiEncoder.Frames.Add(BitmapFrame.Create(roiShot));
        using(var file=File.Create("bin/live-focus-roi.png"))roiEncoder.Save(file);
        vmType.GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,false);view.UpdateLayout();
        check(toggle.IsChecked==true && vm.ShowLiveGraphs && graphs.Visibility==Visibility.Collapsed && ((FrameworkElement)view.FindName("PreviewCard")).Visibility==Visibility.Collapsed,
            "Leaving ROI editing hides the local profile and temporary editor in main Image mode");
        toggle.IsChecked=false;vm.ShowInNinaImage=false;
        var exposureSlider=(Slider)view.FindName("ExposureSlider");
        vm.PreviewExposureMs=250;view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);
        Slider.IncreaseSmall.Execute(null,exposureSlider);check(vm.PreviewExposureMs==300,"Exposure keyboard step increases by exactly 50 ms");
        Slider.DecreaseSmall.Execute(null,exposureSlider);check(vm.PreviewExposureMs==250,"Exposure keyboard step decreases by exactly 50 ms");
        void CheckIcon(string name) {
            var path=Children(button).OfType<System.Windows.Shapes.Path>().Single();
            var bounds=path.TransformToAncestor(button).TransformBounds(new Rect(path.RenderSize));
            check(bounds.Width>8 && bounds.Height>8 && bounds.Left>=0 && bounds.Top>=0 && bounds.Right<=button.ActualWidth && bounds.Bottom<=button.ActualHeight,
                "The "+name+" icon fits completely inside its button");
        }
        void VerifySetupSpace() {
            vm.ShowInNinaImage=true;setup.IsExpanded=true;
            var scroll=(ScrollViewer)view.FindName("ControlsScrollViewer");
            var essential=(FrameworkElement)view.FindName("EssentialControls");
            var root=(FrameworkElement)view.FindName("LayoutRoot");
            foreach(int height in new[]{500,700,350,700}) {
                view.Measure(new Size(486,height));view.Arrange(new Rect(0,0,486,height));view.UpdateLayout();
                view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);view.UpdateLayout();
                double available=root.ActualHeight-essential.ActualHeight-32;
                check(workspace.Visibility==Visibility.Collapsed && Math.Abs(scroll.MaxHeight-available)<1 &&
                    (scroll.ScrollableHeight<1 || scroll.ActualHeight>=available-1),
                    "Main Image setup fills available panel height without reserving a hidden image at 486 x "+height);
                if(height==700)check(scroll.ScrollableHeight<1 && scroll.ComputedVerticalScrollBarVisibility==Visibility.Collapsed,
                    "Setup has no scrollbar when all controls fit after resizing to a taller dock");
            }
            var shot=new RenderTargetBitmap(486,700,96,96,PixelFormats.Pbgra32);shot.Render(view);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(shot));
            using(var file=File.Create("bin/live-focus-setup-space.png"))encoder.Save(file);
            double mainLimit=scroll.MaxHeight;
            vm.ShowInNinaImage=false;view.UpdateLayout();
            check(workspace.Visibility==Visibility.Visible && scroll.MaxHeight<mainLimit,
                "Switching back to local preview restores image space immediately without resizing the dock");
            setup.IsExpanded=false;
            view.Measure(new Size(350,500));view.Arrange(new Rect(0,0,350,500));view.UpdateLayout();
        }
    }
}
