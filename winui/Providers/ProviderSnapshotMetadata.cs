using QuotaLens.Core;

namespace QuotaLens.Providers;

internal static class ProviderSnapshotMetadata
{
    public static ProviderSnapshot Apply(
        string providerType,
        string defaultSourceLabel,
        Confidence defaultConfidence,
        ProviderSnapshot snapshot,
        bool replaceSourceLabel = false)
    {
        if (replaceSourceLabel || string.IsNullOrWhiteSpace(snapshot.SourceLabel))
            snapshot.SourceLabel = defaultSourceLabel;

        snapshot.Confidence = defaultConfidence;

        if (ProviderContracts.TryGet(providerType, out var contract) && contract is not null)
        {
            var source = contract.SourceFor(snapshot.SourceLabel);
            snapshot.SourceKind = source.SourceKind;
            snapshot.ContractStability = source.Stability;
            snapshot.Confidence = source.Stability switch
            {
                ProviderContractStability.Official => defaultConfidence,
                ProviderContractStability.DocumentedCli or ProviderContractStability.UpstreamCompatibility => Confidence.SemiOfficial,
                _ => Confidence.Unofficial,
            };
        }

        ProviderSnapshotIdentity.Normalize(providerType, snapshot);

        return snapshot;
    }

    public static ProviderSnapshot Apply(IProvider provider, ProviderSnapshot snapshot) =>
        Apply(provider.Type, provider.SourceLabel, provider.Confidence, snapshot);
}
