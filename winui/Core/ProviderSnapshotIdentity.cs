namespace QuotaLens.Core;

public readonly record struct ProviderPlanIdentity(string? PlanId, string? PlanName)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(PlanId) && string.IsNullOrWhiteSpace(PlanName);
}

/// <summary>
/// Keeps provider/instance identity separate from provider-owned plan identity and
/// composes the user-facing title in one place.
/// </summary>
public static class ProviderSnapshotIdentity
{
    private static readonly HashSet<string> MissingPlanNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "?",
        "unknown",
        "no plan",
        "none",
        "null",
        "n/a",
        "not available",
        "inactive",
        "expired",
    };

    public static void Normalize(string providerType, ProviderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var providerName = Catalog.ProviderName(providerType);
        var planName = ResolvePlanName(providerName, providerName, snapshot);

        if (snapshot.EntitlementStatus == EntitlementStatus.Expired)
        {
            planName = null;
            snapshot.PlanId = null;
        }

        snapshot.PlanName = planName;
        snapshot.Name = ComposeTitle(providerType, providerName, snapshot);
    }

    public static string ComposeTitle(string instanceName, ProviderSnapshot snapshot)
    {
        var providerType = Catalog.ProviderTypeFromId(snapshot.ProviderId);
        return ComposeTitle(providerType, instanceName, snapshot);
    }

    public static string ComposeTitle(string providerType, string instanceName, ProviderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var identity = string.IsNullOrWhiteSpace(instanceName)
            ? Catalog.ProviderName(providerType)
            : instanceName.Trim();
        var providerName = Catalog.ProviderName(providerType);
        var planName = ResolvePlanName(providerName, identity, snapshot);
        return snapshot.EntitlementStatus == EntitlementStatus.Expired
            || string.IsNullOrWhiteSpace(planName)
                ? identity
                : $"{identity} · {planName}";
    }

    /// <summary>Returns only explicit provider-owned plan identity for pricing/ranking.</summary>
    public static string? PlanLookupText(string providerType, ProviderSnapshot snapshot)
    {
        var identity = PlanIdentity(providerType, snapshot);
        if (identity.PlanId is null)
            return identity.PlanName;
        return identity.PlanName is null ? identity.PlanId : $"{identity.PlanId} {identity.PlanName}";
    }

    /// <summary>Returns canonical plan ID and display name without flattening their semantics.</summary>
    public static ProviderPlanIdentity PlanIdentity(string providerType, ProviderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.EntitlementStatus == EntitlementStatus.Expired)
            return default;

        var providerName = Catalog.ProviderName(providerType);
        return new ProviderPlanIdentity(
            Clean(snapshot.PlanId),
            ResolvePlanName(providerName, providerName, snapshot));
    }

    public static string? NormalizePlanName(string providerName, string? candidate)
    {
        var planName = Clean(candidate);
        if (planName is null || MissingPlanNames.Contains(planName))
            return null;

        var cleanProviderName = Clean(providerName);
        if (cleanProviderName is null)
            return planName;

        if (planName.Equals(cleanProviderName, StringComparison.OrdinalIgnoreCase)
            || cleanProviderName.EndsWith(" " + planName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var providerPrefix = cleanProviderName + " ";
        if (planName.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            planName = Clean(planName[providerPrefix.Length..]);
            if (planName is null || MissingPlanNames.Contains(planName))
                return null;
        }

        return planName;
    }

    private static string? ResolvePlanName(
        string providerName,
        string instanceName,
        ProviderSnapshot snapshot)
    {
        var explicitPlanName = NormalizePlanName(providerName, snapshot.PlanName);
        if (explicitPlanName is not null)
            return explicitPlanName;

        // Snapshots persisted by older builds encoded plan identity only in Name.
        // Read that legacy title at this boundary so pricing, privacy, and title
        // composition no longer need provider-specific compatibility branches.
        return PlanNameFromLegacyTitle(providerName, snapshot.Name)
            ?? PlanNameFromLegacyTitle(instanceName, snapshot.Name);
    }

    private static string? PlanNameFromLegacyTitle(string identity, string? title)
    {
        var cleanIdentity = Clean(identity);
        var cleanTitle = Clean(title);
        if (cleanIdentity is null || cleanTitle is null)
            return null;

        var prefix = cleanIdentity + " · ";
        if (!cleanTitle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return NormalizePlanName(cleanIdentity, cleanTitle[prefix.Length..]);
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var clean = value.Trim().Trim('·').Trim();
        return clean.Length == 0 ? null : clean;
    }
}
