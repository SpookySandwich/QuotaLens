using QuotaLens.Core;
using QuotaLens.Services;

namespace QuotaLens.Providers;

/// <summary>
/// Shared provider adapter for browser-session providers whose capture logic
/// lives in <see cref="WebLoginService"/>.
/// </summary>
public sealed class WebViewLoginProvider : IProvider
{
    public WebViewLoginProvider(string type)
    {
        Type = type;
        Name = Catalog.ProviderName(type);
    }

    public string Type { get; }
    public string Name { get; }
    public string SourceLabel => $"{Name} WebView";
    public Confidence Confidence => Confidence.Official;

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var svc = WebLoginService.Instance
            ?? throw new ProviderException("Not available: WebLoginService not initialized");

        var snapshot = await svc.FetchAsync(instanceId, Type, config).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return snapshot;
    }
}
