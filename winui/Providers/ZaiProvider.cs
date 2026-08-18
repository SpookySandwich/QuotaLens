using System.Net.Http;
using System.Text.Json;
using QuotaLens.Core;
using QuotaLens.Services;
using static QuotaLens.Core.JsonUtil;

namespace QuotaLens.Providers;

/// <summary>
/// z.ai with CLI-first detection. When ZCode is installed and signed in, its local
/// session (read from ~/.zcode/v2/credentials.json, see <see cref="ZcodeCredentials"/>)
/// authorizes the official coding-plan endpoint
/// GET https://zcode.z.ai/api/v1/zcode-plan/billing/balance — the token pool ZCode
/// actually consumes (per-entitlement total/used/remaining units). Without a local
/// session this falls back to the z.ai API-key flow (SimpleApiProvider), which
/// measures API-key resource packs — a different pool than coding plans.
/// </summary>
public sealed class ZaiProvider : IProvider
{
    private const string BalanceEndpoint = "https://zcode.z.ai/api/v1/zcode-plan/billing/balance";
    private static readonly TimeSpan BalanceTimeout = TimeSpan.FromSeconds(15);

    private readonly IReadOnlyList<IProviderSource> _sources;

    public ZaiProvider()
        : this(ZcodeCredentials.HasSession, SendBalanceAsync, ZcodeCredentials.TryReadSessionToken)
    {
    }

    internal ZaiProvider(
        Func<bool> cliIsAvailable,
        Func<string, CancellationToken, Task<HttpResponseMessage>> sendBalanceAsync,
        Func<string?>? readSessionToken = null,
        Func<bool>? apiIsAvailable = null,
        Func<string, IConfig, CancellationToken, Task<ProviderSnapshot>>? apiFetchAsync = null)
    {
        var readToken = readSessionToken ?? ZcodeCredentials.TryReadSessionToken;
        var isApiAvailable = apiIsAvailable ?? (() => true);
        var fetchFromApi = apiFetchAsync
            ?? ((string instanceId, IConfig config, CancellationToken ct) =>
                new SimpleApiProvider("zai").FetchAsync(instanceId, config, ct));
        var recovery = new ProviderRecoveryAction(
            ProviderRecoveryKind.LaunchApp,
            "zcode.cliSourceNote");

        _sources = new IProviderSource[]
        {
            new ProviderSource(
                ProviderSourceMode.Cli,
                (_, _) => cliIsAvailable(),
                (_, _, ct) => FetchZcodeAsync(readToken, sendBalanceAsync, ct),
                configFieldKeys: new[] { "zai_home", "zai_app_path" },
                attentionNote: "zcode.cliSourceNote",
                unavailableRecovery: recovery,
                connectionAction: new AppProviderConnectionAction(
                    "zai",
                    "zai_app_path",
                    cliIsAvailable,
                    verificationFieldKeys: new[] { "zai_home", "zai_app_path" }),
                launchAction: new AppProviderLaunchAction("zai"),
                watchPaths: (instanceId, config) =>
                    new[] { ZcodeCredentials.StorePath(config, instanceId) }),
            new ProviderSource(
                ProviderSourceMode.Web,
                (_, _) => isApiAvailable(),
                fetchFromApi,
                configFieldKeys: new[] { "zai_key", "zai_base_url", "zai_quota_url" },
                legacyConfigValues: new[] { "key" },
                attentionNote: "zai.webSourceNote"),
        };
    }

    public string Type => "zai";
    public string Name => "z.ai";
    public string SourceLabel => "ZCode CLI";
    public Confidence Confidence => Confidence.Official;
    public IReadOnlyList<IProviderSource> Sources => _sources;

    public Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct) =>
        ProviderSourceRunner.FetchAsync(this, _sources, instanceId, config, ct);

    private static async Task<ProviderSnapshot> FetchZcodeAsync(
        Func<string?> readSessionToken,
        Func<string, CancellationToken, Task<HttpResponseMessage>> sendBalanceAsync,
        CancellationToken ct)
    {
        var token = readSessionToken()
            ?? throw new ProviderException(
                "Login required: ZCode is not signed in on this machine.",
                ProviderErrorKind.AuthenticationRequired);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(BalanceTimeout);

        using var response = await sendBalanceAsync(token, timeout.Token).ConfigureAwait(false);
        if ((int)response.StatusCode is 401 or 403)
            throw new ProviderException(
                "Login required: ZCode session was rejected. Open ZCode to re-login.",
                ProviderErrorKind.AuthenticationRequired);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException($"Network error: HTTP {(int)response.StatusCode}");

        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return ParseBalance(json);
    }

    private static async Task<HttpResponseMessage> SendBalanceAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BalanceEndpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    // ---- coding-plan balance parsing -------------------------------------------

    internal static ProviderSnapshot ParseBalance(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (OptionalLong(root, "code") is { } code && code != 0)
            throw new ProviderException($"Not available: zcode plan API returned {OptionalString(root, "msg") ?? $"code {code}"}");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new ProviderException("Parse error: Missing zcode plan data");

        var balances = new List<(long Priority, JsonElement Balance)>();
        if (data.TryGetProperty("balances", out var balancesElement)
            && balancesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var balance in balancesElement.EnumerateArray())
            {
                var priority = OptionalLong(balance, "entitlement_priority") ?? 0;
                balances.Add((priority, balance));
            }
        }

        if (balances.Count == 0)
            // The call succeeded and the account is signed in — there simply is no plan
            // attached (e.g. a promotional plan that has ended). Reporting that as a parse
            // error blames the app for a fact about the account, and no button can fix it.
            throw new ProviderException(
                "Not available: this z.ai account has no active ZCode plan. Buy a coding plan, "
                + "or switch this card to the API Key source to track API credit instead.",
                ProviderErrorKind.Unsupported);

        var ordered = balances
            .OrderByDescending(entry => entry.Priority)
            .Select(entry => entry.Balance)
            .ToList();

        var planName = PlanNameFor(data, ordered[0]);

        return new ProviderSnapshot
        {
            ProviderId = "zai",
            Name = "z.ai",
            PlanName = planName,
            Primary = ToWindow(ordered[0]),
            Secondary = ordered.Count > 1 ? ToWindow(ordered[1]) : null,
            AdditionalWindows = ordered.Skip(2).Select(ToWindow).ToList(),
            SourceLabel = "ZCode CLI",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string? PlanNameFor(JsonElement data, JsonElement primaryBalance)
    {
        var planId = OptionalString(primaryBalance, "plan_id");
        if (planId is null
            || !data.TryGetProperty("plans", out var plans)
            || plans.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var plan in plans.EnumerateArray())
        {
            if (OptionalString(plan, "plan_id") == planId)
                return OptionalString(plan, "name");
        }

        return null;
    }

    private static RateWindow ToWindow(JsonElement balance)
    {
        var showName = OptionalString(balance, "show_name") ?? "Plan";
        var total = OptionalLong(balance, "total_units") ?? 0;
        var used = OptionalLong(balance, "used_units") ?? 0;
        var usedPercent = total > 0
            ? Quota.UtilizationToUsedPercent((double)used / total)
            : 0.0;

        long? windowMinutes = null;
        if (string.Equals(OptionalString(balance, "period"), "daily", StringComparison.OrdinalIgnoreCase)
            && OptionalLong(balance, "period_start") is { } start
            && OptionalLong(balance, "period_end") is { } end
            && end > start)
        {
            windowMinutes = (end - start) / 60;
        }

        return new RateWindow
        {
            Label = showName,
            UsedPercent = usedPercent,
            ResetsAt = OptionalLong(balance, "expires_at") is { } expiresAt && expiresAt > 0
                ? DateTimeOffset.FromUnixTimeSeconds(expiresAt).ToString("o")
                : null,
            DetailText = $"{FmtTokens(used)} / {FmtTokens(total)} tokens",
            WindowMinutes = windowMinutes,
        };
    }

    private static string FmtTokens(double units) => units switch
    {
        >= 1_000_000 => $"{units / 1_000_000:0.#}M",
        >= 1_000 => $"{units / 1_000:0.#}K",
        _ => $"{units:0.#}",
    };
}
