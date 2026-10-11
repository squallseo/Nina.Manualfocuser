using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;
using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;

internal static class MultiStarHfrChecks {
    private static object Default(MethodInfo method) => method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    public static async Task Run(Action<bool,string> check, string folder = null) {
        var settings = Fake.Of<IImageSettings>((m,a) => m.Name switch {
            "get_AutoStretchFactor" => .2, "get_BlackClipping" => -2.8,
            "get_StarSensitivity" => StarSensitivityEnum.Normal, _ => Default(m)
        });
        int brightest=0;
        double innerCrop=1;
        bool manualCrop=false;
        bool detectAllExpected=false;
        var focuserSettings = Fake.Of<IFocuserSettings>((m,a) => m.Name switch {
            "get_AutoFocusUseBrightestStars" => brightest, "get_AutoFocusInnerCropRatio" => innerCrop,
            "get_AutoFocusOuterCropRatio" => 1.0, _ => Default(m)
        });
        var profile = Fake.Of<IProfile>((m,a) => m.Name switch { "get_ImageSettings" => settings,"get_FocuserSettings" => focuserSettings, _ => Default(m) });
        var profiles = Fake.Of<IProfileService>((m,a) => m.Name == "get_ActiveProfile" ? profile : Default(m));
        var native = new StarDetection(); StarDetectionResult last = null;
        var detector = Fake.Of<IStarDetection>((m,a) => m.Name == "Detect" ? Detect(a) : m.Invoke(native,a));
        async Task<StarDetectionResult> Detect(object[] args) {
            var p = (StarDetectionParams)args[2];
            if (manualCrop) check(p.IsAutoFocus && p.UseROI && p.InnerCropRatio==.5 && p.OuterCropRatio==1,"Manual capture applies the configured native autofocus crop");
            else check(p.IsAutoFocus && p.NumberOfAFStars == (detectAllExpected ? 0 : brightest) && !p.UseROI,"Uses the autofocus multi-star parameters, without a second crop");
            return last = await native.Detect((IRenderedImage)args[0],(System.Windows.Media.PixelFormat)args[1],p,(IProgress<NINA.Core.Model.ApplicationStatus>)args[3],(CancellationToken)args[4]);
        }
        var selectors = Fake.Of<IPluggableBehaviorSelector<IStarDetection>>((m,a) => m.Name == "GetBehavior" ? detector : Default(m));
        var annotator = Fake.Of<IStarAnnotator>((m,a) => throw new Exception("Unexpected annotation"));
        var factory = Fake.Of<IImageDataFactory>((m,a) => m.Name == "CreateBaseImageData" ?
            new BaseImageData((ushort[])a[0],(int)a[1],(int)a[2],(int)a[3],(bool)a[4],(ImageMetaData)a[5],profiles,detector,annotator) : throw new Exception(m.Name));
        var camera = Fake.Of<ICameraMediator>((m,a) => m.Name == "GetInfo" ? new CameraInfo() : Default(m));
        var analyzer = new NinaMultiStarHfr(profiles,factory,selectors,camera);
        const int size=512;
        var pixels = Enumerable.Range(0,size*size).Select(i => (ushort)(400+(i*13)%17)).ToArray();
        foreach (var (sx,sy,sigma) in new[] {(80,80,2.0),(400,90,3.0),(100,400,4.0),(380,380,2.5)}) {
            for (int y=sy-24;y<=sy+24;y++) for (int x=sx-24;x<=sx+24;x++) pixels[y*size+x]+=(ushort)(30000*Math.Exp(-((x-sx)*(x-sx)+(y-sy)*(y-sy))/(2*sigma*sigma)));
        }
        var data = factory.CreateBaseImageData(pixels,size,size,16,false,new ImageMetaData());
        var rawCopy=(ushort[])pixels.Clone();
        var result = await analyzer.AnalyzeAsync(new FocusRawFrame(pixels,size,0,0,size,size,data),default);
        check(result.Stars >= 3 && last.StarList.Any(s=>s.Position.X<128 || s.Position.X>384),"Multiple stars outside the former central 256px window contribute");
        check(Math.Abs(result.Hfr-last.AverageHFR)<1e-12 && result.Stars==last.StarList.Count,"Reports the selected detector's aggregate HFR and contributing star count");
        check(result.StarSamples.Length==result.Stars && result.StarSamples.Zip(last.StarList).All(pair=>
            pair.First.X==pair.Second.Position.X && pair.First.Y==pair.Second.Position.Y && pair.First.Hfr==pair.Second.HFR),
            "Star boxes reuse the exact contributing native detector positions and individual HFRs");
        check(result.StarSamples.Zip(last.StarList).All(pair=>pair.First.Bounds==
            new System.Windows.Rect(pair.Second.BoundingBox.X,pair.Second.BoundingBox.Y,pair.Second.BoundingBox.Width,pair.Second.BoundingBox.Height)),
            "The selected detector's actual star boundaries survive HFR analysis for enclosing overlays");
        var softwareRoi=new FocusRawFrame(pixels,size,32,32,448,448,data);
        var roiResult=await analyzer.AnalyzeAsync(softwareRoi,default);
        check(roiResult.Left==32 && roiResult.Top==32 && roiResult.StarSamples.Length>=3 &&
            roiResult.StarSamples.Zip(last.StarList).All(pair=>pair.First.X==pair.Second.Position.X && pair.First.Y==pair.Second.Position.Y),
            "Software ROI annotations retain crop-local coordinates and the original source offset");
        StarBoxChecks.Run(check);
        check(last.StarList.Max(s=>s.HFR)-last.StarList.Min(s=>s.HFR)>.3,"The aggregate combines stars with different HFR values");
        check(rawCopy.SequenceEqual(pixels),"Successful stretch and star analysis never alter the source pixels");
        brightest=2;
        var limited=await analyzer.AnalyzeAsync(new FocusRawFrame(pixels,size,0,0,size,size,data),default);
        check(limited.Stars==2 && Math.Abs(limited.Hfr-last.StarList.Average(s=>s.HFR))<1e-12,"Configured brightest-star limit counts contributing stars, not all detections");
        detectAllExpected=true;
        var all=await analyzer.AnalyzeAsync(new FocusRawFrame(pixels,size,0,0,size,size,data),true,default);
        var tracker=new FocusStarTracker();tracker.Match(limited);
        check(all.Stars>limited.Stars && tracker.Match(all).Stars==limited.Stars,
            "Tracking detects all candidates once, but only the original selected stars contribute to HFR");
        detectAllExpected=false;
        brightest=0;
        innerCrop=.5; manualCrop=true;
        var cropped=await analyzer.AnalyzeAsync(new FocusRawFrame(pixels,size,0,0,size,size,data,useAutofocusRoi:true),default);
        check(cropped.Stars==0 && rawCopy.SequenceEqual(pixels),"Manual autofocus crop excludes outer stars without altering full source pixels");
        innerCrop=1; manualCrop=false;
        var empty=new ushort[size*size];
        var none=await analyzer.AnalyzeAsync(new FocusRawFrame(empty,size,0,0,size,size),default);
        check(none.Stars==0 && double.IsNaN(none.Hfr),"No detected stars yields an empty metric without a single-star fallback");
        using (var cancel = new CancellationTokenSource()) {
            cancel.Cancel(); bool canceled=false;
            try { await analyzer.AnalyzeAsync(new FocusRawFrame(pixels,size,0,0,size,size,data),cancel.Token); } catch(OperationCanceledException) { canceled=true; }
            check(canceled && rawCopy.SequenceEqual(pixels),"Cancellation never alters the raw source used by image sync");
        }
        if (folder == null) return;
        if (Environment.GetCommandLineArgs().Contains("--star-box-files")) {
            foreach (var category in new[] {"initial","attempt01","final"}) {
                var file=Directory.GetFiles(Path.Combine(folder,category),"*.fits").OrderBy(f=>f).First();
                var loaded=await NINA.Image.FileFormat.FITS.FITS.Load(new Uri(file),false,factory,CancellationToken.None);
                const int crop=1024;
                int left=(loaded.Properties.Width-crop)/2,top=(loaded.Properties.Height-crop)/2;
                var frame=new FocusRawFrame(loaded.Data.FlatArray,loaded.Properties.Width,left,top,crop,crop,loaded);
                var measured=await analyzer.AnalyzeAsync(frame,default);
                measured=measured with {StarSamples=measured.StarSamples.Where(s=>s.X>40&&s.X<crop-40&&s.Y>40&&s.Y<crop-40).ToArray()};
                var shiftedPixels=new ushort[crop*crop];
                for(int y=0;y<crop-3;y++) for(int x=5;x<crop;x++)
                    shiftedPixels[y*crop+x]=frame.Pixels[(top+y+3)*frame.Stride+left+x-5];
                var shifted=new FocusRawFrame(shiftedPixels,crop,0,0,crop,crop);
                var localMeasurement=measured with {Left=0,Top=0};
                var clock=Stopwatch.StartNew();var corrected=FocusStarBoxLocator.Locate(localMeasurement,shifted);clock.Stop();
                // Native detection and this bounded centroid use different
                // pixel weighting. Allow 1.5px, well below the simulated 5/3px
                // movement; a stale detector position fails this check.
                check(corrected.StarSamples.Length>0 && corrected.SourceFrameId==shifted.FrameId &&
                    corrected.StarSamples.All(s=>measured.StarSamples.Any(b=>Math.Abs(s.X-b.X-5)<1.5&&Math.Abs(s.Y-b.Y+3)<1.5)),
                    $"Actual {category} FITS boxes follow a new frame shifted +5/-3 pixels instead of stale detector coordinates");
                Console.WriteLine($"Actual {category}: {measured.StarSamples.Length} measured, {corrected.StarSamples.Length} current boxes, centroid refresh {clock.Elapsed.TotalMilliseconds:F2} ms");
            }
            return;
        }
        Directory.CreateDirectory("bin");
        var rows = new List<string> {"category,width,height,stars,hfr,median_ms,max_ms"};
        foreach (var category in new[] {"initial","attempt01","final"}) {
            var file=Directory.GetFiles(Path.Combine(folder,category),"*.fits").OrderBy(f=>f).First();
            var loaded=await NINA.Image.FileFormat.FITS.FITS.Load(new Uri(file),false,factory,CancellationToken.None);
            foreach(int width in new[]{512,1024,loaded.Properties.Width}) {
                int height=Math.Min(width,loaded.Properties.Height),left=(loaded.Properties.Width-width)/2,top=(loaded.Properties.Height-height)/2;
                var frame=new FocusRawFrame(loaded.Data.FlatArray,loaded.Properties.Width,left,top,width,height,loaded);
                var timings=new List<double>(); FocusHfrMeasurement measured=null;
                for(int i=0;i<4;i++) {
                    var clock=Stopwatch.StartNew(); measured=await analyzer.AnalyzeAsync(frame,default);
                    if(i>0)timings.Add(clock.Elapsed.TotalMilliseconds);
                }
                timings.Sort();
                string row=FormattableString.Invariant($"{category},{width},{height},{measured.Stars},{measured.Hfr:F4},{timings[1]:F1},{timings[^1]:F1}");
                rows.Add(row);Console.WriteLine(row);
            }
        }
        File.WriteAllLines("bin/multi-star-performance.csv",rows);
        Console.WriteLine("Installed NINA detector benchmark; Hocus Focus provider and physical camera streaming were not benchmarked.");
    }
}
