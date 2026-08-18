using QuotaLens.Core;
using QuotaLens.Services;

namespace QuotaLens.Providers;

/// <summary>
/// Shared provider adapter for browser-session providers whose capture logic
/// lives in <see cref="WebLoginService"/>.
/// </summary>
public sealed class WebViewLoginProvider : IProvider
{
    private readonly IReadOnlyList<IProviderSource> _sources;

    public WebViewLoginProvider(string type)
    {
        Type = type;
        Name = Catalog.ProviderName(type);
        var placementFieldKey = Catalog.Fields.TryGetValue(type, out var fields)
            ? fields.FirstOrDefault(field => field.Key.EndsWith("_url", StringComparison.OrdinalIgnoreCase))?.Key
            : null;
        placementFieldKey ??= $"{type}_url";

        _sources = new IProviderSource[]
        {
            new ProviderSource(
                ProviderSourceMode.Web,
                (instanceId, _) => WebLoginService.Instance?.GetCached(instanceId, type) is { Error: null },
                FetchWebAsync,
                configFieldKeys: new[] { placementFieldKey },
                connectionAction: new WebProviderConnectionAction(type, placementFieldKey),
                launchAction: new WebProviderLaunchAction(type, placementFieldKey)),
        };
    }

    public string Type { get; }
    public string Name { get; }
    public string SourceLabel => $"{Name} WebView";
    public Confidence Confidence => Confidence.Official;
    public IReadOnlyList<IProviderSource> Sources => _sources;

    public Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct) =>
        ProviderSourceRunner.FetchAsync(this, _sources, instanceId, config, ct);

    private async Task<ProviderSnapshot> FetchWebAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var svc = WebLoginService.Instance
            ?? throw new ProviderException("Not available: WebLoginService not initialized");

        var snapshot = await svc.FetchAsync(instanceId, Type, config).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return snapshot;
    }
}
