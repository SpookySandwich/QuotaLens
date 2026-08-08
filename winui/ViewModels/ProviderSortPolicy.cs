using QuotaLens.Core;

namespace QuotaLens.ViewModels;

public enum ProviderSortMode
{
    PlanValue,
    ResetFrequency,
    NextReset,
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
            .OrderBy(x => x, PriorityComparer<T>.For(priorityOrder, deprioritizeEmptyProviders))
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

    private static int CompareTerm(ProviderPriorityScore left, ProviderPriorityScore right, ProviderSortTerm term) =>
        term switch
        {
            ProviderSortTerm.PlanValue => CompareDouble(right.PlanValue, left.PlanValue),
            ProviderSortTerm.ResetFrequency => left.ResetTier.CompareTo(right.ResetTier),
            ProviderSortTerm.NextReset => CompareDouble(left.ResetMinutesUntil, right.ResetMinutesUntil),
            _ => 0,
        };

    private static int CompareUtilization(ProviderPriorityScore left, ProviderPriorityScore right) =>
        CompareDouble(left.Availability, right.Availability);

    private static ProviderSortTerm PrimaryTerm(ProviderSortMode mode) => mode switch
    {
        ProviderSortMode.ResetFrequency => ProviderSortTerm.ResetFrequency,
        ProviderSortMode.NextReset => ProviderSortTerm.NextReset,
        _ => ProviderSortTerm.PlanValue,
    };

    private static int LastResortRank(ProviderPriorityScore score)
    {
        if (score.Bucket == ProviderPriority.ErrorOrPendingBucket)
            return 2;
        if (score.IsPayAsYouGo)
            return 1;
        return 0;
    }

    private static int ActionabilityRank(ProviderPriorityScore score)
    {
        if (score.Bucket == ProviderPriority.UsableSubscriptionBucket)
            return 0;
        if (score.Bucket == ProviderPriority.ExhaustedSubscriptionBucket)
            return 1;
        if (score.IsPayAsYouGo)
            return 2;
        return 3;
    }

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
        private readonly IReadOnlyList<ProviderSortTerm> _priorityOrder;
        private readonly bool _deprioritizeEmptyProviders;

        private PriorityComparer(IReadOnlyList<ProviderSortTerm> priorityOrder, bool deprioritizeEmptyProviders)
        {
            _priorityOrder = priorityOrder;
            _deprioritizeEmptyProviders = deprioritizeEmptyProviders;
        }

        public static PriorityComparer<T> For(IReadOnlyList<ProviderSortTerm> priorityOrder, bool deprioritizeEmptyProviders) =>
            new(priorityOrder, deprioritizeEmptyProviders);

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

            foreach (var term in _priorityOrder)
            {
                comparison = CompareTerm(left.Score, right.Score, term);
                if (comparison != 0)
                    return comparison;
            }

            comparison = CompareUtilization(left.Score, right.Score);
            if (comparison != 0)
                return comparison;

            comparison = LastResortRank(left.Score).CompareTo(LastResortRank(right.Score));
            if (comparison != 0)
                return comparison;

            return left.Index.CompareTo(right.Index);
        }
    }
}
