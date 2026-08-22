using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.ViewModels;

namespace QuotaLens.Tests.ViewModels;

[TestClass]
public sealed class HeroViewModelTests
{
    [TestMethod]
    public void PublicProperties_DoNotExposeAggregateCredits()
    {
        // Arrange
        var type = typeof(HeroViewModel);

        // Act
        var creditsLabel = type.GetProperty("CreditsLabel");
        var creditsValue = type.GetProperty("CreditsValue");

        // Assert
        Assert.IsNull(creditsLabel);
        Assert.IsNull(creditsValue);
    }

    [TestMethod]
    public void BuildPickDetail_WithOfficialPlan_ShowsSourceBackedMonthlyPrice()
    {
        var snapshot = Snapshot("MiMo · Standard", usedPercent: 20, resetHours: 48, windowMinutes: 30 * 24 * 60);
        var config = new FakeConfig(new[] { new ProviderInstance("mimo", "mimo", "MiMo") });
        var score = ProviderPriority.Score("mimo", snapshot, config);

        var detail = HeroViewModel.BuildPickDetail("mimo", snapshot, score, config);

        Assert.AreEqual("$16/mo · 80% available", detail);
    }

    [TestMethod]
    public void BuildPickDetail_WithLegacyUnverifiedEstimate_OmitsMonthlyPriceClaim()
    {
        var snapshot = Snapshot("BayesDL · Token Standard 标准包", usedPercent: 20, resetHours: 48, windowMinutes: 30 * 24 * 60);
        var config = new FakeConfig(new[] { new ProviderInstance("bayesdl", "bayesdl", "BayesDL") });
        var score = ProviderPriority.Score("bayesdl", snapshot, config);

        var detail = HeroViewModel.BuildPickDetail("bayesdl", snapshot, score, config);

        Assert.AreEqual("80% available", detail);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WhenSensitiveInfoHidden_MasksProviderNames()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("gemini", "gemini", "Gemini") });
        var snapshots = new[]
        {
            ("gemini", Snapshot("Gemini · user@example.com", usedPercent: 20, resetHours: 4, windowMinutes: 24 * 60)),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, hideSensitiveInfo: true);

        Assert.IsTrue(segments.Any());
        Assert.AreEqual("Gemini", segments[0].Label);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_GroupsBracketsByResetCadenceLeftToRight()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("deepseek", "deepseek", "DeepSeek"),
            new ProviderInstance("mimo", "mimo", "MiMo"),
            new ProviderInstance("grok", "grok", "Grok"),
            new ProviderInstance("claude", "claude", "Claude"),
        });
        var snapshots = new[]
        {
            ("deepseek", Balance("DeepSeek", 40)),
            ("mimo", Snapshot("MiMo · Standard", usedPercent: 20, resetHours: 300, windowMinutes: 30 * 24 * 60)),
            ("grok", Snapshot("Grok · SuperGrok", usedPercent: 20, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("claude", FiveHourPlan("Claude Code · Max", fiveHourUsed: 10, weeklyUsed: 20)),
        };

        var groups = HeroViewModel.BuildUsageTimelineSegments(config, snapshots)
            .Select(segment => segment.Group)
            .ToList();

        CollectionAssert.AreEqual(
            new EffectiveUsageGroup?[]
            {
                EffectiveUsageGroup.FiveHour,
                EffectiveUsageGroup.Weekly,
                EffectiveUsageGroup.Monthly,
                EffectiveUsageGroup.Api,
            },
            groups);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_LabelsEachBracketWithItsResetFrequency()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("claude", "claude", "Claude"),
            new ProviderInstance("grok", "grok", "Grok"),
            new ProviderInstance("mimo", "mimo", "MiMo"),
            new ProviderInstance("deepseek", "deepseek", "DeepSeek"),
        });
        var snapshots = new[]
        {
            ("claude", FiveHourPlan("Claude Code · Max", fiveHourUsed: 10, weeklyUsed: 20)),
            ("grok", Snapshot("Grok · SuperGrok", usedPercent: 20, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("mimo", Snapshot("MiMo · Standard", usedPercent: 20, resetHours: 300, windowMinutes: 30 * 24 * 60)),
            ("deepseek", Balance("DeepSeek", 40)),
        };

        var brackets = HeroViewModel.BuildUsageTimelineSegments(config, snapshots)
            .Select(segment => segment.ResetFrequencyText)
            .ToList();

        CollectionAssert.AreEqual(
            new[]
            {
                "resets every 5 hours",
                "resets every week",
                "resets every month",
                "API balance",
            },
            brackets);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_IsIdenticalWhicheverCadenceTheCardsAreSortedBy()
    {
        // The chart answers "what can I spend in the next five hours?". Nothing the
        // user does to the card order below it may change that answer.
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("claude", "claude", "Claude"),
            new ProviderInstance("grok", "grok", "Grok"),
        });
        var snapshots = new[]
        {
            ("claude", FiveHourPlan("Claude Code · Max", fiveHourUsed: 40, weeklyUsed: 20)),
            ("grok", Snapshot("Grok · SuperGrok", usedPercent: 20, resetHours: 100, windowMinutes: 7 * 24 * 60)),
        };

        var expected = HeroViewModel.BuildUsageTimelineSegments(config, snapshots);

        // BuildUsageTimelineSegments no longer takes a sort mode at all; the guard
        // here is that repeated builds off the same data agree bar for bar.
        var again = HeroViewModel.BuildUsageTimelineSegments(config, snapshots);

        CollectionAssert.AreEqual(
            expected.Select(segment => segment.InstanceId).ToList(),
            again.Select(segment => segment.InstanceId).ToList());
        CollectionAssert.AreEqual(
            expected.Select(segment => segment.AvailableText).ToList(),
            again.Select(segment => segment.AvailableText).ToList());
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WeeklyPlanContributesItsWholeRemainingPool()
    {
        // Nothing stops a user burning a whole weekly allowance in one afternoon,
        // so a weekly plan's five-hour capacity is everything it has left.
        var config = new FakeConfig(new[] { new ProviderInstance("grok", "grok", "Grok") });
        var snapshots = new[]
        {
            ("grok", Snapshot("Grok · SuperGrok", usedPercent: 25, resetHours: 100, windowMinutes: 7 * 24 * 60)),
        };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, snapshots).Single();

        // SuperGrok is estimated at 14M tokens/week; 75% of that is still spendable.
        Assert.AreEqual(EffectiveUsageGroup.Weekly, segment.Group);
        Assert.AreEqual(10.5, segment.EffectiveTokensMillions, 0.01);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_FiveHourPlanContributesOnlyTheCurrentWindow()
    {
        // A plan that refills every five hours cannot hand over next week's slices
        // today, so only the current window counts — one 33.6th of the weekly pool.
        var config = new FakeConfig(new[] { new ProviderInstance("claude", "claude", "Claude") });
        var snapshots = new[]
        {
            ("claude", FiveHourPlan("Claude Code · Max", fiveHourUsed: 50, weeklyUsed: 0)),
        };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, snapshots).Single();

        // Max is estimated at 350M/week -> 10.42M per 5h window, half of it spent.
        Assert.AreEqual(EffectiveUsageGroup.FiveHour, segment.Group);
        Assert.AreEqual(350.0 * 5 / 168 * 0.5, segment.EffectiveTokensMillions, 0.01);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_MonthlyPlanScalesItsPoolUpFromTheWeeklyEstimate()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("mimo", "mimo", "MiMo") });
        var snapshots = new[]
        {
            ("mimo", Snapshot("MiMo · Standard", usedPercent: 25, resetHours: 300, windowMinutes: 30 * 24 * 60)),
        };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, snapshots).Single();

        // Standard is estimated at 53M/week -> 30/7 of that per month, 75% left.
        Assert.AreEqual(EffectiveUsageGroup.Monthly, segment.Group);
        Assert.AreEqual(53.0 * 30 / 7 * 0.75, segment.EffectiveTokensMillions, 0.01);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_FiveHourWindowNeverExceedsTheWeeklyPoolItSitsIn()
    {
        // A freshly reset five-hour window inside an almost-spent weekly pool cannot
        // deliver more than the weekly pool has left.
        var config = new FakeConfig(new[] { new ProviderInstance("claude", "claude", "Claude") });
        var snapshots = new[]
        {
            ("claude", FiveHourPlan("Claude Code · Max", fiveHourUsed: 0, weeklyUsed: 99.5)),
        };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, snapshots).Single();

        // 0.5% of a 350M weekly pool is 1.75M, well under a full 10.42M 5h window.
        Assert.AreEqual(EffectiveUsageGroup.FiveHour, segment.Group);
        Assert.AreEqual(1.75, segment.EffectiveTokensMillions, 0.01);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_DoesNotClampTheFiveHourWindowToTheWeeklyPercentage()
    {
        // Ranking clamps a 5h percentage to its weekly pool's percentage, which is
        // meaningless in tokens: a 90%-full 10M window is not "54% full" just
        // because the 350M weekly pool behind it is. The chart must read 90%.
        var config = new FakeConfig(new[] { new ProviderInstance("claude", "claude", "Claude") });
        var snapshots = new[]
        {
            ("claude", FiveHourPlan("Claude Code · Max", fiveHourUsed: 10, weeklyUsed: 46)),
        };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, snapshots).Single();

        Assert.AreEqual(90, segment.AvailablePercent, 0.001);
        Assert.AreEqual(350.0 * 5 / 168 * 0.9, segment.EffectiveTokensMillions, 0.01);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithUncountedFiveHourRateLimit_StillBracketsAsFiveHour()
    {
        // Kimi reports a "5h Rate Limit" that does not count toward its headline
        // availability, but the limit is real and decides which bracket it belongs in.
        var config = new FakeConfig(new[] { new ProviderInstance("kimi", "kimi", "Kimi") });
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "kimi",
            Name = "Kimi · Allegro",
            Primary = new RateWindow
            {
                Label = "Monthly",
                UsedPercent = 50,
                ResetsAt = DateTimeOffset.UtcNow.AddDays(10).ToString("O"),
                WindowMinutes = 30 * 24 * 60,
                CountsForAvailability = true,
            },
            Secondary = new RateWindow
            {
                Label = "Weekly",
                UsedPercent = 20,
                ResetsAt = DateTimeOffset.UtcNow.AddDays(1).ToString("O"),
                WindowMinutes = 7 * 24 * 60,
            },
            Tertiary = new RateWindow
            {
                Label = "5h Rate Limit",
                UsedPercent = 0,
                ResetsAt = DateTimeOffset.UtcNow.AddHours(4).ToString("O"),
                WindowMinutes = 5 * 60,
            },
        };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, new[] { ("kimi", snapshot) }).Single();

        Assert.AreEqual(EffectiveUsageGroup.FiveHour, segment.Group);
        // Allegro is estimated at 900M/week -> a full 5h window is 900 * 5/168.
        Assert.AreEqual(900.0 * 5 / 168, segment.EffectiveTokensMillions, 0.01);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithApiBalance_ConvertsItToTokensOnTheSameAxis()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("deepseek", "deepseek", "DeepSeek") });
        var snapshots = new[] { ("deepseek", Balance("DeepSeek", 25)) };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, snapshots).Single();

        Assert.AreEqual(EffectiveUsageGroup.Api, segment.Group);
        Assert.AreEqual(25 / 0.25, segment.EffectiveTokensMillions, 0.01);
        Assert.AreEqual("100M", segment.AvailableText);
        // Pay-as-you-go is no longer greyed out: it holds real, comparable capacity.
        Assert.IsFalse(segment.IsGrayedOut);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithConfiguredApiTokenPrice_UsesTheOverride()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("deepseek", "deepseek", "DeepSeek") });
        config.Set(ApiTokenRules.ConfigKey("deepseek"), "1");
        var snapshots = new[] { ("deepseek", Balance("DeepSeek", 25)) };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, snapshots).Single();

        Assert.AreEqual(25, segment.EffectiveTokensMillions, 0.01);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithNonTokenMeteredApi_DrawsNoBar()
    {
        // Deepgram bills per audio minute. Inventing a token figure for it would put
        // a number on the chart that nothing in the product could ever justify.
        var config = new FakeConfig(new[] { new ProviderInstance("deepgram", "deepgram", "Deepgram") });
        var snapshots = new[] { ("deepgram", Balance("Deepgram", 25)) };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots);

        Assert.AreEqual(1, segments.Count);
        Assert.AreEqual("", segments[0].InstanceId);
        Assert.IsTrue(segments[0].IsGrayedOut);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_SizesBarsByTokensAndOrdersBiggestFirstInsideABracket()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("grok", "grok", "Grok"),
            new ProviderInstance("copilot", "copilot", "Copilot"),
        });
        var snapshots = new[]
        {
            // 14M/week estimate, 50% left -> 7M.
            ("grok", Snapshot("Grok · SuperGrok", usedPercent: 50, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            // 3.5M/week estimate, 100% left -> 3.5M.
            ("copilot", Snapshot("Copilot · Pro", usedPercent: 0, resetHours: 100, windowMinutes: 7 * 24 * 60)),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots);

        Assert.AreEqual("grok", segments[0].InstanceId);
        Assert.AreEqual("copilot", segments[1].InstanceId);
        Assert.AreEqual(7, segments[0].Weight, 0.01);
        Assert.AreEqual(3.5, segments[1].Weight, 0.01);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_ShowsTokensAndTheResetOfThePoolItMeasured()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("claude", "claude", "Claude") });
        var snapshots = new[]
        {
            ("claude", FiveHourPlan("Claude Code · Max", fiveHourUsed: 0, weeklyUsed: 0, fiveHourResetHours: 3)),
        };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, snapshots).Single();

        // The five-hour reset, not the weekly one: the bar was sized from the 5h pool.
        StringAssert.StartsWith(segment.ResetText, "~2h");
        StringAssert.StartsWith(segment.AvailableText, "10.4M · 2h");
        // A bar too narrow for its provider name keeps the number, not the reset.
        Assert.AreEqual("10.4M", segment.CompactAvailableText);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_IgnoresASubQuotaWhoseResetHasAlreadyPassed()
    {
        // codex-lb keeps reporting per-model weekly windows for days after they
        // refill. Letting one win the tie stamps "now" on a bar that does not come
        // back for another four days.
        var config = new FakeConfig(new[] { new ProviderInstance("codex-lb", "codex-lb", "codex-lb") });
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "codex-lb",
            Name = "codex-lb",
            Primary = new RateWindow
            {
                Label = "Effective Usage",
                UsedPercent = 50,
                ResetsAt = DateTimeOffset.UtcNow.AddDays(4).ToString("O"),
            },
            AdditionalWindows =
            {
                new RateWindow
                {
                    Label = "Effective Weekly",
                    UsedPercent = 50,
                    ResetsAt = DateTimeOffset.UtcNow.AddDays(4).ToString("O"),
                    WindowMinutes = 7 * 24 * 60,
                },
                new RateWindow
                {
                    Label = "Spark · Weekly",
                    UsedPercent = 0,
                    ResetsAt = DateTimeOffset.UtcNow.AddDays(-3).ToString("O"),
                    WindowMinutes = 7 * 24 * 60,
                },
            },
        };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, new[] { ("codex-lb", snapshot) }).Single();

        Assert.AreEqual(EffectiveUsageGroup.Weekly, segment.Group);
        Assert.AreNotEqual("now", segment.ResetText);
        Assert.IsTrue(
            segment.ResetText?.Contains('d', StringComparison.Ordinal) == true,
            $"Expected a multi-day reset, got '{segment.ResetText}'.");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithPooledAccounts_SumsTheirAllowances()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("codex-lb", "codex-lb", "codex-lb") });
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "codex-lb",
            Name = "codex-lb",
            Primary = new RateWindow
            {
                Label = "Effective Weekly",
                UsedPercent = 50,
                ResetsAt = DateTimeOffset.UtcNow.AddDays(3).ToString("O"),
                WindowMinutes = 7 * 24 * 60,
            },
            Accounts =
            {
                new AccountInfo { Email = "a@example.com", Plan = "plus", PrimaryLabel = "Weekly", PrimaryUsedPercent = 50 },
                new AccountInfo { Email = "b@example.com", Plan = "plus", PrimaryLabel = "Weekly", PrimaryUsedPercent = 50 },
            },
        };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, new[] { ("codex-lb", snapshot) }).Single();

        // Two Plus seats at 32M/week each, half spent.
        Assert.AreEqual(32, segment.EffectiveTokensMillions, 0.01);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithFullySpentPool_KeepsAVisibleGrayBar()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("grok", "grok", "Grok") });
        var snapshots = new[]
        {
            ("grok", Snapshot("Grok · SuperGrok", usedPercent: 100, resetHours: 20, windowMinutes: 7 * 24 * 60)),
        };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, snapshots).Single();

        Assert.AreEqual("grok", segment.InstanceId);
        Assert.IsTrue(segment.Weight > 0, "A spent plan still exists and must stay on screen.");
        Assert.IsTrue(segment.IsGrayedOut, "Nothing left to spend reads as gray.");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithNoTokenAllowance_HidesThePlan()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("warp", "warp", "Warp") });
        var snapshots = new[]
        {
            ("warp", Snapshot("Warp · Free", usedPercent: 10, resetHours: 20, windowMinutes: 7 * 24 * 60)),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots);

        Assert.AreEqual(1, segments.Count);
        Assert.AreEqual("", segments[0].InstanceId);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithExpiredEntitlement_ExcludesTheDeadPlan()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("grok", "grok", "Grok"),
            new ProviderInstance("copilot", "copilot", "Copilot"),
        });
        var expired = Snapshot("Grok · SuperGrok", usedPercent: 0, resetHours: 20, windowMinutes: 7 * 24 * 60);
        expired.EntitlementStatus = EntitlementStatus.Expired;
        var snapshots = new[]
        {
            ("grok", expired),
            ("copilot", Snapshot("Copilot · Pro", usedPercent: 0, resetHours: 20, windowMinutes: 7 * 24 * 60)),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots);

        Assert.AreEqual(1, segments.Count);
        Assert.AreEqual("copilot", segments[0].InstanceId);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WhenSlotsAreScarce_KeepsOnePlanFromEveryBracket()
    {
        // Five big weekly plans would otherwise fill every slot and answer "you own
        // nothing that resets every five hours", which is not what the data says.
        var instances = new List<ProviderInstance>
        {
            new("claude", "claude", "Claude"),
            new("deepseek", "deepseek", "DeepSeek"),
            new("mimo", "mimo", "MiMo"),
        };
        var snapshots = new List<(string, ProviderSnapshot)>
        {
            ("claude", FiveHourPlan("Claude Code · Max", fiveHourUsed: 99, weeklyUsed: 0)),
            ("deepseek", Balance("DeepSeek", 1)),
            ("mimo", Snapshot("MiMo · Lite", usedPercent: 99, resetHours: 300, windowMinutes: 30 * 24 * 60)),
        };
        for (var index = 0; index < 8; index++)
        {
            var id = $"grok{index}";
            instances.Add(new ProviderInstance(id, "grok", id));
            snapshots.Add((id, Snapshot("Grok · SuperGrok Heavy", usedPercent: 0, resetHours: 100, windowMinutes: 7 * 24 * 60)));
        }

        var segments = HeroViewModel.BuildUsageTimelineSegments(
            new FakeConfig(instances),
            snapshots);

        var groups = segments.Select(segment => segment.Group).Distinct().ToList();
        CollectionAssert.Contains(groups, (EffectiveUsageGroup?)EffectiveUsageGroup.FiveHour);
        CollectionAssert.Contains(groups, (EffectiveUsageGroup?)EffectiveUsageGroup.Weekly);
        CollectionAssert.Contains(groups, (EffectiveUsageGroup?)EffectiveUsageGroup.Monthly);
        CollectionAssert.Contains(groups, (EffectiveUsageGroup?)EffectiveUsageGroup.Api);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithNoProvidersAtAll_StillHoldsThePlaceWithOneGrayBar()
    {
        var config = new FakeConfig(Array.Empty<ProviderInstance>());

        var segments = HeroViewModel.BuildUsageTimelineSegments(
            config,
            Array.Empty<(string, ProviderSnapshot)>());

        Assert.AreEqual(1, segments.Count);
        Assert.IsTrue(segments[0].IsGrayedOut);
        Assert.AreEqual("", segments[0].Label);
        Assert.AreEqual("", segments[0].AvailableText);
        Assert.IsFalse(segments[0].IsInteractive);
        Assert.IsNull(segments[0].Group);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WhenEveryProviderIsStillConnecting_HoldsThePlace()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("claude", "claude", "Claude") });
        var failing = Snapshot("Claude Code · Max", usedPercent: 0, resetHours: 4, windowMinutes: 5 * 60);
        failing.Error = "not signed in";

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, new[] { ("claude", failing) });

        Assert.AreEqual(1, segments.Count);
        Assert.AreEqual("", segments[0].InstanceId);
        Assert.IsTrue(segments[0].IsGrayedOut);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_TooltipReportsTheFiveHourFigureAgainstItsPool()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("claude", "claude", "Claude") });
        var snapshots = new[]
        {
            ("claude", FiveHourPlan("Claude Code · Max", fiveHourUsed: 50, weeklyUsed: 0)),
        };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, snapshots).Single();

        StringAssert.Contains(segment.ResetToolTip, "usable in the next 5h");
        StringAssert.Contains(segment.ResetToolTip, "5.2M");
        StringAssert.Contains(segment.ResetToolTip, "10.4M");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_TooltipSaysWhenALongerPoolIsTheRealLimit()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("claude", "claude", "Claude") });
        var snapshots = new[]
        {
            ("claude", FiveHourPlan("Claude Code · Max", fiveHourUsed: 0, weeklyUsed: 99.5)),
        };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, snapshots).Single();

        StringAssert.Contains(segment.ResetToolTip, "Capped by the longer pool");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_ApiTooltipShowsBothTheBalanceAndTheAssumedRate()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("deepseek", "deepseek", "DeepSeek") });

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, new[] { ("deepseek", Balance("DeepSeek", 25)) })
            .Single();

        StringAssert.Contains(segment.ResetToolTip, "Balance");
        StringAssert.Contains(segment.ResetToolTip, "blended rate");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_MeasuredThroughputInformsTooltipButNotWidth()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("codex-lb", "codex-lb", "codex-lb") });
        var snapshot = Snapshot("codex-lb · Plus", usedPercent: 0, resetHours: 100, windowMinutes: 7 * 24 * 60);
        snapshot.MeasuredWeeklyTokensMillions = 4321;

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, new[] { ("codex-lb", snapshot) }).Single();

        StringAssert.Contains(segment.ResetToolTip, "measured pool throughput");
        // Plus is estimated at 32M/week; the measurement must not size the bar.
        Assert.AreEqual(32, segment.Weight, 0.01);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_UnknownPlanFallsBackToSmallestTierWithQualifier()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("cursor", "cursor", "Cursor") });
        var snapshots = new[]
        {
            ("cursor", Snapshot("Cursor · Mystery Tier", usedPercent: 0, resetHours: 100, windowMinutes: 7 * 24 * 60)),
        };

        var segment = HeroViewModel.BuildUsageTimelineSegments(config, snapshots).Single();

        StringAssert.Contains(segment.ResetToolTip, "plan not recognized");
    }

    private static ProviderSnapshot Snapshot(string name, double usedPercent, double resetHours, long windowMinutes) => new()
    {
        Name = name,
        Primary = new RateWindow
        {
            Label = "Quota",
            UsedPercent = usedPercent,
            ResetsAt = DateTimeOffset.UtcNow.AddHours(resetHours).ToString("O"),
            WindowMinutes = windowMinutes,
        },
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// A subscription with a five-hour window nested inside a weekly pool.
    private static ProviderSnapshot FiveHourPlan(
        string name,
        double fiveHourUsed,
        double weeklyUsed,
        double fiveHourResetHours = 2) => new()
    {
        Name = name,
        Primary = new RateWindow
        {
            Label = "5h Pool",
            UsedPercent = fiveHourUsed,
            ResetsAt = DateTimeOffset.UtcNow.AddHours(fiveHourResetHours).ToString("O"),
            WindowMinutes = 5 * 60,
        },
        Secondary = new RateWindow
        {
            Label = "7d Pool",
            UsedPercent = weeklyUsed,
            ResetsAt = DateTimeOffset.UtcNow.AddDays(4).ToString("O"),
            WindowMinutes = 7 * 24 * 60,
        },
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// A metered API key with a USD balance and no reset window at all.
    private static ProviderSnapshot Balance(string name, double usd) => new()
    {
        Name = name,
        Primary = new RateWindow
        {
            Label = "Balance",
            UsedPercent = 0,
        },
        Balance = new BalanceInfo { Currency = "USD", Total = usd, Paid = usd },
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class FakeConfig : IConfigService
    {
        private readonly Dictionary<string, string> _values = new();

        public FakeConfig(IReadOnlyList<ProviderInstance> instances)
        {
            Instances = instances;
        }

        public IReadOnlyDictionary<string, string> All => _values;
        public IReadOnlyList<ProviderInstance> Instances { get; }
        public double RefreshMs => 1_800_000;

        public string Get(string key, string fallback = "") =>
            _values.TryGetValue(key, out var value) ? value : fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") =>
            _values.TryGetValue($"{instanceId}.{key}", out var value) ? value : fallback;

        public bool HasScoped(string instanceId, string key) =>
            _values.ContainsKey($"{instanceId}.{key}");

        public bool GetBool(string key, bool fallback = false) =>
            _values.TryGetValue(key, out var value) ? value == "true" : fallback;

        public void Set(string key, string value) => _values[key] = value;
        public void SetMany(IReadOnlyDictionary<string, string> values) { }
        public void Remove(string key) => _values.Remove(key);
        public Task SaveAsync() => Task.CompletedTask;

        public ProviderInstance AddInstance(string providerType) => new(providerType, providerType, providerType);
        public void RemoveInstance(string id) { }
    }
}
