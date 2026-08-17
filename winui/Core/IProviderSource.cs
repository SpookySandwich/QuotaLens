namespace QuotaLens.Core;

/// <summary>
/// A named, probe-able data origin a provider can fetch from. A provider may expose
/// several (e.g. Kimi's desktop App and its CLI); each is selected per instance and
/// tried in priority order by <see cref="ProviderSourceRunner"/>.
/// </summary>
public interface IProviderSource
{
    /// <summary>Stable id for per-instance selection, e.g. "app" or "cli".</summary>
    string Id { get; }

    /// <summary>Human name shown in the source selector, e.g. "App" or "CLI".</summary>
    string Name { get; }

    /// <summary>
    /// Config field keys this source configures. Empty means auto-detect (no inputs).
    /// </summary>
    IReadOnlyList<string> ConfigFieldKeys => Array.Empty<string>();

    /// <summary>
    /// I18n key of a caveat shown as a hover hint on this source in the source
    /// selector (e.g. "only works while the app is in use"). Null = nothing to flag.
    /// </summary>
    string? AttentionNote => null;

    /// <summary>
    /// Recovery offered when this source cannot provide data. The shared source
    /// runner carries it to an error snapshot; the UI never re-probes this source.
    /// </summary>
    ProviderRecoveryAction? UnavailableRecovery => null;

    /// <summary>
    /// Credential/session files whose changes make this source worth refetching.
    /// The refresh service watches these declaratively for every provider.
    /// </summary>
    IReadOnlyList<string> WatchPaths(string instanceId, IConfig config) => Array.Empty<string>();

    /// <summary>True when this source's credentials are present and usable.</summary>
    bool IsAvailable(string instanceId, IConfig config);

    Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct);
}
