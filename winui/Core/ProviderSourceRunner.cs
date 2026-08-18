namespace QuotaLens.Core;

/// <summary>
/// Shared orchestration for multi-source providers: honor the user's selected
/// source when it has credentials. Do not silently switch them to a different
/// login (e.g. App → Web). If nothing is selected, use the first available source.
/// Providers with no sources fall through to their own <see cref="IProvider.FetchAsync"/>.
/// </summary>
public static class ProviderSourceRunner
{
    /// <summary>Per-instance config key holding app, cli, or web.</summary>
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

        var duplicateMode = sources
            .GroupBy(source => source.Mode)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateMode is not null)
            throw new InvalidOperationException($"Provider '{provider.Type}' declares more than one {duplicateMode.Key.DisplayName()} source.");

        var selectedId = config.GetScoped(instanceId, SourceConfigKey);
        var selected = sources.Find(selectedId);
        if (selected is not null)
        {
            await PrepareAsync(selected, instanceId, config, ct).ConfigureAwait(false);
            if (selected.IsAvailable(instanceId, config))
            {
                return await FetchFromAsync(
                        selected,
                        selected.Mode.ConfigValue(),
                        usedFallback: false,
                        instanceId,
                        config,
                        ct)
                    .ConfigureAwait(false);
            }

            var connectionKind = selected.ConnectionAction?.Kind;
            throw new ProviderException(
                connectionKind == ProviderConnectionActionKind.SignIn
                    ? $"Login required: selected {selected.Mode.DisplayName()} data source is not signed in."
                    : $"Not available: selected {selected.Mode.DisplayName()} data source is not ready.",
                connectionKind == ProviderConnectionActionKind.SignIn
                    ? ProviderErrorKind.AuthenticationRequired
                    : ProviderErrorKind.Unknown)
            {
                RecoveryAction = selected.UnavailableRecovery,
            };
        }

        var preferred = sources[0];
        foreach (var source in sources)
        {
            try
            {
                await PrepareAsync(source, instanceId, config, ct).ConfigureAwait(false);
            }
            catch (ProviderException)
            {
                // Automatic selection is allowed to fall through to the next source.
                // An explicit selection above remains strict and surfaces preparation
                // failures such as a bad app path.
                continue;
            }

            if (source.IsAvailable(instanceId, config))
            {
                return await FetchFromAsync(
                        source,
                        requestedSourceId: null,
                        usedFallback: !ReferenceEquals(source, preferred),
                        instanceId,
                        config,
                        ct)
                    .ConfigureAwait(false);
            }
        }

        var preferredConnectionKind = preferred.ConnectionAction?.Kind;
        throw new ProviderException(
            preferredConnectionKind == ProviderConnectionActionKind.SignIn
                ? "Login required: no signed-in data source is ready."
                : "Not available: no data source is ready. Open the app or pick another source.",
            preferredConnectionKind == ProviderConnectionActionKind.SignIn
                ? ProviderErrorKind.AuthenticationRequired
                : ProviderErrorKind.Unknown)
        {
            RecoveryAction = preferred.UnavailableRecovery,
        };
    }

    private static Task PrepareAsync(
        IProviderSource source,
        string instanceId,
        IConfig config,
        CancellationToken ct) =>
        source.ConnectionAction?.PrepareAsync(instanceId, config, ct) ?? Task.CompletedTask;

    private static async Task<ProviderSnapshot> FetchFromAsync(
        IProviderSource source,
        string? requestedSourceId,
        bool usedFallback,
        string instanceId,
        IConfig config,
        CancellationToken ct)
    {
        try
        {
            var snapshot = await source.FetchAsync(instanceId, config, ct).ConfigureAwait(false);
            snapshot.SourceState = new ProviderSourceState(
                requestedSourceId,
                source.Mode.ConfigValue(),
                usedFallback);
            snapshot.RecoveryAction = null;
            return snapshot;
        }
        catch (ProviderException error) when (
            error.Kind == ProviderErrorKind.AuthenticationRequired
            && error.RecoveryAction is null
            && source.UnavailableRecovery is { } recovery)
        {
            throw new ProviderException(error.Message, error.Kind, error)
            {
                RecoveryAction = recovery,
            };
        }
    }
}
