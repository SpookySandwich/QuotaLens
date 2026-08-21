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
    public void BuildUsageTimelineSegments_UsesShortestPoolAndPlacesLongerCadenceOnLeft()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("claude", "claude", "Claude"),
            new ProviderInstance("mimo", "mimo", "MiMo"),
            new ProviderInstance("codex-lb", "codex-lb", "codex-lb"),
        });
        config.Set("codex_lb_value", "70");
        var snapshots = new[]
        {
            ("claude", new ProviderSnapshot
            {
                ProviderId = "claude",
                Name = "Claude Code · Max",
                Primary = new RateWindow
                {
                    Label = "5h Pool",
                    UsedPercent = 10,
                    ResetsAt = DateTimeOffset.UtcNow.AddHours(4).ToString("O"),
                    WindowMinutes = 5 * 60,
                },
                Secondary = new RateWindow
                {
                    Label = "7d Pool",
                    UsedPercent = 5,
                    ResetsAt = DateTimeOffset.UtcNow.AddHours(96).ToString("O"),
                    WindowMinutes = 7 * 24 * 60,
                },
            }),
            ("mimo", Snapshot("MiMo · Standard", usedPercent: 25, resetHours: 720, windowMinutes: 30 * 24 * 60)),
            ("codex-lb", new ProviderSnapshot
            {
                ProviderId = "codex-lb",
                Name = "codex-lb",
                Primary = new RateWindow
                {
                    Label = "5h Pool",
                    UsedPercent = 20,
                    ResetsAt = DateTimeOffset.UtcNow.AddHours(3).AddMinutes(13).ToString("O"),
                    WindowMinutes = 5 * 60,
                },
                Secondary = new RateWindow
                {
                    Label = "Weekly",
                    UsedPercent = 1,
                    ResetsAt = DateTimeOffset.UtcNow.AddHours(120).ToString("O"),
                    WindowMinutes = 7 * 24 * 60,
                },
            }),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots);

        // Value sort: full monthly plan price, highest first.
        var visible = segments.Where(segment => !segment.IsRemainder).ToList();
        Assert.AreEqual("Claude Code · Max", visible[0].Label);
        Assert.AreEqual("claude", visible[0].InstanceId);
        Assert.AreEqual("$100", visible[0].AvailableText);
        Assert.AreEqual("codex-lb", visible[1].Label);
        Assert.AreEqual("codex-lb", visible[1].InstanceId);
        Assert.AreEqual("$70", visible[1].AvailableText);
        Assert.AreEqual("~3h 12m", visible[1].ResetText);
        Assert.AreEqual("MiMo · Standard", visible[2].Label);
        Assert.AreEqual("mimo", visible[2].InstanceId);
        Assert.AreEqual("reset monthly", visible[2].ResetFrequencyText);
        Assert.AreEqual("reset every 5h", visible[0].ResetFrequencyText);
        Assert.AreEqual("reset every 5h", visible[1].ResetFrequencyText);
        Assert.AreEqual(90, visible[0].AvailablePercent, 0.001);
        Assert.AreEqual(80, visible[1].AvailablePercent, 0.001);
        Assert.AreEqual(100, visible[0].Weight, 0.001);
        Assert.AreEqual(70, visible[1].Weight, 0.001);
        Assert.AreEqual(16, visible[2].Weight, 0.001);
        // Spent capacity is not rendered — the bar is runway only.
        Assert.IsFalse(segments.Any(segment => segment.IsRemainder));
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WhenClaudeFiveHourResetTimeMissing_StillUsesFiveHourCadence()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("claude", "claude", "Claude") });
        var snapshots = new[]
        {
            ("claude", new ProviderSnapshot
            {
                ProviderId = "claude",
                Name = "Claude Code · Max",
                Primary = new RateWindow
                {
                    Label = "5h Pool",
                    UsedPercent = 0,
                    ResetsAt = null,
                    WindowMinutes = 5 * 60,
                },
                Secondary = new RateWindow
                {
                    Label = "7d Pool",
                    UsedPercent = 0,
                    ResetsAt = DateTimeOffset.UtcNow.AddDays(4).ToString("O"),
                    WindowMinutes = 7 * 24 * 60,
                },
            }),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots);

        var visible = segments.Where(segment => !segment.IsRemainder).ToList();
        Assert.AreEqual(1, visible.Count);
        Assert.AreEqual("Claude Code · Max", visible[0].Label);
        Assert.AreEqual("reset every 5h", visible[0].ResetFrequencyText);
        Assert.IsNull(visible[0].ResetText);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WhenWindowLengthMissing_InfersFrequencyFromStructuredWindow()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("example", "mimo", "Example") });
        var snapshots = new[]
        {
            ("example", Snapshot("Example", usedPercent: 20, resetHours: 48, windowMinutes: 0)),
        };
        snapshots[0].Item2.Primary.WindowMinutes = null;
        snapshots[0].Item2.Primary.Label = "Monthly credits";

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots);

        Assert.AreEqual("reset monthly", segments.First().ResetFrequencyText);
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
    public void BuildUsageTimelineSegments_WeightsSegmentsByEstimatedTokensRemaining()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("claude", "claude", "Claude"),
            new ProviderInstance("codex", "codex", "Codex"),
        });
        var snapshots = new[]
        {
            ("claude", Snapshot("Claude Code · Pro", usedPercent: 0, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("codex", Snapshot("Codex · Plus", usedPercent: 0, resetHours: 100, windowMinutes: 7 * 24 * 60)),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Weekly);

        var claude = segments.Single(segment => segment.InstanceId == "claude");
        var codex = segments.Single(segment => segment.InstanceId == "codex");
        // Claude Pro ≈ 100M tokens/week, ChatGPT Plus ≈ 32M — the bars must reflect
        // that a Claude Pro subscription simply buys more tokens.
        Assert.AreEqual(100, claude.Weight, 0.001);
        Assert.AreEqual(32, codex.Weight, 0.001);
        StringAssert.Contains(claude.ResetToolTip, "tokens/week");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WeightIsTokensRemainingWithoutSpentFiller()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("claude", "claude", "Claude") });
        var snapshots = new[]
        {
            ("claude", Snapshot("Claude Code · Max 20x", usedPercent: 25, resetHours: 100, windowMinutes: 7 * 24 * 60)),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Weekly);

        Assert.AreEqual(1, segments.Count);
        Assert.AreEqual(600 * 0.75, segments[0].Weight, 0.001);
        Assert.IsFalse(segments[0].IsRemainder);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_MeasuredThroughputInformsTooltipButNotWidth()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("codex-lb", "codex-lb", "codex-lb") });
        var snapshot = Snapshot("codex-lb", usedPercent: 82, resetHours: 40, windowMinutes: 7 * 24 * 60);
        snapshot.MeasuredWeeklyTokensMillions = 8674;
        snapshot.Accounts = new List<AccountInfo>
        {
            new() { Email = "a@example.com", Plan = "pro 20x" },
            new() { Email = "b@example.com", Plan = "business" },
            new() { Email = "c@example.com", Plan = "business" },
        };
        var snapshots = new[] { ("codex-lb", snapshot) };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Weekly);

        var segment = segments.Single();
        // Width uses the comparable plan-sum estimate (600+32+32 at 18% left)…
        Assert.AreEqual(664 * 0.18, segment.Weight, 0.01);
        // …while the user's real cache-heavy throughput is context in the tooltip.
        StringAssert.Contains(segment.ResetToolTip, "measured pool throughput");
        StringAssert.Contains(segment.ResetToolTip, "8.7B");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_HidesPlansWithNoTokenAllowance()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("kimi", "kimi", "Kimi"),
            new ProviderInstance("mimo", "mimo", "MiMo"),
        });
        var snapshots = new[]
        {
            // Kimi Adagio has no coding-agent access at all: no zero-width sliver.
            ("kimi", Snapshot("Kimi · Adagio", usedPercent: 0, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("mimo", Snapshot("MiMo · Standard", usedPercent: 0, resetHours: 100, windowMinutes: 30 * 24 * 60)),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots);

        Assert.IsFalse(segments.Any(segment => segment.InstanceId == "kimi"));
        Assert.IsTrue(segments.Any(segment => segment.InstanceId == "mimo"));
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_UnknownPlanFallsBackToSmallestTierWithQualifier()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("cursor", "cursor", "Cursor") });
        var snapshots = new[]
        {
            ("cursor", Snapshot("Cursor · Hypernova", usedPercent: 0, resetHours: 100, windowMinutes: 30 * 24 * 60)),
        };

        var tokenSegments = HeroViewModel.BuildUsageTimelineSegments(
            config, snapshots, sortMode: ProviderSortMode.Monthly);
        var cursorTokens = tokenSegments.Single(segment => segment.InstanceId == "cursor");
        Assert.AreEqual(120, cursorTokens.Weight, 0.001); // Cursor's smallest PAID token tier (Pro)
        StringAssert.Contains(cursorTokens.ResetToolTip, "plan not recognized");

        var valueSegments = HeroViewModel.BuildUsageTimelineSegments(
            config, snapshots, sortMode: ProviderSortMode.PlanValue);
        var cursorValue = valueSegments.Single(segment => segment.InstanceId == "cursor");
        Assert.AreEqual("$20", cursorValue.AvailableText);
        Assert.AreEqual(20, cursorValue.Weight, 0.001);
        StringAssert.Contains(cursorValue.ResetToolTip, "plan not recognized");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithFiveHourMode_OnlyIncludesFiveHourWindows()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("claude", "claude", "Claude"),
            new ProviderInstance("codex", "codex", "Codex"),
        });
        var snapshots = new[]
        {
            ("claude", new ProviderSnapshot
            {
                ProviderId = "claude",
                Name = "Claude Code · Max",
                Primary = new RateWindow
                {
                    Label = "5h Pool",
                    UsedPercent = 10,
                    ResetsAt = DateTimeOffset.UtcNow.AddHours(4).ToString("O"),
                    WindowMinutes = 5 * 60,
                },
            }),
            ("codex", new ProviderSnapshot
            {
                ProviderId = "codex",
                Name = "Codex · Pro",
                Primary = new RateWindow
                {
                    Label = "Weekly Pool",
                    UsedPercent = 20,
                    ResetsAt = DateTimeOffset.UtcNow.AddHours(96).ToString("O"),
                    WindowMinutes = 7 * 24 * 60,
                },
            }),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.FiveHour);

        // Only Claude has a 5h pool, so it gets the whole bar. Codex has no 5h
        // number to draw; giving it a placeholder would shrink the one real
        // measurement on screen in order to show nothing.
        var segment = segments.Single();
        Assert.AreEqual("claude", segment.InstanceId);
        Assert.IsFalse(segment.IsGrayedOut);
        Assert.AreEqual("effective 5h", segment.ResetFrequencyText);
        StringAssert.StartsWith(segment.AvailableText, "90%");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithPlanValueMode_ShowsBalancesAsFirstClassValue()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("claude", "claude", "Claude"),
            new ProviderInstance("deepseek", "deepseek", "DeepSeek"),
        });
        var snapshots = new[]
        {
            ("claude", new ProviderSnapshot
            {
                ProviderId = "claude",
                Name = "Claude Code · Max",
                Primary = new RateWindow
                {
                    Label = "5h Pool",
                    UsedPercent = 10,
                    ResetsAt = DateTimeOffset.UtcNow.AddHours(4).ToString("O"),
                    WindowMinutes = 5 * 60,
                },
            }),
            ("deepseek", new ProviderSnapshot
            {
                ProviderId = "deepseek",
                Name = "DeepSeek",
                Balance = new BalanceInfo { Total = 23.9, Currency = "CNY" },
            }),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.PlanValue);

        Assert.AreEqual(2, segments.Count);
        Assert.AreEqual("claude", segments[0].InstanceId);
        Assert.IsFalse(segments[0].IsGrayedOut);
        Assert.AreEqual("$100", segments[0].AvailableText);
        Assert.AreEqual(100, segments[0].Weight, 0.001);
        // In the money view an API balance is money like any other: converted to
        // USD and drawn in the provider's own color, not dimmed to a footnote.
        Assert.AreEqual("deepseek", segments[1].InstanceId);
        Assert.IsFalse(segments[1].IsGrayedOut);
        Assert.AreEqual("$3.32", segments[1].AvailableText);
        Assert.AreEqual(23.9 / 7.2, segments[1].Weight, 0.001);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithMonthlyMode_OnlyIncludesMonthlyCadence()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("claude", "claude", "Claude"),
            new ProviderInstance("mimo", "mimo", "MiMo"),
        });
        var snapshots = new[]
        {
            ("claude", new ProviderSnapshot
            {
                ProviderId = "claude",
                Name = "Claude Code · Max",
                Primary = new RateWindow
                {
                    Label = "5h Pool",
                    UsedPercent = 10,
                    ResetsAt = DateTimeOffset.UtcNow.AddHours(4).ToString("O"),
                    WindowMinutes = 5 * 60,
                },
            }),
            ("mimo", Snapshot("MiMo · Standard", usedPercent: 25, resetHours: 720, windowMinutes: 30 * 24 * 60)),
        };
        snapshots[1].Item2.Primary.Label = "Monthly credits";

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Monthly);

        // MiMo is the only monthly pool, so it fills the bar. Claude is left out
        // entirely despite its far larger token allowance — it has no monthly
        // number, and a bar with no number behind it is not worth width.
        var segment = segments.Single();
        Assert.AreEqual("mimo", segment.InstanceId);
        Assert.IsFalse(segment.IsGrayedOut);
        Assert.AreEqual("effective monthly", segment.ResetFrequencyText);
        StringAssert.StartsWith(segment.AvailableText, "75%");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithKimiMonthlyPool_ShowsOnMonthlyWithReset()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("kimi", "kimi", "Kimi") });
        var resetAt = DateTimeOffset.UtcNow.AddDays(12).AddHours(3).ToString("O");
        var snapshots = new[]
        {
            ("kimi", new ProviderSnapshot
            {
                Name = "Kimi · Allegro",
                PlanName = "Allegro",
                Primary = new RateWindow
                {
                    Label = "Monthly",
                    UsedPercent = 68,
                    ResetsAt = resetAt,
                    WindowMinutes = QuotaCadencePolicy.MonthlyMinutes,
                    CountsForAvailability = true,
                    DetailText = "68% used",
                },
            }),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Monthly);

        Assert.AreEqual(1, segments.Count);
        Assert.AreEqual("kimi", segments[0].InstanceId);
        Assert.AreEqual("effective monthly", segments[0].ResetFrequencyText);
        StringAssert.StartsWith(segments[0].AvailableText, "32%");
        StringAssert.Contains(segments[0].AvailableText, "12d");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithThreePlusAccounts_ShowsSummedPlanValue()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("codex-lb", "codex-lb", "codex-lb") });
        var snapshot = Snapshot("codex-lb", usedPercent: 75, resetHours: 40, windowMinutes: 7 * 24 * 60);
        snapshot.Accounts =
        [
            new AccountInfo { Email = "a@example.com", Plan = "plus" },
            new AccountInfo { Email = "b@example.com", Plan = "plus" },
            new AccountInfo { Email = "c@example.com", Plan = "plus" },
        ];

        var segments = HeroViewModel.BuildUsageTimelineSegments(
            config,
            [("codex-lb", snapshot)],
            sortMode: ProviderSortMode.PlanValue);

        var segment = segments.Single();
        Assert.AreEqual("$60", segment.AvailableText);
        Assert.AreEqual(60, segment.Weight, 0.001);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithNoProvidersAtAll_StillHoldsThePlaceWithOneGrayBar()
    {
        var config = new FakeConfig(Array.Empty<ProviderInstance>());

        foreach (var mode in new[]
                 {
                     ProviderSortMode.FiveHour,
                     ProviderSortMode.Weekly,
                     ProviderSortMode.Monthly,
                     ProviderSortMode.PlanValue,
                 })
        {
            var segments = HeroViewModel.BuildUsageTimelineSegments(
                config,
                Array.Empty<(string, ProviderSnapshot)>(),
                sortMode: mode);

            // The card must never collapse: an empty chart that vanishes reflows the
            // whole dashboard every time the user switches cadence.
            var segment = segments.Single();
            Assert.IsTrue(segment.IsGrayedOut, $"{mode} placeholder should be gray");
            Assert.IsTrue(segment.Weight > 0, $"{mode} placeholder needs a drawable weight");
            // The empty bar stands for nothing, so it says nothing: no provider
            // name, no percentage, no dollar amount.
            Assert.AreEqual("", segment.Label, $"{mode} placeholder must carry no label");
            Assert.AreEqual("", segment.AvailableText, $"{mode} placeholder must carry no value");
            // Inert: nothing to scroll to.
            Assert.IsFalse(segment.IsInteractive, $"{mode} placeholder should not be clickable");
        }
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WhenEveryProviderIsStillConnecting_HoldsThePlace()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("claude", "claude", "Claude") });
        var snapshots = new[]
        {
            ("claude", new ProviderSnapshot
            {
                ProviderId = "claude",
                Name = "Claude Code",
                Error = "Network error: timed out",
                Primary = new RateWindow { Label = "5h Pool", UsedPercent = 0 },
            }),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(
            config,
            snapshots,
            sortMode: ProviderSortMode.Monthly);

        var segment = segments.Single();
        Assert.IsTrue(segment.IsGrayedOut);
        Assert.AreEqual("", segment.Label);
        Assert.IsFalse(segment.IsInteractive);
        // Sighted users see a blank gray track; screen-reader users still get told why.
        StringAssert.Contains(segment.AutomationName, "no monthly plan");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithMonthlyMode_ShowsOneBlankGrayBarWhenNoPlanHasThatCadence()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("claude", "claude", "Claude"),
            new ProviderInstance("codex", "codex", "Codex"),
        });
        var snapshots = new[]
        {
            ("claude", new ProviderSnapshot
            {
                ProviderId = "claude",
                Name = "Claude Code · Max",
                Primary = new RateWindow
                {
                    Label = "5h Pool",
                    UsedPercent = 10,
                    WindowMinutes = 5 * 60,
                },
            }),
            ("codex", new ProviderSnapshot
            {
                ProviderId = "codex",
                Name = "Codex · Plus",
                Primary = new RateWindow
                {
                    Label = "Weekly Pool",
                    UsedPercent = 20,
                    WindowMinutes = 7 * 24 * 60,
                },
            }),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Monthly);

        // Neither plan has a monthly pool, so neither is drawn — a per-provider
        // placeholder would be a bar representing a number that does not exist.
        // The chart still holds its place as a single blank gray track.
        var segment = segments.Single();
        Assert.IsTrue(segment.IsGrayedOut);
        Assert.IsTrue(segment.Weight > 0);
        Assert.AreEqual("", segment.Label);
        Assert.AreEqual("", segment.AvailableText);
        Assert.IsFalse(segment.IsInteractive);
        // "0% available" would be a lie: these plans have capacity, just not monthly.
        StringAssert.Contains(segment.AutomationName, "no monthly plan");
        Assert.IsFalse(segment.AutomationName.Contains("available", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithSingleMatchingPlan_GivesItTheWholeBar()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("kimi", "kimi", "Kimi"),
            new ProviderInstance("claude", "claude", "Claude"),
            new ProviderInstance("codex", "codex", "Codex"),
        });
        var snapshots = new[]
        {
            ("kimi", Snapshot("Kimi · Allegro", usedPercent: 70, resetHours: 285, windowMinutes: 30 * 24 * 60)),
            ("claude", Snapshot("Claude Code · Max", usedPercent: 10, resetHours: 4, windowMinutes: 5 * 60)),
            ("codex", Snapshot("Codex · Plus", usedPercent: 20, resetHours: 100, windowMinutes: 7 * 24 * 60)),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Monthly);

        // The one provider with a monthly number owns the full width. Claude and
        // Codex are not drawn at all: sharing the bar with them would shrink the
        // only real measurement to make room for two that do not exist.
        var segment = segments.Single();
        Assert.AreEqual("kimi", segment.InstanceId);
        Assert.IsFalse(segment.IsGrayedOut);
        StringAssert.StartsWith(segment.AvailableText, "30%");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithMixedCadences_ExcludesPlansWithoutTheSelectedCadence()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("claude", "claude", "Claude"),
            new ProviderInstance("codex", "codex", "Codex"),
            new ProviderInstance("mimo", "mimo", "MiMo"),
        });
        var snapshots = new[]
        {
            ("claude", Snapshot("Claude Code · Max", usedPercent: 10, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("codex", Snapshot("Codex · Plus", usedPercent: 40, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("mimo", Snapshot("MiMo · Standard", usedPercent: 25, resetHours: 720, windowMinutes: 30 * 24 * 60)),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Weekly);

        // Only the two weekly plans are drawn, and they split the bar between
        // them. The monthly-only plan is absent rather than gray.
        CollectionAssert.AreEqual(
            new[] { "claude", "codex" },
            segments.Select(segment => segment.InstanceId).ToArray());
        Assert.IsTrue(segments.All(segment => !segment.IsGrayedOut));
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithCadenceMode_ExcludesBalancesWithNoWindowAtThatCadence()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("mimo", "mimo", "MiMo"),
            new ProviderInstance("claude", "claude", "Claude"),
            new ProviderInstance("deepseek", "deepseek", "DeepSeek"),
        });
        var snapshots = new[]
        {
            ("mimo", Snapshot("MiMo · Standard", usedPercent: 25, resetHours: 720, windowMinutes: 30 * 24 * 60)),
            ("claude", Snapshot("Claude Code · Max", usedPercent: 10, resetHours: 4, windowMinutes: 5 * 60)),
            ("deepseek", new ProviderSnapshot
            {
                ProviderId = "deepseek",
                Name = "DeepSeek",
                Balance = new BalanceInfo { Total = 23.9, Currency = "CNY" },
            }),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Monthly);

        // A balance has no refill window at all, so a cadence view has nothing to
        // place it in — it belongs to the value view, where money is the unit.
        // Claude's 5h-only plan is likewise absent from a monthly view.
        var segment = segments.Single();
        Assert.AreEqual("mimo", segment.InstanceId);
        Assert.IsFalse(segment.IsGrayedOut);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WhenSlotsAreScarce_PrefersColoredBarsOverGrayOnes()
    {
        var weeklyIds = new[] { "claude", "codex", "codex-lb", "gemini", "cursor", "kimi" };
        var instances = weeklyIds
            .Select(id => new ProviderInstance(id, id, id))
            .Append(new ProviderInstance("mimo", "mimo", "MiMo"))
            .ToArray();
        var config = new FakeConfig(instances);
        var snapshots = new[]
        {
            ("claude", Snapshot("Claude Code · Max", usedPercent: 20, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("codex", Snapshot("Codex · Plus", usedPercent: 20, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("codex-lb", Snapshot("codex-lb · plus", usedPercent: 20, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("gemini", Snapshot("Gemini · Google AI Pro", usedPercent: 20, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("cursor", Snapshot("Cursor · Pro", usedPercent: 20, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("kimi", Snapshot("Kimi · Allegro", usedPercent: 20, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("mimo", Snapshot("MiMo · Standard", usedPercent: 25, resetHours: 720, windowMinutes: 30 * 24 * 60)),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Weekly);

        // Six slots, seven providers: the gray "no weekly plan" bar is the one
        // that loses, never a plan the view can actually measure.
        Assert.AreEqual(6, segments.Count);
        Assert.IsFalse(segments.Any(segment => segment.IsGrayedOut));
        Assert.IsFalse(segments.Any(segment => segment.InstanceId == "mimo"));
        CollectionAssert.AreEquivalent(weeklyIds, segments.Select(segment => segment.InstanceId).ToArray());
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WhenSlotsAreScarce_KeepsTheProviderThatStillHasCapacity()
    {
        // Six expensive plans, all spent, plus one cheap plan with room left. The
        // chart answers "what can I still use?", so the cheap one must survive the
        // cap — ordering by price alone would fill every slot with 0% bars and drop
        // the only provider the user could actually reach for.
        var spentIds = new[] { "claude", "codex", "codex-lb", "gemini", "cursor", "kimi" };
        var instances = spentIds
            .Select(id => new ProviderInstance(id, id, id))
            .Append(new ProviderInstance("zai", "zai", "z.ai"))
            .ToArray();
        var config = new FakeConfig(instances);
        var snapshots = new[]
        {
            ("claude", Snapshot("Claude Code · Max", usedPercent: 100, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("codex", Snapshot("Codex · Plus", usedPercent: 100, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("codex-lb", Snapshot("codex-lb · plus", usedPercent: 100, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("gemini", Snapshot("Gemini · Google AI Pro", usedPercent: 100, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("cursor", Snapshot("Cursor · Pro", usedPercent: 100, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("kimi", Snapshot("Kimi · Allegro", usedPercent: 100, resetHours: 100, windowMinutes: 7 * 24 * 60)),
            ("zai", Snapshot("z.ai · Lite", usedPercent: 10, resetHours: 100, windowMinutes: 7 * 24 * 60)),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Weekly);

        Assert.AreEqual(6, segments.Count);
        Assert.IsTrue(
            segments.Any(segment => segment.InstanceId == "zai"),
            "the only provider with weekly capacity left was evicted by spent pools");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithExhaustedFiveHourPool_StillShowsHealthyMonthlyPool()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("claude", "claude", "Claude") });
        var snapshots = new[]
        {
            ("claude", new ProviderSnapshot
            {
                ProviderId = "claude",
                Name = "Claude Code · Max",
                Primary = new RateWindow
                {
                    Label = "5h Pool",
                    UsedPercent = 100,
                    ResetsAt = DateTimeOffset.UtcNow.AddHours(2).ToString("O"),
                    WindowMinutes = 5 * 60,
                },
                Secondary = new RateWindow
                {
                    Label = "Monthly Pool",
                    UsedPercent = 30,
                    ResetsAt = DateTimeOffset.UtcNow.AddDays(10).ToString("O"),
                    WindowMinutes = 30 * 24 * 60,
                },
            }),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Monthly);

        // Overall availability is 0 because the 5h pool is burnt, but the monthly
        // view asks about the monthly pool, and that one is 70% full.
        var segment = segments.Single();
        Assert.IsFalse(segment.IsGrayedOut);
        Assert.AreEqual("effective monthly", segment.ResetFrequencyText);
        StringAssert.StartsWith(segment.AvailableText, "70%");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithExpiredEntitlement_ExcludesTheDeadPlan()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("cursor", "cursor", "Cursor"),
            new ProviderInstance("mimo", "mimo", "MiMo"),
        });
        var expired = Snapshot("Cursor · Pro", usedPercent: 0, resetHours: 100, windowMinutes: 30 * 24 * 60);
        expired.EntitlementStatus = EntitlementStatus.Expired;
        var snapshots = new[]
        {
            ("cursor", expired),
            ("mimo", Snapshot("MiMo · Standard", usedPercent: 25, resetHours: 720, windowMinutes: 30 * 24 * 60)),
        };

        // A dead plan must not be priced or drawn in either view.
        var value = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.PlanValue);
        var monthly = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Monthly);

        Assert.AreEqual("mimo", value.Single().InstanceId);
        Assert.AreEqual("mimo", monthly.Single().InstanceId);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithFullySpentMatchingPool_KeepsAVisibleSliver()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("claude", "claude", "Claude") });
        var snapshots = new[]
        {
            ("claude", Snapshot("Claude Code · Max", usedPercent: 100, resetHours: 40, windowMinutes: 7 * 24 * 60)),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.Weekly);

        // Zero tokens left is still a fact worth drawing: the cylinder drops any
        // non-positive weight, so an empty pool floors to a minimum sliver.
        var segment = segments.Single();
        Assert.AreEqual(0.01, segment.Weight, 0.0001);
        Assert.IsFalse(segment.IsGrayedOut);
        Assert.AreEqual("effective weekly", segment.ResetFrequencyText);
        StringAssert.StartsWith(segment.AvailableText, "0%");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithGeminiAndGrokPlans_ShowsOfficialMonthlyPrices()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("gemini", "gemini", "Gemini"),
            new ProviderInstance("grok", "grok", "Grok"),
        });
        var snapshots = new[]
        {
            ("gemini", new ProviderSnapshot
            {
                Name = "Gemini",
                PlanName = "Google AI Pro",
                Primary = new RateWindow { Label = "Gemini weekly", UsedPercent = 0, WindowMinutes = 7 * 24 * 60 },
            }),
            ("grok", new ProviderSnapshot
            {
                Name = "Grok",
                PlanId = "x_premium_plus",
                PlanName = "X Premium+",
                Primary = new RateWindow { Label = "Weekly included", UsedPercent = 5, WindowMinutes = 7 * 24 * 60 },
            }),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.PlanValue);

        Assert.AreEqual("$40", segments.Single(s => s.InstanceId == "grok").AvailableText);
        Assert.AreEqual("$20", segments.Single(s => s.InstanceId == "gemini").AvailableText);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithValueMode_AlwaysShowsDollarsNeverPercent()
    {
        var config = new FakeConfig(new[]
        {
            new ProviderInstance("kimi", "kimi", "Kimi"),
            new ProviderInstance("claude", "claude", "Claude"),
            new ProviderInstance("deepseek", "deepseek", "DeepSeek"),
        });
        var snapshots = new[]
        {
            ("kimi", new ProviderSnapshot
            {
                Name = "Kimi",
                PlanName = "Allegro",
                Primary = new RateWindow { Label = "Weekly", UsedPercent = 68, WindowMinutes = 7 * 24 * 60 },
            }),
            ("claude", new ProviderSnapshot
            {
                Name = "Claude Code · Max",
                Primary = new RateWindow { Label = "5h Pool", UsedPercent = 49, WindowMinutes = 5 * 60 },
            }),
            ("deepseek", new ProviderSnapshot
            {
                Name = "DeepSeek",
                Balance = new BalanceInfo { Total = 23.9, Currency = "CNY" },
            }),
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(config, snapshots, sortMode: ProviderSortMode.PlanValue);

        Assert.AreEqual(3, segments.Count);
        Assert.IsTrue(segments.All(segment => segment.AvailableText.StartsWith('$')
            && !segment.AvailableText.Contains('%', StringComparison.Ordinal)));
        Assert.AreEqual("$99", segments.Single(s => s.InstanceId == "kimi").AvailableText);
        Assert.AreEqual("$100", segments.Single(s => s.InstanceId == "claude").AvailableText);
        Assert.AreEqual("$3.32", segments.Single(s => s.InstanceId == "deepseek").AvailableText);
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithCodexLbEffectiveWindows_UsesConstrainedFiveHour()
    {
        var config = new FakeConfig(new[] { new ProviderInstance("codex-lb", "codex-lb", "codex-lb") });
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "codex-lb",
            Name = "codex-lb",
            Primary = new RateWindow
            {
                Label = "Effective Usage",
                UsedPercent = 80,
                ResetsAt = DateTimeOffset.UtcNow.AddHours(3).ToString("O"),
            },
            AdditionalWindows =
            {
                new RateWindow
                {
                    Label = "Effective 5h",
                    UsedPercent = 60,
                    ResetsAt = DateTimeOffset.UtcNow.AddHours(3).ToString("O"),
                    WindowMinutes = 5 * 60,
                },
                new RateWindow
                {
                    Label = "Effective Weekly",
                    UsedPercent = 80,
                    ResetsAt = DateTimeOffset.UtcNow.AddDays(4).ToString("O"),
                    WindowMinutes = 7 * 24 * 60,
                },
            },
        };

        var segments = HeroViewModel.BuildUsageTimelineSegments(
            config,
            new[] { ("codex-lb", snapshot) },
            sortMode: ProviderSortMode.FiveHour);

        Assert.AreEqual(1, segments.Count);
        Assert.AreEqual("effective 5h", segments[0].ResetFrequencyText);
        // Effective 5h cannot exceed the weekly pool (20% remaining).
        Assert.AreEqual(20, segments[0].AvailablePercent, 0.001);
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
