namespace QuotaLens.Core;

/// <summary>
/// Facade the UI binds to. Implemented during integration (RefreshService): owns
/// the provider instances, their latest snapshots, the refresh scheduler + rate-limit
/// backoff, and exposes config + actions. The UI never talks to providers directly.
/// </summary>
public interface IProviderService
{
    IConfigService Config { get; }

    /// Explicit provider instances selected by the user.
    IReadOnlyList<ProviderInstance> Instances { get; }

    /// Latest snapshot per instanceId; null means "loading / not fetched yet".
    ProviderSnapshot? GetSnapshot(string instanceId);

    bool IsRefreshing(string instanceId);

    /// Raised (on the UI thread) when a provider's snapshot changes.
    event EventHandler<ProviderSnapshot>? SnapshotUpdated;
    /// Raised when a provider's refreshing state flips. Arg = instanceId.
    event EventHandler<string>? RefreshingChanged;
    /// Raised when the instance list changes (add/remove).
    event EventHandler? InstancesChanged;
    /// Rate-limit backoff notice for observers. (instanceId, secondsLeft, attempt)
    event EventHandler<(string Id, int SecondsLeft, int Attempt)>? RateLimited;

    Task RefreshAllAsync();
    Task RefreshAsync(string instanceId);

    ProviderInstance AddInstance(string providerType, bool refreshImmediately = true);
    void RemoveInstance(string instanceId);

    /// Launch an IDE provider (antigravity/kiro).
    void LaunchIde(string instanceId);

    /// Open the interactive login window for a webview-login provider instance.
    /// Returns true when the provider captured usable data during the login flow.
    Task<bool> OpenLoginAsync(string instanceId);
}
