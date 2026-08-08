using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Views;

namespace QuotaLens.Tests.Views;

[TestClass]
public sealed class AdaptiveProviderCardsLayoutTests
{
    [TestMethod]
    public void SegmentLabelModeForWidth_UsesFullPercentOrNoLabelByAvailableWidth()
    {
        Assert.AreEqual(UsageCylinder.SegmentLabelMode.None, UsageCylinder.SegmentLabelModeForWidth(28));
        Assert.AreEqual(UsageCylinder.SegmentLabelMode.PercentOnly, UsageCylinder.SegmentLabelModeForWidth(60));
        Assert.AreEqual(UsageCylinder.SegmentLabelMode.Full, UsageCylinder.SegmentLabelModeForWidth(140));
    }

    [TestMethod]
    public void GetColumnCount_WhenWidthCannotFitTwoColumns_ReturnsSingleColumn()
    {
        var columns = AdaptiveProviderCardsLayout.GetColumnCount(
            availableWidth: 860,
            minColumnWidth: 430,
            columnSpacing: 12,
            itemCount: 4);

        Assert.AreEqual(1, columns);
    }

    [TestMethod]
    public void GetColumnCount_WhenWidthFitsTwoColumns_ReturnsTwoColumns()
    {
        var columns = AdaptiveProviderCardsLayout.GetColumnCount(
            availableWidth: 872,
            minColumnWidth: 430,
            columnSpacing: 12,
            itemCount: 4);

        Assert.AreEqual(2, columns);
    }

    [TestMethod]
    public void GetColumnCount_WithSingleItem_KeepsSingleColumn()
    {
        var columns = AdaptiveProviderCardsLayout.GetColumnCount(
            availableWidth: 1120,
            minColumnWidth: 430,
            columnSpacing: 12,
            itemCount: 1);

        Assert.AreEqual(1, columns);
    }

    [TestMethod]
    public void GetColumnCount_WithDefaultCardMetrics_ChangesAtTwoColumnBreakpoint()
    {
        var justTooNarrow = AdaptiveProviderCardsLayout.GetColumnCount(
            availableWidth: 651,
            minColumnWidth: AdaptiveProviderCardsLayout.DefaultMinColumnWidth,
            columnSpacing: AdaptiveProviderCardsLayout.DefaultColumnSpacing,
            itemCount: 4);
        var wideEnough = AdaptiveProviderCardsLayout.GetColumnCount(
            availableWidth: 652,
            minColumnWidth: AdaptiveProviderCardsLayout.DefaultMinColumnWidth,
            columnSpacing: AdaptiveProviderCardsLayout.DefaultColumnSpacing,
            itemCount: 4);

        Assert.AreEqual(1, justTooNarrow);
        Assert.AreEqual(2, wideEnough);
    }

    [TestMethod]
    public void GetColumnWidth_WithTwoColumns_SubtractsSpacing()
    {
        var width = AdaptiveProviderCardsLayout.GetColumnWidth(
            availableWidth: 1120,
            columnCount: 2,
            columnSpacing: 12);

        Assert.AreEqual(554, width);
    }

    [TestMethod]
    public void Arrange_WithUnevenHeights_UsesShortestColumnWaterfall()
    {
        var placements = AdaptiveProviderCardsLayout.Arrange(
            availableWidth: 1012,
            itemHeights: new[] { 120d, 80d, 90d, 70d },
            minColumnWidth: 430,
            columnSpacing: 12,
            rowSpacing: 10);

        Assert.AreEqual(0, placements[0].Column);
        Assert.AreEqual(1, placements[1].Column);
        Assert.AreEqual(1, placements[2].Column);
        Assert.AreEqual(0, placements[3].Column);
        Assert.AreEqual(90, placements[2].Y);
        Assert.AreEqual(130, placements[3].Y);
    }
}
