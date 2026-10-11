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

namespace Cwseo.NINA.ManualFocuser.Dockables
{
    /// <summary>
    /// Mouse editing for the shared live-preview ROI.
    /// </summary>
    public partial class ManualFocuserDockableView : UserControl
    {
        public ManualFocuserDockableView()
        {
            InitializeComponent();
            // Only occasional setup scrolls; live controls and the fold header stay visible.
            LayoutRoot.SizeChanged += (_, _) => UpdateSetupHeight();
            EssentialControls.SizeChanged += (_, _) => UpdateSetupHeight();
            Loaded += OnLoaded;
            DataContextChanged += OnDataContextChanged;
        }
        private void UpdateSetupHeight()
        {
            // Reserve space only for a local image that is actually displayed.
            double previewHeight = PreviewWorkspace.Visibility == Visibility.Visible
                ? Math.Min(320, LayoutRoot.ActualHeight * .62) : 0;
            ControlsScrollViewer.MaxHeight = Math.Max(64,
                LayoutRoot.ActualHeight - EssentialControls.ActualHeight - 32 - previewHeight);
        }
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ManualFocuserDockableVM vm) await vm.EnsureFocusTargetsLoadedAsync();
        }
        private Point? roiDragStart;
        private Rect DisplayedImageBounds()
        {
            if (PreviewImage.Source == null || PreviewImage.Source.Width <= 0 || PreviewImage.Source.Height <= 0) return Rect.Empty;
            var margin = PreviewImage.Margin;
            double availableWidth = Math.Max(0, PreviewSurface.ActualWidth - margin.Left - margin.Right);
            double availableHeight = Math.Max(0, PreviewSurface.ActualHeight - margin.Top - margin.Bottom);
            if (availableWidth <= 0 || availableHeight <= 0) return Rect.Empty;
            double scale = Math.Min(availableWidth / PreviewImage.Source.Width, availableHeight / PreviewImage.Source.Height);
            double width = PreviewImage.Source.Width * scale, height = PreviewImage.Source.Height * scale;
            return new Rect(margin.Left + (availableWidth - width) / 2, margin.Top + (availableHeight - height) / 2, width, height);
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
            var handle = roiDragStart != null ? roiHandle : RoiInteraction.HitTest(ScreenRoi(vm), e.GetPosition(PreviewSurface));
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
            roiHandle = RoiInteraction.HitTest(ScreenRoi(vm), e.GetPosition(PreviewSurface));
            PreviewSurface.CaptureMouse(); UpdateRoiCursor(e); e.Handled = true;
        }
        private bool IsRectangleDrag(Point start, Point end)
        {
            var bounds = DisplayedImageBounds();
            return Math.Abs(start.X - end.X) * bounds.Width > 3 || Math.Abs(start.Y - end.Y) * bounds.Height > 3;
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
        private void OnPreviewWorkspaceSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewLayout();
        private void OnMeasurementsChanged(object sender, RoutedEventArgs e) => UpdatePreviewLayout();
        private (bool Stacked, bool Selecting, bool ShowMeasurements, bool External)? previewLayout;
        private void UpdatePreviewLayout()
        {
            if (PreviewCard == null || MeasurementsPanel == null || MeasurementsToggle == null || CompactMetric == null) return;
            bool stacked = PreviewWorkspace.ActualWidth < 600;
            bool selecting = DataContext is ManualFocuserDockableVM vm && vm.IsSelectingRoi;
            bool external = DataContext is ManualFocuserDockableVM output && output.ShowInNinaImage && (!selecting || output.IsEditingRoiInNinaImage);
            bool showMeasurements = !external && MeasurementsToggle.IsChecked == true;
            var layout = (stacked, selecting, showMeasurements, external);
            if (previewLayout == layout) return;
            previewLayout = layout;
            MeasurementsToggle.Visibility = !selecting && !external ? Visibility.Visible : Visibility.Collapsed;
            PreviewCard.Visibility = external ? Visibility.Collapsed : Visibility.Visible;
            PreviewWorkspace.Visibility = external && (!showMeasurements || selecting) ? Visibility.Collapsed : Visibility.Visible;
            PreviewWorkspace.MinHeight = external ? 0 : 160;
            PreviewWorkspace.Height = external ? showMeasurements && !selecting ? 190 : 0 : double.NaN;
            LayoutRoot.RowDefinitions[1].Height = external ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            PreviewWorkspace.ColumnDefinitions[0].Width = new GridLength(stacked ? 1 : 3, GridUnitType.Star);
            PreviewWorkspace.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
            PreviewWorkspace.RowDefinitions[1].Height = new GridLength(!external && stacked && showMeasurements && !selecting ? 150 : 0);
            Grid.SetColumnSpan(PreviewCard, stacked || selecting || !showMeasurements ? 2 : 1);
            Grid.SetRowSpan(PreviewCard, selecting ? 2 : 1);
            PreviewCard.Margin = selecting || !showMeasurements ? new Thickness(0) : stacked ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 8, 0);
            MeasurementsPanel.Visibility = selecting || !showMeasurements ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetRow(MeasurementsPanel, !external && stacked ? 1 : 0);
            Grid.SetColumn(MeasurementsPanel, external || stacked ? 0 : 1);
            Grid.SetColumnSpan(MeasurementsPanel, external || stacked ? 2 : 1);
            UpdateSetupHeight();
        }
        private void OnRoiPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ManualFocuserDockableVM.ShowInNinaImage) or nameof(ManualFocuserDockableVM.IsEditingRoiInNinaImage)) UpdatePreviewLayout();
            if (e.PropertyName == nameof(ManualFocuserDockableVM.IsSelectingRoi))
            {
                // Editing happens on the image; Retake and Done remain in its header.
                if (DataContext is ManualFocuserDockableVM vm && vm.IsSelectingRoi) SetupExpander.IsExpanded = false;
                UpdatePreviewLayout();
            }
            if (e.PropertyName is "PreviewRoiRectangle" or "IsSelectingRoi" or "LiveDisplayImage") DrawRoiOutline();
        }
        private void DrawRoiOutline()
        {
            UpdatePreviewLayout();
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
            RoiHandles.Children.Clear();
            var screen = ScreenRoi(vm);
            // Fixed-size text sits outside the ROI; image margins reserve space at sensor edges.
            var sizeLabel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 2, 4, 2),
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
                foreach (double y in new[] { screen.Top, screen.Top + screen.Height / 2, screen.Bottom })
                {
                    if (x == screen.Left + screen.Width / 2 && y == screen.Top + screen.Height / 2) continue;
                    var grip = new Rectangle { Width = 9, Height = 9, Fill = RoiOutline.Stroke, Stroke = Brushes.Black, StrokeThickness = 1 };
                    Canvas.SetLeft(grip, x - 4.5); Canvas.SetTop(grip, y - 4.5); RoiHandles.Children.Add(grip);
                }
        }
        private async void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ManualFocuserDockableVM old) PropertyChangedEventManager.RemoveHandler(old, OnRoiPropertyChanged, string.Empty);
            if (e.NewValue is ManualFocuserDockableVM current) PropertyChangedEventManager.AddHandler(current, OnRoiPropertyChanged, string.Empty);
            DrawRoiOutline();
            if (IsLoaded && e.NewValue is ManualFocuserDockableVM vm) await vm.EnsureFocusTargetsLoadedAsync();
        }
    }
}
