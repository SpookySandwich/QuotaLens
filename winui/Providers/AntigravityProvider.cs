using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>
/// Antigravity local read-only provider.
///
/// Discovery: find the running bundled `language_server.exe` (via PowerShell Get-CimInstance,
/// matching the Rust), extract the --csrf_token from its command line, find its LISTENING
/// localhost port(s) (via `netstat -ano | findstr`), then probe each port over HTTPS
/// (self-signed cert) using the Connect-RPC protocol. Refresh never starts Antigravity or its
/// language server; users retain control over application lifecycle.
///
/// Data fetch: POST GetUserStatus, parse plan/prompt credits + per-model quotas into a
/// ProviderSnapshot with Claude/Gemini/Other family grouping.
/// </summary>
public sealed class AntigravityProvider : IProvider
{
    public string Type => "antigravity";
    public string Name => "Antigravity";
    public string SourceLabel => "Antigravity local probe";
    public Confidence Confidence => Confidence.SemiOfficial;

    /// <summary>Lightweight availability probe: is the bundled language server running?</summary>
    internal static bool IsRunning()
    {
        try { return Process.GetProcessesByName("language_server").Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// Dedicated HttpClient that accepts the self-signed localhost cert. NOT the shared
    /// Http.Client. Mirrors the Rust reqwest client built with danger_accept_invalid_certs(true)
    /// and a 15s default timeout. Per-request timeouts are applied via linked CancellationTokens.
    /// </summary>
    private static readonly HttpClient ProbeClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private const string ProbeBody =
        "{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\",\"ideVersion\":\"unknown\"}";

    private const string QuotaSummaryPath =
        "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";

    private const string UserStatusPath =
        "/exa.language_server_pb.LanguageServerService/GetUserStatus";

    private static readonly Regex PidRegex = new(@"\bProcessId\s*:\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CsrfRegex = new(@"--csrf_token\s+([a-zA-Z0-9_-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ListenRegex = new(@"TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ---- Response shapes (serde names verbatim) ----

    private sealed class GetUserStatusResponse
    {
        [JsonPropertyName("userStatus")] public UserStatus? UserStatus { get; set; }
    }

    private sealed class UserStatus
    {
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("userTier")] public UserTier? UserTier { get; set; }
        [JsonPropertyName("planStatus")] public PlanStatus? PlanStatus { get; set; }
        [JsonPropertyName("cascadeModelConfigData")] public CascadeModelConfigData? CascadeModelConfigData { get; set; }
    }

    private sealed class UserTier
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    }

    private sealed class PlanStatus
    {
        [JsonPropertyName("planInfo")] public PlanInfo? PlanInfo { get; set; }
        [JsonPropertyName("availablePromptCredits")] public long? AvailablePromptCredits { get; set; }
    }

    private sealed class PlanInfo
    {
        [JsonPropertyName("planName")] public string? PlanName { get; set; }
        [JsonPropertyName("monthlyPromptCredits")] public long? MonthlyPromptCredits { get; set; }
    }

    private sealed class CascadeModelConfigData
    {
        [JsonPropertyName("clientModelConfigs")] public List<ModelConfig> ClientModelConfigs { get; set; } = new();
    }

    private sealed class ModelConfig
    {
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("modelId")] public string? ModelId { get; set; }
        [JsonPropertyName("windowType")] public string? WindowType { get; set; }
        [JsonPropertyName("quotaInfo")] public QuotaInfo? QuotaInfo { get; set; }
    }

    private sealed class QuotaInfo
    {
        [JsonPropertyName("remainingFraction")] public double? RemainingFraction { get; set; }
        [JsonPropertyName("resetTime")] public string? ResetTime { get; set; }
        [JsonPropertyName("windowType")] public string? WindowType { get; set; }
        [JsonPropertyName("group")] public string? Group { get; set; }
    }

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        AppLog.Info($"antigravity: discovering language server for {instanceId}...");
        var (port, csrf) = await DiscoverAsync(ct).ConfigureAwait(false);
        AppLog.Info($"antigravity: discovered port={port}");

        ProviderSnapshot? summary = null;
        try
        {
            AppLog.Info($"antigravity: requesting quota summary from port {port}...");
            var summaryJson = await SendLocalRequestAsync(port, csrf, QuotaSummaryPath, ct).ConfigureAwait(false);
            summary = ParseQuotaSummary(instanceId, summaryJson, DateTimeOffset.UtcNow);
            AppLog.Info($"antigravity: successfully parsed quota summary for {instanceId}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderException e)
        {
            AppLog.Warn($"antigravity: quota summary request failed: {e.Message}");
        }

        try
        {
            AppLog.Info($"antigravity: requesting user status from port {port}...");
            var statusJson = await SendLocalRequestAsync(port, csrf, UserStatusPath, ct).ConfigureAwait(false);
            var legacy = ParseSnapshot(instanceId, statusJson, DateTimeOffset.UtcNow);
            AppLog.Info($"antigravity: successfully parsed user status for {instanceId}");
            return summary is null ? legacy : MergeIdentity(summary, legacy);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderException e) when (summary is not null)
        {
            AppLog.Warn($"antigravity: user status request failed, using summary: {e.Message}");
            return summary;
        }
    }

    private static async Task<string> SendLocalRequestAsync(
        int port,
        string csrf,
        string path,
        CancellationToken ct)
    {
        var url = $"https://127.0.0.1:{port}{path}";
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(ProbeBody, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrf);
            req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            using var response = await ProbeClient
                .SendAsync(req, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderException(
                    $"Network error: Antigravity local service returned HTTP {(int)response.StatusCode}");
            }

            return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not ProviderException)
        {
            throw new ProviderException($"Network error: Antigravity local request failed: {e.Message}", e);
        }
    }

    internal static ProviderSnapshot ParseQuotaSummary(
        string instanceId,
        string json,
        DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var payload = FirstObject(root, "response", "summary") ?? root;
            if (!payload.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                throw new ProviderException("Parse error: Invalid Antigravity quota summary: missing groups");

            var windows = new List<(int GroupRank, int CadenceRank, RateWindow Window)>();
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object)
                    continue;

                var groupName = StringValue(group, "displayName") ?? "Quota";
                if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var bucket in buckets.EnumerateArray())
                {
                    if (bucket.ValueKind != JsonValueKind.Object
                        || BoolValue(bucket, "disabled") == true
                        || RemainingFraction(bucket) is not { } remainingFraction)
                    {
                        continue;
                    }

                    var bucketId = StringValue(bucket, "bucketId")?.Trim();
                    if (string.IsNullOrWhiteSpace(bucketId))
                        continue;

                    var displayName = StringValue(bucket, "displayName") ?? bucketId;
                    var family = QuotaFamily(groupName);
                    var cadence = QuotaCadence(bucketId, displayName);
                    var label = cadence switch
                    {
                        QuotaCadenceKind.FiveHour => $"{family} 5-hour",
                        QuotaCadenceKind.Weekly => $"{family} weekly",
                        _ => $"{family} {displayName}",
                    };
                    var reset = StringValue(bucket, "resetTime");
                    windows.Add((
                        GroupRank(family),
                        cadence switch
                        {
                            QuotaCadenceKind.Weekly => 0,
                            QuotaCadenceKind.FiveHour => 1,
                            _ => 2,
                        },
                        new RateWindow
                        {
                            Label = label,
                            AvailabilityGroup = family,
                            UsedPercent = Quota.UsedPercentFromRemaining(
                                Quota.ClampPercent(remainingFraction * 100)),
                            ResetsAt = reset,
                            WindowMinutes = cadence switch
                            {
                                QuotaCadenceKind.FiveHour => 5 * 60,
                                QuotaCadenceKind.Weekly => 7 * 24 * 60,
                                _ => null,
                            },
                        }));
                }
            }

            var ordered = windows
                .OrderBy(item => item.GroupRank)
                .ThenBy(item => item.CadenceRank)
                .Select(item => item.Window)
                .ToList();
            if (ordered.Count == 0)
                throw new ProviderException("Parse error: Invalid Antigravity quota summary: no usable buckets");

            return new ProviderSnapshot
            {
                ProviderId = instanceId,
                Name = "Antigravity",
                Primary = ordered[0],
                Secondary = ordered.ElementAtOrDefault(1),
                Tertiary = ordered.ElementAtOrDefault(2),
                AdditionalWindows = ordered.Skip(3).ToList(),
                UpdatedAt = now,
                SourceLabel = "Antigravity local quota summary",
                Confidence = Confidence.SemiOfficial,
            };
        }
        catch (JsonException e)
        {
            throw new ProviderException($"Parse error: Invalid Antigravity quota summary: {e.Message}", e);
        }
    }

    private static ProviderSnapshot MergeIdentity(ProviderSnapshot summary, ProviderSnapshot identity)
    {
        summary.Name = identity.Name;
        summary.PlanId = identity.PlanId;
        summary.PlanName = identity.PlanName;
        summary.EntitlementStatus = identity.EntitlementStatus;
        summary.Accounts = identity.Accounts;
        return summary;
    }

    private static JsonElement? FirstObject(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
        }

        return null;
    }

    private static string? StringValue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? BoolValue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static double? RemainingFraction(JsonElement bucket)
    {
        if (bucket.TryGetProperty("remainingFraction", out var direct)
            && direct.ValueKind == JsonValueKind.Number
            && direct.TryGetDouble(out var directValue))
        {
            return directValue;
        }

        if (!bucket.TryGetProperty("remaining", out var remaining)
            || remaining.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (remaining.TryGetProperty("remainingFraction", out var nested)
            && nested.ValueKind == JsonValueKind.Number
            && nested.TryGetDouble(out var nestedValue))
        {
            return nestedValue;
        }

        return StringValue(remaining, "case") == "remainingFraction"
            && remaining.TryGetProperty("value", out var oneOf)
            && oneOf.ValueKind == JsonValueKind.Number
            && oneOf.TryGetDouble(out var oneOfValue)
                ? oneOfValue
                : null;
    }

    internal static string QuotaFamily(string groupName)
    {
        if (groupName.Contains("gemini", StringComparison.OrdinalIgnoreCase))
            return "Gemini";
        if (groupName.Contains("claude", StringComparison.OrdinalIgnoreCase)
            || groupName.Contains("gpt", StringComparison.OrdinalIgnoreCase)
            || groupName.Contains("3p", StringComparison.OrdinalIgnoreCase))
        {
            return "Claude/GPT";
        }

        return string.IsNullOrWhiteSpace(groupName) ? "Quota" : groupName.Trim();
    }

    internal static int GroupRank(string family) => family switch
    {
        "Gemini" => 0,
        "Claude/GPT" => 1,
        _ => 2,
    };

    internal static QuotaCadenceKind QuotaCadence(string bucketId, string displayName)
    {
        foreach (var raw in new[] { bucketId, displayName })
        {
            var normalized = raw.Trim().ToLowerInvariant().Replace('_', '-');
            if (normalized.EndsWith(" limit", StringComparison.Ordinal))
                normalized = normalized[..^" limit".Length];

            if (normalized is "session" or "5h" or "5-hour" or "five hour" or "five-hour"
                || normalized.EndsWith("-session", StringComparison.Ordinal)
                || normalized.EndsWith("-5h", StringComparison.Ordinal)
                || normalized.EndsWith("-5-hour", StringComparison.Ordinal)
                || normalized.EndsWith("-five hour", StringComparison.Ordinal)
                || normalized.EndsWith("-five-hour", StringComparison.Ordinal))
            {
                return QuotaCadenceKind.FiveHour;
            }

            if (normalized is "weekly" || normalized.EndsWith("-weekly", StringComparison.Ordinal))
                return QuotaCadenceKind.Weekly;
        }

        return QuotaCadenceKind.Other;
    }

    internal enum QuotaCadenceKind
    {
        Other,
        FiveHour,
        Weekly,
    }

    internal static ProviderSnapshot ParseSnapshot(string instanceId, string json, DateTimeOffset now)
    {
        GetUserStatusResponse? data;
        try
        {
            data = JsonSerializer.Deserialize<GetUserStatusResponse>(json);
        }
        catch (JsonException e)
        {
            throw new ProviderException($"Parse error: Invalid response: {e.Message}", e);
        }

        if (data?.UserStatus is null)
            throw new ProviderException("Parse error: Invalid response: missing userStatus");

        var us = data.UserStatus;
        var planInfo = us.PlanStatus?.PlanInfo;
        long? promptRemaining = us.PlanStatus?.AvailablePromptCredits;
        long? promptTotal = planInfo?.MonthlyPromptCredits;

        double promptPct = 0.0;
        if (promptRemaining is { } rem && promptTotal is { } tot && tot > 0)
        {
            var remainingPercent = (rem / (double)tot) * 100.0;
            promptPct = Quota.UsedPercentFromRemaining(remainingPercent);
        }

        string? promptDesc = null;
        if (promptRemaining is { } r2 && promptTotal is { } t2)
        {
            var used = Math.Max(t2 - r2, 0);
            promptDesc = $"{used} / {t2} prompt credits used · {r2} remaining";
        }

        var models = us.CascadeModelConfigData?.ClientModelConfigs ?? new List<ModelConfig>();

        // Secondary = model with LOWEST remaining fraction.
        var quotaTuples = new List<(string Label, double RemainingPct, string? Reset)>();
        foreach (var m in models)
        {
            var qi = m.QuotaInfo;
            if (qi?.RemainingFraction is not { } rf) continue;
            quotaTuples.Add((m.Label, Quota.ClampPercent(rf * 100.0), qi.ResetTime));
        }
        quotaTuples.Sort((a, b) => a.RemainingPct.CompareTo(b.RemainingPct));

        RateWindow? secondary = null;
        if (quotaTuples.Count > 0)
        {
            var (label, remaining, reset) = quotaTuples[0];
            secondary = new RateWindow
            {
                Label = $"{label} usage",
                UsedPercent = Quota.UsedPercentFromRemaining(remaining),
                ResetsAt = reset,
                WindowMinutes = null,
            };
        }

        var planName = us.UserTier?.DisplayName
            ?? us.UserTier?.Name
            ?? planInfo?.PlanName
            ?? "Unknown";

        var account = new AccountInfo
        {
            Email = us.Email,
            Plan = planName,
            UsedPercent = promptPct,
            CreditsUsed = (promptRemaining is { } r3 && promptTotal is { } t3) ? (double?)Math.Max(t3 - r3, 0) : null,
            CreditsTotal = promptTotal.HasValue ? (double?)promptTotal.Value : null,
        };

        // Per-model quota list for family grouping.
        var modelQuotas = new List<ModelQuota>();
        foreach (var m in models)
        {
            var qi = m.QuotaInfo;
            if (qi?.RemainingFraction is not { } rf) continue;

            var modelName = string.IsNullOrWhiteSpace(m.Label)
                ? m.ModelId ?? m.Model ?? "Unknown model"
                : m.Label;
            var lower = $"{modelName} {m.ModelId} {m.Model}".ToLowerInvariant();
            var family = lower.Contains("claude") || lower.Contains("gpt") || lower.Contains("openai")
                ? "Claude / GPT"
                : lower.Contains("gemini") ? "Gemini"
                : "Other";

            var windowType = ResolveWindowType(m, qi, modelName);

            modelQuotas.Add(new ModelQuota
            {
                Model = modelName,
                Family = family,
                FamilyKind = family switch
                {
                    "Gemini" => ModelQuotaFamilyKind.Gemini,
                    "Claude / GPT" => ModelQuotaFamilyKind.ClaudeGpt,
                    _ => ModelQuotaFamilyKind.Other,
                },
                WindowType = windowType,
                RemainingPercent = Quota.ClampPercent(rf * 100.0),
                UsedPercent = Quota.UsedPercentFromRemaining(rf * 100.0),
                ResetsAt = qi.ResetTime,
            });
        }

        var familyWindows = BuildFamilyWindows(modelQuotas);
        return new ProviderSnapshot
        {
            ProviderId = instanceId,
            Name = $"Antigravity · {planName}",
            PlanName = ProviderSnapshotIdentity.NormalizePlanName("Antigravity", planName),
            Primary = new RateWindow
            {
                Label = $"{planName} Prompt Pool",
                UsedPercent = promptPct,
                ResetsAt = null,
                DetailText = promptDesc,
                WindowMinutes = null,
            },
            Secondary = familyWindows.Count == 0 ? secondary : null,
            Tertiary = null,
            Balance = null,
            Accounts = new List<AccountInfo> { account },
            ModelQuotas = modelQuotas,
            AdditionalWindows = familyWindows,
            UpdatedAt = now,
            SourceLabel = "Antigravity local compatibility",
            Confidence = Confidence.SemiOfficial,
            EntitlementStatus = ProviderSnapshotIdentity.NormalizePlanName("Antigravity", planName) is null
                ? EntitlementStatus.Unknown
                : EntitlementStatus.Active,
            Error = null,
        };
    }

    private static string ResolveWindowType(ModelConfig model, QuotaInfo quota, string modelName)
    {
        var explicitType = quota.WindowType ?? quota.Group ?? model.WindowType;
        var hint = $"{explicitType} {modelName}";
        if (hint.Contains("week", StringComparison.OrdinalIgnoreCase)
            || hint.Contains("7d", StringComparison.OrdinalIgnoreCase))
        {
            return "weekly";
        }

        if (hint.Contains("5h", StringComparison.OrdinalIgnoreCase)
            || hint.Contains("five", StringComparison.OrdinalIgnoreCase)
            || hint.Contains("rolling", StringComparison.OrdinalIgnoreCase))
        {
            return "5h";
        }

        return "quota";
    }

    private static List<RateWindow> BuildFamilyWindows(IEnumerable<ModelQuota> quotas) =>
        quotas
            .Where(ModelQuotaPolicy.CountsForProviderAvailability)
            .GroupBy(quota => (quota.Family, quota.WindowType))
            .OrderBy(group => group.Key.Family == "Gemini" ? 0 : 1)
            .ThenBy(group => group.Key.WindowType == "5h" ? 0 : 1)
            .Select(group => group.OrderByDescending(quota => quota.UsedPercent).First())
            .Select(quota => new RateWindow
            {
                Label = $"{quota.Family} {quota.WindowType}",
                UsedPercent = quota.UsedPercent,
                ResetsAt = quota.ResetsAt,
                WindowMinutes = quota.WindowType switch
                {
                    "5h" => 5 * 60,
                    "weekly" => 7 * 24 * 60,
                    _ => null,
                },
            })
            .ToList();

    /// <summary>
    /// Try discovery without changing Antigravity process state.
    /// </summary>
    private async Task<(int Port, string Csrf)> DiscoverAsync(CancellationToken ct)
    {
        try
        {
            return await TryDiscoverAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderException e)
        {
            throw new ProviderException(
                $"Not available: Antigravity must already be running; QuotaLens will not start it during refresh. {e.Message}",
                e);
        }
    }

    private static List<(int Pid, string Csrf)> DiscoverProcsViaWmi()
    {
        var result = new List<(int, string)>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'language_server.exe'");
            foreach (var obj in searcher.Get())
            {
                var pid = Convert.ToInt32(obj["ProcessId"]);
                var cmd = obj["CommandLine"]?.ToString() ?? "";
                var cm = CsrfRegex.Match(cmd);
                if (cm.Success)
                    result.Add((pid, cm.Groups[1].Value));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"antigravity: WMI query failed ({ex.Message}), falling back to PowerShell");
        }
        return result;
    }

    /// <summary>
    /// Locate the running Antigravity language servers and a working (port, CSRF) pair.
    /// Antigravity spawns SEVERAL language_server.exe processes, each with its OWN csrf_token
    /// and a dynamic port (--https_server_port 0). We enumerate them all, map every listening
    /// port to its owning PID via a single netstat, then probe each process's ports with that
    /// process's csrf until one answers.
    /// </summary>
    private async Task<(int Port, string Csrf)> TryDiscoverAsync(CancellationToken ct)
    {
        var procs = DiscoverProcsViaWmi();
        if (procs.Count == 0)
        {
            const string psQuery =
                "$ProgressPreference = 'SilentlyContinue'; Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'language_server.exe' -and $_.CommandLine -like '*--csrf_token*' } | ForEach-Object { \"$($_.ProcessId)||$($_.CommandLine)\" }";

            var powershellExe = Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (!File.Exists(powershellExe))
                powershellExe = "powershell.exe";

            var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(psQuery));

            string psOut;
            try
            {
                psOut = await RunProcessAsync(
                    powershellExe,
                    $"-NoProfile -NonInteractive -EncodedCommand {encodedCommand}",
                    TimeSpan.FromSeconds(15),
                    ct).ConfigureAwait(false);
            }
            catch (TimeoutException) { throw new ProviderException("Timeout"); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { throw new ProviderException($"Not available: Cannot run powershell: {e.Message}", e); }

            procs = ParseProcs(psOut);
        }

        AppLog.Info($"antigravity: found {procs.Count} candidate processes");
        if (procs.Count == 0)
            throw new ProviderException("Not available: not_running");

        var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(cmdExe))
            cmdExe = "cmd.exe";

        string netstatOut;
        try
        {
            netstatOut = await RunProcessAsync(cmdExe, "/c netstat -ano -p tcp", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { throw new ProviderException("Timeout"); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { throw new ProviderException($"Not available: netstat error: {e.Message}", e); }

        var portsByPid = ParseListeningPorts(netstatOut);
        AppLog.Info($"antigravity: netstat mapped {portsByPid.Count} listening PIDs");

        foreach (var (pid, csrf) in procs)
        {
            if (!portsByPid.TryGetValue(pid, out var ports))
            {
                AppLog.Info($"antigravity: PID {pid} has no listening TCP ports in netstat");
                continue;
            }

            AppLog.Info($"antigravity: PID {pid} has listening ports: {string.Join(", ", ports)}");
            foreach (var port in ports)
            {
                if (await ProbeAsync(port, csrf, ct).ConfigureAwait(false))
                {
                    AppLog.Info($"antigravity: port {port} accepted probe for PID {pid}");
                    return (port, csrf);
                }
            }
        }

        throw new ProviderException("Not available: Cannot find working Antigravity port");
    }

    /// <summary>Parse (PID, CSRF) for every language_server record.</summary>
    internal static List<(int Pid, string Csrf)> ParseProcs(string psOut)
    {
        var result = new List<(int, string)>();
        foreach (var line in psOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split("||", 2, StringSplitOptions.None);
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var pid))
            {
                var cm = CsrfRegex.Match(parts[1]);
                if (cm.Success)
                    result.Add((pid, cm.Groups[1].Value));
            }
            else
            {
                var pm = PidRegex.Match(line);
                if (pm.Success && int.TryParse(pm.Groups[1].Value, out var legacyPid))
                {
                    var cm = CsrfRegex.Match(line);
                    if (cm.Success)
                        result.Add((legacyPid, cm.Groups[1].Value));
                }
            }
        }
        return result;
    }

    /// <summary>Map each PID to its localhost TCP LISTENING ports from `netstat -ano` output.</summary>
    private static Dictionary<int, List<int>> ParseListeningPorts(string netstatOut)
    {
        var map = new Dictionary<int, List<int>>();
        foreach (Match m in ListenRegex.Matches(netstatOut))
        {
            if (!int.TryParse(m.Groups[1].Value, out var port) || port is <= 0 or > 65535) continue;
            if (!int.TryParse(m.Groups[2].Value, out var pid)) continue;
            if (!map.TryGetValue(pid, out var list)) { list = new List<int>(); map[pid] = list; }
            if (!list.Contains(port)) list.Add(port);
        }
        return map;
    }

    /// <summary>Probe one port with a CSRF token via the Connect-RPC GetUnleashData endpoint.</summary>
    private async Task<bool> ProbeAsync(int port, string csrf, CancellationToken ct)
    {
        var url = $"https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetUnleashData";
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(ProbeBody, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrf);
            req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            using var probe = await ProbeClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            AppLog.Info($"antigravity: probe port {port} status={(int)probe.StatusCode}");
            return probe.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            AppLog.Info($"antigravity: probe port {port} exception={e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Run a child process, capture stdout, enforce a hard timeout (mirrors tokio::time::timeout).
    /// On timeout the child is killed and a TimeoutException is thrown.
    /// </summary>
    private static async Task<string> RunProcessAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            throw new InvalidOperationException("process did not start");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            var stdout = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return stdout;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException();
        }
    }

}
