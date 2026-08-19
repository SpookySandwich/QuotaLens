namespace QuotaLens.Core;

public enum Severity { Good, Busy, Warning, Critical }

public readonly record struct ProviderAvailability(
    ProviderAvailabilityKind Kind,
    double Percent);

/// <summary>Pure quota math + formatting, ported faithfully from the Rust/TS originals.</summary>
public static class Quota
{
    public static string Truncate(string s, int n) =>
        s.Length <= n ? s : new string(s.Take(n).ToArray());

    public static double ClampPercent(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 100.0) : 0.0;

    public static double UsedPercentFromRemaining(double remainingPercent) =>
        ClampPercent(100.0 - remainingPercent);

    public static double UtilizationToUsedPercent(double? utilization)
    {
        var value = utilization ?? 0.0;
        return value <= 1.0 ? ClampPercent(value * 100.0) : ClampPercent(value);
    }

    public static double AvailablePct(double usedPct) => Math.Clamp(100.0 - usedPct, 0.0, 100.0);

    public static string DisplayPct(double pct)
    {
        if (!double.IsFinite(pct)) return "0%";
        if (pct > 999) return ">999%";
        return $"{Math.Round(pct)}%";
    }

    public static string FormatUsd(double amount)
    {
        if (!double.IsFinite(amount))
            return "$0";

        var abs = Math.Abs(amount);
        var formatted = abs >= 100
            ? abs.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : abs.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return amount < 0 ? $"-${formatted}" : $"${formatted}";
    }

    // Severity cutoffs by AVAILABLE percent: <=5 critical, <=25 warning, <=50 busy, else good.
    public static Severity SeverityForAvailable(double availablePct) =>
        availablePct <= 5 ? Severity.Critical
        : availablePct <= 25 ? Severity.Warning
        : availablePct <= 50 ? Severity.Busy
        : Severity.Good;

    /// <summary>Stale when older than 2x the refresh interval (min 60s floor).</summary>
    public static bool IsStale(DateTimeOffset updatedAt, double refreshMs)
    {
        var ageMs = (DateTimeOffset.Now - updatedAt).TotalMilliseconds;
        if (!double.IsFinite(ageMs)) return false;
        return ageMs > Math.Max(60_000.0, refreshMs) * 2.0;
    }

    /// <summary>Headline available percentage derived only from structured snapshot data.</summary>
    public static double ProviderAvailability(ProviderSnapshot snapshot)
        => ProviderAvailabilityState(snapshot).Percent;

    public static ProviderAvailability ProviderAvailabilityState(ProviderSnapshot s)
    {
        // An explicit provider-level unlimited contract is authoritative. Usage,
        // activity, or bonus windows can still be displayed, but cannot turn an
        // unlimited entitlement into a finite one.
        if (s.AvailabilityKind == ProviderAvailabilityKind.Unlimited)
            return new ProviderAvailability(ProviderAvailabilityKind.Unlimited, 100);

        var groupedWindows = ProviderSnapshotWindows.AllWindows(s)
            .Where(window => window.Kind == RateWindowKind.Quota
                && !string.IsNullOrWhiteSpace(window.AvailabilityGroup))
            .GroupBy(window => window.AvailabilityGroup!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Min(window => AvailablePct(window.UsedPercent)))
            .ToList();
        if (groupedWindows.Count > 0)
        {
            // Windows inside a product/family are joint constraints, while the
            // products/families themselves are alternative usable capacity.
            return new ProviderAvailability(
                ProviderAvailabilityKind.Finite,
                groupedWindows.Max());
        }

        var priority = s.ModelQuotas.Where(ModelQuotaPolicy.CountsForProviderAvailability).ToList();
        if (priority.Count > 0)
        {
            return new ProviderAvailability(
                ProviderAvailabilityKind.Finite,
                priority.Max(quota => ClampPercent(quota.RemainingPercent)));
        }

        var windows = ProviderSnapshotWindows
            .AvailabilityWindows(s)
            .Select(window => AvailablePct(window.UsedPercent))
            .ToList();
        if (windows.Count > 0)
            return new ProviderAvailability(ProviderAvailabilityKind.Finite, windows.Min());

        // Activity, spend, and raw-count metrics have no finite quota denominator.
        // Keep availability explicitly unknown, but use a neutral percentage so an
        // informational value never masquerades as an exhausted 0%-available quota.
        return ProviderSnapshotWindows.AllWindows(s).Any(window => window.Kind == RateWindowKind.Informational)
            ? new ProviderAvailability(ProviderAvailabilityKind.Unknown, 100)
            : new ProviderAvailability(ProviderAvailabilityKind.Unknown, 0);
    }
}
