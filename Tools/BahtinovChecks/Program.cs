using Cwseo.NINA.ManualFocuser.Models;
using System.Numerics;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
await WorkflowChecks.Run(Check);
double[] Lines(double error, double rotation = 0) {
    const int n = 128; var image = new double[n*n];
    for (int y=0;y<n;y++) for(int x=0;x<n;x++) {
        double dx=x-63.5,dy=y-63.5; double v=10;
        for(int k=0;k<3;k++) { double a=(65+k*25+rotation)*Math.PI/180, d=dx*Math.Cos(a)+dy*Math.Sin(a)-(k==1?error:0); v+=200*Math.Exp(-d*d/2); }
        image[y*n+x]=v;
    }
    return image;
}
foreach(double angle in new[]{0.0,37,90,167}) foreach(double error in new[]{-3.0,0,3}) {
    var m=BahtinovAnalyzer.Analyze(Lines(error,angle),128,128);
    Check(m.IsValid,$"Geometric three-line detection rotation={angle}, error={error}: {m.FailureReason}");
    Check(Math.Abs(Math.Abs(m.SignedErrorPixels)-Math.Abs(error))<.65,"Normal geometry magnitude");
}
var plus=BahtinovAnalyzer.Analyze(Lines(3),128,128);var minus=BahtinovAnalyzer.Analyze(Lines(-3),128,128);
Check(plus.SignedErrorPixels*minus.SignedErrorPixels<0,"Opposite defocus has opposite sign");
Check(!BahtinovAnalyzer.Analyze(new double[128*128],128,128).IsValid,"Blank image invalid, never zero focus");
var blob=new double[128*128]; for(int y=0;y<128;y++)for(int x=0;x<128;x++)blob[y*128+x]=1000*Math.Exp(-((x-63.5)*(x-63.5)+(y-63.5)*(y-63.5))/8);
Check(!BahtinovAnalyzer.Analyze(blob,128,128).IsValid,"Unmasked round star rejected");
Check(BahtinovFocusController.TryFindZero(new (int,double)[]{(100,-3),(200,2)},out int zero)&&zero==160,"Bracket interpolation");
Check(!BahtinovFocusController.TryFindZero(new (int,double)[]{(100,3),(200,2)},out _),"Never extrapolate outside measured bracket");
Check(!BahtinovFocusController.TryFindZero(new (int,double)[]{(100,double.NaN),(200,2)},out _),"Invalid measurement cannot bracket");
Check(!BahtinovFocusController.TryFindZero(new (int,double)[]{(100,-double.MaxValue),(200,double.MaxValue)},out _),"Overflowing interpolation rejected");
Check(double.IsNaN(QuickFocusMetrics.HalfFluxRadius(new double[128*128],128,128)),"Blank live HFR invalid");
var random = new Random(123); var noisy = Enumerable.Range(0,128*128).Select(_=>10+random.NextDouble()).ToArray();
Check(double.IsNaN(QuickFocusMetrics.HalfFluxRadius(noisy,128,128)),"Noise-only live HFR invalid");
var gaussian=new double[128*128];for(int y=0;y<128;y++)for(int x=0;x<128;x++)gaussian[y*128+x]=10+1000*Math.Exp(-((x-63.5)*(x-63.5)+(y-63.5)*(y-63.5))/18);
Check(Math.Abs(QuickFocusMetrics.HalfFluxRadius(gaussian,128,128)-3*Math.Sqrt(2*Math.Log(2)))<.5,"Gaussian live HFR expected radius");
var edge=new double[128*128];edge[3*128+3]=1000;
Check(double.IsNaN(QuickFocusMetrics.HalfFluxRadius(edge,128,128)),"Clipped edge star rejected");
var hot=new double[128*128];hot[64*128+64]=1000;
Check(double.IsNaN(QuickFocusMetrics.HalfFluxRadius(hot,128,128)),"Isolated hot pixel rejected");
string folder=args.Length>0?args[0]:Path.Combine(AppContext.BaseDirectory,"samples");Directory.CreateDirectory(folder);
var opticalErrors=new List<double>();
foreach(double defocus in new[]{-2.0,-1,0,1,2}) {
    var image=PupilImage(defocus);var m=BahtinovAnalyzer.Analyze(image,256,256);
    Check(m.IsValid,$"Physical Fourier pupil phase {defocus} detected");opticalErrors.Add(m.SignedErrorPixels);
    string file=Path.Combine(folder,$"fourier-defocus-{defocus:+0;-0;0}.pgm");
    WriteFits(Path.ChangeExtension(file,"fits"),image,defocus);
    var fits = Cwseo.NINA.ManualFocuser.Tools.SpikeBatch.FitsImage.Load(Path.ChangeExtension(file, "fits"));
    var readback = BahtinovAnalyzer.Analyze(fits.Data.Select(v => (double)v).ToArray(), fits.Width, fits.Height);
    Check(readback.IsValid && Math.Abs(readback.SignedErrorPixels - m.SignedErrorPixels) < .2, "FITS serialization preserves physical focus measurement");
    using(var writer=new StreamWriter(file)) {writer.WriteLine("P2\n# SYNTHETIC scalar monochromatic Fourier pupil, not an observed telescope image\n256 256\n65535");double maximum=image.Max();foreach(var value in image)writer.WriteLine((int)Math.Round(65535*value/maximum));}
    Console.WriteLine($"Fourier pupil phase={defocus}: valid={m.IsValid}, error={m.SignedErrorPixels:F3}, reason={m.FailureReason}, lines={string.Join(";",m.Lines.Select(l=>$"{Math.Atan2(l.Ny,l.Nx)*180/Math.PI:F2}:{l.Offset:F2}"))}, file={file}");
}
Check(Math.Abs(opticalErrors[2])<.2,"Physical zero-defocus near zero");
Check(opticalErrors.Zip(opticalErrors.Skip(1)).All(p=>p.First>p.Second),"Physical signed error monotonic across focus");
var scan=opticalErrors.Select((e,i)=>(Position:1000+i*100,Error:e)).ToArray();
Check(BahtinovFocusController.TryFindZero(scan,out int fittedPosition)&&Math.Abs(fittedPosition-1200)<=10,"Physical scan finds known focus position");
double finalPhase=(fittedPosition-1200)/100.0;
Check(Math.Abs(BahtinovAnalyzer.Analyze(PupilImage(finalPhase),256,256).SignedErrorPixels)<.2,"Physical synthetic final measurement verifies focus");
var benchmarkImage=PupilImage(1);var timer=System.Diagnostics.Stopwatch.StartNew();for(int i=0;i<10;i++)BahtinovAnalyzer.Analyze(benchmarkImage,256,256);timer.Stop();Console.WriteLine($"256px analyzer mean over10: {timer.Elapsed.TotalMilliseconds/10:F1}ms");
Console.WriteLine($"{checks} checks passed. Synthetic optical model is not telescope/hardware validation.");

static void WriteFits(string file,double[] image,double phase) {
    var cards=new[]{"SIMPLE  =                    T","BITPIX  =                   16","NAXIS   =                    2","NAXIS1  =                  256","NAXIS2  =                  256","BSCALE  =                    1","BZERO   =                32768",$"DEFOCUS = {phase.ToString(System.Globalization.CultureInfo.InvariantCulture),20}","COMMENT SYNTHETIC scalar Fourier pupil. Not an observed telescope image.","END"};
    using var output=File.Create(file);var header=System.Text.Encoding.ASCII.GetBytes(string.Concat(cards.Select(c=>c.PadRight(80))));output.Write(header);int headerPadding=(2880-header.Length%2880)%2880;output.Write(Enumerable.Repeat((byte)32,headerPadding).ToArray());
    double maximum=image.Max();foreach(double value in image){int scaled=100+(int)Math.Round(50000*value/maximum)-32768;output.WriteByte((byte)((scaled>>8)&255));output.WriteByte((byte)(scaled&255));}
    int dataPadding=(2880-(image.Length*2)%2880)%2880;output.Write(new byte[dataPadding]);
}

// Fraunhofer intensity |FFT(pupil exp(i defocus r^2))|^2. Three binary grating zones
// are an illustrative mask; no instrument sampling, spectral integration or atmosphere is modeled.
static double[] PupilImage(double defocus) {
    const int n=512;var pupil=new Complex[n*n];
    for(int y=0;y<n;y++)for(int x=0;x<n;x++) {
        double dx=x-n/2.0,dy=y-n/2.0,r2=dx*dx+dy*dy;if(r2>90*90)continue;
        double angle=(dy<0?0:dx<0?-25:25)*Math.PI/180;
        if(Math.Cos(2*Math.PI*(dx*Math.Cos(angle)+dy*Math.Sin(angle))/12)>0)
            pupil[y*n+x]=Complex.FromPolarCoordinates(1,defocus*r2/(90*90));
    }
    var line=new Complex[n];for(int y=0;y<n;y++){Array.Copy(pupil,y*n,line,0,n);Fft(line);Array.Copy(line,0,pupil,y*n,n);}
    for(int x=0;x<n;x++){for(int y=0;y<n;y++)line[y]=pupil[y*n+x];Fft(line);for(int y=0;y<n;y++)pupil[y*n+x]=line[y];}
    var output=new double[256*256];for(int y=0;y<256;y++)for(int x=0;x<256;x++){var c=pupil[((y-128+n)%n)*n+(x-128+n)%n];output[y*256+x]=c.Real*c.Real+c.Imaginary*c.Imaginary;}
    return output;
}
static void Fft(Complex[] a) {
    int n=a.Length;for(int i=1,j=0;i<n;i++){int bit=n>>1;for(; (j&bit)!=0;bit>>=1)j^=bit;j^=bit;if(i<j)(a[i],a[j])=(a[j],a[i]);}
    for(int len=2;len<=n;len<<=1){var root=Complex.FromPolarCoordinates(1,-2*Math.PI/len);for(int i=0;i<n;i+=len){Complex w=1;for(int j=0;j<len/2;j++){var u=a[i+j];var v=a[i+j+len/2]*w;a[i+j]=u+v;a[i+j+len/2]=u-v;w*=root;}}}
}
