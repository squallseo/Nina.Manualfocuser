using System;
using System.Windows;
using System.Windows.Media;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    // One vector visual, rather than one WPF control or a new bitmap per star.
    public sealed class FocusStarOverlay : FrameworkElement {
        public static readonly DependencyProperty BoxesProperty = DependencyProperty.Register(nameof(Boxes), typeof(Rect[]),
            typeof(FocusStarOverlay), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double),
            typeof(FocusStarOverlay), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public Rect[] Boxes { get => (Rect[])GetValue(BoxesProperty); set => SetValue(BoxesProperty, value); }
        public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
        protected override void OnRender(DrawingContext drawing) {
            if (Boxes == null || Boxes.Length == 0) return;
            var pen = new Pen(Brushes.LimeGreen, 1 / Math.Max(.01, Zoom)); pen.Freeze();
            foreach (var box in Boxes) drawing.DrawRectangle(null, pen, box);
        }
    }
}
