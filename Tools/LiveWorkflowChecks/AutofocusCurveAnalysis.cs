using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NINA.WPF.Base.Utility.AutoFocus;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

// Offline review only. Never changes the user's autofocus configuration.
internal static partial class AutofocusCurveAnalysis {
    private sealed record Star(double X,double Y,double Hfr);
    private sealed record Frame(string File,double Position,double JsonPosition,double Hfr,double Spread,List<Star> Stars);
    private sealed record Point(double X,double Y,double Error);
    private sealed record Fit(Func<double,double> Predict,double Position,double Minimum);
    public static void Run(string folder) {
        Exception failure=null;
        var thread=new Thread(()=>{try{
            var runs=Directory.Exists(Path.Combine(folder,"attempt01")) ? new[]{folder} : Directory.GetDirectories(folder).OrderBy(x=>x)
                .Where(x=>File.Exists(Path.Combine(x,"attempt01","autofocus_report_Region0.json"))).ToArray();
            if(runs.Length==0)throw new Exception("No autofocus reports found");
            foreach(var run in runs)Analyze(run);
        }catch(Exception e){failure=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)throw new Exception("Autofocus curve analysis failed",failure);
    }
    private static Fit Calculate(Point[] points,bool hyperbolic,bool weighted) {
        var p=points.Select(p=>new ScatterErrorPoint(p.X,p.Y,0,weighted?Math.Max(.001,p.Error):1)).ToArray();
        if(hyperbolic){var f=new HyperbolicFitting().Calculate(p);return new(f.Fitting,f.Minimum.X,f.Minimum.Y);}
        else {var f=new QuadraticFitting().Calculate(p);return new(f.Fitting,f.Minimum.X,f.Minimum.Y);}
    }
    private static void Analyze(string folder) {
        string output=Path.GetFullPath(Path.Combine("bin","autofocus-analysis",Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))));Directory.CreateDirectory(output);
        var report=JObject.Parse(File.ReadAllText(Path.Combine(folder,"attempt01","autofocus_report_Region0.json")));
        var points=report["MeasurePoints"].Select(p=>new Point((double)p["Position"],(double)p["Value"],(double)p["Error"])).OrderBy(p=>p.X).ToArray();
        Console.WriteLine($"Run {Path.GetFileName(folder)}: {report["Filter"]} filter; {report["Temperature"]} C; {points.Length} positions");
        var frames=Directory.GetFiles(Path.Combine(folder,"attempt01"),"*star_detection_result.json").OrderBy(x=>x).Select(ReadFrame).ToArray();
        var final=Directory.GetFiles(Path.Combine(folder,"final"),"*star_detection_result.json").OrderBy(x=>x).Select(ReadFrame).ToArray();
        foreach(var p in points) {
            var group=frames.Where(f=>f.Position==p.X).ToArray();
            if(group.Length!=(int)report["FocuserOptions"]["AutoFocusNumberOfFramesPerPoint"] || Math.Abs(group.Average(f=>f.Hfr)-p.Y)>1e-10)
                throw new Exception("Frame groups must reproduce the saved autofocus report before fitting");
        }
        var rows=new List<string>{"dataset,weights,model,points,focus_position,minimum_hfr,rmse_px,r_squared,loo_prediction_rmse_px,loo_focus_sd_steps,loo_focus_min,loo_focus_max"};
        var summary=new List<object>();
        void Compare(string name,Point[] p) {
            foreach(bool weighted in new[]{true,false})foreach(bool hyper in new[]{false,true}) {
                var fit=Calculate(p,hyper,weighted);
                double sse=p.Sum(x=>Math.Pow(fit.Predict(x.X)-x.Y,2));
                double total=p.Sum(x=>Math.Pow(x.Y-p.Average(y=>y.Y),2));
                var omitted=new List<(double PredictionError,double Position)>();
                for(int i=0;i<p.Length;i++) {
                    var f=Calculate(p.Where((_,j)=>j!=i).ToArray(),hyper,weighted);
                    omitted.Add((f.Predict(p[i].X)-p[i].Y,f.Position));
                }
                double loo=Math.Sqrt(omitted.Average(x=>x.PredictionError*x.PredictionError));
                double sd=Math.Sqrt(omitted.Sum(x=>Math.Pow(x.Position-omitted.Average(y=>y.Position),2))/(omitted.Count-1));
                var item=new{Dataset=name,Weights=weighted?"saved_HFR_spread":"uniform",Model=hyper?"native_3_parameter_hyperbolic":"parabolic",Points=p.Length,
                    FocusPosition=fit.Position,MinimumHFR=fit.Minimum,RMSE=Math.Sqrt(sse/p.Length),RSquared=1-sse/total,
                    LeaveOnePositionOutPredictionRMSE=loo,LeaveOnePositionOutFocusSD=sd,LeaveOnePositionOutFocusRange=new[]{omitted.Min(x=>x.Position),omitted.Max(x=>x.Position)}};
                summary.Add(item);
                string row=FormattableString.Invariant($"{name},{item.Weights},{item.Model},{p.Length},{fit.Position:F0},{fit.Minimum:F5},{item.RMSE:F5},{item.RSquared:F6},{loo:F5},{sd:F1},{item.LeaveOnePositionOutFocusRange[0]:F0},{item.LeaveOnePositionOutFocusRange[1]:F0}");
                rows.Add(row);Console.WriteLine(row);
            }
            Plot(p,Path.Combine(output,name+".png"),name);
        }
        Compare($"saved_all_stars_{points.Length}_positions",points);
        var storedFit=new QuadraticFitting().Calculate(points.Skip(1).Select(p=>new ScatterErrorPoint(p.X,p.Y,0,p.Error)).ToArray());
        Console.WriteLine($"Native parabolic excluding the final outermost point: {storedFit.Minimum.X:F0}; {storedFit.Expression}; saved: {report["Fittings"]["Quadratic"]}");
        double recordedPosition=(double)report["CalculatedFocusPoint"]["Position"];
        int centerIndex=Array.FindIndex(points,p=>p.X==points.MinBy(p=>Math.Abs(p.X-recordedPosition)).X);
        Compare("saved_near_focus_5_positions",points.Skip(Math.Clamp(centerIndex-2,0,points.Length-5)).Take(5).ToArray());
        var reference=frames.OrderBy(f=>Math.Abs(f.Position-points.MinBy(p=>p.Y).X)).ThenBy(f=>f.File).First();
        // Match the same sky objects in every exposure. Requiring unique nearest
        // neighbours prevents two reference objects sharing one detection.
        foreach(double radius in new[]{8.0,16.0}) {
            var matches=frames.Select(f=>Match(reference.Stars,f.Stars,radius)).ToArray();
            var common=Enumerable.Range(0,reference.Stars.Count).Where(i=>matches.All(m=>m.ContainsKey(i))).ToArray();
            Console.WriteLine($"Common stars across all {frames.Length} exposures, radius {radius}px: {common.Length}");
            if(common.Length<3)continue;
            if(radius==8)CreateImageReview(frames,final,reference,matches,common,output);
            var commonPoints=frames.Select((f,j)=>new{f.Position,Values=common.Select(i=>matches[j][i].Hfr).ToArray()})
                .GroupBy(f=>f.Position).OrderBy(g=>g.Key).Select(g=>new Point(g.Key,g.Average(f=>f.Values.Average()),
                    g.Average(f=>Math.Sqrt(f.Values.Sum(v=>Math.Pow(v-f.Values.Average(),2))/(f.Values.Length-1))))).ToArray();
            Compare($"same_{common.Length}_stars_radius_{radius:F0}px",commonPoints);
            File.WriteAllLines(Path.Combine(output,$"common-stars-{radius:F0}px.csv"),new[]{"position,hfr,descriptive_star_spread"}.Concat(commonPoints.Select(p=>FormattableString.Invariant($"{p.X},{p.Y:R},{p.Error:R}"))));
        }
        File.WriteAllLines(Path.Combine(output,"fit-comparison.csv"),rows);
        File.WriteAllLines(Path.Combine(output,"frame-measurements.csv"),new[]{"file,filename_position,json_position,hfr,hfr_spread,stars"}.Concat(frames.Concat(final).Select(f=>FormattableString.Invariant($"{Path.GetFileName(f.File)},{f.Position},{f.JsonPosition},{f.Hfr:R},{f.Spread:R},{f.Stars.Count}"))));
        File.WriteAllText(Path.Combine(output,"analysis.json"),JsonConvert.SerializeObject(new{
            SourceFolder=folder,Timestamp=report["Timestamp"],Filter=report["Filter"],Temperature=report["Temperature"],StarDetector=report["StarDetectorName"],AutoFocuser=report["AutoFocuserName"],RecordedFitting=report["Fitting"],
            RecordedFocus=report["CalculatedFocusPoint"],RecordedFinalHFR=report["FinalHFR"],Fits=summary,
            Limitations=new[]{$"One autofocus run, {report["Filter"]} filter, {report["FocuserOptions"]["AutoFocusStepSize"]}-step spacing, {report["FocuserOptions"]["AutoFocusNumberOfFramesPerPoint"]} frames per position.",
                "Hyperbolic comparison uses installed NINA's three-parameter fitting class, not Hocus Focus's advanced model selection.",
                "Saved Error values are per-star HFR spread; weights and leave-one-position-out dispersion are not confidence intervals.",
                "Same-star matching estimates per-frame translation then uses 8/16px radii, without affine or astrometric registration.",
                $"{frames.Concat(final).Count(f=>f.Position!=f.JsonPosition)} detection JSON positions disagree with the corresponding FITS filenames. Offline frame grouping uses filename position, verified against saved report means.",
                "No new exposure was captured at the predicted hyperbolic minimum; actual image quality there is unverified."}},Formatting.Indented));
        Console.WriteLine("Analysis output: "+output);
    }
    private static Frame ReadFrame(string path) {
        var j=JObject.Parse(File.ReadAllText(path));
        string prefix=Path.GetFileName(path).Split("_Region")[0];
        string fits=Directory.GetFiles(Path.GetDirectoryName(path),prefix+"_*.fits").Single();
        double position=double.Parse(Path.GetFileNameWithoutExtension(fits).Split("_Focuser")[1],System.Globalization.CultureInfo.InvariantCulture);
        return new(path,position,(double)j["FocuserPosition"],(double)j["AverageHFR"],(double)j["HFRStdDev"],j["StarList"].Select(s=>new Star((double)s["Position"]["X"],(double)s["Position"]["Y"],(double)s["HFR"])).ToList());
    }
    private static Dictionary<int,Star> Match(List<Star> reference,List<Star> stars,double radius) {
        var votes=new Dictionary<(int X,int Y),int>();
        foreach(var r in reference)foreach(var s in stars) {
            double dx=s.X-r.X,dy=s.Y-r.Y;
            if(Math.Abs(dx)>150 || Math.Abs(dy)>150)continue;
            var bin=((int)Math.Round(dx/4),(int)Math.Round(dy/4));votes.TryGetValue(bin,out int count);votes[bin]=count+1;
        }
        var offset=votes.OrderByDescending(p=>p.Value).First().Key;
        double offsetX=offset.X*4,offsetY=offset.Y*4;
        var candidates=new List<(int Reference,int Target,double Distance)>();
        for(int i=0;i<reference.Count;i++)for(int j=0;j<stars.Count;j++) {
            double d=Math.Pow(reference[i].X+offsetX-stars[j].X,2)+Math.Pow(reference[i].Y+offsetY-stars[j].Y,2);
            if(d<=radius*radius)candidates.Add((i,j,d));
        }
        var result=new Dictionary<int,Star>();var used=new HashSet<int>();
        foreach(var p in candidates.OrderBy(p=>p.Distance))if(!result.ContainsKey(p.Reference) && used.Add(p.Target))result[p.Reference]=stars[p.Target];
        return result;
    }
    private static void Plot(Point[] p,string path,string title) {
        var model=new PlotModel{Title=title,Subtitle="Saved Hocus Focus measurements; native NINA fits; weights = 1 / saved spread²",Background=OxyColors.White};
        model.Legends.Add(new OxyPlot.Legends.Legend{LegendPosition=OxyPlot.Legends.LegendPosition.LeftTop});
        model.Axes.Add(new LinearAxis{Position=AxisPosition.Bottom,Title="Focuser position (steps)"});
        model.Axes.Add(new LinearAxis{Position=AxisPosition.Left,Title="Multi-star HFR (pixels)",Minimum=0});
        var measured=new ScatterErrorSeries{Title="Measured",MarkerType=MarkerType.Circle,MarkerSize=4,MarkerFill=OxyColors.Black};
        foreach(var x in p)measured.Points.Add(new ScatterErrorPoint(x.X,x.Y,0,x.Error));model.Series.Add(measured);
        foreach(bool hyper in new[]{false,true}) {
            var fit=Calculate(p,hyper,true);
            var line=new LineSeries{Title=$"{(hyper?"Hyperbolic":"Parabolic")} · minimum {fit.Position:F0}",Color=hyper?OxyColors.Teal:OxyColors.OrangeRed};
            for(double x=p.Min(t=>t.X);x<=p.Max(t=>t.X);x+=25)line.Points.Add(new DataPoint(x,fit.Predict(x)));
            model.Series.Add(line);
        }
        using var stream=File.Create(path);
        new OxyPlot.Wpf.PngExporter{Width=1400,Height=850}.Export(model,stream);
    }
    private static void CreateImageReview(Frame[] frames,Frame[] final,Frame reference,Dictionary<int,Star>[] matches,int[] common,string output) {
        const int size=96;
        var chosen=common.OrderBy(i=>reference.Stars[i].X).ToArray();
        var indices=new[]{chosen[0],chosen[chosen.Length/2],chosen[^1]};
        var positions=frames.Select(f=>f.Position).Distinct().OrderBy(p=>p).ToArray();
        int referenceIndex=Array.IndexOf(positions,reference.Position);
        var columns=new[]{positions[0],positions[Math.Max(0,referenceIndex-1)],reference.Position,
            positions[Math.Min(positions.Length-1,referenceIndex+1)],positions[Math.Min(positions.Length-1,referenceIndex+3)],positions[^1]}.Distinct().OrderBy(p=>p).ToArray();
        var selected=columns.Select(p=>Array.FindIndex(frames,f=>f.Position==p)).ToArray();
        var finalMatches=Match(reference.Stars,final[0].Stars,8);
        var visual=new System.Windows.Media.DrawingVisual();
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(visual,System.Windows.Media.BitmapScalingMode.NearestNeighbor);
        var rows=new List<string>{"reference_x,reference_y,position,saved_star_hfr,aperture_hfr_radius32,max_ADU,saturated_pixels_65000,background_ADU,source_file"};
        using(var dc=visual.RenderOpen()) {
            dc.DrawRectangle(System.Windows.Media.Brushes.White,null,new System.Windows.Rect(0,0,1260,830));
            void Label(string text,double x,double y,int font=15) => dc.DrawText(new System.Windows.Media.FormattedText(text,System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,new System.Windows.Media.Typeface("Arial"),font,System.Windows.Media.Brushes.Black,1),new System.Windows.Point(x,y));
            Label("Same stars in actual FITS exposures",18,12,23);
            Label("96 × 96 pixels per crop; same display stretch within each row; last column = actual final validation",18,43);
            for(int row=0;row<indices.Length;row++) {
                int index=indices[row];var refStar=reference.Stars[index];
                var patches=new List<(double Position,Star Star,string File,double[] Pixels)>();
                foreach(int j in selected) {
                    var star=matches[j][index];string file=FindFits(frames[j].File);
                    patches.Add((frames[j].Position,star,file,ReadTile(file,(int)Math.Round(star.X)-size/2,(int)Math.Round(star.Y)-size/2,size)));
                }
                if(finalMatches.TryGetValue(index,out var fs)) {
                    string file=FindFits(final[0].File);patches.Add((final[0].Position,fs,file,ReadTile(file,(int)Math.Round(fs.X)-size/2,(int)Math.Round(fs.Y)-size/2,size)));
                }
                Label($"Star at ({refStar.X:F0}, {refStar.Y:F0})",18,88+row*240,17);
                double scale=patches.Max(p=>p.Pixels.Max()-Median(p.Pixels));
                for(int col=0;col<patches.Count;col++) {
                    var p=patches[col];double bg=Median(p.Pixels);
                    var pixels=p.Pixels.Select(v=>(byte)Math.Clamp(255*Math.Asinh(30*Math.Max(0,v-bg)/scale)/Math.Asinh(30),0,255)).ToArray();
                    var bmp=System.Windows.Media.Imaging.BitmapSource.Create(size,size,96,96,System.Windows.Media.PixelFormats.Gray8,null,pixels,size);bmp.Freeze();
                    int x=18+col*177,y=117+row*240;
                    Label($"{p.Position:F0}{(col==6?" (final)":"")}",x,y);
                    dc.DrawImage(bmp,new System.Windows.Rect(x,y+22,144,144));
                    Label($"Hocus HFR {p.Star.Hfr:F2}px",x,y+171,14);
                    var flux=new List<(double Radius,double Flux)>();
                    for(int yy=0;yy<size;yy++)for(int xx=0;xx<size;xx++) {
                        double r=Math.Sqrt(Math.Pow(xx-size/2.0,2)+Math.Pow(yy-size/2.0,2));
                        if(r<=32)flux.Add((r,Math.Max(0,p.Pixels[yy*size+xx]-bg)));
                    }
                    double half=flux.Sum(f=>f.Flux)/2,sum=0,hfr=double.NaN;
                    foreach(var f in flux.OrderBy(f=>f.Radius)){sum+=f.Flux;if(sum>=half){hfr=f.Radius;break;}}
                    rows.Add(FormattableString.Invariant($"{refStar.X:R},{refStar.Y:R},{p.Position},{p.Star.Hfr:R},{hfr:R},{p.Pixels.Max()},{p.Pixels.Count(v=>v>=65000)},{bg:R},{Path.GetFileName(p.File)}"));
                }
            }
        }
        var target=new System.Windows.Media.Imaging.RenderTargetBitmap(1260,830,96,96,System.Windows.Media.PixelFormats.Pbgra32);target.Render(visual);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
        using(var stream=File.Create(Path.Combine(output,"same-star-fits-review.png")))encoder.Save(stream);
        File.WriteAllLines(Path.Combine(output,"raw-star-crops.csv"),rows);
    }
    private static double Median(double[] pixels) {var sorted=(double[])pixels.Clone();Array.Sort(sorted);return sorted[sorted.Length/2];}
    private static string FindFits(string json) => Directory.GetFiles(Path.GetDirectoryName(json),Path.GetFileName(json).Split("_Region")[0]+"_*.fits").Single();
    private static double[] ReadTile(string file,int x,int y,int size) {
        using var stream=File.OpenRead(file);var bytes=new byte[80];var header=new Dictionary<string,string>();
        while(true) {
            stream.ReadExactly(bytes);string card=System.Text.Encoding.ASCII.GetString(bytes),key=card[..8].Trim();
            if(key=="END")break;
            if(card[8]=='=')header[key]=card[10..].Split('/')[0].Trim();
        }
        long offset=((stream.Position+2879)/2880)*2880;
        int width=int.Parse(header["NAXIS1"]),height=int.Parse(header["NAXIS2"]);
        if(header["BITPIX"]!="16" || x<0 || y<0 || x+size>width || y+size>height)throw new Exception("Unsupported FITS crop");
        double zero=header.TryGetValue("BZERO",out var z)?double.Parse(z,System.Globalization.CultureInfo.InvariantCulture):0;
        double scale=header.TryGetValue("BSCALE",out var s)?double.Parse(s,System.Globalization.CultureInfo.InvariantCulture):1;
        var pixels=new double[size*size];var row=new byte[size*2];
        for(int i=0;i<size;i++) {
            stream.Position=offset+2L*((y+i)*width+x);stream.ReadExactly(row);
            for(int j=0;j<size;j++)pixels[i*size+j]=System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(row.AsSpan(j*2,2))*scale+zero;
        }
        return pixels;
    }
}
