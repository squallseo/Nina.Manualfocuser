using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.WPF.Base.View;

internal static partial class PreviewDisplayChecks
{
    private static async Task VerifyInspectorHostImage(ManualFocuserDockableVM vm, Action<bool,string> check)
    {
        const int w=2048,h=1536,sw=750,sh=780;
        var raw=Enumerable.Range(0,w*h).Select(i=>(ushort)(1000+i%19)).ToArray();
        var plan=FocusAberrationMosaic.Plan(w,h);
        foreach(var tile in plan.Tiles)
            for(int y=-20;y<=20;y++)for(int x=-20;x<=20;x++)
                raw[(tile.Y+plan.Size/2+y)*w+tile.X+plan.Size/2+x]=30000;
        var image=(BitmapSource)typeof(ManualFocuserDockableVM).GetMethod("RenderAberrationPreview",BindingFlags.Instance|BindingFlags.NonPublic)
            .Invoke(vm,new object[]{new FocusRawFrame(raw,w,0,0,w,h),1.0});
        var view=new ImageView();var root=new Grid {Background=Brushes.Black};root.Children.Add(view);
        using var surface=new HwndSource(new HwndSourceParameters("Inspector host image checks") {
            Width=sw,Height=sh,WindowStyle=unchecked((int)0x80000000) });
        surface.RootVisual=root;
        async Task Layout() {
            root.Measure(new Size(sw,sh));root.Arrange(new Rect(0,0,sw,sh));root.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);root.UpdateLayout();
        }
        view.Image=BitmapSource.Create(w,h,96,96,PixelFormats.Gray8,null,new byte[w*h],w);
        await Layout();view.Image=image;
        foreach(var state in new[]{(0d,1d),(90d,1d),(0d,-1d)}) {
            view.ImageRotation=state.Item1;view.ImageFlip=state.Item2;await Layout();
            var canvas=(Canvas)view.FindName("PART_Canvas");
            var rendered=new RenderTargetBitmap(sw,sh,96,96,PixelFormats.Pbgra32);rendered.Render(root);
            var pixels=new byte[sw*sh*4];rendered.CopyPixels(pixels,sw*4,0);
            foreach(var tile in plan.Tiles) {
                var point=canvas.TranslatePoint(new Point((tile.DisplayX+plan.Size/2d)*canvas.ActualWidth/plan.Width,
                    (tile.DisplayY+plan.Size/2d)*canvas.ActualHeight/plan.Height),root);
                int x=(int)point.X,y=(int)point.Y;
                check(x>=0 && y>=0 && x<sw && y<sh && pixels[(y*sw+x)*4]>180,
                    $"Native ImageView renders Inspector tile {tile.DisplayX},{tile.DisplayY} after full-frame replacement (rotation {state.Item1}, flip {state.Item2})");
            }
        }
    }
}
