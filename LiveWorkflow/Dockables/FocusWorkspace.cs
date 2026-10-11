using System.Windows.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.ViewModel;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableVM {
        private readonly List<IDockableVM> focusPanels = new();
        public ICommand ShowFocusPanelsCommand { get; private set; }
        public ICommand CloseFocusSettingsCommand { get; private set; }
        private bool showFocusImage = true, showFocusHfr, changingPanelVisibility;
        public bool ShowFocusImage {
            get => showFocusImage;
            set { showFocusImage = value; SetPanelVisibility<FocusImageDockableVM>(value); HfrVisibilityChanged(); RefreshStarBoxes(); RaisePropertyChanged(); }
        }
        public bool ShowFocusHfr {
            get => showFocusHfr;
            set { showFocusHfr = value; SetPanelVisibility<FocusHistoryDockableVM>(value); HfrVisibilityChanged(); RaisePropertyChanged(); }
        }
        private bool HistoryPanelEnabled => showFocusHfr &&
            focusPanels.OfType<FocusHistoryDockableVM>().All(p => p.IsVisible && !p.IsClosed);
        private bool ImagePanelEnabled => showFocusImage &&
            focusPanels.OfType<FocusImageDockableVM>().All(p => p.IsVisible && !p.IsClosed);
        private bool HfrAnalysisEnabled => HistoryPanelEnabled || ShowHfrStarBoxes && ImagePanelEnabled;
        internal void RegisterFocusPanel(IDockableVM panel) {
            focusPanels.Add(panel);
            if (panel is FocusHistoryDockableVM history) history.HasSettings = HasSettings;
            panel.PropertyChanged += OnFocusPanelVisibilityChanged;
        }
        private void OnFocusPanelVisibilityChanged(object sender, PropertyChangedEventArgs e) {
            if (e.PropertyName is not (nameof(IDockableVM.IsVisible) or nameof(IDockableVM.IsClosed))) return;
            if (!changingPanelVisibility && sender is IDockableVM panel) {
                if (panel is FocusImageDockableVM) { showFocusImage = panel.IsVisible && !panel.IsClosed; RaisePropertyChanged(nameof(ShowFocusImage)); }
                if (panel is FocusHistoryDockableVM) { showFocusHfr = panel.IsVisible && !panel.IsClosed; RaisePropertyChanged(nameof(ShowFocusHfr)); }
            }
            HfrVisibilityChanged(); RefreshStarBoxes();
        }
        private void SetPanelVisibility<T>(bool visible) where T : IDockableVM {
            changingPanelVisibility = true;
            try { foreach (var panel in focusPanels.OfType<T>()) { if (visible) panel.IsClosed = false; panel.IsVisible = visible; } }
            finally { changingPanelVisibility = false; }
        }
        private void InitializeFocusWorkspace() {
            InitializeFocusDockLayout();
            ShowFocusPanelsCommand = new RelayCommand(_ => { ShowFocusImage = true; ShowFocusHfr = true; ShowFocusWorkspace(); });
            CloseFocusSettingsCommand = new RelayCommand(_ => SettingsVisible = false);
        }
        internal void SetFocusDockLock(bool locked) {
            HasSettings = !locked;
            if (locked) SettingsVisible = false;
            foreach (var history in focusPanels.OfType<FocusHistoryDockableVM>()) {
                history.HasSettings = !locked;
                if (locked) history.SettingsVisible = false;
            }
        }
        private void ShowFocusWorkspace() {
            IsClosed = false; IsVisible = true;
            SetPanelVisibility<FocusImageDockableVM>(showFocusImage);
            SetPanelVisibility<FocusHistoryDockableVM>(showFocusHfr);
        }
        private void ShowFocusImagePanel() {
            ShowFocusImage = true;
        }
        public override void Hide(object o) {
            if (!IsVisible || IsClosed) ShowFocusWorkspace();
            else {
                IsVisible = false;
                SetPanelVisibility<FocusImageDockableVM>(false);
                SetPanelVisibility<FocusHistoryDockableVM>(false);
            }
        }
    }
    internal static class FocusToolbar {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.ComponentModel.ICollectionView, object> configured = new();

        internal static void Configure(global::NINA.WPF.Base.Interfaces.ViewModel.IDockManagerVM host) {
            // NINA keeps toolbar tools separate from the complete docking list.
            // Filter only the toolbar view, leaving all three dockable exports,
            // their visibility commands and saved layouts available to the host.
            var tools = host.AnchorableTools;
            if (tools == null || ReferenceEquals(tools, host.Anchorables)) return;
            var view = CollectionViewSource.GetDefaultView(tools);
            if (!view.CanFilter || configured.TryGetValue(view, out _)) return;
            var previous = view.Filter;
            view.Filter = item => item is not (FocusImageDockableVM or FocusHistoryDockableVM) && (previous?.Invoke(item) ?? true);
            configured.Add(view, new object());
        }
    }
}
