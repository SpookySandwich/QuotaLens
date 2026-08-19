namespace QuotaLens.Core;

public sealed record ProviderType(string Id, string Name);

public sealed record ProviderInstance(string Id, string Type, string Name);

public enum ProviderSetupKind
{
    BrowserLogin,
    ApiKey,
    LocalAppOrCli,
    Ready,
}

/// <summary>A configurable field shown in a provider's edit dialog.</summary>
public sealed record ProviderField(
    string Key,
    string Label,
    string Placeholder = "",
    bool IsPassword = false,
    bool IsFilePath = false,
    bool IsToggle = false,
    bool IsRequired = false,
    string? Description = null,
    bool IsGlobal = false);

public enum ProviderPlanEvidence
{
    LegacyUnverified,
    UpstreamCompatibility,
    Official,
    UserConfigured
}

/// <summary>
/// Plan value used for provider ordering plus optional pricing provenance. Legacy
/// rules deliberately default to unverified so a numeric sort hint is never
/// mistaken for an officially confirmed price.
/// </summary>
public sealed record ProviderPlanValueRule(
    string Keyword,
    double Value,
    string? PlanId = null,
    double? PriceAmount = null,
    string? Currency = null,
    string? Region = null,
    string? Cadence = null,
    string? SeatBasis = null,
    string? OfficialSource = null,
    string? LastVerifiedAt = null,
    string? PriceQualifier = null,
    string? AvailabilityNote = null,
    ProviderPlanEvidence Evidence = ProviderPlanEvidence.LegacyUnverified);

/// <summary>
/// Estimated weekly token allowance for a plan, used to size usage-timeline
/// segments proportionally to what the plan actually buys. Values are MILLIONS
/// of tokens per week, cache-inclusive (input + output + cache create + cache
/// read — the ccusage convention), normalized from each platform's own metering
/// (requests, credits, dollar pools) with stated assumptions. These are
/// estimates for relative bar sizing, not billing data.
/// </summary>
public sealed record ProviderPlanTokenRule(string Keyword, double WeeklyTokensMillions);

public sealed record ProviderRequiredFieldSet
{
    public ProviderRequiredFieldSet(params string[] anyOf) => AnyOf = anyOf.ToArray();

    public string[] AnyOf { get; }
}

public sealed record ProviderLaunchTarget(
    string DisplayName,
    string? ConfigKey,
    string[] DefaultPaths,
    string[] DirectoryExecutableNames,
    string? PackageFamilyName,
    string? PackageExecutableRelativePath,
    IReadOnlyDictionary<string, string>? ExecutableDisplayNames = null)
{
    public ProviderLaunchTarget(string displayName, string? configKey, string[] defaultPaths)
        : this(displayName, configKey, defaultPaths, Array.Empty<string>(), null, null, null)
    {
    }

    public ProviderLaunchTarget(string displayName, string? configKey, string[] defaultPaths, string[] directoryExecutableNames)
        : this(displayName, configKey, defaultPaths, directoryExecutableNames, null, null, null)
    {
    }

    public ProviderLaunchTarget(
        string displayName,
        string? configKey,
        string[] defaultPaths,
        string[] directoryExecutableNames,
        IReadOnlyDictionary<string, string> executableDisplayNames)
        : this(displayName, configKey, defaultPaths, directoryExecutableNames, null, null, executableDisplayNames)
    {
    }

    public string DisplayNameFor(string executablePath)
    {
        var executableName = Path.GetFileName(executablePath);
        if (ExecutableDisplayNames is not null)
        {
            foreach (var (candidate, displayName) in ExecutableDisplayNames)
            {
                if (string.Equals(candidate, executableName, StringComparison.OrdinalIgnoreCase))
                    return displayName;
            }
        }

        return DisplayName;
    }
}

public enum ProviderLocalSetupRequirementKind
{
    ScopedConfig,
    Environment,
    FilePath,
    DirectoryPath,
    PathExecutable,
    ScopedConfigFilePath,
    EnvironmentFilePath,
}

public sealed record ProviderLocalSetupRequirement
{
    private const string ValueToken = "{value}";

    private ProviderLocalSetupRequirement(
        ProviderLocalSetupRequirementKind kind,
        string[] values,
        bool requireAll = false,
        string[]? pathTemplates = null)
    {
        Kind = kind;
        Values = values;
        RequireAll = requireAll;
        PathTemplates = pathTemplates is { Length: > 0 } ? pathTemplates : new[] { ValueToken };
    }

    public ProviderLocalSetupRequirementKind Kind { get; }
    public string[] Values { get; }
    public bool RequireAll { get; }
    public string[] PathTemplates { get; }

    public static ProviderLocalSetupRequirement AnyScopedConfig(params string[] keys) =>
        new(ProviderLocalSetupRequirementKind.ScopedConfig, keys);

    public static ProviderLocalSetupRequirement AllScopedConfig(params string[] keys) =>
        new(ProviderLocalSetupRequirementKind.ScopedConfig, keys, requireAll: true);

    public static ProviderLocalSetupRequirement AnyEnvironment(params string[] keys) =>
        new(ProviderLocalSetupRequirementKind.Environment, keys);

    public static ProviderLocalSetupRequirement AllEnvironment(params string[] keys) =>
        new(ProviderLocalSetupRequirementKind.Environment, keys, requireAll: true);

    public static ProviderLocalSetupRequirement AnyFilePath(params string[] paths) =>
        new(ProviderLocalSetupRequirementKind.FilePath, paths);

    public static ProviderLocalSetupRequirement AnyDirectoryPath(params string[] paths) =>
        new(ProviderLocalSetupRequirementKind.DirectoryPath, paths);

    public static ProviderLocalSetupRequirement AnyPathExecutable(params string[] commands) =>
        new(ProviderLocalSetupRequirementKind.PathExecutable, commands);

    public static ProviderLocalSetupRequirement AnyScopedConfigFilePath(string[] keys, params string[] pathTemplates) =>
        new(ProviderLocalSetupRequirementKind.ScopedConfigFilePath, keys, pathTemplates: pathTemplates);

    public static ProviderLocalSetupRequirement AnyEnvironmentFilePath(string[] keys, params string[] pathTemplates) =>
        new(ProviderLocalSetupRequirementKind.EnvironmentFilePath, keys, pathTemplates: pathTemplates);
}

public sealed record ProviderLocalSetupSource
{
    public ProviderLocalSetupSource(params ProviderLocalSetupRequirement[] requirements)
    {
        Requirements = requirements;
    }

    public ProviderLocalSetupRequirement[] Requirements { get; }

    public static ProviderLocalSetupSource[] AnyOf(params ProviderLocalSetupRequirement[] requirements) =>
        requirements.Select(requirement => new ProviderLocalSetupSource(requirement)).ToArray();
}

public sealed record ProviderLocalSetupProbe
{
    public ProviderLocalSetupProbe(
        string configKey,
        string displayName,
        string[] defaultPaths,
        string[] environmentKeys,
        string[] pathExecutableNames)
        : this(new[] { configKey }, displayName, defaultPaths, Array.Empty<string>(), environmentKeys, pathExecutableNames)
    {
    }

    public ProviderLocalSetupProbe(
        string[] configKeys,
        string displayName,
        string[] defaultFilePaths,
        string[] defaultDirectoryPaths,
        string[] environmentKeys,
        string[] pathExecutableNames)
        : this(
            displayName,
            LegacySources(configKeys, defaultFilePaths, defaultDirectoryPaths, environmentKeys, pathExecutableNames))
    {
    }

    public ProviderLocalSetupProbe(string displayName, params ProviderLocalSetupSource[] sources)
    {
        DisplayName = displayName;
        Sources = sources;
    }

    public string DisplayName { get; }
    public ProviderLocalSetupSource[] Sources { get; }
    public string[] ConfigKeys => RequirementValues(
        ProviderLocalSetupRequirementKind.ScopedConfig,
        ProviderLocalSetupRequirementKind.ScopedConfigFilePath);
    public string[] DefaultFilePaths => RequirementValues(ProviderLocalSetupRequirementKind.FilePath);
    public string[] DefaultDirectoryPaths => RequirementValues(ProviderLocalSetupRequirementKind.DirectoryPath);
    public string[] EnvironmentKeys => RequirementValues(
        ProviderLocalSetupRequirementKind.Environment,
        ProviderLocalSetupRequirementKind.EnvironmentFilePath);
    public string[] PathExecutableNames => RequirementValues(ProviderLocalSetupRequirementKind.PathExecutable);
    public string ConfigKey => ConfigKeys.FirstOrDefault() ?? "";

    private string[] RequirementValues(params ProviderLocalSetupRequirementKind[] kinds) =>
        Sources
            .SelectMany(source => source.Requirements)
            .Where(requirement => kinds.Contains(requirement.Kind))
            .SelectMany(requirement => requirement.Values)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static ProviderLocalSetupSource[] LegacySources(
        string[] configKeys,
        string[] defaultFilePaths,
        string[] defaultDirectoryPaths,
        string[] environmentKeys,
        string[] pathExecutableNames)
    {
        var requirements = new List<ProviderLocalSetupRequirement>();
        if (configKeys.Length > 0)
            requirements.Add(ProviderLocalSetupRequirement.AnyScopedConfig(configKeys));
        if (defaultFilePaths.Length > 0)
            requirements.Add(ProviderLocalSetupRequirement.AnyFilePath(defaultFilePaths));
        if (defaultDirectoryPaths.Length > 0)
            requirements.Add(ProviderLocalSetupRequirement.AnyDirectoryPath(defaultDirectoryPaths));
        if (environmentKeys.Length > 0)
            requirements.Add(ProviderLocalSetupRequirement.AnyEnvironment(environmentKeys));
        if (pathExecutableNames.Length > 0)
            requirements.Add(ProviderLocalSetupRequirement.AnyPathExecutable(pathExecutableNames));
        return ProviderLocalSetupSource.AnyOf(requirements.ToArray());
    }
}

/// <summary>Static catalog: provider types, defaults, per-provider fields, plan pricing.</summary>
public static class Catalog
{
    public const string DefaultLaunchEditorPathKey = "default_launch_editor_path";

    public static readonly IReadOnlyList<ProviderType> Types = new[]
    {
        new ProviderType("codex-lb", "codex-lb"),
        new ProviderType("codex", "Codex"),
        new ProviderType("copilot", "Copilot"),
        new ProviderType("gemini", "Gemini"),
        new ProviderType("bedrock", "AWS Bedrock"),
        new ProviderType("vertexai", "Vertex AI"),
        new ProviderType("claude", "Claude Code"),
        new ProviderType("deepseek", "DeepSeek"),
        new ProviderType("kiro", "Kiro"),
        new ProviderType("alibaba", "Alibaba"),
        new ProviderType("alibabacloud", "Alibaba Cloud"),
        new ProviderType("alibabatokenplan", "Alibaba Token Plan"),
        new ProviderType("antigravity", "Antigravity"),
        new ProviderType("bayesdl", "BayesDL"),
        new ProviderType("mimo", "MiMo"),
        new ProviderType("qoder", "Qoder"),
        new ProviderType("kimi", "Kimi"),
        new ProviderType("amp", "Amp"),
        new ProviderType("cursor", "Cursor"),
        new ProviderType("augment", "Augment"),
        new ProviderType("factory", "Factory"),
        new ProviderType("minimax", "MiniMax"),
        new ProviderType("windsurf", "Windsurf"),
        new ProviderType("openrouter", "OpenRouter"),
        new ProviderType("moonshot", "Moonshot"),
        new ProviderType("venice", "Venice"),
        new ProviderType("crof", "Crof"),
        new ProviderType("openai", "OpenAI API"),
        new ProviderType("azureopenai", "Azure OpenAI"),
        new ProviderType("elevenlabs", "ElevenLabs"),
        new ProviderType("warp", "Warp"),
        new ProviderType("codebuff", "Codebuff"),
        new ProviderType("synthetic", "Synthetic"),
        new ProviderType("zai", "z.ai (API)"),
        new ProviderType("zcode", "ZCode"),
        new ProviderType("llmproxy", "LLM Proxy"),
        new ProviderType("doubao", "Doubao"),
        new ProviderType("groq", "Groq"),
        new ProviderType("deepgram", "Deepgram"),
        new ProviderType("grok", "Grok"),
        new ProviderType("kilo", "Kilo"),
        new ProviderType("jetbrains", "JetBrains AI"),
        new ProviderType("kimik2", "Kimi K2"),
        new ProviderType("manus", "Manus"),
        new ProviderType("perplexity", "Perplexity"),
        new ProviderType("t3chat", "T3 Chat"),
        new ProviderType("commandcode", "Command Code"),
        new ProviderType("ollama", "Ollama"),
        new ProviderType("abacus", "Abacus AI"),
        new ProviderType("stepfun", "StepFun"),
        new ProviderType("opencode", "OpenCode"),
        new ProviderType("opencodego", "OpenCode Go"),
        new ProviderType("mistral", "Mistral"),
    };

    private static readonly IReadOnlySet<string> InternalProviderTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Cloud account balance is the same Alibaba card's pay-as-you-go overflow
        // once the coding-plan tokens run out — not a separate product.
        "alibabacloud",
    };

    public static readonly IReadOnlySet<string> RetiredProviderTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "kimik2",
        "antigravity", // now a source under Gemini
    };

    public static readonly IReadOnlyList<ProviderType> AddableTypes =
        Types.Where(type => !InternalProviderTypes.Contains(type.Id) && !RetiredProviderTypes.Contains(type.Id)).ToArray();

    public static bool IsAddableProviderType(string providerType) =>
        FindType(providerType) is not null
        && !InternalProviderTypes.Contains(providerType)
        && !RetiredProviderTypes.Contains(providerType);

    public static bool IsInternalProviderType(string providerType) =>
        InternalProviderTypes.Contains(providerType);

    public static bool IsRetiredProviderType(string providerType) =>
        RetiredProviderTypes.Contains(providerType);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> DefaultConfigValue =
        new(BuildDefaultConfig);

    public static IReadOnlyDictionary<string, string> DefaultConfig => DefaultConfigValue.Value;

    /// <summary>
    /// Renamed configuration fields. ConfigService applies these aliases generically
    /// to both global and instance-scoped keys, so compatibility does not leak into
    /// provider or view-model code.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ConfigKeyAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["show_antigravity_other_quotas"] = "show_other_quota_groups",
        };

    private static readonly IReadOnlyDictionary<string, string> GlobalDefaultConfig = new Dictionary<string, string>
    {
        [DefaultLaunchEditorPathKey] = "",
        ["language"] = "",
        ["empty_threshold_pct"] = "5",
        ["deprioritize_empty_providers"] = "true",
        ["hide_sensitive_info"] = "false",
        ["sort_priority_order"] = "plan-value,reset-frequency,next-reset",
        // Desktop-app launch paths are GLOBAL (one per provider type, shared by every
        // instance) rather than per-instance fields.
        ["claude_app_path"] = "",
        ["codex_app_path"] = "",
        ["codex_lb_app_path"] = "",
        ["antigravity_path"] = "",
        ["kiro_app_path"] = "",
        ["qoder_app_path"] = "",
        ["kimi_app_path"] = "",
        ["gemini_app_path"] = "",
        ["openai_app_path"] = "",
        ["moonshot_app_path"] = "",
        ["zcode_app_path"] = "",
        ["alibaba_app_path"] = "",
        ["cursor_app_path"] = "",
        ["windsurf_app_path"] = "",
        ["warp_app_path"] = "",
        ["copilot_app_path"] = "",
        ["factory_app_path"] = "",
        ["perplexity_app_path"] = "",
        ["ollama_app_path"] = "",
        ["doubao_app_path"] = "",
    };

    private static readonly IReadOnlyDictionary<string, string> FieldDefaultOverrides = new Dictionary<string, string>
    {
        ["codex_lb_url"] = "http://127.0.0.1:2455",
        ["show_other_quota_groups"] = "false",
    };

    private static readonly IReadOnlyDictionary<string, string> PlanValueOverrideKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex-lb"] = "codex_lb_value",
        };

    public static string? PlanValueOverrideKeyFor(string providerType) =>
        PlanValueOverrideKeys.TryGetValue(providerType, out var key) ? key : null;

    public static readonly IReadOnlySet<string> SensitiveKeys = new HashSet<string>
    {
        "deepseek_key", "deepseek_user_token", "alibabacloud_key_id", "alibabacloud_key_secret",
        "openrouter_key", "openrouter_management_key", "moonshot_key", "venice_key", "crof_key",
        "openai_key", "copilot_key", "bedrock_access_key_id", "bedrock_secret_access_key", "bedrock_session_token", "vertexai_credentials_path", "azureopenai_key", "azureopenai_arm_token", "elevenlabs_key", "warp_key", "codebuff_key", "synthetic_key", "zai_key", "llmproxy_key",
        "doubao_key", "groq_key", "deepgram_key",
        "grok_path",
        "kilo_key", "kilo_auth_path",
        "jetbrains_base_path",
        "kimik2_key",
        "codex_home", "codex_app_path", "claude_path", "claude_app_path", "antigravity_path", "kiro_app_path", "kiro_cli_path",
        "zcode_home", "zcode_app_path",
        "codex_lb_app_path", "qoder_app_path", "qoder_cli_path", DefaultLaunchEditorPathKey,
    };

    public static readonly IReadOnlyDictionary<string, ProviderField[]> Fields = new Dictionary<string, ProviderField[]>
    {
        ["codex-lb"] = new[]
        {
            new ProviderField("codex_lb_url", "Base URL", "http://127.0.0.1:2455",
                Description: "Local codex-lb service endpoint."),

        },
        ["codex"] = new[]
        {
            new ProviderField("codex_home", "Codex home", @"%USERPROFILE%\.codex",
                Description: "Optional Codex home directory. Leave empty to use CODEX_HOME or the default user profile location."),
            new ProviderField("codex_path", "Codex CLI executable", "codex", IsFilePath: true,
                Description: "Path to the Codex CLI used for interactive sign-in. Leave empty to use 'codex' on PATH."),
            new ProviderField("codex_chatgpt_base_url", "ChatGPT base URL", "https://chatgpt.com/backend-api",
                Description: "Optional Codex usage API base URL. Leave empty unless your Codex configuration uses a custom ChatGPT base URL."),

        },
        ["copilot"] = new[]
        {
            new ProviderField("copilot_key", "GitHub token", "gho_...", IsPassword: true, IsRequired: true,
                Description: "GitHub OAuth token with Copilot access. The provider calls GitHub's Copilot usage endpoint."),
            new ProviderField("copilot_enterprise_host", "Enterprise host", "github.com",
                Description: "Optional GitHub Enterprise host. Leave empty for github.com."),
        },
        ["gemini"] = new[]
        {
            new ProviderField("gemini_app_path", "Antigravity app path", @"%LOCALAPPDATA%\Programs\Antigravity\Antigravity.exe",
                IsFilePath: true, IsGlobal: true,
                Description: "Optional Antigravity or Antigravity IDE executable. Leave empty to detect either installed app automatically."),
            new ProviderField("gemini_auto_launch_app", "Automatically start Antigravity in background", IsToggle: true,
                Description: "When the App source is selected, start Antigravity hidden before refresh if its local quota service is not already running."),
            new ProviderField("gemini_home", "Gemini CLI data directory", @"%USERPROFILE%\.gemini",
                Description: "Directory where the Gemini CLI stores its OAuth credentials (oauth_creds.json). Leave empty to use the .gemini directory under your user profile."),
            new ProviderField("gemini_path", "Gemini CLI executable", "gemini", IsFilePath: true,
                Description: "Path to the gemini executable. Used to refresh the OAuth token and open the sign-in window. Leave empty to use 'gemini' on PATH."),

        },
        ["bedrock"] = new[]
        {
            new ProviderField("bedrock_auth_mode", "Auth mode", "keys or profile",
                Description: "Optional. Use profile to force AWS CLI profile authentication; otherwise QuotaLens uses static keys when present and falls back to AWS_PROFILE."),
            new ProviderField("bedrock_access_key_id", "Access key ID", "AKIA...", IsPassword: true,
                Description: "AWS access key with Cost Explorer permission. Leave empty when using an AWS profile."),
            new ProviderField("bedrock_secret_access_key", "Secret access key", "...", IsPassword: true,
                Description: "AWS secret access key. Leave empty when using an AWS profile."),
            new ProviderField("bedrock_session_token", "Session token", "...", IsPassword: true,
                Description: "Optional AWS session token for temporary credentials."),
            new ProviderField("bedrock_profile", "AWS profile", "default",
                Description: "Optional AWS CLI profile. QuotaLens uses aws configure export-credentials, including SSO and assume-role profiles."),
            new ProviderField("bedrock_region", "Region", "us-east-1",
                Description: "Optional Bedrock region label. Cost Explorer itself is queried in us-east-1."),
            new ProviderField("bedrock_budget", "Monthly budget", "50",
                Description: "Optional USD budget used to convert monthly spend into a utilization bar."),
            new ProviderField("bedrock_aws_cli_path", "AWS CLI", "aws", IsFilePath: true,
                Description: "Optional AWS CLI executable path used for profile authentication."),
            new ProviderField("bedrock_cost_explorer_url", "Cost Explorer URL", "https://ce.us-east-1.amazonaws.com",
                Description: "Optional Cost Explorer endpoint override. Leave empty unless using a test endpoint or proxy."),
        },
        ["vertexai"] = new[]
        {
            new ProviderField("vertexai_credentials_path", "ADC credentials", @"%APPDATA%\gcloud\application_default_credentials.json", IsFilePath: true,
                Description: "Optional Application Default Credentials file. Leave empty to use GOOGLE_APPLICATION_CREDENTIALS or the gcloud default location."),
            new ProviderField("vertexai_project_id", "Project ID", "my-gcp-project",
                Description: "Optional Google Cloud project. Leave empty to use the credentials file, gcloud config, or environment."),
            new ProviderField("vertexai_gcloud_path", "gcloud CLI", "gcloud", IsFilePath: true,
                Description: "Optional gcloud executable path. Used for service account ADC token printing."),
            new ProviderField("vertexai_gcloud_config_dir", "gcloud config directory", @"%APPDATA%\gcloud", IsFilePath: true,
                Description: "Optional gcloud configuration directory. Leave empty to use CLOUDSDK_CONFIG or the default location."),
        },
        ["claude"] = new[]
        {
            new ProviderField("claude_path", "Claude Code executable", "claude", IsFilePath: true,
                Description: "Leave empty to use the claude command on PATH."),

        },
        ["deepseek"] = new[]
        {
            new ProviderField("deepseek_key", "API Key", "sk-...", IsPassword: true, IsRequired: true),
            new ProviderField("deepseek_user_token", "Platform user token", "...", IsPassword: true,
                Description: "Optional private dashboard session token for detailed usage. It is never replaced with or inferred from the API key."),
        },
        ["kiro"] = new[]
        {

            new ProviderField("kiro_cli_path", "CLI location", @"%LOCALAPPDATA%\Kiro-Cli\kiro-cli.exe", IsFilePath: true,
                Description: "Leave empty to use KIRO_CLI_PATH, then the default LocalAppData install path."),
        },
        ["alibaba"] = new[]
        {
            new ProviderField("alibaba_url", "Coding Plan URL", "https://bailian.console.aliyun.com/cn-beijing/?tab=model#/efm/coding_plan",
                Description: "Capture/return page after signing in. Leave empty for Aliyun/Bailian CN; set an alibabacloud.com ModelStudio URL only if your Coding Plan lives in the international console."),
            new ProviderField("alibabacloud_key_id", "AccessKey ID", "...",
                Description: "Optional. Reads the same-account pay-as-you-go balance used after Coding Plan tokens run out."),
            new ProviderField("alibabacloud_key_secret", "AccessKey Secret", "...", IsPassword: true,
                Description: "Optional. Used with the AccessKey ID to read the overflow API balance on this card."),
        },
        ["alibabacloud"] = new[]
        {
            new ProviderField("alibabacloud_key_id", "AccessKey ID", "...", IsRequired: true),
            new ProviderField("alibabacloud_key_secret", "AccessKey Secret", "...", IsPassword: true, IsRequired: true),
        },
        ["alibabatokenplan"] = new[]
        {
            new ProviderField("alibabatokenplan_url", "Login URL", "https://bailian.console.aliyun.com/cn-beijing?tab=plan#/efm/subscription/token-plan",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["antigravity"] = new[]
        {

            new ProviderField("show_other_quota_groups", "Show Other model group", IsToggle: true,
                Description: "Include non-Claude and non-Gemini Antigravity model quotas in the card."),
        },
        ["bayesdl"] = new[]
        {
            new ProviderField("bayesdl_url", "Login URL", "https://ai.bayesdl.com/base/login",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["mimo"] = new[]
        {
            new ProviderField("mimo_url", "Login URL", "https://platform.xiaomimimo.com/console/plan-manage",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["qoder"] = new[]
        {

            new ProviderField("qoder_cli_path", "Qoder CLI location", @"%ProgramFiles%\QoderWork\QoderWork\resources\bin\qodercli.exe", IsFilePath: true,
                Description: "Leave empty to use the default QoderWork install path."),
            new ProviderField("qoder_token", "Personal access token", "...", IsPassword: true,
                Description: "Optional qodercli token. Import it from QODER_PERSONAL_ACCESS_TOKEN, or paste it here."),
        },
        ["kimi"] = new[]
        {
            new ProviderField("kimi_app_path", "Kimi app path", @"%LOCALAPPDATA%\Programs\kimi-desktop\Kimi.exe",
                IsFilePath: true, IsGlobal: true,
                Description: "Kimi desktop app executable. Leave empty to auto-detect the installed app."),
            new ProviderField("kimi_cli_path", "Kimi CLI path", "kimi", IsFilePath: true,
                Description: "Optional Kimi Code CLI executable. Used to refresh the OAuth token; leave empty to use 'kimi' on PATH."),

            new ProviderField("kimi_url", "Login URL", "https://www.kimi.com/code/console",
                Description: "Opened when signing in from this provider's settings. If the Kimi Code CLI is installed and logged in ('kimi login'), its credentials are used automatically instead."),
        },
        ["amp"] = new[]
        {
            new ProviderField("amp_url", "Login URL", "https://ampcode.com/settings",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["cursor"] = new[]
        {
            new ProviderField("cursor_url", "Login URL", "https://cursor.com/settings",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["augment"] = new[]
        {
            new ProviderField("augment_url", "Login URL", "https://app.augmentcode.com",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["factory"] = new[]
        {
            new ProviderField("factory_url", "Login URL", "https://app.factory.ai",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["minimax"] = new[]
        {
            new ProviderField("minimax_url", "Login URL", "https://platform.minimax.io/user-center/payment/token-plan",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["windsurf"] = new[]
        {
            new ProviderField("windsurf_url", "Login URL", "https://windsurf.com/subscription/usage",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["openrouter"] = new[]
        {
            new ProviderField("openrouter_key", "API Key", "sk-or-...", IsPassword: true, IsRequired: true,
                Description: "Reads this key's credit limit and daily, weekly, and monthly usage from /api/v1/key."),
            new ProviderField("openrouter_management_key", "Management Key", "sk-or-...", IsPassword: true,
                Description: "Optional. Adds account credit balance from /api/v1/credits. The ordinary API key is never sent to this management-only endpoint."),
        },
        ["moonshot"] = new[]
        {
            new ProviderField("moonshot_key", "API Key", "sk-...", IsPassword: true, IsRequired: true,
                Description: "Uses the international Moonshot balance endpoint by default."),
            new ProviderField("moonshot_base_url", "API base URL", "https://api.moonshot.ai",
                Description: "Optional. Use https://api.moonshot.cn for a China-region account."),
        },
        ["venice"] = new[]
        {
            new ProviderField("venice_key", "API Key", "api_...", IsPassword: true, IsRequired: true),
        },
        ["crof"] = new[]
        {
            new ProviderField("crof_key", "API Key", "...", IsPassword: true, IsRequired: true),
        },
        ["openai"] = new[]
        {
            new ProviderField("openai_key", "Organization Admin Key", "sk-admin-...", IsPassword: true, IsRequired: true,
                Description: "Requires an organization Admin API key (or OPENAI_ADMIN_KEY). Standard OPENAI_API_KEY credentials are never used."),
            new ProviderField("openai_project_ids", "Project IDs", "proj_abc, proj_def",
                Description: "Optional comma-separated filter applied to both organization usage and cost queries."),
        },
        ["azureopenai"] = new[]
        {
            new ProviderField("azureopenai_subscription_id", "Azure subscription ID", "00000000-0000-0000-0000-000000000000", IsRequired: true,
                Description: "Subscription containing the regional Azure OpenAI quota."),
            new ProviderField("azureopenai_location", "Azure location", "eastus", IsRequired: true,
                Description: "Azure region whose Cognitive Services quota should be read."),
            new ProviderField("azureopenai_arm_token", "ARM access token", "Optional bearer token", IsPassword: true,
                Description: "Optional short-lived Resource Manager token. When empty, QuotaLens uses the signed-in Azure CLI session."),
            new ProviderField("azureopenai_az_path", "Azure CLI path", "az", IsFilePath: true,
                Description: "Optional path to az.exe used only to obtain a read-only Resource Manager token."),
            new ProviderField("azureopenai_key", "Legacy resource API key", "...", IsPassword: true,
                Description: "Retained for migration only. Resource API keys are never used during quota refresh."),
            new ProviderField("azureopenai_endpoint", "Legacy resource endpoint", "https://your-resource.openai.azure.com",
                Description: "Retained for migration only. Resource endpoints cannot report Azure quota."),
            new ProviderField("azureopenai_deployment", "Legacy deployment", "gpt-4o",
                Description: "Retained for migration only. QuotaLens never validates deployments with inference."),
            new ProviderField("azureopenai_api_version", "API version", "2024-10-21",
                Description: "Retained for migration only and never used during quota refresh."),
        },
        ["elevenlabs"] = new[]
        {
            new ProviderField("elevenlabs_key", "API Key", "...", IsPassword: true, IsRequired: true),
            new ProviderField("elevenlabs_base_url", "API base URL", "https://api.elevenlabs.io",
                Description: "Optional ElevenLabs API base URL. Leave empty unless using a proxy or alternate host."),
        },
        ["warp"] = new[]
        {
            new ProviderField("warp_key", "API Key", "...", IsPassword: true, IsRequired: true),
        },
        ["codebuff"] = new[]
        {
            new ProviderField("codebuff_key", "API Key", "...", IsPassword: true, IsRequired: true),
            new ProviderField("codebuff_base_url", "API base URL", "https://www.codebuff.com",
                Description: "Optional Codebuff API base URL. Leave empty unless using a proxy or alternate host."),
        },
        ["synthetic"] = new[]
        {
            new ProviderField("synthetic_key", "API Key", "...", IsPassword: true, IsRequired: true),
            new ProviderField("synthetic_url", "Quota URL", "https://api.synthetic.new/v2/quotas",
                Description: "Optional Synthetic quota endpoint. Leave empty to use the default API endpoint."),
        },
        ["zai"] = new[]
        {
            new ProviderField("zai_key", "API Key", "...", IsPassword: true, IsRequired: true,
                Description: "z.ai API key from https://open.bigmodel.cn or https://api.z.ai to track API token packs and balance. This is a different pool than the ZCode token plan."),
            new ProviderField("zai_base_url", "API base URL", "https://api.z.ai",
                Description: "Optional z.ai API base URL. Use https://open.bigmodel.cn for BigModel CN accounts."),
            new ProviderField("zai_quota_url", "Quota URL", "https://api.z.ai/api/monitor/usage/quota/limit",
                Description: "Optional full quota URL. Leave empty to derive it from the base URL."),
        },
        ["zcode"] = new[]
        {
            new ProviderField("zcode_home", "ZCode data folder", @"%USERPROFILE%\.zcode", IsFilePath: true,
                Description: "Where ZCode keeps its signed-in session (credentials live under v2). Set this if ZCode stores its data somewhere other than the default."),
            new ProviderField("zcode_app_path", "ZCode app location", @"%LOCALAPPDATA%\Programs\ZCode\ZCode.exe", IsFilePath: true,
                Description: "Only needed if ZCode is installed outside the default location."),
        },
        ["llmproxy"] = new[]
        {
            new ProviderField("llmproxy_key", "API Key", "...", IsPassword: true, IsRequired: true),
            new ProviderField("llmproxy_base_url", "Base URL", "https://proxy.example.com", IsRequired: true,
                Description: "Required LLM Proxy host. QuotaLens reads /v1/quota-stats from this base URL."),
        },
        ["doubao"] = new[]
        {
            new ProviderField("doubao_cli_path", "arkcli location", "arkcli", IsFilePath: true,
                Description: "Optional path to an existing arkcli installation. Refresh runs only: arkcli usage plan --format json."),
        },
        ["groq"] = new[]
        {
            new ProviderField("groq_key", "API Key", "...", IsPassword: true, IsRequired: true),
            new ProviderField("groq_base_url", "API base URL", "https://api.groq.com/v1",
                Description: "Optional Groq API base URL. Metrics require Enterprise Prometheus access."),
        },
        ["deepgram"] = new[]
        {
            new ProviderField("deepgram_key", "API Key", "...", IsPassword: true, IsRequired: true),
            new ProviderField("deepgram_project_id", "Project ID", "...",
                Description: "Optional. Leave empty to aggregate every visible project."),
            new ProviderField("deepgram_base_url", "API base URL", "https://api.deepgram.com/v1",
                Description: "Optional Deepgram API base URL."),
        },
        ["grok"] = new[]
        {
            new ProviderField("grok_path", "Grok CLI location", "grok", IsFilePath: true,
                Description: "Leave empty to use GROK_CLI_PATH, then the grok command on PATH. QuotaLens reuses the CLI login to read billing quota and subscription identity."),
        },
        ["kilo"] = new[]
        {
            new ProviderField("kilo_key", "API Key", "...", IsPassword: true,
                Description: "Optional. Leave empty to use the Kilo CLI auth file."),
            new ProviderField("kilo_auth_path", "CLI auth file", @"%USERPROFILE%\.local\share\kilo\auth.json", IsFilePath: true,
                Description: "Optional Kilo CLI auth file created by kilo login."),
            new ProviderField("kilo_organization_id", "Organization ID", "...",
                Description: "Optional. Leave empty for personal Kilo usage."),
            new ProviderField("kilo_base_url", "API base URL", "https://app.kilo.ai/api/trpc",
                Description: "Optional Kilo tRPC endpoint."),
        },
        ["jetbrains"] = new[]
        {
            new ProviderField("jetbrains_base_path", "IDE config folder", @"%APPDATA%\JetBrains\WebStorm2025.2", IsFilePath: true,
                Description: "Optional JetBrains IDE configuration folder. Leave empty to auto-detect the most recently updated IDE with AI Assistant quota data."),
        },
        ["manus"] = new[]
        {
            new ProviderField("manus_url", "Login URL", "https://manus.im",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["perplexity"] = new[]
        {
            new ProviderField("perplexity_url", "Login URL", "https://www.perplexity.ai/account/usage",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["t3chat"] = new[]
        {
            new ProviderField("t3chat_url", "Login URL", "https://t3.chat/settings/customization",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["commandcode"] = new[]
        {
            new ProviderField("commandcode_url", "Login URL", "https://commandcode.ai/studio",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["ollama"] = new[]
        {
            new ProviderField("ollama_url", "Login URL", "https://ollama.com/settings",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["abacus"] = new[]
        {
            new ProviderField("abacus_url", "Login URL", "https://apps.abacus.ai/chatllm/admin/compute-points-usage",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["stepfun"] = new[]
        {
            new ProviderField("stepfun_url", "Login URL", "https://platform.stepfun.com/plan-usage",
                Description: "Opened when signing in from this provider's settings."),
        },
        ["opencode"] = new[]
        {
            new ProviderField("opencode_url", "Login URL", "https://opencode.ai",
                Description: "Opened when signing in from this provider's settings."),
            new ProviderField("opencode_workspace_id", "Workspace ID", "wrk_...",
                Description: "Optional. Leave empty to use the first workspace returned by OpenCode."),
        },
        ["opencodego"] = new[]
        {
            new ProviderField("opencodego_cli_path", "OpenCode CLI path", "opencode", IsFilePath: true,
                Description: "Optional path used to read OpenCode Go usage from the local history database without opening a browser."),
            new ProviderField("opencodego_url", "Login URL", "https://opencode.ai",
                Description: "Opened when signing in from this provider's settings."),
            new ProviderField("opencodego_workspace_id", "Workspace ID", "wrk_...",
                Description: "Optional. Leave empty to use the first workspace returned by OpenCode."),
        },
        ["mistral"] = new[]
        {
            new ProviderField("mistral_url", "Login URL", "https://admin.mistral.ai/organization/usage",
                Description: "Opened when signing in from this provider's settings."),
        },
    };

    private const string PlanPricingVerifiedAt = "2026-08-02";

    private static ProviderPlanValueRule OfficialMonthlyPlan(
        string keyword,
        double monthlyValue,
        string planId,
        string source,
        string seatBasis = "account",
        string region = "US/default storefront",
        string? priceQualifier = null,
        string? availabilityNote = null) => new(
            keyword,
            monthlyValue,
            PlanId: planId,
            PriceAmount: monthlyValue,
            Currency: "USD",
            Region: region,
            Cadence: "monthly",
            SeatBasis: seatBasis,
            OfficialSource: source,
            LastVerifiedAt: PlanPricingVerifiedAt,
            PriceQualifier: priceQualifier,
            AvailabilityNote: availabilityNote,
            Evidence: ProviderPlanEvidence.Official);

    // Estimated weekly token allowances per plan (millions, cache-inclusive).
    // Sourced 2026-08 from official limits pages, credit rate cards, and community
    // telemetry, cross-validated adversarially (three independent $200 tiers —
    // Claude Max 20x, Codex Pro, Google Ultra 20x — all landed at ~600M/week).
    // Ordering matters: first matching keyword wins, so specific tiers precede
    // generic ones. A value of 0 means the coding agent is structurally
    // unavailable on that plan (segment is hidden, not rendered as a sliver).
    public static readonly IReadOnlyDictionary<string, ProviderPlanTokenRule[]> DefaultPlanTokenRules = new Dictionary<string, ProviderPlanTokenRule[]>
    {
        ["claude"] = new ProviderPlanTokenRule[]
        {
            new("max 20x", 600), new("max20x", 600), new("20x", 600),
            new("team premium", 350), new("premium seat", 350),
            new("max 5x", 350), new("max5x", 350), new("5x", 350), new("max", 350),
            new("team standard", 100), new("standard seat", 100), new("team", 100),
            new("enterprise", 350),
            new("pro", 100),
            new("free", 0),
        },
        // Bare "pro" is deliberately the 5x minimum (matching DefaultPlanValueRules'
        // "minimum when the 5x/20x tier is not reported" convention).
        ["codex"] = new ProviderPlanTokenRule[]
        {
            new("pro 5x", 160), new("pro5x", 160), new("5x", 160),
            new("pro 20x", 600), new("pro20x", 600), new("20x", 600),
            new("enterprise", 45), new("edu", 45),
            new("business", 32), new("team", 32), new("plus", 32),
            new("pro", 160),
            new("go", 11),
            new("free", 1.5),
        },
        ["codex-lb"] = new ProviderPlanTokenRule[]
        {
            new("pro 5x", 160), new("pro5x", 160), new("5x", 160),
            new("pro 20x", 600), new("pro20x", 600), new("20x", 600),
            new("enterprise", 45), new("edu", 45),
            new("business", 32), new("team", 32), new("plus", 32),
            new("pro", 160),
            new("go", 11),
            new("free", 1.5),
        },
        // Gemini CLI and Antigravity draw from the same Google AI subscription.
        ["gemini"] = new ProviderPlanTokenRule[]
        {
            new("ultra max", 600), new("20x", 600), new("max", 600),
            new("ultra", 150),
            new("pro", 30),
            new("plus", 8), new("individual", 8), new("no plan", 8), new("base", 8), new("free", 8),
        },
        ["antigravity"] = new ProviderPlanTokenRule[]
        {
            new("ultra max", 600), new("20x", 600), new("max", 600),
            new("ultra", 150),
            new("pro", 30),
            new("plus", 8), new("individual", 8), new("no plan", 8), new("base", 8), new("free", 8),
        },
        // Anchored on community-measured Allegretto (~330M/wk); other tiers follow
        // Moonshot's published credit ladder (1x/4x/20x/60x CN = 1x/5x/15x/30x intl),
        // so Moderato uses the ladder-consistent 66 rather than the request-cap-derived 92.
        ["kimi"] = new ProviderPlanTokenRule[]
        {
            new("vivace", 1750),
            new("allegro", 900),
            new("allegretto", 330), new("advanced", 330),
            new("moderato", 66), new("intermediate", 66),
            new("andante", 17), new("basic", 17),
            new("adagio", 0), new("free", 0),
        },
        ["cursor"] = new ProviderPlanTokenRule[]
        {
            new("ultra", 250),
            new("teams premium", 160), new("team premium", 160), new("premium", 160),
            new("pro+", 150), new("pro plus", 150), new("proplus", 150),
            new("teams", 120), new("team", 120), new("business", 120),
            new("hobby", 2), new("free", 2),
            new("pro", 120),
        },
        ["windsurf"] = new ProviderPlanTokenRule[]
        {
            new("max", 44),
            new("teams", 7), new("team", 7), new("pro", 7),
            new("free", 0.5),
        },
        ["copilot"] = new ProviderPlanTokenRule[]
        {
            new("pro+", 16), new("pro plus", 16),
            new("max", 46),
            new("enterprise", 9),
            new("business", 4.4),
            new("pro", 3.5),
            new("free", 0.4),
        },
        ["qoder"] = new ProviderPlanTokenRule[]
        {
            new("ultra", 46),
            new("team", 6.9),
            new("pro trial", 1.5), new("trial", 1.5),
            new("pro plus", 14), new("pro+", 14),
            new("pro", 4.6),
            new("community", 0.5), new("basic", 0.5), new("free", 0.5),
        },
        ["mimo"] = new ProviderPlanTokenRule[]
        {
            new("max", 390),
            new("pro", 180),
            new("standard", 53),
            new("lite", 20),
            new("trial", 1), new("payg", 1), new("free", 1),
        },
        ["kiro"] = new ProviderPlanTokenRule[]
        {
            new("power", 92),
            new("pro max", 46), new("promax", 46),
            new("pro+", 18), new("pro plus", 18),
            new("pro", 9),
            new("free", 0.5),
        },
        ["bayesdl"] = new ProviderPlanTokenRule[]
        {
            new("coding pro", 110),
            new("token pro", 4),
            new("token standard", 2),
            new("token lite", 0.5),
            new("体验包", 0.5),
            new("千万token", 1), new("免费", 1),
        },
        // Amp Free capped below Megawatt: the historical 5M free grant is being
        // wound down and a free tier out-sizing the entry paid tier reads as a bug.
        ["amp"] = new ProviderPlanTokenRule[]
        {
            new("gigawatt", 24),
            new("megawatt", 4),
            new("enterprise", 30),
            new("pay as you go", 6), new("payg", 6),
            new("free", 3),
        },
        ["factory"] = new ProviderPlanTokenRule[]
        {
            new("enterprise", 120),
            new("business", 45), new("team", 45), new("plus", 45),
            new("max", 90),
            new("pro", 9),
        },
        ["warp"] = new ProviderPlanTokenRule[]
        {
            new("build", 3),
            new("max", 35),
            new("business", 3),
            new("enterprise", 6),
            new("free", 0),
        },
    };

    public static readonly IReadOnlyDictionary<string, ProviderPlanValueRule[]> DefaultPlanValueRules = new Dictionary<string, ProviderPlanValueRule[]>
    {
        ["codex-lb"] = new[]
        {
            OfficialMonthlyPlan("business", 25, "chatgpt-business-monthly", "https://chatgpt.com/pricing", "user",
                priceQualifier: "monthly billing; annual billing is $20/user/month"),
            OfficialMonthlyPlan("pro 20x", 200, "chatgpt-pro-20x", "https://help.openai.com/en/articles/9793128-what-is-chatgpt-pro"),
            OfficialMonthlyPlan("pro 20", 200, "chatgpt-pro-20x", "https://help.openai.com/en/articles/9793128-what-is-chatgpt-pro"),
            OfficialMonthlyPlan("pro 5x", 100, "chatgpt-pro-5x", "https://help.openai.com/en/articles/9793128-what-is-chatgpt-pro"),
            OfficialMonthlyPlan("pro 5", 100, "chatgpt-pro-5x", "https://help.openai.com/en/articles/9793128-what-is-chatgpt-pro"),
            OfficialMonthlyPlan("pro", 100, "chatgpt-pro-5x-minimum", "https://help.openai.com/en/articles/9793128-what-is-chatgpt-pro",
                priceQualifier: "minimum when the 5x/20x tier is not reported"),
            new ProviderPlanValueRule("team", 25),
            OfficialMonthlyPlan("plus", 20, "chatgpt-plus", "https://chatgpt.com/pricing"),
            OfficialMonthlyPlan("go", 8, "chatgpt-go", "https://chatgpt.com/pricing"),
            OfficialMonthlyPlan("free", 0, "chatgpt-free", "https://chatgpt.com/pricing"),
        },
        ["codex"] = new[]
        {
            OfficialMonthlyPlan("business", 25, "chatgpt-business-monthly", "https://chatgpt.com/pricing", "user",
                priceQualifier: "monthly billing; annual billing is $20/user/month"),
            OfficialMonthlyPlan("pro 20x", 200, "chatgpt-pro-20x", "https://help.openai.com/en/articles/9793128-what-is-chatgpt-pro"),
            OfficialMonthlyPlan("pro 20", 200, "chatgpt-pro-20x", "https://help.openai.com/en/articles/9793128-what-is-chatgpt-pro"),
            OfficialMonthlyPlan("pro 5x", 100, "chatgpt-pro-5x", "https://help.openai.com/en/articles/9793128-what-is-chatgpt-pro"),
            OfficialMonthlyPlan("pro 5", 100, "chatgpt-pro-5x", "https://help.openai.com/en/articles/9793128-what-is-chatgpt-pro"),
            OfficialMonthlyPlan("pro", 100, "chatgpt-pro-5x-minimum", "https://help.openai.com/en/articles/9793128-what-is-chatgpt-pro",
                priceQualifier: "minimum when the 5x/20x tier is not reported"),
            new ProviderPlanValueRule("team", 25),
            OfficialMonthlyPlan("plus", 20, "chatgpt-plus", "https://chatgpt.com/pricing"),
            OfficialMonthlyPlan("go", 8, "chatgpt-go", "https://chatgpt.com/pricing"),
            OfficialMonthlyPlan("free", 0, "chatgpt-free", "https://chatgpt.com/pricing"),
        },
        ["copilot"] = new[]
        {
            OfficialMonthlyPlan("enterprise", 39, "copilot-enterprise", "https://github.com/features/copilot/plans", "user"),
            OfficialMonthlyPlan("business", 19, "copilot-business", "https://github.com/features/copilot/plans", "user",
                availabilityNote: "some new self-serve sign-ups are temporarily paused"),
            OfficialMonthlyPlan("max", 100, "copilot-max", "https://github.com/features/copilot/plans", "user"),
            OfficialMonthlyPlan("pro+", 39, "copilot-pro-plus", "https://github.com/features/copilot/plans", "user"),
            OfficialMonthlyPlan("pro", 10, "copilot-pro", "https://github.com/features/copilot/plans", "user"),
            OfficialMonthlyPlan("free", 0, "copilot-free", "https://github.com/features/copilot/plans", "user"),
        },
        ["gemini"] = new[]
        {
            OfficialMonthlyPlan("ultra", 250, "google-ai-ultra", "https://one.google.com/about/google-ai-plans"),
            OfficialMonthlyPlan("google ai pro", 20, "google-ai-pro", "https://one.google.com/about/google-ai-plans"),
            OfficialMonthlyPlan("ai pro", 20, "google-ai-pro", "https://one.google.com/about/google-ai-plans"),
            OfficialMonthlyPlan("gemini pro", 20, "google-ai-pro", "https://one.google.com/about/google-ai-plans"),
            OfficialMonthlyPlan("pro", 20, "google-ai-pro", "https://one.google.com/about/google-ai-plans"),
            new ProviderPlanValueRule("paid", 20),
            new ProviderPlanValueRule("workspace", 0),
            OfficialMonthlyPlan("free", 0, "google-ai-free", "https://one.google.com/about/google-ai-plans"),
        },
        ["bedrock"] = new[]
        {
            new ProviderPlanValueRule("bedrock", -1),
        },
        ["vertexai"] = new[]
        {
            new ProviderPlanValueRule("vertex", -1),
        },
        ["claude"] = new[]
        {
            OfficialMonthlyPlan("team premium", 125, "claude-team-premium-monthly", "https://claude.com/pricing", "seat"),
            OfficialMonthlyPlan("team standard", 25, "claude-team-standard-monthly", "https://claude.com/pricing", "seat"),
            OfficialMonthlyPlan("max 20", 200, "claude-max-20x", "https://support.claude.com/en/articles/11049741-what-is-the-max-plan"),
            OfficialMonthlyPlan("max 5", 100, "claude-max-5x", "https://support.claude.com/en/articles/11049741-what-is-the-max-plan"),
            OfficialMonthlyPlan("max", 100, "claude-max-minimum", "https://support.claude.com/en/articles/11049741-what-is-the-max-plan",
                priceQualifier: "minimum when the 5x/20x tier is not reported"),
            new ProviderPlanValueRule("team", 25),
            OfficialMonthlyPlan("pro", 20, "claude-pro-monthly", "https://claude.com/pricing"),
            OfficialMonthlyPlan("free", 0, "claude-free", "https://claude.com/pricing"),
        },
        ["antigravity"] = new[]
        {
            new ProviderPlanValueRule("ultra", 100),
            new ProviderPlanValueRule("pro", 20),
        },
        ["kiro"] = new[]
        {
            new ProviderPlanValueRule("power", 200),
            new ProviderPlanValueRule("pro+", 40),
            new ProviderPlanValueRule("pro", 20),
        },
        ["bayesdl"] = new[]
        {
            new ProviderPlanValueRule("coding pro", 40),
            new ProviderPlanValueRule("token pro", 40),
            new ProviderPlanValueRule("token standard", 20),
            new ProviderPlanValueRule("token lite", 5),
        },
        ["mimo"] = new[]
        {
            OfficialMonthlyPlan("max", 100, "mimo-token-plan-max-monthly", "https://mimo.mi.com/docs/en-US/price/token-plan",
                region: "international/default USD storefront"),
            OfficialMonthlyPlan("pro", 50, "mimo-token-plan-pro-monthly", "https://mimo.mi.com/docs/en-US/price/token-plan",
                region: "international/default USD storefront"),
            OfficialMonthlyPlan("standard", 16, "mimo-token-plan-standard-monthly", "https://mimo.mi.com/docs/en-US/price/token-plan",
                region: "international/default USD storefront"),
            OfficialMonthlyPlan("lite", 6, "mimo-token-plan-lite-monthly", "https://mimo.mi.com/docs/en-US/price/token-plan",
                region: "international/default USD storefront"),
        },
        ["qoder"] = new[]
        {
            new ProviderPlanValueRule("ultra", 200),
            new ProviderPlanValueRule("pro+", 60),
            new ProviderPlanValueRule("pro trial", 0),
            new ProviderPlanValueRule("pro", 20),
        },
        ["kimi"] = new[]
        {
            new ProviderPlanValueRule("vivace", 199),
            new ProviderPlanValueRule("allegro", 99),
            new ProviderPlanValueRule("allegretto", 39),
            new ProviderPlanValueRule("moderato", 19),
            new ProviderPlanValueRule("andante", 7),
            new ProviderPlanValueRule("adagio", 0),
        },
        ["amp"] = new[]
        {
            OfficialMonthlyPlan("gigawatt", 200, "amp-gigawatt", "https://ampcode.com/pricing"),
            OfficialMonthlyPlan("megawatt", 20, "amp-megawatt", "https://ampcode.com/pricing"),
        },
        ["cursor"] = new[]
        {
            OfficialMonthlyPlan("team premium", 120, "cursor-teams-premium-monthly", "https://cursor.com/pricing", "user"),
            OfficialMonthlyPlan("teams premium", 120, "cursor-teams-premium-monthly", "https://cursor.com/pricing", "user"),
            OfficialMonthlyPlan("team standard", 40, "cursor-teams-standard-monthly", "https://cursor.com/pricing", "user"),
            OfficialMonthlyPlan("teams standard", 40, "cursor-teams-standard-monthly", "https://cursor.com/pricing", "user"),
            OfficialMonthlyPlan("ultra", 200, "cursor-ultra-monthly", "https://cursor.com/pricing"),
            OfficialMonthlyPlan("pro+", 60, "cursor-pro-plus-monthly", "https://cursor.com/pricing"),
            OfficialMonthlyPlan("team", 40, "cursor-teams-standard-monthly", "https://cursor.com/pricing", "user"),
            OfficialMonthlyPlan("pro", 20, "cursor-pro-monthly", "https://cursor.com/pricing"),
            OfficialMonthlyPlan("hobby", 0, "cursor-hobby", "https://cursor.com/pricing"),
            OfficialMonthlyPlan("free", 0, "cursor-hobby", "https://cursor.com/pricing"),
        },
        ["augment"] = new[]
        {
            OfficialMonthlyPlan("business", 100, "augment-business-flat", "https://www.augmentcode.com/pricing", "flat subscription (up to 50 seats)"),
        },
        ["factory"] = new[]
        {
            OfficialMonthlyPlan("max", 200, "factory-max", "https://www.factory.ai/pricing"),
            OfficialMonthlyPlan("plus", 100, "factory-plus", "https://www.factory.ai/pricing"),
            OfficialMonthlyPlan("pro", 20, "factory-pro", "https://www.factory.ai/pricing"),
        },
        ["minimax"] = new[]
        {
            OfficialMonthlyPlan("ultra", 120, "minimax-token-plan-ultra", "https://platform.minimax.io/docs/guides/pricing-token-plan"),
            OfficialMonthlyPlan("max", 50, "minimax-token-plan-max", "https://platform.minimax.io/docs/guides/pricing-token-plan"),
            OfficialMonthlyPlan("plus", 20, "minimax-token-plan-plus", "https://platform.minimax.io/docs/guides/pricing-token-plan"),
        },
        ["windsurf"] = new[]
        {
            new ProviderPlanValueRule("teams", 30),
            new ProviderPlanValueRule("team", 30),
            new ProviderPlanValueRule("pro", 15),
            new ProviderPlanValueRule("free", 0),
        },
        ["crof"] = Array.Empty<ProviderPlanValueRule>(),
        ["elevenlabs"] = new[]
        {
            OfficialMonthlyPlan("business", 990, "elevenlabs-business-monthly", "https://elevenlabs.io/pricing", "subscription (includes 10 seats)"),
            OfficialMonthlyPlan("scale", 299, "elevenlabs-scale-monthly", "https://elevenlabs.io/pricing", "subscription (includes 3 seats)"),
            OfficialMonthlyPlan("pro", 99, "elevenlabs-pro-monthly", "https://elevenlabs.io/pricing"),
            OfficialMonthlyPlan("creator", 22, "elevenlabs-creator-monthly", "https://elevenlabs.io/pricing"),
            OfficialMonthlyPlan("starter", 6, "elevenlabs-starter-monthly", "https://elevenlabs.io/pricing"),
            OfficialMonthlyPlan("free", 0, "elevenlabs-free", "https://elevenlabs.io/pricing"),
        },
        ["warp"] = new[]
        {
            OfficialMonthlyPlan("business", 50, "warp-business-from-monthly", "https://www.warp.dev/pricing", "user",
                priceQualifier: "starts at"),
            OfficialMonthlyPlan("max", 200, "warp-max-from-monthly", "https://www.warp.dev/pricing",
                priceQualifier: "starts at"),
            OfficialMonthlyPlan("build", 20, "warp-build-from-monthly", "https://www.warp.dev/pricing",
                priceQualifier: "starts at"),
            OfficialMonthlyPlan("free", 0, "warp-free", "https://www.warp.dev/pricing"),
        },
        ["codebuff"] = Array.Empty<ProviderPlanValueRule>(),
        ["synthetic"] = Array.Empty<ProviderPlanValueRule>(),
        ["zai"] = Array.Empty<ProviderPlanValueRule>(),
        ["zcode"] = Array.Empty<ProviderPlanValueRule>(),
        ["doubao"] = new[]
        {
            new ProviderPlanValueRule("doubao", -1),
        },
        ["groq"] = new[]
        {
            new ProviderPlanValueRule("groq", -1),
        },
        ["deepgram"] = new[]
        {
            new ProviderPlanValueRule("deepgram", -1),
        },
        ["grok"] = new[]
        {
            OfficialMonthlyPlan("supergrok heavy", 300, "supergrok_heavy", "https://x.ai/grok"),
            OfficialMonthlyPlan("supergrok plus", 30, "supergrok_plus", "https://x.ai/grok"),
            OfficialMonthlyPlan("supergrok", 30, "supergrok", "https://x.ai/grok"),
            OfficialMonthlyPlan("premium+", 40, "x_premium_plus", "https://help.x.com/en/using-x/x-premium"),
            OfficialMonthlyPlan("x premium+", 40, "x_premium_plus", "https://help.x.com/en/using-x/x-premium"),
            OfficialMonthlyPlan("premium plus", 40, "x_premium_plus", "https://help.x.com/en/using-x/x-premium"),
            OfficialMonthlyPlan("x premium", 16, "x_premium", "https://help.x.com/en/using-x/x-premium"),
            OfficialMonthlyPlan("premium", 16, "x_premium", "https://help.x.com/en/using-x/x-premium"),
            OfficialMonthlyPlan("free", 0, "grok-free", "https://x.ai/grok"),
        },
        ["kilo"] = new[]
        {
            OfficialMonthlyPlan("kilo pass expert", 199, "kilo-pass-expert", "https://kilo.ai/pricing"),
            OfficialMonthlyPlan("pass expert", 199, "kilo-pass-expert", "https://kilo.ai/pricing"),
            OfficialMonthlyPlan("expert", 199, "kilo-pass-expert", "https://kilo.ai/pricing"),
            OfficialMonthlyPlan("kilo pass pro", 49, "kilo-pass-pro", "https://kilo.ai/pricing"),
            OfficialMonthlyPlan("pass pro", 49, "kilo-pass-pro", "https://kilo.ai/pricing"),
            OfficialMonthlyPlan("pro", 49, "kilo-pass-pro", "https://kilo.ai/pricing"),
            OfficialMonthlyPlan("kilo pass starter", 19, "kilo-pass-starter", "https://kilo.ai/pricing"),
            OfficialMonthlyPlan("pass starter", 19, "kilo-pass-starter", "https://kilo.ai/pricing"),
            OfficialMonthlyPlan("starter", 19, "kilo-pass-starter", "https://kilo.ai/pricing"),
            OfficialMonthlyPlan("teams", 15, "kilo-platform-teams", "https://kilo.ai/pricing", "user"),
            OfficialMonthlyPlan("team", 15, "kilo-platform-teams", "https://kilo.ai/pricing", "user"),
            OfficialMonthlyPlan("free", 0, "kilo-platform-individual-free", "https://kilo.ai/pricing"),
        },
        ["jetbrains"] = new[]
        {
            new ProviderPlanValueRule("ai ultimate", 20),
            new ProviderPlanValueRule("ai pro", 10),
            new ProviderPlanValueRule("free", 0),
        },
        ["manus"] = new[]
        {
            new ProviderPlanValueRule("pro", 39),
            new ProviderPlanValueRule("free", 0),
        },
        ["perplexity"] = new[]
        {
            new ProviderPlanValueRule("max", 200),
            new ProviderPlanValueRule("pro", 20),
            new ProviderPlanValueRule("free", 0),
        },
        ["t3chat"] = new[]
        {
            new ProviderPlanValueRule("ultra", 60),
            new ProviderPlanValueRule("pro", 20),
            new ProviderPlanValueRule("plus", 8),
            new ProviderPlanValueRule("free", 0),
        },
        ["commandcode"] = new[]
        {
            new ProviderPlanValueRule("ultra", 300),
            new ProviderPlanValueRule("max", 150),
            new ProviderPlanValueRule("pro", 30),
            new ProviderPlanValueRule("go", 10),
        },
        ["ollama"] = new[]
        {
            OfficialMonthlyPlan("team", 25, "ollama-team-introductory", "https://ollama.com/pricing", "seat (5-seat minimum)",
                priceQualifier: "introductory",
                availabilityNote: "waitlist; five-seat minimum"),
            OfficialMonthlyPlan("max", 100, "ollama-max", "https://ollama.com/pricing",
                availabilityNote: "new subscriptions temporarily paused"),
            OfficialMonthlyPlan("pro", 20, "ollama-pro", "https://ollama.com/pricing"),
            OfficialMonthlyPlan("free", 0, "ollama-free", "https://ollama.com/pricing"),
        },
        ["abacus"] = new[]
        {
            OfficialMonthlyPlan("pro", 20, "abacus-chatllm-pro", "https://abacus.ai/pricing"),
            OfficialMonthlyPlan("basic", 10, "abacus-chatllm-basic", "https://abacus.ai/pricing",
                priceQualifier: "$7 first month, then $10 recurring"),
        },
        ["stepfun"] = Array.Empty<ProviderPlanValueRule>(),
        ["opencode"] = Array.Empty<ProviderPlanValueRule>(),
        ["opencodego"] = new[]
        {
            OfficialMonthlyPlan("go", 10, "opencode-go-recurring", "https://opencode.ai/go", "workspace (one member)",
                priceQualifier: "$5 first month, then $10 recurring"),
        },
        ["mistral"] = new[]
        {
            new ProviderPlanValueRule("mistral", -1),
        },
        ["deepseek"] = new[]
        {
            new ProviderPlanValueRule("deepseek", -1),
        },
        ["alibaba"] = Array.Empty<ProviderPlanValueRule>(),
        ["alibabacloud"] = new[]
        {
            new ProviderPlanValueRule("alibaba cloud", -1),
        },
        ["alibabatokenplan"] = Array.Empty<ProviderPlanValueRule>(),
        ["openrouter"] = new[]
        {
            new ProviderPlanValueRule("openrouter", -1),
        },
        ["moonshot"] = new[]
        {
            new ProviderPlanValueRule("moonshot", -1),
        },
        ["venice"] = new[]
        {
            new ProviderPlanValueRule("venice", -1),
        },
        ["openai"] = new[]
        {
            new ProviderPlanValueRule("openai", -1),
        },
        ["azureopenai"] = new[]
        {
            new ProviderPlanValueRule("azure openai", -1),
        },
        ["llmproxy"] = new[]
        {
            new ProviderPlanValueRule("llm proxy", -1),
        },
        ["kimik2"] = Array.Empty<ProviderPlanValueRule>(),
    };

    public static readonly IReadOnlySet<string> PayAsYouGoProviderTypes = BuildPayAsYouGoProviderTypes();

    public static readonly IReadOnlySet<string> SubscriptionProviderTypes = BuildSubscriptionProviderTypes();

    /// <summary>
    /// Configuration completeness rules. Every set must be satisfied; a set is
    /// satisfied when at least one of its keys has a scoped value.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ProviderRequiredFieldSet[]> RequiredFieldSets =
        BuildRequiredFieldSets();

    /// <summary>
    /// Flat view of every key that should be seeded into scoped config. Derived
    /// from <see cref="RequiredFieldSets"/> so completeness checks and seeding
    /// cannot drift apart.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> RequiredFields =
        RequiredFieldSets.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .SelectMany(set => set.AnyOf)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The environment variables that map onto each provider's config field. This is
    /// the single source of truth for the "import from environment" step in the edit
    /// dialog: runtime resolution reads config only, and these entries tell the importer
    /// which env var(s) to copy into each (empty) field. SimpleApi-backed providers are
    /// covered by <see cref="SimpleApiProvider.EnvironmentKeysFor"/> instead.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> FieldEnvironment =
        new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["azureopenai"] = FieldEnv(
                ("azureopenai_subscription_id", ["AZURE_SUBSCRIPTION_ID"]),
                ("azureopenai_location", ["AZURE_LOCATION", "AZURE_OPENAI_LOCATION"]),
                ("azureopenai_arm_token", ["AZURE_ACCESS_TOKEN"]),
                ("azureopenai_az_path", ["AZURE_CLI_PATH"])),
            ["alibabacloud"] = FieldEnv(
                ("alibabacloud_key_id", ["ALIBABA_ACCESS_KEY_ID"]),
                ("alibabacloud_key_secret", ["ALIBABA_ACCESS_KEY_SECRET"])),
            ["alibaba"] = FieldEnv(
                ("alibabacloud_key_id", ["ALIBABA_ACCESS_KEY_ID"]),
                ("alibabacloud_key_secret", ["ALIBABA_ACCESS_KEY_SECRET"])),
            ["bedrock"] = FieldEnv(
                ("bedrock_budget", ["CODEXBAR_BEDROCK_BUDGET", "QUOTALENS_BEDROCK_BUDGET"]),
                ("bedrock_cost_explorer_url", ["CODEXBAR_BEDROCK_API_URL", "QUOTALENS_BEDROCK_API_URL"]),
                ("bedrock_auth_mode", ["CODEXBAR_BEDROCK_AUTH_MODE", "QUOTALENS_BEDROCK_AUTH_MODE"]),
                ("bedrock_profile", ["AWS_PROFILE"]),
                ("bedrock_aws_cli_path", ["AWS_CLI_PATH"]),
                ("bedrock_access_key_id", ["AWS_ACCESS_KEY_ID"]),
                ("bedrock_secret_access_key", ["AWS_SECRET_ACCESS_KEY"]),
                ("bedrock_session_token", ["AWS_SESSION_TOKEN"]),
                ("bedrock_region", ["AWS_REGION", "AWS_DEFAULT_REGION"])),
            ["deepseek"] = FieldEnv(
                ("deepseek_key", ["DEEPSEEK_API_KEY"]),
                ("deepseek_user_token", ["DEEPSEEK_PLATFORM_TOKEN", "DEEPSEEK_USER_TOKEN"])),
            ["deepgram"] = FieldEnv(
                ("deepgram_key", ["DEEPGRAM_API_KEY"]),
                ("deepgram_project_id", ["DEEPGRAM_PROJECT_ID"]),
                ("deepgram_base_url", ["DEEPGRAM_API_URL"])),
            ["gemini"] = FieldEnv(("gemini_home", ["GEMINI_HOME"])),
            ["codex"] = FieldEnv(("codex_home", ["CODEX_HOME"])),
            ["kimi"] = FieldEnv(("kimi_cli_path", ["KIMI_CLI_PATH"])),
            ["kilo"] = FieldEnv(
                ("kilo_key", ["KILO_API_KEY"]),
                ("kilo_auth_path", ["KILO_AUTH_PATH"]),
                ("kilo_base_url", ["KILO_API_URL"])),
            ["groq"] = FieldEnv(
                ("groq_key", ["GROQ_API_KEY"]),
                ("groq_base_url", ["GROQ_API_URL"])),
            ["kiro"] = FieldEnv(("kiro_cli_path", ["KIRO_CLI_PATH"])),
            ["opencodego"] = FieldEnv(("opencodego_cli_path", ["OPENCODE_CLI_PATH"])),
            ["vertexai"] = FieldEnv(
                ("vertexai_project_id", ["GOOGLE_CLOUD_PROJECT", "GCLOUD_PROJECT", "CLOUDSDK_CORE_PROJECT"]),
                ("vertexai_gcloud_path", ["GCLOUD_PATH", "GCLOUD_BIN"]),
                ("vertexai_credentials_path", ["GOOGLE_APPLICATION_CREDENTIALS"]),
                ("vertexai_gcloud_config_dir", ["CLOUDSDK_CONFIG"])),
            ["grok"] = FieldEnv(("grok_path", ["GROK_CLI_PATH"])),
            ["doubao"] = FieldEnv(("doubao_cli_path", ["ARKCLI_PATH", "DOUBAO_ARKCLI_PATH"])),
            ["qoder"] = FieldEnv(
                ("qoder_cli_path", ["QODER_CLI_PATH"]),
                ("qoder_token", ["QODER_PERSONAL_ACCESS_TOKEN"])),
            ["openrouter"] = FieldEnv(("openrouter_management_key", ["OPENROUTER_MANAGEMENT_KEY"])),
            ["openai"] = FieldEnv(("openai_project_ids", ["OPENAI_PROJECT_IDS", "OPENAI_PROJECT_ID"])),
            ["elevenlabs"] = FieldEnv(("elevenlabs_base_url", ["ELEVENLABS_API_URL"])),
            ["moonshot"] = FieldEnv(("moonshot_base_url", ["MOONSHOT_API_URL"])),
            ["codebuff"] = FieldEnv(("codebuff_base_url", ["CODEBUFF_API_URL"])),
            ["synthetic"] = FieldEnv(("synthetic_url", ["SYNTHETIC_API_URL"])),
            ["zai"] = FieldEnv(
                ("zai_base_url", ["Z_AI_API_HOST", "ZAI_API_HOST"]),
                ("zai_quota_url", ["Z_AI_QUOTA_URL", "ZAI_QUOTA_URL"])),
            ["llmproxy"] = FieldEnv(("llmproxy_base_url", ["LLM_PROXY_BASE_URL", "LLMPROXY_BASE_URL"])),
            ["copilot"] = FieldEnv(("copilot_enterprise_host", ["COPILOT_ENTERPRISE_HOST"])),
        };

    private static IReadOnlyDictionary<string, string[]> FieldEnv(params (string Key, string[] Env)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Env, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] AntigravityAppPaths =
    [
        @"%LOCALAPPDATA%\Programs\Antigravity\Antigravity.exe",
        @"%LOCALAPPDATA%\Programs\Antigravity IDE\Antigravity IDE.exe",
        @"%ProgramFiles%\Antigravity\Antigravity.exe",
        @"%ProgramFiles%\Antigravity IDE\Antigravity IDE.exe",
    ];

    private static readonly string[] AntigravityExecutableNames =
        ["Antigravity.exe", "Antigravity IDE.exe"];

    private static readonly IReadOnlyDictionary<string, string> AntigravityDisplayNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Antigravity.exe"] = "Antigravity",
            ["Antigravity IDE.exe"] = "Antigravity IDE",
        };

    /// <summary>Providers that get a Launch button for their desktop GUI app.</summary>
    public static readonly IReadOnlyDictionary<string, ProviderLaunchTarget> LaunchTargets = new Dictionary<string, ProviderLaunchTarget>
    {
        ["codex-lb"] = new(
            "ChatGPT",
            "codex_lb_app_path",
            new[]
            {
                @"%ProgramFiles%\WindowsApps\OpenAI.Codex_*\app\ChatGPT.exe",
                @"%ProgramFiles%\WindowsApps\OpenAI.ChatGPT_*\ChatGPT.exe",
                @"%LOCALAPPDATA%\Programs\ChatGPT\ChatGPT.exe",
                @"%LOCALAPPDATA%\Programs\Codex\Codex.exe",
            },
            new[] { "ChatGPT.exe", "Codex.exe" },
            "OpenAI.Codex_2p2nqsd0c76g0",
            @"app\ChatGPT.exe"),
        ["claude"] = new(
            "Claude",
            "claude_app_path",
            new[]
            {
                @"%ProgramFiles%\WindowsApps\Claude_*\app\claude.exe",
                @"%LOCALAPPDATA%\Programs\Claude\Claude.exe",
                @"%LOCALAPPDATA%\Programs\Claude Code\Claude Code.exe",
                @"%ProgramFiles%\Claude\Claude.exe",
            },
            new[] { "claude.exe", "Claude.exe", "Claude Code.exe" },
            "Claude_pzs8sxrjxfjjc",
            @"app\claude.exe"),
        ["codex"] = new(
            "ChatGPT",
            "codex_app_path",
            new[]
            {
                @"%ProgramFiles%\WindowsApps\OpenAI.Codex_*\app\ChatGPT.exe",
                @"%ProgramFiles%\WindowsApps\OpenAI.ChatGPT_*\ChatGPT.exe",
                @"%LOCALAPPDATA%\Programs\ChatGPT\ChatGPT.exe",
                @"%LOCALAPPDATA%\Programs\Codex\Codex.exe",
            },
            new[] { "ChatGPT.exe", "Codex.exe" },
            "OpenAI.Codex_2p2nqsd0c76g0",
            @"app\ChatGPT.exe"),
        ["antigravity"] = new(
            "Antigravity",
            "antigravity_path",
            AntigravityAppPaths,
            AntigravityExecutableNames,
            AntigravityDisplayNames),
        ["kiro"] = new(
            "Kiro",
            "kiro_app_path",
            new[]
            {
                @"%LOCALAPPDATA%\Programs\Kiro\Kiro.exe",
                @"%ProgramFiles%\Kiro\Kiro.exe",
                @"%ProgramFiles(x86)%\Kiro\Kiro.exe",
            },
            new[] { "Kiro.exe" }),
        ["qoder"] = new(
            "Qoder",
            "qoder_app_path",
            new[]
            {
                @"%ProgramFiles%\QoderWork\QoderWork",
                @"%ProgramFiles%\QoderWork\QoderWork\QoderWork.exe",
                @"%ProgramFiles%\Qoder\Qoder.exe",
                @"%LOCALAPPDATA%\Programs\QoderWork\QoderWork.exe",
                @"%LOCALAPPDATA%\Programs\Qoder\Qoder.exe",
            },
            new[] { "QoderWork.exe", "Qoder.exe" }),
        ["kimi"] = new(
            "Kimi",
            "kimi_app_path",
            new[]
            {
                @"%LOCALAPPDATA%\Programs\kimi-desktop\Kimi.exe",
                @"%LOCALAPPDATA%\Programs\Kimi.work\Kimi.exe",
                @"%LOCALAPPDATA%\Programs\Kimi\Kimi.exe",
                @"%ProgramFiles%\kimi-desktop\Kimi.exe",
                @"%ProgramFiles%\Kimi.work\Kimi.exe",
            },
            new[] { "Kimi.exe", "kimi.exe" }),
        ["gemini"] = new(
            "Antigravity",
            "gemini_app_path",
            AntigravityAppPaths,
            AntigravityExecutableNames,
            AntigravityDisplayNames),
        ["openai"] = new(
            "ChatGPT",
            "openai_app_path",
            new[]
            {
                @"%ProgramFiles%\WindowsApps\OpenAI.ChatGPT_*\ChatGPT.exe",
                @"%LOCALAPPDATA%\Programs\ChatGPT\ChatGPT.exe",
            },
            new[] { "ChatGPT.exe" }),
        ["moonshot"] = new(
            "Kimi",
            "moonshot_app_path",
            new[]
            {
                @"%LOCALAPPDATA%\Programs\Kimi\Kimi.exe",
                @"%ProgramFiles%\Kimi\Kimi.exe",
            },
            new[] { "Kimi.exe" }),
        ["zcode"] = new(
            "ZCode",
            "zcode_app_path",
            new[]
            {
                @"%LOCALAPPDATA%\Programs\ZCode\ZCode.exe",
                @"%ProgramFiles%\ZCode\ZCode.exe",
            },
            new[] { "ZCode.exe" }),
        ["alibaba"] = new(
            "Qwen Chat",
            "alibaba_app_path",
            new[]
            {
                @"%LOCALAPPDATA%\Programs\Qwen\QwenChat.exe",
                @"%ProgramFiles%\Qwen\QwenChat.exe",
            },
            new[] { "QwenChat.exe" }),
        ["cursor"] = new(
            "Cursor",
            "cursor_app_path",
            new[]
            {
                @"%LOCALAPPDATA%\Programs\Cursor\Cursor.exe",
                @"%ProgramFiles%\Cursor\Cursor.exe",
            },
            new[] { "Cursor.exe" }),
        ["windsurf"] = new(
            "Windsurf",
            "windsurf_app_path",
            new[]
            {
                @"%LOCALAPPDATA%\Programs\Windsurf\Windsurf.exe",
                @"%ProgramFiles%\Windsurf\Windsurf.exe",
            },
            new[] { "Windsurf.exe" }),
        ["warp"] = new(
            "Warp",
            "warp_app_path",
            new[]
            {
                @"%LOCALAPPDATA%\Programs\Warp\Warp.exe",
                @"%ProgramFiles%\Warp\Warp.exe",
            },
            new[] { "Warp.exe" }),
        ["copilot"] = new(
            "GitHub Copilot",
            "copilot_app_path",
            new[]
            {
                @"%LOCALAPPDATA%\Programs\GitHub Copilot\GitHub Copilot.exe",
                @"%ProgramFiles%\GitHub Copilot\GitHub Copilot.exe",
            },
            new[] { "GitHub Copilot.exe" }),
        ["factory"] = new(
            "Factory",
            "factory_app_path",
            new[] { @"%LOCALAPPDATA%\Programs\Factory\Factory.exe" },
            new[] { "Factory.exe" }),
        ["perplexity"] = new(
            "Perplexity",
            "perplexity_app_path",
            new[] { @"%LOCALAPPDATA%\Programs\Perplexity\Perplexity.exe" },
            new[] { "Perplexity.exe" }),
        ["ollama"] = new(
            "Ollama",
            "ollama_app_path",
            new[] { @"%LOCALAPPDATA%\Programs\Ollama" },
            new[] { "ollama app.exe" }),
        ["doubao"] = new(
            "豆包 Doubao",
            "doubao_app_path",
            new[] { @"%LOCALAPPDATA%\Doubao\Doubao.exe" },
            new[] { "Doubao.exe" }),
    };

    public static readonly IReadOnlyDictionary<string, ProviderLocalSetupProbe> LocalSetupProbes =
        new Dictionary<string, ProviderLocalSetupProbe>
        {
            ["qoder"] = new(
                "qoder_cli_path",
                "Qoder CLI",
                new[]
                {
                    @"%ProgramFiles%\QoderWork\QoderWork\resources\bin\qodercli.exe",
                    @"%LOCALAPPDATA%\Programs\QoderWork\resources\bin\qodercli.exe",
                },
                new[] { "QODER_CLI_PATH" },
                new[] { "qodercli.exe", "qodercli" }),
            ["kiro"] = new(
                "kiro_cli_path",
                "Kiro CLI",
                new[]
                {
                    @"%LOCALAPPDATA%\Kiro-Cli\kiro-cli.exe",
                    @"%LOCALAPPDATA%\Programs\Kiro\resources\bin\kiro-cli.exe",
                    @"%ProgramFiles%\Kiro\resources\bin\kiro-cli.exe",
                },
                new[] { "KIRO_CLI_PATH" },
                new[] { "kiro-cli.exe", "kiro-cli" }),
            ["doubao"] = new(
                "doubao_cli_path",
                "arkcli",
                Array.Empty<string>(),
                new[] { "ARKCLI_PATH", "DOUBAO_ARKCLI_PATH" },
                new[] { "arkcli.exe", "arkcli" }),
            ["grok"] = new(
                new[] { "grok_path" },
                "Grok CLI",
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { "GROK_CLI_PATH" },
                new[] { "grok.exe", "grok" }),
            ["kilo"] = new(
                new[] { "kilo_key", "kilo_auth_path" },
                "Kilo credentials",
                new[] { @"%USERPROFILE%\.local\share\kilo\auth.json" },
                Array.Empty<string>(),
                new[] { "KILO_API_KEY", "KILO_AUTH_PATH" },
                Array.Empty<string>()),
            ["jetbrains"] = new(
                new[] { "jetbrains_base_path" },
                "JetBrains AI quota file",
                new[]
                {
                    @"%APPDATA%\JetBrains\*\options\AIAssistantQuotaManager2.xml",
                    @"%LOCALAPPDATA%\JetBrains\*\options\AIAssistantQuotaManager2.xml",
                    @"%APPDATA%\Google\AndroidStudio*\options\AIAssistantQuotaManager2.xml",
                    @"%LOCALAPPDATA%\Google\AndroidStudio*\options\AIAssistantQuotaManager2.xml",
                },
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()),
            ["codex"] = new(
                "Codex auth",
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyScopedConfigFilePath(
                        new[] { "codex_home" },
                        @"{value}\auth.json")),
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyEnvironmentFilePath(
                        new[] { "CODEX_HOME" },
                        @"{value}\auth.json")),
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyFilePath(
                        @"%USERPROFILE%\.codex\auth.json"))),
            ["gemini"] = new(
                "Gemini CLI OAuth",
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyScopedConfigFilePath(
                        new[] { "gemini_home" },
                        @"{value}\oauth_creds.json",
                        @"{value}\.gemini\oauth_creds.json")),
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyEnvironmentFilePath(
                        new[] { "GEMINI_HOME" },
                        @"{value}\oauth_creds.json",
                        @"{value}\.gemini\oauth_creds.json")),
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyFilePath(
                        @"%USERPROFILE%\.gemini\oauth_creds.json"))),
            ["bedrock"] = new(
                "AWS credentials",
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AllScopedConfig(
                        "bedrock_access_key_id",
                        "bedrock_secret_access_key")),
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AllEnvironment(
                        "AWS_ACCESS_KEY_ID",
                        "AWS_SECRET_ACCESS_KEY")),
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyScopedConfig("bedrock_profile"),
                    ProviderLocalSetupRequirement.AnyScopedConfig("bedrock_aws_cli_path")),
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyScopedConfig("bedrock_profile"),
                    ProviderLocalSetupRequirement.AnyEnvironment("AWS_CLI_PATH")),
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyScopedConfig("bedrock_profile"),
                    ProviderLocalSetupRequirement.AnyPathExecutable("aws.exe", "aws")),
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyEnvironment("AWS_PROFILE"),
                    ProviderLocalSetupRequirement.AnyScopedConfig("bedrock_aws_cli_path")),
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyEnvironment("AWS_PROFILE"),
                    ProviderLocalSetupRequirement.AnyEnvironment("AWS_CLI_PATH")),
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyEnvironment("AWS_PROFILE"),
                    ProviderLocalSetupRequirement.AnyPathExecutable("aws.exe", "aws"))),
            ["vertexai"] = new(
                "gcloud Application Default Credentials",
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyScopedConfigFilePath(
                        new[] { "vertexai_credentials_path" })),
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyEnvironmentFilePath(
                        new[] { "GOOGLE_APPLICATION_CREDENTIALS" })),
                new ProviderLocalSetupSource(
                    ProviderLocalSetupRequirement.AnyFilePath(
                        @"%APPDATA%\gcloud\application_default_credentials.json",
                        @"%USERPROFILE%\.config\gcloud\application_default_credentials.json"))),
        };

    public static ProviderLaunchTarget? LaunchTargetFor(string providerType, IConfig config)
    {
        if (LaunchTargets.TryGetValue(providerType, out var target))
            return target;

        return string.IsNullOrWhiteSpace(config.Get(DefaultLaunchEditorPathKey))
            ? null
            : new ProviderLaunchTarget(
                "Default editor",
                DefaultLaunchEditorPathKey,
                Array.Empty<string>());
    }

    public static string ProviderTypeFromId(string id)
    {
        var exact = Types.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact.Id;

        var prefixed = Types
            .OrderByDescending(t => t.Id.Length)
            .FirstOrDefault(t => id.StartsWith(t.Id + "-", StringComparison.OrdinalIgnoreCase));
        if (prefixed is not null)
            return prefixed.Id;

        return id.Split('-')[0];
    }

    public static ProviderType? FindType(string providerType) =>
        Types.FirstOrDefault(t => string.Equals(t.Id, providerType, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnownProviderType(string providerType) =>
        FindType(providerType) is not null;

    public static string ProviderTypeForInstance(string instanceId, IConfig? config)
    {
        if (config is IConfigService configService)
        {
            var instance = configService.Instances.FirstOrDefault(i =>
                string.Equals(i.Id, instanceId, StringComparison.OrdinalIgnoreCase));
            if (instance is not null && !string.IsNullOrWhiteSpace(instance.Type))
                return instance.Type;
        }

        return ProviderTypeFromId(instanceId);
    }

    public static string ProviderName(string id)
    {
        var t = Types.FirstOrDefault(x => x.Id == id);
        if (t != null) return t.Name;
        var byType = Types.FirstOrDefault(x => x.Id == ProviderTypeFromId(id));
        return byType?.Name ?? id;
    }

    public static string? DefaultLoginUrlFor(string providerType)
    {
        var urlKey = $"{providerType}_url";
        return Fields.TryGetValue(providerType, out var fields)
            ? fields.FirstOrDefault(field => field.Key == urlKey)?.Placeholder
            : null;
    }

    public static bool IsProviderUnconfigured(string providerId, IConfig config)
    {
        var type = ProviderTypeForInstance(providerId, config);
        if (!RequiredFieldSets.TryGetValue(type, out var required) || required.Length == 0) return false;
        return required.Any(set => !set.AnyOf.Any(key => !string.IsNullOrWhiteSpace(config.GetScoped(providerId, key))));
    }

    public static bool RequiresUserConfiguration(string providerType)
    {
        var normalized = FindType(providerType)?.Id ?? providerType;
        return RequiredFieldSets.TryGetValue(normalized, out var required) && required.Length > 0;
    }

    public static ProviderSetupKind SetupKindFor(string providerType)
    {
        var normalized = FindType(providerType)?.Id ?? providerType;

        if (RequiresUserConfiguration(normalized))
            return ProviderSetupKind.ApiKey;

        if (HasBrowserLoginField(normalized))
            return ProviderSetupKind.BrowserLogin;

        if (LaunchTargets.ContainsKey(normalized)
            || (Fields.TryGetValue(normalized, out var fields) && fields.Any(field => field.IsFilePath)))
        {
            return ProviderSetupKind.LocalAppOrCli;
        }

        return ProviderSetupKind.Ready;
    }

    private static bool HasBrowserLoginField(string providerType) =>
        Fields.TryGetValue(providerType, out var fields)
        && fields.Any(field => string.Equals(field.Key, providerType + "_url", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, ProviderRequiredFieldSet[]> BuildRequiredFieldSets() =>
        Fields
            .Select(pair => new
            {
                ProviderType = pair.Key,
                FieldSets = pair.Value
                    .Where(field => field.IsRequired)
                    .Select(field => new ProviderRequiredFieldSet(field.Key))
                    .ToArray(),
            })
            .Where(provider => provider.FieldSets.Length > 0)
            .ToDictionary(
                provider => provider.ProviderType,
                provider => provider.FieldSets,
                StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> BuildDefaultConfig()
    {
        var defaults = new Dictionary<string, string>(GlobalDefaultConfig, StringComparer.OrdinalIgnoreCase);

        foreach (var field in Fields.Values.SelectMany(fields => fields))
            defaults.TryAdd(field.Key, "");

        foreach (var (key, value) in FieldDefaultOverrides)
            defaults[key] = value;

        return defaults;
    }

    private static IReadOnlySet<string> BuildPayAsYouGoProviderTypes() =>
        DefaultPlanValueRules
            .Where(pair => !RetiredProviderTypes.Contains(pair.Key)
                && pair.Value.Any(rule => rule.Value < 0))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> BuildSubscriptionProviderTypes() =>
        DefaultPlanValueRules
            .Where(pair => !RetiredProviderTypes.Contains(pair.Key)
                && pair.Value.All(rule => rule.Value >= 0))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
