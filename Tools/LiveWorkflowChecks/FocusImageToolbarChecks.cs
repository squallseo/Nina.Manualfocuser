using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

internal static class FocusImageToolbarChecks {
    public static async Task Run(ManualFocuserDockableVM vm, Action<bool,string> check) {
        Application.Current.Resources["ProfileService"] = new { ActiveProfile = new { ColorSchemaSettings = new { ColorSchema = new {
            PrimaryColor=Colors.White, SecondaryColor=Colors.Gray, BackgroundColor=Color.FromRgb(27,29,32), BorderColor=Colors.Gray,
            ButtonBackgroundColor=Color.FromRgb(50,54,60), ButtonBackgroundSelectedColor=Color.FromRgb(71,81,96), ButtonForegroundColor=Colors.White,
            ButtonForegroundDisabledColor=Colors.Gray, TertiaryBackgroundColor=Color.FromRgb(27,29,32)
        } } } };
        foreach(string name in new[]{"Brushes","SVGDictionary","Converters"}) Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary {
            Source=new Uri($"/NINA.WPF.Base;component/Resources/StaticResources/{name}.xaml",UriKind.Relative)
        });
        var view = new ManualFocuserDockableView {DataContext=vm};
        view.Measure(new Size(1000,220));view.Arrange(new Rect(0,0,1000,220));view.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var native = ((Grid)view.FindName("PreviewCard")).Children.OfType<NINA.WPF.Base.View.ImageView>().Single();
        var overlay = (FocusRoiOverlay)native.ImageAreaContent;
        var tools = ((StackPanel)native.ButtonHeaderContent).Children.OfType<FrameworkElement>().ToArray();
        check(tools.Length==7 && tools.OfType<ToggleButton>().Count()==3,"MF Image places stretch adjustment and native-image sync after the four image tools");
        var stretchControls=(Grid)tools[4];
        var stretch=stretchControls.Children.OfType<Slider>().Single();
        var reset=stretchControls.Children.OfType<Button>().Single();
        check(ReferenceEquals(((Button)tools[0]).Template,((Style)view.FindResource("StandardButton")).Setters.OfType<Setter>().Single(s=>s.Property==Control.TemplateProperty).Value),
            "Image tools reuse the original NINA button template");
        check(ReferenceEquals(native.Image,vm.LiveDisplayBitmap) && ((Button)tools[6]).Command==vm.SyncImageCommand,
            "Image toolbar sync uses the shared command and original raw source");
        var grid = (Grid)native.Content;
        var zoomButtons = ((Grid)grid.Children[0]).Children.OfType<Button>().ToArray();
        zoomButtons.Single(b=>Grid.GetColumn(b)==3).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var zoom=(ScaleTransform)native.FindName("PART_ScaleTransform");
        check(zoom.ScaleX==1 && zoom.ScaleY==1,"Native 1:1 zoom operates on the actual MF image");
        zoomButtons.Single(b=>Grid.GetColumn(b)==0).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(zoom.ScaleX>1,"Native zoom-in increases image magnification");
        double hfr=vm.LiveHfr; var roi=vm.PreviewRoiRectangle; var source=vm.FocusPreviewImage;
        ((Button)tools[0]).Command.Execute(null);
        ((ToggleButton)tools[1]).SetCurrentValue(ToggleButton.IsCheckedProperty,true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(native.ImageRotation==90 && native.ImageFlip==-1 && ReferenceEquals(vm.FocusPreviewImage,source) && vm.LiveHfr==hfr,"Rotation and flip transform only the display, preserving source pixels and HFR");
        ((ToggleButton)tools[2]).SetCurrentValue(ToggleButton.IsCheckedProperty,true);
        await Task.Delay(250);
        var mosaic=vm.FocusPreviewImage;
        check(!vm.CanInspectFocusImage && !vm.IsAberrationInspector && !tools[2].IsEnabled && ReferenceEquals(mosaic,source) && vm.PreviewRoiRectangle==roi && vm.LiveHfr==hfr,
            "Inspector is disabled for a small captured image and cannot change its presentation");
        ((ToggleButton)tools[3]).SetCurrentValue(ToggleButton.IsCheckedProperty,false);
        await Task.Delay(250);
        check(!vm.PreviewAutoStretch && !ReferenceEquals(vm.FocusPreviewImage,mosaic) && vm.LiveHfr==hfr,"Auto stretch can be disabled on the cached image without another measurement");
        check(!stretch.IsEnabled && !reset.IsEnabled,"Disabling Auto Stretch disables its strength slider and reset together");
        ((ToggleButton)tools[2]).SetCurrentValue(ToggleButton.IsCheckedProperty,false);
        await Task.Delay(250);
        var raw=typeof(ManualFocuserDockableVM).GetField("syncSourceImage",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(vm) switch {
            NINA.Image.Interfaces.IImageData data => new FocusRawFrame(data.Data.FlatArray,256,0,0,256,256,data), _=>throw new Exception("Missing source")
        };
        var expected=FocusDisplayStretch.RenderRaw(raw,autoStretch:false);
        var actual=new byte[256*256]; native.Image.CopyPixels(actual,256,0);
        check(actual.SequenceEqual(expected) && vm.LiveHfr==hfr,"Turning inspector off restores a linear view of the same original source pixels");
        vm.PreviewAutoStretch=true;vm.ImageFlip=false;
        for(int i=0;i<3;i++)vm.RotateImageCommand.Execute(null);
        await Task.Delay(250);
        check(native.ImageRotation==0 && native.ImageFlip==1 && vm.PreviewAutoStretch,"Four quarter turns and flip reset restore the normal image presentation");
        var displayed=vm.LiveDisplayBitmap;
        stretch.SetCurrentValue(Slider.ValueProperty,.5);
        await Task.Delay(250);
        check(stretch.IsEnabled && reset.IsEnabled && vm.PreviewStretchStrength==.5 && !ReferenceEquals(displayed,vm.LiveDisplayBitmap) && vm.LiveHfr==hfr,
            "Image toolbar strength changes rerender the cached pixels without recalculating HFR");
        reset.Command.Execute(null);
        await Task.Delay(250);
        check(stretch.Value==1 && vm.PreviewStretchStrength==1,"The image toolbar reset restores shared stretch strength");
        var type=typeof(ManualFocuserDockableVM);
        var widthField=type.GetField("previewRoiWidth",BindingFlags.Instance|BindingFlags.NonPublic);
        var heightField=type.GetField("previewRoiHeight",BindingFlags.Instance|BindingFlags.NonPublic);
        var overviewField=type.GetField("overviewImage",BindingFlags.Instance|BindingFlags.NonPublic);
        object oldWidth=widthField.GetValue(vm), oldHeight=heightField.GetValue(vm), oldOverview=overviewField.GetValue(vm);
        try {
            widthField.SetValue(vm,64);heightField.SetValue(vm,96);overviewField.SetValue(vm,vm.FocusPreviewImage);
            type.GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,true);
            vm.RotateImageCommand.Execute(null);vm.ImageFlip=true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();
            var surface=(Grid)overlay.FindName("PreviewSurface");
            var point=new Point(128,128);
            var screen=surface.TransformToAncestor(view).Transform(point);
            var inverse=view.TransformToDescendant(surface).Transform(screen);
            var outline=(System.Windows.Shapes.Rectangle)overlay.FindName("RoiOutline");
            check(Math.Abs(inverse.X-point.X)<1e-6 && Math.Abs(inverse.Y-point.Y)<1e-6 &&
                Canvas.GetLeft(outline)==96 && Canvas.GetTop(outline)==80 && outline.Width==64 && outline.Height==96,
                "ROI overlay stays in source coordinates through native zoom, rotation and horizontal flip");
            check(!vm.CanInspectFocusImage,"Inspector is disabled while editing the full-frame ROI");
        } finally {
            widthField.SetValue(vm,oldWidth);heightField.SetValue(vm,oldHeight);overviewField.SetValue(vm,oldOverview);
            type.GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,false);
            vm.ImageFlip=false;for(int i=0;i<3;i++)vm.RotateImageCommand.Execute(null);
        }
        await VerifyLargeMosaic(view,native,overlay,vm,check);
    }

    private static async Task VerifyLargeMosaic(ManualFocuserDockableView view, NINA.WPF.Base.View.ImageView native, FocusRoiOverlay overlay,
        ManualFocuserDockableVM vm, Action<bool,string> check) {
        var original=vm.FocusPreviewImage;
        const int width=2048,height=1536;
        var pixels=new ushort[width*height];
        var plan=FocusAberrationMosaic.Plan(width,height);
        for(int i=0;i<9;i++) {
            var tile=plan.Tiles[i];
            for(int y=0;y<plan.Size;y++) for(int x=0;x<plan.Size;x++) pixels[(tile.Y+y)*width+tile.X+x]=(ushort)(6000+i*6000);
        }
        bool auto=vm.PreviewAutoStretch;
        vm.PreviewAutoStretch=false;
        await Task.Delay(150);
        var render=typeof(ManualFocuserDockableVM).GetMethod("RenderAberrationPreview",BindingFlags.Instance|BindingFlags.NonPublic);
        var preview=(BitmapSource)render.Invoke(vm,new object[]{new FocusRawFrame(pixels,width,0,0,width,height),1.0});
        typeof(ManualFocuserDockableVM).GetMethod("SetAberrationInspector",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(vm,new object[]{true});
        typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.FocusPreviewImage)).SetValue(vm,preview);
        try {
            foreach(var size in new[]{new Size(920,890),new Size(1280,1000)}) {
                view.Width=size.Width;view.Height=size.Height;
                view.Measure(size);view.Arrange(new Rect(size));view.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var grid=(Grid)native.Content;
                ((Grid)grid.Children[0]).Children.OfType<Button>().Single(b=>Grid.GetColumn(b)==2).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();
                var screenshot=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);
                screenshot.Render(view);
                var actual=new byte[(int)size.Width*(int)size.Height*4];screenshot.CopyPixels(actual,(int)size.Width*4,0);
                var surface=(Grid)overlay.FindName("PreviewSurface");
                for(int i=0;i<9;i++) {
                    var tile=plan.Tiles[i];
                    var point=surface.TransformToAncestor(view).Transform(new Point(tile.DisplayX+256,tile.DisplayY+256));
                    int offset=((int)point.Y*(int)size.Width+(int)point.X)*4;
                    int expected=(int)Math.Round((6000+i*6000)*255.0/65535);
                    check(point.X>=0 && point.X<size.Width && point.Y>=40 && point.Y<size.Height && Math.Abs(actual[offset]-expected)<=2,
                        $"Rendered 1552-pixel mosaic preserves tile {i} at viewport {size.Width}x{size.Height}");
                }
                if(size.Width==920) {
                    var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(screenshot));
                    using var output=System.IO.File.Create("bin/mf-inspector-rendered.png");png.Save(output);
                }
            }
            check(preview.Format==PixelFormats.Pbgra32 && pixels[^1]!=0,"Inspector display is opaque 32-bit while scientific source pixels remain 16-bit");
            var type=typeof(ManualFocuserDockableVM);
            var setPreview=type.GetProperty(nameof(vm.FocusPreviewImage));
            var renderRaw=type.GetMethod("RenderRawFocusPreview",BindingFlags.Instance|BindingFlags.NonPublic);
            var normal=(BitmapSource)renderRaw.Invoke(vm,new object[]{new FocusRawFrame(pixels,width,0,0,width,height),null,1.0});
            var header=(Grid)((Grid)native.Content).Children[0];
            var zoomIn=header.Children.OfType<Button>().Single(b=>Grid.GetColumn(b)==0);
            var zoomOut=header.Children.OfType<Button>().Single(b=>Grid.GetColumn(b)==1);
            var scroll=(ScrollViewer)native.FindName("PART_ScrollViewer");
            async Task ZoomSeveralTimes(int count, Button button) {
                for(int i=0;i<count;i++) {
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();
                }
            }
            for(int cycle=0;cycle<24;cycle++) {
                // Exact reported sequence: 3x3 off, zoom normal image repeatedly,
                // 3x3 on/off again. Never use Fit to hide a stale zoom/layout state.
                vm.IsAberrationInspector=false;setPreview.SetValue(vm,normal);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();
                await ZoomSeveralTimes(6,zoomIn);await ZoomSeveralTimes(8,zoomOut);
                vm.IsAberrationInspector=true;setPreview.SetValue(vm,preview);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();
                await ZoomSeveralTimes(3,zoomIn);await ZoomSeveralTimes(6,zoomOut);
                scroll.ScrollToHorizontalOffset(0);scroll.ScrollToVerticalOffset(0);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();
                var canvas=(Canvas)native.FindName("PART_Canvas");
                check(canvas.ActualWidth==preview.PixelWidth && canvas.ActualHeight==preview.PixelHeight,
                    $"Inspector toggle {cycle}: native canvas follows the new bitmap dimensions ({canvas.ActualWidth}x{canvas.ActualHeight})");
                var shot=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);shot.Render(view);
                var bytes=new byte[(int)view.ActualWidth*(int)view.ActualHeight*4];shot.CopyPixels(bytes,(int)view.ActualWidth*4,0);
                for(int row=0;row<3;row++) {
                    int i=row*3+2;var tile=plan.Tiles[i];
                    var p=((Grid)overlay.FindName("PreviewSurface")).TransformToAncestor(view).Transform(new Point(tile.DisplayX+256,tile.DisplayY+256));
                    int offset=((int)p.Y*(int)view.ActualWidth+(int)p.X)*4;
                    int expected=(int)Math.Round((6000+i*6000)*255.0/65535);
                    check(p.X>=0&&p.X<view.ActualWidth&&p.Y>=40&&p.Y<view.ActualHeight&&Math.Abs(bytes[offset]-expected)<=2,
                        $"Inspector/normal zoom cycle {cycle}: right-column tile {i} remains visible without Fit");
                }
            }
            await VerifyViewerTrace(native,overlay,check);
        } finally {
            typeof(ManualFocuserDockableVM).GetMethod("SetAberrationInspector",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(vm,new object[]{false});
            typeof(ManualFocuserDockableVM).GetProperty(nameof(vm.FocusPreviewImage)).SetValue(vm,original);
            vm.PreviewAutoStretch=auto;
            await Task.Delay(150);
        }
    }

    private static async Task VerifyViewerTrace(NINA.WPF.Base.View.ImageView native,FocusRoiOverlay overlay,Action<bool,string> check) {
        var trace=typeof(ManualFocuserDockableVM).Assembly.GetType("Cwseo.NINA.ManualFocuser.Dockables.FocusViewerDiagnostics");
        // This bitmap/layout fixture has no Window or normal Loaded lifecycle.
        // Real panel loading and native-viewer isolation are checked separately.
        trace.GetMethod("Observe",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{native});
        var table=trace.GetField("observers",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        object[] args={native,null};
        check((bool)table.GetType().GetMethod("TryGetValue").Invoke(table,args),"MF Image's detached bitmap/layout fixture can opt into diagnostics");
        var observer=args[1];
        var queue=trace.GetMethod("Queue",BindingFlags.Instance|BindingFlags.NonPublic);
        var capture=trace.GetMethod("Capture",BindingFlags.Instance|BindingFlags.NonPublic);
        string directory=(string)trace.GetProperty("DirectoryPath",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        string path=System.IO.Path.Combine(directory,"viewer.jsonl");
        int viewerId=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(native);
        async Task VerifyLogged(string reason,string flag) {
            queue.Invoke(observer,new object[]{reason,true});
            capture.Invoke(observer,null);
            var wait=System.Diagnostics.Stopwatch.StartNew();
            while(wait.ElapsedMilliseconds<5000) {
                await Task.Delay(50);
                if(!System.IO.File.Exists(path))continue;
                using var reader=new System.IO.StreamReader(new System.IO.FileStream(path,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.ReadWrite));
                for(string line=reader.ReadLine();line!=null;line=reader.ReadLine()) {
                    System.Text.Json.JsonDocument record;
                    try{record=System.Text.Json.JsonDocument.Parse(line);}catch(System.Text.Json.JsonException){continue;}
                    using(record) {
                        var root=record.RootElement;
                        if(root.GetProperty("Event").GetString()!="state"||root.GetProperty("ViewerId").GetInt32()!=viewerId)continue;
                        if(root.GetProperty("Reasons").EnumerateArray().Any(r=>r.GetString()==reason)&&root.GetProperty(flag).GetBoolean()) {
                            check(root.GetProperty("Anomaly").GetBoolean()&&root.GetProperty("Pixels").GetArrayLength()==9&&
                                root.GetProperty("FocusFrame").GetProperty("TileSources").GetArrayLength()==9,
                                $"Automatic trace catches injected {reason} and retains all nine source tile pixel summaries");
                            return;
                        }
                    }
                }
            }
            throw new Exception("Diagnostic trace did not record injected "+reason);
        }
        var canvas=(Canvas)native.FindName("PART_Canvas");
        var widthBinding=System.Windows.Data.BindingOperations.GetBindingBase(canvas,FrameworkElement.WidthProperty);
        try {
            canvas.Width=native.Image.PixelWidth-512;native.UpdateLayout();
            await VerifyLogged("test-injected-right-clip","BoundsMismatch");
        } finally {System.Windows.Data.BindingOperations.SetBinding(canvas,FrameworkElement.WidthProperty,widthBinding);native.UpdateLayout();}
        var items=(ItemsControl)overlay.FindName("InspectorTiles");
        var rightContainer=(ContentPresenter)items.ItemContainerGenerator.ContainerFromIndex(2);
        Image FindImage(DependencyObject parent) {
            if(parent is Image image)return image;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var found=FindImage(VisualTreeHelper.GetChild(parent,i));if(found!=null)return found;}
            return null;
        }
        var tileImage=FindImage(rightContainer);
        try {
            tileImage.Visibility=Visibility.Collapsed;native.UpdateLayout();
            await VerifyLogged("test-injected-missing-right-tile","MissingTileContainers");
        } finally {tileImage.Visibility=Visibility.Visible;native.UpdateLayout();}
        var rightImages=new[]{2,5,8}.Select(i=>FindImage((ContentPresenter)items.ItemContainerGenerator.ContainerFromIndex(i))).ToArray();
        try {
            foreach(var image in rightImages)image.Opacity=0;
            native.UpdateLayout();
            trace.GetField("lastSnapshot",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(observer,DateTime.MinValue);
            await VerifyLogged("test-injected-transparent-right-column","MissingTileContainers");
        } finally {foreach(var image in rightImages)image.Opacity=1;native.UpdateLayout();}
    }
}
