using Cwseo.NINA.ManualFocuser.Models;
using Cwseo.NINA.ManualFocuser.Tools.SpikeBatch;
using System.Reflection;
internal static class GateProbe {
    private static double Median(List<double> values) {values.Sort();return values.Count%2==0?(values[values.Count/2-1]+values[values.Count/2])/2:values[values.Count/2];}
    public static void Run(string path) {
        var image=FitsImage.Load(path);
        var local=FocusRoi.CenterWindow(image.Data.Select(v=>(double)v).ToArray(),image.Width,image.Height);
        double hfr=QuickFocusMetrics.HalfFluxRadius(local.Pixels,local.Width,local.Height);
        int peak=Array.IndexOf(local.Pixels,local.Pixels.Max()),baseSize=Math.Clamp((int)Math.Ceiling(hfr*4),6,60);
        var parameters=new SpikeAnalysisParams{metricKind=SpikeMetricKind.Fwhm,minUsedStarsForValidFrame=1,autoSpikeAngle=true,minimumRoiSizePx=128,resolveParallelSpikes=true};
        var seeds=new[]{new SpikeSeedStar{X=peak%local.Width+(image.Width-local.Width)/2,Y=peak/local.Width+(image.Height-local.Height)/2,WidthPx=baseSize,HeightPx=baseSize,MaxBrightness=local.Pixels[peak]}};
        var result=new SpikeAutofocusAnalysis(parameters).Evaluate(image.Data,image.Width,image.Height,SpikeCore.CreateTrackingState(seeds,parameters));
        var point=result.StarPoints[0];
        var args=new object[]{image.Data,image.Width,image.Height,point.X,point.Y,baseSize,parameters,null,0,0,0};
        typeof(SpikeCore).GetMethod("TryExtractROIAt",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);
        var roi=(float[])args[7];int size=(int)args[8];
        typeof(SpikeCore).GetMethod("RemoveBackground",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{roi,size,parameters});
        double offX=point.X-(int)args[9],offY=point.Y-(int)args[10];
        Console.WriteLine($"PROBE {Path.GetFileName(path)} seed={baseSize} center={point.X:F3},{point.Y:F3} size={size} angle={result.UsedAngleDeg:F3} clear={result.HasClearSpikes}");
        foreach(double inner in new[]{(double)baseSize,Math.Max(16,baseSize*.7)}) foreach(int bg in new[]{24,32,40,48}) {
            (int offset,int bands,double margin,string report) best=(-100,0,double.NegativeInfinity,"");
            double a=result.UsedAngleDeg*Math.PI/180,cs=Math.Cos(a),sn=Math.Sin(a),outer=size*.43;
            for(int offset=-32;offset<=32;offset++) {
                int passed=0;double minMargin=double.PositiveInfinity;string text="";
                for(int side=-1;side<=1;side+=2)for(int band=0;band<3;band++) {
                    var on=new List<double>();var background=new List<double>();var seenOn=new HashSet<int>();var seenBg=new HashSet<int>();bool fits=true;
                    for(double r=inner+(outer-inner)*band/3;r<inner+(outer-inner)*(band+1)/3;r++)for(int u=-2;u<=2;u++)foreach(int strip in new[]{0,-bg,bg}) {
                        double t=offset+u+strip;
                        int x=(int)Math.Round(size/2.0+offX+side*r*cs-t*sn),y=(int)Math.Round(size/2.0+offY+side*r*sn+t*cs);
                        if(x<0 || y<0 || x>=size || y>=size){fits=false;continue;}
                        int index=y*size+x;
                        if(strip==0){if(seenOn.Add(index))on.Add(roi[index]);}
                        else if(seenBg.Add(index))background.Add(roi[index]);
                    }
                    if(!fits || on.Count==0 || background.Count==0){minMargin=double.NegativeInfinity;continue;}
                    double signal=Median(on),baseline=Median(background),noise=1.4826*Median(background.Select(v=>Math.Abs(v-baseline)).ToList());
                    double sigma=Math.Max(1,noise)*1.253*Math.Sqrt(1.0/on.Count+1.0/background.Count);
                    double ratio=signal/Math.Max(1,baseline),significance=(signal-baseline)/sigma,margin=Math.Min(ratio/2,significance/5);
                    minMargin=Math.Min(minMargin,margin);if(margin>=1 && signal>0)passed++;
                    text+=$" [{side},{band} on={signal:F1} bg={baseline:F1} ratio={ratio:F2} sig={significance:F1}]";
                }
                if(passed>best.bands || passed==best.bands && minMargin>best.margin)best=(offset,passed,minMargin,text);
            }
            Console.WriteLine($"GATE inner={inner} bg={bg} offset={best.offset} bands={best.bands}/6 margin={best.margin:F3} {best.report}");
        }
    }
}
