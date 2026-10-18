using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cwseo.NINA.ManualFocuser.Dockables;
using ImageView = NINA.WPF.Base.View.ImageView;

internal static class ViewerDiagnosticsChecks {
    public static async Task Run(ManualFocuserDockableVM vm, object host, Action<bool,string> check) {
        var trace=typeof(ManualFocuserDockableVM).Assembly.GetType("Cwseo.NINA.ManualFocuser.Dockables.FocusViewerDiagnostics",true);
        var table=trace.GetField("observers",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        object Observer(ImageView image) {
            object[] args={image,null};
            return (bool)table.GetType().GetMethod("TryGetValue").Invoke(table,args)?args[1]:null;
        }
        var observe=trace.GetMethod("Observe",BindingFlags.Static|BindingFlags.NonPublic);
        var capture=trace.GetMethod("Capture",BindingFlags.Instance|BindingFlags.NonPublic);
        var queue=trace.GetMethod("Queue",BindingFlags.Instance|BindingFlags.NonPublic);
        var lastSnapshot=trace.GetField("lastSnapshot",BindingFlags.Instance|BindingFlags.NonPublic);
        var attached=trace.GetField("attached",BindingFlags.Instance|BindingFlags.NonPublic);
        var sequence=trace.GetField("sequence",BindingFlags.Instance|BindingFlags.NonPublic);
        string directory=(string)trace.GetProperty("DirectoryPath",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        var panel=new ManualFocuserDockableView();
        var own=((Grid)panel.FindName("PreviewCard")).Children.OfType<ImageView>().Single();
        var native=new ImageView{DataContext=host};
        var grid=new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(panel,1);grid.Children.Add(native);grid.Children.Add(panel);
        var window=new Window{Content=grid,Width=1000,Height=350,Opacity=0,ShowActivated=false,ShowInTaskbar=false,Left=-10000,Top=-10000};
        try {
            window.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(Observer(own)==null,"An unbound MF panel does not create diagnostic hooks");
            panel.DataContext=vm;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var observer=Observer(own);
            check(observer!=null && (bool)attached.GetValue(observer),"MF Image attaches its own observer even when its context arrives after Loaded");
            check(host.GetType().Name=="ImageControlVM" && Observer(native)==null,"Loading a real native ImageControlVM viewer does not attach MF diagnostics");
            observe.Invoke(null,new object[]{native});
            check(Observer(native)==null,"Explicit discovery also refuses the host's native Image viewer");
            int viewerId=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(own);
            async Task VerifyBitmap(BitmapSource image, byte expected, string reason, Action afterCapture=null) {
                own.SetCurrentValue(ImageView.ImageProperty,image);native.Image=image;
                var scale=(ScaleTransform)own.FindName("PART_ScaleTransform");
                scale.ScaleX=scale.ScaleY=1+expected/255.0;
                lastSnapshot.SetValue(observer,DateTime.MinValue);
                queue.Invoke(observer,new object[]{reason,true});capture.Invoke(observer,null);
                afterCapture?.Invoke();
                check(ReferenceEquals(own.Image,image) && ReferenceEquals(native.Image,image),"Diagnostic capture preserves the displayed frame references: "+reason);
                var wait=System.Diagnostics.Stopwatch.StartNew();
                while(wait.ElapsedMilliseconds<5000) {
                    await Task.Delay(25);
                    if(!Directory.Exists(directory))continue;
                    foreach(string path in Directory.GetFiles(directory,"snapshot-*.json")) {
                        try {
                            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                            using var json=JsonDocument.Parse(input);
                            var state=json.RootElement;
                            if(state.GetProperty("ViewerId").GetInt32()!=viewerId ||
                                !state.GetProperty("Reasons").EnumerateArray().Any(r=>r.GetString()==reason))continue;
                            string png=Path.Combine(directory,Path.GetFileNameWithoutExtension(path)+"-bitmap.png");
                            using var bitmapInput=new FileStream(png,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                            var frame=BitmapFrame.Create(bitmapInput,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                            var bytes=new byte[32*32];frame.CopyPixels(bytes,32,0);
                            check(state.GetProperty("SnapshotKind").GetString()=="source-bitmap; not screen capture" &&
                                frame.PixelWidth==32 && frame.PixelHeight==32 && bytes.All(b=>b==expected),
                                "Diagnostic PNG contains the requested frame's bitmap pixels: "+reason);
                            return;
                        } catch(IOException) { } catch(JsonException) { }
                    }
                }
                throw new Exception("Source bitmap snapshot was not written: "+reason);
            }
            // Exercise replacement frames and zoom changes without taking a WPF
            // screenshot to inspect them: that was the operation causing issue #2.
            for(int frame=1;frame<=20;frame++) {
                byte value=(byte)(frame*10);
                var image=BitmapSource.Create(32,32,96,96,PixelFormats.Gray8,null,Enumerable.Repeat(value,1024).ToArray(),32);
                image.Freeze();
                await VerifyBitmap(image,value,"issue-2-frame-"+frame);
            }
            check(Observer(native)==null && !Directory.GetFiles(directory,"*-software-view.png").Any(),
                "Twenty diagnostic snapshots leave native viewers unobserved and produce no live-view screenshots");
            var mutable=new WriteableBitmap(32,32,96,96,PixelFormats.Gray8,null);
            var bright=Enumerable.Repeat((byte)211,1024).ToArray();
            mutable.WritePixels(new Int32Rect(0,0,32,32),bright,32,0);
            await VerifyBitmap(mutable,211,"issue-2-mutable-source",()=>mutable.WritePixels(new Int32Rect(0,0,32,32),new byte[1024],32,0));
            check(!mutable.IsFrozen,"Saving a mutable bitmap freezes a private copy instead of the live display source");
            own.DataContext=host;
            long before=(long)sequence.GetValue(observer);
            queue.Invoke(observer,new object[]{"foreign-context",true});capture.Invoke(observer,null);
            check(!(bool)attached.GetValue(observer) && (long)sequence.GetValue(observer)==before,
                "Reusing an MF viewer for a foreign context detaches hooks and ignores queued captures");
            own.DataContext=vm;
            check((bool)attached.GetValue(observer),"Returning to the MF context restores its local observer");
        } finally { window.Close(); }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var closedObserver=Observer(own);
        long afterClose=(long)sequence.GetValue(closedObserver);
        capture.Invoke(closedObserver,null);
        check(!(bool)attached.GetValue(closedObserver) && (long)sequence.GetValue(closedObserver)==afterClose,
            "Closing MF Image detaches diagnostics and prevents deferred capture work");
    }
}
