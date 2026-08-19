namespace QuotaLens.Core;

[Flags]
public enum ProviderAuthKind
{
    None = 0,
    ApiKey = 1 << 0,
    OAuth = 1 << 1,
    BrowserSession = 1 << 2,
    LocalCli = 1 << 3,
    LocalService = 1 << 4,
    CloudCredentials = 1 << 5,
}

[Flags]
public enum ProviderCapability
{
    None = 0,
    QuotaWindows = 1 << 0,
    DynamicWindows = 1 << 1,
    ModelQuotas = 1 << 2,
    Balance = 1 << 3,
    CostActivity = 1 << 4,
    Accounts = 1 << 5,
    CapacityAllocation = 1 << 6,
}

public sealed record ProviderSourceChannel(
    string LabelMarker,
    ProviderSourceKind SourceKind,
    ProviderContractStability Stability,
    string? EvidenceUrl = null,
    string? LastVerifiedAt = null);

/// <summary>
/// Security and provenance contract for a provider integration. Documentation and
/// verification fields are intentionally nullable: absence means QuotaLens must not
/// present a private or upstream-derived contract as officially verified.
/// </summary>
public sealed record ProviderContract(
    string ProviderType,
    ProviderAuthKind Auth,
    ProviderSourceKind SourceKind,
    ProviderContractStability Stability,
    ProviderCapability Capabilities,
    IReadOnlyList<string> ApprovedCredentialHosts,
    bool AllowsCustomCredentialHost = false,
    bool AllowsLoopbackHttp = false,
    string? OfficialDocumentation = null,
    string? LastVerifiedAt = null,
    string? UpstreamRevision = null,
    IReadOnlyList<ProviderSourceChannel>? SourceChannels = null)
{
    public ProviderSourceChannel SourceFor(string sourceLabel) =>
        SourceChannels?.FirstOrDefault(channel =>
            sourceLabel.Contains(channel.LabelMarker, StringComparison.OrdinalIgnoreCase))
        ?? new ProviderSourceChannel(
            sourceLabel,
            SourceKind,
            Stability,
            OfficialDocumentation,
            LastVerifiedAt);
}

public static class ProviderContracts
{
    public const string AuditedUpstreamRevision = "8ef86077e70ac27d45ddddaf49e409824ccdf668";

    private static readonly IReadOnlyDictionary<string, ProviderContract> Contracts = Build();

    public static IReadOnlyCollection<ProviderContract> All => Contracts.Values.ToArray();

    public static ProviderContract For(string providerType) =>
        Contracts.TryGetValue(providerType, out var contract)
            ? contract
            : throw new ArgumentException($"Unknown provider contract: {providerType}", nameof(providerType));

    public static bool TryGet(string providerType, out ProviderContract? contract) =>
        Contracts.TryGetValue(providerType, out contract);

    private static IReadOnlyDictionary<string, ProviderContract> Build()
    {
        var contracts = new Dictionary<string, ProviderContract>(StringComparer.OrdinalIgnoreCase);

        Add(contracts, "codex-lb", ProviderAuthKind.LocalService, ProviderSourceKind.CliOrLocal,
            ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.ModelQuotas |
            ProviderCapability.Balance | ProviderCapability.Accounts,
            Array.Empty<string>(), allowsLoopbackHttp: true,
            officialDocumentation: "https://github.com/Soju06/codex-lb",
            lastVerifiedAt: "2026-08-02",
            upstreamRevision: "c539a200c301e5cdf2cf524dea336e1c40094bbd");
        Add(contracts, "codex", ProviderAuthKind.OAuth | ProviderAuthKind.LocalCli, ProviderSourceKind.CliOrLocal,
            ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance,
            new[] { "chatgpt.com", "chat.openai.com" });
        Add(contracts, "gemini", ProviderAuthKind.OAuth | ProviderAuthKind.LocalCli, ProviderSourceKind.UndocumentedApi,
            ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.ModelQuotas,
            new[] { "cloudcode-pa.googleapis.com", "oauth2.googleapis.com", "cloudresourcemanager.googleapis.com" },
            sourceChannels: new[]
            {
                new ProviderSourceChannel(
                    "Gemini OAuth",
                    ProviderSourceKind.UndocumentedApi,
                    ProviderContractStability.UpstreamCompatibility,
                    "https://github.com/steipete/CodexBar/blob/8ef86077e70ac27d45ddddaf49e409824ccdf668/Sources/CodexBarCore/Providers/Gemini/GeminiStatusProbe.swift",
                    "2026-08-02"),
            });
        Add(contracts, "bedrock", ProviderAuthKind.CloudCredentials, ProviderSourceKind.OfficialApi,
            ProviderContractStability.Official, ProviderCapability.CostActivity,
            new[] { "ce.us-east-1.amazonaws.com", "monitoring.*.amazonaws.com" },
            officialDocumentation: "https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostAndUsage.html",
            lastVerifiedAt: "2026-08-02");
        Add(contracts, "vertexai", ProviderAuthKind.OAuth | ProviderAuthKind.CloudCredentials | ProviderAuthKind.LocalCli,
            ProviderSourceKind.OfficialApi, ProviderContractStability.Official,
            ProviderCapability.QuotaWindows | ProviderCapability.CostActivity,
            new[] { "cloudbilling.googleapis.com", "monitoring.googleapis.com", "serviceusage.googleapis.com", "oauth2.googleapis.com" },
            officialDocumentation: "https://docs.cloud.google.com/monitoring/api/ref_v3/rest/v3/projects.timeSeries/list",
            lastVerifiedAt: "2026-08-02");
        Add(contracts, "claude", ProviderAuthKind.OAuth | ProviderAuthKind.LocalCli,
            ProviderSourceKind.UndocumentedApi, ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance,
            new[] { "api.anthropic.com" },
            sourceChannels: new[]
            {
                new ProviderSourceChannel(
                    "OAuth API",
                    ProviderSourceKind.UndocumentedApi,
                    ProviderContractStability.UpstreamCompatibility,
                    "https://github.com/steipete/CodexBar/blob/8ef86077e70ac27d45ddddaf49e409824ccdf668/Sources/CodexBarCore/Providers/Claude/ClaudeOAuth/ClaudeOAuthUsageFetcher.swift",
                    "2026-08-02"),
            });
        Add(contracts, "deepseek", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.Official, ProviderCapability.Balance | ProviderCapability.CostActivity,
            new[] { "api.deepseek.com", "platform.deepseek.com" },
            officialDocumentation: "https://api-docs.deepseek.com/api/get-user-balance",
            lastVerifiedAt: "2026-08-02",
            sourceChannels: new[]
            {
                new ProviderSourceChannel("private dashboard", ProviderSourceKind.PrivateDashboard, ProviderContractStability.PrivateContract),
                new ProviderSourceChannel(
                    "API",
                    ProviderSourceKind.OfficialApi,
                    ProviderContractStability.Official,
                    "https://api-docs.deepseek.com/api/get-user-balance",
                    "2026-08-02"),
            });
        Add(contracts, "kiro", ProviderAuthKind.LocalCli, ProviderSourceKind.CliOrLocal,
            ProviderContractStability.DocumentedCli,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance,
            Array.Empty<string>());
        Add(contracts, "alibabacloud", ProviderAuthKind.CloudCredentials, ProviderSourceKind.OfficialApi,
            ProviderContractStability.Official, ProviderCapability.Balance,
            new[] { "business.aliyuncs.com" },
            officialDocumentation: "https://www.alibabacloud.com/help/en/user-center/developer-reference/api-bssopenapi-2017-12-14-queryaccountbalance",
            lastVerifiedAt: "2026-08-02");
        Add(contracts, "antigravity", ProviderAuthKind.OAuth | ProviderAuthKind.LocalService | ProviderAuthKind.LocalCli,
            ProviderSourceKind.CliOrLocal, ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.ModelQuotas,
            new[] { "cloudcode-pa.googleapis.com", "daily-cloudcode-pa.googleapis.com" },
            officialDocumentation: "https://antigravity.google/docs/plans",
            lastVerifiedAt: "2026-08-02");
        Add(contracts, "qoder", ProviderAuthKind.LocalCli, ProviderSourceKind.CliOrLocal,
            ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance,
            Array.Empty<string>());
        Add(contracts, "azureopenai", ProviderAuthKind.OAuth | ProviderAuthKind.CloudCredentials,
            ProviderSourceKind.OfficialApi, ProviderContractStability.Official,
            ProviderCapability.DynamicWindows | ProviderCapability.CapacityAllocation,
            new[] { "management.azure.com" },
            officialDocumentation: "https://learn.microsoft.com/en-us/rest/api/cognitiveservices/accountmanagement/usages/list?view=rest-cognitiveservices-accountmanagement-2024-10-01",
            lastVerifiedAt: "2026-08-02");
        Add(contracts, "doubao", ProviderAuthKind.LocalCli, ProviderSourceKind.CliOrLocal,
            ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows,
            Array.Empty<string>());
        Add(contracts, "groq", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.Official, ProviderCapability.CostActivity,
            new[] { "api.groq.com" },
            officialDocumentation: "https://console.groq.com/docs/prometheus-metrics",
            lastVerifiedAt: "2026-08-02");
        Add(contracts, "deepgram", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.Official, ProviderCapability.CostActivity,
            new[] { "api.deepgram.com" },
            officialDocumentation: "https://developers.deepgram.com/reference/manage/usage/breakdown/get",
            lastVerifiedAt: "2026-08-02");
        Add(contracts, "grok", ProviderAuthKind.LocalCli | ProviderAuthKind.BrowserSession,
            ProviderSourceKind.PrivateDashboard, ProviderContractStability.PrivateContract,
            ProviderCapability.Balance | ProviderCapability.CostActivity,
            new[] { "cli-chat-proxy.grok.com" },
            // The CLI's own billing backend is overridable via GROK_CLI_CHAT_PROXY_BASE_URL
            // (enterprise proxies), so a user-chosen HTTPS host is permitted.
            allowsCustomCredentialHost: true);
        Add(contracts, "kilo", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance,
            new[] { "app.kilo.ai", "api.kilo.ai" });
        Add(contracts, "jetbrains", ProviderAuthKind.LocalService, ProviderSourceKind.CliOrLocal,
            ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.Balance, Array.Empty<string>());
        Add(contracts, "kimi", ProviderAuthKind.LocalCli | ProviderAuthKind.BrowserSession,
            ProviderSourceKind.CliOrLocal, ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows, Array.Empty<string>(),
            sourceChannels: new[]
            {
                new ProviderSourceChannel("WebView", ProviderSourceKind.PrivateDashboard, ProviderContractStability.PrivateContract),
                new ProviderSourceChannel("CLI", ProviderSourceKind.CliOrLocal, ProviderContractStability.UpstreamCompatibility),
            });

        AddOfficialApiContracts(contracts);
        AddPrivateDashboardContracts(contracts);

        Add(contracts, "opencodego", ProviderAuthKind.LocalCli | ProviderAuthKind.BrowserSession,
            ProviderSourceKind.CliOrLocal, ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance,
            Array.Empty<string>(),
            sourceChannels: new[]
            {
                new ProviderSourceChannel("Web quota", ProviderSourceKind.PrivateDashboard, ProviderContractStability.PrivateContract),
                new ProviderSourceChannel("Web balance", ProviderSourceKind.PrivateDashboard, ProviderContractStability.PrivateContract),
                new ProviderSourceChannel("WebView", ProviderSourceKind.PrivateDashboard, ProviderContractStability.PrivateContract),
                new ProviderSourceChannel("local history", ProviderSourceKind.CliOrLocal, ProviderContractStability.UpstreamCompatibility),
            });

        Add(contracts, "llmproxy", ProviderAuthKind.ApiKey, ProviderSourceKind.CustomOrSelfHosted,
            ProviderContractStability.Custom,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.CostActivity |
            ProviderCapability.Accounts,
            Array.Empty<string>(), allowsCustomCredentialHost: true, allowsLoopbackHttp: true);
        Add(contracts, "kimik2", ProviderAuthKind.ApiKey, ProviderSourceKind.UnverifiedRelay,
            ProviderContractStability.Retired, ProviderCapability.None, Array.Empty<string>(),
            upstreamRevision: "54cfd1a3f504b74d8c438ace0131d81a9482d18c");

        var missing = Catalog.Types.Where(type => !contracts.ContainsKey(type.Id)).Select(type => type.Id).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Provider contracts missing: {string.Join(", ", missing)}");

        return contracts;
    }

    private static void AddOfficialApiContracts(IDictionary<string, ProviderContract> contracts)
    {
        Add(contracts, "copilot", ProviderAuthKind.ApiKey, ProviderSourceKind.UndocumentedApi,
            ProviderContractStability.PrivateContract, ProviderCapability.QuotaWindows,
            new[] { "api.github.com", "*.githubcopilot.com" }, allowsCustomCredentialHost: true);
        Add(contracts, "openrouter", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.Official, ProviderCapability.QuotaWindows | ProviderCapability.Balance,
            new[] { "openrouter.ai" },
            officialDocumentation: "https://openrouter.ai/docs/api-reference/limits",
            lastVerifiedAt: "2026-08-02");
        Add(contracts, "moonshot", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.Official, ProviderCapability.Balance,
            new[] { "api.moonshot.ai", "api.moonshot.cn" },
            officialDocumentation: "https://platform.kimi.ai/docs/api/balance",
            lastVerifiedAt: "2026-08-02");
        Add(contracts, "venice", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.Official, ProviderCapability.Balance,
            new[] { "api.venice.ai" },
            officialDocumentation: "https://docs.venice.ai/api-reference/endpoint/billing/balance",
            lastVerifiedAt: "2026-08-02");
        Add(contracts, "crof", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.UpstreamCompatibility, ProviderCapability.QuotaWindows | ProviderCapability.Balance,
            new[] { "crof.ai" });
        Add(contracts, "openai", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.Official, ProviderCapability.CostActivity,
            new[] { "api.openai.com" },
            officialDocumentation: "https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/completions",
            lastVerifiedAt: "2026-08-02");
        Add(contracts, "elevenlabs", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.Official, ProviderCapability.QuotaWindows,
            new[] { "api.elevenlabs.io" },
            officialDocumentation: "https://elevenlabs.io/docs/api-reference/user/subscription",
            lastVerifiedAt: "2026-08-02");
        Add(contracts, "warp", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows,
            new[] { "app.warp.dev" });
        Add(contracts, "codebuff", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance,
            new[] { "www.codebuff.com", "codebuff.com" });
        Add(contracts, "synthetic", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows,
            new[] { "api.synthetic.new" });
        Add(contracts, "zai", ProviderAuthKind.ApiKey, ProviderSourceKind.OfficialApi,
            ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows,
            new[] { "api.z.ai", "open.bigmodel.cn" });
        Add(contracts, "zcode", ProviderAuthKind.LocalCli, ProviderSourceKind.OfficialApi,
            ProviderContractStability.UpstreamCompatibility,
            ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows,
            new[] { "zcode.z.ai" });
    }

    private static void AddPrivateDashboardContracts(IDictionary<string, ProviderContract> contracts)
    {
        foreach (var (providerType, capabilities) in new (string ProviderType, ProviderCapability Capabilities)[]
        {
            ("alibaba", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("alibabatokenplan", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("bayesdl", ProviderCapability.QuotaWindows | ProviderCapability.Balance),
            ("mimo", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("amp", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("cursor", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("augment", ProviderCapability.QuotaWindows | ProviderCapability.Balance),
            ("factory", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("minimax", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("windsurf", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows),
            ("manus", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("perplexity", ProviderCapability.QuotaWindows | ProviderCapability.Balance),
            ("t3chat", ProviderCapability.QuotaWindows),
            ("commandcode", ProviderCapability.QuotaWindows | ProviderCapability.Balance),
            ("ollama", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows),
            ("abacus", ProviderCapability.QuotaWindows | ProviderCapability.Balance),
            ("stepfun", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows),
            ("opencode", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows),
            ("mistral", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows |
                ProviderCapability.Balance | ProviderCapability.CostActivity),
        })
        {
            Add(contracts, providerType, ProviderAuthKind.BrowserSession, ProviderSourceKind.PrivateDashboard,
                ProviderContractStability.PrivateContract, capabilities,
                Array.Empty<string>());
        }
    }

    private static void Add(
        IDictionary<string, ProviderContract> contracts,
        string providerType,
        ProviderAuthKind auth,
        ProviderSourceKind sourceKind,
        ProviderContractStability stability,
        ProviderCapability capabilities,
        IReadOnlyList<string> approvedCredentialHosts,
        bool allowsCustomCredentialHost = false,
        bool allowsLoopbackHttp = false,
        string? officialDocumentation = null,
        string? lastVerifiedAt = null,
        string? upstreamRevision = AuditedUpstreamRevision,
        IReadOnlyList<ProviderSourceChannel>? sourceChannels = null)
    {
        contracts.Add(providerType, new ProviderContract(
            providerType,
            auth,
            sourceKind,
            stability,
            capabilities,
            approvedCredentialHosts,
            allowsCustomCredentialHost,
            allowsLoopbackHttp,
            officialDocumentation,
            lastVerifiedAt,
            upstreamRevision,
            sourceChannels));
    }
}
