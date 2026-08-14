using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaLens.Core;
using static QuotaLens.Core.JsonUtil;

namespace QuotaLens.Providers;

/// <summary>
/// Claude Code (Anthropic OAuth) provider. Reads the Claude CLI OAuth credentials
/// file, then performs the read-only GET https://api.anthropic.com/api/oauth/usage
/// request. The endpoint is Anthropic-hosted, while its response fields are treated
/// as an upstream-compatibility schema because Anthropic does not document them.
///
/// STALE TOKENS: Claude Code refreshes its cached token lazily, so the credentials
/// file is routinely hours or days out of date while the session is perfectly healthy.
/// When the usage API rejects the cached token, this asks the CLI to refresh its OWN
/// file by running a command that initializes auth but sends no prompt
/// (<c>claude mcp list</c>), then re-reads and retries once. Measured: this moved a
/// 43-hour-expired token to 8 hours of validity and the usage call went 401 -> 200.
///
/// This is NOT the old print-mode refresh, which sent a real prompt and therefore spent
/// the very quota being measured. No prompt is sent and no quota is consumed, and the
/// read-only contract holds: QuotaLens still never writes the credentials file — the
/// CLI writes its own.
/// </summary>
public sealed class ClaudeProvider : IProvider
{
    /// <summary>
    /// A CLI command that initializes the auth stack (refreshing an expired token as a
    /// side effect) while sending no prompt. Verified against the live CLI; `--version`
    /// and `auth status` do NOT refresh, so this specific command matters. The argv
    /// itself lives in <see cref="CliRefreshCommands.Claude"/>.
    /// </summary>
    internal static IReadOnlyList<string> RefreshCommandArgumentsForTesting => CliRefreshCommands.Claude;

    private const int RefreshTimeoutSeconds = 45;

    private readonly Func<ClaudeOAuth?> _readToken;
    private readonly Func<string, CancellationToken, Task<HttpResponseMessage>> _sendUsageAsync;
    private readonly Func<string, IConfig, CancellationToken, Task<bool>> _refreshViaCliAsync;

    public ClaudeProvider()
        : this(ReadToken, SendUsageAsync, RefreshViaCliAsync)
    {
    }

    internal ClaudeProvider(
        Func<ClaudeOAuth?> readToken,
        Func<string, CancellationToken, Task<HttpResponseMessage>> sendUsageAsync,
        Func<string, IConfig, CancellationToken, Task<bool>>? refreshViaCliAsync = null)
    {
        _readToken = readToken;
        _sendUsageAsync = sendUsageAsync;
        _refreshViaCliAsync = refreshViaCliAsync ?? ((_, _, _) => Task.FromResult(false));
    }

    public string Type => "claude";
    public string Name => "Claude Code";
    public string SourceLabel => "Anthropic OAuth API";
    public Confidence Confidence => Confidence.SemiOfficial;

    private sealed class ClaudeCredentials
    {
        [JsonPropertyName("claudeAiOauth")] public ClaudeOAuth? ClaudeAiOauth { get; set; }
    }

    internal sealed class ClaudeOAuth
    {
        [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("subscriptionType")] public string? SubscriptionType { get; set; }
        // Read by the Rust struct but unused in the data path (tier scaling was removed —
        // see commit "remove tier_max division — utilization IS the percentage directly").
        [JsonPropertyName("rateLimitTier")] public string? RateLimitTier { get; set; }

        /// <summary>
        /// The long-lived session credential the CLI owns. QuotaLens reads its PRESENCE
        /// only — never its value, and never to mint a token. Present means "signed in,
        /// token merely aged out"; absent means genuinely signed out.
        /// </summary>
        [JsonPropertyName("refreshToken")] public string? SessionCredential { get; set; }

        public bool HasStoredSession => !string.IsNullOrWhiteSpace(SessionCredential);
    }

    private sealed class OAuthUsageResponse
    {
        [JsonPropertyName("five_hour")] public UsageWindowData? FiveHour { get; set; }
        [JsonPropertyName("seven_day")] public UsageWindowData? SevenDay { get; set; }
        [JsonPropertyName("seven_day_opus")] public UsageWindowData? SevenDayOpus { get; set; }
        [JsonPropertyName("seven_day_sonnet")] public UsageWindowData? SevenDaySonnet { get; set; }
        [JsonPropertyName("seven_day_routines")] public UsageWindowData? SevenDayRoutines { get; set; }
        [JsonPropertyName("seven_day_claude_routines")] public UsageWindowData? SevenDayClaudeRoutines { get; set; }
        [JsonPropertyName("claude_routines")] public UsageWindowData? ClaudeRoutines { get; set; }
        [JsonPropertyName("routines")] public UsageWindowData? Routines { get; set; }
        [JsonPropertyName("routine")] public UsageWindowData? Routine { get; set; }
        [JsonPropertyName("seven_day_cowork")] public UsageWindowData? SevenDayCowork { get; set; }
        [JsonPropertyName("cowork")] public UsageWindowData? Cowork { get; set; }
    }

    private sealed class UsageWindowData
    {
        [JsonPropertyName("utilization")] public double? Utilization { get; set; }
        [JsonPropertyName("resets_at")] public string? ResetsAt { get; set; }
    }

    // Rust: USERPROFILE (or "." fallback) joined with .claude/.credentials.json.
    private static string CredentialsPath()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        var basePath = string.IsNullOrEmpty(home) ? "." : home;
        return Path.Combine(basePath, ".claude", ".credentials.json");
    }

    // Rust read_token(): read file -> parse JSON -> map claudeAiOauth to (token, oauth).
    // Any failure (unreadable file / invalid JSON / missing claudeAiOauth) yields null,
    // which the caller turns into the "not logged in" NotConfigured error.
    private static ClaudeOAuth? ReadToken()
    {
        try
        {
            var content = File.ReadAllText(CredentialsPath());
            var creds = JsonSerializer.Deserialize<ClaudeCredentials>(content);
            return creds?.ClaudeAiOauth;
        }
        catch
        {
            return null;
        }
    }

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var oauth = _readToken()
            ?? throw new ProviderException("Login required: Claude not logged in. Run 'claude auth login' first.");

        using var resp = await SendUsageWithNetworkErrorsAsync(oauth.AccessToken, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode is not (401 or 403))
            return await ParseSnapshotAsync(oauth, resp, ct).ConfigureAwait(false);

        // Only a live session is worth refreshing; without a stored session the user is
        // genuinely signed out and no CLI command will help.
        if (!oauth.HasStoredSession || !await RefreshTokenViaCliAsync(instanceId, config, ct).ConfigureAwait(false))
            throw AuthFailure(oauth);

        var refreshed = _readToken();
        if (refreshed is null || string.Equals(refreshed.AccessToken, oauth.AccessToken, StringComparison.Ordinal))
            throw AuthFailure(oauth);

        using var retry = await SendUsageWithNetworkErrorsAsync(refreshed.AccessToken, ct).ConfigureAwait(false);
        if ((int)retry.StatusCode is 401 or 403)
            throw AuthFailure(refreshed);

        return await ParseSnapshotAsync(refreshed, retry, ct).ConfigureAwait(false);
    }

    private async Task<bool> RefreshTokenViaCliAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        try
        {
            return await _refreshViaCliAsync(instanceId, config, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A refresh that cannot run is not an error worth surfacing on its own; the
            // caller still reports the underlying stale-token state.
            return false;
        }
    }

    /// <summary>
    /// Runs the CLI so it refreshes its own cached token. Sends no prompt, so it costs
    /// no quota. Success is decided by whether the stored token actually changed, not by
    /// the exit code. Runs in a neutral directory so 'claude mcp list' cannot spawn MCP
    /// servers declared by a project-local .mcp.json.
    /// </summary>
    private static async Task<bool> RefreshViaCliAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var binary = ProviderConfig.ResolveCliPath(instanceId, config, "claude", "claude_path", "claude");

        return await CliTokenRefresher.TryRefreshAsync(
            binary,
            CliRefreshCommands.Claude,
            TimeSpan.FromSeconds(RefreshTimeoutSeconds),
            () => ReadToken()?.AccessToken,
            ct,
            useNeutralWorkingDirectory: true).ConfigureAwait(false);
    }

    /// <summary>
    /// A rejected access token does NOT mean the user is signed out. Claude Code refreshes
    /// its cached token lazily, so .credentials.json is routinely hours or days stale while
    /// the session is perfectly healthy — telling the user to run 'claude auth login' there
    /// is both wrong and alarming, and it is what made the card nag constantly. Signing out
    /// removes the credentials file (that is the "Not configured" path above), so a stored
    /// session credential means the session is alive and only the cached token aged out.
    /// Reached only after the silent CLI refresh above failed to produce a working token.
    /// </summary>
    private static ProviderException AuthFailure(ClaudeOAuth oauth) =>
        !oauth.HasStoredSession
            ? new ProviderException("Login required: Claude is signed out. Run 'claude auth login' first.")
            // Not prefixed "Login required": the card must not offer sign-in for a live session.
            // Tagged rather than merely un-prefixed: a comment cannot stop the card
            // offering sign-in, but the structural kind can.
            : new ProviderException(
                "Not available: Claude's cached token is stale and could not be refreshed "
                + "automatically — run any 'claude' command. If it persists, run 'claude auth login'.",
                ProviderErrorKind.Unsupported);

    private async Task<HttpResponseMessage> SendUsageWithNetworkErrorsAsync(string token, CancellationToken ct)
    {
        try
        {
            return await _sendUsageAsync(token, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Network error: {e.Message}", e);
        }
    }

    private static async Task<HttpResponseMessage> SendUsageAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        return await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private async Task<ProviderSnapshot> ParseSnapshotAsync(ClaudeOAuth oauth, HttpResponseMessage resp, CancellationToken ct)
    {
        var subDisplay = SubscriptionDisplay(oauth);

        var status = (int)resp.StatusCode;
        switch (status)
        {
            case 429:
                throw ProviderException.RateLimited(
                    "Not available: Claude usage endpoint returned HTTP 429. Will retry on next refresh.");
            default:
                if (status < 200 || status >= 300)
                    throw new ProviderException($"Network error: HTTP {status}");
                break;
        }

        OAuthUsageResponse? data;
        BalanceInfo? balance;
        List<RateWindow> additionalWindows;
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            data = document.RootElement.Deserialize<OAuthUsageResponse>();
            balance = ExtractCreditBalance(document.RootElement);
            additionalWindows = ExtractScopedWeeklyWindows(document.RootElement);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Parse error: Invalid response: {e.Message}", e);
        }
        if (data is null)
            throw new ProviderException("Parse error: Invalid response: empty response");

        // primary: always present; falls back to an empty 5h window if five_hour missing.
        RateWindow primary;
        if (data.FiveHour is { } fh)
        {
            primary = new RateWindow
            {
                Label = "5h Pool",
                UsedPercent = Quota.UtilizationToUsedPercent(fh.Utilization),
                ResetsAt = fh.ResetsAt,
                ResetDescription = fh.ResetsAt is null ? null : $"resets {fh.ResetsAt}",
                WindowMinutes = 300,
            };
        }
        else
        {
            primary = new RateWindow
            {
                Label = "5h Pool",
                UsedPercent = 0.0,
                ResetsAt = null,
                ResetDescription = null,
                WindowMinutes = null,
            };
        }

        // secondary: present iff seven_day present.
        RateWindow? secondary = null;
        if (data.SevenDay is { } sd)
        {
            secondary = new RateWindow
            {
                Label = "7d Pool",
                UsedPercent = Quota.UtilizationToUsedPercent(sd.Utilization),
                ResetsAt = sd.ResetsAt,
                ResetDescription = sd.ResetsAt is null ? null : $"resets {sd.ResetsAt}",
                WindowMinutes = 10080,
            };
        }

        AddLegacyWeeklyWindows(data, additionalWindows);

        return new ProviderSnapshot
        {
            ProviderId = Type,
            Name = $"Claude Code · {subDisplay}",
            PlanName = ProviderSnapshotIdentity.NormalizePlanName("Claude Code", subDisplay),
            Primary = primary,
            Secondary = secondary,
            Tertiary = null,
            AdditionalWindows = additionalWindows,
            Balance = balance,
            SourceLabel = SourceLabel,
            Confidence = Confidence,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static List<RateWindow> ExtractScopedWeeklyWindows(JsonElement root)
    {
        var windows = new List<RateWindow>();
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("limits", out var limits)
            || limits.ValueKind != JsonValueKind.Array)
        {
            return windows;
        }

        var seenModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object
                || !string.Equals(FirstString(limit, "kind")?.Trim(), "weekly_scoped", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(FirstString(limit, "group")?.Trim(), "weekly", StringComparison.OrdinalIgnoreCase)
                || !TryPercent(limit, out var usedPercent)
                || !TryObject(limit, "scope", out var scope)
                || !TryObject(scope, "model", out var model))
            {
                continue;
            }

            var modelId = FirstString(model, "id")?.Trim();
            var displayName = FirstString(model, "display_name")?.Trim();
            if (string.IsNullOrWhiteSpace(displayName)
                || IsAllModelsScope(modelId, displayName))
            {
                continue;
            }

            var identity = string.IsNullOrWhiteSpace(modelId) ? $"name:{displayName}" : modelId;
            if (!seenModelIds.Add(identity))
                continue;

            var resetsAt = ValidReset(limit);
            windows.Add(new RateWindow
            {
                Label = $"{displayName} only",
                UsedPercent = usedPercent,
                ResetsAt = resetsAt,
                ResetDescription = resetsAt is null ? null : $"resets {resetsAt}",
                WindowMinutes = 10080,
            });
        }

        return windows;
    }

    private static void AddLegacyWeeklyWindows(OAuthUsageResponse data, ICollection<RateWindow> windows)
    {
        AddLegacyWeeklyWindow(windows, "Sonnet only", data.SevenDaySonnet);
        AddLegacyWeeklyWindow(windows, "Opus only", data.SevenDayOpus);
        AddLegacyWeeklyWindow(
            windows,
            "Daily Routines",
            data.SevenDayRoutines
                ?? data.SevenDayClaudeRoutines
                ?? data.ClaudeRoutines
                ?? data.Routines
                ?? data.Routine
                ?? data.SevenDayCowork
                ?? data.Cowork);
    }

    private static void AddLegacyWeeklyWindow(
        ICollection<RateWindow> windows,
        string label,
        UsageWindowData? data)
    {
        if (data?.Utilization is not double utilization || !double.IsFinite(utilization))
            return;

        windows.Add(new RateWindow
        {
            Label = label,
            UsedPercent = Quota.UtilizationToUsedPercent(utilization),
            ResetsAt = data.ResetsAt,
            ResetDescription = data.ResetsAt is null ? null : $"resets {data.ResetsAt}",
            WindowMinutes = 10080,
        });
    }

    private static bool TryPercent(JsonElement limit, out double percent)
    {
        percent = 0;
        if (!limit.TryGetProperty("percent", out var value))
            return false;

        var parsed = ElementDouble(value);
        if (parsed is null || !double.IsFinite(parsed.Value) || parsed.Value < 0 || parsed.Value > 100)
            return false;

        percent = parsed.Value;
        return true;
    }

    private static string? ValidReset(JsonElement limit)
    {
        var resetsAt = FirstString(limit, "resets_at")?.Trim();
        if (string.IsNullOrWhiteSpace(resetsAt))
            return null;

        return DateTimeOffset.TryParse(
            resetsAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _)
            ? resetsAt
            : null;
    }

    private static bool IsAllModelsScope(string? modelId, string displayName)
    {
        if (ScopeSlug(displayName) == "all-models")
            return true;

        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var idSlug = ScopeSlug(modelId);
        return idSlug == "all-models" || idSlug.EndsWith("-all-models", StringComparison.Ordinal);
    }

    private static string ScopeSlug(string value)
    {
        var separators = value.Where(character => !char.IsLetterOrDigit(character)).Distinct().ToArray();
        return string.Join(
            "-",
            value.Trim().ToLowerInvariant().Split(separators, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string SubscriptionDisplay(ClaudeOAuth oauth)
    {
        var subType = string.IsNullOrWhiteSpace(oauth.SubscriptionType)
            ? "?"
            : oauth.SubscriptionType.Trim();
        return subType.ToLowerInvariant() switch
        {
            "max" => "Max",
            "max_5x" or "max-5x" or "max 5x" => "Max 5x",
            "max_20x" or "max-20x" or "max 20x" => "Max 20x",
            "pro" => "Pro",
            "team" => "Team",
            "team_standard" or "team-standard" or "team standard" => "Team Standard",
            "team_premium" or "team-premium" or "team premium" => "Team Premium",
            "enterprise" => "Enterprise",
            _ => subType,
        };
    }

    private static BalanceInfo? ExtractCreditBalance(JsonElement root)
    {
        foreach (var candidate in CreditBalanceCandidates(root))
        {
            if (TryBuildCreditBalance(candidate, out var balance))
                return balance;
        }

        return null;
    }

    private static IEnumerable<JsonElement> CreditBalanceCandidates(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        yield return root;

        foreach (var key in new[]
        {
            "credits",
            "credit",
            "credit_balance",
            "creditBalance",
            "claude_credits",
            "claudeCredits",
            "claude_code_credits",
            "claudeCodeCredits",
            "subscription_credits",
            "subscriptionCredits",
            "billing",
            "balance",
            "balances",
        })
        {
            if (TryObject(root, key, out var obj))
                yield return obj;
        }
    }

    private static bool TryBuildCreditBalance(JsonElement obj, out BalanceInfo balance)
    {
        var remaining = FirstDouble(obj,
            "remaining_credits",
            "remainingCredits",
            "credits_remaining",
            "creditsRemaining",
            "available_credits",
            "availableCredits",
            "credit_balance",
            "creditBalance",
            "remaining_balance",
            "remainingBalance",
            "available_balance",
            "availableBalance",
            "balance",
            "remaining",
            "available",
            "total_balance",
            "totalBalance");

        var used = FirstDouble(obj,
            "used_credits",
            "usedCredits",
            "credits_used",
            "creditsUsed",
            "consumed_credits",
            "consumedCredits",
            "spent_credits",
            "spentCredits",
            "used_extra_credits",
            "usedExtraCredits",
            "used",
            "spent");

        var total = FirstDouble(obj,
            "total_credits",
            "totalCredits",
            "granted_credits",
            "grantedCredits",
            "included_credits",
            "includedCredits",
            "starting_credits",
            "startingCredits",
            "monthly_limit",
            "monthlyLimit",
            "credit_limit",
            "creditLimit",
            "limit",
            "quota",
            "total",
            "granted");

        if (remaining is null && total is not null && used is not null)
            remaining = total.Value - used.Value;
        if (total is null && remaining is not null && used is not null)
            total = remaining.Value + used.Value;
        total ??= remaining;

        if (remaining is null)
        {
            balance = new BalanceInfo();
            return false;
        }

        var normalizedRemaining = Math.Max(0, remaining.Value);
        var normalizedTotal = Math.Max(normalizedRemaining, total ?? normalizedRemaining);
        var normalizedUsed = used is null
            ? Math.Max(0, normalizedTotal - normalizedRemaining)
            : Math.Max(0, used.Value);

        balance = new BalanceInfo
        {
            Currency = CreditCurrency(obj),
            Total = normalizedRemaining,
            Paid = normalizedUsed,
            Granted = normalizedTotal,
        };
        return true;
    }

    private static string CreditCurrency(JsonElement obj)
    {
        var currency = FirstString(obj, "currency", "unit", "units");
        if (string.IsNullOrWhiteSpace(currency))
            return "credits";

        return currency.Trim().Equals("credit", StringComparison.OrdinalIgnoreCase)
            ? "credits"
            : currency.Trim();
    }

    private static bool TryObject(JsonElement obj, string key, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(key, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }



}
