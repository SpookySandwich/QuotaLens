using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Helpers;
using QuotaLens.ViewModels;

namespace QuotaLens.Tests.ViewModels;

[TestClass]
public sealed class DashboardViewModelTests
{
    [TestMethod]
    public void SetSortMode_WhenTopSortedCardChanges_UpdatesAmbientProviderType()
    {
        // Arrange
        var items = new[]
        {
            Item("claude", ProviderPriority.UsableSubscriptionBucket, planValue: 100, availability: 80, hasWeekly: true, weeklyMinutesUntil: 100),
            Item("antigravity", ProviderPriority.UsableSubscriptionBucket, planValue: 20, availability: 10, hasFiveHour: true, fiveHourMinutesUntil: 30),
        };

        var planValueSorted = ProviderSortPolicy.Order(
                items,
                ProviderSortMode.PlanValue,
                item => item.Score,
                new[] { ProviderSortTerm.PlanValue, ProviderSortTerm.ResetFrequency, ProviderSortTerm.NextReset })
            .Select(item => item.ProviderType);
        var fiveHourSorted = ProviderSortPolicy.Order(
                items,
                ProviderSortMode.FiveHour,
                item => item.Score,
                new[] { ProviderSortTerm.PlanValue, ProviderSortTerm.ResetFrequency, ProviderSortTerm.NextReset })
            .Select(item => item.ProviderType);

        // Act + Assert
        Assert.AreEqual(
            "claude",
            DashboardViewModel.AmbientProviderTypeFor(planValueSorted, heroHasPick: true, heroPickProviderType: "claude"));

        Assert.AreEqual(
            "antigravity",
            DashboardViewModel.AmbientProviderTypeFor(fiveHourSorted, heroHasPick: true, heroPickProviderType: "claude"));
    }

    [TestMethod]
    public void AmbientProviderTypeFor_WithNoSortedCards_FallsBackToHeroPick()
    {
        // Act
        var providerType = DashboardViewModel.AmbientProviderTypeFor(
            Array.Empty<string>(),
            heroHasPick: true,
            heroPickProviderType: "mimo");

        // Assert
        Assert.AreEqual("mimo", providerType);
    }

    [TestMethod]
    public void AmbientProviderColor_MatchesSelectedProviderBrandColor()
    {
        var providerType = DashboardViewModel.AmbientProviderTypeFor(
            new[] { "qoder" },
            heroHasPick: true,
            heroPickProviderType: "claude");

        Assert.AreEqual(Brand.Color("qoder"), Brand.Color(providerType));
    }

    [TestMethod]
    public void UsageTimelineVisibleFor_RequiresTimelineAndMultiColumnProviderGrid()
    {
        Assert.IsFalse(DashboardViewModel.UsageTimelineVisibleFor(hasUsageTimeline: false, isProviderGridMultiColumn: false));
        Assert.IsFalse(DashboardViewModel.UsageTimelineVisibleFor(hasUsageTimeline: true, isProviderGridMultiColumn: false));
        Assert.IsFalse(DashboardViewModel.UsageTimelineVisibleFor(hasUsageTimeline: false, isProviderGridMultiColumn: true));
        Assert.IsTrue(DashboardViewModel.UsageTimelineVisibleFor(hasUsageTimeline: true, isProviderGridMultiColumn: true));
    }

    private static SortItem Item(
        string providerType,
        int bucket,
        double planValue,
        double availability,
        bool hasFiveHour = false,
        double fiveHourMinutesUntil = double.PositiveInfinity,
        bool hasWeekly = false,
        double weeklyMinutesUntil = double.PositiveInfinity) =>
        new(providerType, new ProviderPriorityScore(
            bucket,
            planValue,
            availability,
            IsPayAsYouGo: false,
            HasFiveHour: hasFiveHour,
            FiveHourMinutesUntil: fiveHourMinutesUntil,
            FiveHourAvailability: availability,
            HasWeekly: hasWeekly,
            WeeklyMinutesUntil: weeklyMinutesUntil,
            WeeklyAvailability: availability));

    private sealed record SortItem(string ProviderType, ProviderPriorityScore Score);
}
