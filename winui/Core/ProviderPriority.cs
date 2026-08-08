using System.Globalization;

namespace QuotaLens.Core;

public readonly record struct ProviderPriorityScore(
    int Bucket,
    double PlanValue,
    double Availability,
    bool IsPayAsYouGo,
    int ResetTier = ProviderPriority.NoResetTier,
    double ResetMinutesUntil = double.PositiveInfinity);

public readonly record struct ProviderPriorityCandidate(
    string Id,
    ProviderSnapshot Snapshot,
    ProviderPriorityScore Score);

public sealed record ProviderPlanDisplay(
    double MonthlyValue,
    string Currency,
    string Cadence,
    string? SeatBasis,
    string? PriceQualifier,
    string? OfficialSource,
    string? LastVerifiedAt,
    ProviderPlanEvidence Evidence)
{
    public string FormatMonthlyPrice()
    {
        var symbol = Currency.Equals("USD", StringComparison.OrdinalIgnoreCase) ? "$" : $"{Currency} ";
        var prefix = Evidence == ProviderPlanEvidence.UserConfigured
            ? "est. "
            : PriceQualifier?.Contains("minimum", StringComparison.OrdinalIgnoreCase) == true
                || PriceQualifier?.Contains("starts at", StringComparison.OrdinalIgnoreCase) == true
                    ? "from "
                    : PriceQualifier?.StartsWith("introductory", StringComparison.OrdinalIgnoreCase) == true
                        ? "intro "
                        : "";
        var basis = SeatBasis?.Trim().ToLowerInvariant() switch
        {
            "user" => "/user/mo",
            "seat" => "/seat/mo",
            var value when value?.StartsWith("workspace", StringComparison.Ordinal) == true => "/workspace/mo",
            _ => "/mo",
        };
        return $"{prefix}{symbol}{MonthlyValue:0.##}{basis}";
    }
}

/// <summary>
/// Centralized provider priority policy: paid subscription value is the primary
/// ordering signal, availability only breaks ties, and pay-as-you-go APIs are
/// kept as last-resort options because they add marginal cost.
/// </summary>
public static class ProviderPriority
{
    public const int ErrorOrPendingBucket = 0;
    public const int PayAsYouGoBucket = 1;
    public const int ExhaustedSubscriptionBucket = 2;
    public const int UsableSubscriptionBucket = 3;
    public const int ShortResetTier = 0;
    public const int MediumResetTier = 1;
    public const int LongResetTier = 2;
    public const int NoResetTier = 3;

    public static ProviderPriorityScore Score(
        string instanceId,
        ProviderSnapshot? snapshot,
        IConfig? config = null)
    {
        if (snapshot is null || !string.IsNullOrEmpty(snapshot.Error))
            return new ProviderPriorityScore(ErrorOrPendingBucket, 0, 0, IsPayAsYouGo: false);

        if (snapshot.EntitlementStatus == EntitlementStatus.Expired)
        {
            return new ProviderPriorityScore(
                ExhaustedSubscriptionBucket,
                0,
                0,
                IsPayAsYouGo: false);
        }

        var planValue = PlanValue(instanceId, snapshot, config);
        var availability = Quota.ProviderAvailability(instanceId, snapshot, config);
        var reset = ResetPriority(instanceId, snapshot, config);

        if (planValue < 0)
            return new ProviderPriorityScore(
                PayAsYouGoBucket,
                planValue,
                availability,
                IsPayAsYouGo: true,
                reset.Tier,
                reset.MinutesUntil);

        double threshold = 5.0;
        if (config is not null)
        {
            var rawThreshold = config.Get("empty_threshold_pct", "5");
            if (double.TryParse(rawThreshold, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                threshold = parsed;
            }
        }

        var bucket = availability > threshold
            ? UsableSubscriptionBucket
            : ExhaustedSubscriptionBucket;
        return new ProviderPriorityScore(
            bucket,
            planValue,
            availability,
            IsPayAsYouGo: false,
            reset.Tier,
            reset.MinutesUntil);
    }

    public static IReadOnlyList<ProviderPriorityCandidate> RankUsableSubscriptions(
        IEnumerable<(string Id, ProviderSnapshot Snapshot)> snapshots,
        IConfig? config = null)
    {
        return snapshots
            .Select(x => new ProviderPriorityCandidate(x.Id, x.Snapshot, Score(x.Id, x.Snapshot, config)))
            .Where(x => x.Score.Bucket == UsableSubscriptionBucket)
            .OrderByDescending(x => x.Score.PlanValue)
            .ThenByDescending(x => x.Score.Availability)
            .ThenBy(x => x.Snapshot.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static double PlanValue(string instanceId, ProviderSnapshot snapshot, IConfig? config = null)
    {
        var providerType = Catalog.ProviderTypeForInstance(instanceId, config);

        if (providerType == "codex-lb" && TryAggregateCodexAccountPlanValue(snapshot, config, out var accountValue))
            return accountValue;

        var planIdentity = ProviderSnapshotIdentity.PlanIdentity(providerType, snapshot);
        if (!planIdentity.IsEmpty)
        {
            var configuredRuleValue = PlanValueRules.MatchConfigured(providerType, planIdentity, config);
            if (configuredRuleValue.HasValue)
                return configuredRuleValue.Value;
        }

        if (providerType == "codex-lb" && TryConfiguredCodexValue(config, out var configuredValue))
            return configuredValue;

        if (!planIdentity.IsEmpty)
        {
            var ruleValue = PlanValueRules.Match(providerType, planIdentity, config);
            if (ruleValue.HasValue)
                return ruleValue.Value;
        }

        return Catalog.PayAsYouGoProviderTypes.Contains(providerType)
            ? -1
            : 0;
    }

    /// <summary>
    /// Returns a monthly USD value that is safe to present as a price. Legacy
    /// estimates remain available to <see cref="PlanValue"/> for sorting, but are
    /// never promoted into a user-visible pricing claim.
    /// </summary>
    public static double? DisplayMonthlyValue(
        string instanceId,
        ProviderSnapshot snapshot,
        IConfig? config = null) =>
        DisplayPlanValue(instanceId, snapshot, config)?.MonthlyValue;

    public static ProviderPlanDisplay? DisplayPlanValue(
        string instanceId,
        ProviderSnapshot snapshot,
        IConfig? config = null)
    {
        var providerType = Catalog.ProviderTypeForInstance(instanceId, config);
        var planIdentity = ProviderSnapshotIdentity.PlanIdentity(providerType, snapshot);

        if (providerType == "codex-lb"
            && TryAggregateCodexAccountDisplayValue(snapshot, config, out var accountDisplay))
        {
            return accountDisplay;
        }

        var configuredRule = planIdentity.IsEmpty
            ? null
            : PlanValueRules.MatchConfiguredRule(providerType, planIdentity, config);
        if (configuredRule is not null && IsDisplaySafe(configuredRule))
            return DisplayFor(configuredRule);

        if (providerType == "codex-lb" && TryConfiguredCodexValue(config, out var configuredValue))
        {
            return new ProviderPlanDisplay(
                configuredValue,
                "USD",
                "monthly",
                null,
                "user configured estimate",
                null,
                null,
                ProviderPlanEvidence.UserConfigured);
        }

        var rule = planIdentity.IsEmpty
            ? null
            : PlanValueRules.MatchRule(providerType, planIdentity, config);
        return rule is not null && IsDisplaySafe(rule) ? DisplayFor(rule) : null;
    }

    private static bool TryAggregateCodexAccountPlanValue(
        ProviderSnapshot snapshot,
        IConfig? config,
        out double value)
    {
        value = 0.0;

        if (snapshot.Accounts.Count == 0)
            return false;

        var matched = 0;
        foreach (var account in snapshot.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Plan))
                continue;

            var planValue = PlanValueRules.MatchIncludingDefaults("codex-lb", account.Plan, config);
            if (!planValue.HasValue)
                continue;

            value += planValue.Value;
            matched++;
        }

        return matched > 0;
    }

    private static bool TryAggregateCodexAccountDisplayValue(
        ProviderSnapshot snapshot,
        IConfig? config,
        out ProviderPlanDisplay? display)
    {
        display = null;

        if (snapshot.Accounts.Count == 0)
            return false;

        var rules = new List<ProviderPlanValueRule>();

        foreach (var account in snapshot.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Plan))
                return false;

            var rule = PlanValueRules.MatchIncludingDefaultsRule("codex-lb", account.Plan, config);
            if (rule is null || !IsDisplaySafe(rule))
                return false;

            rules.Add(rule);
        }

        display = new ProviderPlanDisplay(
            rules.Sum(rule => rule.Value),
            "USD",
            "monthly",
            null,
            "combined account subscriptions",
            rules.Select(rule => rule.OfficialSource).Distinct(StringComparer.Ordinal).Count() == 1
                ? rules[0].OfficialSource
                : null,
            rules.Select(rule => rule.LastVerifiedAt).Distinct(StringComparer.Ordinal).Count() == 1
                ? rules[0].LastVerifiedAt
                : null,
            rules.All(rule => rule.Evidence == ProviderPlanEvidence.Official)
                ? ProviderPlanEvidence.Official
                : ProviderPlanEvidence.UserConfigured);
        return true;
    }

    private static ProviderPlanDisplay DisplayFor(ProviderPlanValueRule rule) => new(
        rule.Value,
        rule.Currency ?? "USD",
        rule.Cadence ?? "monthly",
        rule.SeatBasis,
        rule.PriceQualifier,
        rule.OfficialSource,
        rule.LastVerifiedAt,
        rule.Evidence);

    private static bool TryConfiguredCodexValue(IConfig? config, out double value)
    {
        value = 0;
        return config is not null
            && double.TryParse(
                config.Get("codex_lb_value"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)
            && value >= 0;
    }

    private static bool IsDisplaySafe(ProviderPlanValueRule rule) =>
        rule.Evidence is ProviderPlanEvidence.Official or ProviderPlanEvidence.UserConfigured;

    public static (int Tier, double MinutesUntil) ResetPriority(
        string instanceId,
        ProviderSnapshot snapshot,
        IConfig? config = null)
    {
        var providerType = Catalog.ProviderTypeForInstance(instanceId, config);
        var candidates = ResetCandidates(providerType, snapshot)
            .Select(candidate => ResetCandidateScore(candidate.Label, candidate.ResetsAt, candidate.WindowMinutes))
            .Where(score => score.HasValue)
            .Select(score => score!.Value)
            .ToList();

        if (candidates.Count == 0)
            return (NoResetTier, double.PositiveInfinity);

        return candidates
            .OrderBy(candidate => candidate.Tier)
            .ThenBy(candidate => candidate.MinutesUntil)
            .First();
    }

    private static IEnumerable<SnapshotRateWindow> ResetCandidates(
        string providerType,
        ProviderSnapshot snapshot)
    {
        if (providerType == "antigravity" && snapshot.ModelQuotas.Count > 0)
        {
            var modelCandidates = snapshot.ModelQuotas
                .Where(ModelQuotaPolicy.CountsForProviderAvailability)
                .ToList();
            foreach (var quota in modelCandidates)
                yield return new SnapshotRateWindow(quota.WindowType, quota.UsedPercent, quota.ResetsAt, null);
            if (modelCandidates.Count > 0)
                yield break;
        }

        foreach (var window in ProviderSnapshotWindows.ResetWindows(snapshot))
            yield return window;
    }

    private static (int Tier, double MinutesUntil)? ResetCandidateScore(
        string label,
        string? resetsAt,
        long? windowMinutes)
    {
        var minutesUntil = double.PositiveInfinity;
        if (!string.IsNullOrWhiteSpace(resetsAt)
            && DateTimeOffset.TryParse(
                resetsAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var when))
        {
            minutesUntil = Math.Max(0, (when - DateTimeOffset.UtcNow).TotalMinutes);
        }
        else if (!HasResetCadenceHint(label, windowMinutes))
        {
            return null;
        }

        return (ResetTier(label, windowMinutes, minutesUntil), minutesUntil);
    }

    private static bool HasResetCadenceHint(string label, long? windowMinutes)
    {
        if (windowMinutes.HasValue)
            return true;

        var normalized = label.ToLowerInvariant();
        return normalized.Contains("month", StringComparison.Ordinal)
            || normalized.Contains("plan credits", StringComparison.Ordinal)
            || normalized.Contains("credits", StringComparison.Ordinal)
            || normalized.Contains("token plan", StringComparison.Ordinal)
            || normalized.Contains("compensation", StringComparison.Ordinal)
            || normalized.Contains("5h", StringComparison.Ordinal)
            || normalized.Contains("hour", StringComparison.Ordinal)
            || normalized.Contains("today", StringComparison.Ordinal)
            || normalized.Contains("daily", StringComparison.Ordinal)
            || normalized.Contains("short", StringComparison.Ordinal)
            || normalized.Contains("7d", StringComparison.Ordinal)
            || normalized.Contains("week", StringComparison.Ordinal);
    }

    private static int ResetTier(string label, long? windowMinutes, double minutesUntil)
    {
        if (windowMinutes.HasValue)
            return windowMinutes.Value <= 24 * 60
                ? ShortResetTier
                : windowMinutes.Value <= 14 * 24 * 60
                    ? MediumResetTier
                    : LongResetTier;

        var normalized = label.ToLowerInvariant();
        if (normalized.Contains("month", StringComparison.Ordinal)
            || normalized.Contains("plan credits", StringComparison.Ordinal)
            || normalized.Contains("credits", StringComparison.Ordinal)
            || normalized.Contains("token plan", StringComparison.Ordinal)
            || normalized.Contains("compensation", StringComparison.Ordinal))
        {
            return LongResetTier;
        }

        if (normalized.Contains("5h", StringComparison.Ordinal)
            || normalized.Contains("hour", StringComparison.Ordinal)
            || normalized.Contains("today", StringComparison.Ordinal)
            || normalized.Contains("daily", StringComparison.Ordinal)
            || normalized.Contains("short", StringComparison.Ordinal))
        {
            return ShortResetTier;
        }

        if (normalized.Contains("7d", StringComparison.Ordinal)
            || normalized.Contains("week", StringComparison.Ordinal))
        {
            return MediumResetTier;
        }

        return minutesUntil <= 24 * 60
            ? ShortResetTier
            : minutesUntil <= 14 * 24 * 60
                ? MediumResetTier
                : LongResetTier;
    }

}
