using System.Globalization;
using System.Text.RegularExpressions;
using QuotaLens.Core;

namespace QuotaLens.Providers;

internal static partial class ProviderConfig
{
    /// <summary>Reads a per-instance config value and nothing else (empty IS empty).</summary>
    public static string? Scoped(string instanceId, IConfig config, string configKey) =>
        Clean(config.GetScoped(instanceId, configKey));

    /// <summary>
    /// The single env-fallback rule every provider uses: the typed per-instance config
    /// value wins; otherwise the first set environment variable mapped to that field
    /// (see <see cref="Catalog.FieldEnvironment"/>); otherwise the default.
    /// </summary>
    public static string? Resolve(
        string instanceId,
        IConfig config,
        string providerType,
        string fieldKey,
        string? defaultValue = null)
    {
        var configured = Scoped(instanceId, config, fieldKey);
        if (configured is not null)
            return configured;

        foreach (var envKey in EnvironmentKeysFor(providerType, fieldKey))
        {
            var envValue = Environment(envKey);
            if (envValue is not null)
                return envValue;
        }

        return Clean(defaultValue);
    }

    /// <summary>
    /// The first non-empty environment value mapped to a provider field, or null.
    /// Used by the settings dialog to render the effective value as placeholder text.
    /// </summary>
    public static string? EnvironmentValueFor(string providerType, string fieldKey)
    {
        foreach (var envKey in EnvironmentKeysFor(providerType, fieldKey))
        {
            var envValue = Environment(envKey);
            if (envValue is not null)
                return envValue;
        }

        return null;
    }

    public static string? Environment(string key) =>
        Clean(System.Environment.GetEnvironmentVariable(key))
        ?? Clean(System.Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User))
        ?? Clean(System.Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine));

    /// <summary>
    /// Resolves a CLI binary the same way every CLI-backed provider does: per-instance
    /// config value, then environment variable(s) mapped to that field, then a bare
    /// command name left for <see cref="HiddenCliProcess"/> to resolve on PATH.
    /// Configured values are environment-expanded (so %VAR% in a user-set path works).
    /// </summary>
    public static string ResolveCliPath(
        string instanceId,
        IConfig config,
        string providerType,
        string configKey,
        string fallbackCommand)
    {
        var resolved = Resolve(instanceId, config, providerType, configKey, fallbackCommand);
        return string.IsNullOrWhiteSpace(resolved)
            ? fallbackCommand
            : System.Environment.ExpandEnvironmentVariables(resolved);
    }

    public static string? Clean(string? value) => TextUtil.Clean(value);

    public static string ResponseSummary(string? body, int maxLength = 240)
    {
        var collapsed = WhitespacePattern().Replace(body ?? string.Empty, " ").Trim();
        if (string.IsNullOrEmpty(collapsed))
            return "empty body";

        var redacted = SecretPattern().Replace(collapsed, "$1[REDACTED]");
        if (redacted.Length <= maxLength)
            return redacted;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{redacted[..maxLength]}... [truncated]");
    }

    /// <summary>
    /// The env var(s) that import into a provider's config field. Backed by
    /// <see cref="Catalog.FieldEnvironment"/> plus <see cref="SimpleApiProvider"/>'s
    /// own per-type definitions.
    /// </summary>
    public static IReadOnlyList<string> EnvironmentKeysFor(string providerType, string fieldKey)
    {
        if (Catalog.FieldEnvironment.TryGetValue(providerType, out var fields)
            && fields.TryGetValue(fieldKey, out var keys))
        {
            return keys;
        }

        return SimpleApiProvider.TryGetEnvironmentKeys(providerType, fieldKey, out var simpleKeys)
            ? simpleKeys
            : Array.Empty<string>();
    }

    public static string AppendPath(string baseUrl, string path)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        var normalizedPath = path.TrimStart('/');
        if (trimmed.EndsWith(normalizedPath, StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return $"{trimmed}/{normalizedPath}";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"(?i)(token\s+|bearer\s+|api[_-]?key["":\s]+)[A-Za-z0-9._\-]+")]
    private static partial Regex SecretPattern();
}
