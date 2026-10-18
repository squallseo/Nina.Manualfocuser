using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    public enum FocusLevelHandle { Black, Midtone, White }

    /// <summary>One input-level axis, three keyboard-accessible handles.</summary>
    public partial class FocusLevelsTrack : UserControl {
        public static readonly DependencyProperty BlackPointProperty = Register(nameof(BlackPoint), 0d);
        public static readonly DependencyProperty MidtoneProperty = Register(nameof(Midtone), .5d);
        public static readonly DependencyProperty WhitePointProperty = Register(nameof(WhitePoint), 1d);
        private static DependencyProperty Register(string name, double value) => DependencyProperty.Register(name, typeof(double), typeof(FocusLevelsTrack),
            new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((FocusLevelsTrack)d).UpdateHandles()),
            value => value is double number && double.IsFinite(number) && number >= 0 && number <= 1);
        public double BlackPoint { get => (double)GetValue(BlackPointProperty); set => SetValue(BlackPointProperty, value); }
        public double Midtone { get => (double)GetValue(MidtoneProperty); set => SetValue(MidtoneProperty, value); }
        public double WhitePoint { get => (double)GetValue(WhitePointProperty); set => SetValue(WhitePointProperty, value); }
        private double Travel => Math.Max(1, ActualWidth - 16);
        private double MidtonePosition => BlackPoint + (WhitePoint - BlackPoint) * Midtone;
        public FocusLevelsTrack() { InitializeComponent(); UpdateHandles(); }
        private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHandles();
        private void OnEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => Opacity = IsEnabled ? 1 : .35;
        private void UpdateHandles() {
            if (BlackHandle == null) return;
            Canvas.SetLeft(BlackHandle, BlackPoint * Travel);
            Canvas.SetLeft(MidtoneHandle, MidtonePosition * Travel);
            Canvas.SetLeft(WhiteHandle, WhitePoint * Travel);
            BlackHandle.ToolTip = $"Black {BlackPoint:F3} · drag right to darken shadows";
            MidtoneHandle.ToolTip = $"Midtone {Midtone:F3} · drag left to reveal faint stars";
            WhiteHandle.ToolTip = $"White {WhitePoint:F3} · drag left to clip highlights";
        }
        public void MoveHandle(FocusLevelHandle handle, double position) {
            if (!IsEnabled || !double.IsFinite(position)) return;
            const double gap = 1d / 65535;
            switch (handle) {
                case FocusLevelHandle.Black: SetCurrentValue(BlackPointProperty, Math.Clamp(position, 0, Math.Max(0, WhitePoint - gap))); break;
                case FocusLevelHandle.White: SetCurrentValue(WhitePointProperty, Math.Clamp(position, Math.Min(1, BlackPoint + gap), 1)); break;
                case FocusLevelHandle.Midtone:
                    SetCurrentValue(MidtoneProperty, Math.Clamp((position - BlackPoint) / Math.Max(gap, WhitePoint - BlackPoint), .001, .999)); break;
            }
        }
        private static FocusLevelHandle Kind(Thumb thumb) => Enum.Parse<FocusLevelHandle>((string)thumb.Tag);
        private void OnDragStarted(object sender, DragStartedEventArgs e) {
            var thumb = (Thumb)sender; thumb.Focus(); Panel.SetZIndex(thumb, 1);
            foreach (Thumb other in Handles.Children) if (!ReferenceEquals(other, thumb)) Panel.SetZIndex(other, 0);
        }
        private void OnDragDelta(object sender, DragDeltaEventArgs e) {
            // Use the pointer's absolute track position. A bound value may update
            // the thumb during a drag; accumulating deltas would amplify movement.
            MoveHandle(Kind((Thumb)sender), (Mouse.GetPosition(Surface).X - 8) / Travel);
        }
        private void OnHandleKeyDown(object sender, KeyEventArgs e) {
            var kind = Kind((Thumb)sender);
            double position = kind == FocusLevelHandle.Black ? BlackPoint : kind == FocusLevelHandle.White ? WhitePoint : MidtonePosition;
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .001 : .01;
            if (e.Key == Key.Left || e.Key == Key.Down) position -= step;
            else if (e.Key == Key.Right || e.Key == Key.Up) position += step;
            else if (e.Key == Key.Home) position = kind == FocusLevelHandle.Midtone ? BlackPoint : 0;
            else if (e.Key == Key.End) position = kind == FocusLevelHandle.Midtone ? WhitePoint : 1;
            else return;
            MoveHandle(kind, position); e.Handled = true;
        }
        private void OnTrackClick(object sender, MouseButtonEventArgs e) {
            if (e.Handled || !IsEnabled || e.OriginalSource is FrameworkElement element && element.TemplatedParent is Thumb) return;
            double position = Math.Clamp((e.GetPosition(Surface).X - 8) / Travel, 0, 1);
            var handle = Math.Abs(position - BlackPoint) < Math.Abs(position - MidtonePosition) ? BlackHandle : MidtoneHandle;
            if (Math.Abs(position - WhitePoint) < Math.Abs(position - (ReferenceEquals(handle, BlackHandle) ? BlackPoint : MidtonePosition))) handle = WhiteHandle;
            handle.Focus(); MoveHandle(Kind(handle), position); e.Handled = true;
        }
    }
}
