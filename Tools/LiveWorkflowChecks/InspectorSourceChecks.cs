using System.Reflection;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Core.Interfaces;
using NINA.Image.ImageAnalysis;

internal static class InspectorSourceChecks {
    private static object Default(MethodInfo method) => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    public static async Task Run(string folder, bool wholeFolder = false) {
        var settings=Fake.Of<IImageSettings>((m,a)=>Default(m));
        var profile=Fake.Of<IProfile>((m,a)=>m.Name=="get_ImageSettings"?settings:Default(m));
        var profiles = Fake.Of<IProfileService>((m,a) => m.Name=="get_ActiveProfile"?profile:Default(m));
        var analysis=Fake.Of<IStarDetectionAnalysis>((m,a)=>Default(m));
        var detection=Fake.Of<IStarDetection>((m,a)=>m.Name=="CreateAnalysis"?analysis:Default(m));
        var factory = Fake.Of<IImageDataFactory>((m,a) => m.Name == "CreateBaseImageData"
            ? new BaseImageData((ushort[])a[0],(int)a[1],(int)a[2],(int)a[3],(bool)a[4],(ImageMetaData)a[5],profiles,detection,null)
            : throw new Exception(m.Name));
        var files = wholeFolder ? Directory.GetFiles(folder,"*.fits",SearchOption.AllDirectories)
            : new[] { "initial", "attempt01", "final" }.Select(category=>Directory.GetFiles(Path.Combine(folder,category),"*.fits").OrderBy(f=>f).First()).ToArray();
        int failures = 0;
        foreach (string file in files) {
            string category = Path.GetFileName(Path.GetDirectoryName(file));
            var image = await NINA.Image.FileFormat.FITS.FITS.Load(new Uri(file),false,factory,CancellationToken.None);
            var raw = new FocusRawFrame(image.Data.FlatArray,image.Properties.Width,0,0,image.Properties.Width,image.Properties.Height,image);
            var layout = FocusAberrationMosaic.Plan(raw.Width,raw.Height);
            var mosaic = FocusAberrationMosaic.Display(raw,layout);
            Console.WriteLine($"{Path.GetFileName(file)} {raw.Width}x{raw.Height}, {image.Data.FlatArray.Length} pixels");
            for (int i=0;i<9;i++) {
                var tile=layout.Tiles[i]; long total=0; int rawNonzero=0,displayNonzero=0;
                for(int y=0;y<layout.Size;y++) for(int x=0;x<layout.Size;x++) {
                    ushort value=raw.Pixels[(tile.Y+y)*raw.Stride+tile.X+x]; total+=value;
                    if(value>0)rawNonzero++;
                    if(mosaic[(tile.DisplayY+y)*layout.Width+tile.DisplayX+x]>0)displayNonzero++;
                }
                Console.WriteLine($"tile {i+1}: raw mean {total/(double)(layout.Size*layout.Size):F1}, nonzero {rawNonzero}, display nonzero {displayNonzero}");
                if(rawNonzero==0 || displayNonzero==0) { failures++; Console.WriteLine($"EMPTY TILE {i+1}: {file}"); }
            }
            var bitmap=BitmapSource.Create(layout.Width,layout.Height,96,96,PixelFormats.Gray8,null,mosaic,layout.Width);bitmap.Freeze();
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string output=Path.Combine(AppContext.BaseDirectory,$"inspector-{category}.png");
            using(var stream=File.Create(output))encoder.Save(stream);
            Console.WriteLine("Saved " + output);
        }
        Console.WriteLine($"Audited {files.Length} FITS files, {failures} empty tiles.");
    }
    public static async Task Show(Cwseo.NINA.ManualFocuser.Dockables.ManualFocuserDockableVM vm, string folder, Action<bool,string> check = null) {
        var settings=Fake.Of<IImageSettings>((m,a)=>Default(m));
        var profile=Fake.Of<IProfile>((m,a)=>m.Name=="get_ImageSettings"?settings:Default(m));
        var profiles=Fake.Of<IProfileService>((m,a)=>m.Name=="get_ActiveProfile"?profile:Default(m));
        var analysis=Fake.Of<IStarDetectionAnalysis>((m,a)=>Default(m));
        var detection=Fake.Of<IStarDetection>((m,a)=>m.Name=="CreateAnalysis"?analysis:Default(m));
        var factory=Fake.Of<IImageDataFactory>((m,a)=>m.Name=="CreateBaseImageData"
            ?new BaseImageData((ushort[])a[0],(int)a[1],(int)a[2],(int)a[3],(bool)a[4],(ImageMetaData)a[5],profiles,detection,null):throw new Exception(m.Name));
        var original=vm.FocusPreviewImage;
        var render=vm.GetType().GetMethod("RenderAberrationPreview",BindingFlags.Instance|BindingFlags.NonPublic);
        foreach(string category in check==null ? new[]{"initial"} : new[]{"initial","attempt01","final"}) {
        string file=Directory.GetFiles(Path.Combine(folder,category),"*.fits").OrderBy(f=>f).First();
        var image=await NINA.Image.FileFormat.FITS.FITS.Load(new Uri(file),false,factory,CancellationToken.None);
        var raw=new FocusRawFrame(image.Data.FlatArray,image.Properties.Width,0,0,image.Properties.Width,image.Properties.Height,image);
        var normal=vm.GetType().GetMethod("RenderRawFocusPreview",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(vm,new object[]{raw,null,1.0});
        vm.GetType().GetProperty(nameof(vm.FocusPreviewImage)).SetValue(vm,normal);
        vm.IsAberrationInspector=true;
        var bitmap=(BitmapSource)render.Invoke(vm,new object[]{new FocusRawFrame(image.Data.FlatArray,image.Properties.Width,0,0,image.Properties.Width,image.Properties.Height,image),1.0});
        vm.GetType().GetProperty(nameof(vm.FocusPreviewImage)).SetValue(vm,bitmap);
        var view=new Cwseo.NINA.ManualFocuser.Dockables.ManualFocuserDockableView {DataContext=vm};
        if(check!=null) {
            var size=new System.Windows.Size(920,890);
            view.Measure(size);view.Arrange(new System.Windows.Rect(size));view.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            var native=((System.Windows.Controls.Grid)view.FindName("PreviewCard")).Children.OfType<NINA.WPF.Base.View.ImageView>().Single();
            var overlay=(Cwseo.NINA.ManualFocuser.Dockables.FocusRoiOverlay)native.ImageAreaContent;
            var surface=(System.Windows.Controls.Grid)overlay.FindName("PreviewSurface");
            var screenshot=new RenderTargetBitmap(920,890,96,96,PixelFormats.Pbgra32);screenshot.Render(view);
            var values=new byte[920*890*4];screenshot.CopyPixels(values,920*4,0);
            check(vm.LiveDisplayInspectorTiles.Length==9 && vm.LiveDisplayInspectorTiles.All(t=>t.Bitmap.PixelWidth<=512),"Actual FITS is displayed with nine independent inspector textures");
            foreach(var tile in vm.LiveDisplayInspectorTiles) {
                var point=surface.TransformToAncestor(view).Transform(new System.Windows.Point(tile.X+256,tile.Y+256));
                long total=0;
                for(int y=-8;y<=8;y++)for(int x=-8;x<=8;x++)total+=values[(((int)point.Y+y)*920+(int)point.X+x)*4];
                check(total>1000,$"Actual {category} FITS renders inspector tile at {tile.X},{tile.Y} without a black region");
            }
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(screenshot));
            using(var stream=File.Create(Path.Combine(AppContext.BaseDirectory,$"inspector-view-{category}.png")))encoder.Save(stream);
            continue;
        }
        var window=new System.Windows.Window {Title="MF Inspector verification",Content=view,Width=1000,Height=950};
        var closed=new TaskCompletionSource(); window.Closed+=(_,_)=>closed.TrySetResult();
        window.Show();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Console.WriteLine("Inspector window ready; render tier " + (RenderCapability.Tier>>16));
        await closed.Task;
        }
        vm.GetType().GetProperty(nameof(vm.FocusPreviewImage)).SetValue(vm,original);vm.IsAberrationInspector=false;
    }
}
