using System.Globalization;

namespace QuotaLens.Core;

/// <summary>
/// Resolves a plan's estimated weekly token allowance (millions, cache-inclusive)
/// for proportional usage-timeline sizing. Matching mirrors PlanValueRules:
/// first rule whose keyword appears (word-boundary) in the plan name wins, so
/// tables list specific tiers before generic ones. Users can override per
/// provider via the config key <c>plan_token_rules.&lt;providerType&gt;</c> with
/// one <c>keyword=millions</c> entry per line.
/// </summary>
public static class PlanTokenRules
{
    private const string KeyPrefix = "plan_token_rules.";

    /// <summary>
    /// Fallback when a provider has no token table at all. Deliberately modest:
    /// generosity at the same price point spans ~40x across platforms, so an
    /// unknown provider gets a small bar rather than a misleading large one.
    /// </summary>
    public const double GlobalDefaultWeeklyTokensMillions = 15;

    public static string ConfigKey(string providerType) => $"{KeyPrefix}{providerType}";

    public static IReadOnlyList<ProviderPlanTokenRule> ForProvider(string providerType, IConfig? config = null)
    {
        var configured = config?.Get(ConfigKey(providerType));
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var parsed = Parse(configured);
            if (parsed.Count > 0)
                return parsed;
        }

        return Catalog.DefaultPlanTokenRules.TryGetValue(providerType, out var defaults)
            ? defaults
            : Array.Empty<ProviderPlanTokenRule>();
    }

    public static double? Match(string providerType, ProviderPlanIdentity identity, IConfig? config = null)
    {
        var lookupText = !string.IsNullOrWhiteSpace(identity.PlanName)
            ? identity.PlanName
            : identity.PlanId;
        return string.IsNullOrWhiteSpace(lookupText)
            ? null
            : Match(providerType, lookupText!, config);
    }

    public static double? Match(string providerType, string planName, IConfig? config = null)
    {
        var normalized = PlanValueRules.NormalizeMatchText(planName);
        foreach (var rule in ForProvider(providerType, config))
        {
            if (PlanValueRules.KeywordMatches(normalized, rule.Keyword))
                return rule.WeeklyTokensMillions;
        }

        return null;
    }

    /// <summary>How the weekly-token figure for a timeline segment was obtained.</summary>
    public enum TokenEstimateKind
    {
        /// <summary>Back-computed from real token counts the provider reports.</summary>
        Measured,
        /// <summary>Plan (or every pooled account's plan) matched the rules table.</summary>
        PlanMatched,
        /// <summary>Plan not recognized; smallest paid tier or global default used.</summary>
        Fallback,
    }

    /// <summary>
    /// Weekly-token figure for a snapshot, in preference order: the provider's own
    /// measured capacity → sum of pooled account plans (load balancers aggregate
    /// several subscriptions; one bar must represent all of them) → the snapshot's
    /// plan identity → smallest paid tier / global default.
    /// </summary>
    public static double EstimateWeeklyTokensMillions(
        string providerType,
        ProviderSnapshot snapshot,
        IConfig? config,
        out TokenEstimateKind kind,
        bool preferMeasured = true)
    {
        // Callers comparing ACROSS providers pass preferMeasured: false — a measured
        // figure reflects one user's cache-heavy workload and is not on the same
        // axis as the normalized community estimates used for everyone else.
        if (preferMeasured && snapshot.MeasuredWeeklyTokensMillions is > 0)
        {
            kind = TokenEstimateKind.Measured;
            return snapshot.MeasuredWeeklyTokensMillions.Value;
        }

        if (snapshot.Accounts.Count > 0)
        {
            var total = 0.0;
            var anyMatched = false;
            var anyFallback = false;
            foreach (var account in snapshot.Accounts)
            {
                var matched = string.IsNullOrWhiteSpace(account.Plan)
                    ? null
                    : Match(providerType, account.Plan!, config);
                if (matched.HasValue)
                {
                    total += matched.Value;
                    anyMatched = true;
                }
                else
                {
                    total += SmallestPaidTierOrDefault(providerType, config);
                    anyFallback = true;
                }
            }

            if (anyMatched)
            {
                kind = anyFallback ? TokenEstimateKind.Fallback : TokenEstimateKind.PlanMatched;
                return total;
            }
        }

        var identity = ProviderSnapshotIdentity.PlanIdentity(providerType, snapshot);
        var identityMatch = Match(providerType, identity, config);
        if (identityMatch.HasValue)
        {
            kind = TokenEstimateKind.PlanMatched;
            return identityMatch.Value;
        }

        kind = TokenEstimateKind.Fallback;
        // Pool of N unrecognized accounts still deserves N × the fallback.
        var multiplier = Math.Max(1, snapshot.Accounts.Count);
        return SmallestPaidTierOrDefault(providerType, config) * multiplier;
    }

    // Free/promo tiers must not become the "unknown plan" fallback: an unrecognized
    // plan is far more likely a new paid tier than a free one (Cursor's is Pro, not
    // Hobby), and a free-sized bar for a paid pool is exactly the misread the chart
    // exists to prevent.
    private static readonly string[] FreeTierKeywords =
    {
        "free", "hobby", "community", "trial", "adagio", "no plan", "base",
        "individual", "payg", "pay as you go",
    };

    private static double SmallestPaidTierOrDefault(string providerType, IConfig? config)
    {
        var smallestPaidTier = ForProvider(providerType, config)
            .Where(rule => rule.WeeklyTokensMillions > 0)
            .Where(rule => !FreeTierKeywords.Contains(rule.Keyword.Trim().ToLowerInvariant()))
            .Select(rule => (double?)rule.WeeklyTokensMillions)
            .Min();
        return smallestPaidTier ?? GlobalDefaultWeeklyTokensMillions;
    }

    internal static List<ProviderPlanTokenRule> Parse(string text)
    {
        var rules = new List<ProviderPlanTokenRule>();
        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var separator = line.LastIndexOf('=');
            if (separator < 0)
                separator = line.LastIndexOf(':');
            if (separator <= 0 || separator >= line.Length - 1)
                continue;

            var keyword = line[..separator].Trim();
            var valueText = line[(separator + 1)..].Trim();
            if (keyword.Length == 0
                || !double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || value < 0)
            {
                continue;
            }

            rules.Add(new ProviderPlanTokenRule(keyword, value));
        }

        return rules;
    }
}
