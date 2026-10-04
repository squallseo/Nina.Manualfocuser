using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    /// <summary>
    /// NewtonraphsonautofocusDockableView.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class ManualFocuserDockableView : UserControl {
        public ManualFocuserDockableView() {
            InitializeComponent();
            // Scroll only the controls when the dock is short; keep the chart in
            // a finite star-sized row so it can fill a larger dock.
            LayoutRoot.SizeChanged += (_, _) => ControlsScrollViewer.MaxHeight = Math.Max(64, LayoutRoot.ActualHeight - 160);
            Loaded += OnLoaded;
            DataContextChanged += OnDataContextChanged;
        }
        private async void OnLoaded(object sender, RoutedEventArgs e) {
            if (DataContext is ManualFocuserDockableVM vm) await vm.EnsureFocusTargetsLoadedAsync();
        }
        private async void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) {
            if (IsLoaded && e.NewValue is ManualFocuserDockableVM vm) await vm.EnsureFocusTargetsLoadedAsync();
        }
    }
}
