using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace QuotaLens.Views;

/// <summary>
/// Lays provider cards out as a normal list until the window has enough room for
/// two comfortable columns, then reflows into a stable masonry-style grid.
/// </summary>
public sealed class AdaptiveProviderCardsPanel : Panel
{
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public static readonly DependencyProperty MinColumnWidthProperty =
        DependencyProperty.Register(
            nameof(MinColumnWidth),
            typeof(double),
            typeof(AdaptiveProviderCardsPanel),
            new PropertyMetadata(AdaptiveProviderCardsLayout.DefaultMinColumnWidth, OnLayoutPropertyChanged));

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public static readonly DependencyProperty RowSpacingProperty =
        DependencyProperty.Register(
            nameof(RowSpacing),
            typeof(double),
            typeof(AdaptiveProviderCardsPanel),
            new PropertyMetadata(AdaptiveProviderCardsLayout.DefaultRowSpacing, OnLayoutPropertyChanged));

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public static readonly DependencyProperty ColumnSpacingProperty =
        DependencyProperty.Register(
            nameof(ColumnSpacing),
            typeof(double),
            typeof(AdaptiveProviderCardsPanel),
            new PropertyMetadata(AdaptiveProviderCardsLayout.DefaultColumnSpacing, OnLayoutPropertyChanged));

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (AdaptiveProviderCardsPanel)d;
        panel.InvalidateMeasure();
        panel.InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = AdaptiveProviderCardsLayout.ResolveAvailableWidth(availableSize.Width);
        var columns = AdaptiveProviderCardsLayout.GetColumnCount(width, MinColumnWidth, ColumnSpacing, Children.Count);
        var columnWidth = AdaptiveProviderCardsLayout.GetColumnWidth(width, columns, ColumnSpacing);
        var desiredHeights = new double[Children.Count];

        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            desiredHeights[index] = child.DesiredSize.Height;
        }

        var placements = AdaptiveProviderCardsLayout.Arrange(width, desiredHeights, MinColumnWidth, ColumnSpacing, RowSpacing);
        return new Size(width, AdaptiveProviderCardsLayout.ContentHeight(placements));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = AdaptiveProviderCardsLayout.ResolveAvailableWidth(finalSize.Width);
        var desiredHeights = new double[Children.Count];

        for (var index = 0; index < Children.Count; index++)
            desiredHeights[index] = Children[index].DesiredSize.Height;

        var placements = AdaptiveProviderCardsLayout.Arrange(width, desiredHeights, MinColumnWidth, ColumnSpacing, RowSpacing);
        for (var index = 0; index < Children.Count; index++)
        {
            var placement = placements[index];
            Children[index].Arrange(new Rect(placement.X, placement.Y, placement.Width, placement.Height));
        }

        return new Size(width, AdaptiveProviderCardsLayout.ContentHeight(placements));
    }
}
