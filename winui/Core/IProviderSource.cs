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

    /// <summary>True when this source's credentials are present and usable.</summary>
    bool IsAvailable(string instanceId, IConfig config);

    Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct);
}
