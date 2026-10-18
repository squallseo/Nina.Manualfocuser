using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading;
using NINA.Core.Utility;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class FocusLevelsView : UserControl {
        private Window popupOwner;
        private readonly DispatcherTimer histogramTimer = new() { Interval = TimeSpan.FromMilliseconds(333) };
        private CancellationTokenSource histogramCancellation;
        private bool histogramBusy;
        private ImageSource histogramSource;
        public FocusLevelsView() {
            InitializeComponent();
            Unloaded += (_, _) => LevelsPopup.SetCurrentValue(Popup.IsOpenProperty, false);
            histogramTimer.Tick += (_, _) => RefreshHistogram();
            DataContextChanged += (_, _) => {
                histogramSource = null; HistogramSurface.Plot = null;
                histogramCancellation?.Cancel(); histogramCancellation?.Dispose();
                histogramCancellation = LevelsPopup.IsOpen ? new CancellationTokenSource() : null;
            };
        }
        private void OnPopupOpened(object sender, EventArgs e) {
            histogramCancellation = new CancellationTokenSource();
            histogramSource = null;
            histogramTimer.Start(); RefreshHistogram();
            popupOwner = Window.GetWindow(this);
            if (popupOwner == null) return;
            popupOwner.PreviewMouseDown += OnOwnerMouseDown;
            popupOwner.Deactivated += OnOwnerDeactivated;
        }
        private void OnPopupClosed(object sender, EventArgs e) {
            histogramTimer.Stop();
            histogramCancellation?.Cancel(); histogramCancellation?.Dispose(); histogramCancellation = null;
            histogramSource = null; HistogramSurface.Plot = null;
            ClippingText.Text = "No image";
            if (popupOwner == null) return;
            popupOwner.PreviewMouseDown -= OnOwnerMouseDown;
            popupOwner.Deactivated -= OnOwnerDeactivated;
            popupOwner = null;
        }
        private async void RefreshHistogram() {
            if (histogramBusy || !LevelsPopup.IsOpen || DataContext is not ManualFocuserDockableVM vm || histogramCancellation == null) return;
            var source = vm.LiveDisplayImage;
            if (source == null) { HistogramSurface.Plot = null; histogramSource = null; ClippingText.Text = "No image"; return; }
            if (ReferenceEquals(source, histogramSource)) return;
            histogramBusy = true;
            var token = histogramCancellation.Token;
            try {
                var plot = await vm.GetPreviewHistogramAsync(token);
                if (token.IsCancellationRequested || !LevelsPopup.IsOpen || !ReferenceEquals(DataContext, vm)) return;
                HistogramSurface.Plot = plot; histogramSource = source;
                ClippingText.Text = plot == null ? "No image" : $"Clipped · Shadows {plot.ShadowsPercent:F2}% · Highlights {plot.HighlightsPercent:F2}%";
            } catch (OperationCanceledException) { }
            catch (Exception error) { Logger.Error("MF histogram refresh failed", error); }
            finally { histogramBusy = false; }
        }
        private void OnOwnerMouseDown(object sender, MouseButtonEventArgs e) {
            if (!IsMouseOver && !((FrameworkElement)LevelsPopup.Child).IsMouseOver)
                LevelsPopup.SetCurrentValue(Popup.IsOpenProperty, false);
        }
        private void OnOwnerDeactivated(object sender, EventArgs e) => LevelsPopup.SetCurrentValue(Popup.IsOpenProperty, false);
        private void OnPopupKeyDown(object sender, KeyEventArgs e) {
            if (e.Key != Key.Escape) return;
            LevelsPopup.SetCurrentValue(Popup.IsOpenProperty, false);
            LevelsButton.Focus(); e.Handled = true;
        }
    }
}
