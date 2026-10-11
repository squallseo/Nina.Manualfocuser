using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

internal static class PreviewPerformance {
    public static void Run() {
        Exception failure=null;
        var thread=new Thread(()=>{try{Measure();}catch(Exception e){failure=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)throw new Exception("Preview performance measurement failed",failure);
    }
    private static void Measure() {
        var rows=new List<string>{"width,height,stage,p50_ms,p95_ms,managed_bytes_per_call"};
        foreach(var (w,h) in new[]{(512,512),(1024,1024),(2048,2048),(4096,4096),(5744,4256),(9576,6388)}) {
            Console.WriteLine($"Synthetic processing only: {w}x{h}");
            var raw=new ushort[w*h];
            for(int i=0;i<raw.Length;i++)raw[i]=(ushort)(300+(i*1103515245u+12345u)%80);
            for(int y=h/2-8;y<=h/2+8;y++)for(int x=w/2-8;x<=w/2+8;x++)
                raw[y*w+x]=(ushort)(340+40000*Math.Exp(-((x-w/2)*(x-w/2)+(y-h/2)*(y-h/2))/8.0));
            double[] pixels=null;byte[] display=null;BitmapSource bitmap=null;
            int count=w>=4096?5:12;
            void Bench(string stage,Action action) {
                action();GC.Collect();GC.WaitForPendingFinalizers();
                var times=new double[count];long allocated=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<count;i++){var clock=Stopwatch.StartNew();action();times[i]=clock.Elapsed.TotalMilliseconds;}
                allocated=(GC.GetAllocatedBytesForCurrentThread()-allocated)/count;
                Array.Sort(times);double median=times[times.Length/2],p95=times[(int)Math.Ceiling(times.Length*.95)-1];
                rows.Add(string.Join(",",w,h,stage,median.ToString("F3",CultureInfo.InvariantCulture),p95.ToString("F3",CultureInfo.InvariantCulture),allocated));
                Console.WriteLine($"  {stage}: p50 {median:F2} ms, p95 {p95:F2} ms, allocation {allocated/1048576.0:F2} MiB");
            }
            Bench("raw_to_double",()=>{pixels=new double[raw.Length];for(int i=0;i<raw.Length;i++)pixels[i]=raw[i];});
            Bench("stretch",()=>display=FocusDisplayStretch.Render(pixels));
            var rawFrame = new FocusRawFrame(raw,w,0,0,w,h);
            Bench("raw_stretch_lookup",()=>display=FocusDisplayStretch.RenderRaw(rawFrame));
            Bench("bitmap",()=>{bitmap=BitmapSource.Create(w,h,96,96,PixelFormats.Gray8,null,display,w);bitmap.Freeze();});
            Bench("hfr_central_256",()=>{var center=FocusRoi.CenterWindow(pixels,w,h);_ =QuickFocusMetrics.HalfFluxRadius(center.Pixels,center.Width,center.Height);});
            Bench("raw_hfr_central_256",()=>{var center=rawFrame.CenterWindow();_ =QuickFocusMetrics.HalfFluxRadius(center.Pixels,center.Width,center.Height);});
            GC.KeepAlive(bitmap);
        }
        Directory.CreateDirectory("bin");File.WriteAllLines("bin/preview-performance.csv",rows);
    }
}
