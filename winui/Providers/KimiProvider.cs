using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>
/// Kimi provider with CLI-first detection. When the Kimi Code CLI is installed
/// (https://moonshotai.github.io/kimi-code/) its device-code OAuth credentials live in
/// %USERPROFILE%\.kimi-code\credentials\kimi-code.json and work against the official
/// coding usage endpoint GET https://api.kimi.com/coding/v1/usages — no embedded
/// browser needed. Without CLI credentials this falls back to the WebView login flow
/// in WebLoginService (the pre-existing behavior).
///
/// READ-ONLY BY POLICY: the credential store belongs to the Kimi CLI, and QuotaLens
/// never writes to it. Kimi's OAuth refresh ROTATES the refresh token, so refreshing
/// would force us to persist the rotation — and a crash between the server rotating and
/// us writing would strand the CLI's own session. A monitoring app must not be able to
/// break the tool it monitors. Access tokens last ~15 minutes, so when the stored token
/// has expired this reports "login required" and defers to the WebView session or a
/// manual 'kimi login'; running the CLI refreshes the token for us.
/// Enforced by ReadOnlyProviderSafetyTests.
/// </summary>
public sealed class KimiProvider : IProvider
{
    private const string UsageEndpoint = "https://api.kimi.com/coding/v1/usages";
    private const int ExpiryGraceSeconds = 60;
    private const int CliRefreshTimeoutSeconds = 20;

    private readonly Func<JsonObject?> _readCredentials;
    private readonly Func<string, CancellationToken, Task<HttpResponseMessage>> _sendUsageAsync;
    private readonly Func<string, IConfig, CancellationToken, Task<bool>> _refreshViaCliAsync;
    private readonly Func<CancellationToken, Task<CliUsageDetail?>> _fetchWebTotalQuotaAsync;
    private readonly IProvider _webFallback;

    public KimiProvider()
        : this(ReadCredentials, SendUsageAsync, new WebViewLoginProvider("kimi"), RefreshViaCliAsync, FetchWebTotalQuotaAsync)
    {
    }

    internal KimiProvider(
        Func<JsonObject?> readCredentials,
        Func<string, CancellationToken, Task<HttpResponseMessage>> sendUsageAsync,
        IProvider webFallback,
        Func<string, IConfig, CancellationToken, Task<bool>>? refreshViaCliAsync = null,
        Func<CancellationToken, Task<CliUsageDetail?>>? fetchWebTotalQuotaAsync = null)
    {
        _readCredentials = readCredentials;
        _sendUsageAsync = sendUsageAsync;
        _webFallback = webFallback;
        _refreshViaCliAsync = refreshViaCliAsync ?? ((_, _, _) => Task.FromResult(false));
        _fetchWebTotalQuotaAsync = fetchWebTotalQuotaAsync ?? ((_) => Task.FromResult<CliUsageDetail?>(null));
    }

    public string Type => "kimi";
    public string Name => "Kimi";
    public string SourceLabel => "Kimi Code CLI";
    public Confidence Confidence => Confidence.Official;

    /// <summary>Auth failures that mean the CLI session is dead (vs transient errors).</summary>
    private sealed class KimiCliAuthException : Exception
    {
        public KimiCliAuthException(string message) : base(message) { }
    }

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var creds = _readCredentials();
        if (creds is null)
            return await _webFallback.FetchAsync(instanceId, config, ct).ConfigureAwait(false);

        try
        {
            return await FetchWithCliAsync(instanceId, creds, config, ct).ConfigureAwait(false);
        }
        catch (KimiCliAuthException authError)
        {
            // The CLI session is dead; the WebView session (or its cache) may still work,
            // and if not, the card should surface the sign-in action.
            try
            {
                return await _webFallback.FetchAsync(instanceId, config, ct).ConfigureAwait(false);
            }
            catch (ProviderException)
            {
                throw new ProviderException(
                    $"Login required: Kimi Code CLI session is not usable ({authError.Message}). " +
                    "Run any 'kimi' command to refresh it, or click to open Kimi in browser.");
            }
        }
    }

    private async Task<ProviderSnapshot> FetchWithCliAsync(string instanceId, JsonObject creds, IConfig config, CancellationToken ct)
    {
        var token = StringField(creds, "access_token") ?? "";
        if (string.IsNullOrWhiteSpace(token))
            throw new KimiCliAuthException("no access token stored");

        // Kimi access tokens live ~15 minutes, so without this the card is stale almost
        // all the time. Expiry is checked BEFORE sending: an expired token is refreshed
        // rather than spent on a request that is certain to 401.
        if (IsExpired(creds))
        {
            creds = await RefreshCredentialsAsync(instanceId, creds, config, ct).ConfigureAwait(false)
                ?? throw new KimiCliAuthException("the stored access token has expired");
            token = StringField(creds, "access_token") ?? "";
        }

        using var resp = await SendUsageWithNetworkErrorsAsync(token, ct).ConfigureAwait(false);
        if (!IsAuthFailure(resp))
            return await ParseUsageResponseAsync(resp, ct).ConfigureAwait(false);

        var refreshed = await RefreshCredentialsAsync(instanceId, creds, config, ct).ConfigureAwait(false)
            ?? throw new KimiCliAuthException("the stored access token was rejected");

        using var retry = await SendUsageWithNetworkErrorsAsync(
            StringField(refreshed, "access_token") ?? "", ct).ConfigureAwait(false);
        if (IsAuthFailure(retry))
            throw new KimiCliAuthException("the refreshed access token was also rejected");

        return await ParseUsageResponseAsync(retry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the Kimi CLI to refresh its own credential file, returning the reloaded
    /// credentials or null. Gated on a real (if expired) token: with a tombstoned
    /// credential the CLI would pop a browser for a full sign-in, which a background
    /// quota refresh must never do unprompted.
    /// </summary>
    private async Task<JsonObject?> RefreshCredentialsAsync(string instanceId, JsonObject creds, IConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(StringField(creds, "access_token"))
            || NumberField(creds, "expires_at") is not > 0)
        {
            return null;
        }

        return await _refreshViaCliAsync(instanceId, config, ct).ConfigureAwait(false)
            ? _readCredentials()
            : null;
    }

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

    private static bool IsAuthFailure(HttpResponseMessage resp) =>
        (int)resp.StatusCode is 401 or 403;

    private static bool IsExpired(JsonObject creds)
    {
        var expiresAt = NumberField(creds, "expires_at");
        if (expiresAt is null)
            return false; // no expiry info: try the token as-is

        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresAt.Value - ExpiryGraceSeconds;
    }

    private static string? StringField(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node)
            && node is JsonValue value
            && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static long? NumberField(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
            return null;

        if (value.TryGetValue<long>(out var integral))
            return integral;
        if (value.TryGetValue<double>(out var floating))
            return (long)floating;
        return null;
    }

    // ---- default (production) IO --------------------------------------------

    private static string CredentialsDirectory()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        var basePath = string.IsNullOrEmpty(home) ? "." : home;
        return Path.Combine(basePath, ".kimi-code");
    }

    internal static string CredentialsPath() =>
        Path.Combine(CredentialsDirectory(), "credentials", "kimi-code.json");

    private static JsonObject? ReadCredentials()
    {
        try
        {
            var content = File.ReadAllText(CredentialsPath());
            return JsonNode.Parse(content) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Runs the Kimi CLI so it refreshes its own token. Sends no prompt, so it costs no
    /// quota. Success is decided by the credential changing — measured on Kimi Code
    /// 0.28.1, `kimi login` exits 0xC0000409 AFTER succeeding and exits 0 when it
    /// no-ops, so the exit code says the opposite of the truth.
    /// </summary>
    private static async Task<bool> RefreshViaCliAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var binary = ProviderConfig.ResolveCliPath(instanceId, config, "kimi", "kimi_cli_path", "kimi");

        return await CliTokenRefresher.TryRefreshAsync(
            binary,
            CliRefreshCommands.Kimi,
            TimeSpan.FromSeconds(CliRefreshTimeoutSeconds),
            () => StringField(ReadCredentials() ?? new JsonObject(), "access_token"),
            ct).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendUsageAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        return await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    // ---- total-quota enrichment (web billing, via the Kimi desktop app token) --

    private static readonly TimeSpan WebTotalQuotaTimeout = TimeSpan.FromSeconds(5);

    private static async Task<CliUsageDetail?> FetchWebTotalQuotaAsync(CancellationToken ct)
    {
        try
        {
            var token = ReadKimiDesktopAccessToken();
            if (string.IsNullOrWhiteSpace(token))
                return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(WebTotalQuotaTimeout);

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://www.kimi.com/apiv2/kimi.gateway.billing.v1.BillingService/GetUsages")
            {
                Content = new StringContent("{\"scope\":[\"FEATURE_CODING\"]}", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("connect-protocol-version", "1");
            request.Headers.TryAddWithoutValidation("x-msh-platform", "web");

            using var response = await Http.Client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ParseWebTotalQuota(json);
        }
        catch
        {
            return null; // enrichment is best-effort and must never fail the fetch
        }
    }

    private static string? ReadKimiDesktopAccessToken()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "kimi-desktop", "bridge-store", "token-store.json");
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("tokens", out var tokens)
                && tokens.ValueKind == JsonValueKind.Object
                && tokens.TryGetProperty("access_token", out var accessToken)
                && accessToken.ValueKind == JsonValueKind.String)
            {
                return accessToken.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    internal static CliUsageDetail? ParseWebTotalQuota(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("totalQuota", out var totalQuota)
            || totalQuota.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        static string? Get(JsonElement obj, string key) =>
            obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        var detail = new CliUsageDetail
        {
            Limit = Get(totalQuota, "limit"),
            Used = Get(totalQuota, "used"),
            Remaining = Get(totalQuota, "remaining"),
        };
        return detail.Limit is null && detail.Used is null && detail.Remaining is null
            ? null
            : detail;
    }

    // ---- usage response parsing ----------------------------------------------

    private sealed class CliUsageResponse
    {
        [JsonPropertyName("user")] public CliUser? User { get; set; }
        [JsonPropertyName("usage")] public CliUsageDetail? Usage { get; set; }
        [JsonPropertyName("limits")] public List<CliUsageLimit>? Limits { get; set; }
        [JsonPropertyName("totalQuota")] public CliUsageDetail? TotalQuota { get; set; }
        [JsonPropertyName("parallel")] public CliParallel? Parallel { get; set; }
    }

    private sealed class CliUser
    {
        [JsonPropertyName("membership")] public CliMembership? Membership { get; set; }
    }

    private sealed class CliMembership
    {
        [JsonPropertyName("level")] public string? Level { get; set; }
    }

    private sealed class CliUsageLimit
    {
        [JsonPropertyName("window")] public CliUsageWindow? Window { get; set; }
        [JsonPropertyName("detail")] public CliUsageDetail? Detail { get; set; }
    }

    private sealed class CliUsageWindow
    {
        [JsonPropertyName("duration")] public long? Duration { get; set; }
        [JsonPropertyName("timeUnit")] public string? TimeUnit { get; set; }
    }

    internal sealed class CliUsageDetail
    {
        [JsonPropertyName("limit")] public string? Limit { get; set; }
        [JsonPropertyName("used")] public string? Used { get; set; }
        [JsonPropertyName("remaining")] public string? Remaining { get; set; }
        [JsonPropertyName("resetTime")] public string? ResetTime { get; set; }
    }

    private sealed class CliParallel
    {
        [JsonPropertyName("limit")] public string? Limit { get; set; }
    }

    private async Task<ProviderSnapshot> ParseUsageResponseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var status = (int)resp.StatusCode;
        if (status == 429)
            throw ProviderException.RateLimited("Not available: Kimi usage API rate limited. Will retry on next refresh.");
        if (status < 200 || status >= 300)
            throw new ProviderException($"Network error: HTTP {status}");

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var snapshot = ParseCliUsage(json);

        // Best-effort enrichment: the CLI endpoint reports totalQuota as empty for
        // CLI-auth; the web billing endpoint (using the Kimi desktop app's token) does
        // report it. Attach it only when the CLI response did not already carry one.
        if (snapshot.Tertiary is null)
        {
            var webTotal = await _fetchWebTotalQuotaAsync(ct).ConfigureAwait(false);
            if (webTotal is not null)
                snapshot.Tertiary = BuildWindow("Total quota", webTotal, windowMinutes: null, descriptionPrefix: null);
        }

        return snapshot;
    }

    internal ProviderSnapshot ParseCliUsage(string json)
    {
        CliUsageResponse? data;
        try
        {
            data = JsonSerializer.Deserialize<CliUsageResponse>(json);
        }
        catch (Exception e)
        {
            throw new ProviderException($"Parse error: Invalid JSON: {e.Message}", e);
        }

        var weekly = data?.Usage
            ?? throw new ProviderException("Parse error: Kimi usage detail missing");

        var primary = BuildWindow("Weekly", weekly, windowMinutes: 10080, descriptionPrefix: null);

        // Every rate-limit window the API reports, shortest first (observed: a single
        // 300-minute rolling limit, but other accounts can report several).
        var rateLimits = (data.Limits ?? new List<CliUsageLimit>())
            .Where(l => l.Detail is not null)
            .OrderBy(l => WindowMinutesFor(l.Window) ?? long.MaxValue)
            .ToList();
        RateWindow? secondary = null;
        var additional = new List<RateWindow>();
        foreach (var (rateLimit, index) in rateLimits.Select((limit, index) => (limit, index)))
        {
            var minutes = WindowMinutesFor(rateLimit.Window);
            var window = BuildWindow(
                minutes == 300 ? "5h Rate Limit" : "Rate Limit",
                rateLimit.Detail!,
                windowMinutes: minutes,
                descriptionPrefix: "Rate: ");
            if (index == 0) secondary = window;
            else additional.Add(window);
        }

        // Total quota ("总额度") is present for some accounts and empty for others.
        var tertiary = data.TotalQuota is { } totalQuota
            && (ParseLong(totalQuota.Limit) is not null
                || ParseLong(totalQuota.Remaining) is not null
                || ParseLong(totalQuota.Used) is not null)
            ? BuildWindow("Total quota", totalQuota, windowMinutes: null, descriptionPrefix: null)
            : null;

        // Concurrency is informational, not a quota denominator.
        var parallel = ParseLong(data.Parallel?.Limit);
        if (parallel is > 0)
        {
            additional.Add(new RateWindow
            {
                Label = "Concurrency",
                Kind = RateWindowKind.Informational,
                ValueText = $"{parallel} concurrent",
            });
        }

        var tier = TierName(data.User?.Membership?.Level);
        return new ProviderSnapshot
        {
            ProviderId = Type,
            Name = string.IsNullOrWhiteSpace(tier) ? "Kimi" : $"Kimi · {tier}",
            PlanName = tier,
            Primary = primary,
            Secondary = secondary,
            Tertiary = tertiary,
            AdditionalWindows = additional,
            SourceLabel = SourceLabel,
            Confidence = Confidence,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static long? WindowMinutesFor(CliUsageWindow? window)
    {
        if (window?.Duration is not { } duration)
            return null;

        return (window.TimeUnit ?? "").ToUpperInvariant() switch
        {
            "TIME_UNIT_MINUTE" or "" => duration,
            "TIME_UNIT_HOUR" => duration * 60,
            "TIME_UNIT_DAY" => duration * 1440,
            _ => null,
        };
    }

    private static RateWindow BuildWindow(
        string label,
        CliUsageDetail detail,
        long? windowMinutes,
        string? descriptionPrefix)
    {
        var limit = ParseLong(detail.Limit);
        var remaining = ParseLong(detail.Remaining);
        var used = ParseLong(detail.Used);
        if (used is null && limit is not null && remaining is not null)
            used = Math.Max(0, limit.Value - remaining.Value);

        var resolvedLimit = Math.Max(0, limit ?? 0);
        var resolvedUsed = Math.Max(0, used ?? 0);
        var usedPercent = resolvedLimit > 0
            ? Quota.UtilizationToUsedPercent((double)resolvedUsed / resolvedLimit)
            : 0.0;

        // The CLI endpoint normalizes limits to 100 (percent) for the observed account;
        // keep the raw x/y form for any account where the limit is a real request count.
        var prefix = descriptionPrefix ?? "";
        var description = resolvedLimit == 100
            ? $"{prefix}{resolvedUsed}% used"
            : $"{prefix}{resolvedUsed}/{resolvedLimit} requests";

        return new RateWindow
        {
            Label = label,
            UsedPercent = usedPercent,
            ResetsAt = detail.ResetTime,
            ResetDescription = description,
            WindowMinutes = windowMinutes,
        };
    }

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Maps the CLI membership level onto Kimi For Coding's public plan names.
    /// LEVEL_INTERMEDIATE was verified against a live Moderato-tier account; the
    /// BASIC/ADVANCED mappings follow the same ordering (Andante &lt; Moderato &lt;
    /// Allegretto) but are unverified. Unknown levels fall back to a prettified
    /// form of the raw value so new tiers still display something sensible.
    /// </summary>
    internal static string? TierName(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
            return null;

        switch (level.ToUpperInvariant())
        {
            case "LEVEL_BASIC": return "Andante";
            case "LEVEL_INTERMEDIATE": return "Moderato";
            case "LEVEL_ADVANCED": return "Allegretto";
        }

        var raw = level.StartsWith("LEVEL_", StringComparison.OrdinalIgnoreCase)
            ? level["LEVEL_".Length..]
            : level;
        raw = raw.Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(raw)
            ? null
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.ToLowerInvariant());
    }
}
