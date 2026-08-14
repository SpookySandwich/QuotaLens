namespace QuotaLens.Core;

/// <summary>
/// App configuration + provider-instance management, persisted to a JSON file in
/// %LOCALAPPDATA%\QuotaLens. Also performs one-time migration from the old Tauri
/// WebView2 localStorage. Implements IConfig for providers.
/// </summary>
public interface IConfigService : IConfig
{
    IReadOnlyDictionary<string, string> All { get; }

    void Set(string key, string value);
    void SetMany(IReadOnlyDictionary<string, string> values);
    void Remove(string key);
    Task SaveAsync();

    /// Explicit provider instances selected by the user.
    IReadOnlyList<ProviderInstance> Instances { get; }
    ProviderInstance AddInstance(string providerType);
    void RemoveInstance(string id);

    /// Refresh interval in milliseconds (from min_refresh_interval_secs; default 1800s, floor 30s).
    double RefreshMs { get; }
}
