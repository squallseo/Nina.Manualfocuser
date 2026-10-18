using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

internal static class StretchLevelsChecks {
    public static void Run(Action<bool,string> check) {
        var pixels = Enumerable.Range(0,65536).Select(i=>(ushort)i).ToArray();
        var original=(ushort[])pixels.Clone();
        var frame=new FocusRawFrame(pixels,256,0,0,256,256);
        var standard=FocusDisplayStretch.RenderRaw(frame);
        check(standard.Zip(standard.Skip(1)).All(p=>p.First<=p.Second),"Automatic transfer is monotonic across all 65536 input levels");
        check(standard[^1]==255 && standard[0]<standard[^1],"White endpoint saturates while the dark end stays finite");
        var white=FocusDisplayStretch.RenderRaw(frame,levels:new(-2.8,.5,.2));
        check(white[40000]==255 && standard[40000]<255,"White point moves the clipping endpoint independently of brightness");
        var lowerBlack=FocusDisplayStretch.RenderRaw(frame,levels:new(-8,1,.2));
        var higherBlack=FocusDisplayStretch.RenderRaw(frame,levels:new(0,1,.2));
        check(!lowerBlack.SequenceEqual(higherBlack) && higherBlack[30000]==0,"Black point changes shadow clipping rather than applying a brightness offset");
        check(pixels.SequenceEqual(original),"Every levels operation preserves original 16-bit camera pixels");
        check(FocusDisplayStretch.RenderRaw(frame,autoStretch:false,levels:new(0,.01,.5)).SequenceEqual(FocusDisplayStretch.RenderRaw(frame,autoStretch:false)),"Disabling Auto Stretch bypasses all three levels controls");
        check(FocusDisplayStretch.RenderRaw(frame,levels:new(double.NaN,double.NaN,double.NaN)).SequenceEqual(standard),"Nonfinite levels fall back to the automatic defaults");
        foreach(ushort value in new ushort[]{0,1000,65000,65535}) {
            var flat=new FocusRawFrame(Enumerable.Repeat(value,4096).ToArray(),64,0,0,64,64);
            var result=FocusDisplayStretch.RenderRaw(flat);
            check(result.Distinct().Count()==1,$"Constant frame {value} has a stable finite display, including overexposure");
        }
        var high=Enumerable.Range(0,65536).Select(i=>(ushort)(49000+i%2001)).ToArray();
        var highResult=FocusDisplayStretch.RenderRaw(new FocusRawFrame(high,256,0,0,256,256));
        var highAtMedian=high.Select((v,i)=>(v,i)).Where(p=>p.v==50000).Select(p=>highResult[p.i]).First();
        check(Math.Abs(highAtMedian-51)<=1,"High-background branch retains NINA's target median rather than inverting display brightness");
        var padding=Enumerable.Repeat((ushort)65535,2048*1200).ToArray();
        for(int y=11;y<1035;y++)for(int x=19;x<1043;x++)padding[y*2048+x]=(ushort)(1000+(x*13+y*29)%50);
        var roi=new FocusRawFrame(padding,2048,19,11,1024,1024);
        check(FocusDisplayStretch.RenderRaw(roi).SequenceEqual(FocusDisplayStretch.Render(roi.ToDoubles())),"Sampled MAD/LUT uses only the padded ROI and agrees with the double reference");
        var copy=FocusDisplayStretch.RenderRaw(roi);
        var repeated=Enumerable.Range(0,8).AsParallel().Select(_=>FocusDisplayStretch.RenderRaw(roi)).ToArray();
        check(repeated.All(b=>b.SequenceEqual(copy)),"Parallel rendering shares immutable statistics without buffer races or flicker");
        var largePixels=Enumerable.Range(0,2048*2049).Select(i=>(ushort)((i*37)&65535)).ToArray();
        var large=FocusDisplayStretch.RenderRaw(new FocusRawFrame(largePixels,2048,0,0,2048,2049),autoStretch:false);
        check(Enumerable.Range(0,large.Length).All(i=>large[i]==(byte)Math.Round(largePixels[i]*255d/65535)),"Parallel LUT writes every pixel, including a partial final row block, before publishing uninitialized storage");
        // A faint, unsaturated star is separable from the background after STF;
        // linear 8-bit conversion loses both into the same display level.
        var faint=Enumerable.Range(0,256*256).Select(i=>(ushort)(500+(i*13)%21)).ToArray();
        faint[32896]=550;
        var faintFrame=new FocusRawFrame(faint,256,0,0,256,256);
        var stretched=FocusDisplayStretch.RenderRaw(faintFrame);var linear=FocusDisplayStretch.RenderRaw(faintFrame,autoStretch:false);
        check(stretched[32896]-stretched[0]>30 && linear[32896]-linear[0]<=1,"Automatic levels reveal a faint star hidden by linear 8-bit display quantization");
        var light=FocusDisplayStretch.RenderRaw(frame,levels:FocusStretchLevels.Default with {InputMidtone=.25});
        var dark=FocusDisplayStretch.RenderRaw(frame,levels:FocusStretchLevels.Default with {InputMidtone=.75});
        check(light.Zip(standard).All(p=>p.First>=p.Second) && dark.Zip(standard).All(p=>p.First<=p.Second),"Moving the shared midtone handle left brightens and right darkens, preserving monotonic levels");
        var blackWhite=FocusDisplayStretch.RenderRaw(frame,levels:FocusStretchLevels.Default with {InputBlack=.1,InputWhite=.7});
        check(blackWhite.Zip(blackWhite.Skip(1)).All(p=>p.First<=p.Second) && blackWhite.Count(v=>v==0)>standard.Count(v=>v==0) && blackWhite.Count(v=>v==255)>standard.Count(v=>v==255),"Shared endpoint handles clip the automatic stretch input without changing the source");
        var invalid=FocusStretchLevels.Default with {InputBlack=1,InputMidtone=double.NaN,InputWhite=-1};
        var sanitized=invalid.Sanitize();
        check(sanitized.InputBlack<sanitized.InputWhite && sanitized.InputMidtone==.5 && FocusDisplayStretch.RenderRaw(frame,levels:invalid).Length==65536,"Crossed or invalid manual levels are sanitized to finite nonzero ranges");
        var manual=FocusStretchLevels.Default with {InputBlack=.1,InputMidtone=.25,InputWhite=.8};
        var manualBytes=FocusDisplayStretch.RenderRaw(frame,levels:manual);
        var plot=FocusDisplayStretch.PlotHistogram(FocusDisplayStretch.GetHistogram(frame),levels:manual);
        var counts=new long[256]; foreach(byte value in manualBytes)counts[value]++;
        check(Enumerable.Range(0,256).All(i=>counts[i]==plot.DisplayAt(i)),"Shared-level histogram agrees with every output pixel, including clipped endpoints");
        check(FocusDisplayStretch.RenderRaw(frame,autoStretch:false,levels:manual).SequenceEqual(FocusDisplayStretch.RenderRaw(frame,autoStretch:false)),"Linear preview bypasses the shared level handles as well as automatic STF");
    }
}
