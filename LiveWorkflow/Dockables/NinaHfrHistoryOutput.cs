using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using NINA.Core.Utility;
using NINA.WPF.Base.Interfaces.ViewModel;
using OxyPlot;
using OxyPlot.Axes;
using Plot = OxyPlot.Wpf.Plot;
using Series = OxyPlot.Wpf.Series;
using Axis = OxyPlot.Wpf.Axis;
using LineSeries = OxyPlot.Wpf.LineSeries;
using LinearAxis = OxyPlot.Wpf.LinearAxis;

namespace Cwseo.NINA.ManualFocuser.Dockables
{
    /// <summary>Reuse the host's existing HFR plot without publishing fake saved images.</summary>
    internal sealed class NinaHfrHistoryOutput : IDisposable
    {
        private readonly ManualFocuserDockableVM vm;
        private readonly DispatcherTimer timer;
        private readonly HashSet<Plot> incompatible = new();
        private Plot plot;
        private IImageHistoryVM history;
        private Series[] originalSeries;
        private Axis[] originalAxes;
        private LineSeries liveSeries;
        private LinearAxis timeAxis, hfrAxis;
        private readonly List<PropertyOverride> overrides = new();
        private bool disposed;

        public NinaHfrHistoryOutput(ManualFocuserDockableVM vm)
        {
            this.vm = vm;
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += OnTick;
        }

        public void Start() { if (disposed) return; OnTick(null, EventArgs.Empty); timer.Start(); }

        private void OnTick(object sender, EventArgs e)
        {
            if (disposed) return;
            if (!vm.CanPublishNinaHfrHistory) { Detach(); return; }
            if (plot != null)
            {
                if (plot.IsVisible && ReferenceEquals(plot.DataContext, history) && plot.Series.Contains(liveSeries)) return;
                Detach();
            }
            if (Application.Current == null) return;
            foreach (Window window in Application.Current.Windows)
                if (window.IsVisible && TryAttach(window)) return;
        }

        internal bool TryAttach(DependencyObject root)
        {
            if (disposed || !vm.CanPublishNinaHfrHistory) return false;
            if (plot != null) return true;
            var candidate = MainImageRoiEditor.Descendants(root).OfType<Plot>()
                .FirstOrDefault(p => p.IsVisible && p.DataContext is IImageHistoryVM && !incompatible.Contains(p));
            if (candidate == null) return false;
            var bottom = candidate.Axes.FirstOrDefault(a => a.Position == AxisPosition.Bottom);
            var left = candidate.Axes.FirstOrDefault(a => a.Position == AxisPosition.Left && a.Visibility == Visibility.Visible);
            if (bottom == null || left == null) return false;
            try
            {
                plot = candidate; history = (IImageHistoryVM)candidate.DataContext;
                originalSeries = plot.Series.ToArray(); originalAxes = plot.Axes.ToArray();
                timeAxis = new LinearAxis
                {
                    Key = "Cwseo.LiveFocus.Time", Position = AxisPosition.Bottom, Title = "Time (s)",
                    Minimum = -120, Maximum = 0, IsPanEnabled = false, IsZoomEnabled = false,
                    IntervalLength = 50, MinimumPadding = 0, MaximumPadding = 0,
                    TextColor = bottom.TextColor, TitleColor = bottom.TextColor, TicklineColor = bottom.TicklineColor,
                    AxislineColor = bottom.AxislineColor
                };
                hfrAxis = new LinearAxis
                {
                    Key = "Cwseo.LiveFocus.Hfr", Position = AxisPosition.Left, Title = "HFR (px)",
                    IsPanEnabled = false, IsZoomEnabled = false, MinimumPadding = .08, MaximumPadding = .08,
                    TextColor = left.TextColor, TitleColor = left.TextColor, TicklineColor = left.TicklineColor,
                    AxislineColor = left.AxislineColor, MajorGridlineStyle = left.MajorGridlineStyle,
                    MajorGridlineColor = left.MajorGridlineColor
                };
                liveSeries = new LineSeries
                {
                    Title = vm.IsAberrationInspector ? "Live Focus HFR (sensor center)" : "Live Focus HFR (ROI)", XAxisKey = timeAxis.Key, YAxisKey = hfrAxis.Key,
                    DataFieldX = "X", DataFieldY = "Y", ItemsSource = vm.LiveGraphPoints,
                    Color = Color.FromRgb(255, 213, 79), MarkerFill = Color.FromRgb(255, 213, 79),
                    StrokeThickness = 1.5, MarkerType = MarkerType.Circle, MarkerSize = 2
                };
                plot.Series.Clear(); plot.Axes.Clear();
                plot.Axes.Add(timeAxis); plot.Axes.Add(hfrAxis); plot.Series.Add(liveSeries);
                // These controls normally clear/export real exposure history. In
                // live mode Clear affects only the visible live measurements;
                // saving capture-history CSV is unavailable until normal mode.
                foreach (var button in MainImageRoiEditor.Descendants(root).OfType<Button>()
                    .Where(b => ReferenceEquals(b.DataContext, history)))
                {
                    string command = BindingOperations.GetBinding(button, Button.CommandProperty)?.Path?.Path;
                    if (command == "PlotClearCommand")
                    {
                        overrides.Add(new PropertyOverride(button, Button.CommandProperty));
                        overrides.Add(new PropertyOverride(button, FrameworkElement.ToolTipProperty));
                        button.SetValue(Button.CommandProperty, vm.ClearLiveGraphCommand);
                        button.SetValue(FrameworkElement.ToolTipProperty, "Clear live HFR measurements. Captured-image history is preserved.");
                    }
                    else if (command == "PlotSaveCommand")
                    {
                        overrides.Add(new PropertyOverride(button, UIElement.IsEnabledProperty));
                        overrides.Add(new PropertyOverride(button, FrameworkElement.ToolTipProperty));
                        button.SetValue(UIElement.IsEnabledProperty, false);
                        button.SetValue(FrameworkElement.ToolTipProperty, "Stop live focus to export normal captured-image history.");
                    }
                }
                plot.InvalidatePlot(true);
                return true;
            }
            catch (Exception e)
            {
                incompatible.Add(candidate); Detach();
                Logger.Error("[LiveFocus] Could not reuse NINA HFR History", e);
                return false;
            }
        }

        public void UpdateFrame()
        {
            if (disposed || plot == null || !vm.CanPublishNinaHfrHistory) return;
            liveSeries.ItemsSource = vm.LiveGraphPoints;
            plot.InvalidatePlot(true);
        }

        private void Detach()
        {
            if (plot == null) return;
            foreach (var change in overrides) change.Restore();
            overrides.Clear();
            if (liveSeries != null) plot.Series.Remove(liveSeries);
            if (timeAxis != null) plot.Axes.Remove(timeAxis);
            if (hfrAxis != null) plot.Axes.Remove(hfrAxis);
            // Preserve unrelated additions made by other extensions during live mode.
            for (int i = 0; i < (originalAxes?.Length ?? 0); i++)
                if (!plot.Axes.Contains(originalAxes[i])) plot.Axes.Insert(Math.Min(i, plot.Axes.Count), originalAxes[i]);
            for (int i = 0; i < (originalSeries?.Length ?? 0); i++)
                if (!plot.Series.Contains(originalSeries[i])) plot.Series.Insert(Math.Min(i, plot.Series.Count), originalSeries[i]);
            plot.InvalidatePlot(true);
            if (liveSeries != null) liveSeries.ItemsSource = null;
            plot = null; history = null; originalSeries = null; originalAxes = null;
            liveSeries = null; timeAxis = null; hfrAxis = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; timer.Stop(); timer.Tick -= OnTick; Detach(); incompatible.Clear();
        }

        private sealed class PropertyOverride
        {
            private readonly DependencyObject target;
            private readonly DependencyProperty property;
            private readonly BindingBase binding;
            private readonly object local;
            public PropertyOverride(DependencyObject target, DependencyProperty property)
            {
                this.target = target; this.property = property;
                binding = BindingOperations.GetBindingBase(target, property); local = target.ReadLocalValue(property);
            }
            public void Restore()
            {
                if (binding != null) BindingOperations.SetBinding(target, property, binding);
                else if (local == DependencyProperty.UnsetValue) target.ClearValue(property);
                else target.SetValue(property, local);
            }
        }
    }
}
