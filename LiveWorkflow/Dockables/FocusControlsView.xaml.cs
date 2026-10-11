using System;
using System.Windows;
using System.Windows.Controls;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class FocusControlsView : UserControl {
        public FocusControlsView() {
            InitializeComponent();
            SizeChanged += (_, _) => UpdateSetupHeight();
            EssentialControls.SizeChanged += (_, _) => UpdateSetupHeight();
            Loaded += async (_, _) => {
                if (DataContext is ManualFocuserDockableVM vm) await vm.EnsureFocusTargetsLoadedAsync();
            };
        }
        private void UpdateSetupHeight() => ControlsScrollViewer.MaxHeight =
            Math.Max(64, ActualHeight - EssentialControls.ActualHeight - 48);
        private void OnMeasurementsChanged(object sender, RoutedEventArgs e) { }
    }
}
