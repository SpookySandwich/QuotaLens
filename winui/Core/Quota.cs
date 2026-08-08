using System.Globalization;

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

    // Severity cutoffs by AVAILABLE percent: <=5 critical, <=25 warning, <=50 busy, else good.
    public static Severity SeverityForAvailable(double availablePct) =>
        availablePct <= 5 ? Severity.Critical
        : availablePct <= 25 ? Severity.Warning
        : availablePct <= 50 ? Severity.Busy
        : Severity.Good;

    /// <summary>"now" / "&lt; 1h" / "3h" / "2d 4h" — mirrors fmtReset.</summary>
    public static string? FmtReset(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            return null;
        var ms = (when - DateTimeOffset.UtcNow).TotalMilliseconds;
        if (ms <= 0) return "now";
        var h = (int)Math.Floor(ms / 3_600_000.0);
        if (h < 1) return "< 1h";
        return h > 24 ? $"{h / 24}d {h % 24}h" : $"{h}h";
    }

    /// <summary>Stale when older than 2x the refresh interval (min 60s floor).</summary>
    public static bool IsStale(DateTimeOffset updatedAt, double refreshMs)
    {
        var ageMs = (DateTimeOffset.Now - updatedAt).TotalMilliseconds;
        if (!double.IsFinite(ageMs)) return false;
        return ageMs > Math.Max(60_000.0, refreshMs) * 2.0;
    }

    /// <summary>Headline available % for sorting/hero — antigravity uses its priority families.</summary>
    public static double ProviderAvailability(string id, ProviderSnapshot s, IConfig? config = null)
        => ProviderAvailabilityState(id, s, config).Percent;

    public static ProviderAvailability ProviderAvailabilityState(
        string id,
        ProviderSnapshot s,
        IConfig? config = null)
    {
        var providerType = Catalog.ProviderTypeForInstance(id, config);
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

        if (providerType == "antigravity")
        {
            var priority = s.ModelQuotas.Where(ModelQuotaPolicy.CountsForProviderAvailability).ToList();
            if (priority.Count > 0)
            {
                return new ProviderAvailability(
                    ProviderAvailabilityKind.Finite,
                    priority.Max(quota => ClampPercent(quota.RemainingPercent)));
            }
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
