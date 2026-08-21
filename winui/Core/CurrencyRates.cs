namespace QuotaLens.Core;

/// <summary>
/// The single place balances are turned into comparable dollars. QuotaLens never
/// calls an FX service: a quota dashboard has to keep working offline and must not
/// add third-party traffic to a refresh, so these are deliberate fixed estimates
/// the user has accepted. They exist to make balances rankable against monthly
/// plan prices, never to state a price.
/// </summary>
public static class CurrencyRates
{
    /// Pinned: the value view's existing CNY expectations are calibrated to it.
    public const double CnyPerUsd = 7.2;
    public const double UsdPerEur = 1.1;

    /// <summary>
    /// Converts a provider balance to USD, or returns null when the unit is not
    /// money we can price. Credits, points, DIEM and arbitrary provider-reported
    /// strings have no defensible dollar figure, and pretending they do let a raw
    /// credit count outrank a real subscription in the shared value ordering.
    /// </summary>
    public static double? ToUsd(double amount, string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return null;

        return currency.Trim().ToUpperInvariant() switch
        {
            "USD" => amount,
            "CNY" or "RMB" => amount / CnyPerUsd,
            "EUR" => amount * UsdPerEur,
            _ => null,
        };
    }
}
