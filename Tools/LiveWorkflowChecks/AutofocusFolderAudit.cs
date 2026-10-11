using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class AutofocusCurveAnalysis {
    public static void RunAudit(string folder) {
        Exception failure=null;
        var thread=new Thread(()=>{try {
            var runs=Directory.Exists(Path.Combine(folder,"attempt01"))?new[]{folder}:Directory.GetDirectories(folder).OrderBy(x=>x).ToArray();
            foreach(var run in runs)Audit(run);
        }catch(Exception e){failure=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)throw new Exception("Full autofocus folder audit failed",failure);
    }
    private sealed record PixelAudit(int Width,int Height,int Median,int Mad,int P99,int P999,int Maximum,long Saturated65000,double Mean,double StdDev,
        string Exposure,string Filter,string Binning,string Gain,string FocuserPosition,string Timestamp,long BytesScanned);
    private static void Audit(string folder) {
        string name=Path.GetFileName(folder),output=Path.GetFullPath(Path.Combine("bin","autofocus-analysis",name));Directory.CreateDirectory(output);
        var report=JObject.Parse(File.ReadAllText(Path.Combine(folder,"attempt01","autofocus_report_Region0.json")));
        var frames=new List<Frame>();var stages=new List<object>();var audit=new List<object>();
        foreach(string stage in new[]{"initial","attempt01","final"}) {
            string path=Path.Combine(folder,stage);
            var fits=Directory.GetFiles(path,"*.fits").OrderBy(x=>x).ToArray();
            var detections=Directory.GetFiles(path,"*star_detection_result.json").OrderBy(x=>x).Select(ReadFrame).ToArray();
            if(fits.Length!=detections.Length || fits.Length==0)throw new Exception($"{name}/{stage}: unmatched image and detection counts");
            var paired=new HashSet<string>(detections.Select(d=>FindFits(d.File)),StringComparer.OrdinalIgnoreCase);
            if(!fits.All(f=>paired.Contains(f)))throw new Exception("Unpaired FITS source");
            foreach(var frame in detections) {
                string file=FindFits(frame.File);var stats=ScanPixels(file);
                if(stats.Width!=9576 || stats.Height!=6388)throw new Exception("Unexpected QHY600M frame dimensions");
                audit.Add(new{Stage=stage,File=Path.GetFileName(file),FilenamePosition=frame.Position,DetectionJsonPosition=frame.JsonPosition,
                    SavedHFR=frame.Hfr,ContributingStars=frame.Stars.Count,Pixels=stats});
                Console.WriteLine($"SCAN {name}/{stage}/{Path.GetFileName(file)}: {stats.BytesScanned} bytes, median {stats.Median}, saturation {stats.Saturated65000}");
            }
            frames.AddRange(detections);
            double mean=detections.Average(f=>f.Hfr);
            stages.Add(new{Stage=stage,Frames=detections.Length,MeanHFR=mean,MinimumHFR=detections.Min(f=>f.Hfr),MaximumHFR=detections.Max(f=>f.Hfr),
                MinimumStars=detections.Min(f=>f.Stars.Count),MaximumStars=detections.Max(f=>f.Stars.Count)});
            if(stage=="initial" && Math.Abs(mean-(double)report["InitialFocusPoint"]["Value"])>1e-10)throw new Exception("Initial HFR does not reproduce report");
            if(stage=="final" && Math.Abs(mean-(double)report["FinalHFR"])>1e-10)throw new Exception("Final HFR does not reproduce report");
        }
        var initial=frames.Where(f=>Path.GetFileName(Path.GetDirectoryName(f.File))=="initial").ToArray();
        var final=frames.Where(f=>Path.GetFileName(Path.GetDirectoryName(f.File))=="final").ToArray();
        var attempt=frames.Where(f=>Path.GetFileName(Path.GetDirectoryName(f.File))=="attempt01").ToArray();
        double bestPosition=report["MeasurePoints"].MinBy(p=>(double)p["Value"]).Value<double>("Position");
        var reference=attempt.OrderBy(f=>Math.Abs(f.Position-bestPosition)).ThenBy(f=>f.File).First();
        var matches=frames.Select(f=>Match(reference.Stars,f.Stars,8)).ToArray();
        var common=Enumerable.Range(0,reference.Stars.Count).Where(i=>matches.All(m=>m.ContainsKey(i))).ToArray();
        var fixedStarComparison=common.Length==0?null:new{
            Stars=common.Length,InitialMeanHFR=initial.Average(f=>common.Average(i=>matches[frames.IndexOf(f)][i].Hfr)),
            FinalMeanHFR=final.Average(f=>common.Average(i=>matches[frames.IndexOf(f)][i].Hfr))};
        if(common.Length>0)ContactSheet(frames.ToArray(),reference,matches,common,output);
        var result=new{Run=name,Source=folder,CheckedFrames=frames.Count,ExpectedFrames=Directory.GetFiles(folder,"*.fits",SearchOption.AllDirectories).Length,
            RawPixelsScanned=true,PairingAndReportedStageMeansVerified=true,Stages=stages,
            InitialHFR=initial.Average(f=>f.Hfr),FinalHFR=final.Average(f=>f.Hfr),
            FinalChangePercent=100*(final.Average(f=>f.Hfr)/initial.Average(f=>f.Hfr)-1),FixedStarComparison=fixedStarComparison,
            MetadataPositionMismatches=frames.Count(f=>f.Position!=f.JsonPosition),Frames=audit,
            Notes=new[]{"Every image's entire 16-bit pixel payload was scanned, not just the header or a sampled crop.",
                "No new autofocus run or selected-detector recalculation was performed. Curve fitting uses saved Hocus Focus measurements.",
                "The contact sheet shows the same object in every frame, with per-frame translation correction and a shared display stretch.",
                "A before/after HFR change cannot by itself isolate seeing, backlash, star selection, or fitting error."}};
        if(result.CheckedFrames!=result.ExpectedFrames)throw new Exception("Images outside recognized capture stages were not audited");
        File.WriteAllText(Path.Combine(output,"full-folder-audit.json"),JsonConvert.SerializeObject(result,Formatting.Indented));
        File.WriteAllLines(Path.Combine(output,"all-stage-measurements.csv"),new[]{"stage,file,filename_position,json_position,hfr,stars"}.Concat(frames.Select(f=>FormattableString.Invariant($"{Path.GetFileName(Path.GetDirectoryName(f.File))},{Path.GetFileName(f.File)},{f.Position},{f.JsonPosition},{f.Hfr:R},{f.Stars.Count}"))));
        Console.WriteLine($"COMPLETE {name}: {frames.Count} entire images; initial HFR {result.InitialHFR:F4}, final {result.FinalHFR:F4}, change {result.FinalChangePercent:+0.00;-0.00}% ; common stars {common.Length}");
        if(fixedStarComparison!=null)Console.WriteLine($"SAME STARS: {fixedStarComparison.InitialMeanHFR:F4} -> {fixedStarComparison.FinalMeanHFR:F4}");
    }
    private static PixelAudit ScanPixels(string path) {
        using var stream=File.OpenRead(path);var cardBytes=new byte[80];var header=new Dictionary<string,string>();
        while(true) {
            stream.ReadExactly(cardBytes);string card=System.Text.Encoding.ASCII.GetString(cardBytes),key=card[..8].Trim();
            if(key=="END")break;
            if(card[8]=='=')header[key]=card[10..].Split('/')[0].Trim().Trim('\'');
        }
        int width=int.Parse(header["NAXIS1"]),height=int.Parse(header["NAXIS2"]);
        if(header["BITPIX"]!="16" || header["NAXIS"]!="2" || header["BZERO"]!="32768" || header.TryGetValue("BSCALE",out var scale) && double.Parse(scale,CultureInfo.InvariantCulture)!=1)
            throw new Exception("Unsupported FITS encoding");
        stream.Position=((stream.Position+2879)/2880)*2880;
        long count=(long)width*height,totalBytes=count*2,remaining=totalBytes;
        if(stream.Length<stream.Position+totalBytes)throw new Exception("Truncated FITS pixel payload");
        var histogram=new int[65536];var buffer=new byte[4*1024*1024];
        while(remaining>0) {
            int bytes=(int)Math.Min(buffer.Length,remaining);stream.ReadExactly(buffer.AsSpan(0,bytes));
            for(int i=0;i<bytes;i+=2)histogram[((buffer[i]<<8)|buffer[i+1])^32768]++;
            remaining-=bytes;
        }
        int Quantile(int[] hist,double q) {long cumulative=0,target=(long)Math.Ceiling(count*q);for(int i=0;i<hist.Length;i++){cumulative+=hist[i];if(cumulative>=target)return i;}throw new Exception("Missing pixels");}
        int median=Quantile(histogram,.5);var deviation=new int[65536];double sum=0,square=0;long saturated=0;
        int maximum=0;
        for(int i=0;i<histogram.Length;i++) {
            deviation[Math.Abs(i-median)]+=histogram[i];sum+=(double)i*histogram[i];square+=(double)i*i*histogram[i];
            if(i>=65000)saturated+=histogram[i];if(histogram[i]>0)maximum=i;
        }
        string Get(string key)=>header.TryGetValue(key,out var value)?value:"";
        return new(width,height,median,Quantile(deviation,.5),Quantile(histogram,.99),Quantile(histogram,.999),maximum,saturated,
            sum/count,Math.Sqrt(Math.Max(0,square/count-Math.Pow(sum/count,2))),Get("EXPTIME"),Get("FILTER"),Get("XBINNING")+"x"+Get("YBINNING"),
            Get("GAIN"),Get("FOCPOS"),Get("DATE-OBS"),totalBytes);
    }
    private static void ContactSheet(Frame[] frames,Frame reference,Dictionary<int,Star>[] matches,int[] common,string output) {
        const int size=96,cols=6,cellWidth=146,cellHeight=158,top=82;
        int index=common.MinBy(i=>Math.Pow(reference.Stars[i].X-4788,2)+Math.Pow(reference.Stars[i].Y-3194,2));
        var tiles=frames.Select((f,j)=>{var star=matches[j][index];return ReadTile(FindFits(f.File),(int)Math.Round(star.X)-48,(int)Math.Round(star.Y)-48,size);}).ToArray();
        double stretch=tiles.Max(p=>p.Max()-Median(p));int height=top+(int)Math.Ceiling(frames.Length/(double)cols)*cellHeight;
        var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.NearestNeighbor);
        using(var dc=visual.RenderOpen()) {
            dc.DrawRectangle(Brushes.White,null,new Rect(0,0,cols*cellWidth,height));
            void Label(string s,double x,double y,int font=12)=>dc.DrawText(new FormattedText(s,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Arial"),font,Brushes.Black,1),new System.Windows.Point(x,y));
            Label(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(reference.File))),10,10,20);
            Label($"All {frames.Length} exposures: the same star, same stretch, 96 x 96 pixels per crop",10,37);
            Label("Order: initial captures, every autofocus sample, final validation captures",10,56);
            for(int j=0;j<frames.Length;j++) {
                var f=frames[j];var p=tiles[j];double bg=Median(p);var star=matches[j][index];
                int x=(j%cols)*cellWidth+8,y=top+(j/cols)*cellHeight;
                Label($"{Path.GetFileName(Path.GetDirectoryName(f.File))} / {Path.GetFileName(f.File).Split('_')[0]}",x,y);
                Label($"Pos {f.Position:F0} | HFR {star.Hfr:F2}",x,y+16);
                var bytes=p.Select(v=>(byte)Math.Clamp(255*Math.Asinh(30*Math.Max(0,v-bg)/stretch)/Math.Asinh(30),0,255)).ToArray();
                var bitmap=BitmapSource.Create(size,size,96,96,PixelFormats.Gray8,null,bytes,size);bitmap.Freeze();dc.DrawImage(bitmap,new Rect(x,y+34,112,112));
            }
        }
        var bitmapTarget=new RenderTargetBitmap(cols*cellWidth,height,96,96,PixelFormats.Pbgra32);bitmapTarget.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmapTarget));using(var stream=File.Create(Path.Combine(output,"every-frame-contact-sheet.png")))encoder.Save(stream);
    }
}
