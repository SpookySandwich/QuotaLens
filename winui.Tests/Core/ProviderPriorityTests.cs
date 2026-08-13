using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Services;
using QuotaLens.ViewModels;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class ProviderPriorityTests
{
    [TestMethod]
    public void RankUsableSubscriptions_HigherPlanValueBeatsHigherAvailability_ReturnsHigherValuePlan()
    {
        // Arrange
        var claude = Snapshot("Claude Code · Pro", usedPercent: 70);
        var antigravity = Snapshot("Antigravity", usedPercent: 0);

        // Act
        var ranked = ProviderPriority.RankUsableSubscriptions(
            new[]
            {
                ("antigravity", antigravity),
                ("claude", claude),
            });

        // Assert
        Assert.AreEqual("claude", ranked[0].Id);
    }

    [TestMethod]
    public void RankUsableSubscriptions_WithPayAsYouGoApi_ExcludesApiProvider()
    {
        // Arrange
        var deepSeek = Snapshot("DeepSeek", usedPercent: 0);
        var claude = Snapshot("Claude Code · Pro", usedPercent: 70);

        // Act
        var ranked = ProviderPriority.RankUsableSubscriptions(
            new[]
            {
                ("deepseek", deepSeek),
                ("claude", claude),
            });

        // Assert
        Assert.AreEqual(1, ranked.Count);
        Assert.AreEqual("claude", ranked[0].Id);
    }

    [TestMethod]
    public void Score_WithExhaustedPaidPlan_RanksAbovePayAsYouGoButBelowUsablePlans()
    {
        // Arrange
        var exhaustedClaude = Snapshot("Claude Code · Pro", usedPercent: 100);
        var usableBayes = Snapshot("BayesDL", usedPercent: 80);
        var deepSeek = Snapshot("DeepSeek", usedPercent: 0);

        // Act
        var exhausted = ProviderPriority.Score("claude", exhaustedClaude);
        var usable = ProviderPriority.Score("bayesdl", usableBayes);
        var payAsYouGo = ProviderPriority.Score("deepseek", deepSeek);

        // Assert
        Assert.AreEqual(ProviderPriority.ExhaustedSubscriptionBucket, exhausted.Bucket);
        Assert.AreEqual(ProviderPriority.UsableSubscriptionBucket, usable.Bucket);
        Assert.AreEqual(ProviderPriority.PayAsYouGoBucket, payAsYouGo.Bucket);
    }

    [TestMethod]
    public void Score_ExpiredEntitlement_IsExhaustedRegardlessOfStaleQuota()
    {
        // Arrange
        var snapshot = Snapshot("MiMo · Standard", usedPercent: 0);
        snapshot.EntitlementStatus = EntitlementStatus.Expired;

        // Act
        var score = ProviderPriority.Score("mimo", snapshot);

        // Assert
        Assert.AreEqual(ProviderPriority.ExhaustedSubscriptionBucket, score.Bucket);
        Assert.AreEqual(0, score.PlanValue, 0.001);
        Assert.AreEqual(0, score.Availability, 0.001);
    }

    [TestMethod]
    public void PlanValue_WithCodexConfiguredValue_UsesConfiguredValue()
    {
        // Arrange
        var snapshot = Snapshot("codex-lb", usedPercent: 20);
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["codex_lb_value"] = "75",
        });

        // Act
        var value = ProviderPriority.PlanValue("codex-lb", snapshot, config);

        // Assert
        Assert.AreEqual(75, value);
    }

    [TestMethod]
    public void PlanValue_WithCodexAccounts_AggregatesAccountPlanValuesBeforeProviderFallback()
    {
        // Arrange
        var snapshot = Snapshot("codex-lb", usedPercent: 20);
        snapshot.Accounts.Add(new AccountInfo { Plan = "team" });
        snapshot.Accounts.Add(new AccountInfo { Plan = "plus" });
        snapshot.Accounts.Add(new AccountInfo { Plan = "team" });
        var config = new FakeConfig(new Dictionary<string, string>
        {
            [PlanValueRules.ConfigKey("codex-lb")] = "codex-lb=50",
        });

        // Act
        var value = ProviderPriority.PlanValue("codex-lb", snapshot, config);

        // Assert
        Assert.AreEqual(70, value);
    }

    [TestMethod]
    public void PlanValue_WithCodexAccounts_UsesConfiguredAccountPlanRules()
    {
        // Arrange
        var snapshot = Snapshot("codex-lb", usedPercent: 20);
        snapshot.Accounts.Add(new AccountInfo { Plan = "team" });
        snapshot.Accounts.Add(new AccountInfo { Plan = "plus" });
        var config = new FakeConfig(new Dictionary<string, string>
        {
            [PlanValueRules.ConfigKey("codex-lb")] = "team=60",
        });

        // Act
        var value = ProviderPriority.PlanValue("codex-lb", snapshot, config);

        // Assert
        Assert.AreEqual(80, value);
    }

    [TestMethod]
    public void PlanValue_WithScopedCodexConfiguredValue_DoesNotInventProviderFallback()
    {
        // Arrange
        var snapshot = Snapshot("codex-lb", usedPercent: 20);
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["codex-lb.codex_lb_value"] = "75",
        });

        // Act
        var value = ProviderPriority.PlanValue("codex-lb", snapshot, config);

        // Assert
        Assert.AreEqual(0, value);
    }

    [TestMethod]
    public void PlanValue_WithConfiguredPlanRule_UsesRuleValue()
    {
        // Arrange
        var snapshot = Snapshot("Claude Code · Team", usedPercent: 20);
        var config = new FakeConfig(new Dictionary<string, string>
        {
            [PlanValueRules.ConfigKey("claude")] = "team=60",
        });

        // Act
        var value = ProviderPriority.PlanValue("claude", snapshot, config);

        // Assert
        Assert.AreEqual(60, value);
    }

    [TestMethod]
    public void PlanValue_WithCodexPlanRule_PrefersRuleOverLegacyValue()
    {
        // Arrange
        var snapshot = Snapshot("codex-lb · pooled", usedPercent: 20);
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["codex_lb_value"] = "75",
            [PlanValueRules.ConfigKey("codex-lb")] = "pooled=120",
        });

        // Act
        var value = ProviderPriority.PlanValue("codex-lb", snapshot, config);

        // Assert
        Assert.AreEqual(120, value);
    }

    [TestMethod]
    public void PlanValue_WithBayesdlTokenPlan_UsesDefaultPackageValue()
    {
        // Arrange
        var tokenStandard = Snapshot("BayesDL · Token Standard 标准包", usedPercent: 20);
        var codingPro = Snapshot("BayesDL · Coding Pro 进阶包", usedPercent: 20);

        // Act
        var tokenValue = ProviderPriority.PlanValue("bayesdl", tokenStandard);
        var codingValue = ProviderPriority.PlanValue("bayesdl", codingPro);

        // Assert
        Assert.AreEqual(20, tokenValue);
        Assert.AreEqual(40, codingValue);
    }

    [TestMethod]
    public void DisplayMonthlyValue_WithOfficialPlan_ReturnsPublicPrice()
    {
        var snapshot = Snapshot("MiMo · Standard", usedPercent: 20);

        var value = ProviderPriority.DisplayMonthlyValue("mimo", snapshot);

        Assert.AreEqual(16, value);
    }

    [TestMethod]
    public void DisplayMonthlyValue_WithLegacyEstimate_DoesNotPresentItAsPrice()
    {
        var snapshot = Snapshot("BayesDL · Token Standard 标准包", usedPercent: 20);

        var sortValue = ProviderPriority.PlanValue("bayesdl", snapshot);
        var displayValue = ProviderPriority.DisplayMonthlyValue("bayesdl", snapshot);

        Assert.AreEqual(20, sortValue);
        Assert.IsNull(displayValue);
    }

    [TestMethod]
    public void DisplayMonthlyValue_WithUserConfiguredRule_ReturnsConfiguredEstimate()
    {
        var snapshot = Snapshot("Qoder · Ultra", usedPercent: 20);
        var config = new FakeConfig(new Dictionary<string, string>
        {
            [PlanValueRules.ConfigKey("qoder")] = "ultra=275",
        });

        var value = ProviderPriority.DisplayMonthlyValue("qoder", snapshot, config);

        Assert.AreEqual(275, value);
    }

    [TestMethod]
    public void DisplayMonthlyValue_WithCodexAccounts_RequiresEveryAccountPriceToBeDisplaySafe()
    {
        var officialAccounts = Snapshot("codex-lb", usedPercent: 20);
        officialAccounts.Accounts.Add(new AccountInfo { Plan = "Plus" });
        officialAccounts.Accounts.Add(new AccountInfo { Plan = "Pro 5x" });
        var mixedEvidenceAccounts = Snapshot("codex-lb", usedPercent: 20);
        mixedEvidenceAccounts.Accounts.Add(new AccountInfo { Plan = "Plus" });
        mixedEvidenceAccounts.Accounts.Add(new AccountInfo { Plan = "Team" });

        var officialValue = ProviderPriority.DisplayMonthlyValue("codex-lb", officialAccounts);
        var mixedEvidenceValue = ProviderPriority.DisplayMonthlyValue("codex-lb", mixedEvidenceAccounts);

        Assert.AreEqual(120, officialValue);
        Assert.IsNull(mixedEvidenceValue);
    }

    [TestMethod]
    public void DisplayMonthlyValue_WithLegacyCodexConfiguredValue_TreatsUserValueAsDisplaySafe()
    {
        var snapshot = Snapshot("codex-lb", usedPercent: 20);
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["codex_lb_value"] = "75",
        });

        var value = ProviderPriority.DisplayMonthlyValue("codex-lb", snapshot, config);

        Assert.AreEqual(75, value);
    }

    [TestMethod]
    public void PlanValueRules_ParseSerialize_RoundTripsValidRowsAndSkipsInvalidRows()
    {
        // Act
        var rules = PlanValueRules.Parse("""
            plus=20
            bad
            pro:200
            negative=-1
            """);
        var serialized = PlanValueRules.Serialize(rules);

        // Assert
        Assert.AreEqual(2, rules.Count);
        Assert.AreEqual("plus", rules[0].Keyword);
        Assert.AreEqual(20, rules[0].Value);
        Assert.AreEqual("pro", rules[1].Keyword);
        Assert.AreEqual(200, rules[1].Value);
        Assert.AreEqual("plus=20" + Environment.NewLine + "pro=200", serialized);
    }

    [TestMethod]
    public void PlanValueRules_AreEquivalentToDefaults_MatchesOnlySameDefaultRows()
    {
        Assert.IsTrue(PlanValueRules.AreEquivalentToDefaults("claude", new[]
        {
            new ProviderPlanValueRule("team premium", 125),
            new ProviderPlanValueRule("team standard", 25),
            new ProviderPlanValueRule("max 20", 200),
            new ProviderPlanValueRule("max 5", 100),
            new ProviderPlanValueRule("max", 100),
            new ProviderPlanValueRule("team", 25),
            new ProviderPlanValueRule("pro", 20),
            new ProviderPlanValueRule("free", 0),
        }));

        Assert.IsFalse(PlanValueRules.AreEquivalentToDefaults("claude", new[]
        {
            new ProviderPlanValueRule("team premium", 125),
            new ProviderPlanValueRule("team standard", 25),
            new ProviderPlanValueRule("max 20", 200),
            new ProviderPlanValueRule("max 5", 100),
            new ProviderPlanValueRule("max", 100),
            new ProviderPlanValueRule("team", 60),
            new ProviderPlanValueRule("pro", 20),
            new ProviderPlanValueRule("free", 0),
        }));
    }

    [TestMethod]
    public void PlanValueRules_ShortAliasRequiresTokenBoundary()
    {
        Assert.IsNull(PlanValueRules.Match("opencodego", "Gold Enterprise"));
        Assert.AreEqual(10, PlanValueRules.Match("opencodego", "OpenCode-Go"));
    }

    [TestMethod]
    public void PlanValueRules_CanonicalPlanIdTakesPriorityOverAmbiguousDisplayName()
    {
        var rule = PlanValueRules.MatchRule(
            "codex",
            new ProviderPlanIdentity("chatgpt-plus", "Pro"));

        Assert.IsNotNull(rule);
        Assert.AreEqual("chatgpt-plus", rule.PlanId);
        Assert.AreEqual(20, rule.Value);
    }

    [TestMethod]
    public void Score_WithAvailabilityBelowEmptyThreshold_ExhaustsSubscription()
    {
        // Arrange
        // Available: 100 - 96 = 4%
        var snapshot = Snapshot("Claude Code · Pro", usedPercent: 96);
        
        // Threshold: 5%
        var configWithDefault = new FakeConfig(new Dictionary<string, string>
        {
            ["empty_threshold_pct"] = "5"
        });

        // Threshold: 3%
        var configWithCustom = new FakeConfig(new Dictionary<string, string>
        {
            ["empty_threshold_pct"] = "3"
        });

        // Act & Assert
        // With default 5.0 (read from config): 4% <= 5.0% -> Exhausted
        var scoreWithDefault = ProviderPriority.Score("claude", snapshot, configWithDefault);
        Assert.AreEqual(ProviderPriority.ExhaustedSubscriptionBucket, scoreWithDefault.Bucket);

        // With custom 3.0 (read from config): 4% > 3.0% -> Usable
        var scoreWithCustom = ProviderPriority.Score("claude", snapshot, configWithCustom);
        Assert.AreEqual(ProviderPriority.UsableSubscriptionBucket, scoreWithCustom.Bucket);
    }

    [TestMethod]
    public void Score_WithPairedWindows_RequiresBothWindowsToHaveCapacity()
    {
        // Arrange: 5h has capacity, but the weekly pool is empty.
        var snapshot = Snapshot("codex-lb", usedPercent: 0);
        snapshot.Secondary = new RateWindow
        {
            Label = "Weekly",
            UsedPercent = 100,
        };

        // Act
        var score = ProviderPriority.Score("codex-lb", snapshot);

        // Assert
        Assert.AreEqual(ProviderPriority.ExhaustedSubscriptionBucket, score.Bucket);
        Assert.AreEqual(0, score.Availability);
    }

    [TestMethod]
    public void Score_WithInformationalSecondary_IgnoresMetricForAvailabilityAndResetPriority()
    {
        var snapshot = Snapshot("Claude Code · Pro", usedPercent: 20);
        snapshot.Secondary = new RateWindow
        {
            Label = "Requests today",
            Kind = RateWindowKind.Informational,
            UsedPercent = 100,
            ValueText = "1,234 requests",
            ResetsAt = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
            WindowMinutes = 5,
        };

        var score = ProviderPriority.Score("claude", snapshot);

        Assert.AreEqual(ProviderPriority.UsableSubscriptionBucket, score.Bucket);
        Assert.AreEqual(80, score.Availability);
        Assert.AreEqual(ProviderPriority.NoResetTier, score.ResetTier);
    }

    [TestMethod]
    public void ProviderAvailability_WithOnlyInformationalMetrics_ReturnsNeutralAvailability()
    {
        var snapshot = Snapshot("OpenAI API", usedPercent: 100);
        snapshot.Primary.Kind = RateWindowKind.Informational;
        snapshot.Primary.ValueText = "$12.34 spent";

        var availability = Quota.ProviderAvailability("openai", snapshot);

        Assert.AreEqual(100, availability);
    }

    [TestMethod]
    public void Score_WithGeneratedAntigravityInstanceId_UsesPriorityModelQuotaAvailability()
    {
        var snapshot = Snapshot("Antigravity · Pro", usedPercent: 100);
        snapshot.ModelQuotas.Add(new ModelQuota
        {
            Family = "Gemini",
            Model = "Gemini 3.1 Pro",
            RemainingPercent = 80,
        });

        var score = ProviderPriority.Score("antigravity-1234abcd", snapshot);

        Assert.AreEqual(ProviderPriority.UsableSubscriptionBucket, score.Bucket);
        Assert.AreEqual(80, score.Availability);
    }

    [TestMethod]
    public void Score_WithUnprefixedAntigravityInstanceId_UsesConfiguredInstanceType()
    {
        var snapshot = Snapshot("Antigravity · Pro", usedPercent: 100);
        snapshot.ModelQuotas.Add(new ModelQuota
        {
            Family = "Gemini",
            Model = "Gemini 3.1 Pro",
            RemainingPercent = 80,
        });
        var config = new FakeConfigService(new ProviderInstance("work", "antigravity", "Work Antigravity"));

        var score = ProviderPriority.Score("work", snapshot, config);

        Assert.AreEqual(ProviderPriority.UsableSubscriptionBucket, score.Bucket);
        Assert.AreEqual(80, score.Availability);
    }

    [TestMethod]
    public void Score_WithUnprefixedPayAsYouGoInstanceId_UsesConfiguredInstanceType()
    {
        var snapshot = Snapshot("DeepSeek", usedPercent: 0);
        var config = new FakeConfigService(new ProviderInstance("work", "deepseek", "Work DeepSeek"));

        var score = ProviderPriority.Score("work", snapshot, config);

        Assert.AreEqual(ProviderPriority.PayAsYouGoBucket, score.Bucket);
        Assert.IsTrue(score.IsPayAsYouGo);
    }

    [TestMethod]
    public void Score_WithShortWindowReset_StoresSoonResetPriority()
    {
        // Arrange
        var snapshot = Snapshot("Claude Code · Pro", usedPercent: 100);
        snapshot.Primary.Label = "5h Pool";
        snapshot.Primary.ResetsAt = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O");
        snapshot.Primary.WindowMinutes = 300;
        snapshot.Secondary = new RateWindow
        {
            Label = "7d Pool",
            UsedPercent = 10,
            ResetsAt = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
            WindowMinutes = 10_080,
        };

        // Act
        var score = ProviderPriority.Score("claude", snapshot);

        // Assert
        Assert.AreEqual(ProviderPriority.ShortResetTier, score.ResetTier);
        Assert.IsTrue(score.ResetMinutesUntil is >= 0 and <= 10);
    }

    [TestMethod]
    public void Score_WithShortWindowCadenceButNoExactReset_StillUsesShortResetPriority()
    {
        // Arrange
        var snapshot = Snapshot("Claude Code · Pro", usedPercent: 0);
        snapshot.Primary.Label = "5h Pool";
        snapshot.Primary.ResetsAt = null;
        snapshot.Primary.WindowMinutes = 300;
        snapshot.Secondary = new RateWindow
        {
            Label = "7d Pool",
            UsedPercent = 10,
            ResetsAt = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
            WindowMinutes = 10_080,
        };

        // Act
        var score = ProviderPriority.Score("claude", snapshot);

        // Assert
        Assert.AreEqual(ProviderPriority.ShortResetTier, score.ResetTier);
        Assert.IsTrue(double.IsPositiveInfinity(score.ResetMinutesUntil));
    }

    [TestMethod]
    public void Score_WithAdditionalShortWindowReset_StoresSoonResetPriority()
    {
        // Arrange: imported providers can expose model-specific windows after the standard rows.
        var snapshot = Snapshot("Codex · Pro", usedPercent: 20);
        snapshot.Primary.Label = "Weekly Pool";
        snapshot.Primary.ResetsAt = DateTimeOffset.UtcNow.AddDays(4).ToString("O");
        snapshot.Primary.WindowMinutes = 10_080;
        snapshot.AdditionalWindows.Add(new RateWindow
        {
            Label = "Codex Spark 5-hour",
            UsedPercent = 100,
            ResetsAt = DateTimeOffset.UtcNow.AddMinutes(12).ToString("O"),
            WindowMinutes = 300,
        });

        // Act
        var score = ProviderPriority.Score("codex", snapshot);

        // Assert
        Assert.AreEqual(ProviderPriority.ShortResetTier, score.ResetTier);
        Assert.IsTrue(score.ResetMinutesUntil is >= 0 and <= 20);
    }

    [TestMethod]
    public void Score_WithExhaustedAdditionalWindow_DoesNotDemoteWholeProviderByDefault()
    {
        // Arrange: model-specific extra limits are visible and sortable by reset,
        // but they do not necessarily mean the entire provider is unavailable.
        var snapshot = Snapshot("Codex · Pro", usedPercent: 20);
        snapshot.AdditionalWindows.Add(new RateWindow
        {
            Label = "Codex Spark Weekly",
            UsedPercent = 100,
            WindowMinutes = 10_080,
        });

        // Act
        var score = ProviderPriority.Score("codex", snapshot);

        // Assert
        Assert.AreEqual(ProviderPriority.UsableSubscriptionBucket, score.Bucket);
        Assert.AreEqual(80, score.Availability);
    }

    [TestMethod]
    public void Score_WithAvailabilityGatingOptionalWindow_DemotesWhenThatWindowIsEmpty()
    {
        // Arrange
        var snapshot = Snapshot("Claude Code · Pro", usedPercent: 20);
        snapshot.AdditionalWindows.Add(new RateWindow
        {
            Label = "Shared weekly pool",
            UsedPercent = 100,
            WindowMinutes = 10_080,
            CountsForAvailability = true,
        });

        // Act
        var score = ProviderPriority.Score("claude", snapshot);

        // Assert
        Assert.AreEqual(ProviderPriority.ExhaustedSubscriptionBucket, score.Bucket);
        Assert.AreEqual(0, score.Availability);
    }

    [TestMethod]
    public void Score_WithMonthlyResetLabel_KeepsLongResetPriority()
    {
        // Arrange
        var snapshot = Snapshot("Qoder", usedPercent: 100);
        snapshot.Primary.Label = "Plan Credits";
        snapshot.Primary.ResetsAt = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O");

        // Act
        var score = ProviderPriority.Score("qoder", snapshot);

        // Assert
        Assert.AreEqual(ProviderPriority.LongResetTier, score.ResetTier);
    }

    [TestMethod]
    public void ConfigService_WithExistingConfigMissingEmptyThreshold_SeedsGlobalDefault()
    {
        // Arrange: existing installs may have config.json from before this setting existed.
        var dir = Path.Combine(Path.GetTempPath(), "QuotaLens.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), "{}");

        try
        {
            // Act
            var config = new ConfigService(dir);

            // Assert
            Assert.AreEqual("5", config.Get("empty_threshold_pct"));
            Assert.IsTrue(config.GetBool(ProviderSortPolicy.DeprioritizeEmptyProvidersConfigKey));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static ProviderSnapshot Snapshot(string name, double usedPercent)
    {
        return new ProviderSnapshot
        {
            ProviderId = name,
            Name = name,
            Primary = new RateWindow
            {
                Label = "Quota",
                UsedPercent = usedPercent,
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private sealed class FakeConfig(IReadOnlyDictionary<string, string> values) : IConfig
    {
        public string Get(string key, string fallback = "")
            => values.TryGetValue(key, out var value) ? value : fallback;

        public string GetScoped(string instanceId, string key, string fallback = "")
            => values.TryGetValue($"{instanceId}.{key}", out var scoped)
                ? scoped
                : Get(key, fallback);

        public bool HasScoped(string instanceId, string key)
            => values.ContainsKey($"{instanceId}.{key}");

        public bool GetBool(string key, bool fallback = false)
            => values.TryGetValue(key, out var value)
                ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                : fallback;
    }

    private sealed class FakeConfigService(ProviderInstance instance) : IConfigService
    {
        public IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>();
        public IReadOnlyList<ProviderInstance> Instances { get; } = new[] { instance };
        public double RefreshMs => 1_800_000;

        public string Get(string key, string fallback = "") => fallback;
        public string GetScoped(string instanceId, string key, string fallback = "") => fallback;
        public bool HasScoped(string instanceId, string key) => false;
        public bool GetBool(string key, bool fallback = false) => fallback;
        public void Set(string key, string value) { }
        public void SetMany(IReadOnlyDictionary<string, string> values) { }
        public void Remove(string key) { }
        public Task SaveAsync() => Task.CompletedTask;
        public int ImportEnvironment(string instanceId) => 0;
        public string? ImportEnvironmentField(string instanceId, string fieldKey) => null;
        public ProviderInstance AddInstance(string providerType) => instance;
        public void RemoveInstance(string id) { }
    }
}
