using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;

internal static class StretchComparison {
    private static object Default(MethodInfo m) => m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null;
    public static async Task Run(string folder) {
        string output=Path.GetFullPath("bin/stretch-comparison");Directory.CreateDirectory(output);
        var settings=Fake.Of<IImageSettings>((m,a)=>m.Name switch {"get_AutoStretchFactor"=>.2,"get_BlackClipping"=>-2.8,_=>Default(m)});
        var profile=Fake.Of<IProfile>((m,a)=>m.Name=="get_ImageSettings"?settings:Default(m));
        var profiles=Fake.Of<IProfileService>((m,a)=>m.Name=="get_ActiveProfile"?profile:Default(m));
        var analysis=Fake.Of<IStarDetectionAnalysis>((m,a)=>Default(m));
        var detection=Fake.Of<IStarDetection>((m,a)=>m.Name=="CreateAnalysis"?analysis:Default(m));
        BaseImageData Data(ushort[] p,int w,int h,int depth=16)=>new(p,w,h,depth,false,new ImageMetaData(),profiles,detection,null);
        var factory=Fake.Of<IImageDataFactory>((m,a)=>m.Name=="CreateBaseImageData"
            ?new BaseImageData((ushort[])a[0],(int)a[1],(int)a[2],(int)a[3],(bool)a[4],(ImageMetaData)a[5],profiles,detection,null):throw new Exception(m.Name));
        var context=new AssemblyLoadContext("stretch-baseline",true);
        context.Resolving+=(_,name)=>AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a=>a.GetName().Name==name.Name);
        var baseline=context.LoadFromAssemblyPath(Path.GetFullPath("bin/stretch-baseline/Cwseo.NINA.ManualFocuser.dll"));
        var rawType=baseline.GetType(typeof(FocusRawFrame).FullName,true);
        var legacy=baseline.GetType(typeof(FocusDisplayStretch).FullName,true).GetMethod("RenderRaw");
        var rows=new List<string>{"file,width,height,stage,p50_ms,p95_ms,managed_allocated_mib"};
        var quality=new List<string>{"file,width,height,old_background,new_background,nina_background,new_nina_mae_8bit,new_nina_p99_error_8bit,old_saturated_pct,new_saturated_pct"};
        foreach(string file in Directory.GetFiles(folder,"*.fits").OrderBy(f=>f)) {
            var loaded=await NINA.Image.FileFormat.FITS.FITS.Load(new Uri(file),false,factory,CancellationToken.None);
            foreach(int size in new[]{512,1024,2048,loaded.Properties.Width}) {
                int w=Math.Min(size,loaded.Properties.Width),h=Math.Min(size,loaded.Properties.Height);
                var pixels=new ushort[w*h];
                for(int y=0;y<h;y++)Array.Copy(loaded.Data.FlatArray,((loaded.Properties.Height-h)/2+y)*loaded.Properties.Width+(loaded.Properties.Width-w)/2,pixels,y*w,w);
                int depth=loaded.Properties.BitDepth;
                var metadata=Data(pixels,w,h,depth);
                FocusRawFrame Frame()=>new(pixels,w,0,0,w,h,metadata);
                var cached=Frame();
                object LegacyFrame()=>Activator.CreateInstance(rawType,new object[]{pixels,w,0,0,w,h,metadata,false});
                var legacyArgs=new object[]{LegacyFrame(),1d,true};
                byte[] before=null,after=null;BitmapSource native=null;BaseImageData nativeData=null;
                int count=w>4096?4:12;
                async Task Bench(string name,Func<Task> action) {
                    var warmup=Stopwatch.StartNew();
                    do { await action(); } while(warmup.ElapsedMilliseconds<150);
                    GC.Collect();GC.WaitForPendingFinalizers();
                    long allocated=GC.GetTotalAllocatedBytes(true);var times=new double[count];
                    for(int i=0;i<count;i++){var sw=Stopwatch.StartNew();await action();times[i]=sw.Elapsed.TotalMilliseconds;}
                    allocated=(GC.GetTotalAllocatedBytes(true)-allocated)/count;
                    Array.Sort(times);
                    double p50=times[count/2],p95=times[(int)Math.Ceiling(count*.95)-1];
                    rows.Add(string.Join(",",Path.GetFileName(file),w,h,name,p50.ToString("F3",CultureInfo.InvariantCulture),p95.ToString("F3",CultureInfo.InvariantCulture),(allocated/1048576d).ToString("F3",CultureInfo.InvariantCulture)));
                    Console.WriteLine($"{Path.GetFileName(file)} {w}x{h} {name}: p50 {p50:F2} ms, p95 {p95:F2} ms, {allocated/1048576d:F2} MiB");
                }
                await Bench("previous_1.2.0.1",()=>{legacyArgs[0]=LegacyFrame();before=(byte[])legacy.Invoke(null,legacyArgs);return Task.CompletedTask;});
                await Bench("new_frame",()=>{after=FocusDisplayStretch.RenderRaw(Frame());return Task.CompletedTask;});
                await Bench("new_cached_levels",()=>{_=FocusDisplayStretch.RenderRaw(cached,levels:new(-3.2,.85,.2));return Task.CompletedTask;});
                await Bench("nina_cold_statistics",async()=>{nativeData=Data(pixels,w,h,depth);native=(await nativeData.RenderImage().Stretch(.2,-2.8,false)).Image;});
                var stats=await nativeData.Statistics;
                var estimate=(FocusHistogram)typeof(FocusDisplayStretch).GetMethod("Measure",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{pixels.Length,(Func<int,double>)(i=>pixels[i])});
                Console.WriteLine($"  Stats native median/MAD={stats.Median}/{stats.MedianAbsoluteDeviation}, plugin={estimate.Median}/{estimate.Mad}, depth={depth}");
                var converted=new FormatConvertedBitmap(native,PixelFormats.Gray8,null,0);converted.Freeze();
                var nativeBytes=new byte[w*h];converted.CopyPixels(nativeBytes,w,0);
                int samples=Math.Min(65536,after.Length);
                var errors=new int[samples];var oldSample=new byte[samples];var newSample=new byte[samples];var nativeSample=new byte[samples];
                int oldClipped=0,newClipped=0;
                for(int i=0;i<samples;i++) {
                    int ix=(int)((long)i*after.Length/samples);
                    oldSample[i]=before[ix];newSample[i]=after[ix];nativeSample[i]=nativeBytes[ix];
                    errors[i]=Math.Abs(after[ix]-nativeBytes[ix]);
                    if(before[ix]==255)oldClipped++;if(after[ix]==255)newClipped++;
                }
                double mae=errors.Average();Array.Sort(errors);Array.Sort(oldSample);Array.Sort(newSample);Array.Sort(nativeSample);
                int p99=errors[(int)(samples*.99)];
                quality.Add(string.Join(",",Path.GetFileName(file),w,h,oldSample[samples/2],newSample[samples/2],nativeSample[samples/2],mae.ToString("F3",CultureInfo.InvariantCulture),p99,(100d*oldClipped/samples).ToString("F3",CultureInfo.InvariantCulture),(100d*newClipped/samples).ToString("F3",CultureInfo.InvariantCulture)));
                Console.WriteLine($"  Background old/new/NINA: {oldSample[samples/2]}/{newSample[samples/2]}/{nativeSample[samples/2]}, new/native MAE={mae:F2}, p99={p99} /255");
                if(size==1024) {
                    // Save the identical central image region at its original
                    // pixel scale; no control rendering or synthetic stars.
                    int crop=512;
                    var sheet=new byte[crop*3*crop];
                    var variants=new[]{before,after,nativeBytes};
                    for(int v=0;v<3;v++)for(int y=0;y<crop;y++)Array.Copy(variants[v],((h-crop)/2+y)*w+(w-crop)/2,sheet,y*crop*3+v*crop,crop);
                    var image=BitmapSource.Create(crop*3,crop,96,96,PixelFormats.Gray8,null,sheet,crop*3);image.Freeze();
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                    using(var stream=File.Create(Path.Combine(output,Path.GetFileNameWithoutExtension(file)+"-old-new-nina.png")))encoder.Save(stream);
                }
            }
            File.WriteAllLines(Path.Combine(output,"performance.csv"),rows);
            File.WriteAllLines(Path.Combine(output,"quality.csv"),quality);
        }
        context.Unload();
        Console.WriteLine("Mono CPU stretch only; no capture, HFR, disk decoding, debayer or screen painting included in timings. Previous DLL loaded separately; new-frame measurements include fresh histograms. Contact sheets: previous | new | NINA.");
    }
}
