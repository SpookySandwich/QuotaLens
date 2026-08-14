namespace QuotaLens.Core;

/// <summary>
/// Shared orchestration for multi-source providers: use the user's selected source
/// (when set and available), otherwise the first available source in priority order.
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

        var selected = config.GetScoped(instanceId, SourceConfigKey);
        var selectedSource = sources.FirstOrDefault(source =>
            string.Equals(source.Id, selected, StringComparison.OrdinalIgnoreCase));
        if (selectedSource is not null && selectedSource.IsAvailable(instanceId, config))
            return await selectedSource.FetchAsync(instanceId, config, ct).ConfigureAwait(false);

        foreach (var source in sources)
        {
            if (source.IsAvailable(instanceId, config))
                return await source.FetchAsync(instanceId, config, ct).ConfigureAwait(false);
        }

        throw new ProviderException("Login required: no data source is signed in. Add credentials in Settings.");
    }
}
