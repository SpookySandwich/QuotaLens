using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaLens.Core;

namespace QuotaLens.Providers;

public sealed class DeepgramProvider : IProvider
{
    public string Type => "deepgram";
    public string Name => "Deepgram";
    public string SourceLabel => "Deepgram API";
    public Confidence Confidence => Confidence.Official;

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var apiKey = ProviderConfig.Resolve(instanceId, config, "deepgram", "deepgram_key")
            ?? throw new ProviderException("Not configured: Deepgram API key not set. Add it in Settings.");
        var projectId = ProviderConfig.Resolve(instanceId, config, "deepgram", "deepgram_project_id");
        var configuredBaseUrl = ProviderConfig.Resolve(instanceId, config, "deepgram", "deepgram_base_url")
            ?? "https://api.deepgram.com/v1";
        var baseUrl = ProviderEndpointPolicy.RequireCredentialBase(Type, configuredBaseUrl).ToString();

        if (projectId is not null)
        {
            var usage = await FetchProjectUsageAsync(apiKey, baseUrl, new DeepgramProject(projectId, null), ct).ConfigureAwait(false);
            return Snapshot(usage);
        }

        var projects = await ListProjectsAsync(apiKey, baseUrl, ct).ConfigureAwait(false);
        if (projects.Count == 0)
            throw new ProviderException("Not available: Deepgram did not return any projects for this API key.");

        var usages = new List<DeepgramUsage>(projects.Count);
        foreach (var project in projects)
            usages.Add(await FetchProjectUsageAsync(apiKey, baseUrl, project, ct).ConfigureAwait(false));

        return Snapshot(Aggregate(usages));
    }

    internal static DeepgramUsage ParseUsage(JsonElement root, DeepgramProject project)
    {
        var response = root.Deserialize<DeepgramUsageResponse>()
            ?? throw new ProviderException("Parse error: Deepgram response was empty.");

        return new DeepgramUsage(
            project.ProjectId,
            project.Name,
            1,
            response.Start,
            response.End,
            response.Results.Sum(result => result.Hours ?? 0),
            response.Results.Sum(result => result.TotalHours ?? 0),
            response.Results.Sum(result => result.AgentHours ?? 0),
            response.Results.Sum(result => result.TokensIn ?? 0),
            response.Results.Sum(result => result.TokensOut ?? 0),
            response.Results.Sum(result => result.TtsCharacters ?? 0),
            response.Results.Sum(result => result.Requests ?? 0));
    }

    internal static DeepgramUsage Aggregate(IReadOnlyList<DeepgramUsage> usages)
    {
        if (usages.Count == 0)
            throw new ProviderException("Parse error: no Deepgram project usage was available.");
        if (usages.Count == 1)
            return usages[0];

        return new DeepgramUsage(
            "all",
            null,
            usages.Count,
            MinimumText(usages.Select(usage => usage.Start)),
            MaximumText(usages.Select(usage => usage.End)),
            usages.Sum(usage => usage.Hours),
            usages.Sum(usage => usage.TotalHours),
            usages.Sum(usage => usage.AgentHours),
            usages.Sum(usage => usage.TokensIn),
            usages.Sum(usage => usage.TokensOut),
            usages.Sum(usage => usage.TtsCharacters),
            usages.Sum(usage => usage.Requests));
    }

    internal static ProviderSnapshot Snapshot(DeepgramUsage usage) => new()
    {
        ProviderId = "deepgram",
        Name = usage.ProjectCount > 1
            ? $"Deepgram · {usage.ProjectCount} projects"
            : string.IsNullOrWhiteSpace(usage.ProjectName)
                ? "Deepgram"
                : $"Deepgram · {usage.ProjectName}",
        Primary = new RateWindow
        {
            Label = "Requests",
            Kind = RateWindowKind.Informational,
            Sensitivity = RateWindowSensitivity.Usage,
            UsedPercent = 0,
            ValueText = $"{usage.Requests.ToString("N0", CultureInfo.InvariantCulture)} requests",
        },
        Secondary = new RateWindow
        {
            Label = "Audio",
            Kind = RateWindowKind.Informational,
            Sensitivity = RateWindowSensitivity.Usage,
            UsedPercent = 0,
            ValueText = usage.TotalHours > 0
                ? $"{FormatDecimal(usage.Hours)} audio h · {FormatDecimal(usage.TotalHours)} billable h"
                : $"{FormatDecimal(usage.Hours)} audio h",
        },
        Tertiary = usage.TokensIn + usage.TokensOut + usage.TtsCharacters > 0
            ? new RateWindow
            {
                Label = "Tokens / TTS",
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Usage,
                UsedPercent = 0,
                ValueText = $"{(usage.TokensIn + usage.TokensOut).ToString("N0", CultureInfo.InvariantCulture)} tokens · {usage.TtsCharacters.ToString("N0", CultureInfo.InvariantCulture)} chars",
            }
            : null,
        SourceLabel = "Deepgram API",
        Confidence = Confidence.Official,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<IReadOnlyList<DeepgramProject>> ListProjectsAsync(
        string apiKey,
        string baseUrl,
        CancellationToken ct)
    {
        var url = new Uri(ProviderConfig.AppendPath(baseUrl, "projects"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {apiKey}");
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var projects = await JsonSerializer.DeserializeAsync<DeepgramProjectsResponse>(stream, cancellationToken: ct).ConfigureAwait(false);
        return projects?.Projects ?? Array.Empty<DeepgramProject>();
    }

    private static async Task<DeepgramUsage> FetchProjectUsageAsync(
        string apiKey,
        string baseUrl,
        DeepgramProject project,
        CancellationToken ct)
    {
        var projectPath = $"projects/{Uri.EscapeDataString(project.ProjectId)}/usage/breakdown";
        var url = new Uri(ProviderConfig.AppendPath(baseUrl, projectPath));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {apiKey}");
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return ParseUsage(doc.RootElement, project);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Network error: {e.Message}", e);
        }

        if (response.IsSuccessStatusCode)
            return response;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var summary = ProviderConfig.ResponseSummary(body);
        var statusCode = response.StatusCode;
        response.Dispose();
        throw statusCode switch
        {
            HttpStatusCode.Unauthorized => new ProviderException("Not available: Deepgram API key is invalid or expired."),
            HttpStatusCode.Forbidden => new ProviderException($"Not available: Deepgram rejected access. HTTP 403: {summary}"),
            HttpStatusCode.BadRequest => new ProviderException($"Network error: Deepgram bad request. HTTP 400: {summary}"),
            _ => new ProviderException($"Network error: HTTP {(int)statusCode}: {summary}"),
        };
    }

    private static string FormatDecimal(double value) =>
        value.ToString(value == Math.Floor(value) ? "N0" : "N1", CultureInfo.InvariantCulture);

    private static string? MinimumText(IEnumerable<string?> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value)).Min();

    private static string? MaximumText(IEnumerable<string?> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value)).Max();

    internal sealed record DeepgramUsage(
        string ProjectId,
        string? ProjectName,
        int ProjectCount,
        string? Start,
        string? End,
        double Hours,
        double TotalHours,
        double AgentHours,
        int TokensIn,
        int TokensOut,
        int TtsCharacters,
        int Requests);

    internal sealed record DeepgramProject(
        [property: JsonPropertyName("project_id")] string ProjectId,
        string? Name);

    private sealed record DeepgramProjectsResponse(
        [property: JsonPropertyName("projects")] IReadOnlyList<DeepgramProject> Projects);

    private sealed record DeepgramUsageResponse(
        [property: JsonPropertyName("start")] string? Start,
        [property: JsonPropertyName("end")] string? End,
        [property: JsonPropertyName("results")] IReadOnlyList<DeepgramUsageResult> Results);

    private sealed record DeepgramUsageResult(
        [property: JsonPropertyName("hours")] double? Hours,
        [property: JsonPropertyName("total_hours")] double? TotalHours,
        [property: JsonPropertyName("agent_hours")] double? AgentHours,
        [property: JsonPropertyName("tokens_in")] int? TokensIn,
        [property: JsonPropertyName("tokens_out")] int? TokensOut,
        [property: JsonPropertyName("tts_characters")] int? TtsCharacters,
        [property: JsonPropertyName("requests")] int? Requests);
}
