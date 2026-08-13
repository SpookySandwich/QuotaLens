using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>
/// Grok CLI quota provider. Ports CodexBar's grok-agent-stdio JSON-RPC flow:
/// initialize the ACP session, then call the x.ai/billing extension method.
///
/// KNOWN UPSTREAM LIMITATION: current grok CLI releases only wire x.ai/billing
/// into the interactive TUI; the agent-stdio surface answers -32601 "Method not
/// found" (verified against grok 0.2.106, same as CodexBar documents). When the
/// CLI credentials file (~/.grok/auth.json) has a non-expired session, QuotaLens
/// instead fetches the credits config directly from the CLI's own backend proxy
/// (GET {base}/billing?format=credits) — the exact REST call the CLI's billing
/// extension makes — and only falls back to the stdio RPC when that fails.
/// </summary>
public sealed class GrokProvider : IProvider
{
    public string Type => "grok";
    public string Name => "Grok";
    public string SourceLabel => "grok agent stdio";
    public Confidence Confidence => Confidence.Official;

    /// <summary>Default backend the grok CLI's x.ai/billing extension calls.</summary>
    internal const string DefaultProxyBaseUrl = "https://cli-chat-proxy.grok.com/v1";
    private const string BillingPath = "/billing?format=credits";

    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CreditsRequestTimeout = TimeSpan.FromSeconds(12);

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var binary = ResolveGrokPath(config.GetScoped(instanceId, "grok_path"));
        AppLog.Info($"grok: fetch for {instanceId}, CLI='{binary}'");

        var home = GrokHome();
        var credentials = LoadCredentials(home);

        if (credentials is null && File.Exists(Path.Combine(home, "auth.json")))
        {
            // The session exists but every stored token is expired. 'grok sessions list'
            // silently renews it (never opens a browser), so give the CLI one chance
            // before reporting login-required.
            AppLog.Info("grok: auth.json has no usable token; attempting silent CLI refresh");
            await TrySilentRefreshAsync(binary, ct).ConfigureAwait(false);
            credentials = LoadCredentials(home);
        }

        ProviderException? creditsFailure = null;

        // Fast path: the REST surface the CLI itself uses. Login-required errors are
        // final (re-login is the fix); anything else falls through to the RPC path.
        if (credentials is not null)
        {
            try
            {
                return await FetchCreditsConfigAsync(credentials, binary, ProxyBaseUrl(), ct).ConfigureAwait(false);
            }
            catch (ProviderException error) when (!IsLoginRequired(error))
            {
                creditsFailure = error;
                AppLog.Warn($"grok: credits-config fetch failed ({error.Message}); falling back to agent stdio RPC");
            }
        }

        // RPC fallback: the only path when auth.json is absent (e.g. XAI_API_KEY auth),
        // and the primary path again if xAI wires billing into the stdio surface.
        using var client = new GrokRpcClient(binary);
        try
        {
            await client.StartAsync(ct).ConfigureAwait(false);
            await client.RequestAsync("initialize", new
            {
                protocolVersion = "1",
                clientCapabilities = new
                {
                    fs = new { readTextFile = false, writeTextFile = false },
                    terminal = false,
                },
            }, InitializeTimeout, ct).ConfigureAwait(false);

            var billingJson = await client.RequestResultAsync("x.ai/billing", new { }, RequestTimeout, ct).ConfigureAwait(false);
            var billing = ParseBilling(billingJson);
            return Snapshot(billing);
        }
        catch (ProviderException error) when (IsMethodNotFound(error))
        {
            if (creditsFailure is not null)
                throw creditsFailure;
            if (credentials is null)
                throw new ProviderException("Login required: Grok CLI is not signed in. Run 'grok login' first.");
            throw;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Not available: Grok CLI failed: {e.Message}", e);
        }
        finally
        {
            client.Kill();
        }
    }

    // ---- credits-config REST path (what the CLI's x.ai/billing does today) -----

    /// <summary>
    /// One attempt at the credits endpoint, plus a single silent CLI-refresh retry
    /// when the backend rejects the token: the session is usually alive and only the
    /// cached access token aged out (mirrors Claude's reactive refresh).
    /// </summary>
    private static async Task<ProviderSnapshot> FetchCreditsConfigAsync(
        GrokCredential credentials,
        string binary,
        string proxyBase,
        CancellationToken ct)
    {
        try
        {
            return await FetchCreditsConfigOnceAsync(credentials, proxyBase, ct).ConfigureAwait(false);
        }
        catch (ProviderException error) when (IsLoginRequired(error))
        {
            AppLog.Info($"grok: credits config rejected ({error.Message}); attempting silent CLI refresh");
            if (await TrySilentRefreshAsync(binary, ct).ConfigureAwait(false))
            {
                var refreshed = LoadCredentials(GrokHome());
                if (refreshed is not null)
                    return await FetchCreditsConfigOnceAsync(refreshed, proxyBase, ct).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task<ProviderSnapshot> FetchCreditsConfigOnceAsync(
        GrokCredential credentials,
        string proxyBase,
        CancellationToken ct)
    {
        var requestUri = BillingUrl(proxyBase);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credentials.Key}");
        if (!string.IsNullOrWhiteSpace(credentials.UserId))
            request.Headers.TryAddWithoutValidation("x-userid", credentials.UserId);
        request.Headers.TryAddWithoutValidation("x-grok-client-mode", "interactive");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        AppLog.Info($"grok: GET {requestUri}");
        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CreditsRequestTimeout);
            response = await Http.Client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("Not available: Grok billing request timed out.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Network error: Grok billing request failed: {e.Message}", e);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                AppLog.Warn($"grok: credits config rejected ({response.StatusCode}); session is stale");
                throw new ProviderException("Login required: Grok session expired or was revoked. Run 'grok login' again.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderException($"Not available: Grok billing service error (HTTP {(int)response.StatusCode}).");
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            AppLog.Info($"grok: credits config captured ({json.Length} chars)");
            return Snapshot(ParseCreditsConfig(json));
        }
    }

    /// <summary>Builds the credits endpoint from a validated base URL.</summary>
    internal static Uri BillingUrl(string proxyBase)
    {
        var uri = ProviderEndpointPolicy.RequireCredentialTarget("grok", proxyBase);
        return new Uri(uri.ToString().TrimEnd('/') + BillingPath);
    }

    /// <summary>Grok home (~/.grok or GROK_HOME) — where login stores the session.</summary>
    internal static string GrokHome() =>
        ProviderConfig.Clean(Environment.GetEnvironmentVariable("GROK_HOME"))
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");

    /// <summary>
    /// The billing backend base. Honors the CLI's own override so enterprise
    /// proxies keep working; validated against the provider contract on use.
    /// </summary>
    internal static string ProxyBaseUrl() =>
        ProviderConfig.Clean(Environment.GetEnvironmentVariable("GROK_CLI_CHAT_PROXY_BASE_URL"))
        ?? DefaultProxyBaseUrl;

    /// <summary>
    /// Loads a non-expired bearer session from auth.json. Prefers the SuperGrok
    /// OIDC entry (https://auth.x.ai::client-id), then the legacy accounts entry,
    /// then any entry with a key. Mirrors CodexBar's credential preference.
    /// </summary>
    internal static GrokCredential? LoadCredentials(string? grokHome)
    {
        if (string.IsNullOrWhiteSpace(grokHome))
            return null;

        var path = Path.Combine(grokHome, "auth.json");
        if (!File.Exists(path))
        {
            AppLog.Info($"grok: no auth.json at {path}");
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var now = DateTimeOffset.UtcNow;
            GrokCredential? best = null;
            var bestPriority = int.MinValue;

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var entry = property.Value;
                var key = OptionalString(entry, "key");
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var expiresAt = ParseIso(OptionalString(entry, "expires_at"));
                if (expiresAt is not null && expiresAt <= now)
                    continue; // expired tokens are not sent

                var priority = property.Name.StartsWith("https://auth.x.ai", StringComparison.OrdinalIgnoreCase) ? 2
                    : property.Name.Contains("accounts.x.ai", StringComparison.OrdinalIgnoreCase) ? 1
                    : 0;
                if (priority <= bestPriority)
                    continue;

                bestPriority = priority;
                best = new GrokCredential(
                    key,
                    OptionalString(entry, "user_id"),
                    OptionalString(entry, "email"),
                    expiresAt,
                    OptionalString(entry, "auth_mode"));
            }

            AppLog.Info(best is null
                ? "grok: auth.json has no usable (non-expired) session"
                : $"grok: using auth.json session for {best.Email ?? "unknown email"} (mode={best.AuthMode ?? "unknown"})");
            return best;
        }
        catch (Exception e)
        {
            AppLog.Warn($"grok: failed to read auth.json: {e.Message}");
            return null;
        }
    }

    internal sealed record GrokCredential(
        string Key,
        string? UserId,
        string? Email,
        DateTimeOffset? ExpiresAt,
        string? AuthMode);

    // ---- credits-config parsing ---------------------------------------------

    /// <summary>
    /// The GetGrokCreditsConfig response shape (proto3 JSON: zero-valued fields
    /// are omitted, so absent percent means 0% used).
    /// </summary>
    internal sealed class GrokCreditsConfig
    {
        public double? CreditUsagePercent { get; set; }
        public string? PeriodType { get; set; }
        public string? PeriodStart { get; set; }
        public string? PeriodEnd { get; set; }
        public string? BillingPeriodStart { get; set; }
        public string? BillingPeriodEnd { get; set; }
        public long? MonthlyLimitCents { get; set; }
        public long? UsedCents { get; set; }
        public long? OnDemandCapCents { get; set; }
        public long? OnDemandUsedCents { get; set; }
        public long? PrepaidBalanceCents { get; set; }
        public bool? OnDemandEnabled { get; set; }
        public string? SubscriptionTier { get; set; }
    }

    internal static GrokCreditsConfig ParseCreditsConfig(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("config", out var config)
                || config.ValueKind != JsonValueKind.Object)
            {
                throw new ProviderException("Parse error: Grok credits response has no config object");
            }

            var result = new GrokCreditsConfig
            {
                CreditUsagePercent = OptionalDouble(config, "creditUsagePercent"),
                MonthlyLimitCents = OptionalCents(config, "monthlyLimit"),
                UsedCents = OptionalCents(config, "used"),
                OnDemandCapCents = OptionalCents(config, "onDemandCap"),
                OnDemandUsedCents = OptionalCents(config, "onDemandUsed"),
                PrepaidBalanceCents = OptionalCents(config, "prepaidBalance"),
                BillingPeriodStart = OptionalString(config, "billingPeriodStart"),
                BillingPeriodEnd = OptionalString(config, "billingPeriodEnd"),
                SubscriptionTier = OptionalString(root, "subscriptionTier"),
                OnDemandEnabled = OptionalBool(root, "onDemandEnabled"),
            };

            if (config.TryGetProperty("currentPeriod", out var period)
                && period.ValueKind == JsonValueKind.Object)
            {
                result.PeriodType = OptionalString(period, "type");
                result.PeriodStart = OptionalString(period, "start");
                result.PeriodEnd = OptionalString(period, "end");
            }

            return result;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new ProviderException($"Parse error: Invalid Grok credits JSON: {e.Message}", e);
        }
    }

    internal static ProviderSnapshot Snapshot(GrokCreditsConfig config, DateTimeOffset? updatedAt = null)
    {
        var usedCents = config.UsedCents ?? 0;
        var limitCents = config.MonthlyLimitCents ?? 0;
        var usedPercent = config.CreditUsagePercent is { } percent
            ? Quota.ClampPercent(percent)
            : limitCents > 0
                ? Quota.ClampPercent((double)usedCents / limitCents * 100.0)
                : 0;

        var startsAt = ParseIso(config.PeriodStart ?? config.BillingPeriodStart);
        var resetsAt = ParseIso(config.PeriodEnd ?? config.BillingPeriodEnd);
        var windowMinutes = startsAt is not null && resetsAt is not null && resetsAt > startsAt
            ? (long?)Math.Round((resetsAt.Value - startsAt.Value).TotalMinutes)
            : null;

        var label = config.PeriodType?.Contains("WEEKLY", StringComparison.OrdinalIgnoreCase) == true
            ? "Weekly included"
            : config.PeriodType?.Contains("MONTHLY", StringComparison.OrdinalIgnoreCase) == true
                ? "Monthly included"
                : "Credits";

        var resetDescription = config.CreditUsagePercent is { } displayPercent
            ? $"{displayPercent.ToString("0.#", CultureInfo.InvariantCulture)}% of included allowance used"
            : limitCents > 0
                ? $"{Usd(usedCents)} / {Usd(limitCents)} included"
                : $"{Usd(usedCents)} used";

        var onDemandCap = config.OnDemandCapCents ?? 0;
        var onDemandUsed = config.OnDemandUsedCents ?? 0;
        var secondary = onDemandUsed > 0 || onDemandCap > 0
            ? new RateWindow
            {
                Label = "On-demand",
                UsedPercent = onDemandCap > 0
                    ? Quota.ClampPercent((double)onDemandUsed / onDemandCap * 100.0)
                    : onDemandUsed > 0 ? 100 : 0,
                ResetsAt = resetsAt?.ToString("O", CultureInfo.InvariantCulture),
                ResetDescription = onDemandCap > 0
                    ? $"{Usd(onDemandUsed)} / {Usd(onDemandCap)} cap"
                    : $"{Usd(onDemandUsed)} used",
                WindowMinutes = windowMinutes,
            }
            : null;

        BalanceInfo? balance = null;
        if (limitCents > 0)
        {
            balance = new BalanceInfo
            {
                Currency = "USD",
                Total = Math.Max(0, (limitCents - usedCents) / 100.0),
                Paid = usedCents / 100.0,
                Granted = limitCents / 100.0,
            };
        }
        else if (config.PrepaidBalanceCents is > 0)
        {
            var prepaid = config.PrepaidBalanceCents.Value / 100.0;
            balance = new BalanceInfo
            {
                Currency = "USD",
                Total = prepaid,
                Paid = 0,
                Granted = prepaid,
            };
        }

        return new ProviderSnapshot
        {
            ProviderId = "grok",
            Name = "Grok",
            PlanName = string.IsNullOrWhiteSpace(config.SubscriptionTier) ? null : config.SubscriptionTier,
            Primary = new RateWindow
            {
                Label = label,
                UsedPercent = usedPercent,
                ResetsAt = resetsAt?.ToString("O", CultureInfo.InvariantCulture),
                ResetDescription = resetDescription,
                WindowMinutes = windowMinutes,
            },
            Secondary = secondary,
            Balance = balance,
            SourceLabel = "grok.com billing",
            Confidence = Confidence.Official,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
        };
    }

    // ---- legacy ACP RPC path (kept as fallback + for future CLI versions) ----

    internal static GrokBilling ParseBilling(string json)
    {
        try
        {
            var billing = JsonSerializer.Deserialize<GrokBilling>(json, JsonOptions);
            if (billing is null)
                throw new ProviderException("Parse error: Grok billing response was empty");
            return billing;
        }
        catch (JsonException e)
        {
            throw new ProviderException($"Parse error: Invalid Grok billing JSON: {e.Message}", e);
        }
    }

    internal static ProviderSnapshot Snapshot(GrokBilling billing, DateTimeOffset? updatedAt = null)
    {
        var monthlyLimitCents = billing.MonthlyLimit?.Val ?? 0;
        var includedUsedCents = billing.Usage?.IncludedUsed?.Val ?? 0;
        var totalUsedCents = billing.Usage?.TotalUsed?.Val ?? includedUsedCents;
        var onDemandUsedCents = billing.Usage?.OnDemandUsed?.Val ?? Math.Max(0, totalUsedCents - includedUsedCents);
        var onDemandCapCents = billing.OnDemandCap?.Val;
        var resetsAt = ParseIso(billing.BillingCycle?.BillingPeriodEnd);
        var startsAt = ParseIso(billing.BillingCycle?.BillingPeriodStart);
        var windowMinutes = startsAt is not null && resetsAt is not null && resetsAt > startsAt
            ? (long?)Math.Round((resetsAt.Value - startsAt.Value).TotalMinutes)
            : null;

        var usedPercent = monthlyLimitCents > 0
            ? Quota.ClampPercent((double)totalUsedCents / monthlyLimitCents * 100.0)
            : 0;

        var primaryDescription = monthlyLimitCents > 0
            ? $"{Usd(totalUsedCents)} / {Usd(monthlyLimitCents)} included"
            : $"{Usd(totalUsedCents)} used";

        var secondary = onDemandUsedCents > 0 || onDemandCapCents is > 0
            ? new RateWindow
            {
                Label = "On-demand",
                UsedPercent = onDemandCapCents is > 0
                    ? Quota.ClampPercent((double)onDemandUsedCents / onDemandCapCents.Value * 100.0)
                    : onDemandUsedCents > 0 ? 100 : 0,
                ResetsAt = resetsAt?.ToString("O", CultureInfo.InvariantCulture),
                ResetDescription = onDemandCapCents is > 0
                    ? $"{Usd(onDemandUsedCents)} / {Usd(onDemandCapCents.Value)} cap"
                    : $"{Usd(onDemandUsedCents)} used",
                WindowMinutes = windowMinutes,
            }
            : null;

        return new ProviderSnapshot
        {
            ProviderId = "grok",
            Name = "Grok",
            Primary = new RateWindow
            {
                Label = "Monthly included",
                UsedPercent = usedPercent,
                ResetsAt = resetsAt?.ToString("O", CultureInfo.InvariantCulture),
                ResetDescription = primaryDescription,
                WindowMinutes = windowMinutes,
            },
            Secondary = secondary,
            Balance = new BalanceInfo
            {
                Currency = "USD",
                Total = Math.Max(0, (monthlyLimitCents - totalUsedCents) / 100.0),
                Paid = totalUsedCents / 100.0,
                Granted = monthlyLimitCents / 100.0,
            },
            SourceLabel = "grok agent stdio",
            Confidence = Confidence.Official,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
        };
    }

    internal static string ResolveGrokPath(string? configured)
    {
        var explicitPath = ProviderConfig.Clean(configured)
            ?? ProviderConfig.Environment("GROK_CLI_PATH");
        if (explicitPath is not null)
            return Environment.ExpandEnvironmentVariables(explicitPath);

        return "grok";
    }

    // ---- silent CLI token refresh -------------------------------------------

    /// <summary>
    /// Runs the measured, non-prompt-bearing refresh command ('grok sessions list') so
    /// the CLI renews ITS OWN cached token, and reports whether the stored credential
    /// changed. Never opens a browser. Mirrors Claude/Gemini/Kimi's reactive refresh.
    /// </summary>
    private static async Task<bool> TrySilentRefreshAsync(string binary, CancellationToken ct) =>
        await CliTokenRefresher.TryRefreshAsync(
            binary,
            CliRefreshCommands.Grok,
            TimeSpan.FromSeconds(45),
            () => ReadAuthFileFingerprint(GrokHome()),
            ct,
            useNeutralWorkingDirectory: true).ConfigureAwait(false);

    /// <summary>Full auth.json text — changes exactly when the CLI renews the token.</summary>
    internal static string? ReadAuthFileFingerprint(string? grokHome)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(grokHome))
                return null;

            var path = Path.Combine(grokHome, "auth.json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            // Mid-rewrite read: unknown, not a change.
            return null;
        }
    }

    // ---- shared helpers ------------------------------------------------------

    private static bool IsMethodNotFound(ProviderException error) =>
        error.Message.Contains("Method not found", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoginRequired(ProviderException error) =>
        error.Message.StartsWith("Login required", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? ParseIso(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string Usd(long cents)
    {
        var dollars = cents / 100.0;
        return dollars < 100
            ? $"${dollars.ToString("F2", CultureInfo.InvariantCulture)}"
            : $"${dollars.ToString("F0", CultureInfo.InvariantCulture)}";
    }

    private static string? OptionalString(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? OptionalDouble(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;

    private static long? OptionalCents(JsonElement parent, string key)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(key, out var cent)
            || cent.ValueKind != JsonValueKind.Object
            || !cent.TryGetProperty("val", out var value)
            || !value.TryGetInt64(out var val))
        {
            return null;
        }

        return val;
    }

    private static bool? OptionalBool(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(key, out var value)
        && (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal sealed class GrokBilling
    {
        [JsonPropertyName("billingCycle")] public GrokBillingCycle? BillingCycle { get; set; }
        [JsonPropertyName("monthlyLimit")] public GrokCent? MonthlyLimit { get; set; }
        [JsonPropertyName("onDemandCap")] public GrokCent? OnDemandCap { get; set; }
        [JsonPropertyName("on_demand_enabled")] public bool? OnDemandEnabledSnake { get; set; }
        [JsonPropertyName("onDemandEnabled")] public bool? OnDemandEnabled { get; set; }
        [JsonPropertyName("disabledByConfig")] public bool? DisabledByConfig { get; set; }
        [JsonPropertyName("usage")] public GrokBillingUsage? Usage { get; set; }
    }

    internal sealed class GrokBillingCycle
    {
        [JsonPropertyName("billingPeriodStart")] public string? BillingPeriodStart { get; set; }
        [JsonPropertyName("billingPeriodEnd")] public string? BillingPeriodEnd { get; set; }
    }

    internal sealed class GrokBillingUsage
    {
        [JsonPropertyName("includedUsed")] public GrokCent? IncludedUsed { get; set; }
        [JsonPropertyName("onDemandUsed")] public GrokCent? OnDemandUsed { get; set; }
        [JsonPropertyName("totalUsed")] public GrokCent? TotalUsed { get; set; }
    }

    internal sealed class GrokCent
    {
        [JsonPropertyName("val")] public int? Val { get; set; }
    }

    private sealed class GrokRpcClient : IDisposable
    {
        private readonly string _binary;
        private Process? _process;
        private int _nextId = 1;

        public GrokRpcClient(string binary)
        {
            _binary = binary;
        }

        public Task StartAsync(CancellationToken ct)
        {
            // Shared CLI launch path: resolves the configured/PATH binary including
            // .cmd/.bat shims, exactly like the other CLI-backed providers.
            var startInfo = HiddenCliProcess.CreateStartInfo(_binary, new[] { "agent", "stdio" });
            startInfo.StandardInputEncoding = Encoding.UTF8;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;

            var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Exception e)
            {
                throw new ProviderException($"Not available: Grok CLI not found at {_binary}: {e.Message}", e);
            }

            _process = process;
            AppLog.Info($"grok: started '{startInfo.FileName}' agent stdio");
            return Task.CompletedTask;
        }

        public async Task<JsonElement> RequestAsync(string method, object parameters, TimeSpan timeout, CancellationToken ct)
        {
            var id = _nextId++;
            var payload = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters,
            }, JsonOptions).Replace("\\/", "/", StringComparison.Ordinal);

            var process = _process ?? throw new ProviderException("Not available: Grok CLI process was not started");
            await process.StandardInput.WriteLineAsync(payload.AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            while (!timeoutCts.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Kill();
                    throw new ProviderException($"Timeout: Grok RPC timed out on {method}");
                }

                if (line is null)
                {
                    var stderr = await ReadStderrIfReadyAsync(process).ConfigureAwait(false);
                    throw new ProviderException(ProviderConfig.Clean(stderr) ?? "Malformed Grok RPC response: stdout closed");
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var responseId) || JsonId(responseId) != id)
                        continue;

                    if (root.TryGetProperty("error", out var error))
                        throw new ProviderException(GrokErrorMessage(error));

                    if (!root.TryGetProperty("result", out var result))
                        throw new ProviderException("Malformed Grok RPC response: missing result");

                    return result.Clone();
                }
            }

            Kill();
            throw new ProviderException($"Timeout: Grok RPC timed out on {method}");
        }

        public async Task<string> RequestResultAsync(string method, object parameters, TimeSpan timeout, CancellationToken ct)
        {
            var result = await RequestAsync(method, parameters, timeout, ct).ConfigureAwait(false);
            return result.GetRawText();
        }

        public void Kill()
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch { }
        }

        public void Dispose()
        {
            Kill();
            _process?.Dispose();
        }

        private static int? JsonId(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id))
                return id;
            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringId))
                return stringId;
            return null;
        }

        private static string GrokErrorMessage(JsonElement error)
        {
            var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var messageProperty)
                ? messageProperty.GetString()
                : null;
            var code = error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var codeProperty)
                && codeProperty.ValueKind == JsonValueKind.Number
                    ? codeProperty.GetRawText()
                    : null;

            if (message is not null
                && (message.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("grok login", StringComparison.OrdinalIgnoreCase)))
            {
                return "Not available: Grok billing requires authentication. Run grok login.";
            }

            return $"Not available: Grok request failed: {message ?? error.GetRawText()}"
                + (code is null ? "" : $" (code {code})");
        }

        private static async Task<string> ReadStderrIfReadyAsync(Process process)
        {
            try
            {
                return process.HasExited
                    ? await process.StandardError.ReadToEndAsync().ConfigureAwait(false)
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
