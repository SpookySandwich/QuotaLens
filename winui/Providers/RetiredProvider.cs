using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>
/// Keeps persisted cards for retired integrations removable and self-explanatory
/// without retaining any credential-bearing endpoint or transport implementation.
/// </summary>
internal sealed class RetiredProvider(string type, string retirementMessage) : IProvider
{
    public string Type { get; } = type;
    public string Name => Catalog.ProviderName(Type);
    public string SourceLabel => "Retired provider";
    public Confidence Confidence => Confidence.Unofficial;

    public Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct) =>
        Task.FromException<ProviderSnapshot>(new ProviderException(retirementMessage));
}
