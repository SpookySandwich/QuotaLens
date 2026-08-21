using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class CodexLbProviderTests
{
    private const string BaseUrl = "http://codex-lb.test";

    [TestMethod]
    public async Task FetchAsync_UsesAccountPairedWindowsForPooledCapacity()
    {
        var provider = ProviderReturning(
            Summary(primaryRemainingPercent: 66.67, secondaryRemainingPercent: 33.33),
            """
            {
              "accounts": [
                {
                  "displayName": "team-a",
                  "planType": "team",
                  "usage": { "primaryRemainingPercent": 100.0, "secondaryRemainingPercent": 0.0 },
                  "capacityCreditsPrimary": 100.0,
                  "capacityCreditsSecondary": 100.0
                },
                {
                  "displayName": "team-b",
                  "planType": "team",
                  "usage": { "primaryRemainingPercent": 100.0, "secondaryRemainingPercent": 0.0 },
                  "capacityCreditsPrimary": 100.0,
                  "capacityCreditsSecondary": 100.0
                },
                {
                  "displayName": "team-c",
                  "planType": "team",
                  "usage": { "primaryRemainingPercent": 0.0, "secondaryRemainingPercent": 100.0 },
                  "capacityCreditsPrimary": 100.0,
                  "capacityCreditsSecondary": 100.0
                }
              ]
            }
            """);

        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        Assert.AreEqual(100.0, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Effective Usage", snapshot.Primary.Label);
        Assert.IsNull(snapshot.Secondary);
        Assert.AreEqual("Effective 5h", snapshot.AdditionalWindows[0].Label);
        Assert.AreEqual(100.0, snapshot.AdditionalWindows[0].UsedPercent, 0.001);
        Assert.AreEqual("Effective Weekly", snapshot.AdditionalWindows[1].Label);
        Assert.AreEqual(200.0 / 3.0, snapshot.AdditionalWindows[1].UsedPercent, 0.001);
        Assert.AreEqual(3, snapshot.Accounts.Count);
        Assert.IsTrue(snapshot.Accounts.All(a => a.UsedPercent is >= 99.999));
        Assert.IsTrue(snapshot.Accounts.All(a => a.PrimaryUsedPercent.HasValue));
        Assert.IsTrue(snapshot.Accounts.All(a => a.SecondaryUsedPercent.HasValue));
    }

    [TestMethod]
    public async Task FetchAsync_DerivesPooledLimitFromUsableAccountCapacity()
    {
        var provider = ProviderReturning(
            Summary(primaryRemainingPercent: 99.0, secondaryRemainingPercent: 21.0),
            """
            {
              "accounts": [
                {
                  "displayName": "team-a",
                  "planType": "team",
                  "usage": { "primaryRemainingPercent": 99.0, "secondaryRemainingPercent": 0.0 },
                  "capacityCreditsPrimary": 225.0,
                  "capacityCreditsSecondary": 7560.0
                },
                {
                  "displayName": "plus-b",
                  "planType": "plus",
                  "usage": { "primaryRemainingPercent": 95.0, "secondaryRemainingPercent": 63.0 },
                  "capacityCreditsPrimary": 225.0,
                  "capacityCreditsSecondary": 7560.0
                },
                {
                  "displayName": "team-c",
                  "planType": "team",
                  "usage": { "primaryRemainingPercent": 99.0, "secondaryRemainingPercent": 0.0 },
                  "capacityCreditsPrimary": 225.0,
                  "capacityCreditsSecondary": 7560.0
                }
              ]
            }
            """);

        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        Assert.AreEqual(79.0, snapshot.Primary.UsedPercent, 0.001);
        Assert.IsNull(snapshot.Secondary);
    }

    [TestMethod]
    public async Task FetchAsync_FallsBackToSummaryWhenAccountsEndpointIsUnavailable()
    {
        var provider = ProviderReturning(
            Summary(primaryRemainingPercent: 66.0, secondaryRemainingPercent: 21.0),
            accountsJson: null);

        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        Assert.AreEqual(79.0, snapshot.Primary.UsedPercent, 0.001);
        Assert.IsNull(snapshot.Secondary);
        Assert.AreEqual(0, snapshot.Accounts.Count);
    }

    [TestMethod]
    public async Task FetchAsync_UsesWeeklyOnlyAccountsWhenPrimaryWindowIsAbsent()
    {
        var weeklyReset = DateTimeOffset.UtcNow.AddDays(4).ToString("O", CultureInfo.InvariantCulture);
        var provider = ProviderReturning(
            WeeklyOnlySummary(62.61538461538461, weeklyReset),
            $$"""
            {
              "accounts": [
                {
                  "displayName": "pro-a",
                  "planType": "pro",
                  "usage": { "primaryRemainingPercent": null, "secondaryRemainingPercent": 67.0 },
                  "resetAtPrimary": null,
                  "resetAtSecondary": "{{weeklyReset}}",
                  "windowMinutesPrimary": null,
                  "windowMinutesSecondary": 10080,
                  "capacityCreditsPrimary": 0.0,
                  "capacityCreditsSecondary": 50400.0
                },
                {
                  "displayName": "team-b",
                  "planType": "team",
                  "usage": { "primaryRemainingPercent": null, "secondaryRemainingPercent": 96.0 },
                  "resetAtPrimary": null,
                  "resetAtSecondary": "{{weeklyReset}}",
                  "windowMinutesPrimary": null,
                  "windowMinutesSecondary": 10080,
                  "capacityCreditsPrimary": 0.0,
                  "capacityCreditsSecondary": 7560.0
                },
                {
                  "displayName": "team-c",
                  "planType": "team",
                  "usage": { "primaryRemainingPercent": null, "secondaryRemainingPercent": 0.0 },
                  "resetAtPrimary": null,
                  "resetAtSecondary": "{{weeklyReset}}",
                  "windowMinutesPrimary": null,
                  "windowMinutesSecondary": 10080,
                  "capacityCreditsPrimary": 0.0,
                  "capacityCreditsSecondary": 7560.0
                }
              ]
            }
            """);

        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        Assert.AreEqual(37.38461538461539, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(weeklyReset, snapshot.Primary.ResetsAt);
        Assert.AreEqual(3, snapshot.Accounts.Count);
        Assert.IsTrue(snapshot.Accounts.All(account => account.PrimaryLabel == "Weekly"));
        Assert.IsTrue(snapshot.Accounts.All(account => account.PrimaryUsedPercent.HasValue));
        Assert.IsTrue(snapshot.Accounts.All(account => account.SecondaryLabel is null));
        Assert.IsTrue(snapshot.Accounts.All(account => account.SecondaryUsedPercent is null));
    }

    [TestMethod]
    public async Task FetchAsync_IdlePrimaryWindowCountsAsFreshFiveHourQuota()
    {
        // codex-lb nulls an elapsed 5h sample while keeping the plan's static
        // capacityCreditsPrimary — the window reset, so the 5h budget is full.
        // The effective 5h pool is then capped by each account's weekly budget.
        var weeklyReset = DateTimeOffset.UtcNow.AddDays(2).ToString("O", CultureInfo.InvariantCulture);
        var provider = ProviderReturning(
            WeeklyOnlySummary(45.33333333333333, weeklyReset),
            $$"""
            {
              "accounts": [
                {
                  "displayName": "plus-a",
                  "planType": "plus",
                  "usage": { "primaryRemainingPercent": null, "secondaryRemainingPercent": 98.0 },
                  "resetAtPrimary": null,
                  "resetAtSecondary": "{{weeklyReset}}",
                  "windowMinutesPrimary": null,
                  "windowMinutesSecondary": 10080,
                  "capacityCreditsPrimary": 225.0,
                  "capacityCreditsSecondary": 7560.0
                },
                {
                  "displayName": "team-b",
                  "planType": "team",
                  "usage": { "primaryRemainingPercent": null, "secondaryRemainingPercent": 24.0 },
                  "resetAtPrimary": null,
                  "resetAtSecondary": "{{weeklyReset}}",
                  "windowMinutesPrimary": null,
                  "windowMinutesSecondary": 10080,
                  "capacityCreditsPrimary": 225.0,
                  "capacityCreditsSecondary": 7560.0
                },
                {
                  "displayName": "team-c",
                  "planType": "team",
                  "usage": { "primaryRemainingPercent": null, "secondaryRemainingPercent": 14.0 },
                  "resetAtPrimary": null,
                  "resetAtSecondary": "{{weeklyReset}}",
                  "windowMinutesPrimary": null,
                  "windowMinutesSecondary": 10080,
                  "capacityCreditsPrimary": 225.0,
                  "capacityCreditsSecondary": 7560.0
                }
              ]
            }
            """);

        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        // Every account reports an idle 5h window (0% used) plus its weekly cap.
        Assert.IsTrue(snapshot.Accounts.All(account => account.PrimaryLabel == "5h"));
        Assert.IsTrue(snapshot.Accounts.All(account => account.PrimaryUsedPercent!.Value == 0.0));
        Assert.IsTrue(snapshot.Accounts.All(account => account.SecondaryLabel == "Weekly"));

        var effective5h = snapshot.AdditionalWindows.Single(window => window.Label == "Effective 5h");
        // Nested cap: min(100, weekly) per account → (98 + 24 + 14) / 3 = 45.33% remaining.
        Assert.AreEqual(54.66666666666667, effective5h.UsedPercent, 0.001);
        Assert.IsNull(effective5h.ResetsAt);
        Assert.AreEqual(300L, effective5h.WindowMinutes);

        var effectiveWeekly = snapshot.AdditionalWindows.Single(window => window.Label == "Effective Weekly");
        Assert.AreEqual(54.66666666666667, effectiveWeekly.UsedPercent, 0.001);
    }

    [TestMethod]
    public async Task FetchAsync_IdlePrimaryWindowKeepsFiveHourCadenceForTimeline()
    {
        // The 5h timeline view only draws providers whose priority score has a
        // 5h pool; an all-idle pool must stay eligible instead of vanishing.
        var weeklyReset = DateTimeOffset.UtcNow.AddDays(2).ToString("O", CultureInfo.InvariantCulture);
        var provider = ProviderReturning(
            WeeklyOnlySummary(45.33333333333333, weeklyReset),
            $$"""
            {
              "accounts": [
                {
                  "displayName": "plus-a",
                  "planType": "plus",
                  "usage": { "primaryRemainingPercent": null, "secondaryRemainingPercent": 98.0 },
                  "resetAtPrimary": null,
                  "resetAtSecondary": "{{weeklyReset}}",
                  "windowMinutesPrimary": null,
                  "windowMinutesSecondary": 10080,
                  "capacityCreditsPrimary": 225.0,
                  "capacityCreditsSecondary": 7560.0
                }
              ]
            }
            """);

        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);
        var score = ProviderPriority.Score("codex-lb", snapshot);

        Assert.IsTrue(score.HasFiveHour);
        Assert.AreEqual(98.0, score.FiveHourAvailability, 0.001);
        Assert.IsTrue(score.HasWeekly);
        Assert.AreEqual(98.0, score.WeeklyAvailability, 0.001);
    }

    [TestMethod]
    public async Task FetchAsync_SummaryFallbackIgnoresZeroCapacityPrimaryPlaceholder()
    {
        var weeklyReset = DateTimeOffset.UtcNow.AddDays(4).ToString("O", CultureInfo.InvariantCulture);
        var provider = ProviderReturning(
            WeeklyOnlySummary(61.5, weeklyReset),
            accountsJson: null);

        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        Assert.AreEqual(38.5, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(weeklyReset, snapshot.Primary.ResetsAt);
        Assert.AreEqual(0, snapshot.Accounts.Count);
    }

    [TestMethod]
    public async Task FetchAsync_UsesMonthlyOnlyAccountWindow()
    {
        var monthlyReset = DateTimeOffset.UtcNow.AddDays(20).ToString("O", CultureInfo.InvariantCulture);
        var provider = ProviderReturning(
            MonthlyOnlySummary(80.0, monthlyReset),
            $$"""
            {
              "accounts": [
                {
                  "displayName": "free-a",
                  "planType": "free",
                  "usage": {
                    "primaryRemainingPercent": null,
                    "secondaryRemainingPercent": null,
                    "monthlyRemainingPercent": 75.0
                  },
                  "resetAtMonthly": "{{monthlyReset}}",
                  "windowMinutesMonthly": 43200,
                  "capacityCreditsMonthly": 1000.0
                }
              ]
            }
            """);

        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        Assert.AreEqual(25.0, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(monthlyReset, snapshot.Primary.ResetsAt);
        Assert.AreEqual("Effective Monthly", snapshot.AdditionalWindows[0].Label);
        Assert.AreEqual(25.0, snapshot.AdditionalWindows[0].UsedPercent, 0.001);
        Assert.AreEqual(1, snapshot.Accounts.Count);
        Assert.AreEqual("Monthly", snapshot.Accounts[0].PrimaryLabel);
        Assert.AreEqual(25.0, snapshot.Accounts[0].PrimaryUsedPercent!.Value, 0.001);
        Assert.IsNull(snapshot.Accounts[0].SecondaryUsedPercent);
    }

    [TestMethod]
    public async Task FetchAsync_UsesComparableWeightsForMixedPairedAndWeeklyOnlyAccounts()
    {
        var provider = ProviderReturning(
            SummaryWithoutWindowMinutes(primaryRemainingPercent: 50.0, secondaryRemainingPercent: 75.0),
            """
            {
              "accounts": [
                {
                  "displayName": "paired-a",
                  "usage": { "primaryRemainingPercent": 50.0, "secondaryRemainingPercent": 50.0 },
                  "capacityCreditsPrimary": 100.0,
                  "capacityCreditsSecondary": 3360.0
                },
                {
                  "displayName": "weekly-b",
                  "usage": { "primaryRemainingPercent": null, "secondaryRemainingPercent": 100.0 },
                  "capacityCreditsPrimary": 0.0,
                  "capacityCreditsSecondary": 3360.0
                }
              ]
            }
            """);

        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        Assert.AreEqual(25.0, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("5h", snapshot.Accounts[0].PrimaryLabel);
        Assert.AreEqual("Weekly", snapshot.Accounts[1].PrimaryLabel);
    }

    [TestMethod]
    public async Task FetchAsync_SkipsResetThatCannotIncreaseEffectiveAvailability()
    {
        var ignoredEmptyWeeklyPrimaryReset = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O", CultureInfo.InvariantCulture);
        var primaryResetWithWeeklyCapacity = DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture);
        var weeklyReset = DateTimeOffset.UtcNow.AddHours(3).ToString("O", CultureInfo.InvariantCulture);
        var provider = ProviderReturning(
            Summary(primaryRemainingPercent: 50.0, secondaryRemainingPercent: 50.0),
            $$"""
            {
              "accounts": [
                {
                  "displayName": "empty-weekly-ignores-primary",
                  "usage": { "primaryRemainingPercent": 0.0, "secondaryRemainingPercent": 0.0 },
                  "resetAtPrimary": "{{ignoredEmptyWeeklyPrimaryReset}}",
                  "resetAtSecondary": "{{weeklyReset}}",
                  "capacityCreditsPrimary": 100.0,
                  "capacityCreditsSecondary": 100.0
                },
                {
                  "displayName": "primary-full-but-weekly-has-capacity",
                  "usage": { "primaryRemainingPercent": 99.0, "secondaryRemainingPercent": 20.0 },
                  "resetAtPrimary": "{{primaryResetWithWeeklyCapacity}}",
                  "resetAtSecondary": "{{weeklyReset}}",
                  "capacityCreditsPrimary": 100.0,
                  "capacityCreditsSecondary": 100.0
                }
              ]
            }
            """);

        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        Assert.AreEqual(weeklyReset, snapshot.Primary.ResetsAt);
        Assert.IsNull(snapshot.Primary.DetailText);
    }

    [TestMethod]
    public async Task FetchAsync_RollsExpiredPrimaryResetForwardWhenWeeklyCapacityRemains()
    {
        var justExpiredPrimaryReset = DateTimeOffset.UtcNow.AddMinutes(-1);
        var weeklyReset = DateTimeOffset.UtcNow.AddDays(4).ToString("O", CultureInfo.InvariantCulture);
        var provider = ProviderReturning(
            Summary(primaryRemainingPercent: 10.0, secondaryRemainingPercent: 70.0),
            $$"""
            {
              "accounts": [
                {
                  "displayName": "weekly-has-capacity",
                  "usage": { "primaryRemainingPercent": 10.0, "secondaryRemainingPercent": 70.0 },
                  "resetAtPrimary": "{{justExpiredPrimaryReset.ToString("O", CultureInfo.InvariantCulture)}}",
                  "resetAtSecondary": "{{weeklyReset}}",
                  "capacityCreditsPrimary": 225.0,
                  "capacityCreditsSecondary": 7560.0
                }
              ]
            }
            """);

        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        Assert.IsNotNull(snapshot.Primary.ResetsAt);
        Assert.AreNotEqual(weeklyReset, snapshot.Primary.ResetsAt);
        Assert.IsTrue(DateTimeOffset.TryParse(
            snapshot.Primary.ResetsAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var nextIncrement));
        Assert.IsTrue(nextIncrement < DateTimeOffset.UtcNow.AddHours(5));
        Assert.IsTrue(nextIncrement > DateTimeOffset.UtcNow.AddHours(4.8));
    }

    [TestMethod]
    public async Task FetchAsync_CurrentSchemaFixture_MapsModelCreditsAndResetMetadata()
    {
        // Arrange
        var accountsJson = ReadFixture("accounts-current-schema.redacted.json");
        var requestedUrls = new List<string>();
        var provider = new CodexLbProvider((url, _) =>
        {
            requestedUrls.Add(url);
            if (url.EndsWith("/api/usage/summary", StringComparison.Ordinal))
                return Task.FromResult(Json(Summary(primaryRemainingPercent: 85.0, secondaryRemainingPercent: 65.0)));
            if (url.EndsWith("/api/accounts", StringComparison.Ordinal))
                return Task.FromResult(Json(accountsJson));
            return Task.FromResult(Response(HttpStatusCode.NotFound));
        });

        // Act
        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        // Assert
        CollectionAssert.AreEqual(
            new[] { $"{BaseUrl}/api/usage/summary", $"{BaseUrl}/api/accounts" },
            requestedUrls);
        Assert.AreEqual("codex-lb", snapshot.Name);
        Assert.HasCount(2, snapshot.Accounts);
        Assert.IsNotNull(snapshot.Balance);
        Assert.AreEqual("credits", snapshot.Balance.Currency);
        Assert.AreEqual(20.0, snapshot.Balance.Total, 0.001);
        Assert.IsTrue(snapshot.AdditionalWindows.Any(window => window.Label == "Effective 5h"));
        Assert.IsTrue(snapshot.AdditionalWindows.Any(window => window.Label == "Effective Weekly"));

        var primaryModelWindow = snapshot.AdditionalWindows.Single(window => window.Label == "GPT-5.3-Codex-Spark · 5h");
        Assert.AreEqual("GPT-5.3-Codex-Spark · 5h", primaryModelWindow.Label);
        Assert.AreEqual(25.5, primaryModelWindow.UsedPercent, 0.001);
        Assert.AreEqual(300L, primaryModelWindow.WindowMinutes);
        Assert.AreEqual("2030-01-01T00:00:00.0000000+00:00", primaryModelWindow.ResetsAt);
        Assert.IsFalse(primaryModelWindow.CountsForAvailability);

        var secondaryModelWindow = snapshot.AdditionalWindows.Single(window => window.Label == "GPT-5.3-Codex-Spark · Weekly");
        Assert.AreEqual("GPT-5.3-Codex-Spark · Weekly", secondaryModelWindow.Label);
        Assert.AreEqual(75.25, secondaryModelWindow.UsedPercent, 0.001);
        Assert.AreEqual(10080L, secondaryModelWindow.WindowMinutes);
        Assert.AreEqual("2030-01-08T00:00:00.0000000+00:00", secondaryModelWindow.ResetsAt);

        var resetCredits = snapshot.AdditionalWindows.Single(window => window.Label == "Reset credits");
        Assert.AreEqual(RateWindowKind.Informational, resetCredits.Kind);
        Assert.AreEqual("Reset credits", resetCredits.Label);
        Assert.AreEqual("3 available", resetCredits.ValueText);
        Assert.AreEqual("2030-01-02T03:04:05.0000000+00:00", resetCredits.ResetsAt);
    }

    [TestMethod]
    public async Task FetchAsync_InactiveAndStaleDuplicateAccounts_DoNotInflatePoolMetadata()
    {
        // Arrange
        var provider = ProviderReturning(
            Summary(primaryRemainingPercent: 75.0, secondaryRemainingPercent: 55.0),
            ReadFixture("accounts-status-and-duplicates.redacted.json"));

        // Act
        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        // Assert
        Assert.AreEqual(45.0, snapshot.Primary.UsedPercent, 0.001);
        Assert.HasCount(2, snapshot.Accounts);
        CollectionAssert.AreEqual(
            new[] { "Fresh redacted account", "Second redacted account" },
            snapshot.Accounts.Select(account => account.Email).ToArray());
        Assert.IsNotNull(snapshot.Balance);
        Assert.AreEqual(15.0, snapshot.Balance.Total, 0.001);

        var modelWindows = snapshot.AdditionalWindows
            .Where(window => window.Kind == RateWindowKind.Quota && window.Label.Contains("Spark", StringComparison.Ordinal))
            .ToArray();
        Assert.HasCount(2, modelWindows);
        CollectionAssert.AreEqual(
            new[]
            {
                "GPT-5.3-Codex-Spark · 5h · Account 1",
                "GPT-5.3-Codex-Spark · 5h · Account 2",
            },
            modelWindows.Select(window => window.Label).ToArray());
        CollectionAssert.AreEqual(
            new[] { 30.0, 40.0 },
            modelWindows.Select(window => window.UsedPercent).ToArray());

        var resetCredits = snapshot.AdditionalWindows.Single(window => window.Label == "Reset credits");
        Assert.AreEqual("3 available", resetCredits.ValueText);
        Assert.AreEqual("2030-01-02T03:04:05.0000000+00:00", resetCredits.ResetsAt);
    }

    [TestMethod]
    public async Task FetchAsync_WithUnlimitedCredits_ShowsUnlimitedWithoutFiniteBalance()
    {
        // Arrange
        var provider = ProviderReturning(
            Summary(primaryRemainingPercent: 80.0, secondaryRemainingPercent: 60.0),
            """
            {
              "accounts": [
                {
                  "email": "redacted@example.invalid",
                  "displayName": "Redacted",
                  "planType": "pro",
                  "usage": { "primaryRemainingPercent": 80.0, "secondaryRemainingPercent": 60.0 },
                  "capacityCreditsPrimary": 1500.0,
                  "capacityCreditsSecondary": 50400.0,
                  "creditsHas": true,
                  "creditsUnlimited": true,
                  "creditsBalance": null,
                  "availableResetCredits": 0
                }
              ]
            }
            """);

        // Act
        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        // Assert
        Assert.IsNull(snapshot.Balance);
        var credits = snapshot.AdditionalWindows.Single(window => window.Label == "Credits");
        Assert.AreEqual(RateWindowKind.Informational, credits.Kind);
        Assert.AreEqual("Unlimited", credits.ValueText);
    }

    [TestMethod]
    public async Task FetchAsync_WithCreditCapabilityButNoBalance_ShowsAvailabilityWithoutInventingMoney()
    {
        // Arrange
        var provider = ProviderReturning(
            Summary(primaryRemainingPercent: 80.0, secondaryRemainingPercent: 60.0),
            """
            {
              "accounts": [
                {
                  "email": "redacted@example.invalid",
                  "displayName": "Redacted",
                  "planType": "plus",
                  "usage": { "primaryRemainingPercent": 80.0, "secondaryRemainingPercent": 60.0 },
                  "capacityCreditsPrimary": 225.0,
                  "capacityCreditsSecondary": 7560.0,
                  "creditsHas": true,
                  "creditsUnlimited": false,
                  "creditsBalance": null
                }
              ]
            }
            """);

        // Act
        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        // Assert
        Assert.IsNull(snapshot.Balance);
        var credits = snapshot.AdditionalWindows.Single(window => window.Label == "Credits");
        Assert.AreEqual(RateWindowKind.Informational, credits.Kind);
        Assert.AreEqual("Available", credits.ValueText);
    }

    private static CodexLbProvider ProviderReturning(string summaryJson, string? accountsJson) =>
        new((url, _) =>
        {
            if (url.EndsWith("/api/usage/summary", StringComparison.Ordinal))
                return Task.FromResult(Json(summaryJson));
            if (url.EndsWith("/api/accounts", StringComparison.Ordinal))
                return Task.FromResult(accountsJson is null ? Response(HttpStatusCode.NotFound) : Json(accountsJson));
            return Task.FromResult(Response(HttpStatusCode.NotFound));
        });

    [TestMethod]
    public async Task FetchAsync_ComputesMeasuredWeeklyTokensFromMetrics()
    {
        var summary = """
        {
          "primaryWindow": { "remainingPercent": 0.0, "capacityCredits": 0.0, "remainingCredits": 0.0, "resetAt": null, "windowMinutes": 300 },
          "secondaryWindow": { "remainingPercent": 20.0, "capacityCredits": 65520.0, "remainingCredits": 13104.0, "resetAt": "2026-08-08T08:24:53Z", "windowMinutes": 10080 },
          "metrics": { "tokensSecondaryWindow": 6939377580, "cachedTokensSecondaryWindow": 6608391680 }
        }
        """;
        var provider = ProviderReturning(summary, accountsJson: null);

        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        // 6,939M tokens consumed at 80% window usage → ~8,674M weekly capacity.
        Assert.IsNotNull(snapshot.MeasuredWeeklyTokensMillions);
        Assert.AreEqual(6939.377580 / 0.80, snapshot.MeasuredWeeklyTokensMillions!.Value, 0.5);
    }

    [TestMethod]
    public async Task FetchAsync_SkipsMeasuredTokensEarlyInFreshWindow()
    {
        var summary = """
        {
          "primaryWindow": { "remainingPercent": 100.0, "capacityCredits": 0.0, "remainingCredits": 0.0, "resetAt": null, "windowMinutes": 300 },
          "secondaryWindow": { "remainingPercent": 98.0, "capacityCredits": 65520.0, "remainingCredits": 64209.0, "resetAt": "2026-08-08T08:24:53Z", "windowMinutes": 10080 },
          "metrics": { "tokensSecondaryWindow": 120000000 }
        }
        """;
        var provider = ProviderReturning(summary, accountsJson: null);

        var snapshot = await provider.FetchAsync("codex-lb", new UrlConfig(), CancellationToken.None);

        // <5% of the window used: the division would amplify noise, so no measurement.
        Assert.IsNull(snapshot.MeasuredWeeklyTokensMillions);
    }

    private static string Summary(double primaryRemainingPercent, double secondaryRemainingPercent)
    {
        var primary = primaryRemainingPercent.ToString(CultureInfo.InvariantCulture);
        var secondary = secondaryRemainingPercent.ToString(CultureInfo.InvariantCulture);

        return $$"""
        {
          "primaryWindow": {
            "remainingPercent": {{primary}},
            "capacityCredits": 300.0,
            "remainingCredits": {{primary}},
            "resetAt": "2026-05-30T12:00:00Z",
            "windowMinutes": 300
          },
          "secondaryWindow": {
            "remainingPercent": {{secondary}},
            "capacityCredits": 2100.0,
            "remainingCredits": {{secondary}},
            "resetAt": "2026-06-01T12:00:00Z",
            "windowMinutes": 10080
          }
        }
        """;
    }

    private static string WeeklyOnlySummary(double weeklyRemainingPercent, string weeklyReset)
    {
        var weekly = weeklyRemainingPercent.ToString(CultureInfo.InvariantCulture);
        var remainingCredits = (65520.0 * weeklyRemainingPercent / 100.0).ToString(CultureInfo.InvariantCulture);
        return $$"""
        {
          "primaryWindow": {
            "remainingPercent": 0.0,
            "capacityCredits": 0.0,
            "remainingCredits": 0.0,
            "resetAt": null,
            "windowMinutes": 300
          },
          "secondaryWindow": {
            "remainingPercent": {{weekly}},
            "capacityCredits": 65520.0,
            "remainingCredits": {{remainingCredits}},
            "resetAt": "{{weeklyReset}}",
            "windowMinutes": 10080
          }
        }
        """;
    }

    private static string SummaryWithoutWindowMinutes(double primaryRemainingPercent, double secondaryRemainingPercent)
    {
        var primary = primaryRemainingPercent.ToString(CultureInfo.InvariantCulture);
        var secondary = secondaryRemainingPercent.ToString(CultureInfo.InvariantCulture);
        var primaryCredits = primaryRemainingPercent.ToString(CultureInfo.InvariantCulture);
        var secondaryCredits = (3360.0 * secondaryRemainingPercent / 100.0).ToString(CultureInfo.InvariantCulture);
        return $$"""
        {
          "primaryWindow": {
            "remainingPercent": {{primary}},
            "capacityCredits": 100.0,
            "remainingCredits": {{primaryCredits}},
            "resetAt": null,
            "windowMinutes": null
          },
          "secondaryWindow": {
            "remainingPercent": {{secondary}},
            "capacityCredits": 3360.0,
            "remainingCredits": {{secondaryCredits}},
            "resetAt": null,
            "windowMinutes": null
          }
        }
        """;
    }

    private static string MonthlyOnlySummary(double monthlyRemainingPercent, string monthlyReset)
    {
        var monthly = monthlyRemainingPercent.ToString(CultureInfo.InvariantCulture);
        var remainingCredits = (1000.0 * monthlyRemainingPercent / 100.0).ToString(CultureInfo.InvariantCulture);
        return $$"""
        {
          "primaryWindow": {
            "remainingPercent": 0.0,
            "capacityCredits": 0.0,
            "remainingCredits": 0.0,
            "resetAt": null,
            "windowMinutes": 300
          },
          "secondaryWindow": null,
          "monthlyWindow": {
            "remainingPercent": {{monthly}},
            "capacityCredits": 1000.0,
            "remainingCredits": {{remainingCredits}},
            "resetAt": "{{monthlyReset}}",
            "windowMinutes": 43200
          }
        }
        """;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Response(HttpStatusCode status) => new(status);

    private static string ReadFixture(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(
                directory.FullName,
                "winui.Tests",
                "Fixtures",
                "codex-lb",
                fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException($"Could not locate redacted codex-lb fixture '{fileName}'.");
    }

    private sealed class UrlConfig : IConfig
    {
        public string Get(string key, string fallback = "") => fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") =>
            key == "codex_lb_url" ? BaseUrl : fallback;

        public bool HasScoped(string instanceId, string key) =>
            key == "codex_lb_url";

        public bool GetBool(string key, bool fallback = false) => fallback;
    }
}
