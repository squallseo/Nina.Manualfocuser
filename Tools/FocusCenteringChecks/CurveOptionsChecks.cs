using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Cwseo.NINA.ManualFocuser.Models;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;

internal static class CurveOptionsChecks {
    public static void Run(Action<bool,string> check) {
        Exception failure=null;
        var thread=new Thread(()=>{try{CheckOptions(check);}catch(Exception e){failure=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)throw new Exception("Curve options UI checks failed",failure);
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject parent) {
        foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) {
            yield return child;foreach(var descendant in Children(child))yield return descendant;
        }
    }
    private static void CheckOptions(Action<bool,string> check) {
        var type=typeof(Cwseo.NINA.ManualFocuser.ManualFocuser).Assembly.GetType("Cwseo.NINA.ManualFocuser.Properties.Settings");
        var settings=type.GetProperty("Default").GetValue(null);
        var hfr=type.GetProperty("HfrCurveFit");var spike=type.GetProperty("SpikeCurveFit");var upgrade=type.GetProperty("UpdateSettings");
        var diagnostics=type.GetProperty("EnableFocusDiagnostics");var legacy=type.GetProperty("WriteSpikeDiagnostics");
        object oldDiagnostics=diagnostics.GetValue(settings),oldLegacy=legacy.GetValue(settings);
        object oldHfr=hfr.GetValue(settings),oldSpike=spike.GetValue(settings),oldUpgrade=upgrade.GetValue(settings);
        hfr.SetValue(settings,"Hyperbolic");spike.SetValue(settings,"Parabolic");upgrade.SetValue(settings,false);
        try {
            var profile=Fake.Of<IProfile>((m,a)=>Fake.Unexpected(m));
            var profiles=Fake.Of<IProfileService>((m,a)=>m.Name=="get_ActiveProfile"?profile:m.Name.StartsWith("add_")||m.Name.StartsWith("remove_")?null:Fake.Unexpected(m));
            var broker=Fake.Of<IMessageBroker>((m,a)=>m.Name is "Subscribe" or "Unsubscribe"?null:Fake.Unexpected(m));
            var plugin=new Cwseo.NINA.ManualFocuser.ManualFocuser(profiles,broker,null);
            XNamespace wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var source=XDocument.Load("Options.xaml");
            var stack=source.Descendants(wpf+"StackPanel").First();
            var section=new XElement(wpf+"StackPanel",stack.Elements().TakeWhile(e=>(string)e.Attribute("Text")!="Live focus"));
            string markup="<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Padding=\"12\" Background=\"#20252D\"><Border.Resources>"+
                "<SolidColorBrush x:Key=\"SecondaryBrush\" Color=\"#AAB7C8\"/><Style TargetType=\"TextBlock\"><Setter Property=\"Foreground\" Value=\"#E8EDF4\"/></Style></Border.Resources>"+section+"</Border>";
            var view=(FrameworkElement)XamlReader.Parse(markup);view.DataContext=plugin;
            view.Measure(new Size(350,230));view.Arrange(new Rect(0,0,350,230));view.UpdateLayout();
            view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);
            var combos=Children(view).OfType<ComboBox>().ToArray();
            check(combos.Length==2 && Equals(combos[0].SelectedValue,FocusCurveModel.Hyperbolic) && Equals(combos[1].SelectedValue,FocusCurveModel.Parabolic),
                "Actual plugin option bindings display the recommended HFR and Spike defaults");
            check(combos.All(c=>c.Items.Count==5) && Children(view).OfType<TextBlock>().Any(t=>t.Text=="Linear (zero crossing)"),
                "The fit selectors expose supported width models and Bahtinov displays its signed-linear model");
            var bitmap=new RenderTargetBitmap(350,230,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create("bin/curve-fit-options.png"))encoder.Save(file);
            combos[0].SetCurrentValue(Selector.SelectedValueProperty,FocusCurveModel.Parabolic);
            view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);
            check(plugin.HfrCurveFit==FocusCurveModel.Parabolic && plugin.SpikeCurveFit==FocusCurveModel.Parabolic,
                "Changing the HFR combo saves its override without changing the Spike selection");
            combos[1].SetCurrentValue(Selector.SelectedValueProperty,FocusCurveModel.Hyperbolic);
            view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);
            check(plugin.HfrCurveFit==FocusCurveModel.Parabolic && plugin.SpikeCurveFit==FocusCurveModel.Hyperbolic,
                "Changing the Spike combo updates an independent saved override");
            type.GetMethod("Reload").Invoke(settings,null);
            check(plugin.HfrCurveFit==FocusCurveModel.Parabolic && plugin.SpikeCurveFit==FocusCurveModel.Hyperbolic,
                "Saved plugin fit choices survive settings reload");
            plugin.HfrCurveFit=FocusCurveModel.LinearZeroCrossing;
            check(plugin.HfrCurveFit==FocusCurveModel.Parabolic,"The options setter rejects signed-linear fitting for an HFR width metric");
            CheckDiagnostics(plugin,type,settings,stack,check);
        } finally {
            hfr.SetValue(settings,oldHfr);spike.SetValue(settings,oldSpike);upgrade.SetValue(settings,oldUpgrade);
            diagnostics.SetValue(settings,oldDiagnostics);legacy.SetValue(settings,oldLegacy);
            type.GetMethod("Save").Invoke(settings,null);
        }
    }
    private static void CheckDiagnostics(Cwseo.NINA.ManualFocuser.ManualFocuser plugin,Type settingsType,object settings,XElement stack,Action<bool,string> check) {
        var diagnostics=settingsType.GetProperty("EnableFocusDiagnostics");
        check(diagnostics.GetCustomAttribute<System.Configuration.DefaultSettingValueAttribute>().Value=="False",
            "Diagnostic recording defaults to OFF");
        settingsType.GetProperty("WriteSpikeDiagnostics").SetValue(settings,true);
        plugin.EnableFocusDiagnostics=false;
        var create=typeof(Cwseo.NINA.ManualFocuser.Dockables.ManualFocuserDockableVM).GetMethod("CreateDiagnosticSession",BindingFlags.Static|BindingFlags.NonPublic);
        var disabled=(FocusDiagnosticSession)create.Invoke(null,new object[]{"DisabledCheck"});
        check(!disabled.IsEnabled && disabled.DirectoryPath==null,"Actual focus session creates no directory when OFF, even with a saved legacy CSV flag");
        check(disabled.SaveAsync(null,0,0,null).GetAwaiter().GetResult()==null && disabled.EventAsync(new SerializationBomb()).IsCompletedSuccessfully,
            "Disabled recording performs no pixel processing, metadata serialization or file I/O");
        XNamespace wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var section=new XElement(wpf+"StackPanel",stack.Elements().SkipWhile(e=>(string)e.Attribute("Text")!="Diagnostics").Take(3));
        string markup="<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Padding=\"12\" Background=\"#20252D\"><Border.Resources>"+
            "<SolidColorBrush x:Key=\"SecondaryBrush\" Color=\"#AAB7C8\"/><Style TargetType=\"TextBlock\"><Setter Property=\"Foreground\" Value=\"#E8EDF4\"/></Style></Border.Resources>"+section+"</Border>";
        var view=(FrameworkElement)XamlReader.Parse(markup);view.DataContext=plugin;
        view.Measure(new Size(350,150));view.Arrange(new Rect(0,0,350,150));view.UpdateLayout();
        view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);
        var option=Children(view).OfType<CheckBox>().Single();
        check(option.IsChecked==false,"Actual diagnostic option displays OFF");
        var bitmap=new RenderTargetBitmap(350,150,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var file=File.Create("bin/diagnostics-options.png"))encoder.Save(file);
        option.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
        view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);
        settingsType.GetMethod("Reload").Invoke(settings,null);
        check(plugin.EnableFocusDiagnostics,"The diagnostic checkbox saves ON across settings reload");
        string root=Path.Combine("bin","diagnostic-option-checks",Guid.NewGuid().ToString("N"));
        var session=new FocusDiagnosticSession("EnabledCheck",root,enabled:plugin.EnableFocusDiagnostics,stillEnabled:()=>plugin.EnableFocusDiagnostics);
        string frame=session.SaveAsync(new double[]{0,100,30000,65535},2,2,new {Position=1234}).GetAwaiter().GetResult();
        session.EventAsync(new {Frame=frame}).GetAwaiter().GetResult();
        check(File.Exists(Path.Combine(session.DirectoryPath,frame+".fits")) && File.Exists(Path.Combine(session.DirectoryPath,frame+".json")) && File.Exists(Path.Combine(session.DirectoryPath,"measurements.jsonl")),
            "Enabled recording writes raw FITS, metadata and measurement events");
        option.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,false);
        view.Dispatcher.Invoke(()=>{},DispatcherPriority.DataBind);
        string events=File.ReadAllText(Path.Combine(session.DirectoryPath,"measurements.jsonl"));
        session.SaveAsync(null,0,0,null).GetAwaiter().GetResult();session.EventAsync(new SerializationBomb()).GetAwaiter().GetResult();
        check(!session.IsEnabled && File.ReadAllText(Path.Combine(session.DirectoryPath,"measurements.jsonl"))==events && Directory.GetFiles(session.DirectoryPath).Length==3,
            "Turning OFF stops additional files/events in an already running session");
        var row=typeof(ManualFocuserModel).GetMethod("WriteDiagnosticsRow",BindingFlags.Instance|BindingFlags.NonPublic);
        var model=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ManualFocuserModel));
        row.Invoke(model,new object[]{1234,2.0,.1,null,null});
        check(typeof(ManualFocuserModel).GetField("diagPath",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(model)==null,
            "Manual CSV also skips all work when the global diagnostic option is OFF");
    }
    private sealed class SerializationBomb { public string Value=>throw new Exception("Disabled metadata must not be serialized."); }
}
