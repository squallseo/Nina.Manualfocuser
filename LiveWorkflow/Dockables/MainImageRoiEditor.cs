using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NINA.WPF.Base.View;

namespace Cwseo.NINA.ManualFocuser.Dockables
{
    // Attach only during explicit ROI editing and only to the ImageView displaying
    // our overview. Public WPF visuals are used; no private host fields or bindings
    // are replaced. The viewport adorner keeps text/grips at a fixed screen size.
    internal sealed class MainImageRoiEditor : IDisposable
    {
        private readonly ManualFocuserDockableVM vm;
        private readonly Action<BitmapSource> publish;
        private readonly Action<bool> attached;
        private readonly DispatcherTimer timer;
        private BitmapSource bitmap;
        private RoiViewportAdorner adorner;
        private AdornerLayer layer;
        private bool disposed;

        public MainImageRoiEditor(ManualFocuserDockableVM vm, Action<BitmapSource> publish, Action<bool> attached)
        {
            this.vm = vm; this.publish = publish; this.attached = attached;
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += OnTick;
        }

        public void SetImage(BitmapSource image)
        {
            if (disposed || ReferenceEquals(bitmap, image)) return;
            bitmap = image;
            publish(image);
            adorner?.SetImage(image);
            // Allow the host's existing Image binding to run before matching it.
            timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (disposed) return;
            if (adorner != null)
            {
                if (!adorner.MatchesImage || !adorner.View.IsVisible) Detach();
                else { adorner.RefreshGeometry(); return; }
            }
            if (Application.Current == null) return;
            foreach (Window window in Application.Current.Windows)
                if (window.IsVisible && TryAttach(window)) return;
        }

        internal bool TryAttach(DependencyObject root)
        {
            if (disposed || bitmap == null || !vm.IsSelectingRoi || !vm.ShowInNinaImage) return false;
            var view = Descendants(root).OfType<ImageView>().FirstOrDefault(v => v.IsVisible && ReferenceEquals(v.Image, bitmap));
            if (view == null) return false;
            var canvas = Descendants(view).OfType<Canvas>().FirstOrDefault(c => c.Name == "PART_Canvas");
            var viewport = Descendants(view).OfType<ScrollContentPresenter>().FirstOrDefault();
            var candidateLayer = viewport == null ? null : AdornerLayer.GetAdornerLayer(viewport);
            if (canvas == null || candidateLayer == null || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return false;
            Detach();
            layer = candidateLayer;
            adorner = new RoiViewportAdorner(viewport, view, canvas, vm, bitmap);
            layer.Add(adorner);
            attached(true);
            return true;
        }

        internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
        }

        private void Detach()
        {
            if (adorner == null) return;
            adorner.Dispose(); layer.Remove(adorner);
            adorner = null; layer = null;
            attached(false);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; timer.Stop(); timer.Tick -= OnTick; Detach();
        }
    }

    internal sealed class RoiViewportAdorner : Adorner, IDisposable
    {
        internal ImageView View { get; }
        private readonly Canvas canvas;
        private readonly ManualFocuserDockableVM vm;
        private BitmapSource bitmap;
        private Point? start;
        private Point startScreen;
        private Rect original;
        private RoiHandle handle;
        private bool disposed;
        private Matrix? lastTransform;
        private Size lastCanvasSize, lastViewportSize;
        private static readonly Brush Yellow = Brushes.Gold;
        internal bool MatchesImage => ReferenceEquals(View.Image, bitmap);
        private bool Active => !disposed && vm.IsSelectingRoi && vm.ShowInNinaImage && vm.CanConfigureLive && MatchesImage &&
            ReferenceEquals(AdornerLayer.GetAdornerLayer(AdornedElement), VisualTreeHelper.GetParent(this)) &&
            vm.SelectionSensorWidth >= 32 && vm.SelectionSensorHeight >= 32 && canvas.ActualWidth > 0 && canvas.ActualHeight > 0;

        internal RoiViewportAdorner(UIElement viewport, ImageView view, Canvas canvas, ManualFocuserDockableVM vm, BitmapSource image) : base(viewport)
        {
            View = view; this.canvas = canvas; this.vm = vm; bitmap = image;
            IsHitTestVisible = false; ClipToBounds = true;
            // Handle left clicks before ImageView's ScrollViewer starts panning.
            View.PreviewMouseLeftButtonDown += Down;
            View.PreviewMouseMove += Move;
            View.PreviewMouseLeftButtonUp += Up;
            View.LostMouseCapture += LostCapture;
            View.QueryCursor += OnQueryCursor;
            View.LayoutUpdated += OnLayoutUpdated;
            vm.PropertyChanged += OnRoiChanged;
        }

        internal void SetImage(BitmapSource image) { bitmap = image; InvalidateVisual(); }
        private void OnLayoutUpdated(object sender, EventArgs e) => RefreshGeometry();
        internal void RefreshGeometry()
        {
            if (!Active) return;
            var transform = canvas.TransformToVisual(this);
            var origin = transform.Transform(new Point());
            var x = transform.Transform(new Point(1, 0)) - origin;
            var y = transform.Transform(new Point(0, 1)) - origin;
            var matrix = new Matrix(x.X, x.Y, y.X, y.Y, origin.X, origin.Y);
            if (lastTransform == matrix && lastCanvasSize == canvas.RenderSize && lastViewportSize == RenderSize) return;
            lastTransform = matrix; lastCanvasSize = canvas.RenderSize; lastViewportSize = RenderSize;
            InvalidateVisual();
        }
        private void OnRoiChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(vm.PreviewRoiRectangle) or nameof(vm.CanConfigureLive)) InvalidateVisual();
        }
        private Rect SensorRoi
        {
            get { var r = vm.PreviewRoiRectangle; return new Rect(r.X, r.Y, r.Width, r.Height); }
        }

        // Transform through the host Canvas, including its zoom, scroll, rotation
        // and flip. Work in viewport pixels when hit testing/drawing, sensor pixels
        // when changing the shared ROI.
        internal Point SensorToScreen(Point p) => canvas.TransformToVisual(this).Transform(new Point(
            p.X / vm.SelectionSensorWidth * canvas.ActualWidth, p.Y / vm.SelectionSensorHeight * canvas.ActualHeight));
        internal Point? ScreenToSensor(Point p, bool clamp = false)
        {
            if (!Active) return null;
            var inverse = canvas.TransformToVisual(this).Inverse;
            if (inverse == null || !inverse.TryTransform(p, out var local)) return null;
            double x = local.X / canvas.ActualWidth, y = local.Y / canvas.ActualHeight;
            if (!clamp && (x < 0 || y < 0 || x > 1 || y > 1 || !new Rect(RenderSize).Contains(p))) return null;
            return new Point(Math.Clamp(x, 0, 1) * vm.SelectionSensorWidth, Math.Clamp(y, 0, 1) * vm.SelectionSensorHeight);
        }
        private Point[] Corners(Rect r) => new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft }.Select(SensorToScreen).ToArray();
        private static double DistanceToEdge(Point p, Point a, Point b)
        {
            var v = b - a;
            double t = v.LengthSquared > 0 ? Math.Clamp(Vector.Multiply(p - a, v) / v.LengthSquared, 0, 1) : 0;
            return (p - (a + t * v)).Length;
        }
        internal RoiHandle HitTest(Point p)
        {
            var sensor = ScreenToSensor(p);
            if (sensor == null) return RoiHandle.None;
            var corners = Corners(SensorRoi);
            RoiHandle result = RoiHandle.None;
            if (DistanceToEdge(p, corners[0], corners[3]) <= 6) result |= RoiHandle.Left;
            else if (DistanceToEdge(p, corners[1], corners[2]) <= 6) result |= RoiHandle.Right;
            if (DistanceToEdge(p, corners[0], corners[1]) <= 6) result |= RoiHandle.Top;
            else if (DistanceToEdge(p, corners[3], corners[2]) <= 6) result |= RoiHandle.Bottom;
            return result != RoiHandle.None ? result : SensorRoi.Contains(sensor.Value) ? RoiHandle.Move : RoiHandle.Draw;
        }
        internal Cursor CursorFor(RoiHandle h)
        {
            if (h == RoiHandle.None) return Cursors.Arrow;
            if (h == RoiHandle.Move) return Cursors.SizeAll;
            if (h == RoiHandle.Draw) return Cursors.Cross;
            var direction = new Point(h.HasFlag(RoiHandle.Left) ? -1 : h.HasFlag(RoiHandle.Right) ? 1 : 0,
                h.HasFlag(RoiHandle.Top) ? -1 : h.HasFlag(RoiHandle.Bottom) ? 1 : 0);
            var v = SensorToScreen(direction) - SensorToScreen(new Point());
            int quadrant = ((int)Math.Round(Math.Atan2(v.Y, v.X) * 4 / Math.PI) % 4 + 4) % 4;
            return quadrant switch { 0 => Cursors.SizeWE, 1 => Cursors.SizeNWSE, 2 => Cursors.SizeNS, _ => Cursors.SizeNESW };
        }
        private void OnQueryCursor(object sender, QueryCursorEventArgs e)
        {
            if (!Active) return;
            var h = start != null ? handle : HitTest(Mouse.GetPosition(this));
            if (h == RoiHandle.None) return;
            e.Cursor = CursorFor(h); e.Handled = true;
        }
        private void Down(object sender, MouseButtonEventArgs e)
        {
            if (!Active) return;
            var p = e.GetPosition(this);
            start = ScreenToSensor(p);
            if (start == null) return;
            original = SensorRoi; startScreen = p; handle = HitTest(p);
            if (!View.CaptureMouse()) { start = null; return; }
            e.Handled = true;
        }
        private void Apply(Point end) => vm.EditPreviewRoi(RoiInteraction.Drag(original, start.Value, end, handle,
            vm.SelectionSensorWidth, vm.SelectionSensorHeight));
        private void Move(object sender, MouseEventArgs e)
        {
            if (start == null || !Active || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if ((p - startScreen).Length > 3 && ScreenToSensor(p, true) is Point end) Apply(end);
            e.Handled = true;
        }
        private void Up(object sender, MouseButtonEventArgs e)
        {
            if (start == null) return;
            var p = e.GetPosition(this);
            if (Active && ScreenToSensor(p, true) is Point end)
            {
                if ((p - startScreen).Length > 3) Apply(end);
                else if (handle == RoiHandle.Draw) vm.SelectPreviewRoi(end.X / vm.SelectionSensorWidth, end.Y / vm.SelectionSensorHeight);
            }
            start = null; View.ReleaseMouseCapture(); InvalidateVisual(); e.Handled = true;
        }
        private void LostCapture(object sender, MouseEventArgs e) => start = null;

        protected override void OnRender(DrawingContext dc)
        {
            if (!Active) return;
            var corners = Corners(SensorRoi);
            var outline = new StreamGeometry();
            using (var context = outline.Open()) { context.BeginFigure(corners[0], false, true); context.PolyLineTo(corners.Skip(1).ToArray(), true, false); }
            dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
            dc.DrawGeometry(null, new Pen(Brushes.Black, 5), outline);
            dc.DrawGeometry(null, new Pen(Yellow, 3), outline);
            for (int i = 0; i < corners.Length; i++)
                foreach (var p in new[] { corners[i], corners[i] + .5 * (corners[(i + 1) % 4] - corners[i]) })
                    dc.DrawRectangle(Yellow, new Pen(Brushes.Black, 1), new Rect(p.X - 4, p.Y - 4, 8, 8));
            var text = new FormattedText($"{vm.PreviewRoiRectangle.Width} × {vm.PreviewRoiRectangle.Height} px", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Yellow, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double x = Math.Clamp(corners.Min(p => p.X), 0, Math.Max(0, ActualWidth - text.Width - 8));
            double y = corners.Min(p => p.Y) - text.Height - 7;
            if (y < 0) y = corners.Max(p => p.Y) + 7;
            y = Math.Clamp(y, 0, Math.Max(0, ActualHeight - text.Height - 6));
            dc.DrawRectangle(Brushes.Black, null, new Rect(x, y, text.Width + 8, text.Height + 6));
            dc.DrawText(text, new Point(x + 4, y + 3)); dc.Pop();
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            View.PreviewMouseLeftButtonDown -= Down; View.PreviewMouseMove -= Move; View.PreviewMouseLeftButtonUp -= Up;
            View.LostMouseCapture -= LostCapture; View.QueryCursor -= OnQueryCursor; View.LayoutUpdated -= OnLayoutUpdated;
            vm.PropertyChanged -= OnRoiChanged;
            if (start != null) View.ReleaseMouseCapture(); start = null;
        }
    }
}
