using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaLens.Core;
using QuotaLens.Helpers;
using static QuotaLens.Core.StringValues;

namespace QuotaLens.Providers;

/// <summary>
/// Qoder CLI usage provider. It talks to qodercli through the same stream-json
/// control protocol used by Qoder's official TypeScript SDK and asks for the
/// get_usage_info quota payload.
/// </summary>
public sealed class QoderProvider : IProvider
{
    public string Type => "qoder";
    public string Name => "Qoder";
    public string SourceLabel => "qodercli usage";
    public Confidence Confidence => Confidence.Official;

    private const string TokenEnvName = "QODER_PERSONAL_ACCESS_TOKEN";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var binary = ResolveQoderCliPath(instanceId, config);
        var token = ProviderConfig.Resolve(instanceId, config, "qoder", "qoder_token");

        // Shared launch path: resolves .cmd/.ps1 shims instead of a bare CreateProcess.
        var psi = HiddenCliProcess.CreateStartInfo(binary, new[] { "--output-format", "stream-json", "--input-format", "stream-json" });
        psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        psi.Environment["QODER_ENTRYPOINT"] = "sdk-ts";
        psi.Environment["QODERCLI_INTEGRATION_MODE"] = "qoder_work";
        psi.Environment["NCS_AUTH_AGENT_ID"] = "qoder-work";
        if (!string.IsNullOrWhiteSpace(token))
            psi.Environment[TokenEnvName] = token;

        using var process = new Process { StartInfo = psi };
        try
        {
            try
            {
                process.Start();
            }
            catch (Exception e)
            {
                throw new ProviderException($"Not available: Cannot launch qodercli at {binary}: {e.Message}", e);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Timeout);

            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await SendControlRequestAsync(process, "req_ql_init", new
            {
                subtype = "initialize",
                hooks = (object?)null,
            }, timeoutCts.Token).ConfigureAwait(false);

            await SendControlRequestAsync(process, "req_ql_usage", new
            {
                type = "get_usage_info",
            }, timeoutCts.Token).ConfigureAwait(false);

            QoderStatusData? status = null;
            QoderUsageData? usage = null;

            while (!timeoutCts.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    TryKillTree(process);
                    throw new ProviderException("Timeout");
                }

                if (line is null)
                    break;

                var response = ParseControlResponse(line);
                if (response is null)
                    continue;

                if (response.Error is { } error)
                    throw new ProviderException($"Not available: {error}");

                if (response.RequestId == "req_ql_init")
                    status = ParseInitializeResponse(response.Response);
                else if (response.RequestId == "req_ql_usage")
                {
                    usage = ParseUsageResponse(response.Response);
                    break;
                }
            }

            if (usage is null)
            {
                TryKillTree(process);
                var stderr = await ReadCompletedOrEmptyAsync(stderrTask).ConfigureAwait(false);
                throw new ProviderException(FirstNonEmpty(stderr, "Parse error: qodercli usage response not found")!);
            }

            return BuildSnapshot(usage, status);
        }
        finally
        {
            TryKillTree(process);
        }
    }

    internal static ProviderSnapshot BuildSnapshot(QoderUsageData usage, QoderStatusData? status = null)
    {
        var quota = usage.UserQuota ?? throw new ProviderException("Parse error: qodercli usage response missing userQuota");
        var buckets = UsageBuckets(usage).ToList();
        var unit = FirstNonEmpty(buckets.Select(bucket => bucket.Unit).ToArray()) ?? "credits";
        var compatibleBuckets = buckets
            .Where(bucket => string.Equals(bucket.Unit, unit, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var total = compatibleBuckets.Sum(bucket => bucket.Total);
        var used = compatibleBuckets.Sum(bucket => bucket.Used);
        var remaining = compatibleBuckets.Sum(bucket => bucket.Remaining);
        var aggregate = new UsageBucket(
            compatibleBuckets.Count > 1 ? "Total Credits" : compatibleBuckets[0].Label,
            total,
            used,
            remaining,
            quota.Percentage,
            unit);
        var plan = FirstNonEmpty(status?.Plan, UserTypeToPlan(usage.UserType));
        var resetsAt = ResetTimeOrNull(usage.ExpiresAt);
        var showBreakdown = buckets.Count > 1;

        return new ProviderSnapshot
        {
            ProviderId = "qoder",
            Name = "Qoder",
            PlanName = plan,
            Primary = ToRateWindow(aggregate, resetsAt, usage.IsQuotaExceeded, windowMinutes: 30 * 24 * 60),
            AdditionalWindows = showBreakdown
                ? buckets.Select(bucket => ToRateWindow(bucket, resetsAt: null, forceExhausted: false)).ToList()
                : new List<RateWindow>(),
            Balance = new BalanceInfo
            {
                Currency = unit,
                Total = remaining,
                Paid = used,
                Granted = total,
            },
            SourceLabel = "qodercli usage",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
            Error = null,
        };
    }

    private static IEnumerable<UsageBucket> UsageBuckets(QoderUsageData usage)
    {
        if (usage.UserQuota is { } userQuota)
            yield return UsageBucket.FromQuota(usage.UserQuotaLabel, userQuota);

        if (usage.AddOnQuota is { } addOnQuota && addOnQuota.HasCapacity)
            yield return UsageBucket.FromQuota(usage.AddOnQuotaLabel, addOnQuota);

        if (usage.OrgResourcePackage is { Available: true } orgQuota && orgQuota.HasCapacity)
            yield return UsageBucket.FromOrgPackage("Organization Credits", orgQuota);
    }

    private static RateWindow ToRateWindow(
        UsageBucket bucket,
        string? resetsAt,
        bool forceExhausted,
        long? windowMinutes = null)
    {
        var hasCapacity = bucket.Total > 0.0 || bucket.Remaining > 0.0;
        var usedPercent = forceExhausted || !hasCapacity
            ? 100.0
            : bucket.Total > 0.0
                ? Quota.UtilizationToUsedPercent(bucket.Used / bucket.Total)
                : Quota.ClampPercent(bucket.Percentage);

        return new RateWindow
        {
            Label = bucket.Label,
            UsedPercent = usedPercent,
            ResetsAt = resetsAt,
            DetailText = $"{Fmt0(bucket.Used)}/{Fmt0(bucket.Total)} {bucket.Unit} ({Fmt0(bucket.Remaining)} left)",
            WindowMinutes = windowMinutes,
            CountsForAvailability = false,
        };
    }

    private static string? ResetTimeOrNull(long unixMilliseconds)
    {
        if (unixMilliseconds <= 0)
            return null;

        try
        {
            var reset = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
            return reset <= DateTimeOffset.UtcNow.AddYears(20)
                ? reset.ToString("O")
                : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static async Task SendControlRequestAsync(Process process, string requestId, object request, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = requestId,
            request,
        });
        await process.StandardInput.WriteLineAsync(payload.AsMemory(), ct).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    private static QoderControlResponse? ParseControlResponse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "control_response")
                return null;
            var response = root.GetProperty("response");
            var requestId = response.GetProperty("request_id").GetString() ?? "";
            if (response.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "error")
            {
                var error = response.TryGetProperty("error", out var e) ? e.GetString() : "Unknown qodercli control error";
                return new QoderControlResponse(requestId, default, error);
            }
            if (!response.TryGetProperty("response", out var inner))
                return null;
            return new QoderControlResponse(requestId, inner.Clone(), null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static QoderStatusData? ParseInitializeResponse(JsonElement element)
    {
        var account = GetObject(element, "account");
        if (account is null)
            return null;

        return new QoderStatusData
        {
            Username = GetString(account.Value, "name", "username", "userName"),
            Email = GetString(account.Value, "email"),
            UserType = GetString(account.Value, "subscriptionType", "subscription_type", "userType", "user_type"),
            Plan = UserTypeToPlan(GetString(account.Value, "subscriptionType", "subscription_type", "userType", "user_type")),
        };
    }

    private static QoderStatusData? ParseStatusResponse(JsonElement element)
    {
        if (!OperationSucceeded(element))
            return null;

        var data = OperationData(element);
        if (data is null)
            return null;

        return new QoderStatusData
        {
            LoggedIn = GetBool(data.Value, "logged_in", "loggedIn"),
            Username = GetString(data.Value, "username", "userName"),
            Email = GetString(data.Value, "email"),
            UserType = GetString(data.Value, "user_type", "userType"),
            Plan = GetString(data.Value, "plan"),
            Version = GetString(data.Value, "version"),
        };
    }

    internal static QoderUsageData ParseUsageResponse(JsonElement element)
    {
        if (!OperationSucceeded(element))
            throw new ProviderException($"Not available: {FirstNonEmpty(GetString(element, "error", "message"), "qodercli usage request failed")}");

        JsonElement? rootUsage = GetObject(element, "userQuota", "user_quota", "totalQuota", "total_quota") is not null
            ? element
            : null;
        var data = OperationData(element)
            ?? GetObject(element, "usage")
            ?? rootUsage
            ?? throw new ProviderException("Parse error: qodercli usage response missing data");

        var quotaRoot = GetObject(data, "userQuota", "user_quota", "quota", "credits");
        var totalQuota = GetObject(data, "totalQuota", "total_quota");
        var totalSummary = totalQuota is null
            ? null
            : GetObject(totalQuota.Value, "quotaSummary", "quota_summary");
        var parsedQuota = ParseQuotaBucket(quotaRoot ?? totalSummary)
            ?? throw new ProviderException("Parse error: qodercli usage response missing userQuota");

        var sharedQuota = GetObject(data, "sharedQuota", "shared_quota");
        var sharedSummary = sharedQuota is null
            ? null
            : GetObject(sharedQuota.Value, "quotaSummary", "quota_summary");
        var isWebQuotaShape = totalSummary is not null;

        return new QoderUsageData
        {
            UserId = GetString(data, "userId", "user_id", "id"),
            UserType = GetString(data, "userType", "user_type"),
            TotalUsagePercentage = isWebQuotaShape
                ? parsedQuota.Percentage
                : GetDouble(data, "totalUsagePercentage", "total_usage_percentage", "percentage", "usagePercentage"),
            ExpiresAt = GetTimestampMilliseconds(data, "expiresAt", "expires_at", "resetAt", "reset_at", "nextResetAt", "next_reset_at"),
            IsQuotaExceeded = GetBool(data, "isQuotaExceeded", "is_quota_exceeded"),
            IsPlanQuotaProrated = GetBool(data, "isPlanQuotaProrated", "is_plan_quota_prorated"),
            UserQuota = parsedQuota,
            UserQuotaLabel = isWebQuotaShape ? "Plan + Resource Credits" : "Plan Credits",
            AddOnQuota = isWebQuotaShape
                ? ParseQuotaBucket(sharedSummary)
                : ParseQuotaBucket(GetObject(data, "addOnQuota", "add_on_quota", "addonQuota", "addon_quota")),
            AddOnQuotaLabel = isWebQuotaShape ? I18n.T("quota.sharedAddonCredits") : I18n.T("quota.addonCredits"),
            OrgResourcePackage = ParseOrgResourcePackage(GetObject(data, "orgResourcePackage", "org_resource_package")),
        };
    }

    private static QoderQuota? ParseQuotaBucket(JsonElement? element)
    {
        if (element is null)
            return null;

        var total = GetDouble(element.Value, "total", "limit", "quota", "limitValue", "limit_value");
        var used = GetDouble(element.Value, "used", "usage", "consumed", "usedValue", "used_value");
        var remaining = GetOptionalDouble(
            element.Value,
            "remaining",
            "left",
            "available",
            "remainingValue",
            "remaining_value") ?? Math.Max(0.0, total - used);

        return new QoderQuota
        {
            Total = total,
            Used = used,
            Remaining = remaining,
            Percentage = GetDouble(element.Value, "percentage", "usedPercentage", "used_percentage", "usagePercentage", "usage_percentage"),
            Unit = GetString(element.Value, "unit", "currency") ?? "credits",
            DetailUrl = GetString(element.Value, "detailUrl", "detail_url"),
        };
    }

    private static QoderOrgResourcePackage? ParseOrgResourcePackage(JsonElement? element)
    {
        if (element is null)
            return null;

        return new QoderOrgResourcePackage
        {
            Cap = GetDouble(element.Value, "cap", "total", "limit", "quota"),
            Used = GetDouble(element.Value, "used", "usage", "consumed"),
            Remaining = GetDouble(element.Value, "remaining", "left", "available"),
            Percentage = GetDouble(element.Value, "percentage", "usedPercentage", "used_percentage"),
            Available = GetBool(element.Value, "available"),
            Unit = GetString(element.Value, "unit", "currency") ?? "credits",
        };
    }

    private static string ResolveQoderCliPath(string instanceId, IConfig config)
    {
        var configured = ProviderConfig.Resolve(instanceId, config, "qoder", "qoder_cli_path");
        if (!string.IsNullOrWhiteSpace(configured))
            return Environment.ExpandEnvironmentVariables(configured);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var defaultPath = Path.Combine(programFiles, "QoderWork", "QoderWork", "resources", "bin", "qodercli.exe");
        return File.Exists(defaultPath) ? defaultPath : "qodercli";
    }

    private static string? UserTypeToPlan(string? userType)
    {
        if (string.IsNullOrWhiteSpace(userType))
            return null;
        return userType.Replace("personal_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_", " ", StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant() switch
            {
                "professional trial" => "Pro Trial",
                "professional" => "Pro",
                var value => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value),
            };
    }



    private static string Fmt0(double v) => v.ToString("F0", CultureInfo.InvariantCulture);

    private static async Task<string> ReadCompletedOrEmptyAsync(Task<string> task)
    {
        try { return task.IsCompleted ? await task.ConfigureAwait(false) : ""; }
        catch { return ""; }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static bool OperationSucceeded(JsonElement element)
    {
        if (TryGetProperty(element, "success", out var success))
            return success.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => string.Equals(success.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };

        return true;
    }

    private static JsonElement? OperationData(JsonElement element)
    {
        if (TryGetProperty(element, "data", out var data))
            return data;
        if (TryGetProperty(element, "result", out var result))
            return result;
        return null;
    }

    private static JsonElement? GetObject(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Object)
                return value;
        }

        return null;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.ToString();
        }

        return null;
    }

    private static bool GetBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
            if (value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return false;
    }

    private static double GetDouble(JsonElement element, params string[] names)
        => GetOptionalDouble(element, names) ?? 0;

    private static double? GetOptionalDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                return number;
            if (double.TryParse(value.ToString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return null;
    }

    private static long GetTimestampMilliseconds(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;

            if ((value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                || long.TryParse(value.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return number is > -10_000_000_000 and < 10_000_000_000
                    ? number * 1000
                    : number;
            }

            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    value.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsedDate))
            {
                return parsedDate.ToUnixTimeMilliseconds();
            }
        }

        return 0;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed record QoderControlResponse(string RequestId, JsonElement Response, string? Error);

    private sealed record UsageBucket(
        string Label,
        double Total,
        double Used,
        double Remaining,
        double Percentage,
        string Unit)
    {
        public static UsageBucket FromQuota(string label, QoderQuota quota) =>
            new(
                label,
                quota.Total,
                quota.Used,
                quota.Remaining,
                quota.Percentage,
                string.IsNullOrWhiteSpace(quota.Unit) ? "credits" : quota.Unit);

        public static UsageBucket FromOrgPackage(string label, QoderOrgResourcePackage quota) =>
            new(
                label,
                quota.Cap,
                quota.Used,
                quota.Remaining,
                quota.Percentage,
                string.IsNullOrWhiteSpace(quota.Unit) ? "credits" : quota.Unit);
    }

    internal sealed class QoderStatusData
    {
        [JsonPropertyName("logged_in")] public bool LoggedIn { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("user_type")] public string? UserType { get; set; }
        [JsonPropertyName("plan")] public string? Plan { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
    }

    internal sealed class QoderUsageData
    {
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("userType")] public string? UserType { get; set; }
        [JsonPropertyName("totalUsagePercentage")] public double TotalUsagePercentage { get; set; }
        [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
        [JsonPropertyName("isQuotaExceeded")] public bool IsQuotaExceeded { get; set; }
        [JsonPropertyName("isPlanQuotaProrated")] public bool IsPlanQuotaProrated { get; set; }
        [JsonPropertyName("userQuota")] public QoderQuota? UserQuota { get; set; }
        public string UserQuotaLabel { get; set; } = "Plan Credits";
        [JsonPropertyName("addOnQuota")] public QoderQuota? AddOnQuota { get; set; }
        public string AddOnQuotaLabel { get; set; } = I18n.T("quota.addonCredits");
        [JsonPropertyName("orgResourcePackage")] public QoderOrgResourcePackage? OrgResourcePackage { get; set; }
    }

    internal sealed class QoderQuota
    {
        [JsonPropertyName("total")] public double Total { get; set; }
        [JsonPropertyName("used")] public double Used { get; set; }
        [JsonPropertyName("remaining")] public double Remaining { get; set; }
        [JsonPropertyName("percentage")] public double Percentage { get; set; }
        [JsonPropertyName("unit")] public string? Unit { get; set; }
        [JsonPropertyName("detailUrl")] public string? DetailUrl { get; set; }

        public bool HasCapacity => Total > 0 || Used > 0 || Remaining > 0;
    }

    internal sealed class QoderOrgResourcePackage
    {
        [JsonPropertyName("used")] public double Used { get; set; }
        [JsonPropertyName("cap")] public double Cap { get; set; }
        [JsonPropertyName("remaining")] public double Remaining { get; set; }
        [JsonPropertyName("percentage")] public double Percentage { get; set; }
        [JsonPropertyName("available")] public bool Available { get; set; }
        [JsonPropertyName("unit")] public string? Unit { get; set; }

        public bool HasCapacity => Cap > 0 || Used > 0 || Remaining > 0;
    }
}
