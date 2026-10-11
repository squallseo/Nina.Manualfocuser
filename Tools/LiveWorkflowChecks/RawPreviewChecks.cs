using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

internal static partial class PreviewDisplayChecks
{
    private static async Task VerifyRawPreview(ManualFocuserDockableVM vm, Action<bool,string> check)
    {
        // A cropped host frame has padding on both axes; guard against accidentally
        // including another sensor region in stretch estimation or HFR measurements.
        const int stride=333, height=307;
        var pixels=Enumerable.Range(0,stride*height).Select(i=>(ushort)((i*37)%65536)).ToArray();
        var original=(ushort[])pixels.Clone();
        var frame=new FocusRawFrame(pixels,stride,17,23,277,269);
        var doubles=frame.ToDoubles();
        check(doubles.Length==277*269 && doubles[0]==pixels[23*stride+17] && doubles[^1]==pixels[291*stride+293],
            "Raw ROI respects nonzero origin, padded rows and rectangular dimensions");
        foreach(double strength in new[]{.25,1,2.5,double.NaN})
            check(FocusDisplayStretch.RenderRaw(frame,strength).SequenceEqual(FocusDisplayStretch.Render(doubles,strength)),
                "Raw 16-bit LUT stretch exactly matches original display at strength "+strength);
        var fullRange=new FocusRawFrame(Enumerable.Range(0,65536).Select(i=>(ushort)i).ToArray(),256,0,0,256,256);
        check(FocusDisplayStretch.RenderRaw(fullRange).SequenceEqual(FocusDisplayStretch.Render(fullRange.ToDoubles())),
            "LUT mapping preserves every 16-bit value including black and saturation");
        var expected=FocusRoi.CenterWindow(doubles,277,269);var center=frame.CenterWindow();
        check(center.Width==expected.Width && center.Height==expected.Height && center.Pixels.SequenceEqual(expected.Pixels),
            "Raw central HFR window retains identical original sensor pixels and pixel scale");
        var small=new FocusRawFrame(pixels,stride,1,1,32,64).CenterWindow();
        check(small.Width==32 && small.Height==64 && small.Pixels.Length==32*64,
            "Raw HFR window clips to small rectangular ROIs");
        foreach(var dimensions in new[]{(-1,0,32,32),(0,0,334,32),(0,300,32,32)}){
            bool rejected=false;try{_=new FocusRawFrame(pixels,stride,dimensions.Item1,dimensions.Item2,dimensions.Item3,dimensions.Item4);}catch(ArgumentException){rejected=true;}
            check(rejected,"Invalid raw ROI cannot read beyond the host frame: "+dimensions);
        }
        var type=typeof(ManualFocuserDockableVM);
        var render=type.GetMethod("RenderRawFocusPreview",BindingFlags.NonPublic|BindingFlags.Instance);
        var bitmap=(BitmapSource)render.Invoke(vm,new object[]{frame,null,null});
        var bytes=new byte[frame.Width*frame.Height];bitmap.CopyPixels(bytes,frame.Width,0);
        check(bitmap.IsFrozen && bitmap.PixelWidth==frame.Width && bitmap.PixelHeight==frame.Height && bytes.SequenceEqual(FocusDisplayStretch.Render(doubles)),
            "Raw live preview keeps full ROI resolution and shares the frozen Gray8 bitmap");
        type.GetProperty(nameof(vm.FocusPreviewImage)).SetValue(vm,bitmap);
        vm.PreviewStretchStrength=.5;await Task.Delay(250);
        var refreshed=(BitmapSource)vm.FocusPreviewImage;refreshed.CopyPixels(bytes,frame.Width,0);
        check(!ReferenceEquals(bitmap,refreshed) && bytes.SequenceEqual(FocusDisplayStretch.Render(doubles,.5)) && pixels.SequenceEqual(original),
            "Stretch slider rerenders retained ushort frame without modifying camera pixels");
        vm.ResetPreviewStretchCommand.Execute(null);await Task.Delay(250);
    }
}
