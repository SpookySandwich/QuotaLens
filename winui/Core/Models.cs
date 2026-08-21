using QuotaLens.Helpers;

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
    /// <summary>
    /// Provider-reported detail that is not reset presentation (usage counts, balance,
    /// status, or cadence when no reset instant exists). A valid <see cref="ResetsAt"/>
    /// is always rendered by the shared reset formatter instead of this text.
    /// </summary>
    public string? DetailText { get; set; }
    public long? WindowMinutes { get; set; }
    /// Optional windows are informational unless a provider marks them as gating overall use.
    public bool CountsForAvailability { get; set; }
    /// Alternative-capacity group. Windows in a group are jointly gating; separate groups are alternatives.
    public string? AvailabilityGroup { get; set; }
    /// <summary>
    /// Presentation-only grouping key. Keeps a provider's family or product rows
    /// together on the card without making them jointly gating for availability,
    /// which <see cref="AvailabilityGroup"/> would.
    /// </summary>
    public string? DisplayGroup { get; set; }
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

/// <summary>How a failed data source can be recovered without provider checks in the UI.</summary>
public enum ProviderRecoveryKind
{
    LaunchApp,
}

/// <summary>Declarative recovery action carried from a source failure to the card.</summary>
public sealed record ProviderRecoveryAction(
    ProviderRecoveryKind Kind,
    string DescriptionKey,
    int RetryDelaySeconds = 8);

/// <summary>Which App/CLI/Web source was requested and which one produced the snapshot.</summary>
public sealed record ProviderSourceState(
    string? RequestedSourceId,
    string EffectiveSourceId,
    bool UsedFallback);

/// <summary>
/// One provider's snapshot for the dashboard. Mirrors the Rust ProviderSnapshot
/// so behavior/parsing stays faithful across the port.
/// </summary>
public sealed class ProviderSnapshot
{
    public string ProviderId { get; set; } = "";
    /// <summary>
    /// Normalized presentation title. Provider parsers set only their stable identity;
    /// ProviderSnapshotIdentity is the sole owner of appending an active plan.
    /// </summary>
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
    /// <summary>Resolution metadata supplied by the shared multi-source runner.</summary>
    public ProviderSourceState? SourceState { get; set; }
    /// Non-null when the fetch failed; the UI renders this as the error/needs-attention state.
    public string? Error { get; set; }

    /// <summary>
    /// Structural classification of <see cref="Error"/>, carried from
    /// <see cref="ProviderException.Kind"/>. The card decides its action from THIS,
    /// never from the wording of the message — matching prose is how a reworded
    /// message silently removed a button.
    /// </summary>
    public ProviderErrorKind ErrorKind { get; set; } = ProviderErrorKind.Unknown;

    /// <summary>
    /// Action that can recover an invalid/no-data snapshot. Healthy snapshots never
    /// carry this action, even when the requested source fell back to another source.
    /// </summary>
    public ProviderRecoveryAction? RecoveryAction { get; set; }

    /// <summary>Build an error snapshot for a provider (mirrors fetch_all error mapping).</summary>
    public static ProviderSnapshot ForError(string providerId, string name, string sourceLabel, string error) => new()
    {
        ProviderId = providerId,
        Name = name,
        Primary = new RateWindow { Label = I18n.T("quota.errorLabel"), UsedPercent = 0, DetailText = error },
        SourceLabel = sourceLabel,
        Error = error,
    };
}
