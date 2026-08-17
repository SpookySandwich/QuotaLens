using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaLens.Core;
using QuotaLens.Helpers;

namespace QuotaLens.Providers;

/// <summary>
/// codex-lb local load-balancer provider. Reads the summary endpoint for reset and
/// window metadata, then prefers the account endpoint for usable pooled quota math.
/// codex-lb plans can expose 5-hour + weekly, weekly-only, or monthly-only quotas,
/// so aggregate availability is computed from each account's applicable windows.
/// </summary>
public sealed class CodexLbProvider : IProvider
{
    private readonly Func<string, CancellationToken, Task<HttpResponseMessage>> _sendGetAsync;

    public CodexLbProvider()
        : this(SendGetAsync)
    {
    }

    internal CodexLbProvider(Func<string, CancellationToken, Task<HttpResponseMessage>> sendGetAsync)
    {
        _sendGetAsync = sendGetAsync;
    }

    public string Type => "codex-lb";
    public string Name => "codex-lb";
    public string SourceLabel => "codex-lb local API";
    public Confidence Confidence => Confidence.Official;

    private sealed class UsageSummaryResponse
    {
        [JsonPropertyName("primaryWindow")] public UsageWindow? PrimaryWindow { get; set; }
        [JsonPropertyName("secondaryWindow")] public UsageWindow? SecondaryWindow { get; set; }
        [JsonPropertyName("monthlyWindow")] public UsageWindow? MonthlyWindow { get; set; }
        [JsonPropertyName("cost")] public UsageCost? Cost { get; set; }
        [JsonPropertyName("metrics")] public UsageMetrics? Metrics { get; set; }
    }

    private sealed class UsageMetrics
    {
        [JsonPropertyName("tokensSecondaryWindow")] public double? TokensSecondaryWindow { get; set; }
    }

    private sealed class UsageWindow
    {
        [JsonPropertyName("remainingPercent")] public double RemainingPercent { get; set; }
        [JsonPropertyName("capacityCredits")] public double CapacityCredits { get; set; }
        [JsonPropertyName("remainingCredits")] public double RemainingCredits { get; set; }
        [JsonPropertyName("resetAt")] public string? ResetAt { get; set; }
        [JsonPropertyName("windowMinutes")] public long? WindowMinutes { get; set; }
    }

    private sealed class UsageCost
    {
        [JsonPropertyName("currency")] public string Currency { get; set; } = "";
        [JsonPropertyName("totalUsd7d")] public double? TotalUsd7d { get; set; }
    }

    private sealed class AccountsResponse
    {
        [JsonPropertyName("accounts")] public List<AccountUsage>? Accounts { get; set; }
    }

    private sealed class AccountUsage
    {
        [JsonPropertyName("chatgptAccountId")] public string? ChatgptAccountId { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("alias")] public string? Alias { get; set; }
        [JsonPropertyName("workspaceId")] public string? WorkspaceId { get; set; }
        [JsonPropertyName("workspaceLabel")] public string? WorkspaceLabel { get; set; }
        [JsonPropertyName("planType")] public string? PlanType { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("usage")] public AccountUsageWindow? Usage { get; set; }
        [JsonPropertyName("resetAtPrimary")] public string? ResetAtPrimary { get; set; }
        [JsonPropertyName("resetAtSecondary")] public string? ResetAtSecondary { get; set; }
        [JsonPropertyName("resetAtMonthly")] public string? ResetAtMonthly { get; set; }
        [JsonPropertyName("windowMinutesPrimary")] public long? WindowMinutesPrimary { get; set; }
        [JsonPropertyName("windowMinutesSecondary")] public long? WindowMinutesSecondary { get; set; }
        [JsonPropertyName("windowMinutesMonthly")] public long? WindowMinutesMonthly { get; set; }
        [JsonPropertyName("capacityCreditsPrimary")] public double? CapacityCreditsPrimary { get; set; }
        [JsonPropertyName("capacityCreditsSecondary")] public double? CapacityCreditsSecondary { get; set; }
        [JsonPropertyName("capacityCreditsMonthly")] public double? CapacityCreditsMonthly { get; set; }
        [JsonPropertyName("additionalQuotas")] public List<AccountAdditionalQuota>? AdditionalQuotas { get; set; }
        [JsonPropertyName("creditsHas")] public bool? CreditsHas { get; set; }
        [JsonPropertyName("creditsUnlimited")] public bool? CreditsUnlimited { get; set; }
        [JsonPropertyName("creditsBalance")] public double? CreditsBalance { get; set; }
        [JsonPropertyName("lastRefreshAt")] public string? LastRefreshAt { get; set; }
        [JsonPropertyName("isEmailDuplicate")] public bool IsEmailDuplicate { get; set; }
        [JsonPropertyName("availableResetCredits")] public int? AvailableResetCredits { get; set; }
        [JsonPropertyName("resetCreditNearestExpiresAt")] public string? ResetCreditNearestExpiresAt { get; set; }
    }

    private sealed class AccountUsageWindow
    {
        [JsonPropertyName("primaryRemainingPercent")] public double? PrimaryRemainingPercent { get; set; }
        [JsonPropertyName("secondaryRemainingPercent")] public double? SecondaryRemainingPercent { get; set; }
        [JsonPropertyName("monthlyRemainingPercent")] public double? MonthlyRemainingPercent { get; set; }
    }

    private sealed class AccountAdditionalQuota
    {
        [JsonPropertyName("quotaKey")] public string? QuotaKey { get; set; }
        [JsonPropertyName("limitName")] public string? LimitName { get; set; }
        [JsonPropertyName("meteredFeature")] public string? MeteredFeature { get; set; }
        [JsonPropertyName("displayLabel")] public string? DisplayLabel { get; set; }
        [JsonPropertyName("primaryWindow")] public AccountAdditionalWindow? PrimaryWindow { get; set; }
        [JsonPropertyName("secondaryWindow")] public AccountAdditionalWindow? SecondaryWindow { get; set; }
    }

    private sealed class AccountAdditionalWindow
    {
        [JsonPropertyName("usedPercent")] public double UsedPercent { get; set; }
        [JsonPropertyName("resetAt")] public long? ResetAt { get; set; }
        [JsonPropertyName("windowMinutes")] public long? WindowMinutes { get; set; }
    }

    private sealed record ApplicableWindow(
        string Label,
        double RemainingPercent,
        string? ResetAt,
        long? WindowMinutes,
        double? CapacityCredits);

    private sealed record EffectiveQuota(
        double RemainingPercent,
        string? NextIncrementAt,
        List<AccountInfo> Accounts);

    private sealed record AccountExtras(
        BalanceInfo? Balance,
        List<RateWindow> Windows);

    private sealed record DuplicateAccountKey(
        string Email,
        string ChatgptAccountId,
        string? WorkspaceSlot);

    private sealed record AccountQuotaWindow(
        RateWindow Window,
        int AccountNumber);

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        // Rust: config.get("codex_lb_url").unwrap_or("http://127.0.0.1:2455").
        var baseUrl = config.GetScoped(instanceId, "codex_lb_url");
        if (string.IsNullOrEmpty(baseUrl))
            baseUrl = "http://127.0.0.1:2455";

        var url = $"{baseUrl}/api/usage/summary";

        using var resp = await SendGetWithNetworkErrorsAsync(url, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new ProviderException($"Not available: codex-lb returned HTTP {(int)resp.StatusCode} — is it running?");

        UsageSummaryResponse? data;
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            data = await JsonSerializer.DeserializeAsync<UsageSummaryResponse>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Parse error: Invalid response: {e.Message}", e);
        }
        // primaryWindow is non-optional in the Rust struct; absence is a parse failure there.
        if (data?.PrimaryWindow is null)
            throw new ProviderException("Parse error: Invalid response: missing primaryWindow");

        var accounts = await TryFetchAccountsAsync(baseUrl, ct).ConfigureAwait(false);
        var aggregateAccounts = accounts is null ? null : SelectAggregateAccounts(accounts);
        var effective = aggregateAccounts is null ? null : BuildEffectiveQuota(data, aggregateAccounts);
        var extras = aggregateAccounts is null
            ? new AccountExtras(null, new List<RateWindow>())
            : BuildAccountExtras(aggregateAccounts);

        var aggregate = effective ?? BuildSummaryEffectiveQuota(data);
        var effectiveUsed = Quota.UsedPercentFromRemaining(aggregate.RemainingPercent);

        // cost.totalUsd7d is parsed but discarded in the Rust (`_cost7d`).
        _ = data.Cost?.TotalUsd7d;

        return new ProviderSnapshot
        {
            ProviderId = Type,
            Name = Name,
            Primary = new RateWindow
            {
                Label = "Effective Usage",
                UsedPercent = effectiveUsed,
                ResetsAt = aggregate.NextIncrementAt,
                WindowMinutes = null,
            },
            Secondary = null,
            AdditionalWindows = extras.Windows,
            Balance = extras.Balance,
            SourceLabel = SourceLabel,
            Confidence = Confidence,
            UpdatedAt = DateTimeOffset.UtcNow,
            Accounts = aggregate.Accounts,
            MeasuredWeeklyTokensMillions = MeasuredWeeklyTokensFrom(data),
        };
    }

    /// <summary>
    /// The pool's real weekly token capacity, back-computed from codex-lb's measured
    /// consumption: tokens used this weekly window ÷ used fraction of the window's
    /// credits. Requires ≥5% usage — early in a fresh window the division amplifies
    /// noise, and estimates from PlanTokenRules take over instead.
    /// </summary>
    private static double? MeasuredWeeklyTokensFrom(UsageSummaryResponse data)
    {
        var tokensUsed = data.Metrics?.TokensSecondaryWindow;
        var weekly = data.SecondaryWindow;
        if (tokensUsed is not > 0 || weekly is null)
            return null;

        var usedFraction = 1.0 - Math.Clamp(weekly.RemainingPercent, 0, 100) / 100.0;
        if (usedFraction < 0.05)
            return null;

        return tokensUsed.Value / usedFraction / 1_000_000.0;
    }

    private static async Task<HttpResponseMessage> SendGetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        return await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendGetWithNetworkErrorsAsync(string url, CancellationToken ct)
    {
        try
        {
            return await _sendGetAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Network error: codex-lb not reachable: {e.Message}", e);
        }
    }

    private async Task<List<AccountUsage>?> TryFetchAccountsAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var resp = await _sendGetAsync($"{baseUrl}/api/accounts", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var data = await JsonSerializer.DeserializeAsync<AccountsResponse>(stream, cancellationToken: ct).ConfigureAwait(false);
            return data?.Accounts is { Count: > 0 } accounts ? accounts : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static EffectiveQuota? BuildEffectiveQuota(UsageSummaryResponse summary, IReadOnlyList<AccountUsage> accounts)
    {
        var effectiveRemainingCredits = 0.0;
        var effectiveCapacityCredits = 0.0;
        var effectiveAccounts = new List<AccountInfo>();
        var incrementCandidates = new List<IncrementCandidate>();

        foreach (var account in accounts)
        {
            var windows = ApplicableAccountWindows(summary, account);
            if (windows.Count == 0)
                continue;

            var effectiveRemaining = windows.Min(window => window.RemainingPercent);
            // Capacities from differently sized windows are not directly
            // comparable. Normalize the first applicable window to a credit rate
            // so mixed paired, weekly-only, and monthly-only pools stay weighted
            // by the account's actual throughput.
            var aggregationWeight = AggregationWeight(summary, account, windows[0]);
            var displayedCapacity = PositiveOrDefault(
                windows[0].CapacityCredits,
                PositiveOrDefault(account.CapacityCreditsPrimary, 1.0));

            effectiveRemainingCredits += aggregationWeight * effectiveRemaining / 100.0;
            effectiveCapacityCredits += aggregationWeight;

            AddNextIncrementCandidate(incrementCandidates, windows);

            var primary = windows[0];
            var secondary = windows.Count > 1 ? windows[1] : null;

            effectiveAccounts.Add(new AccountInfo
            {
                Email = AccountLabel(account),
                Plan = account.PlanType,
                UsedPercent = Quota.UsedPercentFromRemaining(effectiveRemaining),
                PrimaryLabel = primary.Label,
                PrimaryUsedPercent = Quota.UsedPercentFromRemaining(primary.RemainingPercent),
                PrimaryResetsAt = primary.ResetAt,
                SecondaryLabel = secondary?.Label,
                SecondaryUsedPercent = secondary is null
                    ? null
                    : Quota.UsedPercentFromRemaining(secondary.RemainingPercent),
                SecondaryResetsAt = secondary?.ResetAt,
                CreditsUsed = displayedCapacity * (100.0 - effectiveRemaining) / 100.0,
                CreditsTotal = displayedCapacity,
            });
        }

        if (effectiveCapacityCredits <= 0.0)
            return null;

        var remainingPercent = effectiveRemainingCredits / effectiveCapacityCredits * 100.0;

        return new EffectiveQuota(
            Quota.ClampPercent(remainingPercent),
            EarliestFutureIso(incrementCandidates),
            effectiveAccounts);
    }

    private static EffectiveQuota BuildSummaryEffectiveQuota(UsageSummaryResponse summary)
    {
        var windows = ApplicableSummaryWindows(summary);
        var effectiveRemaining = windows.Min(window => window.RemainingPercent);
        var incrementCandidates = new List<IncrementCandidate>();

        AddNextIncrementCandidate(incrementCandidates, windows);

        return new EffectiveQuota(
            effectiveRemaining,
            EarliestFutureIso(incrementCandidates),
            new List<AccountInfo>());
    }

    private static AccountExtras BuildAccountExtras(IReadOnlyList<AccountUsage> accounts)
    {
        var windows = BuildAdditionalQuotaWindows(accounts);
        var hasUnlimitedCredits = accounts.Any(account => account.CreditsUnlimited == true);
        var finiteBalances = accounts
            .Select(account => account.CreditsBalance)
            .Where(balance => balance is double value && double.IsFinite(value))
            .Select(balance => Math.Max(0.0, balance!.Value))
            .ToArray();

        BalanceInfo? balance = null;
        if (hasUnlimitedCredits)
        {
            windows.Add(InformationalWindow("Credits", "Unlimited"));
        }
        else if (finiteBalances.Length > 0)
        {
            var total = finiteBalances.Sum();
            balance = new BalanceInfo
            {
                Currency = "credits",
                Total = total,
                Paid = 0.0,
                Granted = total,
            };
        }
        else if (accounts.Any(account => account.CreditsHas == true))
        {
            windows.Add(InformationalWindow("Credits", "Available"));
        }

        var resetCreditCount = accounts.Sum(account => (long)Math.Max(0, account.AvailableResetCredits ?? 0));
        if (resetCreditCount > 0)
        {
            windows.Add(InformationalWindow(
                I18n.T("quota.resetCredits"),
                $"{resetCreditCount.ToString("N0", CultureInfo.InvariantCulture)} {I18n.T("common.available")}",
                EarliestDate(accounts
                    .Where(account => account.AvailableResetCredits is > 0)
                    .Select(account => account.ResetCreditNearestExpiresAt))));
        }

        return new AccountExtras(balance, windows);
    }

    private static List<AccountUsage> SelectAggregateAccounts(IReadOnlyList<AccountUsage> accounts)
    {
        var eligible = accounts
            .Where(account => !IsAdministrativelyInactive(account.Status))
            .ToList();
        var duplicateWinners = eligible
            .Select(account => (Account: account, Key: DuplicateKeyFor(account)))
            .Where(candidate => candidate.Key is not null)
            .GroupBy(candidate => candidate.Key!)
            .Select(group => group
                .OrderByDescending(candidate => AccountStatusPriority(candidate.Account.Status))
                .ThenByDescending(candidate => ParsedDate(candidate.Account.LastRefreshAt))
                .First()
                .Account)
            .ToHashSet();

        return eligible
            .Where(account => DuplicateKeyFor(account) is null || duplicateWinners.Contains(account))
            .ToList();
    }

    private static bool IsAdministrativelyInactive(string? status) =>
        status is not null
        && (status.Equals("paused", StringComparison.OrdinalIgnoreCase)
            || status.Equals("reauth_required", StringComparison.OrdinalIgnoreCase)
            || status.Equals("deactivated", StringComparison.OrdinalIgnoreCase));

    private static int AccountStatusPriority(string? status) => status?.ToLowerInvariant() switch
    {
        "active" => 3,
        "rate_limited" => 2,
        "quota_exceeded" => 1,
        _ => 0,
    };

    private static DuplicateAccountKey? DuplicateKeyFor(AccountUsage account)
    {
        if (!account.IsEmailDuplicate)
            return null;

        var email = FirstNonEmpty(account.Email);
        var chatgptAccountId = FirstNonEmpty(account.ChatgptAccountId);
        if (email is null || chatgptAccountId is null)
            return null;

        return new DuplicateAccountKey(
            email,
            chatgptAccountId,
            FirstNonEmpty(account.WorkspaceId) ?? FirstNonEmpty(account.WorkspaceLabel));
    }

    private static DateTimeOffset ParsedDate(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static List<RateWindow> BuildAdditionalQuotaWindows(IReadOnlyList<AccountUsage> accounts)
    {
        var candidates = new List<AccountQuotaWindow>();
        for (var accountIndex = 0; accountIndex < accounts.Count; accountIndex++)
        {
            var account = accounts[accountIndex];
            foreach (var quota in account.AdditionalQuotas ?? Enumerable.Empty<AccountAdditionalQuota>())
            {
                var label = AdditionalQuotaLabel(quota);
                AddAdditionalQuotaWindow(candidates, accountIndex + 1, label, "Primary", quota.PrimaryWindow);
                AddAdditionalQuotaWindow(candidates, accountIndex + 1, label, "Secondary", quota.SecondaryWindow);
            }
        }

        var duplicateLabels = candidates
            .GroupBy(candidate => candidate.Window.Label, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Where(candidate => duplicateLabels.Contains(candidate.Window.Label)))
            candidate.Window.Label += $" · Account {candidate.AccountNumber.ToString(CultureInfo.InvariantCulture)}";

        return candidates.Select(candidate => candidate.Window).ToList();
    }

    private static void AddAdditionalQuotaWindow(
        List<AccountQuotaWindow> windows,
        int accountNumber,
        string label,
        string role,
        AccountAdditionalWindow? window)
    {
        if (window is null)
            return;

        windows.Add(new AccountQuotaWindow(
            new RateWindow
            {
                Label = $"{label} · {AdditionalWindowLabel(role, window.WindowMinutes)}",
                UsedPercent = Quota.ClampPercent(window.UsedPercent),
                ResetsAt = IsoFromUnixSeconds(window.ResetAt),
                WindowMinutes = window.WindowMinutes is > 0 ? window.WindowMinutes : null,
            },
            accountNumber));
    }

    private static string AdditionalQuotaLabel(AccountAdditionalQuota quota)
    {
        var explicitLabel = FirstNonEmpty(quota.DisplayLabel);
        if (explicitLabel is not null)
            return explicitLabel;

        var quotaKey = FirstNonEmpty(quota.QuotaKey);
        if (IsSparkQuota(quotaKey))
            return "GPT-5.3-Codex-Spark";

        var identifier = FirstNonEmpty(quota.LimitName)
            ?? FirstNonEmpty(quota.MeteredFeature)
            ?? quotaKey
            ?? I18n.T("quota.additionalQuota");
        if (IsSparkQuota(identifier))
            return "GPT-5.3-Codex-Spark";

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            identifier.Replace('_', ' ').Trim().ToLowerInvariant());
    }

    private static bool IsSparkQuota(string? value) =>
        value is not null
        && (value.Equals("codex_spark", StringComparison.OrdinalIgnoreCase)
            || value.Equals("codex_other", StringComparison.OrdinalIgnoreCase)
            || value.Equals("gpt-5.3-codex-spark", StringComparison.OrdinalIgnoreCase));

    private static string AdditionalWindowLabel(string role, long? windowMinutes) => windowMinutes switch
    {
        > 0 and <= 6 * 60 => "5h",
        >= 6 * 24 * 60 and < 28 * 24 * 60 => "Weekly",
        >= 28 * 24 * 60 => "Monthly",
        _ => role,
    };

    private static RateWindow InformationalWindow(string label, string value, string? expiresAt = null) => new()
    {
        Label = label,
        Kind = RateWindowKind.Informational,
        Sensitivity = RateWindowSensitivity.Financial,
        ValueText = value,
        ResetsAt = expiresAt,
    };

    private static string? IsoFromUnixSeconds(long? value)
    {
        if (value is not > 0)
            return null;

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value.Value)
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? EarliestDate(IEnumerable<string?> values) => values
        .Select(value => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
                ? parsed
                : (DateTimeOffset?)null)
        .Where(value => value.HasValue)
        .OrderBy(value => value)
        .Select(value => value!.Value.ToString("O", CultureInfo.InvariantCulture))
        .FirstOrDefault();

    private static List<ApplicableWindow> ApplicableAccountWindows(UsageSummaryResponse summary, AccountUsage account)
    {
        var windows = new List<ApplicableWindow>(3);
        var usage = account.Usage;
        if (usage is null)
            return windows;

        AddAccountWindow(
            windows,
            "5h",
            usage.PrimaryRemainingPercent,
            account.ResetAtPrimary,
            account.WindowMinutesPrimary ?? summary.PrimaryWindow?.WindowMinutes,
            account.CapacityCreditsPrimary);
        AddAccountWindow(
            windows,
            "Weekly",
            usage.SecondaryRemainingPercent,
            account.ResetAtSecondary,
            account.WindowMinutesSecondary ?? summary.SecondaryWindow?.WindowMinutes,
            account.CapacityCreditsSecondary);
        AddAccountWindow(
            windows,
            "Monthly",
            usage.MonthlyRemainingPercent,
            account.ResetAtMonthly,
            account.WindowMinutesMonthly ?? summary.MonthlyWindow?.WindowMinutes,
            account.CapacityCreditsMonthly);

        return windows;
    }

    private static void AddAccountWindow(
        List<ApplicableWindow> windows,
        string label,
        double? remainingPercent,
        string? resetAt,
        long? windowMinutes,
        double? capacityCredits)
    {
        if (remainingPercent is not double remaining)
            return;

        windows.Add(new ApplicableWindow(
            label,
            Quota.ClampPercent(remaining),
            resetAt,
            windowMinutes,
            capacityCredits));
    }

    private static List<ApplicableWindow> ApplicableSummaryWindows(UsageSummaryResponse summary)
    {
        var windows = new List<ApplicableWindow>(3);
        AddSummaryWindow(windows, "5h", summary.PrimaryWindow);
        AddSummaryWindow(windows, "Weekly", summary.SecondaryWindow);
        AddSummaryWindow(windows, "Monthly", summary.MonthlyWindow);

        // primaryWindow is required by codex-lb. Preserve it as a last-resort
        // fallback for older responses that do not provide capacity metadata.
        if (windows.Count == 0 && summary.PrimaryWindow is { } primary)
        {
            windows.Add(ToApplicableWindow("5h", primary));
        }

        return windows;
    }

    private static void AddSummaryWindow(List<ApplicableWindow> windows, string label, UsageWindow? window)
    {
        if (window is null || !HasQuotaData(window))
            return;

        windows.Add(ToApplicableWindow(label, window));
    }

    private static ApplicableWindow ToApplicableWindow(string label, UsageWindow window) =>
        new(
            label,
            Quota.ClampPercent(window.RemainingPercent),
            window.ResetAt,
            window.WindowMinutes,
            window.CapacityCredits);

    private static bool HasQuotaData(UsageWindow window) =>
        PositiveFinite(window.CapacityCredits)
        || PositiveFinite(window.RemainingCredits)
        || PositiveFinite(window.RemainingPercent)
        || !string.IsNullOrWhiteSpace(window.ResetAt);

    private static double AggregationWeight(
        UsageSummaryResponse summary,
        AccountUsage account,
        ApplicableWindow firstApplicableWindow)
    {
        var applicableWindowMinutes = firstApplicableWindow.WindowMinutes
            ?? DefaultWindowMinutes(firstApplicableWindow.Label);
        if (firstApplicableWindow.CapacityCredits is double capacity
            && PositiveFinite(capacity)
            && applicableWindowMinutes > 0)
        {
            return capacity / applicableWindowMinutes;
        }

        var primaryWindowMinutes = account.WindowMinutesPrimary
            ?? summary.PrimaryWindow?.WindowMinutes
            ?? DefaultWindowMinutes("5h");
        if (account.CapacityCreditsPrimary is double primaryCapacity
            && PositiveFinite(primaryCapacity)
            && primaryWindowMinutes > 0)
        {
            return primaryCapacity / primaryWindowMinutes;
        }

        return 1.0;
    }

    private static long DefaultWindowMinutes(string label) => label switch
    {
        "5h" => 300,
        "Weekly" => 10_080,
        "Monthly" => 43_200,
        _ => 1,
    };

    private static void AddNextIncrementCandidate(
        List<IncrementCandidate> candidates,
        IReadOnlyList<ApplicableWindow> windows)
    {
        var resetEvents = new List<WindowResetCandidate>(windows.Count);
        for (var index = 0; index < windows.Count; index++)
        {
            var candidate = ResolveFutureReset(windows[index].ResetAt, windows[index].WindowMinutes);
            if (candidate is not null)
                resetEvents.Add(new WindowResetCandidate(index, candidate));
        }

        if (resetEvents.Count == 0)
            return;

        var simulatedRemaining = windows
            .Select(window => window.RemainingPercent)
            .ToArray();
        var currentEffectiveRemaining = simulatedRemaining.Min();

        foreach (var resetGroup in resetEvents
                     .OrderBy(resetEvent => resetEvent.Candidate.When)
                     .GroupBy(resetEvent => resetEvent.Candidate.When))
        {
            foreach (var resetEvent in resetGroup)
                simulatedRemaining[resetEvent.WindowIndex] = 100.0;

            if (simulatedRemaining.Min() > currentEffectiveRemaining)
            {
                candidates.Add(resetGroup.First().Candidate);
                return;
            }
        }
    }

    private static IncrementCandidate? ResolveFutureReset(string? iso, long? windowMinutes)
    {
        if (string.IsNullOrWhiteSpace(iso)
            || !DateTimeOffset.TryParse(
                iso,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var when))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (when > now)
            return new IncrementCandidate(iso, when);

        if (!windowMinutes.HasValue || windowMinutes.Value <= 0)
            return null;

        var window = TimeSpan.FromMinutes(windowMinutes.Value);
        var elapsedWindows = Math.Floor((now - when).TotalMinutes / window.TotalMinutes) + 1;
        var rolled = when + TimeSpan.FromTicks((long)elapsedWindows * window.Ticks);
        return rolled > now
            ? new IncrementCandidate(rolled.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), rolled)
            : null;
    }

    private static string? EarliestFutureIso(IEnumerable<IncrementCandidate> candidates) =>
        candidates
            .OrderBy(candidate => candidate.When)
            .Select(candidate => candidate.Iso)
            .FirstOrDefault();

    private static double PositiveOrDefault(double? value, double fallback) =>
        value is double finite && PositiveFinite(finite) ? finite : fallback;

    private static bool PositiveFinite(double value) =>
        value > 0.0 && double.IsFinite(value);

    private sealed record IncrementCandidate(string Iso, DateTimeOffset When);
    private sealed record WindowResetCandidate(int WindowIndex, IncrementCandidate Candidate);

    private static string? AccountLabel(AccountUsage account) =>
        FirstNonEmpty(account.DisplayName) ?? FirstNonEmpty(account.Email) ?? FirstNonEmpty(account.Alias);

    private static string? FirstNonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
