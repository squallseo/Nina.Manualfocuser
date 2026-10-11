using NINA.Image.ImageAnalysis;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NINA.Core.Model;
using NINA.Core.Interfaces;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

internal static class HostImagePerformance {
    public static void Run(string folder = null, bool verificationOnly = false) {
        Exception failure = null;
        var thread = new Thread(() => {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () => {
                try { await Measure(folder,verificationOnly); } catch (Exception e) { failure = e; }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new Exception("Host image benchmark failed", failure);
    }

    private static object Default(MethodInfo method) => method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    private static async Task Measure(string folder, bool verificationOnly) {
        Application.Current.Resources["PictureSVG"] = new GeometryGroup();
        Application.Current.Resources["HistogramSVG"] = new GeometryGroup();
        var imageSettings = Fake.Of<IImageSettings>((m,a) => m.Name switch {
            "get_AutoStretchFactor" => .2,
            "get_BlackClipping" => -2.8,
            _ => Default(m)
        });
        var profile = Fake.Of<IProfile>((m,a) => m.Name == "get_ImageSettings" ? imageSettings : Default(m));
        var profiles = Fake.Of<IProfileService>((m,a) => m.Name == "get_ActiveProfile" ? profile : Default(m));
        var camera = Fake.Of<ICameraMediator>((m,a) => Default(m));
        var telescope = Fake.Of<ITelescopeMediator>((m,a) => Default(m));
        var imaging = Fake.Of<IImagingMediator>((m,a) => Default(m));
        var status = Fake.Of<IApplicationStatusMediator>((m,a) => Default(m));
        var analysis = Fake.Of<IStarDetectionAnalysis>((m,a) => Default(m));
        var detection = Fake.Of<IStarDetection>((m,a) => m.Name == "CreateAnalysis" ? analysis : throw new Exception("Unexpected star detection: " + m.Name));
        var annotator = Fake.Of<IStarAnnotator>((m,a) => throw new Exception("Unexpected annotation: " + m.Name));
        var hostAssembly = typeof(NINA.ViewModel.ImageHistory.ImageHistoryVM).Assembly;
        var hostType = hostAssembly.GetType("NINA.ViewModel.ImageControlVM", true);
        var host = Activator.CreateInstance(hostType, profiles, camera, telescope, imaging, status);
        var prepare = hostType.GetMethod("PrepareImage", new[] { typeof(IImageData), typeof(PrepareImageParameters), typeof(CancellationToken) });
        var renderedProperty = hostType.GetProperty("RenderedImage");
        var imageProperty = hostType.GetProperty("Image");
        var statistics = new NINA.ViewModel.ImageStatisticsVM(profiles);
        Console.WriteLine("Installed host: " + hostAssembly.GetName().Version);
        var sources = new List<(string Name, ushort[] Pixels, int Width, int Height)>();
        if (folder != null) {
            var factory = Fake.Of<IImageDataFactory>((m,a) => m.Name == "CreateBaseImageData" ?
                new BaseImageData((ushort[])a[0],(int)a[1],(int)a[2],(int)a[3],(bool)a[4],(ImageMetaData)a[5],profiles,detection,annotator) : throw new Exception("Unexpected image factory: " + m.Name));
            foreach (var category in new[] { "initial", "attempt01", "final" }) {
                var file = Directory.GetFiles(Path.Combine(folder,category),"*.fits").OrderBy(f => f).First();
                var watch = Stopwatch.StartNew();
                var loaded = await NINA.Image.FileFormat.FITS.FITS.Load(new Uri(file),false,factory,CancellationToken.None);
                Console.WriteLine($"FITS loaded {Path.GetFileName(file)}: {loaded.Properties.Width}x{loaded.Properties.Height}, {watch.Elapsed.TotalMilliseconds:F1} ms");
                foreach (var size in new[] { 512,1024,loaded.Properties.Width }) {
                    int width=Math.Min(size,loaded.Properties.Width),height=Math.Min(size,loaded.Properties.Height);
                    var crop=new ushort[width*height];
                    int left=(loaded.Properties.Width-width)/2,top=(loaded.Properties.Height-height)/2;
                    for(int row=0;row<height;row++) Array.Copy(loaded.Data.FlatArray,(top+row)*loaded.Properties.Width+left,crop,row*width,width);
                    sources.Add((category,crop,width,height));
                }
            }
        } else {
            foreach (var (width,height) in new[] { (256,256), (512,512), (1024,1024), (2048,2048), (1536,1536), (4788,3194), (6416,4280), (9576,6388) }) {
                var values=new ushort[width*height];
                for(int i=0;i<values.Length;i++) values[i]=(ushort)(300+(i*1103515245u+12345u)%4096);
                sources.Add(("synthetic",values,width,height));
            }
        }
        var rows = new List<string> { "source,width,height,stage,p50_ms,p95_ms,managed_bytes_per_call" };
        foreach (var source in sources) {
            var (sourceName,pixels,w,h)=source;
            Console.WriteLine("Source: " + sourceName);
            BaseImageData NewData(bool snapshot = false) {
                var metadata = new ImageMetaData(); metadata.Image.ExposureTime = .1;
                return new BaseImageData(snapshot ? (ushort[])pixels.Clone() : pixels,w,h,16,false,metadata,profiles,detection,annotator);
            }
            var displayBytes = new byte[w*h];
            var bitmap = BitmapSource.Create(w,h,96,96,PixelFormats.Gray8,null,displayBytes,w); bitmap.Freeze();
            int samples = w > 4096 ? 8 : 20;
            async Task Bench(string stage, Func<Task> action) {
                if (verificationOnly) return;
                await action(); GC.Collect(); GC.WaitForPendingFinalizers();
                var times = new double[samples]; long allocated = GC.GetTotalAllocatedBytes(true);
                for (int i=0;i<samples;i++) { var watch=Stopwatch.StartNew(); await action(); times[i]=watch.Elapsed.TotalMilliseconds; }
                allocated = (GC.GetTotalAllocatedBytes(true)-allocated)/samples;
                Array.Sort(times);
                double p50=times[samples/2],p95=times[(int)Math.Ceiling(samples*.95)-1];
                rows.Add(string.Join(",",sourceName,w,h,stage,p50.ToString("F3",CultureInfo.InvariantCulture),p95.ToString("F3",CultureInfo.InvariantCulture),allocated));
                Console.WriteLine($"{w}x{h} {stage}: p50={p50:F3} ms p95={p95:F3} ms allocation={allocated/1048576.0:F3} MiB");
            }
            await Bench("bitmap_only_baseline", () => { imageProperty.SetValue(host,bitmap); return Task.CompletedTask; });
            await Bench("raw_and_bitmap_wrapper_only_no_host_api", () => { _=new RenderedImage(bitmap,NewData(),profiles,detection,annotator); return Task.CompletedTask; });
            await Bench("plugin_stretch_and_bitmap", () => {
                var raw=new Cwseo.NINA.ManualFocuser.LiveWorkflow.Models.FocusRawFrame(pixels,w,0,0,w,h);
                var bytes=Cwseo.NINA.ManualFocuser.LiveWorkflow.Models.FocusDisplayStretch.RenderRaw(raw);
                var output=BitmapSource.Create(w,h,96,96,PixelFormats.Gray8,null,bytes,w); output.Freeze();
                return Task.CompletedTask;
            });
            await Bench("native_render_and_stretch_cold_statistics", async () => {
                var result=await NewData().RenderImage().Stretch(.2,-2.8,false);
                if (result.Image == null) throw new Exception("Native stretch returned no image");
            });
            await Bench("host_prepare_no_stretch_no_stars", async () => {
                var data=NewData();
                var result=await (Task<IRenderedImage>)prepare.Invoke(host,new object[] {data,new PrepareImageParameters(false,false),CancellationToken.None});
                if (!ReferenceEquals(result.RawImageData,data) || !ReferenceEquals(renderedProperty.GetValue(host),result) || !ReferenceEquals(imageProperty.GetValue(host),result.Image)) throw new Exception("Host display/raw identity mismatch");
            });
            await Bench("host_prepare_plus_native_statistics", async () => {
                var data=NewData();
                await (Task<IRenderedImage>)prepare.Invoke(host,new object[] {data,new PrepareImageParameters(false,false),CancellationToken.None});
                await statistics.UpdateStatistics(data);
            });
            await Bench("host_prepare_plus_statistics_and_raw_snapshot", async () => {
                var data=NewData(true);
                await (Task<IRenderedImage>)prepare.Invoke(host,new object[] {data,new PrepareImageParameters(false,false),CancellationToken.None});
                await statistics.UpdateStatistics(data);
            });
            await Bench("host_prepare_autostretch_plus_statistics", async () => {
                var data=NewData();
                await (Task<IRenderedImage>)prepare.Invoke(host,new object[] {data,new PrepareImageParameters(true,false),CancellationToken.None});
                await statistics.UpdateStatistics(data);
            });
            await Bench("host_prepare_autostretch_statistics_panel_deferred", async () => {
                var data=NewData();
                await (Task<IRenderedImage>)prepare.Invoke(host,new object[] {data,new PrepareImageParameters(true,false),CancellationToken.None});
            });
            var statisticsBeforeLive=statistics.Statistics;
            var lastFrame=NewData();
            await (Task<IRenderedImage>)prepare.Invoke(host,new object[] {lastFrame,new PrepareImageParameters(true,false),CancellationToken.None});
            if (!ReferenceEquals(statistics.Statistics,statisticsBeforeLive) || !ReferenceEquals(((IRenderedImage)renderedProperty.GetValue(host)).RawImageData,lastFrame)) throw new Exception("Deferred statistics or live raw identity failed");
            Console.WriteLine("PASS live preparation updates current raw data without replacing the statistics panel");
            var final=(IRenderedImage)renderedProperty.GetValue(host);
            await (Task<bool>)hostType.GetMethod("ProcessImageHelper",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(host,null);
            var reprocessed=(IRenderedImage)renderedProperty.GetValue(host);
            if (!ReferenceEquals(final.RawImageData,reprocessed.RawImageData) || !ReferenceEquals(imageProperty.GetValue(host),reprocessed.Image)) throw new Exception("Native reprocessing lost the current raw frame or display identity");
            Console.WriteLine("PASS after updates stop, native reprocessing retains the latest raw frame and matching display");
            var stopWatch=Stopwatch.StartNew();
            await statistics.UpdateStatistics(lastFrame);
            if (ReferenceEquals(statistics.Statistics,statisticsBeforeLive)) throw new Exception("Statistics panel did not update on stop");
            Console.WriteLine($"PASS stop updates final-frame statistics using the cached result: {stopWatch.Elapsed.TotalMilliseconds:F3} ms");
            imageProperty.SetValue(host,null); renderedProperty.SetValue(host,null);
        }
        if (!verificationOnly) { Directory.CreateDirectory("bin"); File.WriteAllLines("bin/host-image-performance.csv",rows); }
        Console.WriteLine("Mono processing; no physical equipment, plate solver execution, debayer, star detection, GPU paint or camera capture measured. FITS disk loading is reported separately.");
    }
}
