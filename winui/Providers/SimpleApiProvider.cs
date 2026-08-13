using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuotaLens.Core;
using static QuotaLens.Core.StringValues;

namespace QuotaLens.Providers;

/// <summary>
/// Shared implementation for API-token providers whose quota endpoint can be
/// represented as a small HTTP request plus a JSON parser. This is the landing
/// zone for CodexBar providers that do not require browser-cookie or OS-specific
/// session import code.
/// </summary>
public sealed class SimpleApiProvider : IProvider
{
    private sealed record Definition(
        string Type,
        string ConfigKey,
        string[] EnvironmentKeys,
        Func<string, IConfig, string> ResolveUrl,
        HttpMethod Method,
        Action<HttpRequestMessage, string> ApplyAuth,
        string? JsonBody,
        Func<JsonElement, ProviderSnapshot>? Parse,
        string CredentialLabel = "API key",
        Func<SimpleApiProvider, string, IConfig, string, CancellationToken, Task<ProviderSnapshot>>? CustomFetch = null);

    internal sealed record OpenAiUsagePage(
        double InputTokens,
        double OutputTokens,
        double Requests,
        bool HasMore,
        string? NextPage);

    internal sealed record OpenAiCostPage(
        double Amount,
        string? Currency,
        bool HasMore,
        string? NextPage);

    private sealed record ApiLimit(
        string Type,
        long? WindowMinutes,
        double UsedPercent,
        string? ResetsAt,
        string Label,
        string? ResetDescription);

    private static readonly IReadOnlyDictionary<string, Definition> Definitions =
        new Dictionary<string, Definition>
        {
            ["openrouter"] = new(
                "openrouter",
                "openrouter_key",
                new[] { "OPENROUTER_API_KEY", "OPENROUTER_TOKEN" },
                (_, _) => OpenRouterKeyEndpoint,
                HttpMethod.Get,
                ApplyOpenRouterAuth,
                null,
                ParseOpenRouter,
                CustomFetch: FetchOpenRouterAsync),
            ["moonshot"] = new(
                "moonshot",
                "moonshot_key",
                new[] { "MOONSHOT_API_KEY", "KIMI_API_KEY" },
                ResolveMoonshotUrl,
                HttpMethod.Get,
                ApplyBearerAuth,
                null,
                ParseMoonshot),
            ["venice"] = new(
                "venice",
                "venice_key",
                new[] { "VENICE_API_KEY" },
                (_, _) => "https://api.venice.ai/api/v1/billing/balance",
                HttpMethod.Get,
                ApplyBearerAuth,
                null,
                ParseVenice),
            ["crof"] = new(
                "crof",
                "crof_key",
                new[] { "CROF_API_KEY" },
                (_, _) => "https://crof.ai/usage_api/",
                HttpMethod.Get,
                ApplyBearerAuth,
                null,
                ParseCrof),
            ["openai"] = new(
                "openai",
                "openai_key",
                new[] { "OPENAI_ADMIN_KEY" },
                (_, _) => OpenAiUsageEndpoint,
                HttpMethod.Get,
                ApplyBearerAuth,
                null,
                null,
                "admin key",
                FetchOpenAiAsync),
            ["copilot"] = new(
                "copilot",
                "copilot_key",
                new[] { "COPILOT_API_TOKEN", "GITHUB_TOKEN" },
                ResolveCopilotUrl,
                HttpMethod.Get,
                ApplyCopilotAuth,
                null,
                ParseCopilot),
            ["elevenlabs"] = new(
                "elevenlabs",
                "elevenlabs_key",
                new[] { "ELEVENLABS_API_KEY", "XI_API_KEY" },
                ResolveElevenLabsUrl,
                HttpMethod.Get,
                ApplyElevenLabsAuth,
                null,
                ParseElevenLabs),
            ["warp"] = new(
                "warp",
                "warp_key",
                new[] { "WARP_API_KEY", "WARP_TOKEN" },
                (_, _) => "https://app.warp.dev/graphql/v2?op=GetRequestLimitInfo",
                HttpMethod.Post,
                ApplyWarpAuth,
                WarpGraphQlBody,
                ParseWarp),
            ["codebuff"] = new(
                "codebuff",
                "codebuff_key",
                new[] { "CODEBUFF_API_KEY" },
                ResolveCodebuffUrl,
                HttpMethod.Post,
                ApplyBearerAuth,
                CodebuffUsageBody,
                ParseCodebuff,
                CustomFetch: FetchCodebuffAsync),
            ["synthetic"] = new(
                "synthetic",
                "synthetic_key",
                new[] { "SYNTHETIC_API_KEY" },
                ResolveSyntheticUrl,
                HttpMethod.Get,
                ApplyBearerAuth,
                null,
                ParseSynthetic),
            ["zai"] = new(
                "zai",
                "zai_key",
                new[] { "Z_AI_API_KEY", "ZAI_API_KEY" },
                ResolveZaiUrl,
                HttpMethod.Get,
                ApplyBearerAuth,
                null,
                ParseZai),
            ["llmproxy"] = new(
                "llmproxy",
                "llmproxy_key",
                new[] { "LLM_PROXY_API_KEY", "LLMPROXY_API_KEY" },
                ResolveLlmProxyUrl,
                HttpMethod.Get,
                ApplyBearerAuth,
                null,
                ParseLlmProxy),
        };

    private const string WarpGraphQlBody = """
    {
      "operationName": "GetRequestLimitInfo",
      "query": "query GetRequestLimitInfo($requestContext: RequestContext!) { user(requestContext: $requestContext) { __typename ... on UserOutput { user { requestLimitInfo { isUnlimited nextRefreshTime requestLimit requestsUsedSinceLastRefresh } bonusGrants { requestCreditsGranted requestCreditsRemaining expiration } workspaces { bonusGrantsInfo { grants { requestCreditsGranted requestCreditsRemaining expiration } } } } } } }",
      "variables": {
        "requestContext": {
          "clientContext": {},
          "osContext": {
            "category": "Windows",
            "name": "Windows",
            "version": "10"
          }
        }
      }
    }
    """;

    private const int OpenAiLookbackDays = 30;
    private const int OpenAiMaxPages = 100;
    private const int CodebuffSubscriptionGraceSeconds = 2;
    private const string CodebuffUsageBody = """{"fingerprintId":"quotalens-usage"}""";
    private const string OpenAiUsageEndpoint = "https://api.openai.com/v1/organization/usage/completions";
    private const string OpenAiCostsEndpoint = "https://api.openai.com/v1/organization/costs";
    private const string OpenRouterKeyEndpoint = "https://openrouter.ai/api/v1/key";
    private const string OpenRouterCreditsEndpoint = "https://openrouter.ai/api/v1/credits";

    private readonly Definition _definition;
    private readonly HttpClient _httpClient;

    public static IReadOnlyCollection<string> SupportedTypes => Definitions.Keys.ToArray();

    public SimpleApiProvider(string type)
        : this(type, Http.Client)
    {
    }

    internal SimpleApiProvider(string type, HttpClient httpClient)
    {
        _definition = Definitions.TryGetValue(type, out var definition)
            ? definition
            : throw new ArgumentException($"Unknown simple API provider type: {type}");
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string Type => _definition.Type;
    public string Name => Catalog.ProviderName(Type);
    public string SourceLabel => Name.EndsWith(" API", StringComparison.Ordinal) ? Name : $"{Name} API";
    public Confidence Confidence => Confidence.Official;

    public static string ConfigKeyFor(string type) =>
        Definitions.TryGetValue(type, out var definition)
            ? definition.ConfigKey
            : throw new ArgumentException($"Unknown simple API provider type: {type}");

    internal static IReadOnlyList<string> EnvironmentKeysFor(string type) =>
        Definitions.TryGetValue(type, out var definition)
            ? definition.EnvironmentKeys
            : throw new ArgumentException($"Unknown simple API provider type: {type}");

    internal static bool TryGetEnvironmentKeys(string type, string fieldKey, out IReadOnlyList<string> keys)
    {
        if (Definitions.TryGetValue(type, out var definition)
            && string.Equals(definition.ConfigKey, fieldKey, StringComparison.OrdinalIgnoreCase))
        {
            keys = definition.EnvironmentKeys;
            return true;
        }

        keys = Array.Empty<string>();
        return false;
    }

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var token = ResolveToken(instanceId, config)
            ?? throw new ProviderException($"Not configured: {Name} {_definition.CredentialLabel} not set. Add it in Settings.");

        try
        {
            ProviderSnapshot snapshot;
            if (_definition.CustomFetch is not null)
            {
                snapshot = await _definition.CustomFetch(this, instanceId, config, token, ct).ConfigureAwait(false);
            }
            else
            {
                using var response = await SendAsync(instanceId, config, token, ct).ConfigureAwait(false);
                EnsureSuccess(response, _definition.CredentialLabel);
                var parser = _definition.Parse
                    ?? throw new ProviderException($"Parse error: {Name} has no response parser.");
                snapshot = parser(await ReadJsonAsync(response, ct).ConfigureAwait(false));
            }

            return ProviderSnapshotMetadata.Apply(this, snapshot);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Parse error: {e.Message}", e);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        string instanceId,
        IConfig config,
        string token,
        CancellationToken ct)
    {
        return await SendAsync(
            _definition.ResolveUrl(instanceId, config),
            _definition.Method,
            _definition.ApplyAuth,
            token,
            _definition.JsonBody,
            ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string endpoint,
        HttpMethod method,
        Action<HttpRequestMessage, string> applyAuth,
        string token,
        string? jsonBody,
        CancellationToken ct)
    {
        try
        {
            var requestUri = ProviderEndpointPolicy.RequireCredentialTarget(
                Type,
                endpoint);
            using var request = new HttpRequestMessage(method, requestUri);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            applyAuth(request, token);
            if (!string.IsNullOrWhiteSpace(jsonBody))
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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

    private void EnsureSuccess(HttpResponseMessage response, string credentialLabel)
    {
        if ((int)response.StatusCode is 401 or 403)
        {
            throw new ProviderException(
                $"Not available: {Name} authentication failed. Check the configured {credentialLabel}.");
        }
        if (!response.IsSuccessStatusCode)
            throw new ProviderException($"Network error: HTTP {(int)response.StatusCode}");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    private string? ResolveToken(string instanceId, IConfig config)
    {
        return ResolveCredential(Type, instanceId, config);
    }

    internal static string? ResolveCredential(string type, string instanceId, IConfig config)
    {
        if (!Definitions.TryGetValue(type, out var definition))
            throw new ArgumentException($"Unknown simple API provider type: {type}");

        return ProviderConfig.Scoped(instanceId, config, definition.ConfigKey);
    }

    private static async Task<ProviderSnapshot> FetchOpenRouterAsync(
        SimpleApiProvider provider,
        string instanceId,
        IConfig config,
        string apiKey,
        CancellationToken ct)
    {
        using var keyResponse = await provider.SendAsync(
            OpenRouterKeyEndpoint,
            HttpMethod.Get,
            ApplyOpenRouterAuth,
            apiKey,
            null,
            ct).ConfigureAwait(false);
        provider.EnsureSuccess(keyResponse, "API key");
        var keyRoot = await ReadJsonAsync(keyResponse, ct).ConfigureAwait(false);

        var managementKey = ResolveOpenRouterManagementKey(instanceId, config);
        if (managementKey is null)
            return ParseOpenRouter(keyRoot);

        using var creditsResponse = await provider.SendAsync(
            OpenRouterCreditsEndpoint,
            HttpMethod.Get,
            ApplyOpenRouterAuth,
            managementKey,
            null,
            ct).ConfigureAwait(false);
        provider.EnsureSuccess(creditsResponse, "management key");
        var creditsRoot = await ReadJsonAsync(creditsResponse, ct).ConfigureAwait(false);
        return ParseOpenRouter(keyRoot, creditsRoot);
    }

    internal static string? ResolveOpenRouterManagementKey(string instanceId, IConfig config) =>
        ProviderConfig.Scoped(instanceId, config, "openrouter_management_key");

    private static async Task<ProviderSnapshot> FetchCodebuffAsync(
        SimpleApiProvider provider,
        string instanceId,
        IConfig config,
        string apiKey,
        CancellationToken ct)
    {
        using var usageResponse = await provider.SendAsync(
            ResolveCodebuffUrl(instanceId, config),
            HttpMethod.Post,
            ApplyBearerAuth,
            apiKey,
            CodebuffUsageBody,
            ct).ConfigureAwait(false);
        provider.EnsureSuccess(usageResponse, "API key");
        var usageRoot = await ReadJsonAsync(usageResponse, ct).ConfigureAwait(false);
        var usageSnapshot = ParseCodebuff(usageRoot);

        try
        {
            using var subscriptionCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            subscriptionCancellation.CancelAfter(TimeSpan.FromSeconds(CodebuffSubscriptionGraceSeconds));
            using var subscriptionResponse = await provider.SendAsync(
                ResolveCodebuffSubscriptionUrl(instanceId, config),
                HttpMethod.Get,
                ApplyBearerAuth,
                apiKey,
                null,
                subscriptionCancellation.Token).ConfigureAwait(false);
            if (!subscriptionResponse.IsSuccessStatusCode)
                return usageSnapshot;

            var subscriptionRoot = await ReadJsonAsync(
                subscriptionResponse,
                subscriptionCancellation.Token).ConfigureAwait(false);
            return ParseCodebuff(usageRoot, subscriptionRoot);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return usageSnapshot;
        }
        catch (ProviderException)
        {
            return usageSnapshot;
        }
        catch (HttpRequestException)
        {
            return usageSnapshot;
        }
        catch (IOException)
        {
            return usageSnapshot;
        }
        catch (JsonException)
        {
            return usageSnapshot;
        }
    }

    private static async Task<ProviderSnapshot> FetchOpenAiAsync(
        SimpleApiProvider provider,
        string instanceId,
        IConfig config,
        string adminKey,
        CancellationToken ct)
    {
        var startTime = DateTimeOffset.UtcNow.AddDays(-OpenAiLookbackDays).ToUnixTimeSeconds();
        var projectIds = ResolveOpenAiProjectIds(instanceId, config);
        var usageTask = FetchAllOpenAiUsageAsync(provider, adminKey, startTime, projectIds, ct);
        var costsTask = FetchAllOpenAiCostsAsync(provider, adminKey, startTime, projectIds, ct);
        await Task.WhenAll(usageTask, costsTask).ConfigureAwait(false);

        var usage = await usageTask.ConfigureAwait(false);
        var costs = await costsTask.ConfigureAwait(false);
        return BuildOpenAiSnapshot(
            usage.InputTokens,
            usage.OutputTokens,
            usage.Requests,
            costs.Amount,
            costs.Currency ?? "USD",
            OpenAiLookbackDays);
    }

    private static async Task<(double InputTokens, double OutputTokens, double Requests)> FetchAllOpenAiUsageAsync(
        SimpleApiProvider provider,
        string adminKey,
        long startTime,
        IReadOnlyList<string> projectIds,
        CancellationToken ct)
    {
        var inputTokens = 0.0;
        var outputTokens = 0.0;
        var requests = 0.0;
        string? page = null;
        var seenPages = new HashSet<string>(StringComparer.Ordinal);

        for (var pageNumber = 0; pageNumber < OpenAiMaxPages; pageNumber++)
        {
            var endpoint = BuildOpenAiRequestUrl(OpenAiUsageEndpoint, startTime, projectIds, page);
            using var response = await provider.SendAsync(
                endpoint,
                HttpMethod.Get,
                ApplyBearerAuth,
                adminKey,
                null,
                ct).ConfigureAwait(false);
            provider.EnsureSuccess(response, "admin key");

            var parsed = ParseOpenAiUsagePage(await ReadJsonAsync(response, ct).ConfigureAwait(false));
            inputTokens += parsed.InputTokens;
            outputTokens += parsed.OutputTokens;
            requests += parsed.Requests;
            page = AdvanceOpenAiPage(parsed.HasMore, parsed.NextPage, seenPages);
            if (page is null)
                return (inputTokens, outputTokens, requests);
        }

        throw new ProviderException($"Parse error: OpenAI usage pagination exceeded {OpenAiMaxPages} pages.");
    }

    private static async Task<(double Amount, string? Currency)> FetchAllOpenAiCostsAsync(
        SimpleApiProvider provider,
        string adminKey,
        long startTime,
        IReadOnlyList<string> projectIds,
        CancellationToken ct)
    {
        var amount = 0.0;
        string? currency = null;
        string? page = null;
        var seenPages = new HashSet<string>(StringComparer.Ordinal);

        for (var pageNumber = 0; pageNumber < OpenAiMaxPages; pageNumber++)
        {
            var endpoint = BuildOpenAiRequestUrl(OpenAiCostsEndpoint, startTime, projectIds, page);
            using var response = await provider.SendAsync(
                endpoint,
                HttpMethod.Get,
                ApplyBearerAuth,
                adminKey,
                null,
                ct).ConfigureAwait(false);
            provider.EnsureSuccess(response, "admin key");

            var parsed = ParseOpenAiCostPage(await ReadJsonAsync(response, ct).ConfigureAwait(false));
            amount += parsed.Amount;
            if (parsed.Currency is not null)
            {
                if (currency is not null && !string.Equals(currency, parsed.Currency, StringComparison.OrdinalIgnoreCase))
                    throw new ProviderException("Parse error: OpenAI costs returned mixed currencies.");
                currency = parsed.Currency;
            }

            page = AdvanceOpenAiPage(parsed.HasMore, parsed.NextPage, seenPages);
            if (page is null)
                return (amount, currency);
        }

        throw new ProviderException($"Parse error: OpenAI costs pagination exceeded {OpenAiMaxPages} pages.");
    }

    internal static IReadOnlyList<string> ResolveOpenAiProjectIds(string instanceId, IConfig config) =>
        ParseOpenAiProjectIds(ProviderConfig.Scoped(instanceId, config, "openai_project_ids"));

    internal static IReadOnlyList<string> ParseOpenAiProjectIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        return value
            .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static string BuildOpenAiRequestUrl(
        string endpoint,
        long startTime,
        IReadOnlyList<string> projectIds,
        string? page)
    {
        if (!string.Equals(endpoint, OpenAiUsageEndpoint, StringComparison.Ordinal)
            && !string.Equals(endpoint, OpenAiCostsEndpoint, StringComparison.Ordinal))
        {
            throw new ArgumentException("Unknown OpenAI organization endpoint.", nameof(endpoint));
        }

        var query = new List<string>
        {
            $"start_time={startTime.ToString(CultureInfo.InvariantCulture)}",
            "bucket_width=1d",
            "limit=31",
        };
        query.AddRange(projectIds.Select(projectId => $"project_ids={Uri.EscapeDataString(projectId)}"));
        if (!string.IsNullOrWhiteSpace(page))
            query.Add($"page={Uri.EscapeDataString(page.Trim())}");
        return $"{endpoint}?{string.Join('&', query)}";
    }

    internal static string? AdvanceOpenAiPage(bool hasMore, string? nextPage, ISet<string> seenPages)
    {
        if (!hasMore)
            return null;

        var page = Clean(nextPage)
            ?? throw new ProviderException("Parse error: OpenAI response has_more=true without next_page.");
        if (!seenPages.Add(page))
            throw new ProviderException("Parse error: OpenAI response repeated a pagination cursor.");
        return page;
    }

    internal static ProviderSnapshot ParseOpenRouter(JsonElement root)
    {
        var data = ObjectProperty(root, "data")
            ?? throw new ProviderException("Parse error: Missing OpenRouter key data");
        if (!data.TryGetProperty("limit_remaining", out var remainingElement))
            throw new ProviderException("Parse error: Missing OpenRouter limit_remaining");

        var hasNoKeyLimit = remainingElement.ValueKind == JsonValueKind.Null;
        double? remaining = hasNoKeyLimit
            ? null
            : ElementDouble(remainingElement)
                ?? throw new ProviderException("Parse error: Invalid OpenRouter limit_remaining");
        var limit = OptionalDouble(data, "limit");
        var resetType = OptionalString(data, "limit_reset");
        var used = limit is not null && remaining is not null
            ? Math.Max(0, limit.Value - remaining.Value)
            : 0;
        var usedPercent = hasNoKeyLimit
            ? 0
            : limit is > 0
                ? Quota.UtilizationToUsedPercent(used / limit.Value)
                : remaining is > 0 ? 0 : 100;
        var windows = new[]
        {
            OpenRouterUsageWindow("Daily usage", OptionalDouble(data, "usage_daily"), "Current UTC day"),
            OpenRouterUsageWindow("Weekly usage", OptionalDouble(data, "usage_weekly"), "Current UTC week"),
            OpenRouterUsageWindow("Monthly usage", OptionalDouble(data, "usage_monthly"), "Current UTC month"),
        }.Where(window => window is not null).Select(window => window!).ToList();

        return new ProviderSnapshot
        {
            ProviderId = "openrouter",
            Name = "OpenRouter",
            AvailabilityKind = hasNoKeyLimit
                ? ProviderAvailabilityKind.Unknown
                : ProviderAvailabilityKind.Finite,
            Primary = new RateWindow
            {
                Label = "API key credit limit",
                Kind = hasNoKeyLimit ? RateWindowKind.Informational : RateWindowKind.Quota,
                UsedPercent = usedPercent,
                ValueText = hasNoKeyLimit ? "No per-key limit" : null,
                WindowMinutes = hasNoKeyLimit ? null : OpenRouterResetWindowMinutes(resetType),
                ResetDescription = hasNoKeyLimit
                    ? "Account funding is reported separately"
                    : OpenRouterLimitDescription(limit, remaining, resetType),
            },
            Secondary = windows.ElementAtOrDefault(0),
            Tertiary = windows.ElementAtOrDefault(1),
            AdditionalWindows = windows.Skip(2).ToList(),
            SourceLabel = "OpenRouter API",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    internal static ProviderSnapshot ParseOpenRouter(JsonElement keyRoot, JsonElement creditsRoot)
    {
        var snapshot = ParseOpenRouter(keyRoot);
        snapshot.Balance = ParseOpenRouterCredits(creditsRoot);
        return snapshot;
    }

    internal static BalanceInfo ParseOpenRouterCredits(JsonElement root)
    {
        var data = ObjectProperty(root, "data")
            ?? throw new ProviderException("Parse error: Missing OpenRouter credits data");
        var totalCredits = RequiredDouble(data, "total_credits");
        var totalUsage = RequiredDouble(data, "total_usage");
        return new BalanceInfo
        {
            Currency = "USD",
            Total = Math.Max(0, totalCredits - totalUsage),
            Paid = totalUsage,
            Granted = totalCredits,
        };
    }

    internal static OpenAiUsagePage ParseOpenAiUsagePage(JsonElement root)
    {
        var data = ArrayProperty(root, "data")
            ?? throw new ProviderException("Parse error: Missing OpenAI usage data");
        var inputTokens = 0.0;
        var outputTokens = 0.0;
        var requests = 0.0;

        foreach (var bucket in data.EnumerateArray())
        {
            var results = ArrayProperty(bucket, "results")
                ?? throw new ProviderException("Parse error: Missing OpenAI usage results");
            foreach (var result in results.EnumerateArray())
            {
                // input_tokens/output_tokens are inclusive totals. Audio, image, text,
                // cached, and uncached subdivisions must not be added again.
                inputTokens += OptionalDouble(result, "input_tokens") ?? 0;
                outputTokens += OptionalDouble(result, "output_tokens") ?? 0;
                requests += OptionalDouble(result, "num_model_requests") ?? 0;
            }
        }

        return new OpenAiUsagePage(
            inputTokens,
            outputTokens,
            requests,
            OptionalBool(root, "has_more") ?? false,
            OptionalString(root, "next_page"));
    }

    internal static OpenAiCostPage ParseOpenAiCostPage(JsonElement root)
    {
        var data = ArrayProperty(root, "data")
            ?? throw new ProviderException("Parse error: Missing OpenAI costs data");
        var amount = 0.0;
        string? currency = null;

        foreach (var bucket in data.EnumerateArray())
        {
            var results = ArrayProperty(bucket, "results")
                ?? throw new ProviderException("Parse error: Missing OpenAI cost results");
            foreach (var result in results.EnumerateArray())
            {
                var amountObject = ObjectProperty(result, "amount")
                    ?? throw new ProviderException("Parse error: Missing OpenAI cost amount");
                amount += RequiredDouble(amountObject, "value");
                var resultCurrency = OptionalString(amountObject, "currency")?.ToUpperInvariant();
                if (resultCurrency is null)
                    continue;
                if (currency is not null && !string.Equals(currency, resultCurrency, StringComparison.OrdinalIgnoreCase))
                    throw new ProviderException("Parse error: OpenAI costs returned mixed currencies.");
                currency = resultCurrency;
            }
        }

        return new OpenAiCostPage(
            amount,
            currency,
            OptionalBool(root, "has_more") ?? false,
            OptionalString(root, "next_page"));
    }

    internal static ProviderSnapshot BuildOpenAiSnapshot(
        double inputTokens,
        double outputTokens,
        double requests,
        double amount,
        string currency,
        int lookbackDays)
    {
        var normalizedCurrency = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();
        var cost = string.Equals(normalizedCurrency, "USD", StringComparison.Ordinal)
            ? $"${Fmt2(amount)} spent"
            : $"{normalizedCurrency} {Fmt2(amount)} spent";
        var period = $"{lookbackDays}-day";

        return new ProviderSnapshot
        {
            ProviderId = "openai",
            Name = "OpenAI API",
            Primary = new RateWindow
            {
                Label = $"{period} cost",
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Financial,
                UsedPercent = 0,
                ValueText = cost,
            },
            Secondary = new RateWindow
            {
                Label = $"{period} input tokens",
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Usage,
                UsedPercent = 0,
                ValueText = $"{FmtCount(inputTokens)} tokens",
            },
            Tertiary = new RateWindow
            {
                Label = $"{period} output tokens",
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Usage,
                UsedPercent = 0,
                ValueText = $"{FmtCount(outputTokens)} tokens",
            },
            AdditionalWindows = new List<RateWindow>
            {
                new()
                {
                    Label = $"{period} requests",
                    Kind = RateWindowKind.Informational,
                    Sensitivity = RateWindowSensitivity.Usage,
                    UsedPercent = 0,
                    ValueText = $"{FmtCount(requests)} requests",
                },
            },
            SourceLabel = "OpenAI API",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static RateWindow? OpenRouterUsageWindow(string label, double? usage, string period)
    {
        if (usage is null)
            return null;

        return new RateWindow
        {
            Label = label,
            Kind = RateWindowKind.Informational,
            Sensitivity = RateWindowSensitivity.Financial,
            UsedPercent = 0,
            ValueText = $"${Fmt2(usage.Value)} used",
            ResetDescription = period,
        };
    }

    private static string OpenRouterLimitDescription(double? limit, double? remaining, string? resetType)
    {
        var remainingText = remaining is not null
            ? limit is > 0
                ? $"${Fmt2(remaining.Value)} of ${Fmt2(limit.Value)} remaining"
                : $"${Fmt2(remaining.Value)} remaining"
            : "Limit unavailable";
        var normalizedReset = Clean(resetType)?.Replace('_', ' ').Replace('-', ' ');
        return normalizedReset is null
            ? $"{remainingText} · does not reset"
            : $"{remainingText} · resets {normalizedReset.ToLowerInvariant()}";
    }

    private static long? OpenRouterResetWindowMinutes(string? resetType) =>
        Clean(resetType)?.ToLowerInvariant() switch
        {
            "daily" => 24 * 60,
            "weekly" => 7 * 24 * 60,
            _ => null,
        };

    internal static ProviderSnapshot ParseMoonshot(JsonElement root)
    {
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
            throw new ProviderException($"Not available: Moonshot API code {code.GetInt32()}");
        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.False)
            throw new ProviderException("Not available: Moonshot API returned status=false");

        var data = root.GetProperty("data");
        var available = RequiredDouble(data, "available_balance");
        var voucher = OptionalDouble(data, "voucher_balance") ?? 0.0;
        var cash = OptionalDouble(data, "cash_balance") ?? 0.0;
        var usedPercent = available > 0 ? 0.0 : 100.0;
        var balanceDescription = available < 0
            ? $"${Fmt2(Math.Abs(available))} deficit (cash ${Fmt2(cash)}, voucher ${Fmt2(voucher)})"
            : $"${Fmt2(available)} available (cash ${Fmt2(cash)}, voucher ${Fmt2(voucher)})";

        return BalanceSnapshot(
            "moonshot",
            "Moonshot",
            "Balance",
            usedPercent,
            balanceDescription,
            new BalanceInfo { Currency = "USD", Total = available, Paid = cash, Granted = voucher },
            "Moonshot API");
    }

    internal static ProviderSnapshot ParseVenice(JsonElement root)
    {
        var canConsume = !root.TryGetProperty("canConsume", out var canConsumeElement)
            || canConsumeElement.ValueKind != JsonValueKind.False;
        var balances = root.GetProperty("balances");
        var usd = OptionalDouble(balances, "usd");
        var diem = OptionalDouble(balances, "diem");
        var allocation = OptionalDouble(root, "diemEpochAllocation");
        var activeCurrency = OptionalString(root, "consumptionCurrency")?.ToUpperInvariant();

        double total;
        double usedPercent;
        string selectedCurrency;
        string description;
        double paid = 0;
        double granted;

        if (!canConsume)
        {
            selectedCurrency = activeCurrency == "DIEM" ? "DIEM" : "USD";
            total = selectedCurrency == "DIEM" ? diem ?? 0 : usd ?? 0;
            granted = total;
            usedPercent = 100;
            description = "Balance unavailable for API calls";
        }
        else if (activeCurrency == "USD" && usd is > 0)
        {
            selectedCurrency = "USD";
            total = usd.Value;
            granted = total;
            usedPercent = 0;
            description = $"${Fmt2(total)} USD remaining";
        }
        else if (activeCurrency != "USD" && diem is not null && allocation is > 0)
        {
            selectedCurrency = "DIEM";
            total = diem.Value;
            granted = allocation.Value;
            paid = Math.Max(0, granted - total);
            usedPercent = Quota.ClampPercent(paid / granted * 100);
            description = $"DIEM {Fmt2(total)} / {Fmt2(granted)} epoch allocation";
        }
        else if (diem is > 0)
        {
            selectedCurrency = "DIEM";
            total = diem.Value;
            granted = total;
            usedPercent = 0;
            description = $"DIEM {Fmt2(total)} remaining";
        }
        else if (usd is > 0)
        {
            selectedCurrency = "USD";
            total = usd.Value;
            granted = total;
            usedPercent = 0;
            description = $"${Fmt2(total)} USD remaining";
        }
        else
        {
            selectedCurrency = activeCurrency == "DIEM" ? "DIEM" : "USD";
            total = 0;
            granted = 0;
            usedPercent = 100;
            description = "No Venice API balance available";
        }

        return BalanceSnapshot(
            "venice",
            "Venice",
            "Balance",
            usedPercent,
            description,
            new BalanceInfo { Currency = selectedCurrency, Total = total, Paid = paid, Granted = granted },
            "Venice API");
    }

    internal static ProviderSnapshot ParseCrof(JsonElement root)
    {
        var credits = RequiredDouble(root, "credits");
        var clampedCredits = Math.Max(0, credits);
        var displayedCredits = Math.Floor(clampedCredits * 100) / 100;
        var requestsPlan = OptionalDouble(root, "requests_plan");
        var usableRequests = OptionalDouble(root, "usable_requests");
        var creditsWindow = new RateWindow
        {
            Label = "Credits",
            Kind = RateWindowKind.Informational,
            Sensitivity = RateWindowSensitivity.Financial,
            UsedPercent = 0,
            ValueText = $"${Fmt2(displayedCredits)}",
            ResetDescription = $"${Fmt2(displayedCredits)}",
        };

        RateWindow primary = creditsWindow;
        RateWindow? secondary = null;
        if (requestsPlan is not null && usableRequests is not null)
        {
            var clampedUsable = Math.Clamp(usableRequests.Value, 0, Math.Max(0, requestsPlan.Value));
            var remainingPercent = requestsPlan > 0
                ? Math.Floor(clampedUsable / requestsPlan.Value * 100)
                : 0;
            primary = new RateWindow
            {
                Label = "Requests",
                UsedPercent = Quota.ClampPercent(100 - remainingPercent),
                ResetsAt = NextCrofRequestReset(DateTimeOffset.UtcNow),
                ResetDescription = $"{Fmt0(Math.Max(0, usableRequests.Value))} requests left",
                WindowMinutes = 24 * 60,
            };
            secondary = creditsWindow;
        }

        return new ProviderSnapshot
        {
            ProviderId = "crof",
            Name = "Crof",
            Primary = primary,
            Secondary = secondary,
            Balance = new BalanceInfo
            {
                Currency = "USD",
                Total = clampedCredits,
                Paid = 0.0,
                Granted = clampedCredits,
            },
            SourceLabel = "Crof API",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    internal static ProviderSnapshot ParseCopilot(JsonElement root)
    {
        var quotaSnapshots = ObjectProperty(root, "quota_snapshots");
        var hasUnlimitedQuota = CopilotHasUnlimitedQuota(quotaSnapshots);
        var premium = CopilotQuotaWindow("Premium", ObjectProperty(quotaSnapshots, "premium_interactions"))
            ?? CopilotQuotaWindow(
                "Premium",
                CopilotFindDynamicQuota(quotaSnapshots, static key =>
                key.Contains("premium", StringComparison.OrdinalIgnoreCase)
                || key.Contains("completion", StringComparison.OrdinalIgnoreCase)
                || key.Contains("code", StringComparison.OrdinalIgnoreCase)))
            ?? CopilotQuotaWindow(
                "Premium",
                CopilotQuotaFromCounts(
                "completions",
                ObjectProperty(root, "monthly_quotas"),
                ObjectProperty(root, "limited_user_quotas")));
        var chat = CopilotQuotaWindow("Chat", ObjectProperty(quotaSnapshots, "chat"))
            ?? CopilotQuotaWindow(
                "Chat",
                CopilotFindDynamicQuota(quotaSnapshots, static key => key.Contains("chat", StringComparison.OrdinalIgnoreCase)))
            ?? CopilotQuotaWindow(
                "Chat",
                CopilotQuotaFromCounts(
                "chat",
                ObjectProperty(root, "monthly_quotas"),
                ObjectProperty(root, "limited_user_quotas")));

        premium ??= CopilotQuotaWindow("Premium", CopilotFindDynamicQuota(quotaSnapshots, static _ => true));

        if (premium is null && chat is null && (OptionalBool(root, "token_based_billing") == true || hasUnlimitedQuota))
        {
            premium = new RateWindow
            {
                Label = hasUnlimitedQuota ? "Plan quota" : "Token billing",
                Kind = RateWindowKind.Informational,
                UsedPercent = 0,
                ValueText = hasUnlimitedQuota ? "Unlimited" : "Token-based billing",
            };
        }

        if (premium is null && chat is null)
            throw new ProviderException("Parse error: Missing Copilot quota data");

        var plan = DisplayName(FirstString(root, "copilot_plan", "plan", "sku"));
        var reset = FirstDateIso(root, "quota_reset_date", "reset_at", "resetAt");
        if (reset is not null)
        {
            if (premium is not null) premium.ResetsAt ??= reset;
            if (chat is not null) chat.ResetsAt ??= reset;
        }
        var primary = premium ?? chat!;
        var secondary = premium is null ? null : chat;

        return new ProviderSnapshot
        {
            ProviderId = "copilot",
            Name = string.IsNullOrWhiteSpace(plan) ? "Copilot" : $"Copilot · {plan}",
            PlanName = plan,
            Primary = primary,
            Secondary = secondary,
            SourceLabel = "Copilot API",
            Confidence = Confidence.Official,
            AvailabilityKind = hasUnlimitedQuota && primary.Kind == RateWindowKind.Informational
                ? ProviderAvailabilityKind.Unlimited
                : ProviderAvailabilityKind.Finite,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    internal static ProviderSnapshot ParseElevenLabs(JsonElement root)
    {
        var used = RequiredDouble(root, "character_count");
        var limit = RequiredDouble(root, "character_limit");
        var tier = DisplayName(OptionalString(root, "tier"));
        var status = OptionalString(root, "status");
        var name = string.IsNullOrWhiteSpace(tier) ? "ElevenLabs" : $"ElevenLabs · {tier}";
        var isActive = string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "trialing", StringComparison.OrdinalIgnoreCase);
        var isExpired = string.Equals(status, "inactive", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "unpaid", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(status) && !isActive)
            name += $" · {status}";

        var usageDescription = $"{Fmt0(used)} / {Fmt0(limit)} credits";
        if (!string.IsNullOrWhiteSpace(status) && !isActive)
            usageDescription += $" · Status: {DisplayName(status)}";

        return new ProviderSnapshot
        {
            ProviderId = "elevenlabs",
            Name = name,
            PlanName = tier,
            Primary = new RateWindow
            {
                Label = "Character credits",
                UsedPercent = limit > 0 ? Quota.UtilizationToUsedPercent(used / limit) : 0,
                ResetsAt = UnixSecondsToIso(OptionalLong(root, "next_character_count_reset_unix")),
                ResetDescription = usageDescription,
            },
            Secondary = SlotWindow(root, "voice_slots_used", "voice_limit", "Voice slots"),
            Tertiary = SlotWindow(root, "professional_voice_slots_used", "professional_voice_limit", "Professional voices"),
            SourceLabel = "ElevenLabs API",
            Confidence = Confidence.Official,
            EntitlementStatus = isExpired
                ? EntitlementStatus.Expired
                : isActive
                    ? EntitlementStatus.Active
                    : EntitlementStatus.Unknown,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    internal static ProviderSnapshot ParseWarp(JsonElement root)
    {
        var user = ObjectProperty(ObjectProperty(ObjectProperty(root, "data"), "user"), "user")
            ?? throw new ProviderException("Parse error: Missing Warp user data");
        var info = ObjectProperty(user, "requestLimitInfo")
            ?? throw new ProviderException("Parse error: Missing Warp requestLimitInfo");

        var isUnlimited = OptionalBool(info, "isUnlimited") ?? false;
        var limit = OptionalDouble(info, "requestLimit") ?? 0;
        var used = OptionalDouble(info, "requestsUsedSinceLastRefresh") ?? 0;
        var bonus = WarpBonusSummary(user);

        return new ProviderSnapshot
        {
            ProviderId = "warp",
            Name = "Warp",
            AvailabilityKind = isUnlimited
                ? ProviderAvailabilityKind.Unlimited
                : ProviderAvailabilityKind.Finite,
            Primary = new RateWindow
            {
                Label = "Requests",
                UsedPercent = isUnlimited ? 0 : limit > 0 ? Quota.UtilizationToUsedPercent(used / limit) : 0,
                ResetsAt = isUnlimited ? null : OptionalDateIso(info, "nextRefreshTime"),
                ResetDescription = isUnlimited ? "Unlimited" : $"{Fmt0(used)}/{Fmt0(limit)} credits",
            },
            Secondary = bonus.Total > 0
                ? new RateWindow
                {
                    Label = "Bonus credits",
                    UsedPercent = bonus.Total > 0 ? Quota.UtilizationToUsedPercent((bonus.Total - bonus.Remaining) / bonus.Total) : 100,
                    ResetsAt = bonus.NextExpiration,
                    ResetDescription = $"{Fmt0(bonus.Remaining)}/{Fmt0(bonus.Total)} remaining",
                }
                : null,
            SourceLabel = "Warp API",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    internal static ProviderSnapshot ParseCodebuff(JsonElement root)
    {
        var used = OptionalDouble(root, "usage") ?? OptionalDouble(root, "used");
        var total = OptionalDouble(root, "quota") ?? OptionalDouble(root, "limit");
        var remaining = OptionalDouble(root, "remainingBalance") ?? OptionalDouble(root, "remaining");
        if (total is null && used is not null && remaining is not null)
            total = used + remaining;
        if (used is null && total is not null && remaining is not null)
            used = Math.Max(0, total.Value - remaining.Value);
        if (remaining is null && total is not null && used is not null)
            remaining = Math.Max(0, total.Value - used.Value);
        if (used is null && total is null && remaining is null)
            throw new ProviderException("Parse error: Missing Codebuff usage fields");

        var resolvedUsed = Math.Max(0, used ?? 0);
        var resolvedTotal = Math.Max(0, total ?? resolvedUsed + Math.Max(0, remaining ?? 0));
        var resolvedRemaining = Math.Max(0, remaining ?? Math.Max(0, resolvedTotal - resolvedUsed));
        var autoTopUpEnabled = OptionalBool(root, "autoTopupEnabled") ?? OptionalBool(root, "auto_topup_enabled");

        return new ProviderSnapshot
        {
            ProviderId = "codebuff",
            Name = "Codebuff",
            Primary = new RateWindow
            {
                Label = "Credits",
                UsedPercent = resolvedTotal > 0 ? Quota.UtilizationToUsedPercent(resolvedUsed / resolvedTotal) : resolvedRemaining > 0 ? 0 : 100,
                ResetsAt = OptionalDateIso(root, "next_quota_reset"),
                ResetDescription = $"{Fmt0(resolvedUsed)}/{Fmt0(resolvedTotal)} credits",
            },
            Balance = new BalanceInfo
            {
                Currency = "credits",
                Total = resolvedRemaining,
                Paid = resolvedUsed,
                Granted = resolvedTotal,
            },
            AdditionalWindows = autoTopUpEnabled is null
                ? new List<RateWindow>()
                : new List<RateWindow>
                {
                    new()
                    {
                        Label = "Auto top-up",
                        Kind = RateWindowKind.Informational,
                        ValueText = autoTopUpEnabled.Value ? "Enabled" : "Disabled",
                    },
                },
            SourceLabel = "Codebuff API",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    internal static ProviderSnapshot ParseCodebuff(JsonElement usageRoot, JsonElement subscriptionRoot)
    {
        var snapshot = ParseCodebuff(usageRoot);
        var subscription = ObjectProperty(subscriptionRoot, "subscription");
        var rateLimit = ObjectProperty(subscriptionRoot, "rateLimit");
        var tier = DisplayName(
            OptionalString(subscription, "displayName")
            ?? OptionalString(subscriptionRoot, "displayName")
            ?? OptionalString(subscription, "tier")
            ?? OptionalString(subscriptionRoot, "tier")
            ?? OptionalString(subscription, "scheduledTier"));
        var rawStatus = OptionalString(subscription, "status");
        var status = DisplayName(rawStatus);
        var billingPeriodEnd = subscription is { } subscriptionObject
            ? FirstDateIso(subscriptionObject, "billingPeriodEnd", "currentPeriodEnd")
            : null;
        var email = OptionalString(subscriptionRoot, "email")
            ?? OptionalString(ObjectProperty(subscriptionRoot, "user"), "email");
        var entitlementStatus = CodebuffEntitlementStatus(rawStatus, OptionalBool(subscriptionRoot, "hasSubscription"));

        var weeklyUsed = OptionalDouble(rateLimit, "weeklyUsed") ?? OptionalDouble(rateLimit, "used");
        var weeklyLimit = OptionalDouble(rateLimit, "weeklyLimit") ?? OptionalDouble(rateLimit, "limit");
        if (weeklyUsed is not null && weeklyLimit is > 0)
        {
            var resolvedWeeklyUsed = Math.Max(0, weeklyUsed.Value);
            snapshot.Secondary = new RateWindow
            {
                Label = "Weekly",
                UsedPercent = Quota.UtilizationToUsedPercent(resolvedWeeklyUsed / weeklyLimit.Value),
                ResetsAt = rateLimit is { } rateLimitObject
                    ? OptionalDateIso(rateLimitObject, "weeklyResetsAt")
                    : null,
                ResetDescription = $"{Fmt0(resolvedWeeklyUsed)}/{Fmt0(weeklyLimit.Value)} credits",
                WindowMinutes = 7 * 24 * 60,
            };
        }

        var subscriptionDetails = new[] { tier, status }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (subscriptionDetails.Length > 0 || billingPeriodEnd is not null)
        {
            snapshot.AdditionalWindows.Insert(0, new RateWindow
            {
                Label = "Subscription",
                Kind = RateWindowKind.Informational,
                ValueText = subscriptionDetails.Length > 0
                    ? string.Join(" · ", subscriptionDetails)
                    : billingPeriodEnd,
                ResetsAt = billingPeriodEnd,
            });
        }

        if (email is not null || tier is not null)
        {
            snapshot.Accounts.Add(new AccountInfo
            {
                Email = email,
                Plan = tier,
            });
        }

        snapshot.EntitlementStatus = entitlementStatus;
        if (entitlementStatus == EntitlementStatus.Active && tier is not null)
        {
            snapshot.PlanName = tier;
            snapshot.Name = $"Codebuff · {tier}";
        }
        else if (entitlementStatus == EntitlementStatus.Expired)
        {
            snapshot.PlanName = null;
        }
        return snapshot;
    }

    internal static ProviderSnapshot ParseSynthetic(JsonElement root)
    {
        var data = ObjectProperty(root, "data");
        var known = new[]
        {
            ParseSyntheticQuota(ObjectProperty(root, "rollingFiveHourLimit") ?? ObjectProperty(data, "rollingFiveHourLimit"), "Rolling five-hour limit"),
            ParseSyntheticQuota(ObjectProperty(root, "weeklyTokenLimit") ?? ObjectProperty(data, "weeklyTokenLimit"), "Weekly token limit"),
            ParseSyntheticQuota(ObjectProperty(ObjectProperty(root, "search"), "hourly") ?? ObjectProperty(ObjectProperty(data, "search"), "hourly"), "Search hourly"),
        }.Where(window => window is not null).Select(window => window!).ToList();

        var windows = new List<RateWindow>(known);
        foreach (var discovered in SyntheticFallbackWindows(root))
        {
            if (!known.Any(window => window.Label.Equals(discovered.Label, StringComparison.OrdinalIgnoreCase)))
                windows.Add(discovered);
        }
        if (windows.Count == 0)
            throw new ProviderException("Parse error: Missing Synthetic quota data");

        var plan = DisplayName(OptionalString(root, "plan") ?? OptionalString(data, "planName") ?? OptionalString(data, "tier"));
        return new ProviderSnapshot
        {
            ProviderId = "synthetic",
            Name = string.IsNullOrWhiteSpace(plan) ? "Synthetic" : $"Synthetic · {plan}",
            PlanName = plan,
            Primary = windows[0],
            Secondary = windows.Count > 1 ? windows[1] : null,
            Tertiary = windows.Count > 2 ? windows[2] : null,
            AdditionalWindows = windows.Skip(3).ToList(),
            SourceLabel = "Synthetic API",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    internal static ProviderSnapshot ParseZai(JsonElement root)
    {
        if (OptionalBool(root, "success") == false)
            throw new ProviderException($"Not available: z.ai API returned {OptionalString(root, "msg") ?? "success=false"}");
        if (OptionalDouble(root, "code") is { } code && Math.Abs(code - 200) > 0.001)
            throw new ProviderException($"Not available: z.ai API code {Fmt0(code)}");

        var data = ObjectProperty(root, "data")
            ?? throw new ProviderException("Parse error: Missing z.ai data");
        var limitsElement = ArrayProperty(data, "limits")
            ?? throw new ProviderException("Parse error: Missing z.ai limits");

        var limits = ArrayItems(limitsElement)
            .Select(ParseZaiLimit)
            .Where(limit => limit is not null)
            .Select(limit => limit!)
            .ToList();
        var tokenLimits = limits
            .Where(limit => string.Equals(limit.Type, "TOKENS_LIMIT", StringComparison.OrdinalIgnoreCase))
            .OrderBy(limit => limit.WindowMinutes ?? long.MaxValue)
            .ToList();
        var sessionTokenLimit = tokenLimits.Count >= 2 ? tokenLimits.First() : null;
        var tokenLimit = tokenLimits.Count >= 2 ? tokenLimits.Last() : tokenLimits.FirstOrDefault();
        var timeLimit = limits.FirstOrDefault(limit => string.Equals(limit.Type, "TIME_LIMIT", StringComparison.OrdinalIgnoreCase));
        var primary = tokenLimit ?? timeLimit
            ?? throw new ProviderException("Parse error: No usable z.ai quota limit");
        var plan = DisplayName(OptionalString(data, "planName") ?? OptionalString(data, "plan") ?? OptionalString(data, "plan_type") ?? OptionalString(data, "packageName"));

        return new ProviderSnapshot
        {
            ProviderId = "zai",
            Name = string.IsNullOrWhiteSpace(plan) ? "z.ai" : $"z.ai · {plan}",
            PlanName = plan,
            Primary = ToRateWindow(primary),
            Secondary = tokenLimit is not null && timeLimit is not null ? ToRateWindow(timeLimit) : null,
            Tertiary = sessionTokenLimit is not null ? ToRateWindow(sessionTokenLimit) : null,
            SourceLabel = "z.ai API",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    internal static ProviderSnapshot ParseLlmProxy(JsonElement root) =>
        ParseLlmProxy(root, DateTimeOffset.UtcNow);

    internal static ProviderSnapshot ParseLlmProxy(JsonElement root, DateTimeOffset updatedAt)
    {
        var providers = ObjectProperty(root, "providers")
            ?? throw new ProviderException("Parse error: Missing LLM Proxy providers");

        var providerCount = 0;
        var credentialCount = 0.0;
        var activeCount = 0.0;
        var exhaustedCount = 0.0;
        var requests = 0.0;
        var tokens = 0.0;
        var providerCosts = 0.0;
        var hasProviderCost = false;
        double? minRemaining = null;
        var resetCandidates = new List<(string Iso, DateTimeOffset When)>();
        var providerSummaries = new List<(string Name, double Requests, double Tokens, double? Cost)>();

        foreach (var provider in providers.EnumerateObject())
        {
            providerCount++;
            var stats = provider.Value;
            credentialCount += OptionalDouble(stats, "credential_count") ?? 0;
            activeCount += OptionalDouble(stats, "active_count") ?? 0;
            exhaustedCount += OptionalDouble(stats, "exhausted_count") ?? 0;
            var providerRequests = OptionalDouble(stats, "total_requests") ?? 0;
            var providerTokens = TokenTotal(ObjectProperty(stats, "tokens"));
            var providerCost = OptionalDouble(stats, "approx_cost");
            requests += providerRequests;
            tokens += providerTokens;
            if (providerCost is not null)
            {
                providerCosts += providerCost.Value;
                hasProviderCost = true;
            }
            providerSummaries.Add((provider.Name, providerRequests, providerTokens, providerCost));

            foreach (var group in QuotaGroups(stats))
            {
                var remaining = OptionalDouble(group, "remaining_percent");
                if (remaining.HasValue)
                    minRemaining = minRemaining.HasValue ? Math.Min(minRemaining.Value, remaining.Value) : remaining.Value;

                var reset = OptionalDateIso(group, "reset_time");
                if (reset is not null
                    && DateTimeOffset.TryParse(
                        reset,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var when)
                    && when > updatedAt)
                {
                    resetCandidates.Add((reset, when));
                }
            }
        }

        double? approximateCost = hasProviderCost ? providerCosts : null;
        if (ObjectProperty(root, "summary") is { } summary)
        {
            requests = OptionalDouble(summary, "total_requests") ?? requests;
            tokens = OptionalDouble(summary, "total_tokens") ?? tokens;
            approximateCost = OptionalDouble(summary, "approx_cost") ?? approximateCost;
        }

        var additional = new List<RateWindow>();
        if (credentialCount > 0)
        {
            additional.Add(new RateWindow
            {
                Label = "Credentials",
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Usage,
                ValueText = $"{Fmt0(activeCount)}/{Fmt0(credentialCount)} active · {Fmt0(exhaustedCount)} exhausted",
            });
        }
        if (approximateCost is not null)
        {
            additional.Add(new RateWindow
            {
                Label = "Approx. spend",
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Financial,
                ValueText = $"${Fmt2(approximateCost.Value)}",
            });
        }
        foreach (var provider in providerSummaries
                     .OrderByDescending(provider => provider.Requests)
                     .ThenBy(provider => provider.Name, StringComparer.Ordinal)
                     .Take(3))
        {
            var cost = provider.Cost is null ? "" : $" · ${Fmt2(provider.Cost.Value)}";
            additional.Add(new RateWindow
            {
                Label = provider.Name,
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Usage,
                ValueText = $"{FmtCount(provider.Requests)} req · {FmtCount(provider.Tokens)} tok{cost}",
            });
        }

        return new ProviderSnapshot
        {
            ProviderId = "llmproxy",
            Name = "LLM Proxy",
            Primary = minRemaining.HasValue
                ? new RateWindow
                {
                    Label = "Minimum quota",
                    UsedPercent = Quota.ClampPercent(100 - minRemaining.Value),
                    ResetsAt = resetCandidates.OrderBy(candidate => candidate.When).Select(candidate => candidate.Iso).FirstOrDefault(),
                }
                : new RateWindow
                {
                    Label = "Providers",
                    Kind = RateWindowKind.Informational,
                    Sensitivity = RateWindowSensitivity.Usage,
                    ValueText = $"{providerCount} providers",
                },
            Secondary = new RateWindow
            {
                Label = "Requests",
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Usage,
                ValueText = $"{FmtCount(requests)} requests",
            },
            Tertiary = new RateWindow
            {
                Label = "Tokens",
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Usage,
                ValueText = $"{FmtCount(tokens)} tokens",
            },
            AdditionalWindows = additional,
            SourceLabel = "LLM Proxy API",
            Confidence = Confidence.Official,
            UpdatedAt = updatedAt,
        };
    }

    private static ProviderSnapshot BalanceSnapshot(
        string providerId,
        string name,
        string label,
        double usedPercent,
        string resetDescription,
        BalanceInfo balance,
        string sourceLabel,
        string? resetsAt = null) => new()
        {
            ProviderId = providerId,
            Name = name,
            Primary = new RateWindow
            {
                Label = label,
                UsedPercent = usedPercent,
                ResetsAt = resetsAt,
                ResetDescription = resetDescription,
            },
            Balance = balance,
            SourceLabel = sourceLabel,
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static RateWindow? SlotWindow(JsonElement root, string usedKey, string limitKey, string label)
    {
        var used = OptionalDouble(root, usedKey);
        var limit = OptionalDouble(root, limitKey);
        if (used is null || limit is not > 0)
            return null;

        return new RateWindow
        {
            Label = label,
            UsedPercent = Quota.UtilizationToUsedPercent(used.Value / limit.Value),
            ResetDescription = $"{Fmt0(used.Value)} / {Fmt0(limit.Value)}",
        };
    }

    private static RateWindow? CopilotQuotaWindow(string label, JsonElement? snapshot)
    {
        if (snapshot is not { ValueKind: JsonValueKind.Object } obj)
            return null;
        if (OptionalBool(obj, "unlimited") == true)
            return null;

        var entitlement = OptionalDouble(obj, "entitlement");
        var remaining = OptionalDouble(obj, "remaining");
        var percentRemaining = OptionalDouble(obj, "percent_remaining");
        if (percentRemaining is null && entitlement is > 0 && remaining is not null)
            percentRemaining = remaining.Value / entitlement.Value * 100.0;

        if (entitlement is 0 && remaining is 0)
            return null;
        if (percentRemaining is null)
            return null;

        var usedPercent = Quota.ClampPercent(100 - percentRemaining.Value);
        return new RateWindow
        {
            Label = label,
            UsedPercent = usedPercent,
            ResetDescription = entitlement is > 0 && remaining is not null
                ? $"{Fmt0(Math.Max(0, entitlement.Value - remaining.Value))}/{Fmt0(entitlement.Value)}"
                : usedPercent > 100
                    ? $"{Fmt0(usedPercent)}% used"
                    : null,
        };
    }

    private static bool CopilotHasUnlimitedQuota(JsonElement? quotas)
    {
        if (quotas is not { ValueKind: JsonValueKind.Object } obj)
            return false;

        return obj.EnumerateObject().Any(property =>
            property.Value.ValueKind == JsonValueKind.Object
            && OptionalBool(property.Value, "unlimited") == true);
    }

    private static JsonElement? CopilotQuotaFromCounts(string key, JsonElement? monthly, JsonElement? limited)
    {
        var entitlement = OptionalDouble(monthly, key);
        var remaining = OptionalDouble(limited, key);
        if (entitlement is not > 0 || remaining is null)
            return null;

        var json = $$"""{"entitlement":{{entitlement.Value.ToString(CultureInfo.InvariantCulture)}},"remaining":{{remaining.Value.ToString(CultureInfo.InvariantCulture)}}}""";
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement? CopilotFindDynamicQuota(JsonElement? quotas, Func<string, bool> predicate)
    {
        if (quotas is not { ValueKind: JsonValueKind.Object } obj)
            return null;

        foreach (var property in obj.EnumerateObject())
        {
            if (!predicate(property.Name) || property.Value.ValueKind != JsonValueKind.Object)
                continue;
            if (CopilotQuotaWindow("probe", property.Value) is not null)
                return property.Value;
        }

        return null;
    }

    private static (double Remaining, double Total, string? NextExpiration) WarpBonusSummary(JsonElement user)
    {
        var grants = new List<(double Granted, double Remaining, string? Expiration)>();
        foreach (var grant in ArrayItems(ArrayProperty(user, "bonusGrants")))
            grants.Add(ParseWarpBonusGrant(grant));

        foreach (var workspace in ArrayItems(ArrayProperty(user, "workspaces")))
        {
            foreach (var grant in ArrayItems(ArrayProperty(ObjectProperty(workspace, "bonusGrantsInfo"), "grants")))
                grants.Add(ParseWarpBonusGrant(grant));
        }

        var remaining = grants.Sum(grant => grant.Remaining);
        var total = grants.Sum(grant => grant.Granted);
        var next = grants
            .Where(grant => grant.Remaining > 0 && !string.IsNullOrWhiteSpace(grant.Expiration))
            .OrderBy(grant => grant.Expiration, StringComparer.Ordinal)
            .FirstOrDefault()
            .Expiration;
        return (remaining, total, next);
    }

    private static (double Granted, double Remaining, string? Expiration) ParseWarpBonusGrant(JsonElement grant) =>
        (
            OptionalDouble(grant, "requestCreditsGranted") ?? 0,
            OptionalDouble(grant, "requestCreditsRemaining") ?? 0,
            OptionalDateIso(grant, "expiration"));

    private static List<RateWindow> SyntheticFallbackWindows(JsonElement root)
    {
        var windows = new List<RateWindow>();
        CollectSyntheticWindows(root, windows, null);
        return windows;
    }

    private static void CollectSyntheticWindows(
        JsonElement element,
        List<RateWindow> windows,
        string? pathLabel)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var window = ParseSyntheticQuota(element, pathLabel);
            if (window is not null)
            {
                windows.Add(window);
                return;
            }

            foreach (var property in element.EnumerateObject())
            {
                var propertyLabel = DisplayName(Regex.Replace(
                    property.Name,
                    "(?<=[a-z0-9])(?=[A-Z])",
                    " ",
                    RegexOptions.CultureInvariant)) ?? property.Name;
                var isStructuralWrapper = property.Name.Equals("data", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("usage", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("quotas", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("limits", StringComparison.OrdinalIgnoreCase);
                var childLabel = isStructuralWrapper
                    ? pathLabel
                    : string.IsNullOrWhiteSpace(pathLabel)
                        ? propertyLabel
                        : $"{pathLabel} {propertyLabel}";
                CollectSyntheticWindows(property.Value, windows, childLabel);
            }
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectSyntheticWindows(item, windows, pathLabel);
        }
    }

    private static RateWindow? ParseSyntheticQuota(JsonElement? quota, string? fallbackLabel)
    {
        if (quota is not { ValueKind: JsonValueKind.Object } obj)
            return null;

        var usedPercent = FirstDouble(obj, "percentUsed", "usedPercent", "usagePercent", "usage_percent", "used_percent", "percent_used", "percent");
        if (usedPercent is null && FirstDouble(obj, "percentRemaining", "remainingPercent", "remaining_percent", "percent_remaining") is { } remainingPercent)
            usedPercent = 100 - NormalizePercent(remainingPercent);

        var limit = FirstDouble(obj, "limit", "messageLimit", "message_limit", "messages", "maxRequests", "max_requests", "requestLimit", "request_limit", "quota", "max", "total", "capacity", "allowance");
        var used = FirstDouble(obj, "used", "usage", "usedMessages", "used_messages", "messagesUsed", "messages_used", "requests", "requestCount", "request_count", "consumed", "spent");
        var remaining = FirstDouble(obj, "remaining", "left", "available", "balance");

        if (usedPercent is null)
        {
            if (limit is null && used is not null && remaining is not null)
                limit = used + remaining;
            if (used is null && limit is not null && remaining is not null)
                used = Math.Max(0, limit.Value - remaining.Value);
            if (limit is not null && used is not null && limit > 0)
                usedPercent = used / limit * 100;
        }

        if (usedPercent is null)
            return null;

        var windowMinutes = SyntheticWindowMinutes(obj);
        var reset = FirstDateIso(obj, "resetAt", "reset_at", "resetsAt", "resets_at", "renewAt", "renew_at", "renewsAt", "renews_at", "nextTickAt", "next_tick_at", "nextRegenAt", "next_regen_at", "periodEnd", "period_end", "expiresAt", "expires_at", "endAt", "end_at");
        var label = FirstString(obj, "name", "label", "type", "period", "scope", "title", "id")
            ?? fallbackLabel
            ?? "Quota";
        var description = limit is not null && used is not null
            ? $"{Fmt0(used.Value)}/{Fmt0(limit.Value)}"
            : SyntheticWindowDescription(windowMinutes);

        return new RateWindow
        {
            Label = DisplayName(label) ?? label,
            UsedPercent = Quota.ClampPercent(NormalizePercent(usedPercent.Value)),
            ResetsAt = reset,
            ResetDescription = reset is null ? description : null,
            WindowMinutes = windowMinutes,
        };
    }

    private static long? SyntheticWindowMinutes(JsonElement obj)
    {
        if (FirstDouble(obj, "windowMinutes", "window_minutes", "periodMinutes", "period_minutes") is { } minutes)
            return (long)Math.Round(minutes);
        if (FirstDouble(obj, "windowHours", "window_hours", "periodHours", "period_hours") is { } hours)
            return (long)Math.Round(hours * 60);
        if (FirstDouble(obj, "windowDays", "window_days", "periodDays", "period_days") is { } days)
            return (long)Math.Round(days * 24 * 60);
        if (FirstDouble(obj, "windowSeconds", "window_seconds", "periodSeconds", "period_seconds") is { } seconds)
            return (long)Math.Round(seconds / 60);
        var text = FirstString(obj, "window", "windowLabel", "window_label", "period", "periodLabel", "period_label");
        return DurationTextToMinutes(text);
    }

    private static string? SyntheticWindowDescription(long? minutes)
    {
        if (minutes is not > 0)
            return null;
        if (minutes.Value % (24 * 60) == 0)
        {
            var days = minutes.Value / (24 * 60);
            return $"{days} day{(days == 1 ? "" : "s")} window";
        }
        if (minutes.Value % 60 == 0)
        {
            var hours = minutes.Value / 60;
            return $"{hours} hour{(hours == 1 ? "" : "s")} window";
        }
        return $"{minutes.Value} minute{(minutes == 1 ? "" : "s")} window";
    }

    private static ApiLimit? ParseZaiLimit(JsonElement obj)
    {
        var type = OptionalString(obj, "type");
        if (string.IsNullOrWhiteSpace(type))
            return null;

        var unit = (int)(OptionalDouble(obj, "unit") ?? 0);
        var number = (long)(OptionalDouble(obj, "number") ?? 0);
        var isMonthlyMarker = type == "TIME_LIMIT" && unit == 5 && number == 1;
        var windowMinutes = isMonthlyMarker ? null : ZaiWindowMinutes(unit, number);
        var usedPercent = ZaiUsedPercent(obj);
        var label = ZaiWindowLabel(type, unit, number);
        var description = isMonthlyMarker
            ? "Monthly"
            : label.EndsWith("window", StringComparison.OrdinalIgnoreCase)
                ? label
                : null;

        return new ApiLimit(
            type,
            windowMinutes,
            usedPercent,
            UnixMillisecondsToIso(OptionalLong(obj, "nextResetTime")),
            label,
            description);
    }

    private static RateWindow ToRateWindow(ApiLimit limit) => new()
    {
        Label = limit.Label,
        UsedPercent = limit.UsedPercent,
        ResetsAt = limit.ResetsAt,
        ResetDescription = limit.ResetDescription,
        WindowMinutes = limit.WindowMinutes,
    };

    private static double ZaiUsedPercent(JsonElement obj)
    {
        var quota = OptionalDouble(obj, "usage");
        var current = OptionalDouble(obj, "currentValue");
        var remaining = OptionalDouble(obj, "remaining");
        if (quota is > 0)
        {
            double? used = null;
            if (remaining is not null)
                used = quota.Value - remaining.Value;
            if (current is not null)
                used = used.HasValue ? Math.Max(used.Value, current.Value) : current.Value;
            if (used is not null)
                return Quota.ClampPercent(used.Value / quota.Value * 100);
        }

        return Quota.ClampPercent(OptionalDouble(obj, "percentage") ?? 0);
    }

    private static long? ZaiWindowMinutes(int unit, long number)
    {
        if (number <= 0)
            return null;

        return unit switch
        {
            5 => number,
            3 => number * 60,
            1 => number * 24 * 60,
            6 => number * 7 * 24 * 60,
            _ => null,
        };
    }

    private static string ZaiWindowLabel(string type, int unit, long number)
    {
        if (number > 0)
        {
            var unitLabel = unit switch
            {
                5 => "minute",
                3 => "hour",
                1 => "day",
                6 => "week",
                _ => null,
            };
            if (unitLabel is not null)
                return $"{number} {unitLabel}{(number == 1 ? "" : "s")} window";
        }

        return type == "TIME_LIMIT" ? "Monthly" : "Token quota";
    }

    private static double TokenTotal(JsonElement? tokens)
    {
        if (tokens is not { ValueKind: JsonValueKind.Object } obj)
            return 0;

        return (OptionalDouble(obj, "input_cached") ?? 0)
            + (OptionalDouble(obj, "input_uncached") ?? 0)
            + (OptionalDouble(obj, "output") ?? 0);
    }

    private static IEnumerable<JsonElement> QuotaGroups(JsonElement stats)
    {
        if (stats.ValueKind != JsonValueKind.Object
            || !stats.TryGetProperty("quota_groups", out var groups))
        {
            yield break;
        }

        if (groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
                yield return group;
        }
        else if (groups.ValueKind == JsonValueKind.Object)
        {
            foreach (var group in groups.EnumerateObject())
                yield return group.Value;
        }
    }

    private static double? ElementDouble(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var number) => number,
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null,
    };

    private static string ResolveElevenLabsUrl(string instanceId, IConfig config) =>
        ResolveUrlWithOptionalBase(instanceId, config, "elevenlabs_base_url", new[] { "ELEVENLABS_API_URL" }, "https://api.elevenlabs.io", "v1/user/subscription");

    internal static string ResolveMoonshotUrl(string instanceId, IConfig config) =>
        ResolveUrlWithOptionalBase(instanceId, config, "moonshot_base_url", new[] { "MOONSHOT_API_URL" }, "https://api.moonshot.ai", "v1/users/me/balance");

    private static string ResolveCodebuffUrl(string instanceId, IConfig config) =>
        ResolveUrlWithOptionalBase(instanceId, config, "codebuff_base_url", new[] { "CODEBUFF_API_URL" }, "https://www.codebuff.com", "api/v1/usage");

    private static string ResolveCodebuffSubscriptionUrl(string instanceId, IConfig config) =>
        ResolveUrlWithOptionalBase(instanceId, config, "codebuff_base_url", new[] { "CODEBUFF_API_URL" }, "https://www.codebuff.com", "api/user/subscription");

    private static string ResolveSyntheticUrl(string instanceId, IConfig config) =>
        FirstNonEmpty(
            config.GetScoped(instanceId, "synthetic_url"),
            Env("SYNTHETIC_API_URL"),
            "https://api.synthetic.new/v2/quotas")!;

    private static string ResolveZaiUrl(string instanceId, IConfig config)
    {
        var quotaUrl = FirstNonEmpty(config.GetScoped(instanceId, "zai_quota_url"), Env("Z_AI_QUOTA_URL"), Env("ZAI_QUOTA_URL"));
        if (!string.IsNullOrWhiteSpace(quotaUrl))
            return quotaUrl!;

        return ResolveUrlWithOptionalBase(
            instanceId,
            config,
            "zai_base_url",
            new[] { "Z_AI_API_HOST", "ZAI_API_HOST" },
            "https://api.z.ai",
            "api/monitor/usage/quota/limit");
    }

    internal static string ResolveLlmProxyUrl(string instanceId, IConfig config)
    {
        var baseUrl = FirstNonEmpty(
            config.GetScoped(instanceId, "llmproxy_base_url"),
            Env("LLM_PROXY_BASE_URL"),
            Env("LLMPROXY_BASE_URL"));
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ProviderException("Not configured: LLM Proxy base URL not set. Add it in Settings.");

        var validatedBase = ProviderEndpointPolicy.RequireCredentialBase("llmproxy", baseUrl!);
        var path = validatedBase.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? AppendPath(validatedBase.ToString(), "quota-stats")
            : AppendPath(validatedBase.ToString(), "v1/quota-stats");
    }

    private static string ResolveCopilotUrl(string instanceId, IConfig config)
    {
        var host = FirstNonEmpty(config.GetScoped(instanceId, "copilot_enterprise_host"), Env("COPILOT_ENTERPRISE_HOST"), "github.com")!;
        host = host.Trim()
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Trim('/');
        var apiHost = host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? "api.github.com"
            : host.StartsWith("api.", StringComparison.OrdinalIgnoreCase)
                ? host
                : $"api.{host}";
        return $"https://{apiHost}/copilot_internal/user";
    }

    private static string ResolveUrlWithOptionalBase(
        string instanceId,
        IConfig config,
        string configKey,
        string[] environmentKeys,
        string defaultBaseUrl,
        string path)
    {
        var candidates = new List<string?> { config.GetScoped(instanceId, configKey) };
        candidates.AddRange(environmentKeys.Select(Env));
        candidates.Add(defaultBaseUrl);
        var baseUrl = FirstNonEmpty(candidates.ToArray());
        return AppendPath(baseUrl!, path);
    }

    private static string AppendPath(string baseUrl, string path) => ProviderConfig.AppendPath(baseUrl, path);

    private static void ApplyBearerAuth(HttpRequestMessage request, string token)
    {
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
    }

    private static void ApplyOpenRouterAuth(HttpRequestMessage request, string token)
    {
        ApplyBearerAuth(request, token);
        request.Headers.TryAddWithoutValidation("X-Title", "QuotaLens");
    }

    private static void ApplyCopilotAuth(HttpRequestMessage request, string token)
    {
        request.Headers.TryAddWithoutValidation("Authorization", $"token {token}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.96.2");
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.26.7");
        request.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.26.7");
        request.Headers.TryAddWithoutValidation("X-Github-Api-Version", "2025-04-01");
    }

    private static void ApplyElevenLabsAuth(HttpRequestMessage request, string token)
    {
        request.Headers.TryAddWithoutValidation("xi-api-key", token);
    }

    private static void ApplyWarpAuth(HttpRequestMessage request, string token)
    {
        ApplyBearerAuth(request, token);
        request.Headers.TryAddWithoutValidation("User-Agent", "Warp/1.0");
        request.Headers.TryAddWithoutValidation("x-warp-client-id", "warp-app");
        request.Headers.TryAddWithoutValidation("x-warp-os-category", "Windows");
        request.Headers.TryAddWithoutValidation("x-warp-os-name", "Windows");
        request.Headers.TryAddWithoutValidation("x-warp-os-version", "10");
    }

    private static double RequiredDouble(JsonElement obj, string property)
    {
        var value = OptionalDouble(obj, property);
        if (value is null)
            throw new ProviderException($"Parse error: Missing numeric field {property}");
        return value.Value;
    }

    private static double? OptionalDouble(JsonElement? obj, string property)
    {
        if (obj is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
    }

    private static long? OptionalLong(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.Number when value.TryGetDouble(out var number) => (long)number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => (long)number,
            _ => null,
        };
    }

    private static bool? OptionalBool(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetDouble(out var number) => Math.Abs(number) > 0.001,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => Math.Abs(number) > 0.001,
            _ => null,
        };
    }

    private static string? OptionalString(JsonElement? obj, string property)
    {
        if (obj is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => Clean(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static JsonElement? ObjectProperty(JsonElement? parent, string key)
    {
        if (parent is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return null;
    }

    private static JsonElement? ArrayProperty(JsonElement? parent, string key)
    {
        if (parent is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Array)
        {
            return value;
        }

        return null;
    }

    private static IEnumerable<JsonElement> ArrayItems(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array)
            yield break;

        foreach (var item in array.EnumerateArray())
            yield return item;
    }

    private static double? FirstDouble(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (OptionalDouble(obj, key) is { } value)
                return value;
        }

        return null;
    }

    private static string? FirstString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (OptionalString(obj, key) is { } value)
                return value;
        }

        return null;
    }

    private static string? FirstDateIso(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (OptionalDateIso(obj, key) is { } value)
                return value;
        }

        return null;
    }

    private static string? OptionalDateIso(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return NumericEpochToIso(number);
        if (value.ValueKind != JsonValueKind.String)
            return null;

        var text = Clean(value.GetString());
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
            return NumericEpochToIso(numeric);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToString("O", CultureInfo.InvariantCulture)
            : null;
    }

    private static string? NumericEpochToIso(double value)
    {
        if (value <= 0 || !double.IsFinite(value))
            return null;

        var seconds = Math.Abs(value) > 10_000_000_000 ? value / 1000 : value;
        return DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(seconds)).ToString("O", CultureInfo.InvariantCulture);
    }

    private static string? UnixSecondsToIso(long? seconds) =>
        seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value).ToString("O", CultureInfo.InvariantCulture) : null;

    private static string? UnixMillisecondsToIso(long? milliseconds) =>
        milliseconds is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value).ToString("O", CultureInfo.InvariantCulture) : null;

    internal static string NextCrofRequestReset(DateTimeOffset updatedAt)
    {
        TimeZoneInfo central;
        try
        {
            central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            central = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        }

        var local = TimeZoneInfo.ConvertTime(updatedAt, central);
        var nextLocalMidnight = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
        var reset = new DateTimeOffset(nextLocalMidnight, central.GetUtcOffset(nextLocalMidnight));
        return reset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static double NormalizePercent(double value) => value <= 1 ? value * 100 : value;

    private static long? DurationTextToMinutes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = text.Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        foreach (var (suffix, multiplier) in DurationSuffixes)
        {
            if (!normalized.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            var valueText = normalized[..^suffix.Length];
            if (double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
                return (long)Math.Round(value * multiplier);
        }

        return null;
    }

    private static readonly (string Suffix, double Multiplier)[] DurationSuffixes =
    {
        ("minutes", 1), ("minute", 1), ("mins", 1), ("min", 1), ("m", 1),
        ("hours", 60), ("hour", 60), ("hrs", 60), ("hr", 60), ("h", 60),
        ("days", 24 * 60), ("day", 24 * 60), ("d", 24 * 60),
    };

    private static string? Clean(string? value) => ProviderConfig.Clean(value);

    private static string? Env(string key) => ProviderConfig.Environment(key);



    private static string? DisplayName(string? value)
    {
        var clean = Clean(value);
        if (clean is null)
            return null;

        var spaced = clean.Replace("_", " ", StringComparison.Ordinal).Replace("-", " ", StringComparison.Ordinal);
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
    }

    private static EntitlementStatus CodebuffEntitlementStatus(string? status, bool? hasSubscription) =>
        Clean(status)?.ToLowerInvariant() switch
        {
            "active" or "trialing" => EntitlementStatus.Active,
            "expired" or "inactive" => EntitlementStatus.Expired,
            _ when hasSubscription == true => EntitlementStatus.Active,
            _ => EntitlementStatus.Unknown,
        };

    private static string Fmt0(double value) => value.ToString("F0", CultureInfo.InvariantCulture);
    private static string Fmt2(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    private static string FmtCount(double value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
