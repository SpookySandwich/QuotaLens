namespace QuotaLens.Core;

/// <summary>Shared tiny string helpers used across providers and core services.</summary>
internal static class StringValues
{
    /// <summary>The first non-blank value, trimmed, or null when all are blank.</summary>
    public static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
