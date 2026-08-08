namespace QuotaLens.Views;

internal readonly record struct AdaptiveProviderCardPlacement(
    int Column,
    double X,
    double Y,
    double Width,
    double Height);

internal static class AdaptiveProviderCardsLayout
{
    public const double DefaultMinColumnWidth = 320d;
    public const double DefaultColumnSpacing = 12d;
    public const double DefaultRowSpacing = 10d;

    private const int MaximumColumnCount = 2;

    public static int GetColumnCount(double availableWidth, double minColumnWidth, double columnSpacing, int itemCount)
    {
        if (itemCount <= 0 || !IsUsableDimension(availableWidth))
            return 1;

        var minWidth = Math.Max(1, minColumnWidth);
        var spacing = Math.Max(0, columnSpacing);
        var possibleColumns = (int)Math.Floor((availableWidth + spacing) / (minWidth + spacing));

        return Math.Clamp(Math.Min(possibleColumns, itemCount), 1, MaximumColumnCount);
    }

    public static double GetColumnWidth(double availableWidth, int columnCount, double columnSpacing)
    {
        if (!IsUsableDimension(availableWidth))
            return 0;

        if (columnCount <= 1)
            return availableWidth;

        var spacing = Math.Max(0, columnSpacing);
        return Math.Max(0, (availableWidth - (spacing * (columnCount - 1))) / columnCount);
    }

    public static IReadOnlyList<AdaptiveProviderCardPlacement> Arrange(
        double availableWidth,
        IReadOnlyList<double> itemHeights,
        double minColumnWidth,
        double columnSpacing,
        double rowSpacing)
    {
        var columnCount = GetColumnCount(availableWidth, minColumnWidth, columnSpacing, itemHeights.Count);
        var columnWidth = GetColumnWidth(availableWidth, columnCount, columnSpacing);
        var heights = new double[columnCount];
        var placements = new AdaptiveProviderCardPlacement[itemHeights.Count];
        var spacing = Math.Max(0, columnSpacing);
        var rowGap = Math.Max(0, rowSpacing);

        for (var index = 0; index < itemHeights.Count; index++)
        {
            var column = ShortestColumnIndex(heights);
            if (heights[column] > 0)
                heights[column] += rowGap;

            var height = Math.Max(0, itemHeights[index]);
            placements[index] = new AdaptiveProviderCardPlacement(
                column,
                column * (columnWidth + spacing),
                heights[column],
                columnWidth,
                height);
            heights[column] += height;
        }

        return placements;
    }

    public static double ContentHeight(IReadOnlyList<AdaptiveProviderCardPlacement> placements)
    {
        var height = 0d;
        foreach (var placement in placements)
            height = Math.Max(height, placement.Y + placement.Height);
        return height;
    }

    public static double ResolveAvailableWidth(double width) =>
        IsUsableDimension(width) ? width : 0;

    private static bool IsUsableDimension(double value) =>
        double.IsFinite(value) && value > 0;

    private static int ShortestColumnIndex(IReadOnlyList<double> heights)
    {
        var selected = 0;
        var selectedHeight = heights[0];
        for (var i = 1; i < heights.Count; i++)
        {
            if (heights[i] >= selectedHeight)
                continue;

            selected = i;
            selectedHeight = heights[i];
        }

        return selected;
    }
}
