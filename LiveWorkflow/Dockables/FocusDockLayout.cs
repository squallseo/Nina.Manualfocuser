using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using AvalonDock;
using AvalonDock.Layout;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        private WeakReference<DockingManager> focusDockManager;
        private IPluginOptionsAccessor focusDockOptions;
        private bool initialHistoryVisibilityApplied;
        private const string InitialLayoutKey = "FocusWorkspaceInitialLayoutV1";
        public ICommand ArrangeFocusPanelsCommand { get; private set; }

        private void InitializeFocusDockLayout() {
            focusDockOptions = new PluginOptionsAccessor(profileService, Guid.Parse("56e3434f-95de-49fe-bb59-2034ea457afb"));
            FocusDockLayoutObserver.Initialize();
            FocusViewerDiagnostics.Initialize();
            ArrangeFocusPanelsCommand = new RelayCommand(_ => {
                ShowFocusWorkspace();
                if (focusDockManager?.TryGetTarget(out var manager) == true)
                    manager.Dispatcher.BeginInvoke(new Action(() => ApplyFocusDockLayout(manager, true)), DispatcherPriority.ContextIdle);
            }, _ => !disposed && focusDockManager?.TryGetTarget(out var manager) == true &&
                manager.Layout.Descendents().OfType<LayoutAnchorable>().Any(a => a.ContentId == "ImageControlVM" && a.Parent is LayoutAnchorablePane));
        }

        internal void ApplyFocusDockLayout(DockingManager manager, bool force = false, IReadOnlySet<string> savedIds = null) {
            if (disposed) return;
            // The host restores dock visibility before this callback. Start each
            // session with HFR off even when an older layout saved it as visible.
            if (!initialHistoryVisibilityApplied) { initialHistoryVisibilityApplied = true; ShowFocusHfr = false; }
            if (focusDockManager?.TryGetTarget(out var current) != true || !ReferenceEquals(current, manager)) {
                focusDockManager = new WeakReference<DockingManager>(manager);
                CommandManager.InvalidateRequerySuggested();
            }
            if (profileService.ActiveProfile?.PluginSettings == null) return;
            try {
                if (!force && focusDockOptions.GetValueBoolean(InitialLayoutKey, false)) return;
                var placements = new List<(IDockableVM Panel, string Target)> { (this, "ImagingVM") };
                placements.AddRange(focusPanels.Select(p => (p, p is FocusImageDockableVM ? "ImageControlVM" : "ImageHistoryVM")));
                if (placements.Count != 3) return;
                if (!force && !focusDockOptions.GetValueBoolean(InitialLayoutKey + ".Started", false)) {
                    var saved = savedIds ?? ReadSavedFocusDockIds();
                    // Adopt workspaces from earlier builds without changing their
                    // custom positions or independently closed panels.
                    if (saved.Contains(nameof(FocusImageDockableVM)) || saved.Contains(nameof(FocusHistoryDockableVM))) {
                        focusDockOptions.SetValueBoolean(InitialLayoutKey, true);
                        return;
                    }
                    foreach (var (panel, _) in placements)
                        if (saved.Contains(panel.ContentId)) focusDockOptions.SetValueBoolean(InitialLayoutKey + "." + panel.ContentId, true);
                }
                // Remember that initialization began before saving a partial
                // layout. That save must not turn pending panels into "existing"
                // panels on the next run, when a native counterpart was hidden.
                focusDockOptions.SetValueBoolean(InitialLayoutKey + ".Started", true);
                bool complete = true;
                bool placed = false;
                foreach (var (panel, targetId) in placements) {
                    string panelKey = InitialLayoutKey + "." + panel.ContentId;
                    if (!force && focusDockOptions.GetValueBoolean(panelKey, false)) continue;
                    var target = manager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == targetId);
                    if (target?.Parent is not LayoutAnchorablePane pane || target.FindParent<LayoutFloatingWindow>() != null) {
                        complete = false; continue;
                    }
                    bool wasChangingVisibility = changingPanelVisibility;
                    changingPanelVisibility = true;
                    try { panel.IsClosed = false; panel.IsVisible = true; }
                    finally { changingPanelVisibility = wasChangingVisibility; }
                    var source = manager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => ReferenceEquals(a.Content, panel));
                    if (source == null) { complete = false; continue; }
                    var selected = pane.Children.FirstOrDefault(a => a.IsSelected);
                    if (source.Parent != pane || pane.Children.IndexOf(source) != pane.Children.IndexOf(target) + 1) {
                        source.Parent?.RemoveChild(source);
                        pane.Children.Insert(pane.Children.IndexOf(target) + 1, source);
                    }
                    // Initial placement leaves the native tabs selected. Only
                    // the explicit Arrange action switches to our three tabs.
                    if (force) source.IsSelected = true;
                    else if (selected != null) selected.IsSelected = true;
                    focusDockOptions.SetValueBoolean(panelKey, true);
                    if (panel is FocusHistoryDockableVM) SetPanelVisibility<FocusHistoryDockableVM>(showFocusHfr);
                    if (panel is FocusImageDockableVM) SetPanelVisibility<FocusImageDockableVM>(showFocusImage);
                    placed = true;
                }
                if (placed && manager.DataContext is IDockManagerVM host) host.SaveAvalonDockLayout();
                if (complete) {
                    focusDockOptions.SetValueBoolean(InitialLayoutKey, true);
                }
            } catch (Exception e) {
                Logger.Error("Manual Focuser initial dock placement failed; existing layout retained", e);
            }
        }

        private HashSet<string> ReadSavedFocusDockIds() {
            var paths = new[] {
                Path.Combine(ProfileService.PROFILEFOLDER, $"{profileService.ActiveProfile.Id}.dock.config"),
                Path.Combine(ProfileService.PROFILEFOLDER, "GLOBAL.dock.config")
            };
            // Preserve saved positions conservatively when a global layout is
            // present as well: NINA owns its global/per-profile layout setting.
            var existing = paths.Where(File.Exists).ToArray();
            if (existing.Length == 0) existing = new[] { Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "avalondock.config") }.Where(File.Exists).ToArray();
            return existing.SelectMany(path => XDocument.Load(path).Descendants()
                .Select(e => (string)e.Attribute("ContentId")).Where(id => id != null)).ToHashSet();
        }
    }

    // Wait for the host's public layout-load command to complete. A Loaded
    // callback alone can run before NINA deserializes (and replaces) the layout.
    // These bindings also handle delayed plugin composition and profile changes.
    internal sealed class FocusDockLayoutObserver : DependencyObject {
        private static readonly ConditionalWeakTable<DockingManager, FocusDockLayoutObserver> observers = new();
        private static bool initialized;
        private readonly DockingManager manager;
        private LayoutRoot observedLayout;
        private bool queued, applying;
        private static readonly DependencyProperty ReadyProperty = DependencyProperty.Register("Ready", typeof(bool), typeof(FocusDockLayoutObserver), new PropertyMetadata(false, OnChanged));
        private static readonly DependencyProperty PanelsProperty = DependencyProperty.Register("Panels", typeof(object), typeof(FocusDockLayoutObserver), new PropertyMetadata(null, OnChanged));
        private static readonly DependencyProperty ToolsProperty = DependencyProperty.Register("Tools", typeof(object), typeof(FocusDockLayoutObserver), new PropertyMetadata(null, OnChanged));
        private static readonly DependencyProperty LockedProperty = DependencyProperty.Register("Locked", typeof(bool), typeof(FocusDockLayoutObserver), new PropertyMetadata(true, OnChanged));

        internal static void Initialize() {
            if (initialized) return;
            initialized = true;
            EventManager.RegisterClassHandler(typeof(DockingManager), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, e) => {
                    if (sender is DockingManager manager && ReferenceEquals(e.OriginalSource, manager) && manager.DataContext is IDockManagerVM)
                        observers.GetValue(manager, m => new FocusDockLayoutObserver(m)).Queue();
                }));
            // Plugin composition may finish after the Imaging view's Loaded
            // event. Attach to an already loaded host as well.
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
                foreach (Window window in Application.Current.Windows)
                    foreach (var manager in MainImageRoiEditor.Descendants(window).OfType<DockingManager>())
                        if (manager.IsLoaded && manager.DataContext is IDockManagerVM)
                            observers.GetValue(manager, m => new FocusDockLayoutObserver(m)).Queue();
            }), DispatcherPriority.ContextIdle);
        }
        private FocusDockLayoutObserver(DockingManager manager) {
            this.manager = manager;
            BindingOperations.SetBinding(this, ReadyProperty, new Binding("DataContext.LoadAvalonDockLayoutCommand.Execution.IsCompleted") {
                Source = manager, FallbackValue = false, TargetNullValue = false
            });
            BindingOperations.SetBinding(this, PanelsProperty, new Binding(nameof(DockingManager.AnchorablesSource)) { Source = manager });
            BindingOperations.SetBinding(this, ToolsProperty, new Binding("DataContext.AnchorableTools") { Source = manager });
            BindingOperations.SetBinding(this, LockedProperty, new Binding("DataContext.IsLocked") { Source = manager, FallbackValue = true, TargetNullValue = true });
            manager.LayoutChanged += (_, _) => ObserveLayout();
            ObserveLayout();
        }
        private void ObserveLayout() {
            if (observedLayout != null) observedLayout.Updated -= OnLayoutUpdated;
            observedLayout = manager.Layout;
            if (observedLayout != null) observedLayout.Updated += OnLayoutUpdated;
            Queue();
        }
        private void OnLayoutUpdated(object sender, EventArgs e) => Queue();
        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((FocusDockLayoutObserver)d).Queue();
        private void Queue() {
            if (queued || applying || !(bool)GetValue(ReadyProperty)) return;
            queued = true;
            manager.Dispatcher.BeginInvoke(new Action(() => {
                queued = false;
                if (!(bool)GetValue(ReadyProperty) || manager.DataContext is not IDockManagerVM host) return;
                var session = (manager.AnchorablesSource as IEnumerable)?.OfType<ManualFocuserDockableVM>().SingleOrDefault();
                if (session == null) return;
                FocusToolbar.Configure(host);
                session.SetFocusDockLock((bool)GetValue(LockedProperty));
                applying = true;
                try { session.ApplyFocusDockLayout(manager); }
                finally { applying = false; }
            }), DispatcherPriority.ContextIdle);
        }
    }
}
