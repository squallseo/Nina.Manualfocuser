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
using System.ComponentModel;

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
        private Point? roiDragStart;
        private Rect DisplayedImageBounds() {
            if (PreviewImage.Source == null || PreviewImage.Source.Width <= 0 || PreviewImage.Source.Height <= 0) return Rect.Empty;
            var margin = PreviewImage.Margin;
            double availableWidth = Math.Max(0, PreviewSurface.ActualWidth - margin.Left - margin.Right);
            double availableHeight = Math.Max(0, PreviewSurface.ActualHeight - margin.Top - margin.Bottom);
            if (availableWidth <= 0 || availableHeight <= 0) return Rect.Empty;
            double scale = Math.Min(availableWidth / PreviewImage.Source.Width, availableHeight / PreviewImage.Source.Height);
            double width = PreviewImage.Source.Width * scale, height = PreviewImage.Source.Height * scale;
            return new Rect(margin.Left + (availableWidth - width) / 2, margin.Top + (availableHeight - height) / 2, width, height);
        }
        private Point? PreviewPoint(MouseEventArgs e, bool clamp = false) {
            var bounds = DisplayedImageBounds();
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;
            var p = e.GetPosition(PreviewSurface);
            if (!clamp && !bounds.Contains(p)) return null;
            return new Point(Math.Clamp((p.X - bounds.X) / bounds.Width, 0, 1), Math.Clamp((p.Y - bounds.Y) / bounds.Height, 0, 1));
        }
        private RoiHandle roiHandle;
        private Rect originalRoi;
        private Rect ScreenRoi(ManualFocuserDockableVM vm) {
            var bounds = DisplayedImageBounds();
            var roi = vm.PreviewRoiRectangle;
            return new Rect(bounds.X + bounds.Width * roi.X / vm.SelectionSensorWidth,
                bounds.Y + bounds.Height * roi.Y / vm.SelectionSensorHeight,
                bounds.Width * roi.Width / vm.SelectionSensorWidth, bounds.Height * roi.Height / vm.SelectionSensorHeight);
        }
        private void UpdateRoiCursor(MouseEventArgs e) {
            if (DataContext is not ManualFocuserDockableVM vm || !vm.IsSelectingRoi || !vm.CanConfigureLive || PreviewPoint(e) == null) {
                PreviewSurface.Cursor = Cursors.Arrow; return;
            }
            var handle = roiDragStart != null ? roiHandle : RoiInteraction.HitTest(ScreenRoi(vm), e.GetPosition(PreviewSurface));
            PreviewSurface.Cursor = handle switch {
                RoiHandle.Move => Cursors.SizeAll,
                RoiHandle.Left or RoiHandle.Right => Cursors.SizeWE,
                RoiHandle.Top or RoiHandle.Bottom => Cursors.SizeNS,
                RoiHandle.Left | RoiHandle.Top or RoiHandle.Right | RoiHandle.Bottom => Cursors.SizeNWSE,
                RoiHandle.Right | RoiHandle.Top or RoiHandle.Left | RoiHandle.Bottom => Cursors.SizeNESW,
                _ => Cursors.Cross
            };
        }
        private void OnPreviewDown(object sender, MouseButtonEventArgs e) {
            if (DataContext is not ManualFocuserDockableVM vm || !vm.IsSelectingRoi || !vm.CanConfigureLive) return;
            roiDragStart = PreviewPoint(e);
            if (roiDragStart == null) return;
            var roi = vm.PreviewRoiRectangle;
            originalRoi = new Rect(roi.X, roi.Y, roi.Width, roi.Height);
            roiHandle = RoiInteraction.HitTest(ScreenRoi(vm), e.GetPosition(PreviewSurface));
            PreviewSurface.CaptureMouse(); UpdateRoiCursor(e); e.Handled = true;
        }
        private bool IsRectangleDrag(Point start, Point end) {
            var bounds = DisplayedImageBounds();
            return Math.Abs(start.X - end.X) * bounds.Width > 3 || Math.Abs(start.Y - end.Y) * bounds.Height > 3;
        }
        private void ApplyRoiDrag(ManualFocuserDockableVM vm, Point start, Point end) {
            var rectangle = RoiInteraction.Drag(originalRoi,
                new Point(start.X * vm.SelectionSensorWidth, start.Y * vm.SelectionSensorHeight),
                new Point(end.X * vm.SelectionSensorWidth, end.Y * vm.SelectionSensorHeight),
                roiHandle, vm.SelectionSensorWidth, vm.SelectionSensorHeight);
            vm.EditPreviewRoi(rectangle);
        }
        private void OnPreviewMove(object sender, MouseEventArgs e) {
            UpdateRoiCursor(e);
            if (roiDragStart is not Point start || e.LeftButton != MouseButtonState.Pressed || DataContext is not ManualFocuserDockableVM vm || !vm.CanConfigureLive) return;
            if (PreviewPoint(e, true) is Point end && IsRectangleDrag(start, end)) ApplyRoiDrag(vm, start, end);
        }
        private void OnPreviewUp(object sender, MouseButtonEventArgs e) {
            if (roiDragStart is not Point start || DataContext is not ManualFocuserDockableVM vm) return;
            var end = PreviewPoint(e, true) ?? start;
            if (vm.CanConfigureLive) {
                if (IsRectangleDrag(start, end)) ApplyRoiDrag(vm, start, end);
                else if (roiHandle == RoiHandle.Draw) vm.SelectPreviewRoi(end.X, end.Y);
            }
            roiDragStart = null; PreviewSurface.ReleaseMouseCapture(); UpdateRoiCursor(e); e.Handled = true;
        }
        private void OnPreviewCaptureLost(object sender, MouseEventArgs e) {
            roiDragStart = null; UpdateRoiCursor(e);
        }
        private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => DrawRoiOutline();
        private void OnRoiPropertyChanged(object sender, PropertyChangedEventArgs e) {
            if (e.PropertyName is "PreviewRoiRectangle" or "IsSelectingRoi" or "LiveDisplayImage") DrawRoiOutline();
        }
        private void DrawRoiOutline() {
            if (RoiOutline == null) return;
            if (DataContext is not ManualFocuserDockableVM vm || !vm.IsSelectingRoi || vm.SelectionSensorWidth < 32 || vm.SelectionSensorHeight < 32) {
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
            RoiHandles.Children.Clear();
            var screen = ScreenRoi(vm);
            // Fixed-size text sits outside the ROI; image margins reserve space at sensor edges.
            var sizeLabel = new Border {
                Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
                CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 2, 4, 2),
                Child = new TextBlock { Text = $"{roi.Width} × {roi.Height} px", Foreground = RoiOutline.Stroke, FontSize = 13 }
            };
            sizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double labelX = Math.Clamp(screen.Left, 0, Math.Max(0, PreviewSurface.ActualWidth - sizeLabel.DesiredSize.Width));
            double labelY = screen.Top - sizeLabel.DesiredSize.Height - 6;
            if (labelY < 0) labelY = screen.Bottom + 6;
            labelY = Math.Clamp(labelY, 0, Math.Max(0, PreviewSurface.ActualHeight - sizeLabel.DesiredSize.Height));
            Canvas.SetLeft(sizeLabel, labelX); Canvas.SetTop(sizeLabel, labelY);
            RoiHandles.Children.Add(sizeLabel);
            foreach (double x in new[] { screen.Left, screen.Left + screen.Width / 2, screen.Right })
                foreach (double y in new[] { screen.Top, screen.Top + screen.Height / 2, screen.Bottom }) {
                    if (x == screen.Left + screen.Width / 2 && y == screen.Top + screen.Height / 2) continue;
                    var grip = new Rectangle { Width = 9, Height = 9, Fill = RoiOutline.Stroke, Stroke = Brushes.Black, StrokeThickness = 1 };
                    Canvas.SetLeft(grip, x - 4.5); Canvas.SetTop(grip, y - 4.5); RoiHandles.Children.Add(grip);
                }
        }
        private async void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) {
            if (e.OldValue is ManualFocuserDockableVM old) PropertyChangedEventManager.RemoveHandler(old, OnRoiPropertyChanged, string.Empty);
            if (e.NewValue is ManualFocuserDockableVM current) PropertyChangedEventManager.AddHandler(current, OnRoiPropertyChanged, string.Empty);
            DrawRoiOutline();
            if (IsLoaded && e.NewValue is ManualFocuserDockableVM vm) await vm.EnsureFocusTargetsLoadedAsync();
        }
    }
}
