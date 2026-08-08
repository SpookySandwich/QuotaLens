namespace QuotaLens.Core;

public readonly record struct SnapshotRateWindow(
    string Label,
    double UsedPercent,
    string? ResetsAt,
    long? WindowMinutes);

public static class ProviderSnapshotWindows
{
    public static IEnumerable<RateWindow> AllWindows(ProviderSnapshot snapshot)
    {
        yield return snapshot.Primary;

        if (snapshot.Secondary is not null)
            yield return snapshot.Secondary;

        if (snapshot.Tertiary is not null)
            yield return snapshot.Tertiary;

        foreach (var window in snapshot.AdditionalWindows)
            yield return window;
    }

    public static IEnumerable<SnapshotRateWindow> AvailabilityWindows(ProviderSnapshot snapshot)
    {
        if (snapshot.Primary.Kind == RateWindowKind.Quota)
            yield return FromRateWindow(snapshot.Primary);

        if (snapshot.Secondary is { Kind: RateWindowKind.Quota } secondary)
            yield return FromRateWindow(secondary);

        if (snapshot.Tertiary is { Kind: RateWindowKind.Quota, CountsForAvailability: true } tertiary)
            yield return FromRateWindow(tertiary);

        foreach (var window in snapshot.AdditionalWindows.Where(
                     window => window.Kind == RateWindowKind.Quota && window.CountsForAvailability))
            yield return FromRateWindow(window);
    }

    public static IEnumerable<SnapshotRateWindow> ResetWindows(ProviderSnapshot snapshot)
    {
        if (snapshot.Primary.Kind == RateWindowKind.Quota)
            yield return FromRateWindow(snapshot.Primary);

        if (snapshot.Secondary is { Kind: RateWindowKind.Quota } secondary)
            yield return FromRateWindow(secondary);

        if (snapshot.Tertiary is { Kind: RateWindowKind.Quota } tertiary)
            yield return FromRateWindow(tertiary);

        foreach (var window in snapshot.AdditionalWindows.Where(window => window.Kind == RateWindowKind.Quota))
            yield return FromRateWindow(window);
    }

    private static SnapshotRateWindow FromRateWindow(RateWindow window) =>
        new(window.Label, window.UsedPercent, window.ResetsAt, window.WindowMinutes);
}
