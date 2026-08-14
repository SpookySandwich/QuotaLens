using QuotaLens.Core;
using QuotaLens.Helpers;
using Microsoft.UI.Xaml.Media;

namespace QuotaLens.ViewModels;

public sealed record ProviderAddOption(
    ProviderType Type,
    ProviderSetupKind SetupKind,
    int AlreadyAddedCount = 0)
{
    public string Id => Type.Id;
    public string Name => I18n.ProviderName(Type.Id, Type.Name);
    public string Monogram => Brand.Monogram(Id);
    public Brush BrandBrush => Brand.Brush(Id);
    public Brush BrandSoftBrush => Brand.SoftBrush(Id);

    /// <summary>Brand ink for the picker monogram, lifted for legibility on the neutral chip.</summary>
    public Brush TileBrush => Brand.TileBrush(Id);

    public string CategoryI18nKey => CategoryKeyFor(SetupKind);

    public string CategoryLabel => I18n.T(CategoryI18nKey);

    public string HintI18nKey => HintKeyFor(SetupKind);

    /// <summary>What this setup kind will actually ask of the user; shown once per group header.</summary>
    public string SetupHint => I18n.T(HintI18nKey);

    /// <summary>True when the user already tracks at least one instance of this provider.</summary>
    public bool IsAlreadyAdded => AlreadyAddedCount > 0;

    /// <summary>
    /// Adding a second instance is legitimate (multiple accounts), so this is a
    /// quiet informational badge — "Added" for one, "Added ×N" beyond that —
    /// never a blocker.
    /// </summary>
    public string AddedBadgeText => AlreadyAddedCount switch
    {
        <= 0 => "",
        1 => I18n.T("addProvider.added"),
        _ => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            I18n.T("addProvider.addedCount"),
            AlreadyAddedCount),
    };

    /// <summary>One check glyph for a single instance; a numeral once there are several.</summary>
    public bool ShowAddedCheck => AlreadyAddedCount == 1;

    public bool ShowAddedCount => AlreadyAddedCount >= 2;

    public string AddedCountText => AlreadyAddedCount >= 2
        ? (AlreadyAddedCount > 9 ? "9+" : AlreadyAddedCount.ToString(System.Globalization.CultureInfo.CurrentCulture))
        : "";

    /// <summary>
    /// The row prints only the provider name, so the setup kind (and added state)
    /// live here — this is what screen readers and hover recover.
    /// </summary>
    public string AccessibleLabel => IsAlreadyAdded
        ? $"{Name} — {CategoryLabel}, {AddedBadgeText}"
        : $"{Name} — {CategoryLabel}";

    public string RowTooltip => AccessibleLabel;

    public string CategorySearchText => SetupKind switch
    {
        ProviderSetupKind.BrowserLogin => "Browser login",
        ProviderSetupKind.ApiKey => "API key",
        ProviderSetupKind.LocalAppOrCli => "Local app or CLI",
        _ => "Ready to add",
    };

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var text = query.Trim();
        return Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || Id.Contains(text, StringComparison.OrdinalIgnoreCase)
            // Monogram EQUALITY only: "OR" should find OpenRouter, but a Contains
            // would make two-letter queries match nearly everything.
            || string.Equals(Monogram, text, StringComparison.OrdinalIgnoreCase)
            || CategoryLabel.Contains(text, StringComparison.OrdinalIgnoreCase)
            || CategorySearchText.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static string CategoryKeyFor(ProviderSetupKind kind) => kind switch
    {
        ProviderSetupKind.BrowserLogin => "addProvider.setup.browserLogin",
        ProviderSetupKind.ApiKey => "addProvider.setup.apiKey",
        ProviderSetupKind.LocalAppOrCli => "addProvider.setup.localAppOrCli",
        _ => "addProvider.setup.ready",
    };

    private static string HintKeyFor(ProviderSetupKind kind) => kind switch
    {
        ProviderSetupKind.BrowserLogin => "addProvider.setup.browserLogin.hint",
        ProviderSetupKind.ApiKey => "addProvider.setup.apiKey.hint",
        ProviderSetupKind.LocalAppOrCli => "addProvider.setup.localAppOrCli.hint",
        _ => "addProvider.setup.ready.hint",
    };
}

/// <summary>
/// A titled run of providers in the picker (a setup kind, "Suggested", or the flat
/// search-results group). Public because it is an x:DataType in the group header.
/// </summary>
public sealed record ProviderAddGroup(
    string Key,
    string Label,
    string Hint,
    IReadOnlyList<ProviderAddOption> Items)
{
    public int Count => Items.Count;

    public string CountText => Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string AccessibleLabel => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        "{0}, {1}",
        Label,
        string.Format(System.Globalization.CultureInfo.CurrentCulture, I18n.T("addProvider.countLabel"), Count));
}

internal static class ProviderAddOptions
{
    public static IReadOnlyList<ProviderAddOption> Build(
        IEnumerable<ProviderType> types,
        IReadOnlyList<ProviderInstance>? existingInstances = null)
    {
        var addedCounts = existingInstances?
            .GroupBy(instance => instance.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return types
            .Select(type => new ProviderAddOption(
                type,
                SetupKindFor(type.Id),
                addedCounts is not null && addedCounts.TryGetValue(type.Id, out var count) ? count : 0))
            .OrderBy(option => SortRank(option.SetupKind))
            .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<ProviderAddOption> Filter(IEnumerable<ProviderAddOption> options, string query) =>
        options.Where(option => option.Matches(query)).ToArray();

    /// <summary>
    /// Filter plus relevance ordering, so pressing Enter always commits the option
    /// the user meant: exact match, then prefix, then monogram, then word-boundary,
    /// then any substring.
    /// </summary>
    public static IReadOnlyList<ProviderAddOption> FilterRanked(
        IEnumerable<ProviderAddOption> options,
        string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return options.ToArray();

        return options
            .Where(option => option.Matches(query))
            .OrderBy(option => Rank(option, query))
            .ThenBy(option => SortRank(option.SetupKind))
            .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Relevance tier for <paramref name="query"/>; lower is a better match.</summary>
    public static int Rank(ProviderAddOption option, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 0;

        var text = query.Trim();
        if (option.Name.Equals(text, StringComparison.OrdinalIgnoreCase)
            || option.Id.Equals(text, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (option.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            return 1;

        if (option.Monogram.Equals(text, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (StartsAtWordBoundary(option.Name, text))
            return 3;

        if (option.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            return 4;

        if (option.Id.Contains(text, StringComparison.OrdinalIgnoreCase))
            return 5;

        return 6;
    }

    /// <summary>Groups by setup kind in setup-order; empty kinds are never emitted.</summary>
    public static IReadOnlyList<ProviderAddGroup> GroupBySetupKind(IEnumerable<ProviderAddOption> options) =>
        options
            .GroupBy(option => option.SetupKind)
            .OrderBy(group => SortRank(group.Key))
            .Select(group =>
            {
                var first = group.First();
                return new ProviderAddGroup(
                    group.Key.ToString(),
                    first.CategoryLabel,
                    first.SetupHint,
                    group
                        .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
                        .ToArray());
            })
            .Where(group => group.Count > 0)
            .ToArray();

    /// <summary>
    /// Popular providers, shown at the top even when they are already added, so the
    /// idle dialog opens on a short list of likely picks instead of an alphabet.
    /// Entries are duplicated into this group, not moved, so the canonical sections
    /// stay complete.
    /// </summary>
    public static ProviderAddGroup? SuggestedGroup(IReadOnlyList<ProviderAddOption> options)
    {
        var suggested = SuggestedIds
            .Select(id => options.FirstOrDefault(option =>
                string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase)))
            .Where(option => option is not null)
            .Select(option => option!)
            .Take(MaxSuggested)
            .ToArray();

        return suggested.Length == 0
            ? null
            : new ProviderAddGroup(
                "Suggested",
                I18n.T("addProvider.group.suggested"),
                I18n.T("addProvider.group.suggested.hint"),
                suggested);
    }

    public static ProviderSetupKind SetupKindFor(string providerType)
        => Catalog.SetupKindFor(providerType);

    private const int MaxSuggested = 6;

    private static readonly string[] SuggestedIds =
        { "codex", "claude", "cursor", "deepseek", "opencode" };

    private static bool StartsAtWordBoundary(string name, string query)
    {
        var index = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        while (index > 0)
        {
            if (!char.IsLetterOrDigit(name[index - 1]))
                return true;

            index = name.IndexOf(query, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static int SortRank(ProviderSetupKind kind) => kind switch
    {
        ProviderSetupKind.BrowserLogin => 0,
        ProviderSetupKind.ApiKey => 1,
        ProviderSetupKind.LocalAppOrCli => 2,
        _ => 3,
    };
}
