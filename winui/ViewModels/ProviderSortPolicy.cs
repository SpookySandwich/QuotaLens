using QuotaLens.Core;

namespace QuotaLens.ViewModels;

public enum ProviderSortMode
{
    FiveHour,
    Weekly,
    Monthly,
    PlanValue,
}

public enum ProviderSortTerm
{
    PlanValue,
    ResetFrequency,
    NextReset,
}

public static class ProviderSortPriorityOrder
{
    public const string ConfigKey = "sort_priority_order";
    public const string DefaultSerialized = "plan-value,reset-frequency,next-reset";

    private static readonly ProviderSortTerm[] DefaultOrder =
    {
        ProviderSortTerm.PlanValue,
        ProviderSortTerm.ResetFrequency,
        ProviderSortTerm.NextReset,
    };

    public static IReadOnlyList<ProviderSortTerm> Default => DefaultOrder;

    public static IReadOnlyList<ProviderSortTerm> FromConfig(IConfig config) =>
        Parse(config.Get(ConfigKey, DefaultSerialized));

    public static IReadOnlyList<ProviderSortTerm> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultOrder;

        var seen = new HashSet<ProviderSortTerm>();
        var terms = new List<ProviderSortTerm>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseTerm(token, out var term) || !seen.Add(term))
                continue;

            terms.Add(term);
        }

        if (terms.Count == 0)
            return DefaultOrder;

        foreach (var term in DefaultOrder)
        {
            if (seen.Add(term))
                terms.Add(term);
        }

        return terms;
    }

    public static string Serialize(IEnumerable<ProviderSortTerm> terms) =>
        string.Join(",", Parse(string.Join(",", terms.Select(Token))).Select(Token));

    public static string Token(ProviderSortTerm term) => term switch
    {
        ProviderSortTerm.PlanValue => "plan-value",
        ProviderSortTerm.ResetFrequency => "reset-frequency",
        ProviderSortTerm.NextReset => "next-reset",
        _ => "plan-value",
    };

    public static string I18nKey(ProviderSortTerm term) => term switch
    {
        ProviderSortTerm.PlanValue => "sort.term.planValue",
        ProviderSortTerm.ResetFrequency => "sort.term.resetFrequency",
        ProviderSortTerm.NextReset => "sort.term.nextReset",
        _ => "sort.term.planValue",
    };

    public static string DescriptionI18nKey(ProviderSortTerm term) => term switch
    {
        ProviderSortTerm.PlanValue => "sort.term.planValue.desc",
        ProviderSortTerm.ResetFrequency => "sort.term.resetFrequency.desc",
        ProviderSortTerm.NextReset => "sort.term.nextReset.desc",
        _ => "sort.term.planValue.desc",
    };

    private static bool TryParseTerm(string token, out ProviderSortTerm term)
    {
        switch (token.Trim().ToLowerInvariant())
        {
            case "utilization":
            case "availability":
                term = default;
                return false;
            case "value":
            case "value-tier":
            case "value_tier":
            case "plan-value":
            case "plan_value":
                term = ProviderSortTerm.PlanValue;
                return true;
            case "reset-cycle":
            case "reset_cycle":
            case "cadence":
            case "reset-cadence":
            case "reset-frequency":
            case "reset_frequency":
                term = ProviderSortTerm.ResetFrequency;
                return true;
            case "reset-time":
            case "reset_time":
            case "reset":
            case "next-reset":
            case "next_reset":
                term = ProviderSortTerm.NextReset;
                return true;
            default:
                term = default;
                return false;
        }
    }
}

public static class ProviderSortPolicy
{
    public const string DeprioritizeEmptyProvidersConfigKey = "deprioritize_empty_providers";

    public static bool DeprioritizeEmptyProvidersFromConfig(IConfig config) =>
        config.GetBool(DeprioritizeEmptyProvidersConfigKey, fallback: true);

    public static IReadOnlyList<T> Order<T>(
        IEnumerable<T> items,
        ProviderSortMode mode,
        Func<T, ProviderPriorityScore> scoreSelector,
        IReadOnlyList<ProviderSortTerm>? secondaryPriorityOrder = null,
        bool deprioritizeEmptyProviders = false)
    {
        var indexed = items
            .Select((item, idx) => new IndexedItem<T>(item, idx, scoreSelector(item)))
            .ToList();

        var priorityOrder = EffectivePriorityOrder(mode, secondaryPriorityOrder ?? ProviderSortPriorityOrder.Default);
        return indexed
            .OrderBy(x => x, PriorityComparer<T>.For(mode, priorityOrder, deprioritizeEmptyProviders))
            .Select(x => x.Item)
            .ToList();
    }

    public static IReadOnlyList<ProviderSortTerm> EffectivePriorityOrder(
        ProviderSortMode primaryMode,
        IReadOnlyList<ProviderSortTerm> secondaryPriorityOrder)
    {
        var primary = PrimaryTerm(primaryMode);
        return new[] { primary }
            .Concat(ProviderSortPriorityOrder
                .Parse(ProviderSortPriorityOrder.Serialize(secondaryPriorityOrder))
                .Where(term => term != primary))
            .ToList();
    }

    /// <summary>
    /// Ranks a monthly plan price and a balance on the same USD scale. Safe only
    /// because <see cref="ProviderPriorityScore.BalanceAmount"/> is monetary USD or
    /// zero — a credits or points count reads as 0 here rather than as dollars.
    /// </summary>
    private static double EffectiveValue(ProviderPriorityScore score) =>
        score.PlanValue > 0
            ? score.PlanValue
            : (score.HasBalance || score.IsPayAsYouGo ? score.BalanceAmount : 0.0);

    private static int CompareTerm(ProviderPriorityScore left, ProviderPriorityScore right, ProviderSortTerm term) =>
        term switch
        {
            ProviderSortTerm.PlanValue => CompareDouble(EffectiveValue(right), EffectiveValue(left)),
            ProviderSortTerm.ResetFrequency => left.ResetTier.CompareTo(right.ResetTier),
            ProviderSortTerm.NextReset => CompareDouble(left.ResetMinutesUntil, right.ResetMinutesUntil),
            _ => 0,
        };

    private static int CompareUtilization(ProviderPriorityScore left, ProviderPriorityScore right) =>
        CompareDouble(left.Availability, right.Availability);

    private static ProviderSortTerm PrimaryTerm(ProviderSortMode mode) => mode switch
    {
        ProviderSortMode.FiveHour => ProviderSortTerm.ResetFrequency,
        ProviderSortMode.Weekly => ProviderSortTerm.NextReset,
        ProviderSortMode.Monthly => ProviderSortTerm.NextReset,
        _ => ProviderSortTerm.PlanValue,
    };

    private static int ActionabilityRank(ProviderPriorityScore score)
    {
        if (score.Bucket == ProviderPriority.UsableSubscriptionBucket)
            return 0;
        if (score.IsPayAsYouGo && score.BalanceAmount > 0)
            return 0;
        if (score.Bucket == ProviderPriority.ExhaustedSubscriptionBucket)
            return 1;
        if (score.IsPayAsYouGo)
            return 2;
        return 3;
    }

    private static int CadenceTier(ProviderPriorityScore score, ProviderSortMode mode) => mode switch
    {
        ProviderSortMode.FiveHour => score.HasFiveHour ? 0 : (score.HasWeekly ? 1 : (score.HasMonthly ? 2 : 3)),
        ProviderSortMode.Weekly => score.HasWeekly ? 0 : (score.HasMonthly ? 1 : (score.HasFiveHour ? 2 : 3)),
        ProviderSortMode.Monthly => score.HasMonthly ? 0 : (score.HasWeekly ? 1 : (score.HasFiveHour ? 2 : 3)),
        _ => 0,
    };

    private static int CompareDouble(double left, double right)
    {
        if (double.IsNaN(left) && double.IsNaN(right))
            return 0;
        if (double.IsNaN(left))
            return 1;
        if (double.IsNaN(right))
            return -1;
        return left.CompareTo(right);
    }

    private sealed record IndexedItem<T>(T Item, int Index, ProviderPriorityScore Score);

    private sealed class PriorityComparer<T> : IComparer<IndexedItem<T>>
    {
        private readonly ProviderSortMode _mode;
        private readonly IReadOnlyList<ProviderSortTerm> _priorityOrder;
        private readonly bool _deprioritizeEmptyProviders;

        private PriorityComparer(ProviderSortMode mode, IReadOnlyList<ProviderSortTerm> priorityOrder, bool deprioritizeEmptyProviders)
        {
            _mode = mode;
            _priorityOrder = priorityOrder;
            _deprioritizeEmptyProviders = deprioritizeEmptyProviders;
        }

        public static PriorityComparer<T> For(ProviderSortMode mode, IReadOnlyList<ProviderSortTerm> priorityOrder, bool deprioritizeEmptyProviders) =>
            new(mode, priorityOrder, deprioritizeEmptyProviders);

        public int Compare(IndexedItem<T>? left, IndexedItem<T>? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return 1;
            if (right is null)
                return -1;

            int comparison;
            if (_deprioritizeEmptyProviders)
            {
                comparison = ActionabilityRank(left.Score).CompareTo(ActionabilityRank(right.Score));
                if (comparison != 0)
                    return comparison;
            }

            if (_mode is ProviderSortMode.FiveHour or ProviderSortMode.Weekly or ProviderSortMode.Monthly)
            {
                var leftTier = CadenceTier(left.Score, _mode);
                var rightTier = CadenceTier(right.Score, _mode);
                comparison = leftTier.CompareTo(rightTier);
                if (comparison != 0)
                    return comparison;

                if (leftTier == 0)
                {
                    if (_mode == ProviderSortMode.FiveHour)
                    {
                        comparison = CompareDouble(left.Score.FiveHourMinutesUntil, right.Score.FiveHourMinutesUntil);
                        if (comparison != 0) return comparison;
                        comparison = CompareDouble(right.Score.FiveHourAvailability, left.Score.FiveHourAvailability);
                        if (comparison != 0) return comparison;
                    }
                    else if (_mode == ProviderSortMode.Weekly)
                    {
                        comparison = CompareDouble(left.Score.WeeklyMinutesUntil, right.Score.WeeklyMinutesUntil);
                        if (comparison != 0) return comparison;
                        comparison = CompareDouble(right.Score.WeeklyAvailability, left.Score.WeeklyAvailability);
                        if (comparison != 0) return comparison;
                    }
                    else if (_mode == ProviderSortMode.Monthly)
                    {
                        comparison = CompareDouble(left.Score.MonthlyMinutesUntil, right.Score.MonthlyMinutesUntil);
                        if (comparison != 0) return comparison;
                        comparison = CompareDouble(right.Score.MonthlyAvailability, left.Score.MonthlyAvailability);
                        if (comparison != 0) return comparison;
                    }
                    comparison = CompareDouble(EffectiveValue(right.Score), EffectiveValue(left.Score));
                    if (comparison != 0) return comparison;
                }
                else if (leftTier == 1)
                {
                    if (_mode == ProviderSortMode.FiveHour)
                    {
                        comparison = CompareDouble(left.Score.WeeklyMinutesUntil, right.Score.WeeklyMinutesUntil);
                        if (comparison != 0) return comparison;
                        comparison = CompareDouble(right.Score.WeeklyAvailability, left.Score.WeeklyAvailability);
                        if (comparison != 0) return comparison;
                    }
                    else if (_mode == ProviderSortMode.Weekly)
                    {
                        comparison = CompareDouble(left.Score.MonthlyMinutesUntil, right.Score.MonthlyMinutesUntil);
                        if (comparison != 0) return comparison;
                        comparison = CompareDouble(right.Score.MonthlyAvailability, left.Score.MonthlyAvailability);
                        if (comparison != 0) return comparison;
                    }
                    else if (_mode == ProviderSortMode.Monthly)
                    {
                        comparison = CompareDouble(left.Score.WeeklyMinutesUntil, right.Score.WeeklyMinutesUntil);
                        if (comparison != 0) return comparison;
                        comparison = CompareDouble(right.Score.WeeklyAvailability, left.Score.WeeklyAvailability);
                        if (comparison != 0) return comparison;
                    }
                    comparison = CompareDouble(EffectiveValue(right.Score), EffectiveValue(left.Score));
                    if (comparison != 0) return comparison;
                }
                else if (leftTier == 2)
                {
                    if (_mode == ProviderSortMode.FiveHour)
                    {
                        comparison = CompareDouble(left.Score.MonthlyMinutesUntil, right.Score.MonthlyMinutesUntil);
                        if (comparison != 0) return comparison;
                        comparison = CompareDouble(right.Score.MonthlyAvailability, left.Score.MonthlyAvailability);
                        if (comparison != 0) return comparison;
                    }
                    else if (_mode == ProviderSortMode.Weekly)
                    {
                        comparison = CompareDouble(left.Score.FiveHourMinutesUntil, right.Score.FiveHourMinutesUntil);
                        if (comparison != 0) return comparison;
                        comparison = CompareDouble(right.Score.FiveHourAvailability, left.Score.FiveHourAvailability);
                        if (comparison != 0) return comparison;
                    }
                    else if (_mode == ProviderSortMode.Monthly)
                    {
                        comparison = CompareDouble(left.Score.FiveHourMinutesUntil, right.Score.FiveHourMinutesUntil);
                        if (comparison != 0) return comparison;
                        comparison = CompareDouble(right.Score.FiveHourAvailability, left.Score.FiveHourAvailability);
                        if (comparison != 0) return comparison;
                    }
                    comparison = CompareDouble(EffectiveValue(right.Score), EffectiveValue(left.Score));
                    if (comparison != 0) return comparison;
                }
            }

            foreach (var term in _priorityOrder)
            {
                comparison = CompareTerm(left.Score, right.Score, term);
                if (comparison != 0)
                    return comparison;
            }

            comparison = CompareUtilization(left.Score, right.Score);
            if (comparison != 0)
                return comparison;

            return left.Index.CompareTo(right.Index);
        }
    }
}
