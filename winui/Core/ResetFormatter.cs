using System.Globalization;
using QuotaLens.Helpers;

namespace QuotaLens.Core;

/// <summary>
/// The only reset-time presentation policy in the app. Providers supply an ISO
/// instant; every dashboard surface receives the same compact wording.
/// </summary>
public static class ResetFormatter
{
    public static string? FormatCaption(RateWindow window, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        return FormatReset(window.ResetsAt, now) ?? window.DetailText;
    }

    public static string? FormatReset(string? iso, DateTimeOffset? now = null)
    {
        var duration = FormatDurationUntil(iso, now);
        if (duration is null)
            return null;
        return duration == "now"
            ? I18n.T("quota.resetsNowCompact")
            : I18n.T("quota.resetsInCompact", "duration", duration);
    }

    /// <summary>Compact duration with at most two significant units: 2d 4h, 3h 12m, 8m.</summary>
    public static string? FormatDurationUntil(string? iso, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(iso)
            || !DateTimeOffset.TryParse(
                iso,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var when))
        {
            return null;
        }

        var remaining = when - (now ?? DateTimeOffset.UtcNow);
        if (remaining <= TimeSpan.Zero)
            return "now";

        var totalMinutes = (long)Math.Floor(remaining.TotalMinutes);
        if (totalMinutes < 1)
            return "<1m";

        var days = totalMinutes / (24 * 60);
        var hours = totalMinutes / 60 % 24;
        var minutes = totalMinutes % 60;
        if (days > 0)
            return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        if (hours > 0)
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        return $"{minutes}m";
    }
}
