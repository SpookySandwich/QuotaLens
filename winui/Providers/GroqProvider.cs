using System.Globalization;
using System.Net;
using System.Text.Json;
using QuotaLens.Core;

namespace QuotaLens.Providers;

public sealed class GroqProvider : IProvider
{
    private const string DefaultBaseUrl = "https://api.groq.com/v1";

    private static readonly (string Key, string Query)[] Queries =
    {
        ("requests", "sum(model_project_id_status_code:requests:rate5m)"),
        ("tokens_in", "sum(model_project_id:tokens_in:rate5m)"),
        ("tokens_out", "sum(model_project_id:tokens_out:rate5m)"),
        ("cache_hits", "sum(model_project_id:prompt_cache_hits:rate5m)"),
    };

    public string Type => "groq";
    public string Name => "Groq";
    public string SourceLabel => "Groq Enterprise Prometheus";
    public Confidence Confidence => Confidence.Official;

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var apiKey = ProviderConfig.Scoped(instanceId, config, "groq_key")
            ?? throw new ProviderException("Not configured: Groq API key not set. Add it in Settings.");
        var configuredBaseUrl = ProviderConfig.Scoped(instanceId, config, "groq_base_url")
            ?? DefaultBaseUrl;
        var baseUrl = ProviderEndpointPolicy.RequireCredentialBase(Type, configuredBaseUrl);

        var tasks = Queries.ToDictionary(
            query => query.Key,
            query => QueryScalarAsync(apiKey, baseUrl, query.Query, ct));
        await Task.WhenAll(tasks.Values).ConfigureAwait(false);

        var requestsPerMinute = tasks["requests"].Result * 60;
        var tokensPerMinute = (tasks["tokens_in"].Result + tasks["tokens_out"].Result) * 60;
        var cacheHitsPerMinute = tasks["cache_hits"].Result * 60;
        return Snapshot(requestsPerMinute, tokensPerMinute, cacheHitsPerMinute);
    }

    internal static double ParseScalar(JsonElement root)
    {
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
        {
            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : "query failed";
            throw new ProviderException($"Groq metrics API error: {error}");
        }

        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var total = 0d;
        foreach (var series in result.EnumerateArray())
        {
            if (!series.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                continue;
            var values = value.EnumerateArray().ToArray();
            if (values.Length == 0)
                continue;
            total += NumericValue(values[^1]) ?? 0;
        }

        return total;
    }

    internal static ProviderSnapshot Snapshot(double requestsPerMinute, double tokensPerMinute, double cacheHitsPerMinute) => new()
    {
        ProviderId = "groq",
        Name = "Groq",
        Primary = new RateWindow
        {
            Label = "Requests",
            Kind = RateWindowKind.Informational,
            Sensitivity = RateWindowSensitivity.Usage,
            UsedPercent = 0,
            ValueText = $"{FormatMetric(requestsPerMinute)} req/min",
        },
        Secondary = new RateWindow
        {
            Label = "Tokens",
            Kind = RateWindowKind.Informational,
            Sensitivity = RateWindowSensitivity.Usage,
            UsedPercent = 0,
            ValueText = $"{FormatMetric(tokensPerMinute)} tok/min",
        },
        Tertiary = cacheHitsPerMinute > 0
            ? new RateWindow
            {
                Label = "Cache hits",
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Usage,
                UsedPercent = 0,
                ValueText = $"{FormatMetric(cacheHitsPerMinute)} cache/min",
            }
            : null,
        SourceLabel = "Groq Enterprise Prometheus",
        Confidence = Confidence.Official,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<double> QueryScalarAsync(string apiKey, Uri baseUrl, string query, CancellationToken ct)
    {
        try
        {
            var url = BuildQueryUri(baseUrl, query);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var summary = ProviderConfig.ResponseSummary(body);
                throw response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? new ProviderException($"Not available: Groq Enterprise Prometheus access denied. HTTP {(int)response.StatusCode}: {summary}")
                    : new ProviderException($"Network error: HTTP {(int)response.StatusCode}: {summary}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return ParseScalar(doc.RootElement);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Network error: {e.Message}", e);
        }
    }

    private static Uri BuildQueryUri(Uri baseUrl, string query)
    {
        var url = ProviderConfig.AppendPath(baseUrl.ToString(), "metrics/prometheus/api/v1/query");
        var builder = new UriBuilder(url)
        {
            Query = $"query={Uri.EscapeDataString(query)}",
        };
        return builder.Uri;
    }

    private static double? NumericValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => null,
        };

    private static string FormatMetric(double value)
    {
        if (value >= 100)
            return value.ToString("F0", CultureInfo.InvariantCulture);
        if (value >= 10)
            return value.ToString("F1", CultureInfo.InvariantCulture);
        return value.ToString("F2", CultureInfo.InvariantCulture);
    }
}
