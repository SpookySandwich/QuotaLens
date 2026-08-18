namespace QuotaLens.Core;

public enum ProviderErrorKind
{
    Unknown,
    RateLimited,

    /// <summary>The active credential/session is missing, expired, or rejected.</summary>
    AuthenticationRequired,

    /// <summary>The user must fix a setting; no sign-in will help.</summary>
    Misconfigured,

    /// <summary>Not actionable at all — the card must not offer sign-in.</summary>
    Unsupported,
}

/// <summary>Thrown by a provider when a fetch fails; the message becomes the snapshot error.</summary>
public sealed class ProviderException : Exception
{
    public ProviderException(string message)
        : this(message, InferKind(message))
    {
    }

    public ProviderException(string message, Exception inner)
        : this(message, InferKind(message), inner)
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

    /// <summary>Optional structural recovery supplied by source orchestration.</summary>
    public ProviderRecoveryAction? RecoveryAction { get; init; }

    public static ProviderException RateLimited(string message) =>
        new(message, ProviderErrorKind.RateLimited);

    /// <summary>
    /// Providers already share stable error prefixes for user-facing localization. Use
    /// the same contract for behavior so every "Login required" failure is actionable
    /// without duplicating an error-kind argument across dozens of adapters.
    /// </summary>
    internal static ProviderErrorKind InferKind(string message)
    {
        if (message.StartsWith("Login required", StringComparison.OrdinalIgnoreCase))
            return ProviderErrorKind.AuthenticationRequired;
        if (message.StartsWith("Not configured", StringComparison.OrdinalIgnoreCase))
            return ProviderErrorKind.Misconfigured;
        return ProviderErrorKind.Unknown;
    }
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

    /// <summary>
    /// Ordered data sources, best first. Empty means the provider is single-source and
    /// <see cref="FetchAsync"/> IS the source (the legacy shape). Multi-source providers
    /// delegate their FetchAsync to <see cref="ProviderSourceRunner"/>.
    /// </summary>
    IReadOnlyList<IProviderSource> Sources => Array.Empty<IProviderSource>();

    Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct);
}
