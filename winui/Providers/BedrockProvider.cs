using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>
/// AWS Bedrock usage provider ported from CodexBar's Cost Explorer path.
/// Reads static AWS keys or an AWS CLI profile, signs Cost Explorer
/// GetCostAndUsage with SigV4, and maps monthly Bedrock spend to a quota card.
/// </summary>
public sealed class BedrockProvider : IProvider
{
    private const string DefaultRegion = "us-east-1";
    private const string CostExplorerRegion = "us-east-1";
    private const string DefaultCostExplorerUrl = "https://ce.us-east-1.amazonaws.com";

    public string Type => "bedrock";
    public string Name => "AWS Bedrock";
    public string SourceLabel => "AWS Cost Explorer";
    public Confidence Confidence => Confidence.Official;

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var resolved = await ResolveCredentialsAsync(instanceId, config, ct).ConfigureAwait(false);
        var budget = ParsePositiveDouble(ProviderConfig.Scoped(instanceId, config, "bedrock_budget"));
        var monthlySpend = await FetchMonthlySpendAsync(instanceId, config, resolved.Credentials, ct).ConfigureAwait(false);
        return Snapshot(new BedrockUsage(monthlySpend, budget, resolved.Region, DateTimeOffset.UtcNow));
    }

    internal static ProviderSnapshot Snapshot(BedrockUsage usage)
    {
        var endOfMonth = EndOfCurrentMonth(usage.UpdatedAt);
        var monthlyBudget = usage.MonthlyBudget.GetValueOrDefault();
        var hasBudget = monthlyBudget > 0;
        var usedPercent = hasBudget
            ? Quota.ClampPercent(usage.MonthlySpend / monthlyBudget * 100)
            : 0;
        var budgetText = hasBudget
            ? $" / ${monthlyBudget.ToString("F2", CultureInfo.InvariantCulture)} budget"
            : "";
        var spendText = $"${usage.MonthlySpend.ToString("F2", CultureInfo.InvariantCulture)} spent{budgetText}";

        return new ProviderSnapshot
        {
            ProviderId = "bedrock",
            Name = "AWS Bedrock",
            Primary = new RateWindow
            {
                Label = "Monthly spend",
                Kind = hasBudget ? RateWindowKind.Quota : RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Financial,
                UsedPercent = usedPercent,
                ValueText = hasBudget ? null : spendText,
                ResetsAt = endOfMonth.ToString("O", CultureInfo.InvariantCulture),
                ResetDescription = hasBudget ? spendText : null,
                WindowMinutes = null,
            },
            Balance = hasBudget
                ? new BalanceInfo
                {
                    Currency = "USD",
                    Total = Math.Max(0, monthlyBudget - usage.MonthlySpend),
                    Paid = usage.MonthlySpend,
                    Granted = monthlyBudget,
                }
                : null,
            SourceLabel = $"AWS Cost Explorer · {usage.Region}",
            Confidence = Confidence.Official,
            UpdatedAt = usage.UpdatedAt,
        };
    }

    internal static string CostExplorerBody(string startDate, string endDate, string? nextPageToken = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["TimePeriod"] = new Dictionary<string, string>
            {
                ["Start"] = startDate,
                ["End"] = endDate,
            },
            ["Granularity"] = "MONTHLY",
            ["Metrics"] = new[] { "UnblendedCost" },
            ["GroupBy"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["Type"] = "DIMENSION",
                    ["Key"] = "SERVICE",
                },
            },
        };
        if (!string.IsNullOrWhiteSpace(nextPageToken))
            payload["NextPageToken"] = nextPageToken;

        return JsonSerializer.Serialize(payload);
    }

    internal static double ParseTotalBedrockCost(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("ResultsByTime", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                throw new ProviderException("Parse error: Missing ResultsByTime in AWS Cost Explorer response");
            }

            var total = 0.0;
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("Groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var group in groups.EnumerateArray())
                {
                    if (!IsBedrockServiceGroup(group))
                        continue;

                    var amount = group.GetProperty("Metrics")
                        .GetProperty("UnblendedCost")
                        .GetProperty("Amount")
                        .GetString();
                    if (double.TryParse(amount, NumberStyles.Float, CultureInfo.InvariantCulture, out var cost))
                        total += cost;
                }
            }

            return total;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new ProviderException($"Parse error: Failed to parse AWS Cost Explorer response: {e.Message}", e);
        }
    }

    internal static string? NextPageToken(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("NextPageToken", out var token) && token.ValueKind == JsonValueKind.String
            ? ProviderConfig.Clean(token.GetString())
            : null;
    }

    internal static (string Start, string End) CurrentMonthRange(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var start = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var tomorrow = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);
        return (start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), tomorrow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    internal static void SignAwsRequest(
        HttpRequestMessage request,
        AwsCredentials credentials,
        string region,
        string service,
        DateTimeOffset now)
    {
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
        var bodyHash = Sha256Hex(Encoding.UTF8.GetBytes(body));

        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        request.Headers.TryAddWithoutValidation("Host", request.RequestUri?.Host ?? "");
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", bodyHash);
        if (!string.IsNullOrWhiteSpace(credentials.SessionToken))
            request.Headers.TryAddWithoutValidation("X-Amz-Security-Token", credentials.SessionToken);

        var signedHeaders = SignedHeaders(request);
        var canonicalRequest = string.Join('\n', new[]
        {
            request.Method.Method,
            CanonicalPath(request.RequestUri!),
            CanonicalQuery(request.RequestUri!),
            signedHeaders.Canonical + "\n",
            signedHeaders.Keys,
            bodyHash,
        });

        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join('\n', new[]
        {
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)),
        });
        var signature = Signature(credentials.SecretAccessKey, dateStamp, region, service, stringToSign);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"AWS4-HMAC-SHA256 Credential={credentials.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders.Keys}, Signature={signature}");
    }

    private async Task<double> FetchMonthlySpendAsync(
        string instanceId,
        IConfig config,
        AwsCredentials credentials,
        CancellationToken ct)
    {
        var (start, end) = CurrentMonthRange(DateTimeOffset.UtcNow);
        var total = 0.0;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? nextPageToken = null;

        do
        {
            var body = CostExplorerBody(start, end, nextPageToken);
            var responseBody = await CallCostExplorerAsync(instanceId, config, credentials, body, ct).ConfigureAwait(false);
            total += ParseTotalBedrockCost(responseBody);
            nextPageToken = NextPageToken(responseBody);
            if (nextPageToken is not null && !seenTokens.Add(nextPageToken))
                throw new ProviderException("Parse error: AWS Cost Explorer returned a repeated NextPageToken.");
        } while (nextPageToken is not null);

        return total;
    }

    private static async Task<string> CallCostExplorerAsync(
        string instanceId,
        IConfig config,
        AwsCredentials credentials,
        string body,
        CancellationToken ct)
    {
        var configuredEndpoint = ProviderConfig.Scoped(instanceId, config, "bedrock_cost_explorer_url") ?? DefaultCostExplorerUrl;
        var endpoint = ProviderEndpointPolicy.RequireCredentialTarget("bedrock", configuredEndpoint);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("X-Amz-Target", "AWSInsightsIndexService.GetCostAndUsage");
            request.Content = new StringContent(body, Encoding.UTF8, "application/x-amz-json-1.1");
            SignAwsRequest(request, credentials, CostExplorerRegion, "ce", DateTimeOffset.UtcNow);

            using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 401 or 403)
                throw new ProviderException($"Not available: AWS Cost Explorer access denied: {ProviderConfig.ResponseSummary(responseBody)}");
            if (!response.IsSuccessStatusCode)
                throw new ProviderException($"Network error: AWS Cost Explorer HTTP {(int)response.StatusCode}: {ProviderConfig.ResponseSummary(responseBody)}");
            return responseBody;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Network error: AWS Cost Explorer request failed: {e.Message}", e);
        }
    }

    private static async Task<ResolvedAwsCredentials> ResolveCredentialsAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var authMode = ProviderConfig.Scoped(instanceId, config, "bedrock_auth_mode")?.ToLowerInvariant();
        var profile = ProviderConfig.Scoped(instanceId, config, "bedrock_profile");
        var staticCredentials = StaticCredentials(instanceId, config);
        var hasStaticKeys = staticCredentials is not null;

        if (authMode == "profile" || (!hasStaticKeys && profile is not null))
        {
            if (string.IsNullOrWhiteSpace(profile))
                throw new ProviderException("Not configured: AWS profile not set.", ProviderErrorKind.Misconfigured);

            var awsPath = ProviderConfig.Scoped(instanceId, config, "bedrock_aws_cli_path") ?? "aws";
            var credentials = await ExportProfileCredentialsAsync(awsPath, profile, ct).ConfigureAwait(false);
            var region = Region(instanceId, config)
                ?? await ResolveProfileRegionAsync(awsPath, profile, ct).ConfigureAwait(false)
                ?? DefaultRegion;
            return new ResolvedAwsCredentials(credentials, region);
        }

        if (staticCredentials is not null)
            return new ResolvedAwsCredentials(staticCredentials, Region(instanceId, config) ?? DefaultRegion);

        throw new ProviderException("Not configured: AWS credentials not set. Add access keys or an AWS profile in Settings.", ProviderErrorKind.Misconfigured);
    }

    private static AwsCredentials? StaticCredentials(string instanceId, IConfig config)
    {
        var accessKeyId = ProviderConfig.Scoped(instanceId, config, "bedrock_access_key_id");
        var secretAccessKey = ProviderConfig.Scoped(instanceId, config, "bedrock_secret_access_key");
        if (accessKeyId is null || secretAccessKey is null)
            return null;

        var sessionToken = ProviderConfig.Scoped(instanceId, config, "bedrock_session_token");
        return new AwsCredentials(accessKeyId, secretAccessKey, sessionToken);
    }

    private static string? Region(string instanceId, IConfig config) =>
        ProviderConfig.Scoped(instanceId, config, "bedrock_region");

    private static async Task<AwsCredentials> ExportProfileCredentialsAsync(string awsPath, string profile, CancellationToken ct)
    {
        var output = await RunAwsAsync(
            awsPath,
            new[] { "configure", "export-credentials", "--profile", profile, "--format", "process" },
            ct).ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(output.Stdout);
            var root = document.RootElement;
            var accessKeyId = root.GetProperty("AccessKeyId").GetString();
            var secretAccessKey = root.GetProperty("SecretAccessKey").GetString();
            if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
                throw new ProviderException("Parse error: AWS CLI export-credentials output did not include keys.");

            var sessionToken = root.TryGetProperty("SessionToken", out var token) ? ProviderConfig.Clean(token.GetString()) : null;
            return new AwsCredentials(accessKeyId!, secretAccessKey!, sessionToken);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new ProviderException($"Parse error: Could not parse AWS CLI export-credentials output: {e.Message}", e);
        }
    }

    private static async Task<string?> ResolveProfileRegionAsync(string awsPath, string profile, CancellationToken ct)
    {
        try
        {
            var output = await RunAwsAsync(
                awsPath,
                new[] { "configure", "get", "region", "--profile", profile },
                ct,
                allowFailure: true).ConfigureAwait(false);
            return ProviderConfig.Clean(output.Stdout);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ProcessOutput> RunAwsAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct, bool allowFailure = false)
    {
        try
        {
            using var process = new Process
            {
                // Shared launch path: resolves .cmd/.ps1 shims (AWS CLI v1/pip installs).
                StartInfo = HiddenCliProcess.CreateStartInfo(fileName, arguments),
                EnableRaisingEvents = true,
            };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (process.ExitCode != 0 && !allowFailure)
            {
                var summary = ProviderConfig.ResponseSummary(stderr);
                if (summary.Contains("sso login", StringComparison.OrdinalIgnoreCase)
                    || summary.Contains("expired", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ProviderException("Login required: AWS profile session expired. Run aws sso login for the configured profile.");
                }

                throw new ProviderException($"Not available: AWS CLI failed to export credentials: {summary}");
            }

            return new ProcessOutput(stdout, stderr, process.ExitCode);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Not configured: AWS CLI could not be run: {e.Message}", e);
        }
    }

    private static bool IsBedrockServiceGroup(JsonElement group)
    {
        if (!group.TryGetProperty("Keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
            return false;

        return keys.EnumerateArray()
            .Any(key => key.ValueKind == JsonValueKind.String
                && (key.GetString()?.Contains("Bedrock", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private static (string Keys, string Canonical) SignedHeaders(HttpRequestMessage request)
    {
        var headers = request.Headers
            .Select(header => (Key: header.Key.ToLowerInvariant(), Value: string.Join(",", header.Value).Trim()))
            .Concat(request.Content?.Headers.Select(header => (Key: header.Key.ToLowerInvariant(), Value: string.Join(",", header.Value).Trim()))
                ?? Enumerable.Empty<(string Key, string Value)>())
            .OrderBy(header => header.Key, StringComparer.Ordinal)
            .ToArray();
        return (
            string.Join(';', headers.Select(header => header.Key)),
            string.Join('\n', headers.Select(header => $"{header.Key}:{header.Value}")));
    }

    private static string CanonicalPath(Uri uri) => string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : UriEncodePath(uri.AbsolutePath);

    private static string CanonicalQuery(Uri uri)
    {
        if (string.IsNullOrWhiteSpace(uri.Query))
            return "";

        var query = uri.Query.TrimStart('?');
        return string.Join("&", query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var pieces = part.Split('=', 2);
                return $"{UriEncode(Uri.UnescapeDataString(pieces[0]))}={UriEncode(Uri.UnescapeDataString(pieces.Length > 1 ? pieces[1] : ""))}";
            })
            .Order(StringComparer.Ordinal));
    }

    private static string UriEncodePath(string path) =>
        string.Join("/", path.Split('/', StringSplitOptions.None).Select(UriEncode));

    private static string UriEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if ((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9')
                || b is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%');
                builder.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static string Signature(string secretKey, string dateStamp, string region, string service, string stringToSign)
    {
        var dateKey = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        var regionKey = HmacSha256(dateKey, region);
        var serviceKey = HmacSha256(regionKey, service);
        var signingKey = HmacSha256(serviceKey, "aws4_request");
        return Convert.ToHexString(HmacSha256(signingKey, stringToSign)).ToLowerInvariant();
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static DateTimeOffset EndOfCurrentMonth(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
    }

    private static double? ParsePositiveDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;

    public sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken);
    public sealed record BedrockUsage(double MonthlySpend, double? MonthlyBudget, string Region, DateTimeOffset UpdatedAt);
    private sealed record ResolvedAwsCredentials(AwsCredentials Credentials, string Region);
    private sealed record ProcessOutput(string Stdout, string Stderr, int ExitCode);
}
