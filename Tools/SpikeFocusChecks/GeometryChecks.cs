using Cwseo.NINA.ManualFocuser.Models;

internal static class GeometryChecks {
    public static void Run(Action<bool,string> check) {
        const int n=256;
        var parameters=new SpikeAnalysisParams {metricKind=SpikeMetricKind.Fwhm,minUsedStarsForValidFrame=1,
            autoSpikeAngle=true,minimumRoiSizePx=128,resolveParallelSpikes=true};
        double Gaussian(double value,double sigma)=>Math.Exp(-value*value/(2*sigma*sigma));
        SpikeFrameResult Evaluate(double angle,double separation,double sigma,bool circular=false,bool oneSided=false,bool elliptical=false,int seedSize=12) {
            var random=new Random(9123); double a=angle*Math.PI/180,cs=Math.Cos(a),sn=Math.Sin(a);
            var pixels=Enumerable.Range(0,n*n).Select(i=> {
                double x=i%n-128,y=i/n-128,s=x*cs+y*sn,u=-x*sn+y*cs;
                double v=300+random.NextDouble()*20-10;
                v+=45000*(elliptical?Gaussian(s,10)*Gaussian(u,5):Gaussian(Math.Sqrt(x*x+y*y),sigma));
                if(!circular && !elliptical && (!oneSided || s>0))
                    v+=1800*(Gaussian(u-separation/2,1.5)+Gaussian(u+separation/2,1.5))*Gaussian(s,70);
                return (ushort)Math.Clamp(Math.Round(v),0,65535);
            }).ToArray();
            var original=(ushort[])pixels.Clone();
            var seeds=new[]{new SpikeSeedStar {X=128,Y=128,WidthPx=seedSize,HeightPx=seedSize,MaxBrightness=pixels[128*n+128]}};
            var result=SpikeCore.Evaluate(pixels,n,n,parameters,SpikeCore.CreateTrackingState(seeds,parameters));
            check(pixels.SequenceEqual(original),"Extended geometry analysis preserves all raw ADU samples");
            return result;
        }
        foreach(double angle in new[]{0.0,37,86})foreach(double separation in new[]{0.0,12,24,30}) {
            var result=Evaluate(angle,separation,2,seedSize:separation<=12?12:(int)(separation*2));
            check(result.IsValid && result.HasClearSpikes,$"Narrow/split outer diffraction lines detected at {angle} degrees, separation {separation}");
            double distance=Math.Abs(result.UsedAngleDeg-angle)%180;distance=Math.Min(distance,180-distance);
            check(distance<4,"Outer geometry finds the diffraction axis rather than a bright-core ray");
        }
        var unsplit=Evaluate(37,0,2); var split=Evaluate(37,12,2);
        check(split.Metric>unsplit.Metric+5,"Raw diffraction width distinguishes a split pattern from narrow focus");
        foreach(double sigma in new[]{2.0,7,12}) {
            var result=Evaluate(37,0,sigma,circular:true,seedSize:(int)Math.Ceiling(sigma*4));
            check(!result.HasClearSpikes,$"Circular star sigma {sigma} is not accepted as diffraction evidence");
        }
        check(!Evaluate(37,0,2,oneSided:true).HasClearSpikes,"One-sided bright artifact cannot establish a bilateral diffraction line");
        check(!Evaluate(37,0,2,elliptical:true,seedSize:28).HasClearSpikes,"An elliptical Gaussian core cannot replace an extended diffraction pattern");
        var noiseRandom=new Random(83);
        var noise=Enumerable.Range(0,n*n).Select(_=>(ushort)(300+noiseRandom.Next(50))).ToArray();
        var seed=new[]{new SpikeSeedStar {X=128,Y=128,WidthPx=12,HeightPx=12,MaxBrightness=350}};
        check(!SpikeCore.Evaluate(noise,n,n,parameters,SpikeCore.CreateTrackingState(seed,parameters)).HasClearSpikes,
            "Offset searches do not turn noise into a valid autofocus spike");
    }
}
