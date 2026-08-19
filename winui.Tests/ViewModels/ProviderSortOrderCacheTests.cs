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
            MakeSortItem("high-value", planValue: 100, hasWeekly: true, weeklyMinutesUntil: 20),
            MakeSortItem("short-reset", planValue: 20, hasFiveHour: true, fiveHourMinutesUntil: 60),
            MakeSortItem("soon-reset", planValue: 10, hasMonthly: true, monthlyMinutesUntil: 5),
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
        CollectionAssert.AreEqual(new[] { "short-reset", "high-value", "soon-reset" }, cache.OrderFor(ProviderSortMode.FiveHour).ToArray());
        CollectionAssert.AreEqual(new[] { "high-value", "soon-reset", "short-reset" }, cache.OrderFor(ProviderSortMode.Weekly).ToArray());
        CollectionAssert.AreEqual(new[] { "soon-reset", "high-value", "short-reset" }, cache.OrderFor(ProviderSortMode.Monthly).ToArray());

        Assert.AreEqual(readsAfterRebuild, scoreReads);
    }

    [TestMethod]
    public void OrderFor_UsesPreviousOrderUntilCacheIsRebuilt()
    {
        var highValue = MutableItem("high-value", planValue: 100, hasFiveHour: true, fiveHourMinutesUntil: 30);
        var lowValue = MutableItem("low-value", planValue: 20, hasFiveHour: true, fiveHourMinutesUntil: 30);
        var cache = new ProviderSortOrderCache<MutableSortItem>(item => item.Id, item => item.Score);

        cache.Rebuild(new[] { highValue, lowValue }, ProviderSortPriorityOrder.Default);
        highValue.Score = Score(planValue: 10, hasFiveHour: true, fiveHourMinutesUntil: 30);
        lowValue.Score = Score(planValue: 200, hasFiveHour: true, fiveHourMinutesUntil: 30);

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
                HasMonthly: true,
                MonthlyMinutesUntil: 240));
        var empty = new SortItem(
            "empty-soon",
            new ProviderPriorityScore(
                ProviderPriority.ExhaustedSubscriptionBucket,
                PlanValue: 100,
                Availability: 0,
                IsPayAsYouGo: false,
                HasFiveHour: true,
                FiveHourMinutesUntil: 1));
        var cache = new ProviderSortOrderCache<SortItem>(item => item.Id, item => item.Score);

        cache.Rebuild(new[] { empty, usable }, ProviderSortPriorityOrder.Default, deprioritizeEmptyProviders: true);

        CollectionAssert.AreEqual(new[] { "usable-later", "empty-soon" }, cache.OrderFor(ProviderSortMode.FiveHour).ToArray());
    }

    private static SortItem MakeSortItem(
        string id,
        double planValue,
        bool hasFiveHour = false,
        double fiveHourMinutesUntil = double.PositiveInfinity,
        bool hasWeekly = false,
        double weeklyMinutesUntil = double.PositiveInfinity,
        bool hasMonthly = false,
        double monthlyMinutesUntil = double.PositiveInfinity) =>
        new(id, Score(planValue, hasFiveHour, fiveHourMinutesUntil, hasWeekly, weeklyMinutesUntil, hasMonthly, monthlyMinutesUntil));

    private static MutableSortItem MutableItem(
        string id,
        double planValue,
        bool hasFiveHour = false,
        double fiveHourMinutesUntil = double.PositiveInfinity,
        bool hasWeekly = false,
        double weeklyMinutesUntil = double.PositiveInfinity,
        bool hasMonthly = false,
        double monthlyMinutesUntil = double.PositiveInfinity) =>
        new(id, Score(planValue, hasFiveHour, fiveHourMinutesUntil, hasWeekly, weeklyMinutesUntil, hasMonthly, monthlyMinutesUntil));

    private static ProviderPriorityScore Score(
        double planValue,
        bool hasFiveHour = false,
        double fiveHourMinutesUntil = double.PositiveInfinity,
        bool hasWeekly = false,
        double weeklyMinutesUntil = double.PositiveInfinity,
        bool hasMonthly = false,
        double monthlyMinutesUntil = double.PositiveInfinity) =>
        new(
            ProviderPriority.UsableSubscriptionBucket,
            planValue,
            Availability: 50,
            IsPayAsYouGo: false,
            HasFiveHour: hasFiveHour,
            FiveHourMinutesUntil: fiveHourMinutesUntil,
            FiveHourAvailability: 50,
            HasWeekly: hasWeekly,
            WeeklyMinutesUntil: weeklyMinutesUntil,
            WeeklyAvailability: 50,
            HasMonthly: hasMonthly,
            MonthlyMinutesUntil: monthlyMinutesUntil,
            MonthlyAvailability: 50);

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
