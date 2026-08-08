using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.ViewModels;

namespace QuotaLens.Tests.ViewModels;

[TestClass]
public sealed class ProviderSortOrderCacheTests
{
    [TestMethod]
    public void OrderFor_AfterRebuild_ReturnsCachedOrdersWithoutRecomputingScores()
    {
        var items = new[]
        {
            MakeSortItem("high-value", planValue: 100, resetTier: ProviderPriority.MediumResetTier, resetMinutesUntil: 20),
            MakeSortItem("short-reset", planValue: 20, resetTier: ProviderPriority.ShortResetTier, resetMinutesUntil: 60),
            MakeSortItem("soon-reset", planValue: 10, resetTier: ProviderPriority.LongResetTier, resetMinutesUntil: 5),
        };
        var scoreReads = 0;
        var cache = new ProviderSortOrderCache<SortItem>(
            item => item.Id,
            item =>
            {
                scoreReads++;
                return item.Score;
            });

        cache.Rebuild(items, ProviderSortPriorityOrder.Default);

        Assert.AreEqual(items.Length, scoreReads);
        var readsAfterRebuild = scoreReads;
        CollectionAssert.AreEqual(new[] { "high-value", "short-reset", "soon-reset" }, cache.OrderFor(ProviderSortMode.PlanValue).ToArray());
        CollectionAssert.AreEqual(new[] { "short-reset", "high-value", "soon-reset" }, cache.OrderFor(ProviderSortMode.ResetFrequency).ToArray());
        CollectionAssert.AreEqual(new[] { "soon-reset", "high-value", "short-reset" }, cache.OrderFor(ProviderSortMode.NextReset).ToArray());

        Assert.AreEqual(readsAfterRebuild, scoreReads);
    }

    [TestMethod]
    public void OrderFor_UsesPreviousOrderUntilCacheIsRebuilt()
    {
        var highValue = MutableItem("high-value", planValue: 100, resetTier: ProviderPriority.ShortResetTier, resetMinutesUntil: 30);
        var lowValue = MutableItem("low-value", planValue: 20, resetTier: ProviderPriority.ShortResetTier, resetMinutesUntil: 30);
        var cache = new ProviderSortOrderCache<MutableSortItem>(item => item.Id, item => item.Score);

        cache.Rebuild(new[] { highValue, lowValue }, ProviderSortPriorityOrder.Default);
        highValue.Score = Score(planValue: 10, resetTier: ProviderPriority.ShortResetTier, resetMinutesUntil: 30);
        lowValue.Score = Score(planValue: 200, resetTier: ProviderPriority.ShortResetTier, resetMinutesUntil: 30);

        CollectionAssert.AreEqual(new[] { "high-value", "low-value" }, cache.OrderFor(ProviderSortMode.PlanValue).ToArray());

        cache.Rebuild(new[] { highValue, lowValue }, ProviderSortPriorityOrder.Default);

        CollectionAssert.AreEqual(new[] { "low-value", "high-value" }, cache.OrderFor(ProviderSortMode.PlanValue).ToArray());
    }

    [TestMethod]
    public void OrderFor_WithEmptyDeprioritization_CachesUsableProvidersBeforeEmptyProviders()
    {
        var usable = new SortItem(
            "usable-later",
            new ProviderPriorityScore(
                ProviderPriority.UsableSubscriptionBucket,
                PlanValue: 20,
                Availability: 90,
                IsPayAsYouGo: false,
                ProviderPriority.LongResetTier,
                ResetMinutesUntil: 240));
        var empty = new SortItem(
            "empty-soon",
            new ProviderPriorityScore(
                ProviderPriority.ExhaustedSubscriptionBucket,
                PlanValue: 100,
                Availability: 0,
                IsPayAsYouGo: false,
                ProviderPriority.ShortResetTier,
                ResetMinutesUntil: 1));
        var cache = new ProviderSortOrderCache<SortItem>(item => item.Id, item => item.Score);

        cache.Rebuild(new[] { empty, usable }, ProviderSortPriorityOrder.Default, deprioritizeEmptyProviders: true);

        CollectionAssert.AreEqual(new[] { "usable-later", "empty-soon" }, cache.OrderFor(ProviderSortMode.NextReset).ToArray());
    }

    private static SortItem MakeSortItem(
        string id,
        double planValue,
        int resetTier,
        double resetMinutesUntil) =>
        new(id, Score(planValue, resetTier, resetMinutesUntil));

    private static MutableSortItem MutableItem(
        string id,
        double planValue,
        int resetTier,
        double resetMinutesUntil) =>
        new(id, Score(planValue, resetTier, resetMinutesUntil));

    private static ProviderPriorityScore Score(
        double planValue,
        int resetTier,
        double resetMinutesUntil) =>
        new(
            ProviderPriority.UsableSubscriptionBucket,
            planValue,
            Availability: 50,
            IsPayAsYouGo: false,
            resetTier,
            resetMinutesUntil);

    private sealed record SortItem(string Id, ProviderPriorityScore Score);

    private sealed class MutableSortItem
    {
        public MutableSortItem(string id, ProviderPriorityScore score)
        {
            Id = id;
            Score = score;
        }

        public string Id { get; }
        public ProviderPriorityScore Score { get; set; }
    }
}
