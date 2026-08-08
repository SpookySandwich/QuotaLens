using System.Globalization;

namespace QuotaLens.Core;

public static class PlanValueRules
{
    private const string KeyPrefix = "plan_value_rules.";

    public static string ConfigKey(string providerType) => $"{KeyPrefix}{providerType}";

    public static IReadOnlyList<ProviderPlanValueRule> ForProvider(string providerType, IConfig? config = null)
    {
        if (TryGetConfigured(providerType, config, out var configured))
            return configured;

        return Catalog.DefaultPlanValueRules.TryGetValue(providerType, out var defaults)
            ? defaults
            : Array.Empty<ProviderPlanValueRule>();
    }

    public static bool TryGetConfigured(
        string providerType,
        IConfig? config,
        out IReadOnlyList<ProviderPlanValueRule> rules)
    {
        rules = Array.Empty<ProviderPlanValueRule>();
        var configured = config?.Get(ConfigKey(providerType));
        if (string.IsNullOrWhiteSpace(configured))
            return false;

        var parsed = Parse(configured);
        if (parsed.Count == 0)
            return false;

        // Older builds persisted the visible default rows as if the user had
        // authored them. Treat an exact default round-trip as defaults so their
        // structured provenance and qualifiers are not discarded.
        if (AreEquivalentToDefaults(providerType, parsed))
            return false;

        rules = parsed;
        return true;
    }

    public static double? Match(string providerType, string planName, IConfig? config = null)
        => MatchRule(providerType, planName, config)?.Value;

    public static double? Match(string providerType, ProviderPlanIdentity identity, IConfig? config = null)
        => MatchRule(providerType, identity, config)?.Value;

    public static ProviderPlanValueRule? MatchRule(string providerType, string planName, IConfig? config = null)
    {
        return MatchRule(ForProvider(providerType, config), planName);
    }

    public static ProviderPlanValueRule? MatchRule(
        string providerType,
        ProviderPlanIdentity identity,
        IConfig? config = null) =>
        MatchRule(ForProvider(providerType, config), identity);

    public static double? MatchIncludingDefaults(string providerType, string planName, IConfig? config = null)
        => MatchIncludingDefaultsRule(providerType, planName, config)?.Value;

    public static ProviderPlanValueRule? MatchIncludingDefaultsRule(
        string providerType,
        string planName,
        IConfig? config = null)
    {
        var configured = MatchConfiguredRule(providerType, planName, config);
        if (configured is not null)
            return configured;

        if (!Catalog.DefaultPlanValueRules.TryGetValue(providerType, out var defaults))
            return null;

        return MatchRule(defaults, planName);
    }

    public static double? MatchConfigured(string providerType, string planName, IConfig? config = null)
        => MatchConfiguredRule(providerType, planName, config)?.Value;

    public static double? MatchConfigured(
        string providerType,
        ProviderPlanIdentity identity,
        IConfig? config = null) =>
        MatchConfiguredRule(providerType, identity, config)?.Value;

    public static ProviderPlanValueRule? MatchConfiguredRule(
        string providerType,
        string planName,
        IConfig? config = null)
    {
        if (!TryGetConfigured(providerType, config, out var rules))
            return null;

        return MatchRule(rules, planName);
    }

    public static ProviderPlanValueRule? MatchConfiguredRule(
        string providerType,
        ProviderPlanIdentity identity,
        IConfig? config = null)
    {
        if (!TryGetConfigured(providerType, config, out var rules))
            return null;

        return MatchRule(rules, identity);
    }

    private static ProviderPlanValueRule? MatchRule(
        IEnumerable<ProviderPlanValueRule> rules,
        ProviderPlanIdentity identity)
    {
        var candidates = rules.ToArray();
        if (!string.IsNullOrWhiteSpace(identity.PlanId))
        {
            var exactId = candidates.FirstOrDefault(rule =>
                !string.IsNullOrWhiteSpace(rule.PlanId)
                && string.Equals(rule.PlanId.Trim(), identity.PlanId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exactId is not null)
                return exactId;
        }

        var lookupText = !string.IsNullOrWhiteSpace(identity.PlanName)
            ? identity.PlanName
            : identity.PlanId;
        return string.IsNullOrWhiteSpace(lookupText)
            ? null
            : MatchRule(candidates, lookupText);
    }

    private static ProviderPlanValueRule? MatchRule(
        IEnumerable<ProviderPlanValueRule> rules,
        string planName)
    {
        var normalized = NormalizeMatchText(planName);
        foreach (var rule in rules)
        {
            if (IsMatch(normalized, rule))
                return rule;
        }

        return null;
    }

    private static bool IsMatch(string normalizedPlanName, ProviderPlanValueRule rule) =>
        KeywordMatches(normalizedPlanName, rule.Keyword);

    /// <summary>Word-boundary keyword match on already-normalized plan text (shared with PlanTokenRules).</summary>
    internal static bool KeywordMatches(string normalizedPlanName, string keyword)
    {
        var normalizedKeyword = NormalizeMatchText(keyword);
        return normalizedKeyword.Length > 0
            && $" {normalizedPlanName} ".Contains($" {normalizedKeyword} ", StringComparison.Ordinal);
    }

    internal static string NormalizeMatchText(string text)
    {
        var chars = text.Trim().ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) || character == '+' ? character : ' ');
        return string.Join(' ', new string(chars.ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static List<ProviderPlanValueRule> Parse(string text)
    {
        var rules = new List<ProviderPlanValueRule>();
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
            if (keyword.Length == 0)
                continue;

            if (double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0)
                rules.Add(new ProviderPlanValueRule(keyword, value, Evidence: ProviderPlanEvidence.UserConfigured));
        }

        return rules;
    }

    public static string Serialize(IEnumerable<ProviderPlanValueRule> rules) =>
        string.Join(Environment.NewLine, rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Keyword) && rule.Value >= 0)
            .Select(rule => $"{rule.Keyword.Trim()}={rule.Value.ToString("0.##", CultureInfo.InvariantCulture)}"));

    public static bool AreEquivalentToDefaults(string providerType, IReadOnlyList<ProviderPlanValueRule> rules)
    {
        var defaults = Catalog.DefaultPlanValueRules.TryGetValue(providerType, out var providerDefaults)
            ? providerDefaults.Where(rule => rule.Value >= 0).ToArray()
            : Array.Empty<ProviderPlanValueRule>();
        var normalized = rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Keyword) && rule.Value >= 0)
            .Select(rule => new ProviderPlanValueRule(
                rule.Keyword.Trim(),
                rule.Value,
                Evidence: rule.Evidence))
            .ToArray();

        if (defaults.Length != normalized.Length)
            return false;

        for (var i = 0; i < defaults.Length; i++)
        {
            if (!string.Equals(defaults[i].Keyword.Trim(), normalized[i].Keyword, StringComparison.OrdinalIgnoreCase))
                return false;
            if (Math.Abs(defaults[i].Value - normalized[i].Value) > 0.0001)
                return false;
        }

        return true;
    }
}
