using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>
/// DeepSeek balance provider. Ports src-tauri/src/providers/deepseek.rs faithfully:
/// GET https://api.deepseek.com/user/balance with a Bearer key, parses balance_infos
/// (all amounts arrive as strings), selects one balance row, and renders it as the
/// primary "Balance" window + a BalanceInfo.
/// </summary>
public sealed class DeepSeekProvider : IProvider
{
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";
    private const string UsageAmountUrl = "https://platform.deepseek.com/api/v0/usage/amount";
    private const string UsageCostUrl = "https://platform.deepseek.com/api/v0/usage/cost";
    private static readonly TimeSpan OptionalUsageTimeout = TimeSpan.FromSeconds(2);

    public string Type => "deepseek";
    public string Name => "DeepSeek";
    public string SourceLabel => "DeepSeek API";
    public Confidence Confidence => Confidence.Official;

    private sealed class BalanceResponse
    {
        [JsonPropertyName("is_available")] public bool IsAvailable { get; set; }
        [JsonPropertyName("balance_infos")] public List<BalanceInfoRaw>? BalanceInfos { get; set; }
    }

    private sealed class BalanceInfoRaw
    {
        [JsonPropertyName("currency")] public string Currency { get; set; } = "";
        [JsonPropertyName("total_balance")] public string TotalBalance { get; set; } = "";
        [JsonPropertyName("granted_balance")] public string GrantedBalance { get; set; } = "";
        [JsonPropertyName("topped_up_balance")] public string ToppedUpBalance { get; set; } = "";
    }

    private readonly record struct ParsedBalance(string Currency, double Total, double Paid, double Granted);
    internal sealed record DeepSeekUsageSummary(
        int TodayTokens,
        int CurrentMonthTokens,
        double? TodayCost,
        double? CurrentMonthCost,
        int RequestCount,
        int CurrentMonthRequestCount,
        string? TopModel,
        IReadOnlyList<DeepSeekCategoryUsage> CategoryBreakdown,
        string Currency,
        DateTimeOffset UpdatedAt);

    internal sealed record DeepSeekCategoryUsage(string Category, int Tokens, double? Cost);

    private static string? GetKey(string instanceId, IConfig config)
    {
        // Env fallback is disabled when this explicit instance has a blank scoped key.
        return ProviderConfig.Scoped(instanceId, config, "deepseek_key");
    }

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var apiKey = GetKey(instanceId, config)
            ?? throw new ProviderException("Not configured: DeepSeek API key not set. Add it in Settings.");

        HttpResponseMessage resp;
        try
        {
            var balanceUri = ProviderEndpointPolicy.RequireCredentialTarget(Type, BalanceUrl);
            using var req = new HttpRequestMessage(HttpMethod.Get, balanceUri);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Network error: {e.Message}", e);
        }

        if ((int)resp.StatusCode != 200)
            throw new ProviderException($"Network error: HTTP {(int)resp.StatusCode}");

        BalanceResponse? data;
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            data = await JsonSerializer.DeserializeAsync<BalanceResponse>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Parse error: {e.Message}", e);
        }
        if (data is null)
            throw new ProviderException("Parse error: empty response");

        // Build tuples for every entry whose three amounts parse, mirroring filter_map.
        var balances = new List<ParsedBalance>();
        foreach (var b in data.BalanceInfos ?? new List<BalanceInfoRaw>())
        {
            if (TryParse(b.TotalBalance, out var tot)
                && TryParse(b.ToppedUpBalance, out var paid)
                && TryParse(b.GrantedBalance, out var granted))
            {
                balances.Add(new ParsedBalance(b.Currency, tot, paid, granted));
            }
        }

        if (balances.Count == 0)
        {
            return new ProviderSnapshot
            {
                ProviderId = Type,
                Name = Name,
                Primary = new RateWindow
                {
                    Label = "Balance",
                    UsedPercent = 0.0,
                    ResetsAt = null,
                    ResetDescription = "No balance data",
                    WindowMinutes = null,
                },
                Balance = new BalanceInfo { Currency = "USD", Total = 0.0, Paid = 0.0, Granted = 0.0 },
                SourceLabel = SourceLabel,
                Confidence = Confidence,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        // Selection: USD&>0, else first >0, else first USD, else first.
        var selected = SelectBalance(balances);

        var currency = selected.Currency;
        var total = selected.Total;
        var paidAmt = selected.Paid;
        var grantedAmt = selected.Granted;
        var symbol = currency == "CNY" ? "¥" : "$";

        string desc;
        if (total <= 0.0)
            desc = $"{symbol}0.00 — add credits at platform.deepseek.com";
        else if (!data.IsAvailable)
            desc = "Balance unavailable for API calls";
        else
            desc = $"{symbol}{total.ToString("F2", CultureInfo.InvariantCulture)} (Paid: {symbol}{paidAmt.ToString("F2", CultureInfo.InvariantCulture)} / Granted: {symbol}{grantedAmt.ToString("F2", CultureInfo.InvariantCulture)})";

        var snapshot = new ProviderSnapshot
        {
            ProviderId = Type,
            Name = Name,
            Primary = new RateWindow
            {
                Label = "Balance",
                UsedPercent = (total <= 0.0 || !data.IsAvailable) ? 100.0 : 0.0,
                ResetsAt = null,
                ResetDescription = desc,
                WindowMinutes = null,
            },
            Balance = new BalanceInfo
            {
                Currency = currency,
                Total = total,
                Paid = paidAmt,
                Granted = grantedAmt,
            },
            SourceLabel = SourceLabel,
            Confidence = Confidence,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var platformToken = ProviderConfig.Scoped(instanceId, config, "deepseek_user_token");
        if (platformToken is not null
            && await TryAttachOptionalUsageSummaryAsync(snapshot, platformToken, ct).ConfigureAwait(false))
        {
            snapshot.SourceLabel = "DeepSeek API + private dashboard";
        }
        return snapshot;
    }

    private static async Task<bool> TryAttachOptionalUsageSummaryAsync(
        ProviderSnapshot snapshot,
        string platformToken,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OptionalUsageTimeout);

        try
        {
            var summary = await FetchUsageSummaryAsync(platformToken, timeout.Token).ConfigureAwait(false);
            ApplyUsageSummary(snapshot, summary);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Optional dashboard usage must not delay the balance card.
            return false;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Balance is authoritative enough to render; usage-cost endpoints are optional.
            return false;
        }
    }

    private static async Task<DeepSeekUsageSummary> FetchUsageSummaryAsync(string platformToken, CancellationToken ct)
    {
        var amountTask = FetchJsonAsync(UsageAmountUrl, platformToken, ct);
        var costTask = FetchJsonAsync(UsageCostUrl, platformToken, ct);

        await Task.WhenAll(amountTask, costTask).ConfigureAwait(false);
        var amount = await amountTask.ConfigureAwait(false);
        var cost = await costTask.ConfigureAwait(false);
        return ParseUsageSummary(amount, cost, DateTimeOffset.UtcNow);
    }

    private static async Task<JsonElement> FetchJsonAsync(string url, string platformToken, CancellationToken ct)
    {
        var requestUri = ProviderEndpointPolicy.RequireCredentialTarget("deepseek", url);
        using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {platformToken}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ProviderException($"DeepSeek optional usage endpoint returned HTTP {(int)resp.StatusCode}");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    internal static DeepSeekUsageSummary ParseUsageSummary(JsonElement amountRoot, JsonElement costRoot, DateTimeOffset now)
    {
        ValidateDeepSeekEnvelope(amountRoot, "amount");
        ValidateDeepSeekEnvelope(costRoot, "cost");

        var amountBizData = BizDataObject(amountRoot)
            ?? throw new ProviderException("Parse error: DeepSeek amount response missing biz_data.");
        var costBizData = FirstBizDataObject(costRoot);
        var currency = GetString(costBizData, "currency") ?? "CNY";

        var dailyAmountMap = BuildUsageMap(GetArray(amountBizData, "days"));
        var dailyCostMap = BuildCostMap(GetArray(costBizData, "days"));
        var today = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var todayUsage = AggregateDay(today, dailyAmountMap, dailyCostMap);
        var monthUsage = AggregateMonth(monthStart, now, dailyAmountMap, dailyCostMap);
        var (topModel, categories) = BuildCategoryBreakdown(
            GetArray(amountBizData, "total"),
            GetArray(costBizData, "total"));

        return new DeepSeekUsageSummary(
            todayUsage.Tokens,
            monthUsage.Tokens,
            todayUsage.Cost,
            monthUsage.Cost,
            todayUsage.Requests,
            monthUsage.Requests,
            topModel,
            categories,
            currency,
            now);
    }

    internal static void ApplyUsageSummary(ProviderSnapshot snapshot, DeepSeekUsageSummary summary)
    {
        snapshot.Secondary = new RateWindow
        {
            Label = "Today usage",
            Kind = RateWindowKind.Informational,
            Sensitivity = RateWindowSensitivity.Usage,
            UsedPercent = 0,
            ResetsAt = NextUtcDay(summary.UpdatedAt).ToString("O", CultureInfo.InvariantCulture),
            ValueText = UsageDescription(summary.TodayTokens, summary.RequestCount, summary.TodayCost, summary.Currency),
            WindowMinutes = 24 * 60,
        };

        snapshot.Tertiary = new RateWindow
        {
            Label = "Month usage",
            Kind = RateWindowKind.Informational,
            Sensitivity = RateWindowSensitivity.Usage,
            UsedPercent = 0,
            ResetsAt = NextUtcMonth(summary.UpdatedAt).ToString("O", CultureInfo.InvariantCulture),
            ValueText = UsageDescription(summary.CurrentMonthTokens, summary.CurrentMonthRequestCount, summary.CurrentMonthCost, summary.Currency),
            WindowMinutes = null,
        };

        if (!string.IsNullOrWhiteSpace(summary.TopModel))
        {
            snapshot.AdditionalWindows.Add(new RateWindow
            {
                Label = "Top model",
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Usage,
                UsedPercent = 0,
                ValueText = summary.TopModel,
            });
        }

        foreach (var category in summary.CategoryBreakdown.Where(category => category.Tokens > 0 || category.Cost is > 0))
        {
            snapshot.AdditionalWindows.Add(new RateWindow
            {
                Label = CategoryLabel(category.Category),
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Usage,
                UsedPercent = 0,
                ValueText = UsageDescription(category.Tokens, 0, category.Cost, summary.Currency),
            });
        }
    }

    // find(USD && total>0) -> find(total>0) -> find(USD) -> first.
    private static ParsedBalance SelectBalance(List<ParsedBalance> balances)
    {
        foreach (var b in balances)
            if (b.Currency == "USD" && b.Total > 0.0) return b;
        foreach (var b in balances)
            if (b.Total > 0.0) return b;
        foreach (var b in balances)
            if (b.Currency == "USD") return b;
        return balances[0];
    }

    // Mirrors Rust f64::from_str (".parse::<f64>()"): invariant culture, no thousands separators.
    private static bool TryParse(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static void ValidateDeepSeekEnvelope(JsonElement root, string label)
    {
        if (TryGetInt(root, "code") is { } code && code != 0)
            throw new ProviderException($"DeepSeek {label} API error: code {code}.");

        var data = GetProperty(root, "data");
        if (data is not null && TryGetInt(data.Value, "biz_code") is { } bizCode && bizCode != 0)
            throw new ProviderException($"DeepSeek {label} API error: biz_code {bizCode}.");
    }

    private static JsonElement? BizDataObject(JsonElement root)
    {
        var data = GetProperty(root, "data");
        var bizData = data is null ? null : GetProperty(data.Value, "biz_data");
        return bizData?.ValueKind == JsonValueKind.Object ? bizData : null;
    }

    private static JsonElement? FirstBizDataObject(JsonElement root)
    {
        var data = GetProperty(root, "data");
        var bizData = data is null ? null : GetProperty(data.Value, "biz_data");
        if (bizData?.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in bizData.Value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object)
                return item;

        return null;
    }

    private static Dictionary<string, Dictionary<string, List<UsageAmount>>> BuildUsageMap(JsonElement? days)
    {
        var result = new Dictionary<string, Dictionary<string, List<UsageAmount>>>(StringComparer.Ordinal);
        if (days?.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var day in days.Value.EnumerateArray())
        {
            var date = GetString(day, "date");
            var data = GetArray(day, "data");
            if (string.IsNullOrWhiteSpace(date) || data?.ValueKind != JsonValueKind.Array)
                continue;

            var models = new Dictionary<string, List<UsageAmount>>(StringComparer.Ordinal);
            foreach (var modelUsage in data.Value.EnumerateArray())
            {
                var model = GetString(modelUsage, "model");
                var usage = GetArray(modelUsage, "usage");
                if (string.IsNullOrWhiteSpace(model) || usage?.ValueKind != JsonValueKind.Array)
                    continue;

                models[model] = UsageAmounts(usage.Value).ToList();
            }

            if (models.Count > 0)
                result[date] = models;
        }

        return result;
    }

    private static Dictionary<string, Dictionary<string, List<UsageCost>>> BuildCostMap(JsonElement? days)
    {
        var result = new Dictionary<string, Dictionary<string, List<UsageCost>>>(StringComparer.Ordinal);
        if (days?.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var day in days.Value.EnumerateArray())
        {
            var date = GetString(day, "date");
            var data = GetArray(day, "data");
            if (string.IsNullOrWhiteSpace(date) || data?.ValueKind != JsonValueKind.Array)
                continue;

            var models = new Dictionary<string, List<UsageCost>>(StringComparer.Ordinal);
            foreach (var modelUsage in data.Value.EnumerateArray())
            {
                var model = GetString(modelUsage, "model");
                var usage = GetArray(modelUsage, "usage");
                if (string.IsNullOrWhiteSpace(model) || usage?.ValueKind != JsonValueKind.Array)
                    continue;

                models[model] = UsageCosts(usage.Value).ToList();
            }

            if (models.Count > 0)
                result[date] = models;
        }

        return result;
    }

    private static (int Tokens, int Requests, double? Cost) AggregateDay(
        string date,
        IReadOnlyDictionary<string, Dictionary<string, List<UsageAmount>>> amountMap,
        IReadOnlyDictionary<string, Dictionary<string, List<UsageCost>>> costMap)
    {
        var tokens = 0;
        var requests = 0;
        double? cost = null;

        if (amountMap.TryGetValue(date, out var modelAmounts))
        {
            foreach (var usage in modelAmounts.Values.SelectMany(items => items))
            {
                if (IsRequest(usage.Type)) requests += usage.Amount;
                else if (IsKnownTokenCategory(usage.Type)) tokens += usage.Amount;
            }
        }

        if (costMap.TryGetValue(date, out var modelCosts))
            cost = SumTokenCosts(modelCosts.Values.SelectMany(items => items));

        return (tokens, requests, cost);
    }

    private static (int Tokens, int Requests, double? Cost) AggregateMonth(
        DateTimeOffset monthStart,
        DateTimeOffset now,
        IReadOnlyDictionary<string, Dictionary<string, List<UsageAmount>>> amountMap,
        IReadOnlyDictionary<string, Dictionary<string, List<UsageCost>>> costMap)
    {
        var allDates = amountMap.Keys.Concat(costMap.Keys).Distinct(StringComparer.Ordinal);
        var tokens = 0;
        var requests = 0;
        double? cost = null;

        foreach (var date in allDates)
        {
            if (!DateTimeOffset.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                || parsed < monthStart
                || parsed > now)
            {
                continue;
            }

            var day = AggregateDay(date, amountMap, costMap);
            tokens += day.Tokens;
            requests += day.Requests;
            if (day.Cost is { } dayCost)
                cost = (cost ?? 0) + dayCost;
        }

        return (tokens, requests, cost);
    }

    private static (string? TopModel, IReadOnlyList<DeepSeekCategoryUsage> Categories) BuildCategoryBreakdown(JsonElement? totalAmounts, JsonElement? totalCosts)
    {
        var modelTokens = new Dictionary<string, int>(StringComparer.Ordinal);
        var categoryTokens = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var categoryCosts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (totalAmounts?.ValueKind == JsonValueKind.Array)
        {
            foreach (var modelUsage in totalAmounts.Value.EnumerateArray())
            {
                var model = GetString(modelUsage, "model");
                var usage = GetArray(modelUsage, "usage");
                if (string.IsNullOrWhiteSpace(model) || usage?.ValueKind != JsonValueKind.Array)
                    continue;

                var modelTotal = 0;
                foreach (var item in UsageAmounts(usage.Value))
                {
                    if (!IsKnownTokenCategory(item.Type))
                        continue;

                    modelTotal += item.Amount;
                    categoryTokens[item.Type] = categoryTokens.GetValueOrDefault(item.Type) + item.Amount;
                }
                modelTokens[model] = modelTotal;
            }
        }

        if (totalCosts?.ValueKind == JsonValueKind.Array)
        {
            foreach (var modelUsage in totalCosts.Value.EnumerateArray())
            {
                var usage = GetArray(modelUsage, "usage");
                if (usage?.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in UsageCosts(usage.Value))
                {
                    if (!IsKnownTokenCategory(item.Type))
                        continue;

                    categoryCosts[item.Type] = categoryCosts.GetValueOrDefault(item.Type) + item.Amount;
                }
            }
        }

        var topModel = modelTokens
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .FirstOrDefault();

        var categories = new[]
        {
            "PROMPT_CACHE_HIT_TOKEN",
            "PROMPT_CACHE_MISS_TOKEN",
            "RESPONSE_TOKEN",
        }
            .Select(category => new DeepSeekCategoryUsage(
                category,
                categoryTokens.GetValueOrDefault(category),
                categoryCosts.TryGetValue(category, out var value) ? value : null))
            .ToArray();

        return (string.IsNullOrWhiteSpace(topModel.Key) ? null : topModel.Key, categories);
    }

    private static IEnumerable<UsageAmount> UsageAmounts(JsonElement usage)
    {
        foreach (var item in usage.EnumerateArray())
        {
            var type = NormalizeCategory(GetString(item, "type"));
            if (type is null)
                continue;

            yield return new UsageAmount(type, ParseIntAmount(GetProperty(item, "amount")));
        }
    }

    private static IEnumerable<UsageCost> UsageCosts(JsonElement usage)
    {
        foreach (var item in usage.EnumerateArray())
        {
            var type = NormalizeCategory(GetString(item, "type"));
            if (type is null)
                continue;

            yield return new UsageCost(type, ParseDoubleAmount(GetProperty(item, "amount")));
        }
    }

    private static double? SumTokenCosts(IEnumerable<UsageCost> costs)
    {
        double? total = null;
        foreach (var cost in costs)
        {
            if (!IsKnownTokenCategory(cost.Type))
                continue;

            total = (total ?? 0) + cost.Amount;
        }

        return total;
    }

    private static string UsageDescription(int tokens, int requests, double? cost, string currency)
    {
        var parts = new List<string> { $"{tokens.ToString("N0", CultureInfo.InvariantCulture)} tokens" };
        if (requests > 0)
            parts.Add($"{requests.ToString("N0", CultureInfo.InvariantCulture)} requests");
        if (cost is { } value)
            parts.Add($"{CurrencySymbol(currency)}{value.ToString("0.####", CultureInfo.InvariantCulture)}");
        return string.Join(" · ", parts);
    }

    private static DateTimeOffset NextUtcDay(DateTimeOffset now) =>
        new(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);

    private static DateTimeOffset NextUtcMonth(DateTimeOffset now) =>
        new(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1), TimeSpan.Zero);

    private static string CategoryLabel(string category) => category.ToUpperInvariant() switch
    {
        "PROMPT_CACHE_HIT_TOKEN" => "Cache hit tokens",
        "PROMPT_CACHE_MISS_TOKEN" => "Cache miss tokens",
        "RESPONSE_TOKEN" => "Response tokens",
        _ => category,
    };

    private static string CurrencySymbol(string currency) =>
        string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase) ? "¥" : "$";

    private static bool IsRequest(string type) =>
        string.Equals(type, "REQUEST", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownTokenCategory(string type) =>
        string.Equals(type, "PROMPT_CACHE_HIT_TOKEN", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "PROMPT_CACHE_MISS_TOKEN", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "RESPONSE_TOKEN", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeCategory(string? type)
    {
        var normalized = type?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static int ParseIntAmount(JsonElement? value)
    {
        if (value is null)
            return 0;
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var intValue))
            return intValue;
        return int.TryParse(value.Value.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static double ParseDoubleAmount(JsonElement? value)
    {
        if (value is null)
            return 0;
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var doubleValue))
            return doubleValue;
        return double.TryParse(value.Value.ToString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static JsonElement? GetProperty(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    private static JsonElement? GetArray(JsonElement? root, string name)
    {
        if (root is null)
            return null;

        var property = GetProperty(root.Value, name);
        return property?.ValueKind == JsonValueKind.Array ? property : null;
    }

    private static string? GetString(JsonElement? root, string name)
    {
        if (root is null)
            return null;

        var property = GetProperty(root.Value, name);
        return property?.ValueKind switch
        {
            JsonValueKind.String => property.Value.GetString(),
            JsonValueKind.Number => property.Value.ToString(),
            _ => null,
        };
    }

    private static int? TryGetInt(JsonElement root, string name)
    {
        var property = GetProperty(root, name);
        if (property is null)
            return null;
        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value))
            return value;
        return int.TryParse(property.Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private readonly record struct UsageAmount(string Type, int Amount);
    private readonly record struct UsageCost(string Type, double Amount);
}
