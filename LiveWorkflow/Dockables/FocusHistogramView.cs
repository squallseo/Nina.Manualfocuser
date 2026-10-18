using System;
using System.Windows;
using System.Windows.Media;
using Cwseo.NINA.ManualFocuser.LiveWorkflow.Models;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    /// <summary>Two retained vector paths, rather than hundreds of WPF bars.</summary>
    public sealed class FocusHistogramView : FrameworkElement {
        public static readonly DependencyProperty PlotProperty = DependencyProperty.Register(nameof(Plot), typeof(FocusHistogramPlot), typeof(FocusHistogramView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty IsLogarithmicProperty = DependencyProperty.Register(nameof(IsLogarithmic), typeof(bool), typeof(FocusHistogramView), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty InputBrushProperty = DependencyProperty.Register(nameof(InputBrush), typeof(Brush), typeof(FocusHistogramView), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty DisplayBrushProperty = DependencyProperty.Register(nameof(DisplayBrush), typeof(Brush), typeof(FocusHistogramView), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
        public FocusHistogramPlot Plot { get => (FocusHistogramPlot)GetValue(PlotProperty); set => SetValue(PlotProperty, value); }
        public bool IsLogarithmic { get => (bool)GetValue(IsLogarithmicProperty); set => SetValue(IsLogarithmicProperty, value); }
        public Brush InputBrush { get => (Brush)GetValue(InputBrushProperty); set => SetValue(InputBrushProperty, value); }
        public Brush DisplayBrush { get => (Brush)GetValue(DisplayBrushProperty); set => SetValue(DisplayBrushProperty, value); }

        protected override void OnRender(DrawingContext dc) {
            base.OnRender(dc);
            double width = Math.Max(0, ActualWidth - 2), height = Math.Max(0, ActualHeight - 2);
            if (width == 0 || height == 0) return;
            var gridBrush = InputBrush.CloneCurrentValue(); gridBrush.Opacity = .15; gridBrush.Freeze();
            var grid = new Pen(gridBrush, 1);
            for (int i = 1; i < 4; i++) {
                dc.DrawLine(grid, new Point(width * i / 4, 0), new Point(width * i / 4, height));
                dc.DrawLine(grid, new Point(0, height * i / 4), new Point(width, height * i / 4));
            }
            if (Plot == null) return;
            var fillInput = InputBrush.CloneCurrentValue(); fillInput.Opacity = .13; fillInput.Freeze();
            var fillDisplay = DisplayBrush.CloneCurrentValue(); fillDisplay.Opacity = .22; fillDisplay.Freeze();
            dc.DrawGeometry(fillInput, new Pen(InputBrush, .8), BuildCurve(Plot, new Size(width, height), IsLogarithmic, false));
            dc.DrawGeometry(fillDisplay, new Pen(DisplayBrush, 1.2), BuildCurve(Plot, new Size(width, height), IsLogarithmic, true));
        }

        internal static StreamGeometry BuildCurve(FocusHistogramPlot plot, Size size, bool logarithmic, bool display) {
            long peak = 1;
            for (int i = 0; i < FocusHistogramPlot.Bins; i++) peak = Math.Max(peak, Math.Max(plot.InputAt(i), plot.DisplayAt(i)));
            double scale = logarithmic ? Math.Log(1 + peak) : peak;
            var geometry = new StreamGeometry();
            using (var path = geometry.Open()) {
                path.BeginFigure(new Point(0, size.Height), true, true);
                for (int i = 0; i < FocusHistogramPlot.Bins; i++) {
                    long count = display ? plot.DisplayAt(i) : plot.InputAt(i);
                    double fraction = (logarithmic ? Math.Log(1 + count) : count) / scale;
                    path.LineTo(new Point(size.Width * i / (FocusHistogramPlot.Bins - 1), size.Height * (1 - fraction)), true, false);
                }
                path.LineTo(new Point(size.Width, size.Height), true, false);
            }
            geometry.Freeze(); return geometry;
        }
    }
}
