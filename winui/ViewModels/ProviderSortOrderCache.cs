using QuotaLens.Core;

namespace QuotaLens.ViewModels;

internal sealed class ProviderSortOrderCache<T>
{
    private static readonly ProviderSortMode[] SortModes =
    {
        ProviderSortMode.PlanValue,
        ProviderSortMode.ResetFrequency,
        ProviderSortMode.NextReset,
    };

    private readonly Func<T, string> _keySelector;
    private readonly Func<T, ProviderPriorityScore> _scoreSelector;
    private readonly Dictionary<ProviderSortMode, IReadOnlyList<string>> _orders = new();

    public ProviderSortOrderCache(
        Func<T, string> keySelector,
        Func<T, ProviderPriorityScore> scoreSelector)
    {
        _keySelector = keySelector;
        _scoreSelector = scoreSelector;
    }

    public bool HasOrder(ProviderSortMode mode) => _orders.ContainsKey(mode);

    public IReadOnlyList<string> OrderFor(ProviderSortMode mode) =>
        _orders.TryGetValue(mode, out var order) ? order : Array.Empty<string>();

    public void Rebuild(
        IEnumerable<T> items,
        IReadOnlyList<ProviderSortTerm> secondaryPriorityOrder,
        bool deprioritizeEmptyProviders = false)
    {
        var itemList = items
            .Select(item => new ScoredItem(_keySelector(item), _scoreSelector(item)))
            .ToList();
        _orders.Clear();

        foreach (var mode in SortModes)
        {
            _orders[mode] = ProviderSortPolicy
                .Order(itemList, mode, item => item.Score, secondaryPriorityOrder, deprioritizeEmptyProviders)
                .Select(item => item.Key)
                .ToArray();
        }
    }

    private readonly record struct ScoredItem(string Key, ProviderPriorityScore Score);
}
