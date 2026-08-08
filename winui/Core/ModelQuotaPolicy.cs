namespace QuotaLens.Core;

/// <summary>Shared policy for model-family quota participation in availability and reset ranking.</summary>
public static class ModelQuotaPolicy
{
    public static ModelQuotaFamilyKind ResolveFamily(ModelQuota quota)
    {
        ArgumentNullException.ThrowIfNull(quota);

        if (quota.FamilyKind != ModelQuotaFamilyKind.Unknown)
            return quota.FamilyKind;

        var family = quota.Family.Trim();
        if (family.Contains("gemini", StringComparison.OrdinalIgnoreCase))
            return ModelQuotaFamilyKind.Gemini;
        if (family.Contains("claude", StringComparison.OrdinalIgnoreCase)
            || family.Contains("gpt", StringComparison.OrdinalIgnoreCase)
            || family.Contains("openai", StringComparison.OrdinalIgnoreCase))
        {
            return ModelQuotaFamilyKind.ClaudeGpt;
        }

        return string.IsNullOrWhiteSpace(family)
            ? ModelQuotaFamilyKind.Unknown
            : ModelQuotaFamilyKind.Other;
    }

    public static bool CountsForProviderAvailability(ModelQuota quota) =>
        ResolveFamily(quota) is ModelQuotaFamilyKind.Gemini or ModelQuotaFamilyKind.ClaudeGpt;
}
