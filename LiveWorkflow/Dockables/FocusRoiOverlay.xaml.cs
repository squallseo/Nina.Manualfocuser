using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.ComponentModel;
using System.Windows.Data;

namespace Cwseo.NINA.ManualFocuser.Dockables
{
    /// <summary>
    /// Mouse editing for the shared live-preview ROI.
    /// </summary>
    public partial class FocusRoiOverlay : UserControl
    {
        private global::NINA.WPF.Base.View.ImageView FocusImageView;
        public FocusRoiOverlay()
        {
            InitializeComponent();
            // ROI interactions stay attached to the independently dockable image.
            DataContextChanged += OnDataContextChanged;

        }
        public void AttachViewer(global::NINA.WPF.Base.View.ImageView viewer) {
            FocusImageView = viewer;
            if (viewer.FindName("PART_ScaleTransform") is ScaleTransform starZoom)
                BindingOperations.SetBinding(StarBoxes, FocusStarOverlay.ZoomProperty, new Binding(nameof(ScaleTransform.ScaleX)) { Source = starZoom });
            FocusViewerDiagnostics.Observe(viewer);
            // Keep the native canvas and Image for zoom/fit measurements, but draw
            // pixels directly, avoiding the VisualBrush's intermediate render
            // surface for large live mosaics.
            if (viewer.FindName("PART_Canvas") is Canvas canvas) {
                if (canvas.Background is VisualBrush background) background.Opacity = 0;
                // Pixel-space bounds must follow the displayed frame directly.
                // The transparent native VisualBrush is not a reliable layout
                // source when ROI and mosaic dimensions alternate during live view.
                BindImageSize(canvas, viewer);
            }
            if (viewer.FindName("PART_Image") is Image nativeImage) {
                BindImageSize(nativeImage, viewer);
                BindingOperations.SetBinding(BitmapImage, RenderOptions.BitmapScalingModeProperty,
                    new Binding { Source = nativeImage, Path = new PropertyPath("(0)", RenderOptions.BitmapScalingModeProperty) });
                BindingOperations.SetBinding(InspectorTiles, RenderOptions.BitmapScalingModeProperty,
                    new Binding { Source = nativeImage, Path = new PropertyPath("(0)", RenderOptions.BitmapScalingModeProperty) });
            }
            if (viewer.FindName("PART_ImageViewContent") is FrameworkElement content) BindImageSize(content, viewer);
            BindImageSize(this, viewer);
            if (viewer.FindName("PART_ScaleTransform") is ScaleTransform zoom) zoom.Changed += (_,_) => DrawRoiOutline();
            DrawRoiOutline();
        }
        private static void BindImageSize(FrameworkElement element, global::NINA.WPF.Base.View.ImageView viewer) {
            BindingOperations.SetBinding(element, WidthProperty, new Binding("Image.PixelWidth") { Source = viewer, TargetNullValue = 0d, FallbackValue = 0d });
            BindingOperations.SetBinding(element, HeightProperty, new Binding("Image.PixelHeight") { Source = viewer, TargetNullValue = 0d, FallbackValue = 0d });
        }
        private Point? roiDragStart;
        private double ImageScale => FocusImageView?.FindName("PART_ScaleTransform") is ScaleTransform zoom ? Math.Max(.01,zoom.ScaleX) : 1;
        private Rect DisplayedImageBounds()
        {
            // ImageAreaContent shares NINA's pixel-space canvas and transform.
            // GetPosition automatically undoes the viewer's zoom/rotation/flip.
            var image = FocusImageView?.Image;
            return image == null ? Rect.Empty : new Rect(0,0,image.PixelWidth,image.PixelHeight);
        }
        private Point? PreviewPoint(MouseEventArgs e, bool clamp = false)
        {
            var bounds = DisplayedImageBounds();
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;
            var p = e.GetPosition(PreviewSurface);
            if (!clamp && !bounds.Contains(p)) return null;
            return new Point(Math.Clamp((p.X - bounds.X) / bounds.Width, 0, 1), Math.Clamp((p.Y - bounds.Y) / bounds.Height, 0, 1));
        }
        private RoiHandle roiHandle;
        private Rect originalRoi;
        private Rect ScreenRoi(ManualFocuserDockableVM vm)
        {
            var bounds = DisplayedImageBounds();
            var roi = vm.PreviewRoiRectangle;
            return new Rect(bounds.X + bounds.Width * roi.X / vm.SelectionSensorWidth,
                bounds.Y + bounds.Height * roi.Y / vm.SelectionSensorHeight,
                bounds.Width * roi.Width / vm.SelectionSensorWidth, bounds.Height * roi.Height / vm.SelectionSensorHeight);
        }
        private void UpdateRoiCursor(MouseEventArgs e)
        {
            if (DataContext is not ManualFocuserDockableVM vm || !vm.IsSelectingRoi || !vm.CanConfigureLive || PreviewPoint(e) == null)
            {
                PreviewSurface.Cursor = Cursors.Arrow; return;
            }
            var handle = roiDragStart != null ? roiHandle : RoiInteraction.HitTest(ScreenRoi(vm), e.GetPosition(PreviewSurface),6/ImageScale);
            PreviewSurface.Cursor = handle switch
            {
                RoiHandle.Move => Cursors.SizeAll,
                RoiHandle.Left or RoiHandle.Right => Cursors.SizeWE,
                RoiHandle.Top or RoiHandle.Bottom => Cursors.SizeNS,
                RoiHandle.Left | RoiHandle.Top or RoiHandle.Right | RoiHandle.Bottom => Cursors.SizeNWSE,
                RoiHandle.Right | RoiHandle.Top or RoiHandle.Left | RoiHandle.Bottom => Cursors.SizeNESW,
                _ => Cursors.Cross
            };
        }
        private void OnPreviewDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not ManualFocuserDockableVM vm || !vm.IsSelectingRoi || !vm.CanConfigureLive) return;
            roiDragStart = PreviewPoint(e);
            if (roiDragStart == null) return;
            var roi = vm.PreviewRoiRectangle;
            originalRoi = new Rect(roi.X, roi.Y, roi.Width, roi.Height);
            roiHandle = RoiInteraction.HitTest(ScreenRoi(vm), e.GetPosition(PreviewSurface),6/ImageScale);
            PreviewSurface.CaptureMouse(); UpdateRoiCursor(e); e.Handled = true;
        }
        private bool IsRectangleDrag(Point start, Point end)
        {
            var bounds = DisplayedImageBounds();
            return Math.Abs(start.X - end.X) * bounds.Width * ImageScale > 3 || Math.Abs(start.Y - end.Y) * bounds.Height * ImageScale > 3;
        }
        private void ApplyRoiDrag(ManualFocuserDockableVM vm, Point start, Point end)
        {
            var rectangle = RoiInteraction.Drag(originalRoi,
                new Point(start.X * vm.SelectionSensorWidth, start.Y * vm.SelectionSensorHeight),
                new Point(end.X * vm.SelectionSensorWidth, end.Y * vm.SelectionSensorHeight),
                roiHandle, vm.SelectionSensorWidth, vm.SelectionSensorHeight);
            vm.EditPreviewRoi(rectangle);
        }
        private void OnPreviewMove(object sender, MouseEventArgs e)
        {
            UpdateRoiCursor(e);
            if (roiDragStart is not Point start || e.LeftButton != MouseButtonState.Pressed || DataContext is not ManualFocuserDockableVM vm || !vm.CanConfigureLive) return;
            if (PreviewPoint(e, true) is Point end && IsRectangleDrag(start, end)) ApplyRoiDrag(vm, start, end);
        }
        private void OnPreviewUp(object sender, MouseButtonEventArgs e)
        {
            if (roiDragStart is not Point start || DataContext is not ManualFocuserDockableVM vm) return;
            var end = PreviewPoint(e, true) ?? start;
            if (vm.CanConfigureLive)
            {
                if (IsRectangleDrag(start, end)) ApplyRoiDrag(vm, start, end);
                else if (roiHandle == RoiHandle.Draw) vm.SelectPreviewRoi(end.X, end.Y);
            }
            roiDragStart = null; PreviewSurface.ReleaseMouseCapture(); UpdateRoiCursor(e); e.Handled = true;
        }
        private void OnPreviewCaptureLost(object sender, MouseEventArgs e)
        {
            roiDragStart = null; UpdateRoiCursor(e);
        }
        private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => DrawRoiOutline();
        private void OnRoiPropertyChanged(object sender, PropertyChangedEventArgs e)
        {

            if (e.PropertyName is "PreviewRoiRectangle" or "IsSelectingRoi" or "LiveDisplayBitmap") DrawRoiOutline();
        }
        private void DrawRoiOutline()
        {
            if (RoiOutline == null) return;
            if (DataContext is not ManualFocuserDockableVM vm || !vm.IsSelectingRoi || vm.SelectionSensorWidth < 32 || vm.SelectionSensorHeight < 32)
            {
                RoiOutline.Visibility = Visibility.Collapsed; RoiHandles.Children.Clear(); PreviewSurface.Cursor = Cursors.Arrow; return;
            }
            var bounds = DisplayedImageBounds();
            if (bounds.IsEmpty) { RoiOutline.Visibility = Visibility.Collapsed; RoiHandles.Children.Clear(); PreviewSurface.Cursor = Cursors.Arrow; return; }
            var roi = vm.PreviewRoiRectangle;
            Canvas.SetLeft(RoiOutline, bounds.X + bounds.Width * roi.X / vm.SelectionSensorWidth);
            Canvas.SetTop(RoiOutline, bounds.Y + bounds.Height * roi.Y / vm.SelectionSensorHeight);
            RoiOutline.Width = bounds.Width * roi.Width / vm.SelectionSensorWidth;
            RoiOutline.Height = bounds.Height * roi.Height / vm.SelectionSensorHeight;
            RoiOutline.Visibility = Visibility.Visible;
            RoiOutline.StrokeThickness = 3/ImageScale;
            RoiHandles.Children.Clear();
            var screen = ScreenRoi(vm);
            foreach (double x in new[] { screen.Left, screen.Left + screen.Width / 2, screen.Right })
                foreach (double y in new[] { screen.Top, screen.Top + screen.Height / 2, screen.Bottom })
                {
                    if (x == screen.Left + screen.Width / 2 && y == screen.Top + screen.Height / 2) continue;
                    double size = 9/ImageScale;
                    var grip = new Rectangle { Width = size, Height = size, Fill = RoiOutline.Stroke, Stroke = Brushes.Black, StrokeThickness = 1/ImageScale };
                    Canvas.SetLeft(grip, x - size/2); Canvas.SetTop(grip, y - size/2); RoiHandles.Children.Add(grip);
                }
        }
        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ManualFocuserDockableVM old) PropertyChangedEventManager.RemoveHandler(old, OnRoiPropertyChanged, string.Empty);
            if (e.NewValue is ManualFocuserDockableVM current) PropertyChangedEventManager.AddHandler(current, OnRoiPropertyChanged, string.Empty);
            DrawRoiOutline();
        }
    }
}
