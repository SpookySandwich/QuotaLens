namespace QuotaLens.Core;

/// <summary>How a provider window should be interpreted and rendered.</summary>
public enum RateWindowKind
{
    Quota,
    Informational,
}

/// <summary>Identifies informational values that privacy mode must conceal.</summary>
public enum RateWindowSensitivity
{
    None,
    Usage,
    Financial,
}

/// <summary>A quota window or an informational usage metric.</summary>
public sealed class RateWindow
{
    public string Label { get; set; } = "";
    public RateWindowKind Kind { get; set; } = RateWindowKind.Quota;
    public RateWindowSensitivity Sensitivity { get; set; } = RateWindowSensitivity.None;
    public double UsedPercent { get; set; }
    /// Human-readable value for informational metrics that do not have a finite allowance.
    public string? ValueText { get; set; }
    /// ISO-8601 reset time, or null. Kept as string to mirror the original; UI parses.
    public string? ResetsAt { get; set; }
    public string? ResetDescription { get; set; }
    public long? WindowMinutes { get; set; }
    /// Optional windows are informational unless a provider marks them as gating overall use.
    public bool CountsForAvailability { get; set; }
    /// Alternative-capacity group. Windows in a group are jointly gating; separate groups are alternatives.
    public string? AvailabilityGroup { get; set; }
}

/// <summary>Account balance (DeepSeek, Alibaba CNY, credits, etc.).</summary>
public sealed class BalanceInfo
{
    public string Currency { get; set; } = "USD";
    public double Total { get; set; }
    public double Paid { get; set; }
    public double Granted { get; set; }
    public string? PaidLabelKey { get; set; }
    public string? GrantedLabelKey { get; set; }
}

/// <summary>A single model's quota (Antigravity per-model breakdown).</summary>
public sealed class ModelQuota
{
    public string Model { get; set; } = "";
    public string Family { get; set; } = "";
    public ModelQuotaFamilyKind FamilyKind { get; set; } = ModelQuotaFamilyKind.Unknown;
    public string WindowType { get; set; } = "";
    public double RemainingPercent { get; set; }
    public double UsedPercent { get; set; }
    public string? ResetsAt { get; set; }
}

/// <summary>Stable family identity for model-scoped provider quotas.</summary>
public enum ModelQuotaFamilyKind
{
    Unknown,
    Gemini,
    ClaudeGpt,
    Other,
}

/// <summary>Per-account usage (e.g. codex-lb pooled accounts).</summary>
public sealed class AccountInfo
{
    public string? Email { get; set; }
    public string? Plan { get; set; }
    public double? UsedPercent { get; set; }
    public string? PrimaryLabel { get; set; }
    public double? PrimaryUsedPercent { get; set; }
    public string? PrimaryResetsAt { get; set; }
    public string? SecondaryLabel { get; set; }
    public double? SecondaryUsedPercent { get; set; }
    public string? SecondaryResetsAt { get; set; }
    public double? CreditsUsed { get; set; }
    public double? CreditsTotal { get; set; }
}

/// <summary>Confidence/trust level for a provider's data source.</summary>
public enum Confidence
{
    Official,
    SemiOfficial,
    Unofficial
}

/// <summary>Where a provider snapshot was obtained.</summary>
public enum ProviderSourceKind
{
    Unknown,
    OfficialApi,
    UndocumentedApi,
    PrivateDashboard,
    CliOrLocal,
    CustomOrSelfHosted,
    UnverifiedRelay
}

/// <summary>How stable and supportable the provider contract is expected to be.</summary>
public enum ProviderContractStability
{
    Unknown,
    Official,
    DocumentedCli,
    UpstreamCompatibility,
    PrivateContract,
    Custom,
    Retired
}

/// <summary>Whether a subscription-backed provider currently grants access.</summary>
public enum EntitlementStatus
{
    Unknown,
    Active,
    Expired
}

/// <summary>Whether overall availability is unknown, finite, or explicitly unlimited.</summary>
public enum ProviderAvailabilityKind
{
    Unknown,
    Finite,
    Unlimited,
}

/// <summary>
/// One provider's snapshot for the dashboard. Mirrors the Rust ProviderSnapshot
/// so behavior/parsing stays faithful across the port.
/// </summary>
public sealed class ProviderSnapshot
{
    public string ProviderId { get; set; } = "";
    /// <summary>Presentation title composed from instance identity and active plan.</summary>
    public string Name { get; set; } = "";
    /// <summary>Provider-owned canonical plan identifier, when the response exposes one.</summary>
    public string? PlanId { get; set; }
    /// <summary>Provider-owned active plan label. Provider/instance names do not belong here.</summary>
    public string? PlanName { get; set; }
    public RateWindow Primary { get; set; } = new();
    public RateWindow? Secondary { get; set; }
    public RateWindow? Tertiary { get; set; }
    public List<RateWindow> AdditionalWindows { get; set; } = new();
    public BalanceInfo? Balance { get; set; }
    public List<AccountInfo> Accounts { get; set; } = new();
    public List<ModelQuota> ModelQuotas { get; set; } = new();
    /// <summary>
    /// Weekly token capacity (millions, cache-inclusive) measured from the
    /// provider's own reported consumption, when it exposes real token counts
    /// (e.g. codex-lb metrics). Preferred over PlanTokenRules estimates when set.
    /// </summary>
    public double? MeasuredWeeklyTokensMillions { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string SourceLabel { get; set; } = "";
    public Confidence Confidence { get; set; } = Confidence.Unofficial;
    public ProviderSourceKind SourceKind { get; set; } = ProviderSourceKind.Unknown;
    public ProviderContractStability ContractStability { get; set; } = ProviderContractStability.Unknown;
    public EntitlementStatus EntitlementStatus { get; set; } = EntitlementStatus.Unknown;
    public ProviderAvailabilityKind AvailabilityKind { get; set; } = ProviderAvailabilityKind.Unknown;
    /// Non-null when the fetch failed; the UI renders this as the error/needs-attention state.
    public string? Error { get; set; }

    /// <summary>Build an error snapshot for a provider (mirrors fetch_all error mapping).</summary>
    public static ProviderSnapshot ForError(string providerId, string name, string sourceLabel, string error) => new()
    {
        ProviderId = providerId,
        Name = name,
        Primary = new RateWindow { Label = "Error", UsedPercent = 0, ResetDescription = error },
        SourceLabel = sourceLabel,
        Error = error,
    };
}
