namespace QuotaLens.Core;

public enum ProviderErrorKind
{
    Unknown,
    RateLimited,
}

/// <summary>Thrown by a provider when a fetch fails; the message becomes the snapshot error.</summary>
public sealed class ProviderException : Exception
{
    public ProviderException(string message)
        : this(message, ProviderErrorKind.Unknown)
    {
    }

    public ProviderException(string message, Exception inner)
        : this(message, ProviderErrorKind.Unknown, inner)
    {
    }

    public ProviderException(string message, ProviderErrorKind kind)
        : base(message)
    {
        Kind = kind;
    }

    public ProviderException(string message, ProviderErrorKind kind, Exception inner)
        : base(message, inner)
    {
        Kind = kind;
    }

    public ProviderErrorKind Kind { get; }

    public static ProviderException RateLimited(string message) =>
        new(message, ProviderErrorKind.RateLimited);
}

/// <summary>
/// Read-only config access for providers. Scoped lookup resolves only the
/// per-instance key (`{instanceId}.{key}`). Legacy bare provider keys are
/// migrated into the first matching instance by <see cref="IConfigService"/>.
/// </summary>
public interface IConfig
{
    string Get(string key, string fallback = "");
    string GetScoped(string instanceId, string key, string fallback = "");
    bool HasScoped(string instanceId, string key);
    bool GetBool(string key, bool fallback = false);
}

/// <summary>
/// A provider adapter. One instance per provider TYPE; called with the concrete
/// instanceId so per-instance scoped config works. Throw ProviderException on failure.
/// </summary>
public interface IProvider
{
    string Type { get; }          // e.g. "deepseek"
    string Name { get; }          // display name
    string SourceLabel { get; }   // e.g. "DeepSeek API"
    Confidence Confidence { get; }

    Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct);
}
