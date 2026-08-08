using System.Text.RegularExpressions;

namespace QuotaLens.Helpers;

public static partial class SensitiveDisplay
{
    public const string HiddenText = "••••";
    public const string HiddenBalanceText = "Balance hidden";

    public static string ProviderName(string name, bool hidden) =>
        hidden ? RemoveEmails(name) : name;

    public static bool ContainsSensitiveText(string value) =>
        !string.IsNullOrWhiteSpace(value) && EmailRegex().IsMatch(value);

    public static string AccountName(string name, int index, bool hidden) =>
        hidden ? $"Account {index + 1}" : name;

    public static string BalanceAmount(string value, bool hidden) =>
        hidden ? HiddenText : value;

    public static string? BalanceDetail(string? value, bool hidden)
    {
        if (!hidden)
            return value;

        return string.IsNullOrWhiteSpace(value) ? null : HiddenText;
    }

    public static string InlineBalance(string value, bool hidden) =>
        hidden ? HiddenBalanceText : value;

    public static string? InlineBalanceDetail(string? value, bool hidden) =>
        hidden ? null : value;

    public static string MaskEmails(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return EmailRegex().Replace(value, "••••@••••");
    }

    private static string RemoveEmails(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var stripped = EmailRegex().Replace(value, "");
        while (stripped.Contains(" ·  · ", StringComparison.Ordinal))
            stripped = stripped.Replace(" ·  · ", " · ", StringComparison.Ordinal);

        var clean = stripped.Trim().Trim('·').Trim();
        return string.IsNullOrWhiteSpace(clean) ? HiddenText : clean;
    }

    [GeneratedRegex(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
}
