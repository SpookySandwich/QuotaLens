using System.Globalization;

namespace QuotaLens.Core;

/// <summary>
/// Shared, culture-safe string and number formatting. Provider response parsers
/// previously carried per-file copies of Clean / DisplayName / Fmt*; this is the
/// single home for them. Culture is pinned to Invariant so provider values never
/// render differently across locales.
/// </summary>
public static class TextUtil
{
    /// <summary>
    /// Normalizes a possibly-whitespace or quoted config/JSON value into a clean
    /// string, or null when it is empty. Symmetric with how the config store treats
    /// "empty" (an empty value IS empty).
    /// </summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if ((trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            || (trimmed.StartsWith("'") && trimmed.EndsWith("'")))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    /// <summary>Turns a slug-like plan/tier name into Title Case ("tier_49" -> "Tier 49").</summary>
    public static string? DisplayName(string? value)
    {
        var clean = Clean(value);
        if (clean is null)
            return null;

        var spaced = clean
            .Replace("_", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal);
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
    }

    public static string Fmt0(double value) => value.ToString("F0", CultureInfo.InvariantCulture);
    public static string Fmt1(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
    public static string Fmt2(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    public static string FmtCount(double value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
