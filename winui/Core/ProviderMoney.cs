namespace QuotaLens.Core;

public enum ProviderMoneyKind
{
    Subscription,
    Balance,
    Estimate,
}

/// <summary>
/// Provider-agnostic monthly money for the Value chart. Every healthy snapshot
/// resolves to a USD amount through the same path: matched plan value, then
/// pay-as-you-go balance, then the shared unknown-plan estimate. The chart must
/// not fall back to a percentage.
/// </summary>
public readonly record struct ProviderMoney(double AmountUsd, ProviderMoneyKind Kind)
{
    public static ProviderMoney For(
        string instanceId,
        ProviderSnapshot snapshot,
        ProviderPriorityScore score,
        IConfig? config = null)
    {
        if (score.IsPayAsYouGo)
            return new(Math.Max(0, score.BalanceAmount), ProviderMoneyKind.Balance);

        if (score.PlanValue > 0)
            return new(score.PlanValue, ProviderMoneyKind.Subscription);

        var providerType = Catalog.ProviderTypeForInstance(instanceId, config);
        if (Catalog.PayAsYouGoProviderTypes.Contains(providerType))
            return new(Math.Max(0, score.BalanceAmount), ProviderMoneyKind.Balance);

        return new(
            PlanValueRules.EstimateMonthlyUsd(providerType, snapshot, config),
            ProviderMoneyKind.Estimate);
    }
}
