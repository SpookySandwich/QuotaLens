using System.Net.Http;
using System.Text.Json;
using QuotaLens.Core;
using QuotaLens.Services;
using static QuotaLens.Core.JsonUtil;

namespace QuotaLens.Providers;

/// <summary>
/// ZCode Coding Plan provider. Reads the local session (~/.zcode/v2/credentials.json)
/// and queries the official ZCode coding-plan balance endpoint:
/// GET https://zcode.z.ai/api/v1/zcode-plan/billing/balance.
/// </summary>
public sealed class ZcodeProvider : IProvider
{
    private const string BalanceEndpoint = "https://zcode.z.ai/api/v1/zcode-plan/billing/balance";
    private static readonly TimeSpan BalanceTimeout = TimeSpan.FromSeconds(15);

    private readonly IReadOnlyList<IProviderSource> _sources;

    public ZcodeProvider()
        : this(ZcodeCredentials.HasSession, SendBalanceAsync, ZcodeCredentials.TryReadSessionToken)
    {
    }

    internal ZcodeProvider(
        Func<bool> cliIsAvailable,
        Func<string, CancellationToken, Task<HttpResponseMessage>> sendBalanceAsync,
        Func<string?>? readSessionToken = null)
    {
        var readToken = readSessionToken ?? ZcodeCredentials.TryReadSessionToken;
        var recovery = new ProviderRecoveryAction(
            ProviderRecoveryKind.LaunchApp,
            "zcode.cliSourceNote");

        _sources = new IProviderSource[]
        {
            new ProviderSource(
                ProviderSourceMode.Cli,
                (_, _) => cliIsAvailable(),
                (_, _, ct) => FetchZcodeAsync(readToken, sendBalanceAsync, ct),
                configFieldKeys: new[] { "zcode_home", "zcode_app_path" },
                legacyConfigValues: new[] { "zai_home", "zai_app_path" },
                attentionNote: "zcode.cliSourceNote",
                unavailableRecovery: recovery,
                connectionAction: new AppProviderConnectionAction(
                    "zcode",
                    "zcode_app_path",
                    cliIsAvailable,
                    verificationFieldKeys: new[] { "zcode_home", "zcode_app_path" }),
                launchAction: new AppProviderLaunchAction("zcode"),
                watchPaths: (instanceId, config) =>
                    new[] { ZcodeCredentials.StorePath(config, instanceId) }),
        };
    }

    public string Type => "zcode";
    public string Name => "ZCode";
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
            throw new ProviderException(
                "Not available: this z.ai account has no active ZCode plan. Buy a coding plan to track ZCode tokens.",
                ProviderErrorKind.Unsupported);

        var ordered = balances
            .OrderByDescending(entry => entry.Priority)
            .Select(entry => entry.Balance)
            .ToList();

        var planName = PlanNameFor(data, ordered[0]);

        return new ProviderSnapshot
        {
            ProviderId = "zcode",
            Name = "ZCode",
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
