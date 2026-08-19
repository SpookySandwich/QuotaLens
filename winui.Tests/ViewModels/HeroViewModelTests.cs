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

        Assert.AreEqual(1, segments.Count);
        Assert.AreEqual("claude", segments[0].InstanceId);
        Assert.AreEqual("effective 5h", segments[0].ResetFrequencyText);
        StringAssert.StartsWith(segments[0].AvailableText, "90%");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithPlanValueMode_IncludesGrayedOutBalanceSegments()
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

        Assert.IsTrue(segments.Count >= 1);
        var claude = segments.Single(s => s.InstanceId == "claude");
        Assert.IsFalse(claude.IsGrayedOut);
        Assert.AreEqual("$100", claude.AvailableText);
        Assert.AreEqual(100, claude.Weight, 0.001);
        Assert.IsTrue(segments.Any(s => s.InstanceId == "deepseek" && s.IsGrayedOut));
        Assert.AreEqual("$3.32", segments.First(s => s.InstanceId == "deepseek").AvailableText);
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

        Assert.AreEqual(1, segments.Count);
        Assert.AreEqual("mimo", segments[0].InstanceId);
        Assert.AreEqual("effective monthly", segments[0].ResetFrequencyText);
        StringAssert.StartsWith(segments[0].AvailableText, "75%");
    }

    [TestMethod]
    public void BuildUsageTimelineSegments_WithKimiTotalQuota_ShowsOnMonthlyWithReset()
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
                    Label = "Total quota",
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
    public void BuildUsageTimelineSegments_WithMonthlyMode_DoesNotInventMonthlyFromFiveHourPlans()
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

        Assert.AreEqual(0, segments.Count);
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
