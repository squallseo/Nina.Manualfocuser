using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OxyPlot;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public partial class LiveFocusHistoryView : UserControl {
        private readonly OxyPlot.Wpf.ArrowAnnotation movement = new() {
            Color = Colors.LightSkyBlue, StrokeThickness = 2, HeadLength = 6, HeadWidth = 4,
            Layer = OxyPlot.Annotations.AnnotationLayer.BelowSeries
        };
        private readonly OxyPlot.Wpf.PointAnnotation minimum = new() {
            Fill = Colors.Red, Shape = MarkerType.Triangle, Size = 7,
            TextColor = Colors.LightGray, TextMargin = 8,
            TextHorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextVerticalAlignment = System.Windows.VerticalAlignment.Bottom
        };
        private ManualFocuserDockableVM session;
        public LiveFocusHistoryView() {
            InitializeComponent();
            DataContextChanged += (_,_) => ObserveSession();
            Loaded += (_,_) => ObserveSession();
            Unloaded += (_,_) => {
                if (session != null) PropertyChangedEventManager.RemoveHandler(session, OnHistoryChanged, nameof(ManualFocuserDockableVM.FocusGraphPoints));
                session = null;
            };
        }
        private void ObserveSession() {
            if (session != null) PropertyChangedEventManager.RemoveHandler(session, OnHistoryChanged, nameof(ManualFocuserDockableVM.FocusGraphPoints));
            session = DataContext as ManualFocuserDockableVM;
            if (session != null) PropertyChangedEventManager.AddHandler(session, OnHistoryChanged, nameof(ManualFocuserDockableVM.FocusGraphPoints));
            UpdateAnnotations();
        }
        private void OnHistoryChanged(object sender, PropertyChangedEventArgs e) => UpdateAnnotations();
        private void UpdateAnnotations() {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateAnnotations)); return; }
            var measurements = (OxyPlot.Wpf.LineSeries)HistoryPlot.Series[0];
            bool manual = session?.IsManualFocus == true;
            double markerSize = manual ? 4.5 : 1.5;
            if (measurements.MarkerSize != markerSize) {
                measurements.MarkerSize = markerSize;
                measurements.MarkerStroke = manual ? Color.FromRgb(27,29,32) : Colors.Transparent;
                measurements.MarkerStrokeThickness = manual ? 1.2 : 0;
            }
            if (session?.IsManualFocus != true && HistoryPlot.Annotations.Count == 0) return;
            HistoryPlot.Annotations.Clear();
            if (session?.IsManualFocus != true) return;
            var points = session.ManualFocusMovement;
            if (points.Length == 2) {
                movement.StartPoint = points[0]; movement.EndPoint = points[1];
                HistoryPlot.Annotations.Add(movement);
            }
            var best = session.ManualFocusMinimum;
            if (double.IsFinite(best.X) && double.IsFinite(best.Y)) {
                minimum.X = best.X; minimum.Y = best.Y;
                minimum.Text = $"Pos {best.X:0} · HFR {best.Y:0.000}";
                HistoryPlot.Annotations.Add(minimum);
            }
        }
    }
}
