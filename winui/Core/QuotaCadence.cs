namespace QuotaLens.Core;

public enum QuotaCadence
{
    None,
    FiveHour,
    Weekly,
    Monthly,
}

/// <summary>
/// Shared 5h / weekly / monthly classification for snapshot windows. Labels such as
/// "credits" are not treated as monthly unless the window length or an explicit
/// month/token-plan hint says so.
/// </summary>
public static class QuotaCadencePolicy
{
    public const long FiveHourMinutes = 5 * 60;
    public const long DayMinutes = 24 * 60;
    public const long WeeklyMinutes = 7 * DayMinutes;
    public const long MonthlyMinutes = 30 * DayMinutes;

    public static QuotaCadence For(string? label, long? windowMinutes, double minutesUntil = double.PositiveInfinity)
    {
        if (IsOverallEffectiveLabel(label) || IsNonCadenceLabel(label))
            return QuotaCadence.None;

        var fromLabel = FromLabel(label);
        if (fromLabel != QuotaCadence.None)
            return fromLabel;

        if (windowMinutes is > 0)
            return FromMinutes(windowMinutes.Value);

        return double.IsFinite(minutesUntil) && minutesUntil >= 0
            ? FromMinutes(minutesUntil)
            : QuotaCadence.None;
    }

    public static QuotaCadence FromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || IsOverallEffectiveLabel(label) || IsNonCadenceLabel(label))
            return QuotaCadence.None;

        var normalized = label.ToLowerInvariant();
        if (normalized.Contains("month", StringComparison.Ordinal))
            return QuotaCadence.Monthly;
        if (normalized.Contains("5h", StringComparison.Ordinal)
            || normalized.Contains("hour", StringComparison.Ordinal)
            || normalized.Contains("today", StringComparison.Ordinal)
            || normalized.Contains("daily", StringComparison.Ordinal)
            || normalized.Contains("short", StringComparison.Ordinal))
        {
            return QuotaCadence.FiveHour;
        }

        if (normalized.Contains("7d", StringComparison.Ordinal)
            || normalized.Contains("week", StringComparison.Ordinal))
        {
            return QuotaCadence.Weekly;
        }

        if (normalized.Contains("token plan", StringComparison.Ordinal)
            || normalized.Contains("total quota", StringComparison.Ordinal))
            return QuotaCadence.Monthly;

        return QuotaCadence.None;
    }

    public static QuotaCadence FromMinutes(double minutes)
    {
        if (!double.IsFinite(minutes) || minutes <= 0)
            return QuotaCadence.None;
        if (minutes <= DayMinutes)
            return QuotaCadence.FiveHour;
        if (minutes <= 14 * DayMinutes)
            return QuotaCadence.Weekly;
        return QuotaCadence.Monthly;
    }

    public static bool HasCadenceHint(string? label, long? windowMinutes) =>
        !IsNonCadenceLabel(label)
        && !IsOverallEffectiveLabel(label)
        && (windowMinutes is > 0 || FromLabel(label) != QuotaCadence.None);

    public static bool IsOverallEffectiveLabel(string? label) =>
        !string.IsNullOrWhiteSpace(label)
        && (label.Equals("Effective Usage", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Usage", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Spend caps, balances, and generic credit rows are not 5h/weekly/monthly
    /// pools. Their reset time or billing period must not invent a cadence.
    /// </summary>
    public static bool IsNonCadenceLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return false;

        var normalized = label.Trim().ToLowerInvariant();
        return normalized is "on-demand" or "ondemand" or "on demand"
            or "credits" or "balance" or "account balance"
            or "requests";
    }

    public static string EffectiveLabel(QuotaCadence cadence) => cadence switch
    {
        QuotaCadence.FiveHour => "Effective 5h",
        QuotaCadence.Weekly => "Effective Weekly",
        QuotaCadence.Monthly => "Effective Monthly",
        _ => "Effective Usage",
    };

    public static long DefaultWindowMinutes(QuotaCadence cadence) => cadence switch
    {
        QuotaCadence.FiveHour => FiveHourMinutes,
        QuotaCadence.Weekly => WeeklyMinutes,
        QuotaCadence.Monthly => MonthlyMinutes,
        _ => 0,
    };

    public static int ResetTier(QuotaCadence cadence) => cadence switch
    {
        QuotaCadence.FiveHour => ProviderPriority.ShortResetTier,
        QuotaCadence.Weekly => ProviderPriority.MediumResetTier,
        QuotaCadence.Monthly => ProviderPriority.LongResetTier,
        _ => ProviderPriority.NoResetTier,
    };
}
