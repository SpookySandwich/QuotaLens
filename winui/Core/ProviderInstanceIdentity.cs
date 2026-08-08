namespace QuotaLens.Core;

/// <summary>Canonical validation and filesystem keying for persisted provider instance IDs.</summary>
public static class ProviderInstanceIdentity
{
    private const int MaxLength = 80;

    public static bool IsValid(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)
            || instanceId.Length > MaxLength
            || !char.IsAsciiLetterOrDigit(instanceId[0]))
        {
            return false;
        }

        return instanceId.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    public static void RequireValid(string? instanceId, string? parameterName = null)
    {
        if (!IsValid(instanceId))
        {
            throw new ArgumentException(
                "Provider instance IDs may contain only ASCII letters, digits, '-' and '_', must start with a letter or digit, and may be at most 80 characters.",
                parameterName ?? nameof(instanceId));
        }
    }
}
