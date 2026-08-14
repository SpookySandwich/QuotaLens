using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>
/// Gemini CLI quota provider. This ports CodexBar's Gemini probe: reuse the
/// Gemini CLI OAuth credential file, call Cloud Code's quota endpoint, and group
/// model buckets into Pro / Flash / Flash Lite usage windows.
/// </summary>
public sealed partial class GeminiProvider : IProvider
{
    private const int GeminiRefreshTimeoutSeconds = 30;
    private const string QuotaEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    private const string LoadCodeAssistEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string ProjectsEndpoint = "https://cloudresourcemanager.googleapis.com/v1/projects";
    private static readonly DateTimeOffset ConsumerOAuthRetirement =
        new(2026, 6, 18, 0, 0, 0, TimeSpan.Zero);

    private readonly IReadOnlyList<IProviderSource> _sources;

    public GeminiProvider()
    {
        _sources = new IProviderSource[]
        {
            new GeminiCliSource(this),
            new AntigravityIdeSource(),
        };
    }

    public string Type => "gemini";
    public string Name => "Gemini";
    public string SourceLabel => "Gemini OAuth";
    public Confidence Confidence => Confidence.SemiOfficial;
    public IReadOnlyList<IProviderSource> Sources => _sources;

    public Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct) =>
        ProviderSourceRunner.FetchAsync(this, _sources, instanceId, config, ct);

    private async Task<ProviderSnapshot> FetchCliAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var geminiDir = ResolveGeminiDirectory(instanceId, config);
        var authType = CurrentAuthType(geminiDir);
        if (authType.Equals("api-key", StringComparison.OrdinalIgnoreCase))
            throw new ProviderException("Not available: Gemini API-key auth does not expose CLI quota usage. Sign in to Gemini CLI with Google OAuth.");
        if (authType.Equals("vertex-ai", StringComparison.OrdinalIgnoreCase))
            throw new ProviderException(
                "Not available: Gemini Vertex AI auth is tracked separately from Gemini CLI quota.",
                ProviderErrorKind.Unsupported);

        var credentials = await LoadCredentialsAsync(geminiDir, ct).ConfigureAwait(false);
        var accessToken = credentials.AccessToken;
        var needsReauthentication = string.IsNullOrWhiteSpace(accessToken)
            || credentials.Expiry is not null && credentials.Expiry <= DateTimeOffset.UtcNow.AddMinutes(1);
        if (needsReauthentication && credentials.HasStoredSession)
        {
            // The CLI refreshes its own token on startup. Measured: `gemini
            // --list-extensions` rotates an expired access token even though the command
            // then fails for a personal account, so the refresh must be judged by the
            // credential changing, never by the exit code.
            if (await RefreshViaCliAsync(instanceId, config, geminiDir, ct).ConfigureAwait(false))
            {
                credentials = await LoadCredentialsAsync(geminiDir, ct).ConfigureAwait(false);
                accessToken = credentials.AccessToken;
                needsReauthentication = string.IsNullOrWhiteSpace(accessToken)
                    || credentials.Expiry is not null && credentials.Expiry <= DateTimeOffset.UtcNow.AddMinutes(1);
            }
        }

        if (needsReauthentication)
            throw new ProviderException("Login required: Gemini CLI OAuth credentials are expired. Run `gemini` to sign in again.");

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ProviderException("Login required: Gemini CLI OAuth access token was not found.");

        var claims = ExtractClaims(credentials.IdToken);
        var codeAssist = await LoadCodeAssistStatusAsync(accessToken!, ct).ConfigureAwait(false);
        if (IsRetiredConsumerTier(codeAssist.TierId, codeAssist.TierName, claims.HostedDomain, DateTimeOffset.UtcNow))
        {
            throw new ProviderException(
                "Not available: Gemini CLI consumer OAuth for Individual, Google AI Pro, and Google AI Ultra " +
                "ended on June 18, 2026. Use Antigravity for a personal account; Workspace Standard and " +
                "Enterprise remain supported.",
                ProviderErrorKind.Unsupported);
        }

        var projectId = codeAssist.ProjectId ?? await DiscoverProjectIdAsync(accessToken!, ct).ConfigureAwait(false);
        var quotaJson = await FetchQuotaJsonAsync(accessToken!, projectId, ct).ConfigureAwait(false);
        var usage = ParseQuotaResponse(quotaJson, claims.Email);
        return Snapshot(usage with { AccountPlan = PlanName(codeAssist, claims.HostedDomain) });
    }

    internal static GeminiUsage ParseQuotaResponse(string json, string? email = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                throw new ProviderException("Parse error: Gemini quota response did not include buckets");

            var byModel = new Dictionary<string, GeminiModelQuota>(StringComparer.OrdinalIgnoreCase);
            foreach (var bucket in buckets.EnumerateArray())
            {
                var model = StringValue(bucket, "modelId", "model_id", "model");
                var remainingFraction = DoubleValue(bucket, "remainingFraction", "remaining_fraction");
                if (string.IsNullOrWhiteSpace(model) || remainingFraction is null)
                    continue;

                var quota = new GeminiModelQuota(
                    model,
                    Quota.ClampPercent(remainingFraction.Value * 100),
                    IsoValue(bucket, "resetTime", "reset_time"),
                    ResetDescription(IsoValue(bucket, "resetTime", "reset_time")));
                if (!byModel.TryGetValue(model, out var existing) || quota.PercentLeft < existing.PercentLeft)
                    byModel[model] = quota;
            }

            if (byModel.Count == 0)
                throw new ProviderException("Parse error: Gemini quota response did not include model quota buckets");

            return new GeminiUsage(byModel.Values.OrderBy(quota => quota.ModelId, StringComparer.OrdinalIgnoreCase).ToArray(), email, null);
        }
        catch (JsonException e)
        {
            throw new ProviderException($"Parse error: Invalid Gemini quota JSON: {e.Message}", e);
        }
    }

    internal static ProviderSnapshot Snapshot(GeminiUsage usage, DateTimeOffset? updatedAt = null)
    {
        var lower = usage.Quotas.Select(quota => (Model: quota.ModelId.ToLowerInvariant(), Quota: quota)).ToArray();
        var pro = lower.Where(item => item.Model.Contains("pro", StringComparison.Ordinal)).Select(item => item.Quota).MinBy(item => item.PercentLeft);
        var flashLite = lower.Where(item => item.Model.Contains("flash-lite", StringComparison.Ordinal)).Select(item => item.Quota).MinBy(item => item.PercentLeft);
        var flash = lower.Where(item => item.Model.Contains("flash", StringComparison.Ordinal) && !item.Model.Contains("flash-lite", StringComparison.Ordinal)).Select(item => item.Quota).MinBy(item => item.PercentLeft);
        var fallback = usage.Quotas.MinBy(item => item.PercentLeft);

        var primary = ToWindow("Pro", pro ?? fallback)!;
        var secondary = ToWindow("Flash", flash);
        var tertiary = ToWindow("Flash Lite", flashLite);
        var plan = string.IsNullOrWhiteSpace(usage.AccountPlan) ? "" : $" · {usage.AccountPlan}";

        return new ProviderSnapshot
        {
            ProviderId = "gemini",
            Name = $"Gemini{plan}",
            PlanName = usage.AccountPlan,
            Primary = primary,
            Secondary = secondary,
            Tertiary = tertiary,
            Accounts = string.IsNullOrWhiteSpace(usage.AccountEmail) && string.IsNullOrWhiteSpace(usage.AccountPlan)
                ? new List<AccountInfo>()
                : new List<AccountInfo>
                {
                    new()
                    {
                        Email = usage.AccountEmail,
                        Plan = usage.AccountPlan,
                    },
                },
            ModelQuotas = usage.Quotas.Select(quota => new ModelQuota
            {
                Model = quota.ModelId,
                Family = GeminiFamily(quota.ModelId),
                FamilyKind = ModelQuotaFamilyKind.Gemini,
                WindowType = "Daily",
                RemainingPercent = quota.PercentLeft,
                UsedPercent = Quota.ClampPercent(100 - quota.PercentLeft),
                ResetsAt = quota.ResetsAt,
            }).ToList(),
            SourceLabel = "Gemini OAuth",
            Confidence = Confidence.SemiOfficial,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
        };
    }

    private static RateWindow? ToWindow(string label, GeminiModelQuota? quota)
    {
        if (quota is null)
            return null;

        return new RateWindow
        {
            Label = label,
            UsedPercent = Quota.ClampPercent(100 - quota.PercentLeft),
            ResetsAt = quota.ResetsAt,
            ResetDescription = quota.ResetDescription,
            WindowMinutes = 24 * 60,
        };
    }

    private static string GeminiFamily(string modelId)
    {
        var lower = modelId.ToLowerInvariant();
        if (lower.Contains("flash-lite", StringComparison.Ordinal)) return "Flash Lite";
        if (lower.Contains("flash", StringComparison.Ordinal)) return "Flash";
        if (lower.Contains("pro", StringComparison.Ordinal)) return "Pro";
        return "Gemini";
    }

    private static async Task<string> FetchQuotaJsonAsync(string accessToken, string? projectId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, QuotaEndpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        request.Content = new StringContent(
            string.IsNullOrWhiteSpace(projectId) ? "{}" : $$"""{"project":"{{JsonEscape(projectId!)}}"}""",
            Encoding.UTF8,
            "application/json");
        using var response = await Core.Http.Client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if ((int)response.StatusCode is 401 or 403)
            throw new ProviderException("Login required: Gemini OAuth token was rejected.");
        if (!response.IsSuccessStatusCode)
            throw new ProviderException($"Network error: Gemini quota API HTTP {(int)response.StatusCode}: {ProviderConfig.ResponseSummary(body)}");
        return body;
    }

    private static async Task<CodeAssistStatus> LoadCodeAssistStatusAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, LoadCodeAssistEndpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            request.Content = new StringContent("""{"metadata":{"ideType":"GEMINI_CLI","pluginType":"GEMINI"}}""", Encoding.UTF8, "application/json");
            using var response = await Core.Http.Client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return CodeAssistStatus.Empty;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseCodeAssistStatus(json);
        }
        catch
        {
            return CodeAssistStatus.Empty;
        }
    }

    internal static CodeAssistStatus ParseCodeAssistStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var project = StringValue(root, "cloudaicompanionProject");
        if (string.IsNullOrWhiteSpace(project)
            && root.TryGetProperty("cloudaicompanionProject", out var projectObj)
            && projectObj.ValueKind == JsonValueKind.Object)
        {
            project = StringValue(projectObj, "id", "projectId");
        }

        JsonElement tier = default;
        if (!(root.TryGetProperty("paidTier", out tier) && tier.ValueKind == JsonValueKind.Object)
            && !(root.TryGetProperty("currentTier", out tier) && tier.ValueKind == JsonValueKind.Object))
        {
            tier = default;
        }

        return new CodeAssistStatus(
            Clean(project),
            tier.ValueKind == JsonValueKind.Object ? Clean(StringValue(tier, "id")) : null,
            tier.ValueKind == JsonValueKind.Object ? Clean(StringValue(tier, "name")) : null);
    }

    private static async Task<string?> DiscoverProjectIdAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProjectsEndpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            using var response = await Core.Http.Client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var project in projects.EnumerateArray())
            {
                var projectId = StringValue(project, "projectId");
                if (string.IsNullOrWhiteSpace(projectId))
                    continue;
                if (projectId.StartsWith("gen-lang-client", StringComparison.OrdinalIgnoreCase))
                    return projectId;
                if (project.TryGetProperty("labels", out var labels)
                    && labels.ValueKind == JsonValueKind.Object
                    && labels.TryGetProperty("generative-language", out _))
                {
                    return projectId;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Asks the Gemini CLI to refresh its own credential file. `--list-extensions` sends
    /// no prompt and exits, so it costs no quota; NO_BROWSER stops it opening a browser
    /// if it decides interaction is needed.
    /// </summary>
    private static async Task<bool> RefreshViaCliAsync(
        string instanceId,
        IConfig config,
        string geminiDir,
        CancellationToken ct)
    {
        var configured = config.GetScoped(instanceId, "gemini_path");
        var binary = string.IsNullOrWhiteSpace(configured)
            ? "gemini"
            : Environment.ExpandEnvironmentVariables(configured);

        return await CliTokenRefresher.TryRefreshAsync(
            binary,
            CliRefreshCommands.Gemini,
            TimeSpan.FromSeconds(GeminiRefreshTimeoutSeconds),
            () => ReadAccessTokenFingerprint(geminiDir),
            ct,
            environment: new Dictionary<string, string> { ["NO_BROWSER"] = "true" }).ConfigureAwait(false);
    }

    private static string? ReadAccessTokenFingerprint(string geminiDir)
    {
        try
        {
            var path = Path.Combine(geminiDir, "oauth_creds.json");
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return StringValue(doc.RootElement, "access_token");
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveGeminiDirectory(string instanceId, IConfig config)
    {
        var configured = ProviderConfig.Resolve(instanceId, config, "gemini", "gemini_home");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured!);
            return Path.GetFileName(expanded).Equals(".gemini", StringComparison.OrdinalIgnoreCase)
                ? expanded
                : Path.Combine(expanded, ".gemini");
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".gemini");
    }

    private static async Task<OAuthCredentials> LoadCredentialsAsync(string geminiDir, CancellationToken ct)
    {
        var path = Path.Combine(geminiDir, "oauth_creds.json");
        if (!File.Exists(path))
            throw new ProviderException("Login required: Gemini CLI OAuth credentials were not found. Run `gemini` and sign in with Google.");

        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            var root = doc.RootElement;
            return new OAuthCredentials(
                StringValue(root, "access_token"),
                StringValue(root, "id_token"),
                Expiry(root),
                // Presence only — never the value, and never used to mint a token
                // ourselves. Present means "signed in, token merely aged out".
                !string.IsNullOrWhiteSpace(StringValue(root, "refresh_token")));
        }
        catch (JsonException e)
        {
            throw new ProviderException($"Parse error: Invalid Gemini OAuth credentials JSON: {e.Message}", e);
        }
    }

    private static string CurrentAuthType(string geminiDir)
    {
        var path = Path.Combine(geminiDir, "settings.json");
        if (!File.Exists(path))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var auth = doc.RootElement.TryGetProperty("security", out var security)
                && security.ValueKind == JsonValueKind.Object
                && security.TryGetProperty("auth", out var authObj)
                && authObj.ValueKind == JsonValueKind.Object
                ? authObj
                : default;
            return auth.ValueKind == JsonValueKind.Object ? StringValue(auth, "selectedType") ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    private static TokenClaims ExtractClaims(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
            return new TokenClaims(null, null);
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return new TokenClaims(null, null);
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return new TokenClaims(StringValue(doc.RootElement, "email"), StringValue(doc.RootElement, "hd"));
        }
        catch
        {
            return new TokenClaims(null, null);
        }
    }

    private static DateTimeOffset? Expiry(JsonElement root)
    {
        var value = DoubleValue(root, "expiry_date");
        if (value is not > 0)
            return null;
        var milliseconds = value.Value > 10_000_000_000 ? value.Value : value.Value * 1000.0;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(milliseconds));
    }

    internal static bool IsRetiredConsumerTier(
        string? tierId,
        string? tierName,
        string? hostedDomain,
        DateTimeOffset now)
    {
        if (now < ConsumerOAuthRetirement || !string.IsNullOrWhiteSpace(hostedDomain))
            return false;

        if (string.Equals(tierId, "standard-tier", StringComparison.OrdinalIgnoreCase)
            || tierName?.Contains("standard", StringComparison.OrdinalIgnoreCase) == true
            || tierName?.Contains("enterprise", StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }

        return string.Equals(tierId, "free-tier", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tierId, "legacy-tier", StringComparison.OrdinalIgnoreCase)
            || tierName?.Contains("individual", StringComparison.OrdinalIgnoreCase) == true
            || tierName?.Contains("ai pro", StringComparison.OrdinalIgnoreCase) == true
            || tierName?.Contains("ai ultra", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? PlanName(CodeAssistStatus status, string? hostedDomain)
    {
        if (!string.IsNullOrWhiteSpace(status.TierName))
            return status.TierName;

        return status.TierId switch
        {
            "standard-tier" => "Standard",
            "free-tier" when !string.IsNullOrWhiteSpace(hostedDomain) => "Workspace",
            "free-tier" => "Free",
            "legacy-tier" => "Legacy",
            _ => null,
        };
    }

    private static string? StringValue(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var value))
                continue;
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return null;
    }

    private static double? DoubleValue(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var value))
                continue;
            var number = value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetDouble(out var parsed) => parsed,
                JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => (double?)null,
            };
            if (number is not null)
                return number;
        }

        return null;
    }

    private static string? IsoValue(JsonElement obj, params string[] keys)
    {
        var raw = StringValue(obj, keys);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToString("O", CultureInfo.InvariantCulture)
            : null;
    }

    private static string? ResetDescription(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)
            || !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var reset))
        {
            return null;
        }

        var interval = reset - DateTimeOffset.UtcNow;
        if (interval <= TimeSpan.Zero)
            return "Resets soon";
        return interval.TotalHours >= 1
            ? $"Resets in {(int)interval.TotalHours}h {interval.Minutes}m"
            : $"Resets in {Math.Max(0, interval.Minutes)}m";
    }

    private static string? Clean(string? value) => ProviderConfig.Clean(value);

    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    internal sealed record GeminiUsage(IReadOnlyList<GeminiModelQuota> Quotas, string? AccountEmail, string? AccountPlan);
    internal sealed record GeminiModelQuota(string ModelId, double PercentLeft, string? ResetsAt, string? ResetDescription);
    private sealed record OAuthCredentials(
        string? AccessToken,
        string? IdToken,
        DateTimeOffset? Expiry,
        bool HasStoredSession = false);
    private sealed record TokenClaims(string? Email, string? HostedDomain);
    internal sealed record CodeAssistStatus(string? ProjectId, string? TierId, string? TierName)
    {
        public static CodeAssistStatus Empty { get; } = new(null, null, null);
    }

    // ---- sources -------------------------------------------------------------

    private bool CliCredentialsExist(string instanceId, IConfig config)
    {
        try
        {
            var dir = ResolveGeminiDirectory(instanceId, config);
            return File.Exists(Path.Combine(dir, "oauth_creds.json"));
        }
        catch
        {
            return false;
        }
    }

    private sealed class GeminiCliSource : IProviderSource
    {
        private readonly GeminiProvider _owner;

        public GeminiCliSource(GeminiProvider owner) => _owner = owner;

        public string Id => "cli";
        public string Name => "Gemini CLI";
        public IReadOnlyList<string> ConfigFieldKeys => new[] { "gemini_home", "gemini_path" };
        public bool IsAvailable(string instanceId, IConfig config) => _owner.CliCredentialsExist(instanceId, config);
        public Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct) =>
            _owner.FetchCliAsync(instanceId, config, ct);
    }

    private sealed class AntigravityIdeSource : IProviderSource
    {
        private static readonly AntigravityProvider Provider = new();

        public string Id => "ide";
        public string Name => "Antigravity IDE";
        public IReadOnlyList<string> ConfigFieldKeys => Array.Empty<string>();
        public bool IsAvailable(string instanceId, IConfig config) => AntigravityProvider.IsRunning();
        public Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct) =>
            Provider.FetchAsync(instanceId, config, ct);
    }

}
