using System.Globalization;
using System.Text.RegularExpressions;
using QuotaLens.Core;

namespace QuotaLens.Providers;

internal static partial class ProviderConfig
{
    public static string? ScopedOrEnvironment(
        string instanceId,
        IConfig config,
        string configKey,
        params string[] environmentKeys)
    {
        var configured = Clean(config.GetScoped(instanceId, configKey));
        if (configured is not null)
            return configured;
        if (config.HasScoped(instanceId, configKey))
            return null;

        foreach (var key in environmentKeys)
        {
            var value = Environment(key);
            if (value is not null)
                return value;
        }

        return null;
    }

    public static string? Environment(string key) =>
        Clean(System.Environment.GetEnvironmentVariable(key))
        ?? Clean(System.Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User))
        ?? Clean(System.Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine));

    /// <summary>
    /// Resolves a CLI binary the same way every CLI-backed provider does: per-instance
    /// config value, then environment variable(s), then a bare command name left for
    /// <see cref="HiddenCliProcess"/> to resolve on PATH. Configured values are
    /// environment-expanded (so %VAR% in a user-set path works).
    /// </summary>
    public static string ResolveCliPath(
        string instanceId,
        IConfig config,
        string configKey,
        string fallbackCommand,
        params string[] environmentKeys)
    {
        var configured = ScopedOrEnvironment(instanceId, config, configKey, environmentKeys);
        return string.IsNullOrWhiteSpace(configured)
            ? fallbackCommand
            : System.Environment.ExpandEnvironmentVariables(configured);
    }

    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) || (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
            trimmed = trimmed[1..^1].Trim();

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

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
