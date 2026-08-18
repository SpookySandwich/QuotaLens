namespace QuotaLens.Core;

/// <summary>
/// Read-only view of configuration with one instance's unsaved dialog values layered
/// over persisted settings. Interactive actions can therefore honor what the user just
/// typed without committing a partially edited form.
/// </summary>
public sealed class OverlayConfig : IConfig
{
    private readonly IConfig _baseConfig;
    private readonly string _instanceId;
    private readonly IReadOnlyDictionary<string, string> _globalValues;
    private readonly IReadOnlyDictionary<string, string> _scopedValues;

    public OverlayConfig(
        IConfig baseConfig,
        string instanceId,
        IReadOnlyDictionary<string, string>? globalValues = null,
        IReadOnlyDictionary<string, string>? scopedValues = null)
    {
        _baseConfig = baseConfig;
        _instanceId = instanceId;
        _globalValues = globalValues ?? new Dictionary<string, string>();
        _scopedValues = scopedValues ?? new Dictionary<string, string>();
    }

    public string Get(string key, string fallback = "") =>
        _globalValues.TryGetValue(key, out var value) ? value : _baseConfig.Get(key, fallback);

    public string GetScoped(string instanceId, string key, string fallback = "") =>
        string.Equals(instanceId, _instanceId, StringComparison.OrdinalIgnoreCase)
        && _scopedValues.TryGetValue(key, out var value)
            ? value
            : _baseConfig.GetScoped(instanceId, key, fallback);

    public bool HasScoped(string instanceId, string key) =>
        string.Equals(instanceId, _instanceId, StringComparison.OrdinalIgnoreCase)
        && _scopedValues.ContainsKey(key)
        || _baseConfig.HasScoped(instanceId, key);

    public bool GetBool(string key, bool fallback = false)
    {
        if (!_globalValues.TryGetValue(key, out var value))
            return _baseConfig.GetBool(key, fallback);

        return value.Trim().ToLowerInvariant() is "true" or "1" or "yes";
    }
}
