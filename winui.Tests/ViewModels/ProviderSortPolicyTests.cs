using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Helpers;
using QuotaLens.ViewModels;

namespace QuotaLens.Tests.ViewModels;

[TestClass]
public sealed class ProviderSortPolicyTests
{
    [TestMethod]
    public void Order_ByPlanValue_UsesPlanValueAsPrimaryProperty()
    {
        var items = new[]
        {
            Item("low-value-used", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 10),
            Item("high-value-full", ProviderPriority.UsableSubscriptionBucket, planValue: 100, availability: 80),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.PlanValue, x => x.Score);

        Assert.AreEqual("high-value-full", ordered[0].Id);
    }

    [TestMethod]
    public void Order_ByResetFrequency_UsesConfiguredSecondaryPrioritiesAfterFrequency()
    {
        var items = new[]
        {
            Item("short-low-value", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 80, resetTier: ProviderPriority.ShortResetTier, resetMinutesUntil: 240),
            Item("weekly-high-value", ProviderPriority.UsableSubscriptionBucket, planValue: 100, availability: 10, resetTier: ProviderPriority.MediumResetTier, resetMinutesUntil: 5),
        };

        var ordered = ProviderSortPolicy.Order(
            items,
            ProviderSortMode.ResetFrequency,
            x => x.Score,
            new[] { ProviderSortTerm.PlanValue, ProviderSortTerm.ResetFrequency, ProviderSortTerm.NextReset });

        Assert.AreEqual("short-low-value", ordered[0].Id);
    }

    [TestMethod]
    public void Order_ByNextReset_UsesNextResetAsPrimaryProperty()
    {
        var items = new[]
        {
            Item("short-later", ProviderPriority.UsableSubscriptionBucket, planValue: 100, availability: 80, resetTier: ProviderPriority.ShortResetTier, resetMinutesUntil: 240),
            Item("weekly-sooner", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 10, resetTier: ProviderPriority.MediumResetTier, resetMinutesUntil: 5),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.NextReset, x => x.Score);

        Assert.AreEqual("weekly-sooner", ordered[0].Id);
    }

    [TestMethod]
    public void Order_ByPlanValue_DoesNotHideHighValueExhaustedSubscriptions()
    {
        var items = new[]
        {
            Item("error", ProviderPriority.ErrorOrPendingBucket, planValue: 0, availability: 0),
            Item("paygo", ProviderPriority.PayAsYouGoBucket, planValue: -1, availability: 100, payAsYouGo: true),
            Item("exhausted", ProviderPriority.ExhaustedSubscriptionBucket, planValue: 100, availability: 0),
            Item("usable", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 10),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.PlanValue, x => x.Score);

        Assert.AreEqual("exhausted", ordered[0].Id);
        Assert.AreEqual("usable", ordered[1].Id);
        Assert.AreEqual("error", ordered[2].Id);
        Assert.AreEqual("paygo", ordered[3].Id);
    }

    [TestMethod]
    public void Order_WithEmptyDeprioritization_DemotesExhaustedSubscriptionsBeforeSortTerms()
    {
        var items = new[]
        {
            Item(
                "empty-soon",
                ProviderPriority.ExhaustedSubscriptionBucket,
                planValue: 100,
                availability: 0,
                resetTier: ProviderPriority.ShortResetTier,
                resetMinutesUntil: 1),
            Item(
                "usable-later",
                ProviderPriority.UsableSubscriptionBucket,
                planValue: 20,
                availability: 90,
                resetTier: ProviderPriority.LongResetTier,
                resetMinutesUntil: 240),
        };

        var ordered = ProviderSortPolicy.Order(
            items,
            ProviderSortMode.NextReset,
            x => x.Score,
            deprioritizeEmptyProviders: true);

        Assert.AreEqual("usable-later", ordered[0].Id);
        Assert.AreEqual("empty-soon", ordered[1].Id);
    }

    [TestMethod]
    public void Order_WithEmptyDeprioritizationDisabled_KeepsSelectedSortAsPrimary()
    {
        var items = new[]
        {
            Item(
                "empty-soon",
                ProviderPriority.ExhaustedSubscriptionBucket,
                planValue: 100,
                availability: 0,
                resetTier: ProviderPriority.ShortResetTier,
                resetMinutesUntil: 1),
            Item(
                "usable-later",
                ProviderPriority.UsableSubscriptionBucket,
                planValue: 20,
                availability: 90,
                resetTier: ProviderPriority.LongResetTier,
                resetMinutesUntil: 240),
        };

        var ordered = ProviderSortPolicy.Order(
            items,
            ProviderSortMode.NextReset,
            x => x.Score,
            deprioritizeEmptyProviders: false);

        Assert.AreEqual("empty-soon", ordered[0].Id);
        Assert.AreEqual("usable-later", ordered[1].Id);
    }

    [TestMethod]
    public void SortPriorityOrder_Parse_MigratesOldTermsAndRepairsMissingDuplicateTerms()
    {
        var order = ProviderSortPriorityOrder.Parse("reset-time,unknown,value,value,utilization,reset-cycle");

        CollectionAssert.AreEqual(
            new[]
            {
                ProviderSortTerm.NextReset,
                ProviderSortTerm.PlanValue,
                ProviderSortTerm.ResetFrequency,
            },
            order.ToArray());
    }

    [TestMethod]
    public void SortPriorityOrder_DescriptionKeys_HaveLocalizedTooltipText()
    {
        foreach (var term in ProviderSortPriorityOrder.Default)
        {
            var key = ProviderSortPriorityOrder.DescriptionI18nKey(term);
            var text = I18n.T(key);

            Assert.AreNotEqual(key, text);
            Assert.IsTrue(text.Length > 20);
        }
    }

    [TestMethod]
    public void Order_UsesUtilizationAsHiddenFinalTieBreaker()
    {
        var items = new[]
        {
            Item("subscription-full", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 100, resetTier: ProviderPriority.ShortResetTier, resetMinutesUntil: 30),
            Item("subscription-used", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 7, resetTier: ProviderPriority.ShortResetTier, resetMinutesUntil: 30),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.PlanValue, x => x.Score);

        Assert.AreEqual("subscription-used", ordered[0].Id);
        Assert.AreEqual("subscription-full", ordered[1].Id);
    }

    [TestMethod]
    public void Order_LeavesErrorsLast()
    {
        var items = new[]
        {
            Item("error", ProviderPriority.ErrorOrPendingBucket, planValue: 0, availability: 0),
            Item("subscription-used", ProviderPriority.UsableSubscriptionBucket, planValue: 10, availability: 7),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.PlanValue, x => x.Score);

        Assert.AreEqual("subscription-used", ordered[0].Id);
        Assert.AreEqual("error", ordered[1].Id);
    }

    [TestMethod]
    public void Order_ByResetFrequency_UsesConfiguredSecondaryPrioritiesAfterFrequencyTies()
    {
        var items = new[]
        {
            Item(
                "short-window-just-reset",
                ProviderPriority.ExhaustedSubscriptionBucket,
                planValue: 100,
                availability: 0,
                resetTier: ProviderPriority.ShortResetTier,
                resetMinutesUntil: 240),
            Item(
                "short-window-soon",
                ProviderPriority.ExhaustedSubscriptionBucket,
                planValue: 20,
                availability: 0,
                resetTier: ProviderPriority.ShortResetTier,
                resetMinutesUntil: 5),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.ResetFrequency, x => x.Score);

        Assert.AreEqual("short-window-just-reset", ordered[0].Id);
        Assert.AreEqual("short-window-soon", ordered[1].Id);
    }

    [TestMethod]
    public void Order_ByResetFrequency_PrioritizesShortWindowOverMonthlyEvenWhenMonthlyIsSooner()
    {
        var items = new[]
        {
            Item(
                "monthly-soon",
                ProviderPriority.ExhaustedSubscriptionBucket,
                planValue: 100,
                availability: 0,
                resetTier: ProviderPriority.LongResetTier,
                resetMinutesUntil: 1),
            Item(
                "short-window-hours",
                ProviderPriority.ExhaustedSubscriptionBucket,
                planValue: 20,
                availability: 0,
                resetTier: ProviderPriority.ShortResetTier,
                resetMinutesUntil: 240),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.ResetFrequency, x => x.Score);

        Assert.AreEqual("short-window-hours", ordered[0].Id);
        Assert.AreEqual("monthly-soon", ordered[1].Id);
    }

    [TestMethod]
    public void Order_ByResetFrequency_StillUsesPlanValueBeforeFallbackStatus()
    {
        var items = new[]
        {
            Item(
                "error",
                ProviderPriority.ErrorOrPendingBucket,
                planValue: 0,
                availability: 0,
                resetTier: ProviderPriority.ShortResetTier,
                resetMinutesUntil: 1),
            Item(
                "paygo",
                ProviderPriority.PayAsYouGoBucket,
                planValue: -1,
                availability: 0,
                payAsYouGo: true,
                resetTier: ProviderPriority.ShortResetTier,
                resetMinutesUntil: 1),
            Item(
                "subscription",
                ProviderPriority.ExhaustedSubscriptionBucket,
                planValue: 20,
                availability: 0,
                resetTier: ProviderPriority.ShortResetTier,
                resetMinutesUntil: 30),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.ResetFrequency, x => x.Score);

        Assert.AreEqual("subscription", ordered[0].Id);
        Assert.AreEqual("error", ordered[1].Id);
        Assert.AreEqual("paygo", ordered[2].Id);
    }

    private static SortItem Item(
        string id,
        int bucket,
        double planValue,
        double availability,
        bool payAsYouGo = false,
        int resetTier = ProviderPriority.NoResetTier,
        double resetMinutesUntil = double.PositiveInfinity) =>
        new(id, new ProviderPriorityScore(bucket, planValue, availability, payAsYouGo, resetTier, resetMinutesUntil));

    private sealed record SortItem(string Id, ProviderPriorityScore Score);
}
