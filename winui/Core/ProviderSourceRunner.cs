namespace QuotaLens.Core;

/// <summary>
/// Shared orchestration for multi-source providers: honor the user's selected
/// source when it has credentials. Do not silently switch them to a different
/// login (e.g. App → Web). If nothing is selected, use the first available source.
/// Providers with no sources fall through to their own <see cref="IProvider.FetchAsync"/>.
/// </summary>
public static class ProviderSourceRunner
{
    /// <summary>Per-instance config key holding the selected source id (e.g. "app").</summary>
    public const string SourceConfigKey = "provider_source";

    public static async Task<ProviderSnapshot> FetchAsync(
        IProvider provider,
        IReadOnlyList<IProviderSource> sources,
        string instanceId,
        IConfig config,
        CancellationToken ct)
    {
        if (sources.Count == 0)
            return await provider.FetchAsync(instanceId, config, ct).ConfigureAwait(false);

        var selectedId = config.GetScoped(instanceId, SourceConfigKey);
        var selected = sources.FirstOrDefault(source =>
            string.Equals(source.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        if (selected is not null && selected.IsAvailable(instanceId, config))
            return await selected.FetchAsync(instanceId, config, ct).ConfigureAwait(false);

        foreach (var source in sources)
        {
            if (source.IsAvailable(instanceId, config))
                return await source.FetchAsync(instanceId, config, ct).ConfigureAwait(false);
        }

        throw new ProviderException("Not available: no data source is ready. Open the app or pick another source.");
    }
}
