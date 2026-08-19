using System.Globalization;

namespace QuotaLens.Core;

public readonly record struct ProviderPriorityScore(
    int Bucket,
    double PlanValue,
    double Availability,
    bool IsPayAsYouGo,
    int ResetTier = ProviderPriority.NoResetTier,
    double ResetMinutesUntil = double.PositiveInfinity,
    bool HasFiveHour = false,
    double FiveHourMinutesUntil = double.PositiveInfinity,
    double FiveHourAvailability = 0.0,
    bool HasWeekly = false,
    double WeeklyMinutesUntil = double.PositiveInfinity,
    double WeeklyAvailability = 0.0,
    bool HasMonthly = false,
    double MonthlyMinutesUntil = double.PositiveInfinity,
    double MonthlyAvailability = 0.0,
    bool HasBalance = false,
    double BalanceAmount = 0.0);

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
        var availability = Quota.ProviderAvailability(snapshot);
        var reset = ResetPriority(snapshot);

        bool hasFiveHour = false;
        double fiveHourMinutesUntil = double.PositiveInfinity;
        double fiveHourAvailability = 0.0;

        bool hasWeekly = false;
        double weeklyMinutesUntil = double.PositiveInfinity;
        double weeklyAvailability = 0.0;

        bool hasMonthly = false;
        double monthlyMinutesUntil = double.PositiveInfinity;
        double monthlyAvailability = 0.0;

        foreach (var window in CadenceWindows(snapshot))
        {
            var candidateScore = ResetCandidateScore(window.Label, window.ResetsAt, window.WindowMinutes);
            if (!candidateScore.HasValue)
                continue;

            var avail = Math.Clamp(100.0 - window.UsedPercent, 0, 100);
            var tier = candidateScore.Value.Tier;
            var mins = candidateScore.Value.MinutesUntil;

            if (tier == ShortResetTier)
            {
                hasFiveHour = true;
                if (mins < fiveHourMinutesUntil) fiveHourMinutesUntil = mins;
                if (avail > fiveHourAvailability) fiveHourAvailability = avail;
            }
            else if (tier == MediumResetTier)
            {
                hasWeekly = true;
                if (mins < weeklyMinutesUntil) weeklyMinutesUntil = mins;
                if (avail > weeklyAvailability) weeklyAvailability = avail;
            }
            else if (tier == LongResetTier)
            {
                hasMonthly = true;
                if (mins < monthlyMinutesUntil) monthlyMinutesUntil = mins;
                if (avail > monthlyAvailability) monthlyAvailability = avail;
            }
        }

        // Same-account nested limits: a 5h window cannot exceed the weekly or
        // monthly pool it sits inside. Pooled accounts apply this per account below.
        if (snapshot.Accounts.Count == 0)
        {
            if (hasFiveHour)
            {
                if (hasWeekly)
                    fiveHourAvailability = Math.Min(fiveHourAvailability, weeklyAvailability);
                if (hasMonthly)
                    fiveHourAvailability = Math.Min(fiveHourAvailability, monthlyAvailability);
            }
            if (hasWeekly && hasMonthly)
                weeklyAvailability = Math.Min(weeklyAvailability, monthlyAvailability);
        }

        if (snapshot.Accounts.Count > 0)
        {
            double fiveHourAvailWeighted = 0;
            double fiveHourCapSum = 0;
            double weeklyAvailWeighted = 0;
            double weeklyCapSum = 0;
            double monthlyAvailWeighted = 0;
            double monthlyCapSum = 0;

            foreach (var acc in snapshot.Accounts)
            {
                var cap = (acc.CreditsTotal.HasValue && acc.CreditsTotal.Value > 0) ? acc.CreditsTotal.Value : 1.0;

                double? acc5hAvail = null;
                double? accWeeklyAvail = null;
                double? accMonthlyAvail = null;

                AssignAccountCadence(
                    acc.PrimaryLabel,
                    acc.PrimaryUsedPercent,
                    acc.PrimaryResetsAt,
                    ref hasFiveHour,
                    ref fiveHourMinutesUntil,
                    ref acc5hAvail,
                    ref hasWeekly,
                    ref weeklyMinutesUntil,
                    ref accWeeklyAvail,
                    ref hasMonthly,
                    ref monthlyMinutesUntil,
                    ref accMonthlyAvail);
                AssignAccountCadence(
                    acc.SecondaryLabel,
                    acc.SecondaryUsedPercent,
                    acc.SecondaryResetsAt,
                    ref hasFiveHour,
                    ref fiveHourMinutesUntil,
                    ref acc5hAvail,
                    ref hasWeekly,
                    ref weeklyMinutesUntil,
                    ref accWeeklyAvail,
                    ref hasMonthly,
                    ref monthlyMinutesUntil,
                    ref accMonthlyAvail);

                if (accMonthlyAvail.HasValue)
                {
                    monthlyAvailWeighted += accMonthlyAvail.Value * cap;
                    monthlyCapSum += cap;
                }

                if (accWeeklyAvail.HasValue)
                {
                    var effectiveWeekly = accMonthlyAvail.HasValue
                        ? Math.Min(accWeeklyAvail.Value, accMonthlyAvail.Value)
                        : accWeeklyAvail.Value;
                    weeklyAvailWeighted += effectiveWeekly * cap;
                    weeklyCapSum += cap;
                    accWeeklyAvail = effectiveWeekly;
                }

                if (acc5hAvail.HasValue)
                {
                    var parent = accWeeklyAvail ?? accMonthlyAvail;
                    var effectiveAcc5h = parent.HasValue
                        ? Math.Min(acc5hAvail.Value, parent.Value)
                        : acc5hAvail.Value;
                    fiveHourAvailWeighted += effectiveAcc5h * cap;
                    fiveHourCapSum += cap;
                }
            }

            if (fiveHourCapSum > 0)
                fiveHourAvailability = fiveHourAvailWeighted / fiveHourCapSum;
            if (weeklyCapSum > 0)
                weeklyAvailability = weeklyAvailWeighted / weeklyCapSum;
            if (monthlyCapSum > 0)
                monthlyAvailability = monthlyAvailWeighted / monthlyCapSum;
        }

        bool hasBalance = snapshot.Balance is not null || planValue < 0;
        double balanceRaw = snapshot.Balance?.Total > 0
            ? snapshot.Balance.Total
            : (snapshot.Balance?.Paid ?? 0.0);
        var currency = snapshot.Balance?.Currency?.ToUpperInvariant();
        double balanceAmount = currency switch
        {
            "CNY" or "RMB" => balanceRaw / 7.2,
            _ => balanceRaw,
        };

        if (planValue < 0)
            return new ProviderPriorityScore(
                PayAsYouGoBucket,
                planValue,
                availability,
                IsPayAsYouGo: true,
                reset.Tier,
                reset.MinutesUntil,
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
                balanceAmount);

        double threshold = 5.0;
        if (config is not null)
        {
            var rawThreshold = config.Get("empty_threshold_pct", "5");
            if (double.TryParse(rawThreshold, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
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
            reset.MinutesUntil,
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
            balanceAmount);
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

        if (TryAggregateAccountPlanValue(providerType, snapshot, config, out var accountValue))
            return accountValue;

        var planIdentity = ProviderSnapshotIdentity.PlanIdentity(providerType, snapshot);
        if (!planIdentity.IsEmpty)
        {
            var configuredRuleValue = PlanValueRules.MatchConfigured(providerType, planIdentity, config);
            if (configuredRuleValue.HasValue)
                return configuredRuleValue.Value;
        }

        if (TryConfiguredProviderValue(providerType, config, out var configuredValue))
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

        if (TryAggregateAccountDisplayValue(providerType, snapshot, config, out var accountDisplay))
        {
            return accountDisplay;
        }

        var configuredRule = planIdentity.IsEmpty
            ? null
            : PlanValueRules.MatchConfiguredRule(providerType, planIdentity, config);
        if (configuredRule is not null && IsDisplaySafe(configuredRule))
            return DisplayFor(configuredRule);

        if (TryConfiguredProviderValue(providerType, config, out var configuredValue))
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

    private static bool TryAggregateAccountPlanValue(
        string providerType,
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

            var planValue = PlanValueRules.MatchIncludingDefaults(providerType, account.Plan, config);
            if (!planValue.HasValue)
                continue;

            value += planValue.Value;
            matched++;
        }

        return matched > 0;
    }

    private static bool TryAggregateAccountDisplayValue(
        string providerType,
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

            var rule = PlanValueRules.MatchIncludingDefaultsRule(providerType, account.Plan, config);
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

    private static bool TryConfiguredProviderValue(string providerType, IConfig? config, out double value)
    {
        value = 0;
        var key = Catalog.PlanValueOverrideKeyFor(providerType);
        return config is not null
            && key is not null
            && double.TryParse(
                config.Get(key),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)
            && value >= 0;
    }

    private static bool IsDisplaySafe(ProviderPlanValueRule rule) =>
        rule.Evidence is ProviderPlanEvidence.Official or ProviderPlanEvidence.UserConfigured;

    public static (int Tier, double MinutesUntil) ResetPriority(ProviderSnapshot snapshot)
    {
        var candidates = ResetCandidates(snapshot)
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

    private static IEnumerable<SnapshotRateWindow> ResetCandidates(ProviderSnapshot snapshot)
    {
        if (snapshot.ModelQuotas.Count > 0)
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

    /// <summary>
    /// Windows that define 5h / weekly / monthly remaining. Extra model-specific
    /// quotas stay in <see cref="ResetCandidates"/> for next-reset sorting only.
    /// </summary>
    private static IEnumerable<SnapshotRateWindow> CadenceWindows(ProviderSnapshot snapshot)
    {
        if (snapshot.ModelQuotas.Count > 0)
        {
            var modelCandidates = snapshot.ModelQuotas
                .Where(ModelQuotaPolicy.CountsForProviderAvailability)
                .ToList();
            foreach (var quota in modelCandidates)
                yield return new SnapshotRateWindow(quota.WindowType, quota.UsedPercent, quota.ResetsAt, null);
            if (modelCandidates.Count > 0)
                yield break;
        }

        foreach (var window in ProviderSnapshotWindows.AvailabilityWindows(snapshot))
            yield return window;

        foreach (var window in snapshot.AdditionalWindows.Where(IsEffectiveCadenceWindow))
            yield return new SnapshotRateWindow(window.Label, window.UsedPercent, window.ResetsAt, window.WindowMinutes);
    }

    private static bool IsEffectiveCadenceWindow(RateWindow window) =>
        window.Kind == RateWindowKind.Quota
        && !window.CountsForAvailability
        && QuotaCadencePolicy.FromLabel(window.Label) != QuotaCadence.None
        && window.Label.Contains("Effective", StringComparison.OrdinalIgnoreCase);

    private static (int Tier, double MinutesUntil)? ResetCandidateScore(
        string label,
        string? resetsAt,
        long? windowMinutes)
    {
        var minutesUntil = MinutesUntilIso(resetsAt);
        if (double.IsPositiveInfinity(minutesUntil) && !QuotaCadencePolicy.HasCadenceHint(label, windowMinutes))
            return null;

        var cadence = QuotaCadencePolicy.For(label, windowMinutes, minutesUntil);
        if (cadence == QuotaCadence.None && double.IsPositiveInfinity(minutesUntil))
            return null;

        return (QuotaCadencePolicy.ResetTier(cadence), minutesUntil);
    }

    private static void AssignAccountCadence(
        string? label,
        double? usedPercent,
        string? resetsAt,
        ref bool hasFiveHour,
        ref double fiveHourMinutesUntil,
        ref double? acc5hAvail,
        ref bool hasWeekly,
        ref double weeklyMinutesUntil,
        ref double? accWeeklyAvail,
        ref bool hasMonthly,
        ref double monthlyMinutesUntil,
        ref double? accMonthlyAvail)
    {
        if (string.IsNullOrEmpty(label))
            return;

        var cadence = QuotaCadencePolicy.FromLabel(label);
        if (cadence == QuotaCadence.None)
            return;

        var avail = usedPercent.HasValue
            ? Math.Clamp(100.0 - usedPercent.Value, 0, 100)
            : 100.0;
        var mins = MinutesUntilIso(resetsAt);

        switch (cadence)
        {
            case QuotaCadence.FiveHour:
                hasFiveHour = true;
                acc5hAvail = avail;
                if (mins < fiveHourMinutesUntil) fiveHourMinutesUntil = mins;
                break;
            case QuotaCadence.Weekly:
                hasWeekly = true;
                accWeeklyAvail = avail;
                if (mins < weeklyMinutesUntil) weeklyMinutesUntil = mins;
                break;
            case QuotaCadence.Monthly:
                hasMonthly = true;
                accMonthlyAvail = avail;
                if (mins < monthlyMinutesUntil) monthlyMinutesUntil = mins;
                break;
        }
    }

    private static double MinutesUntilIso(string? iso)
    {
        if (!string.IsNullOrWhiteSpace(iso)
            && DateTimeOffset.TryParse(
                iso,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var when))
        {
            return Math.Max(0, (when - DateTimeOffset.UtcNow).TotalMinutes);
        }
        return double.PositiveInfinity;
    }
}
