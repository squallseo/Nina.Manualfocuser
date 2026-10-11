using System.Reflection;
using System.IO;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using Cwseo.NINA.ManualFocuser.Dockables;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;

internal static class FocusDockLayoutChecks {
#pragma warning disable CS0618 // Match NINA's public layout-load command contract.
    private sealed class DockHost : IDockManagerVM, INotifyPropertyChanged {
        public List<IDockableVM> Anchorables { get; init; }
        public List<IDockableVM> AnchorableInfoPanels { get; } = new();
        public List<IDockableVM> AnchorableTools { get; init; } = new();
        public IAsyncCommand LoadAvalonDockLayoutCommand { get; init; }
        public ICommand ResetDockLayoutCommand => null;
        public Action Save { get; init; }
        private bool locked = true;
        public bool IsLocked { get => locked; set { locked=value; PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(IsLocked))); } }
        public event PropertyChangedEventHandler PropertyChanged;
        public Task<bool> InitializeAvalonDockLayout(object o) => Task.FromResult(true);
        public Task LoadImagingLayout(string path, CancellationToken token) => Task.CompletedTask;
        public void SaveAvalonDockLayout() => Save();
    }
#pragma warning restore CS0618
    public static async Task Run(ManualFocuserDockableVM vm, FocusImageDockableVM image, FocusHistoryDockableVM history,
        IPluginSettings settings, Action<bool,string> check) {
        var guid = Guid.Parse("56e3434f-95de-49fe-bb59-2034ea457afb");
        const string key = "FocusWorkspaceInitialLayoutV1";
        var panels = new IDockableVM[] { vm, image, history };
        void ResetInitialPlacement() {
            settings.SetValue(guid,key,false);
            settings.SetValue(guid,key+".Started",false);
            foreach(var panel in panels) settings.SetValue(guid,key+"."+panel.ContentId,false);
        }
        var apply = typeof(ManualFocuserDockableVM).GetMethod("ApplyFocusDockLayout", BindingFlags.Instance|BindingFlags.NonPublic);
        int saves = 0;
        var ready = new TaskCompletionSource<bool>();
#pragma warning disable CS0618 // Exercise the same layout-load command used by the installed NINA host.
        var command = new AsyncCommand<bool>(() => ready.Task);
#pragma warning restore CS0618
        var host = new DockHost { LoadAvalonDockLayoutCommand=command, Anchorables=panels.ToList(), AnchorableTools=panels.ToList(), Save=()=>saves++ };
        var manager = new DockingManager { DataContext = host, AnchorablesSource = panels };
        var own = panels.ToDictionary(p=>p,p=>manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(a=>ReferenceEquals(a.Content,p)));
        // NINA's LayoutAnchorableItem style supplies these stable IDs.
        foreach (var panel in panels) { own[panel].ContentId=panel.ContentId; own[panel].Title=panel.Title; }
        LayoutAnchorable Own(IDockableVM panel) => own[panel];
        LayoutAnchorable Native(string id) => manager.Layout.Descendents().OfType<LayoutAnchorable>().Single(a => a.ContentId==id);
        LayoutAnchorablePane Pane(string id) => (LayoutAnchorablePane)Native(id).Parent;
        bool After(IDockableVM panel, string id) => Own(panel).Parent == Pane(id) && Pane(id).Children.IndexOf(Own(panel)) == Pane(id).Children.IndexOf(Native(id))+1;
        var wrong = (LayoutAnchorablePane)Own(image).Parent;
        foreach(var id in new[] {"ImageControlVM","ImageHistoryVM","ImagingVM"}) {
            var pane = new LayoutAnchorablePane {DockWidth=new GridLength(3,GridUnitType.Star),DockHeight=new GridLength(2,GridUnitType.Star)};
            pane.Children.Add(new LayoutAnchorable { ContentId=id, Title=id, Content=new object(), IsSelected=true });
            manager.Layout.RootPanel.Children.Add(pane);
        }
        var nativeImage = Native("ImageControlVM");
        var imagePane = Pane("ImageControlVM");
        var imageWidth = imagePane.DockWidth; var imageHeight = imagePane.DockHeight;
        var originalRoot = manager.Layout.RootPanel;
        var loading = command.ExecuteAsync(null);
        manager.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent,manager));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(Own(image).Parent==wrong && saves==0,"New panels wait for NINA's saved layout load to complete");
        ready.SetResult(true); await loading;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(After(image,"ImageControlVM") && After(history,"ImageHistoryVM") && After(vm,"ImagingVM"),
            "First installation places each MF panel immediately after its native counterpart");
        check(!vm.ShowFocusHfr && !history.IsVisible,"Initial docking places the HFR tab without enabling its graph or analysis");
        vm.ShowFocusHfr=true;
        check(nativeImage.IsSelected && ReferenceEquals(manager.Layout.RootPanel,originalRoot) && imagePane.DockWidth==imageWidth && imagePane.DockHeight==imageHeight,
            "Initial placement preserves native tab selection, pane sizes and the surrounding layout");
        check(settings.TryGetValue(guid,key,out bool initialized) && initialized && saves==1,
            "Successful initial placement is saved once and remembered in this profile");
        check(System.Windows.Data.CollectionViewSource.GetDefaultView(host.AnchorableTools).Cast<IDockableVM>().SequenceEqual(new[]{vm}) && host.Anchorables.Count==3,
            "Native host initialization filters auxiliary toolbar entries while preserving all three docking panels");
        check(!vm.HasSettings && !image.HasSettings && !history.HasSettings,"Locked docking hides MF settings without adding image or HFR header icons");
        host.IsLocked=false;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(vm.HasSettings && !image.HasSettings && history.HasSettings,"Unlocking docking exposes native settings icons for MF Controls and MF HFR");
        var historyView=new FocusHistoryPanelView {DataContext=history,Width=600,Height=180};
        historyView.Measure(new Size(600,180));historyView.Arrange(new Rect(0,0,600,180));historyView.UpdateLayout();
        var historyContent=(FrameworkElement)historyView.FindName("HistoryContent");
        var historySettings=(FrameworkElement)historyView.FindName("HistorySettingsPanel");
        check(historyContent.Visibility==Visibility.Visible && historySettings.Visibility==Visibility.Collapsed,"MF HFR keeps its entire content area for the graph while settings are closed");
        history.SettingsVisible=true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);historyView.UpdateLayout();
        var clear=(System.Windows.Controls.Button)historyView.FindName("ClearHfrHistoryButton");
        check(historyContent.Visibility==Visibility.Collapsed && historySettings.Visibility==Visibility.Visible && ReferenceEquals(clear.Command,vm.ClearLiveGraphCommand),
            "MF HFR title settings show the shared Clear command inside its own panel");
        typeof(ManualFocuserDockableVM).GetMethod("RecordLiveHfr",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(vm,new object[]{0.0,2.0});
        clear.Command.Execute(clear.CommandParameter);
        check(vm.FocusGraphPoints.Length==0,"MF HFR settings Clear button clears the displayed live history");
        history.CloseHistorySettingsCommand.Execute(null);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);historyView.UpdateLayout();
        check(historyContent.Visibility==Visibility.Visible && historySettings.Visibility==Visibility.Collapsed && !vm.SettingsVisible,
            "Closing HFR settings restores its graph without changing Controls settings");
        vm.SettingsVisible=true;
        history.SettingsVisible=true;
        host.IsLocked=true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(!vm.HasSettings && !vm.SettingsVisible && !history.HasSettings && !history.SettingsVisible,
            "Locking docking closes settings and hides their icons in both Controls and HFR");
        host.IsLocked=false;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string serialized;
        using(var writer=new StringWriter()) {new XmlLayoutSerializer(manager).Serialize(writer);serialized=writer.ToString();}
        check(serialized.Contains(image.ContentId) && serialized.Contains(history.ContentId) && serialized.Contains(vm.ContentId),
            "NINA's real serializer retains all three dockable identities after placement");
        Own(image).Parent.RemoveChild(Own(image)); wrong.Children.Add(Own(image));
        image.IsVisible=false; history.IsClosed=true;
        apply.Invoke(vm,new object[]{manager,false,null});
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(Own(image).Parent==wrong && !image.IsVisible && history.IsClosed && saves==1,
            "Later layout updates preserve user placement and independently hidden or closed panels");
        check(vm.ArrangeFocusPanelsCommand.CanExecute(null),"A loaded native Image pane enables explicit MF arrangement");
        vm.ArrangeFocusPanelsCommand.Execute(null);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(After(image,"ImageControlVM") && After(history,"ImageHistoryVM") && After(vm,"ImagingVM") && !image.IsVisible && !history.IsVisible && saves==2,
            "Arrange MF panels restores their positions while preserving disabled Image and HFR options");
        ResetInitialPlacement();
        Own(image).Parent.RemoveChild(Own(image)); wrong.Children.Add(Own(image));
        history.IsClosed=true;
        apply.Invoke(vm,new object[]{manager,false,new HashSet<string>{image.ContentId}});
        check(Own(image).Parent==wrong && history.IsClosed && saves==2 && settings.TryGetValue(guid,key,out bool adopted) && adopted,
            "Updating an existing three-panel workspace adopts its saved layout, including closed panels");
        ResetInitialPlacement();
        vm.IsVisible=true; image.IsVisible=true; history.IsClosed=false;
        Own(vm).Parent.RemoveChild(Own(vm)); wrong.Children.Add(Own(vm));
        apply.Invoke(vm,new object[]{manager,false,new HashSet<string>{vm.ContentId}});
        check(Own(vm).Parent==wrong && After(image,"ImageControlVM") && After(history,"ImageHistoryVM"),
            "An older Manual Focuser keeps its saved Controls location while its two new panels get defaults");
        ResetInitialPlacement();
        var removedImage = nativeImage;
        imagePane.Children.Remove(removedImage);
        Own(image).Parent.RemoveChild(Own(image)); wrong.Children.Add(Own(image));
        apply.Invoke(vm,new object[]{manager,false,new HashSet<string>()});
        check(Own(image).Parent==wrong && !vm.ArrangeFocusPanelsCommand.CanExecute(null) && settings.TryGetValue(guid,key,out bool deferred) && !deferred,
            "A missing native Image pane defers initial placement without inventing or resetting a pane");
        Own(history).Parent.RemoveChild(Own(history)); wrong.Children.Add(Own(history));
        history.IsVisible=false;
        imagePane.Children.Add(removedImage);
        apply.Invoke(vm,new object[]{manager,false,panels.Select(p=>p.ContentId).ToHashSet()});
        check(After(image,"ImageControlVM") && settings.TryGetValue(guid,key,out bool resumed) && resumed,
            "Deferred initial placement completes even if a partial saved layout now contains MF panels");
        check(Own(history).Parent==wrong && !history.IsVisible,
            "Deferred placement never overrides a user's changes to panels that were already initialized");
        host.IsLocked=true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        manager.AnchorablesSource=null;
        manager.DataContext=null;
        panels.ToList().ForEach(p=>{p.IsClosed=false;p.IsVisible=true;});
    }
}
