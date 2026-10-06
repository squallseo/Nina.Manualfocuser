using System.Windows;
using System.Windows.Controls;

namespace Cwseo.NINA.ManualFocuser.Dockables {
    /// <summary>Keep focus first, with the star picker beside it when space permits.</summary>
    public sealed class FocusCardsPanel : Grid {
        private bool sideBySide;

        public FocusCardsPanel() {
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SizeChanged += (_, _) => UpdateLayoutMode();
        }

        private void UpdateLayoutMode() {
            foreach (UIElement child in Children)
                if (child is FrameworkElement card) card.VerticalAlignment = VerticalAlignment.Top;
            bool wide = ActualWidth >= 560;
            if (wide == sideBySide || Children.Count < 2) return;
            sideBySide = wide;
            ColumnDefinitions[0].Width = new GridLength(wide ? 1.1 : 1, GridUnitType.Star);
            ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            SetRow(Children[1], wide ? 0 : 1);
            SetColumn(Children[1], wide ? 1 : 0);
            if (Children[0] is FrameworkElement focus) focus.Margin = new Thickness(0, 0, wide ? 6 : 0, 6);
        }
    }
}
