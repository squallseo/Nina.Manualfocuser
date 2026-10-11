using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cwseo.NINA.ManualFocuser.Dockables;
using NINA.WPF.Base.View;

internal static partial class PreviewDisplayChecks {
    private static async Task VerifyMainRoiEditor(ManualFocuserDockableVM vm, Action<Action<BitmapSource>> bindHost, Action<bool,string> check) {
        var view=new ImageView();
        var decorator=new AdornerDecorator {Child=view};
        var root=new Grid {Background=Brushes.Black};root.Children.Add(decorator);
        // Hidden native surface: connect the real WPF visual tree without showing
        // a window, opening NINA or operating any hardware.
        using var surface=new HwndSource(new HwndSourceParameters("Live Focus ROI verification") {
            Width=650,Height=500,WindowStyle=unchecked((int)0x80000000)
        });
        surface.RootVisual=root;
        var vmType=typeof(ManualFocuserDockableVM);
        void Selecting(bool value)=>vmType.GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,value);
        object Editor()=>vmType.GetField("mainRoiEditor",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(vm);
        object Call(object target,string method,params object[] args)=>target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly).Invoke(target,args);
        async Task Layout() {
            root.Measure(new Size(650,500));root.Arrange(new Rect(0,0,650,500));root.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.DataBind);root.UpdateLayout();
        }
        bindHost(image=>view.Image=image);
        vm.ShowInNinaImage=true;Selecting(true);await Layout();
        var editor=Editor();
        check(editor!=null && ReferenceEquals(view.Image,vm.LiveDisplayImage),"ROI mode publishes the full overview to the existing host ImageView");
        check((bool)Call(editor,"TryAttach",root) && vm.IsEditingRoiInNinaImage,
            "ROI editor attaches to the real NINA ImageView using its public WPF visuals");
        var controls=new ManualFocuserDockableView {DataContext=vm};
        vm.ShowLiveGraphs=true;
        controls.Measure(new Size(350,500));controls.Arrange(new Rect(0,0,350,500));controls.UpdateLayout();
        check(((FrameworkElement)controls.FindName("PreviewWorkspace")).Visibility==Visibility.Collapsed &&
            Visuals(controls).OfType<Button>().Any(b=>Equals(b.Content,"Done") && b.Visibility==Visibility.Visible && b.ActualHeight>=28),
            "Main Image ROI mode hides all local image/graph space and keeps Done in the compact controls");
        await Layout();
        var overlay=(Adorner)editor.GetType().GetField("adorner",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(editor);
        Point Screen(Point p)=>(Point)Call(overlay,"SensorToScreen",p);
        Point? Sensor(Point p)=>(Point?)Call(overlay,"ScreenToSensor",p,false);
        RoiHandle Hit(Point p)=>(RoiHandle)Call(overlay,"HitTest",p);
        var roi=vm.PreviewRoiRectangle;
        var middle=new Point(roi.X+roi.Width/2.0,roi.Y+roi.Height/2.0);
        var mapped=Screen(middle);
        check(Sensor(mapped) is Point back && (back-middle).Length<.001,"Host viewport coordinates round-trip to sensor pixels");
        check(Hit(mapped)==RoiHandle.Move && Hit(Screen(new Point(roi.X,roi.Y+roi.Height/2.0)))==RoiHandle.Left,
            "Moving and resizing use separate screen-space hit regions");
        check((Cursor)Call(overlay,"CursorFor",RoiHandle.Move)==Cursors.SizeAll && (Cursor)Call(overlay,"CursorFor",RoiHandle.Left)==Cursors.SizeWE,
            "The host editor exposes move and horizontal-resize cursors");
        void BeginDrag(RoiHandle h) {
            overlay.GetType().GetField("start",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(overlay,middle);
            overlay.GetType().GetField("original",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(overlay,new Rect(roi.X,roi.Y,roi.Width,roi.Height));
            overlay.GetType().GetField("handle",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(overlay,h);
        }
        BeginDrag(RoiHandle.Move);Call(overlay,"Apply",middle+new Vector(128,64));
        check(vm.PreviewRoiRectangle.X==roi.X+128 && vm.PreviewRoiRectangle.Y==roi.Y+64 && vm.PreviewRoiRectangle.Width==roi.Width,
            "Host drag applies the shared sensor ROI position without changing its size");
        BeginDrag(RoiHandle.Right);Call(overlay,"Apply",middle+new Vector(128,0));
        check(vm.PreviewRoiRectangle.X==roi.X && vm.PreviewRoiRectangle.Width==roi.Width+128,
            "Host resize changes shared ROI width while retaining the opposite edge");
        vm.EditPreviewRoi(new Rect(roi.X,roi.Y,roi.Width,roi.Height));
        overlay.GetType().GetField("start",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(overlay,null);
        var pixels=new RenderTargetBitmap(650,500,96,96,PixelFormats.Pbgra32);pixels.Render(root);
        var data=new byte[650*500*4];pixels.CopyPixels(data,650*4,0);
        check(Enumerable.Range(0,650*500).Count(i=>data[i*4+2]>180 && data[i*4+1]>120 && data[i*4]<80)>150,
            "Yellow ROI grips, outline and external size label render over the host image");
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(pixels));
        using(var file=File.Create("bin/live-focus-main-image-roi.png"))encoder.Save(file);
        view.ImageRotation=90;view.ImageFlip=-1;await Layout();
        mapped=Screen(middle);
        check(Sensor(mapped) is Point rotated && (rotated-middle).Length<.001 && Hit(mapped)==RoiHandle.Move,
            "Rotation and flip preserve ROI sensor coordinates and hit testing");
        check((Cursor)Call(overlay,"CursorFor",RoiHandle.Left)==Cursors.SizeNS,
            "Resize cursor rotates with the host image");
        view.ImageRotation=0;view.ImageFlip=1;await Layout();
        // Use the actual host zoom button, not a private field or alternate viewer.
        var zoom=Visuals(view).OfType<Button>().First();
        for(int i=0;i<3;i++) zoom.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,zoom));
        await Layout();
        var scroll=(ScrollViewer)view.PART_ScrollViewerBinding;
        scroll.ScrollToHorizontalOffset(20);scroll.ScrollToVerticalOffset(15);await Layout();
        mapped=Screen(middle);
        check(Sensor(mapped) is Point zoomed && (zoomed-middle).Length<.001,
            "Native zoom and scrolling preserve the ROI coordinate mapping");
        Selecting(false);await Layout();
        check(Editor()==null && !vm.IsEditingRoiInNinaImage && AdornerLayer.GetAdornerLayer(overlay.AdornedElement).GetAdorners(overlay.AdornedElement)==null,
            "Done removes the host overlay and releases the editor");
        check(((FrameworkElement)controls.FindName("MeasurementsPanel")).Visibility==Visibility.Collapsed && vm.ShowLiveGraphs,
            "Done restores the host graph choice without reopening a duplicate image");
        Selecting(true);await Layout();editor=Editor();Call(editor,"TryAttach",root);
        vm.ShowInNinaImage=false;
        check(Editor()==null && !vm.IsEditingRoiInNinaImage && vm.IsSelectingRoi,
            "Turning off NINA Image restores local editing without losing the ROI selection");
        vm.ShowInNinaImage=true;await Layout();editor=Editor();Call(editor,"TryAttach",root);
        view.Image=BitmapSource.Create(32,32,96,96,PixelFormats.Gray8,null,new byte[32*32],32);
        Call(editor,"OnTick",null,EventArgs.Empty);
        check(!vm.IsEditingRoiInNinaImage && !ReferenceEquals(view.Image,vm.LiveDisplayImage),
            "A foreign normal image removes the ROI overlay without being overwritten");
        Selecting(false);vm.ShowInNinaImage=false;bindHost(null);
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root) {
        yield return root;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in Visuals(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
}
