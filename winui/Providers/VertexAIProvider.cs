using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using QuotaLens.Core;
using QuotaLens.Helpers;
using static QuotaLens.Core.JsonUtil;
using static QuotaLens.Core.StringValues;

namespace QuotaLens.Providers;

/// <summary>
/// Vertex AI quota provider ported from CodexBar's gcloud ADC + Cloud Monitoring
/// path. It reads Application Default Credentials, refreshes user OAuth tokens,
/// then compares Vertex AI consumer quota usage and limit time series.
/// </summary>
public sealed class VertexAIProvider : IProvider
{
    private static readonly TimeSpan CurrentUsageMaxAge = TimeSpan.FromMinutes(10);
    private const string MonitoringEndpoint = "https://monitoring.googleapis.com/v3/projects";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UsageFilter = """
        metric.type="serviceruntime.googleapis.com/quota/allocation/usage" AND resource.type="consumer_quota" AND resource.label.service="aiplatform.googleapis.com"
        """;
    private const string RateUsageFilter = """
        metric.type="serviceruntime.googleapis.com/quota/rate/net_usage" AND resource.type="consumer_quota" AND resource.label.service="aiplatform.googleapis.com"
        """;
    private const string LimitFilter = """
        metric.type="serviceruntime.googleapis.com/quota/limit" AND resource.type="consumer_quota" AND resource.label.service="aiplatform.googleapis.com"
        """;

    public string Type => "vertexai";
    public string Name => "Vertex AI";
    public string SourceLabel => "Cloud Monitoring";
    public Confidence Confidence => Confidence.Official;

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var credentials = await LoadCredentialsAsync(instanceId, config, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(credentials.ProjectId))
            throw new ProviderException("Not configured: Google Cloud project not set. Add a project ID in Settings or configure gcloud.");

        var usage = await FetchQuotaUsageAsync(credentials.AccessToken, credentials.ProjectId!, ct).ConfigureAwait(false);
        return Snapshot(usage with
        {
            ProjectId = credentials.ProjectId,
            AccountEmail = credentials.Email,
        });
    }

    internal static ProviderSnapshot Snapshot(VertexAIUsage usage, DateTimeOffset? updatedAt = null)
    {
        var quotaDetails = usage.Quotas.Count > 0
            ? usage.Quotas
            : new[]
            {
                new VertexQuotaUsage(
                    usage.DetailText ?? "Current Vertex AI quota utilization",
                    usage.RequestsUsedPercent,
                    null,
                    null),
            };
        var primary = quotaDetails.OrderByDescending(item => item.UsedPercent).First();
        return new ProviderSnapshot
        {
            ProviderId = "vertexai",
            Name = "Vertex AI",
            Primary = new RateWindow
            {
                Label = primary.Label,
                UsedPercent = Quota.ClampPercent(primary.UsedPercent),
                DetailText = UsageDescription(primary),
            },
            AdditionalWindows = quotaDetails
                .Where(item => !ReferenceEquals(item, primary))
                .Select(item => new RateWindow
                {
                    Label = item.Label,
                    UsedPercent = Quota.ClampPercent(item.UsedPercent),
                    DetailText = UsageDescription(item),
                })
                .ToList(),
            Accounts = string.IsNullOrWhiteSpace(usage.AccountEmail) && string.IsNullOrWhiteSpace(usage.ProjectId)
                ? new List<AccountInfo>()
                : new List<AccountInfo>
                {
                    new()
                    {
                        Email = usage.AccountEmail,
                        Plan = usage.ProjectId,
                    },
                },
            SourceLabel = "Cloud Monitoring",
            Confidence = Confidence.Official,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
        };
    }

    internal static VertexAIUsage ParseMonitoringQuota(string usageJson, string limitJson)
        => ParseMonitoringQuota(usageJson, limitJson, DateTimeOffset.UtcNow);

    internal static VertexAIUsage ParseMonitoringQuota(
        string usageJson,
        string limitJson,
        DateTimeOffset now)
    {
        var usageByKey = AggregateTimeSeries(usageJson, now - CurrentUsageMaxAge);
        var limitByKey = AggregateTimeSeries(limitJson);
        if (usageByKey.Count == 0 || limitByKey.Count == 0)
            throw new ProviderException(I18n.T("quota.noVertexQuota"));

        var quotas = new List<VertexQuotaUsage>();
        foreach (var (key, limit) in limitByKey)
        {
            if (limit <= 0 || !usageByKey.TryGetValue(key, out var usage))
                continue;

            var percent = usage / limit * 100.0;
            quotas.Add(new VertexQuotaUsage(DisplayQuotaKey(key), percent, usage, limit));
        }

        if (quotas.Count == 0)
            throw new ProviderException(I18n.T("quota.noVertexQuota"));

        var ordered = quotas
            .OrderByDescending(quota => quota.UsedPercent)
            .ThenBy(quota => quota.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new VertexAIUsage(ordered[0].UsedPercent, null, ordered[0].Label, null, null, null)
        {
            Quotas = ordered,
        };
    }

    internal static Dictionary<QuotaKey, double> AggregateTimeSeries(string json)
        => AggregateTimeSeries(json, null);

    private static Dictionary<QuotaKey, double> AggregateTimeSeries(
        string json,
        DateTimeOffset? minimumTimestamp)
    {
        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<QuotaKey, double>();
        if (!document.RootElement.TryGetProperty("timeSeries", out var series)
            || series.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in series.EnumerateArray())
        {
            var key = QuotaKeyFromSeries(item);
            var value = LatestPointValue(item, minimumTimestamp);
            if (key is null || value is null)
                continue;

            if (!result.TryGetValue(key.Value, out var previous) || value.Value > previous)
                result[key.Value] = value.Value;
        }

        return result;
    }

    internal static string? NextPageToken(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("nextPageToken", out var token) && token.ValueKind == JsonValueKind.String
            ? ProviderConfig.Clean(token.GetString())
            : null;
    }

    private static async Task<VertexAICredentials> LoadCredentialsAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var path = ResolveCredentialsPath(instanceId, config);
        if (path is null || !File.Exists(path))
            throw new ProviderException("Login required: gcloud Application Default Credentials not found. Run 'gcloud auth application-default login'.");

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Not configured: Could not read gcloud credentials: {e.Message}", e);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var projectId = FirstNonEmpty(
            ProviderConfig.Resolve(instanceId, config, "vertexai", "vertexai_project_id"),
            OptionalString(root, "project_id"),
            LoadGcloudProjectId(instanceId, config));

        if (OptionalString(root, "client_email") is { } serviceAccountEmail)
        {
            var token = await PrintAccessTokenAsync(instanceId, config, ct).ConfigureAwait(false);
            return new VertexAICredentials(token, projectId, serviceAccountEmail);
        }

        var accessToken = ProviderConfig.Clean(OptionalString(root, "access_token"));
        var needsRefresh = string.IsNullOrWhiteSpace(accessToken) || TokenExpired(OptionalString(root, "token_expiry"));
        if (needsRefresh)
        {
            var clientId = OptionalString(root, "client_id");
            var clientSecret = OptionalString(root, "client_secret");
            var refreshToken = OptionalString(root, "refresh_token");
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(refreshToken))
                throw new ProviderException("Login required: gcloud credentials are missing OAuth refresh data.");

            accessToken = await RefreshAccessTokenAsync(clientId!, clientSecret!, refreshToken!, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ProviderException("Login required: gcloud access token not found.");

        return new VertexAICredentials(accessToken!, projectId, EmailFromIdToken(OptionalString(root, "id_token")));
    }

    private static async Task<VertexAIUsage> FetchQuotaUsageAsync(string accessToken, string projectId, CancellationToken ct)
    {
        var allocationJson = await FetchAllTimeSeriesAsync(accessToken, projectId, UsageFilter, ct).ConfigureAwait(false);
        var rateJson = await FetchAllTimeSeriesAsync(accessToken, projectId, RateUsageFilter, ct).ConfigureAwait(false);
        var usageJson = MergeTimeSeries(allocationJson, rateJson);
        var limitJson = await FetchAllTimeSeriesAsync(accessToken, projectId, LimitFilter, ct).ConfigureAwait(false);
        return ParseMonitoringQuota(usageJson, limitJson);
    }

    internal static string MergeTimeSeries(params string[] documents)
    {
        var merged = new List<JsonElement>();
        foreach (var json in documents)
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("timeSeries", out var series)
                && series.ValueKind == JsonValueKind.Array)
            {
                merged.AddRange(series.EnumerateArray().Select(item => item.Clone()));
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("timeSeries");
            writer.WriteStartArray();
            foreach (var item in merged)
                item.WriteTo(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<string> FetchAllTimeSeriesAsync(string accessToken, string projectId, string filter, CancellationToken ct)
    {
        var merged = new List<JsonElement>();
        string? nextPageToken = null;
        do
        {
            var page = await FetchTimeSeriesPageAsync(accessToken, projectId, filter, nextPageToken, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(page);
            if (document.RootElement.TryGetProperty("timeSeries", out var series) && series.ValueKind == JsonValueKind.Array)
            {
                merged.AddRange(series.EnumerateArray().Select(item => item.Clone()));
            }

            nextPageToken = NextPageToken(page);
        } while (nextPageToken is not null);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("timeSeries");
            writer.WriteStartArray();
            foreach (var item in merged)
                item.WriteTo(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<string> FetchTimeSeriesPageAsync(
        string accessToken,
        string projectId,
        string filter,
        string? pageToken,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var start = now.AddHours(-1);
        var query = new Dictionary<string, string>
        {
            ["filter"] = filter,
            ["interval.startTime"] = start.ToString("O", CultureInfo.InvariantCulture),
            ["interval.endTime"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["aggregation.alignmentPeriod"] = "60s",
            ["aggregation.perSeriesAligner"] = "ALIGN_MAX",
            ["view"] = "FULL",
        };
        if (!string.IsNullOrWhiteSpace(pageToken))
            query["pageToken"] = pageToken!;

        var url = $"{MonitoringEndpoint}/{Uri.EscapeDataString(projectId)}/timeSeries?{FormUrlEncoded(query)}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 401)
                throw new ProviderException("Login required: Vertex AI request unauthorized. Run gcloud auth application-default login.");
            if ((int)response.StatusCode is 403)
                throw new ProviderException("Not available: Cloud Monitoring access forbidden. Check IAM permissions.");
            if (!response.IsSuccessStatusCode)
                throw new ProviderException($"Network error: Cloud Monitoring HTTP {(int)response.StatusCode}: {ProviderConfig.ResponseSummary(body)}");
            return body;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Network error: Cloud Monitoring request failed: {e.Message}", e);
        }
    }

    private static async Task<string> RefreshAccessTokenAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        });

        using var response = await Http.Client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if ((int)response.StatusCode is 400 or 401)
            throw new ProviderException("Login required: gcloud refresh token expired or was revoked. Run gcloud auth application-default login.");
        if (!response.IsSuccessStatusCode)
            throw new ProviderException($"Network error: Google OAuth token refresh HTTP {(int)response.StatusCode}: {ProviderConfig.ResponseSummary(body)}");

        using var document = JsonDocument.Parse(body);
        return OptionalString(document.RootElement, "access_token")
            ?? throw new ProviderException("Parse error: Google OAuth refresh response did not include an access token.");
    }

    private static async Task<string> PrintAccessTokenAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var gcloud = ProviderConfig.Resolve(instanceId, config, "vertexai", "vertexai_gcloud_path") ?? "gcloud";
        var output = await RunProcessAsync(gcloud, new[] { "auth", "application-default", "print-access-token" }, ct).ConfigureAwait(false);
        return ProviderConfig.Clean(output)
            ?? throw new ProviderException("Login required: gcloud returned an empty access token.");
    }

    private static async Task<string> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                // Shared launch path: resolves .cmd/.ps1 shims (gcloud on Windows has
                // no gcloud.exe) instead of a bare CreateProcess that only finds .exe.
                StartInfo = HiddenCliProcess.CreateStartInfo(fileName, arguments),
            };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new ProviderException($"Login required: gcloud failed to print an access token: {ProviderConfig.ResponseSummary(stderr)}");
            return stdout;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Not configured: gcloud could not be run: {e.Message}", e);
        }
    }

    private static string? ResolveCredentialsPath(string instanceId, IConfig config)
    {
        var configured = ProviderConfig.Resolve(instanceId, config, "vertexai", "vertexai_credentials_path");
        if (!string.IsNullOrWhiteSpace(configured))
            return Expand(configured!);

        return CandidateGcloudConfigDirs()
            .Select(dir => Path.Combine(dir, "application_default_credentials.json"))
            .FirstOrDefault(File.Exists);
    }

    private static string? LoadGcloudProjectId(string instanceId, IConfig config)
    {
        var configuredConfig = ProviderConfig.Resolve(instanceId, config, "vertexai", "vertexai_gcloud_config_dir");
        var dirs = !string.IsNullOrWhiteSpace(configuredConfig)
            ? new[] { Expand(configuredConfig!) }
            : CandidateGcloudConfigDirs();

        foreach (var dir in dirs)
        {
            var path = Path.Combine(dir, "configurations", "config_default");
            if (!File.Exists(path))
                continue;

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("project", StringComparison.OrdinalIgnoreCase))
                    continue;
                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2 && ProviderConfig.Clean(parts[1]) is { } project)
                    return project;
            }
        }

        return null;
    }

    private static string[] CandidateGcloudConfigDirs()
    {
        var candidates = new List<string>();
        if (ProviderConfig.Environment("CLOUDSDK_CONFIG") is { } cloudSdk)
            candidates.Add(Expand(cloudSdk));
        if (ProviderConfig.Environment("APPDATA") is { } appData)
            candidates.Add(Path.Combine(appData, "gcloud"));
        if (ProviderConfig.Environment("USERPROFILE") is { } userProfile)
            candidates.Add(Path.Combine(userProfile, ".config", "gcloud"));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static QuotaKey? QuotaKeyFromSeries(JsonElement series)
    {
        var metricLabels = Labels(series, "metric");
        var resourceLabels = Labels(series, "resource");
        var quotaMetric = FirstNonEmpty(
            metricLabels.GetValueOrDefault("quota_metric"),
            resourceLabels.GetValueOrDefault("quota_id"));
        if (string.IsNullOrWhiteSpace(quotaMetric))
            return null;

        return new QuotaKey(
            quotaMetric!,
            metricLabels.GetValueOrDefault("limit_name") ?? "",
            resourceLabels.GetValueOrDefault("location") ?? "global");
    }

    private static Dictionary<string, string> Labels(JsonElement series, string property)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!series.TryGetProperty(property, out var obj)
            || !obj.TryGetProperty("labels", out var labels)
            || labels.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var item in labels.EnumerateObject())
            if (item.Value.ValueKind == JsonValueKind.String)
                result[item.Name] = item.Value.GetString() ?? "";
        return result;
    }

    private static double? LatestPointValue(
        JsonElement series,
        DateTimeOffset? minimumTimestamp)
    {
        if (!series.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
            return null;

        var values = points.EnumerateArray()
            .Select((point, index) => new
            {
                Value = PointValue(point),
                Timestamp = PointTimestamp(point),
                Index = index,
            })
            .Where(point => point.Value is not null)
            .ToList();
        if (values.Count == 0)
            return null;

        var dated = values.Where(point => point.Timestamp is not null).ToList();
        if (dated.Count > 0)
        {
            var latest = dated.OrderByDescending(point => point.Timestamp).First();
            return minimumTimestamp is not null && latest.Timestamp < minimumTimestamp
                ? null
                : latest.Value;
        }

        // Cloud Monitoring normally timestamps every point. Preserve first-point
        // compatibility for redacted/upstream fixtures that omit interval metadata.
        return values.OrderBy(point => point.Index).First().Value;
    }

    private static DateTimeOffset? PointTimestamp(JsonElement point)
    {
        if (!point.TryGetProperty("interval", out var interval)
            || interval.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in new[] { "endTime", "startTime" })
        {
            if (OptionalString(interval, property) is { } value
                && DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var timestamp))
            {
                return timestamp;
            }
        }

        return null;
    }

    private static double? PointValue(JsonElement point)
    {
        if (!point.TryGetProperty("value", out var value))
            return null;

        if (value.TryGetProperty("doubleValue", out var doubleValue) && doubleValue.TryGetDouble(out var d))
            return d;
        if (value.TryGetProperty("int64Value", out var int64Value)
            && double.TryParse(int64Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var i))
        {
            return i;
        }

        return null;
    }

    private static bool TokenExpired(string? expiry)
    {
        if (string.IsNullOrWhiteSpace(expiry))
            return true;

        return !DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            || parsed <= DateTimeOffset.UtcNow.AddMinutes(5);
    }

    private static string? EmailFromIdToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var parts = token.Split('.');
        if (parts.Length < 2)
            return null;

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var document = JsonDocument.Parse(json);
            return OptionalString(document.RootElement, "email");
        }
        catch
        {
            return null;
        }
    }

    private static string DisplayQuotaKey(QuotaKey key)
    {
        var metric = key.QuotaMetric.Split('/').LastOrDefault() ?? key.QuotaMetric;
        var location = string.IsNullOrWhiteSpace(key.Location) || key.Location == "global" ? "" : $" · {key.Location}";
        return $"{metric}{location}";
    }

    private static string? UsageDescription(VertexQuotaUsage quota) =>
        quota.Usage is null || quota.Limit is null
            ? null
            : $"{quota.Usage.Value.ToString("0.##", CultureInfo.InvariantCulture)} of " +
              $"{quota.Limit.Value.ToString("0.##", CultureInfo.InvariantCulture)}";

    private static string FormUrlEncoded(IReadOnlyDictionary<string, string> values) =>
        string.Join("&", values.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));




    private static string Expand(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || expanded.StartsWith("~" + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            var home = ProviderConfig.Environment("USERPROFILE") ?? ProviderConfig.Environment("HOME") ?? "";
            expanded = Path.Combine(home, expanded[2..]);
        }
        return expanded;
    }

    public readonly record struct QuotaKey(string QuotaMetric, string LimitName, string Location);
    public sealed record VertexQuotaUsage(string Label, double UsedPercent, double? Usage, double? Limit);
    public sealed record VertexAIUsage(
        double RequestsUsedPercent,
        string? ResetsAt,
        string? DetailText,
        string? RawData,
        string? ProjectId,
        string? AccountEmail)
    {
        public IReadOnlyList<VertexQuotaUsage> Quotas { get; init; } = Array.Empty<VertexQuotaUsage>();
    }
    private sealed record VertexAICredentials(string AccessToken, string? ProjectId, string? Email);
}
