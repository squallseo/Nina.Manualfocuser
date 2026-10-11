using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NINA.Core.Model;
using NINA.Core.Interfaces;
using NINA.Core.Utility;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using ImageView = NINA.WPF.Base.View.ImageView;

internal static class NativeViewerZoomChecks {
    internal static async Task Run(object host, IProfileService profiles, IStarDetection detector, IStarAnnotator annotator, Action<bool,string> check) {
        const int width=2048,height=1536;
        var pixels=new ushort[width*height];
        var layout=Cwseo.NINA.ManualFocuser.LiveWorkflow.Models.FocusAberrationMosaic.Plan(width,height);
        for(int i=0;i<9;i++)for(int y=0;y<layout.Size;y++)for(int x=0;x<layout.Size;x++)
            pixels[(layout.Tiles[i].Y+y)*width+layout.Tiles[i].X+x]=(ushort)(6000+i*6000);
        var raw=new BaseImageData(pixels,width,height,16,false,new ImageMetaData(),profiles,detector,annotator);
        var type=host.GetType();
        await (Task<IRenderedImage>)type.GetMethod("PrepareImage").Invoke(host,new object[]{raw,new PrepareImageParameters(autoStretch:false,detectStars:false),default(CancellationToken)});
        var view=new ImageView {DataContext=host,Width=1280,Height=1000};
        BindingOperations.SetBinding(view,ImageView.ImageProperty,new Binding("Image"));
        var size=new Size(1280,1000);view.Measure(size);view.Arrange(new Rect(size));view.UpdateLayout();
        var trace=typeof(Cwseo.NINA.ManualFocuser.Dockables.ManualFocuserDockableVM).Assembly.GetType("Cwseo.NINA.ManualFocuser.Dockables.FocusViewerDiagnostics");
        trace.GetMethod("Observe",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{view});
        var buttons=((Grid)((Grid)view.Content).Children[0]).Children.OfType<Button>().ToArray();
        var zoomIn=buttons.Single(b=>Grid.GetColumn(b)==0);var zoomOut=buttons.Single(b=>Grid.GetColumn(b)==1);
        var inspector=type.GetMethod("InspectAberration",BindingFlags.Instance|BindingFlags.NonPublic);
        async Task Change(bool enabled) {
            type.GetProperty("ShowAberration").SetValue(host,enabled);
            check(await (Task<bool>)inspector.Invoke(host,null)==enabled,"Native NINA Inspector toggle completes");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();
        }
        async Task Zoom(int count,Button button) {
            for(int i=0;i<count;i++){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();}
        }
        await Change(true);
        for(int cycle=0;cycle<8;cycle++) {
            await Change(false);await Zoom(6,zoomIn);await Zoom(8,zoomOut);
            await Change(true);await Zoom(3,zoomIn);await Zoom(6,zoomOut);
            var scroll=(ScrollViewer)view.FindName("PART_ScrollViewer");
            scroll.ScrollToHorizontalOffset(0);scroll.ScrollToVerticalOffset(0);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);view.UpdateLayout();
            var source=view.Image;
            var converted=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
            var bytes=new byte[source.PixelWidth*source.PixelHeight*4];converted.CopyPixels(bytes,source.PixelWidth*4,0);
            var screenshot=new RenderTargetBitmap(1280,1000,96,96,PixelFormats.Pbgra32);screenshot.Render(view);
            var painted=new byte[1280*1000*4];screenshot.CopyPixels(painted,1280*4,0);
            for(int row=0;row<3;row++) {
                int i=row*3+2,offset=((row*260+128)*source.PixelWidth+2*260+128)*4;
                int expected=(int)Math.Round((6000+i*6000)*255.0/65535);
                check(Math.Abs(bytes[offset]-expected)<=2,$"Native NINA toggle/zoom {cycle}: mosaic tile {i} contains the correct pixels");
                var point=((Canvas)view.FindName("PART_Canvas")).TransformToAncestor(view).Transform(new Point(2*260+128,row*260+128));
                check(point.X>=0&&point.X<1280&&point.Y>=40&&point.Y<1000&&Math.Abs(painted[((int)point.Y*1280+(int)point.X)*4]-expected)<=2,
                    $"Native NINA toggle/zoom {cycle}: right-column tile {i} is actually painted");
            }
            // Allow the real trace timer to observe a steady native mosaic.
            if(cycle==7)await Task.Delay(300);
        }
        await Change(false);
    }
}
