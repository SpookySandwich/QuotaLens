using System.Globalization;
using System.Net;
using System.Text.Json;
using QuotaLens.Core;
using QuotaLens.Helpers;
using static QuotaLens.Core.JsonUtil;

namespace QuotaLens.Providers;

public sealed class KiloProvider : IProvider
{
    private static readonly string[] Procedures =
    {
        "user.getCreditBlocks",
        "kiloPass.getState",
        "user.getAutoTopUpPaymentMethod",
    };

    public string Type => "kilo";
    public string Name => "Kilo";
    public string SourceLabel => "Kilo API";
    public Confidence Confidence => Confidence.Official;

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var token = ResolveToken(instanceId, config)
            ?? throw new ProviderException("Not configured: Kilo API key not set. Add it in Settings or run kilo login.");
        var configuredBaseUrl = ProviderConfig.Resolve(instanceId, config, "kilo", "kilo_base_url")
            ?? "https://app.kilo.ai/api/trpc";
        var baseUrl = ProviderEndpointPolicy.RequireCredentialBase(Type, configuredBaseUrl).ToString();
        var organizationId = ProviderConfig.Clean(config.GetScoped(instanceId, "kilo_organization_id"));

        using var response = await FetchAsync(token, baseUrl, organizationId, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ProviderException(await ApiErrorAsync(response, ct).ConfigureAwait(false));

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return Snapshot(ParseUsage(doc.RootElement), organizationId);
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

    internal static Uri BatchUri(string baseUrl)
    {
        var endpoint = ProviderConfig.AppendPath(baseUrl, string.Join(",", Procedures));
        var input = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, object?>>
        {
            ["0"] = new() { ["json"] = null },
            ["1"] = new() { ["json"] = null },
            ["2"] = new() { ["json"] = null },
        });
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return new Uri($"{endpoint}{separator}batch=1&input={Uri.EscapeDataString(input)}");
    }

    internal static KiloUsage ParseUsage(JsonElement root)
    {
        var entries = ResponseEntriesByIndex(root);
        var payloads = new Dictionary<int, JsonElement>();
        for (var index = 0; index < Procedures.Length; index++)
        {
            if (!entries.TryGetValue(index, out var entry))
                continue;

            if (TrpcError(entry) is { } error)
            {
                if (index != 2)
                    throw error;
                continue;
            }

            if (ResultPayload(entry) is { } payload)
                payloads[index] = payload;
        }

        var credits = CreditFields(payloads.GetValueOrDefault(0));
        var pass = PassFields(payloads.GetValueOrDefault(1));
        var planName = PlanName(payloads.GetValueOrDefault(1));
        var autoTopUp = AutoTopUpState(payloads.GetValueOrDefault(0), payloads.GetValueOrDefault(2));

        return new KiloUsage(
            credits.Used,
            credits.Total,
            credits.Remaining,
            pass.Used,
            pass.Total,
            pass.Remaining,
            pass.Bonus,
            pass.ResetsAt,
            planName,
            autoTopUp.Enabled,
            autoTopUp.Method);
    }

    internal static ProviderSnapshot Snapshot(KiloUsage usage, string? organizationId = null)
    {
        var planParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(usage.PlanName))
            planParts.Add(usage.PlanName!);
        if (usage.AutoTopUpEnabled is not null)
            planParts.Add(usage.AutoTopUpEnabled.Value
                ? $"Auto top-up: {usage.AutoTopUpMethod ?? "enabled"}"
                : "Auto top-up: off");

        return new ProviderSnapshot
        {
            ProviderId = "kilo",
            Name = "Kilo",
            PlanName = usage.PlanName,
            Primary = CreditsWindow(usage),
            Secondary = PassWindow(usage),
            Tertiary = planParts.Count > 0
                ? new RateWindow
                {
                    Label = string.IsNullOrWhiteSpace(organizationId) ? "Activity" : "Organization",
                    UsedPercent = 0,
                    DetailText = string.IsNullOrWhiteSpace(organizationId)
                        ? string.Join(" · ", planParts)
                        : $"{organizationId} · {string.Join(" · ", planParts)}",
                }
                : null,
            Balance = usage.CreditsRemaining is not null
                ? new BalanceInfo
                {
                    Currency = "credits",
                    Total = usage.CreditsRemaining.Value,
                    Paid = usage.CreditsUsed ?? 0,
                    Granted = usage.CreditsTotal ?? 0,
                }
                : null,
            SourceLabel = "Kilo API",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string? ResolveToken(string instanceId, IConfig config)
    {
        var configured = ProviderConfig.Resolve(instanceId, config, "kilo", "kilo_key");
        if (configured is not null)
            return configured;

        var authPath = ProviderConfig.Resolve(instanceId, config, "kilo", "kilo_auth_path")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share",
                "kilo",
                "auth.json");
        return AuthFileToken(authPath);
    }

    private static string? AuthFileToken(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("kilo", out var kilo)
                && kilo.TryGetProperty("access", out var access)
                    ? ProviderConfig.Clean(access.GetString())
                    : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task<HttpResponseMessage> FetchAsync(
        string token,
        string baseUrl,
        string? organizationId,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BatchUri(baseUrl));
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            if (!string.IsNullOrWhiteSpace(organizationId))
                request.Headers.TryAddWithoutValidation("X-KILOCODE-ORGANIZATIONID", organizationId);

            return await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Network error: {e.Message}", e);
        }
    }

    private static async Task<string> ApiErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var summary = ProviderConfig.ResponseSummary(body);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Not available: Kilo authentication failed.",
            HttpStatusCode.NotFound => "Not available: Kilo API endpoint was not found.",
            _ when (int)response.StatusCode >= 500 => $"Network error: Kilo API is unavailable. HTTP {(int)response.StatusCode}: {summary}",
            _ => $"Network error: HTTP {(int)response.StatusCode}: {summary}",
        };
    }

    private static RateWindow CreditsWindow(KiloUsage usage)
    {
        var total = ResolvedTotal(usage.CreditsUsed, usage.CreditsTotal, usage.CreditsRemaining);
        var used = ResolvedUsed(usage.CreditsUsed, total, usage.CreditsRemaining);
        return new RateWindow
        {
            Label = "Credits",
            UsedPercent = total is not null
                ? total.Value > 0 ? Quota.ClampPercent(used / total.Value * 100) : 100
                : 0,
            DetailText = total is not null ? $"{Compact(used)}/{Compact(total.Value)} credits" : I18n.T("quota.noUsageData"),
        };
    }

    private static RateWindow? PassWindow(KiloUsage usage)
    {
        var total = ResolvedTotal(usage.PassUsed, usage.PassTotal, usage.PassRemaining);
        if (total is null)
            return null;

        var used = ResolvedUsed(usage.PassUsed, total, usage.PassRemaining);
        var bonus = Math.Max(0, usage.PassBonus ?? 0);
        var baseCredits = Math.Max(0, total.Value - bonus);
        var detail = $"${Currency(used)} / ${Currency(baseCredits)}";
        if (bonus > 0)
            detail += $" (+ ${Currency(bonus)} bonus)";

        return new RateWindow
        {
            Label = "Kilo Pass",
            UsedPercent = total.Value > 0 ? Quota.ClampPercent(used / total.Value * 100) : 100,
            ResetsAt = usage.PassResetsAt,
            DetailText = detail,
        };
    }

    private static IReadOnlyDictionary<int, JsonElement> ResponseEntriesByIndex(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray()
                .Take(Procedures.Length)
                .Select((entry, index) => (index, entry: entry.Clone()))
                .ToDictionary(item => item.index, item => item.entry);
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            var entries = new Dictionary<int, JsonElement>();
            foreach (var property in root.EnumerateObject())
            {
                if (int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    && index >= 0
                    && index < Procedures.Length)
                {
                    entries[index] = property.Value.Clone();
                }
            }

            if (entries.Count > 0)
                return entries;
        }

        throw new ProviderException("Parse error: unexpected Kilo tRPC batch shape.");
    }

    private static ProviderException? TrpcError(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("error", out var error))
            return null;

        var code = StringPath(error, "json", "data", "code");
        return string.Equals(code, "UNAUTHORIZED", StringComparison.OrdinalIgnoreCase)
            ? new ProviderException("Not available: Kilo authentication failed.")
            : new ProviderException($"Parse error: Kilo tRPC error {code ?? "unknown"}.");
    }

    private static JsonElement? ResultPayload(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("result", out var result))
            return null;
        if (result.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("json", out var json))
                return json.Clone();
            return data.Clone();
        }

        return result.Clone();
    }

    private static (double? Used, double? Total, double? Remaining) CreditFields(JsonElement payload)
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return (null, null, null);

        var contexts = DictionaryContexts(payload).ToList();
        var creditBlocks = FirstArray(contexts, "creditBlocks");
        if (creditBlocks is not null)
        {
            var total = 0d;
            var remaining = 0d;
            var sawTotal = false;
            var sawRemaining = false;
            foreach (var block in creditBlocks.Value.EnumerateArray())
            {
                if (OptionalDouble(block, "amount_mUsd") is { } amount)
                {
                    total += amount / 1_000_000;
                    sawTotal = true;
                }

                if (OptionalDouble(block, "balance_mUsd") is { } balanceMicroUsd)
                {
                    remaining += balanceMicroUsd / 1_000_000;
                    sawRemaining = true;
                }
            }

            if (sawTotal || sawRemaining)
            {
                var resolvedTotal = sawTotal ? Math.Max(0, total) : (double?)null;
                var resolvedRemaining = sawRemaining ? Math.Max(0, remaining) : (double?)null;
                var used = resolvedTotal is not null && resolvedRemaining is not null
                    ? Math.Max(0, resolvedTotal.Value - resolvedRemaining.Value)
                    : (double?)null;
                return (used, resolvedTotal, resolvedRemaining);
            }
        }

        var blockContexts = FirstArray(contexts, "blocks") is { } blocks
            ? blocks.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToList()
            : new List<JsonElement>();
        var usedValue = FirstDouble(blockContexts, "used", "usedCredits", "consumed", "spent", "creditsUsed")
            ?? FirstDouble(contexts, "used", "usedCredits", "creditsUsed", "consumed", "spent");
        var totalValue = FirstDouble(blockContexts, "total", "totalCredits", "creditsTotal", "limit")
            ?? FirstDouble(contexts, "total", "totalCredits", "creditsTotal", "limit");
        var remainingValue = FirstDouble(blockContexts, "remaining", "remainingCredits", "creditsRemaining")
            ?? FirstDouble(contexts, "remaining", "remainingCredits", "creditsRemaining");

        if (totalValue is null && usedValue is not null && remainingValue is not null)
            totalValue = usedValue + remainingValue;
        if (usedValue is null && totalValue is not null && remainingValue is not null)
            usedValue = Math.Max(0, totalValue.Value - remainingValue.Value);

        if (usedValue is null && totalValue is null && remainingValue is null
            && FirstDouble(contexts, "totalBalance_mUsd") is { } balance)
        {
            var converted = Math.Max(0, balance / 1_000_000);
            return balance == 0 ? (0, 0, 0) : (0, converted, converted);
        }

        return (usedValue, totalValue, remainingValue);
    }

    private static (double? Used, double? Total, double? Remaining, double? Bonus, string? ResetsAt) PassFields(JsonElement payload)
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return (null, null, null, null, null);

        if (SubscriptionData(payload) is { } subscription)
        {
            var used = OptionalDouble(subscription, "currentPeriodUsageUsd");
            var baseCredits = OptionalDouble(subscription, "currentPeriodBaseCreditsUsd");
            var bonus = Math.Max(0, OptionalDouble(subscription, "currentPeriodBonusCreditsUsd") ?? 0);
            var total = baseCredits is not null ? baseCredits + bonus : null;
            var remaining = total is not null && used is not null ? Math.Max(0, total.Value - used.Value) : (double?)null;
            var reset = FirstDateIso(new[] { subscription }, "nextBillingAt", "nextRenewalAt", "renewsAt", "renewAt");
            return (used, total, remaining, bonus > 0 ? bonus : null, reset);
        }

        var contexts = DictionaryContexts(payload).ToList();
        var totalValue = MoneyAmount(
            contexts,
            centsKeys: new[] { "amountCents", "totalCents", "planAmountCents", "monthlyAmountCents", "limitCents", "includedCents", "valueCents" },
            microUsdKeys: new[] { "amount_mUsd", "total_mUsd", "planAmount_mUsd", "limit_mUsd", "included_mUsd", "value_mUsd" },
            plainKeys: new[] { "amount", "total", "limit", "included", "value", "creditsTotal", "totalCredits", "planAmount" });
        var usedValue = MoneyAmount(
            contexts,
            centsKeys: new[] { "usedCents", "spentCents", "consumedCents", "usedAmountCents", "consumedAmountCents" },
            microUsdKeys: new[] { "used_mUsd", "spent_mUsd", "consumed_mUsd", "usedAmount_mUsd" },
            plainKeys: new[] { "used", "spent", "consumed", "usage", "creditsUsed", "usedAmount", "consumedAmount" });
        var remainingValue = MoneyAmount(
            contexts,
            centsKeys: new[] { "remainingCents", "remainingAmountCents", "availableCents", "leftCents", "balanceCents" },
            microUsdKeys: new[] { "remaining_mUsd", "available_mUsd", "left_mUsd", "balance_mUsd" },
            plainKeys: new[] { "remaining", "available", "left", "balance", "creditsRemaining", "remainingAmount", "availableAmount" });
        var bonusValue = MoneyAmount(
            contexts,
            centsKeys: new[] { "bonusCents", "bonusAmountCents", "includedBonusCents", "bonusRemainingCents" },
            microUsdKeys: new[] { "bonus_mUsd", "bonusAmount_mUsd" },
            plainKeys: new[] { "bonus", "bonusAmount", "bonusCredits", "includedBonus" });
        var resetsAt = FirstDateIso(contexts, "resetAt", "resetsAt", "nextResetAt", "renewAt", "renewsAt", "nextRenewalAt", "currentPeriodEnd", "periodEndsAt", "expiresAt", "expiryAt");

        if (totalValue is null && usedValue is not null && remainingValue is not null)
            totalValue = usedValue + remainingValue;
        if (usedValue is null && totalValue is not null && remainingValue is not null)
            usedValue = Math.Max(0, totalValue.Value - remainingValue.Value);
        if (remainingValue is null && totalValue is not null && usedValue is not null)
            remainingValue = Math.Max(0, totalValue.Value - usedValue.Value);

        return (usedValue, totalValue, remainingValue, bonusValue, resetsAt);
    }

    private static string? PlanName(JsonElement payload)
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        if (SubscriptionData(payload) is { } subscription)
        {
            var tier = OptionalString(subscription, "tier");
            return tier is null ? "Kilo Pass" : PlanNameForTier(tier);
        }

        var contexts = DictionaryContexts(payload).ToList();
        return FirstString(contexts, "planName", "tier", "tierName", "passName", "subscriptionName")
            ?? StringPathFromContexts(contexts, new[] { "plan", "name" })
            ?? StringPathFromContexts(contexts, new[] { "subscription", "plan", "name" })
            ?? StringPathFromContexts(contexts, new[] { "subscription", "name" })
            ?? StringPathFromContexts(contexts, new[] { "pass", "name" })
            ?? StringPathFromContexts(contexts, new[] { "state", "name" });
    }

    private static (bool? Enabled, string? Method) AutoTopUpState(JsonElement creditBlocksPayload, JsonElement autoTopUpPayload)
    {
        var creditContexts = creditBlocksPayload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new List<JsonElement>()
            : DictionaryContexts(creditBlocksPayload).ToList();
        var autoContexts = autoTopUpPayload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new List<JsonElement>()
            : DictionaryContexts(autoTopUpPayload).ToList();

        var enabled = FirstBool(autoContexts, "enabled", "isEnabled", "active")
            ?? BoolFromStatus(FirstString(autoContexts, "status"))
            ?? FirstBool(creditContexts, "autoTopUpEnabled");
        var rawMethod = FirstString(autoContexts, "paymentMethod", "paymentMethodType", "method", "cardBrand");
        var amount = MoneyAmount(autoContexts, new[] { "amountCents" }, Array.Empty<string>(), new[] { "amount", "topUpAmount", "amountUsd" });
        return (enabled, rawMethod ?? (amount is > 0 ? $"${CompactCurrency(amount.Value)}" : null));
    }

    private static IEnumerable<JsonElement> DictionaryContexts(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            yield break;

        var queue = new Queue<(JsonElement Element, int Depth)>();
        queue.Enqueue((payload, 0));
        while (queue.Count > 0)
        {
            var (element, depth) = queue.Dequeue();
            yield return element;
            if (depth >= 2)
                continue;

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                    queue.Enqueue((property.Value, depth + 1));
                else if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                            queue.Enqueue((item, depth + 1));
                    }
                }
            }
        }
    }

    private static JsonElement? FirstArray(IEnumerable<JsonElement> contexts, params string[] keys)
    {
        foreach (var context in contexts)
            foreach (var key in keys)
                if (context.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
                    return value;
        return null;
    }

    private static JsonElement? SubscriptionData(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        if (payload.TryGetProperty("subscription", out var subscription))
            return subscription.ValueKind == JsonValueKind.Object ? subscription : null;
        return payload.TryGetProperty("currentPeriodUsageUsd", out _)
            || payload.TryGetProperty("currentPeriodBaseCreditsUsd", out _)
            || payload.TryGetProperty("currentPeriodBonusCreditsUsd", out _)
            || payload.TryGetProperty("tier", out _)
                ? payload
                : null;
    }

    private static double? MoneyAmount(IEnumerable<JsonElement> contexts, string[] centsKeys, string[] microUsdKeys, string[] plainKeys)
    {
        if (FirstDouble(contexts, centsKeys) is { } cents)
            return cents / 100;
        if (FirstDouble(contexts, microUsdKeys) is { } microUsd)
            return microUsd / 1_000_000;
        return FirstDouble(contexts, plainKeys);
    }



    private static bool? FirstBool(IEnumerable<JsonElement> contexts, params string[] keys)
    {
        foreach (var context in contexts)
            foreach (var key in keys)
                if (BoolProperty(context, key) is { } value)
                    return value;
        return null;
    }




    private static bool? BoolProperty(JsonElement obj, string key)
    {
        var text = OptionalString(obj, key);
        if (text is null)
            return null;
        return bool.TryParse(text, out var parsed)
            ? parsed
            : BoolFromStatus(text);
    }


    private static string? StringPath(JsonElement root, params string[] path)
    {
        var cursor = root;
        foreach (var segment in path)
        {
            if (cursor.ValueKind != JsonValueKind.Object || !cursor.TryGetProperty(segment, out cursor))
                return null;
        }

        return cursor.ValueKind == JsonValueKind.String ? ProviderConfig.Clean(cursor.GetString()) : null;
    }

    private static string? StringPathFromContexts(IEnumerable<JsonElement> contexts, string[] path)
    {
        foreach (var context in contexts)
            if (StringPath(context, path) is { } value)
                return value;
        return null;
    }

    private static bool? BoolFromStatus(string? status)
    {
        var normalized = ProviderConfig.Clean(status)?.ToLowerInvariant();
        return normalized switch
        {
            "enabled" or "active" or "on" or "true" or "1" or "yes" => true,
            "disabled" or "inactive" or "off" or "false" or "0" or "no" or "none" => false,
            _ => null,
        };
    }


    private static string PlanNameForTier(string tier) =>
        tier switch
        {
            "tier_19" => "Starter",
            "tier_49" => "Pro",
            "tier_199" => "Expert",
            _ => tier,
        };

    private static double? ResolvedTotal(double? used, double? total, double? remaining) =>
        total is not null ? Math.Max(0, total.Value)
        : used is not null && remaining is not null ? Math.Max(0, used.Value + remaining.Value)
        : null;

    private static double ResolvedUsed(double? used, double? total, double? remaining) =>
        used is not null ? Math.Max(0, used.Value)
        : total is not null && remaining is not null ? Math.Max(0, total.Value - remaining.Value)
        : 0;

    private static string Compact(double value) =>
        value == Math.Truncate(value)
            ? value.ToString("F0", CultureInfo.InvariantCulture)
            : value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Currency(double value) =>
        Math.Max(0, value).ToString("F2", CultureInfo.InvariantCulture);

    private static string CompactCurrency(double value) =>
        value == Math.Truncate(value)
            ? value.ToString("F0", CultureInfo.InvariantCulture)
            : value.ToString("F2", CultureInfo.InvariantCulture);

    internal sealed record KiloUsage(
        double? CreditsUsed,
        double? CreditsTotal,
        double? CreditsRemaining,
        double? PassUsed,
        double? PassTotal,
        double? PassRemaining,
        double? PassBonus,
        string? PassResetsAt,
        string? PlanName,
        bool? AutoTopUpEnabled,
        string? AutoTopUpMethod);
}
