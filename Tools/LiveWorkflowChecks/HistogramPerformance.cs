using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Image.ImageData;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;

internal static class HistogramPerformance {
    private static object Default(MethodInfo m) => m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
    public static async Task Run(string folder) {
        var settings=Fake.Of<IImageSettings>((m,a)=>m.Name switch {"get_AutoStretchFactor"=>.2,"get_BlackClipping"=>-2.8,_=>Default(m)});
        var profile=Fake.Of<IProfile>((m,a)=>m.Name=="get_ImageSettings"?settings:Default(m));
        var profiles=Fake.Of<IProfileService>((m,a)=>m.Name=="get_ActiveProfile"?profile:Default(m));
        var analysis=Fake.Of<IStarDetectionAnalysis>((m,a)=>Default(m));
        var detection=Fake.Of<IStarDetection>((m,a)=>m.Name=="CreateAnalysis"?analysis:Default(m));
        var factory=Fake.Of<IImageDataFactory>((m,a)=>m.Name=="CreateBaseImageData"
            ?new BaseImageData((ushort[])a[0],(int)a[1],(int)a[2],(int)a[3],(bool)a[4],(ImageMetaData)a[5],profiles,detection,null):throw new Exception(m.Name));
        var context=new AssemblyLoadContext("histogram-baseline",true);
        context.Resolving+=(_,name)=>AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a=>a.GetName().Name==name.Name);
        var baseline=context.LoadFromAssemblyPath(Path.GetFullPath("bin/histogram-baseline/Cwseo.NINA.ManualFocuser.dll"));
        var rawType=baseline.GetType(typeof(FocusRawFrame).FullName,true);
        var render=baseline.GetType(typeof(FocusDisplayStretch).FullName,true).GetMethod("RenderRaw");
        var build=typeof(FocusHistogramView).GetMethod("BuildCurve",BindingFlags.NonPublic|BindingFlags.Static);
        var rows=new List<string>{"file,width,height,stage,p50_ms,p95_ms,managed_allocated_bytes"};
        foreach(string file in Directory.GetFiles(folder,"*.fits").OrderBy(f=>f)) {
            var image=await NINA.Image.FileFormat.FITS.FITS.Load(new Uri(file),false,factory,default);
            foreach(int size in new[]{512,1024,2048,image.Properties.Width}) {
                int w=Math.Min(size,image.Properties.Width),h=Math.Min(size,image.Properties.Height);
                int left=(image.Properties.Width-w)/2,top=(image.Properties.Height-h)/2;
                FocusRawFrame Frame()=>new(image.Data.FlatArray,image.Properties.Width,left,top,w,h,image);
                object OldFrame()=>Activator.CreateInstance(rawType,new object[]{image.Data.FlatArray,image.Properties.Width,left,top,w,h,image,false});
                var args=new object[render.GetParameters().Length];args[1]=1d;args[2]=true;
                byte[] previous=null,current=null;FocusHistogramPlot plot=null;
                void Bench(string stage,Action action) {
                    var warmup=Stopwatch.StartNew();do{action();}while(warmup.ElapsedMilliseconds<150);
                    GC.Collect();GC.WaitForPendingFinalizers();
                    int count=w>4096?5:32;long allocated=GC.GetTotalAllocatedBytes(true);var times=new double[count];
                    for(int i=0;i<count;i++){var sw=Stopwatch.StartNew();action();times[i]=sw.Elapsed.TotalMilliseconds;}
                    allocated=(GC.GetTotalAllocatedBytes(true)-allocated)/count;Array.Sort(times);
                    rows.Add(string.Join(",",Path.GetFileName(file),w,h,stage,times[count/2].ToString("F4",CultureInfo.InvariantCulture),times[(int)Math.Ceiling(count*.95)-1].ToString("F4",CultureInfo.InvariantCulture),allocated));
                    Console.WriteLine($"{Path.GetFileName(file)} {w}x{h} {stage}: p50 {times[count/2]:F4} ms, p95 {times[(int)Math.Ceiling(count*.95)-1]:F4} ms, {allocated} bytes");
                }
                Bench("before_histogram",()=>{args[0]=OldFrame();previous=(byte[])render.Invoke(null,args);});
                Bench("with_histogram_data",()=>current=FocusDisplayStretch.RenderRaw(Frame()));
                if(!previous.SequenceEqual(current))throw new Exception("Histogram support changed displayed pixels");
                var manual=FocusStretchLevels.Default with {InputBlack=.02,InputMidtone=.3,InputWhite=.9};
                Bench("manual_three_handle_levels",()=>_ = FocusDisplayStretch.RenderRaw(Frame(),levels:manual));
                var histogram=FocusDisplayStretch.GetHistogram(Frame());
                Bench("project_256_bins",()=>plot=FocusDisplayStretch.PlotHistogram(histogram));
                Bench("build_two_vector_paths",()=>{
                    _=build.Invoke(null,new object[]{plot,new Size(340,112),true,false});
                    _=build.Invoke(null,new object[]{plot,new Size(340,112),true,true});
                });
                Console.WriteLine($"  Exact pixels unchanged; {histogram.OccupiedLevels} occupied levels, {histogram.StorageBytes} bytes retained for histogram.");
            }
            File.WriteAllLines("bin/histogram-performance.csv",rows);
        }
        context.Unload();
        Console.WriteLine("CPU only: fresh-frame processing, cached histogram projection and vector path construction. Disk decoding, equipment, HFR and GPU composition are excluded. Popup updates are capped at ~3 Hz and stopped while closed.");
    }
}
