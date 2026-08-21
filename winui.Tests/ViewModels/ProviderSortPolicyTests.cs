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
    public void Order_ByFiveHour_PrioritizesFiveHourPlansAndSortsByResetTime()
    {
        var items = new[]
        {
            Item("weekly-plan", ProviderPriority.UsableSubscriptionBucket, planValue: 100, availability: 80, hasWeekly: true, weeklyMinutesUntil: 10),
            Item("5h-later", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 50, hasFiveHour: true, fiveHourMinutesUntil: 120),
            Item("5h-sooner", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 50, hasFiveHour: true, fiveHourMinutesUntil: 30),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.FiveHour, x => x.Score);

        Assert.AreEqual("5h-sooner", ordered[0].Id);
        Assert.AreEqual("5h-later", ordered[1].Id);
        Assert.AreEqual("weekly-plan", ordered[2].Id);
    }

    [TestMethod]
    public void Order_ByWeekly_PrioritizesWeeklyPlansAndSortsByResetTime()
    {
        var items = new[]
        {
            Item("5h-plan", ProviderPriority.UsableSubscriptionBucket, planValue: 100, availability: 80, hasFiveHour: true, fiveHourMinutesUntil: 10),
            Item("weekly-later", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 50, hasWeekly: true, weeklyMinutesUntil: 500),
            Item("weekly-sooner", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 50, hasWeekly: true, weeklyMinutesUntil: 100),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.Weekly, x => x.Score);

        Assert.AreEqual("weekly-sooner", ordered[0].Id);
        Assert.AreEqual("weekly-later", ordered[1].Id);
        Assert.AreEqual("5h-plan", ordered[2].Id);
    }

    [TestMethod]
    public void Order_ByMonthly_PrioritizesMonthlyPlansAndSortsByResetTime()
    {
        var items = new[]
        {
            Item("weekly-plan", ProviderPriority.UsableSubscriptionBucket, planValue: 100, availability: 80, hasWeekly: true, weeklyMinutesUntil: 10),
            Item("monthly-later", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 50, hasMonthly: true, monthlyMinutesUntil: 5000),
            Item("monthly-sooner", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 50, hasMonthly: true, monthlyMinutesUntil: 1000),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.Monthly, x => x.Score);

        Assert.AreEqual("monthly-sooner", ordered[0].Id);
        Assert.AreEqual("monthly-later", ordered[1].Id);
        Assert.AreEqual("weekly-plan", ordered[2].Id);
    }

    [TestMethod]
    public void Order_ByPlanValue_PrioritizesHighValueSubscriptionsBeforeBalances()
    {
        var items = new[]
        {
            Item("deepseek", ProviderPriority.PayAsYouGoBucket, planValue: -1, availability: 100, payAsYouGo: true, hasBalance: true, balanceAmount: 3.32),
            Item("grok", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 90),
            Item("claude-max", ProviderPriority.UsableSubscriptionBucket, planValue: 200, availability: 80),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.PlanValue, x => x.Score);

        Assert.AreEqual("claude-max", ordered[0].Id);
        Assert.AreEqual("grok", ordered[1].Id);
        Assert.AreEqual("deepseek", ordered[2].Id);
    }

    [TestMethod]
    public void Order_WithNonMonetaryBalance_TreatsAFundedAccountAsUsable()
    {
        // A DIEM/credits/points balance has no dollar figure, so BalanceAmount is 0.
        // That is a statement about price, not about whether there is anything left:
        // reading it as "empty" would rank a fully funded account below a spent plan.
        var items = new[]
        {
            Item("spent-plan", ProviderPriority.ExhaustedSubscriptionBucket, planValue: 20, availability: 0),
            Item(
                "venice",
                ProviderPriority.PayAsYouGoBucket,
                planValue: -1,
                availability: 100,
                payAsYouGo: true,
                hasBalance: true,
                balanceAmount: 0,
                hasSpendableBalance: true),
        };

        var ordered = ProviderSortPolicy.Order(
            items,
            ProviderSortMode.PlanValue,
            x => x.Score,
            deprioritizeEmptyProviders: true);

        Assert.AreEqual("venice", ordered[0].Id);
    }

    [TestMethod]
    public void Order_ByPlanValue_DoesNotLetCreditBalancesOutrankSubscriptions()
    {
        // A 12,000-credit balance used to rank as 12,000 "dollars" whenever the plan
        // itself was unpriced, taking a top slot from a real paid subscription.
        var jetbrains = new ProviderSnapshot
        {
            Name = "JetBrains AI",
            Primary = new RateWindow { Label = "Credits", UsedPercent = 10, WindowMinutes = 30 * 24 * 60 },
            Balance = new BalanceInfo { Currency = "credits", Total = 12_000 },
        };
        var claude = new ProviderSnapshot
        {
            Name = "Claude Code · Pro",
            Primary = new RateWindow { Label = "5h Pool", UsedPercent = 10, WindowMinutes = 5 * 60 },
        };
        var items = new[]
        {
            new SortItem("jetbrains", ProviderPriority.Score("jetbrains", jetbrains)),
            new SortItem("claude", ProviderPriority.Score("claude", claude)),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.PlanValue, x => x.Score);

        Assert.AreEqual("claude", ordered[0].Id);
        Assert.AreEqual("jetbrains", ordered[1].Id);
    }

    [TestMethod]
    public void Order_ByFiveHour_FallsBackToWeeklyThenMonthly()
    {
        var items = new[]
        {
            Item("monthly", ProviderPriority.UsableSubscriptionBucket, planValue: 50, availability: 50, hasMonthly: true, monthlyMinutesUntil: 1000),
            Item("weekly", ProviderPriority.UsableSubscriptionBucket, planValue: 50, availability: 50, hasWeekly: true, weeklyMinutesUntil: 100),
            Item("5h", ProviderPriority.UsableSubscriptionBucket, planValue: 50, availability: 50, hasFiveHour: true, fiveHourMinutesUntil: 10),
        };

        var ordered = ProviderSortPolicy.Order(items, ProviderSortMode.FiveHour, x => x.Score);

        Assert.AreEqual("5h", ordered[0].Id);
        Assert.AreEqual("weekly", ordered[1].Id);
        Assert.AreEqual("monthly", ordered[2].Id);
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
                hasFiveHour: true,
                fiveHourMinutesUntil: 1),
            Item(
                "usable-later",
                ProviderPriority.UsableSubscriptionBucket,
                planValue: 20,
                availability: 90,
                hasFiveHour: true,
                fiveHourMinutesUntil: 240),
        };

        var ordered = ProviderSortPolicy.Order(
            items,
            ProviderSortMode.FiveHour,
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
                hasFiveHour: true,
                fiveHourMinutesUntil: 1),
            Item(
                "usable-later",
                ProviderPriority.UsableSubscriptionBucket,
                planValue: 20,
                availability: 90,
                hasFiveHour: true,
                fiveHourMinutesUntil: 240),
        };

        var ordered = ProviderSortPolicy.Order(
            items,
            ProviderSortMode.FiveHour,
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
            Item("subscription-full", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 100),
            Item("subscription-used", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 7),
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

    private static SortItem Item(
        string id,
        int bucket,
        double planValue,
        double availability,
        bool payAsYouGo = false,
        int resetTier = ProviderPriority.NoResetTier,
        double resetMinutesUntil = double.PositiveInfinity,
        bool hasFiveHour = false,
        double fiveHourMinutesUntil = double.PositiveInfinity,
        double fiveHourAvailability = 0.0,
        bool hasWeekly = false,
        double weeklyMinutesUntil = double.PositiveInfinity,
        double weeklyAvailability = 0.0,
        bool hasMonthly = false,
        double monthlyMinutesUntil = double.PositiveInfinity,
        double monthlyAvailability = 0.0,
        bool hasBalance = false,
        double balanceAmount = 0.0,
        bool hasSpendableBalance = false) =>
        new(id, new ProviderPriorityScore(
            bucket,
            planValue,
            availability,
            payAsYouGo,
            resetTier,
            resetMinutesUntil,
            hasFiveHour,
            fiveHourMinutesUntil,
            fiveHourAvailability,
            hasWeekly,
            weeklyMinutesUntil,
            weeklyAvailability,
            hasMonthly,
            monthlyMinutesUntil,
            monthlyAvailability,
            hasBalance,
            balanceAmount,
            hasSpendableBalance));

    private sealed record SortItem(string Id, ProviderPriorityScore Score);
}
