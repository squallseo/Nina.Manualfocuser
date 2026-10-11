using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cwseo.NINA.ManualFocuser.Dockables;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

internal static partial class PreviewDisplayChecks {
    private static async Task VerifyMainImageOutput(ManualFocuserDockableVM vm, double[] raw,
        Func<(BitmapSource Image,int Count,int Thread)> output,Action<bool,string> check) {
        var type=typeof(ManualFocuserDockableVM);
        var render=type.GetMethod("RenderFocusPreview",BindingFlags.NonPublic|BindingFlags.Instance);
        var source=type.GetProperty(nameof(vm.FocusPreviewImage));
        void Running(bool value) {
            type.GetField("assistRunning",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(vm,value);
            type.GetField("liveImageOutputActive",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(vm,value);
        }
        void Stopping(bool value)=>type.GetField("isStoppingFocusPreview",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(vm,value);
        void Selecting(bool value)=>type.GetProperty(nameof(vm.IsSelectingRoi)).SetValue(vm,value);
        ImageSource Render(BahtinovMeasurement mask=null)=>(ImageSource)render.Invoke(vm,new object[]{raw,256,256,mask,null});
        var preview=Render();
        int baseline=output().Count;
        check(!vm.ShowInNinaImage,"Local preview mode keeps live output disabled");
        vm.ShowInNinaImage=true;
        check(output().Count==baseline,"Enabling output while idle does not overwrite a normal capture");
        type.GetField("assistRunning",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(vm,true);
        source.SetValue(vm,preview);
        check(output().Count==baseline,"ROI capture and selection operations cannot publish as a live stream");
        Running(true);source.SetValue(vm,preview);
        var first=output();
        var bytes=new byte[256*256];first.Image.CopyPixels(bytes,256,0);
        check(first.Count==baseline+1 && ReferenceEquals(first.Image,vm.FocusPreviewImage) && first.Image.IsFrozen && first.Image.PixelWidth==256 && first.Image.PixelHeight==256 && bytes.SequenceEqual(FocusDisplayStretch.Render(raw)),
            "NINA Image reuses the exact frozen ROI bitmap without duplicate pixel buffers or raw-image preparation");
        int before=output().Count;
        for(int i=0;i<50;i++)source.SetValue(vm,preview);
        check(output().Count==before,"Fast camera frames do not flood the main viewer above its update limit");
        vm.ShowInNinaImage=false;source.SetValue(vm,preview);
        check(output().Count==before,"Disabling output leaves the existing main image and stops writes");
        vm.ShowInNinaImage=true;before=output().Count;
        // Queue without yielding the UI, then disable before the callback can run.
        Task.Run(()=>source.SetValue(vm,preview)).GetAwaiter().GetResult();
        vm.ShowInNinaImage=false;await Dispatcher.Yield(DispatcherPriority.Background);
        check(output().Count==before,"Disabling output cancels a queued background publication");
        vm.ShowInNinaImage=true;before=output().Count;
        Task.Run(()=>source.SetValue(vm,preview)).GetAwaiter().GetResult();
        Stopping(true);await Dispatcher.Yield(DispatcherPriority.Background);
        check(output().Count==before,"Stop prevents a queued frame from replacing the shared main image");
        Stopping(false);Selecting(true);
        before=output().Count;
        vm.ShowInNinaImage=false;vm.ShowInNinaImage=true;source.SetValue(vm,preview);
        check(output().Count==before+1 && ReferenceEquals(output().Image,vm.LiveDisplayImage),"Explicit ROI editing publishes the overview once and blocks live stream frames");
        Selecting(false);vm.ShowInNinaImage=false;vm.ShowInNinaImage=true;
        before=output().Count;Running(false);
        vm.PreviewStretchStrength=.5;await Task.Delay(250);
        check(output().Count==before,"Idle stretch changes do not overwrite later normal captures");
        Running(true);vm.ShowInNinaImage=false;vm.ShowInNinaImage=true;
        bytes=new byte[256*256];output().Image.CopyPixels(bytes,256,0);
        check(bytes.SequenceEqual(FocusDisplayStretch.Render(raw,.5)),"Main Image follows Live Focus's current stretch strength");
        var mask=new BahtinovMeasurement {IsValid=true,Lines=new[]{new BahtinovLine(1,0,0),new BahtinovLine(0,1,0),new BahtinovLine(.707,.707,0)}};
        vm.ShowInNinaImage=false;source.SetValue(vm,Render(mask));vm.ShowInNinaImage=true;
        var colored=output().Image;bytes=new byte[256*256*4];colored.CopyPixels(bytes,256*4,0);
        check(colored.Format==PixelFormats.Pbgra32 && colored.IsFrozen && Enumerable.Range(0,256*256).Any(i=>bytes[i*4]!=bytes[i*4+1] || bytes[i*4+1]!=bytes[i*4+2]),
            "Main Image includes the colored Bahtinov overlay, not just the grayscale background");
        // Force a pending publication, then allow it to publish on the host UI thread.
        type.GetField("lastImageOutput",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(vm,0L);
        Task.Run(()=>source.SetValue(vm,preview)).GetAwaiter().GetResult();
        before=output().Count;await Dispatcher.Yield(DispatcherPriority.Background);
        check(output().Count==before+1 && output().Thread==Environment.CurrentManagedThreadId,
            "Background preview updates are marshalled to the host UI thread");
        type.GetField("lastImageOutput",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(vm,0L);
        Task.Run(()=>source.SetValue(vm,preview)).GetAwaiter().GetResult();
        before=output().Count;await VerifyGraphVmDisposal(vm,check);await Dispatcher.Yield(DispatcherPriority.Background);
        check(output().Count==before,"Disposal prevents queued Image output after the plugin closes");
    }
}
