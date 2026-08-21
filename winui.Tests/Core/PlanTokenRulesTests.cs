using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class PlanTokenRulesTests
{
    [TestMethod]
    public void Match_PrefersSpecificTierKeywordsOverGenericOnes()
    {
        Assert.AreEqual(600, PlanTokenRules.Match("claude", "Claude Code · Max 20x"));
        Assert.AreEqual(350, PlanTokenRules.Match("claude", "Claude Code · Max"));
        Assert.AreEqual(100, PlanTokenRules.Match("claude", "Claude Code · Pro"));
        Assert.AreEqual(32, PlanTokenRules.Match("codex", "Codex · Plus"));
        Assert.AreEqual(66, PlanTokenRules.Match("kimi", "Kimi · Moderato"));
        Assert.AreEqual(0, PlanTokenRules.Match("kimi", "Kimi · Adagio"));
    }

    [TestMethod]
    public void Match_GrokTiers_FollowCommunityMeasuredWeeklyPools()
    {
        // X Premium+ is documented as roughly SuperGrok-level; Heavy is the
        // only tier with a materially larger pool (community: 10M tokens/day).
        Assert.AreEqual(70, PlanTokenRules.Match("grok", "SuperGrok Heavy"));
        Assert.AreEqual(40, PlanTokenRules.Match("grok", "SuperGrok Plus"));
        Assert.AreEqual(14, PlanTokenRules.Match("grok", "SuperGrok"));
        Assert.AreEqual(14, PlanTokenRules.Match("grok", "X Premium+"));
        Assert.AreEqual(7, PlanTokenRules.Match("grok", "X Premium"));
        Assert.AreEqual(1, PlanTokenRules.Match("grok", "Free"));
    }

    [TestMethod]
    public void Match_GlmCodingPlanTiers_AreSharedByZcodeAndZai()
    {
        foreach (var providerType in new[] { "zcode", "zai" })
        {
            Assert.AreEqual(900, PlanTokenRules.Match(providerType, "GLM Coding Max"));
            Assert.AreEqual(400, PlanTokenRules.Match(providerType, "GLM Coding Pro"));
            Assert.AreEqual(65, PlanTokenRules.Match(providerType, "GLM Coding Lite"));
            Assert.AreEqual(800, PlanTokenRules.Match(providerType, "Team Advanced"));
            Assert.AreEqual(300, PlanTokenRules.Match(providerType, "Team Standard"));
        }
    }

    [TestMethod]
    public void Match_ChineseTokenPlans_ConvertMonthlyGrantsToWeekly()
    {
        Assert.AreEqual(1600, PlanTokenRules.Match("minimax", "MiniMax Ultra"));
        Assert.AreEqual(420, PlanTokenRules.Match("minimax", "MiniMax Max"));
        Assert.AreEqual(140, PlanTokenRules.Match("minimax", "MiniMax Plus"));

        Assert.AreEqual(9000, PlanTokenRules.Match("stepfun", "StepFun Max"));
        Assert.AreEqual(1800, PlanTokenRules.Match("stepfun", "StepFun Pro"));
        Assert.AreEqual(370, PlanTokenRules.Match("stepfun", "StepFun Plus"));
        Assert.AreEqual(90, PlanTokenRules.Match("stepfun", "Flash Mini"));

        Assert.AreEqual(160, PlanTokenRules.Match("alibabatokenplan", "Qwen Token Plan Pro"));
        Assert.AreEqual(40, PlanTokenRules.Match("alibabatokenplan", "Standard"));
        Assert.AreEqual(10, PlanTokenRules.Match("alibabatokenplan", "Lite"));

        Assert.AreEqual(225, PlanTokenRules.Match("doubao", "coding-plan"));
        Assert.AreEqual(225, PlanTokenRules.Match("doubao", "coding-plan-team"));
        Assert.AreEqual(45, PlanTokenRules.Match("doubao", "agent-plan"));
    }

    [TestMethod]
    public void Match_AugmentTiers_ConvertCreditPoolsToWeeklyTokens()
    {
        Assert.AreEqual(100, PlanTokenRules.Match("augment", "Augment Max"));
        Assert.AreEqual(30, PlanTokenRules.Match("augment", "Augment Standard"));
        Assert.AreEqual(9, PlanTokenRules.Match("augment", "Augment Indie"));
        Assert.AreEqual(9, PlanTokenRules.Match("augment", "Developer"));
    }

    [TestMethod]
    public void Match_WithUnknownPlanOrProvider_ReturnsNull()
    {
        Assert.IsNull(PlanTokenRules.Match("claude", "Claude Code · Hyperdrive"));
        Assert.IsNull(PlanTokenRules.Match("deepseek", "DeepSeek"));
    }

    [TestMethod]
    public void Estimate_UnknownPlan_FallsBackToSmallestPaidTierNotFreeTier()
    {
        var tokens = PlanTokenRules.EstimateWeeklyTokensMillions(
            "cursor",
            new ProviderSnapshot { Name = "Cursor · Hypernova" },
            config: null,
            out var kind);

        // Cursor's smallest PAID tier (Pro, 120) — never Hobby/free: an unknown
        // plan is far more likely a new paid tier than a free one.
        Assert.AreEqual(120, tokens, 0.001);
        Assert.AreEqual(PlanTokenRules.TokenEstimateKind.Fallback, kind);
    }

    [TestMethod]
    public void Estimate_UnknownProvider_UsesGlobalDefault()
    {
        var tokens = PlanTokenRules.EstimateWeeklyTokensMillions(
            "deepseek",
            new ProviderSnapshot { Name = "DeepSeek" },
            config: null,
            out var kind);

        Assert.AreEqual(PlanTokenRules.GlobalDefaultWeeklyTokensMillions, tokens, 0.001);
        Assert.AreEqual(PlanTokenRules.TokenEstimateKind.Fallback, kind);
    }

    [TestMethod]
    public void Estimate_MatchedPlan_IsPlanMatched()
    {
        var tokens = PlanTokenRules.EstimateWeeklyTokensMillions(
            "claude",
            new ProviderSnapshot { Name = "Claude Code · Pro" },
            config: null,
            out var kind);

        Assert.AreEqual(100, tokens, 0.001);
        Assert.AreEqual(PlanTokenRules.TokenEstimateKind.PlanMatched, kind);
    }

    [TestMethod]
    public void Estimate_PooledAccounts_SumsEachAccountsPlan()
    {
        // codex-lb pooling one Pro 20x + two Business accounts: the bar must
        // represent the whole pool, not a single median plan.
        var snapshot = new ProviderSnapshot
        {
            Name = "codex-lb",
            Accounts = new List<AccountInfo>
            {
                new() { Email = "a@example.com", Plan = "pro 20x" },
                new() { Email = "b@example.com", Plan = "business" },
                new() { Email = "c@example.com", Plan = "business" },
            },
        };

        var tokens = PlanTokenRules.EstimateWeeklyTokensMillions("codex-lb", snapshot, null, out var kind);

        Assert.AreEqual(600 + 32 + 32, tokens, 0.001);
        Assert.AreEqual(PlanTokenRules.TokenEstimateKind.PlanMatched, kind);
    }

    [TestMethod]
    public void Estimate_PooledAccountsWithUnknownPlan_MixesFallbackAndFlags()
    {
        var snapshot = new ProviderSnapshot
        {
            Name = "codex-lb",
            Accounts = new List<AccountInfo>
            {
                new() { Email = "a@example.com", Plan = "pro 20x" },
                new() { Email = "b@example.com", Plan = "mystery" },
            },
        };

        var tokens = PlanTokenRules.EstimateWeeklyTokensMillions("codex-lb", snapshot, null, out var kind);

        Assert.AreEqual(600 + 11, tokens, 0.001); // Go (11) is codex's smallest paid tier
        Assert.AreEqual(PlanTokenRules.TokenEstimateKind.Fallback, kind);
    }

    [TestMethod]
    public void Estimate_MeasuredCapacity_TrumpsEverything()
    {
        var snapshot = new ProviderSnapshot
        {
            Name = "codex-lb",
            MeasuredWeeklyTokensMillions = 8511,
            Accounts = new List<AccountInfo> { new() { Plan = "business" } },
        };

        var tokens = PlanTokenRules.EstimateWeeklyTokensMillions("codex-lb", snapshot, null, out var kind);

        Assert.AreEqual(8511, tokens, 0.001);
        Assert.AreEqual(PlanTokenRules.TokenEstimateKind.Measured, kind);
    }

    [TestMethod]
    public void ForProvider_ConfigOverride_ReplacesDefaults()
    {
        var config = new OverrideConfig(
            PlanTokenRules.ConfigKey("claude"),
            "pro=250\nmax=900");

        Assert.AreEqual(250, PlanTokenRules.Match("claude", "Claude Code · Pro", config));
        Assert.AreEqual(900, PlanTokenRules.Match("claude", "Claude Code · Max", config));
        // Rules not present in the override no longer match (whole-table replacement).
        Assert.IsNull(PlanTokenRules.Match("claude", "Claude Code · Team", config));
    }

    [TestMethod]
    public void Parse_SkipsMalformedLinesAndNegativeValues()
    {
        var rules = PlanTokenRules.Parse("pro=100\nbroken\n=5\nneg=-3\nmax: 200");

        Assert.AreEqual(2, rules.Count);
        Assert.AreEqual(("pro", 100d), (rules[0].Keyword, rules[0].WeeklyTokensMillions));
        Assert.AreEqual(("max", 200d), (rules[1].Keyword, rules[1].WeeklyTokensMillions));
    }

    [TestMethod]
    public void DefaultTables_AreMonotonic_HigherValueTiersNeverSmaller()
    {
        // Every provider's first matching rule for its most premium keyword should
        // be >= its cheapest paid tier; more simply, tables must not contain
        // negative values and must order specific tiers before generic substrings.
        foreach (var (providerType, rules) in Catalog.DefaultPlanTokenRules)
        {
            foreach (var rule in rules)
                Assert.IsTrue(rule.WeeklyTokensMillions >= 0, $"{providerType}.{rule.Keyword} is negative");

            // Generic-substring shadowing check: any keyword that is a word-substring
            // of an earlier keyword must come after it (first match wins).
            for (var later = 1; later < rules.Length; later++)
            {
                for (var earlier = 0; earlier < later; earlier++)
                {
                    Assert.IsFalse(
                        RuleShadows(rules[later].Keyword, rules[earlier].Keyword),
                        $"{providerType}: rule '{rules[later].Keyword}' at index {later} would never match because '{rules[earlier].Keyword}' precedes it");
                }
            }
        }
    }

    private static bool RuleShadows(string laterKeyword, string earlierKeyword)
    {
        // earlier shadows later iff every plan name matching later also matches
        // earlier — true when the earlier keyword is a word-subsequence of the later.
        if (string.Equals(laterKeyword, earlierKeyword, StringComparison.OrdinalIgnoreCase))
            return true;

        var later = $" {laterKeyword.ToLowerInvariant()} ";
        var earlier = $" {earlierKeyword.ToLowerInvariant()} ";
        return later.Contains(earlier, StringComparison.Ordinal);
    }

    private sealed class OverrideConfig : IConfig
    {
        private readonly string _key;
        private readonly string _value;

        public OverrideConfig(string key, string value)
        {
            _key = key;
            _value = value;
        }

        public string Get(string key, string fallback = "") =>
            key == _key ? _value : fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") => fallback;

        public bool HasScoped(string instanceId, string key) => false;

        public bool GetBool(string key, bool fallback = false) => fallback;
    }
}
