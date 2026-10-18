using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class ManualFocuserDockableView : UserControl {
        private global::NINA.WPF.Base.View.ImageView FocusImageView => PreviewCard.Children.OfType<global::NINA.WPF.Base.View.ImageView>().Single();
        public ManualFocuserDockableView() {
            InitializeComponent();
            ((FocusRoiOverlay)FocusImageView.ImageAreaContent).AttachViewer(FocusImageView);
            FocusImageView.Loaded += (_, _) => FocusViewerDiagnostics.Observe(FocusImageView);
            FocusImageView.DataContextChanged += (_, _) => {
                if (FocusImageView.IsLoaded) FocusViewerDiagnostics.Observe(FocusImageView);
            };
            DataContextChanged += (_,e) => {
                if (e.OldValue is ManualFocuserDockableVM old) PropertyChangedEventManager.RemoveHandler(old,OnSessionChanged,string.Empty);
                if (e.NewValue is ManualFocuserDockableVM current) PropertyChangedEventManager.AddHandler(current,OnSessionChanged,string.Empty);
                UpdatePreviewLayout();
            };
        }
        private void OnPreviewWorkspaceSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewLayout();
        private void OnSessionChanged(object sender, PropertyChangedEventArgs e) {
            if (e.PropertyName is nameof(ManualFocuserDockableVM.ShowLiveGraphs) or nameof(ManualFocuserDockableVM.IsSelectingRoi)) UpdatePreviewLayout();
        }
        private void UpdatePreviewLayout() {
            if (MeasurementsPanel == null) return;
            bool show = DataContext is ManualFocuserDockableVM vm && vm.ShowLiveGraphs && !vm.IsSelectingRoi;
            PreviewWorkspace.ColumnDefinitions[1].Width = show ? new GridLength(1,GridUnitType.Star) : new GridLength(0);
            PreviewCard.Margin = new Thickness(0,0,show ? 8 : 0,0);
            MeasurementsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
