using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>
/// Official Codex provider. Reads Codex auth from CODEX_HOME/auth.json or
/// %USERPROFILE%\.codex\auth.json and calls the Codex/OpenAI usage endpoint.
/// </summary>
public sealed class CodexProvider : IProvider
{
    private const string DefaultChatGptBaseUrl = "https://chatgpt.com/backend-api";
    private const string WhamUsagePath = "/wham/usage";
    private const string CodexUsagePath = "/api/codex/usage";

    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;

    public CodexProvider()
        : this(SendGetAsync)
    {
    }

    internal CodexProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
    {
        _sendAsync = sendAsync;
    }

    public string Type => "codex";
    public string Name => "Codex";
    public string SourceLabel => "Codex OAuth API";
    public Confidence Confidence => Confidence.Official;

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var home = ResolveCodexHome(instanceId, config);
        var credentials = LoadCredentials(home)
            ?? throw new ProviderException("Login required: Codex auth.json not found. Run 'codex login' first.");

        var usageUrl = ResolveUsageUrl(instanceId, config, home);
        using var response = await SendUsageWithNetworkErrorsAsync(usageUrl, credentials, ct).ConfigureAwait(false);
        if ((int)response.StatusCode is 401 or 403)
            throw new ProviderException("Login required: Codex OAuth token expired or invalid. Run 'codex login' to re-authenticate.");
        if (!response.IsSuccessStatusCode)
            throw new ProviderException($"Network error: HTTP {(int)response.StatusCode}");

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return ParseUsage(document.RootElement.Clone(), credentials, DateTimeOffset.UtcNow);
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

    internal static ProviderSnapshot ParseUsage(JsonElement root, CodexCredentials credentials, DateTimeOffset updatedAt)
    {
        var plan = StringProperty(root, "plan_type") ?? PlanFromIdToken(credentials.IdToken);
        var displayPlan = PlanDisplay(plan);
        var rateLimit = ObjectProperty(root, "rate_limit");
        var primary = WindowFromProperty(rateLimit, "primary_window");
        var secondary = WindowFromProperty(rateLimit, "secondary_window");
        (primary, secondary) = NormalizeWindows(primary, secondary);
        var additionalWindows = AdditionalWindowsFromProperty(root, "additional_rate_limits");

        var credits = CreditsFromProperty(root, "credits");
        if (primary is null && secondary is null)
        {
            if (credits is null)
                throw new ProviderException("Parse error: no Codex usage windows found");

            primary = new RateWindow
            {
                Label = "Credits",
                UsedPercent = credits.Total > 0 ? 0.0 : 100.0,
                ResetDescription = $"{credits.Total.ToString("0.##", CultureInfo.InvariantCulture)} credits remaining",
            };
        }

        return new ProviderSnapshot
        {
            ProviderId = "codex",
            Name = string.IsNullOrWhiteSpace(displayPlan) ? "Codex" : $"Codex · {displayPlan}",
            PlanName = displayPlan,
            Primary = primary!,
            Secondary = secondary,
            AdditionalWindows = additionalWindows,
            Balance = credits,
            Accounts = new List<AccountInfo>
            {
                new()
                {
                    Email = EmailFromIdToken(credentials.IdToken),
                    Plan = displayPlan,
                    UsedPercent = primary!.UsedPercent,
                },
            },
            SourceLabel = "Codex OAuth API",
            Confidence = Confidence.Official,
            UpdatedAt = updatedAt,
        };
    }

    private async Task<HttpResponseMessage> SendUsageWithNetworkErrorsAsync(
        string usageUrl,
        CodexCredentials credentials,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, usageUrl);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credentials.AccessToken}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "QuotaLens");
            if (!string.IsNullOrWhiteSpace(credentials.AccountId))
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);

            return await _sendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Network error: {e.Message}", e);
        }
    }

    private static async Task<HttpResponseMessage> SendGetAsync(HttpRequestMessage request, CancellationToken ct)
    {
        return await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static CodexCredentials? LoadCredentials(string codexHome)
    {
        var path = Path.Combine(codexHome, "auth.json");
        if (!File.Exists(path))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var apiKey = StringProperty(root, "OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(apiKey))
                return new CodexCredentials(apiKey!, null, null);

            var tokens = ObjectProperty(root, "tokens");
            var accessToken = FirstNonEmpty(
                StringProperty(tokens, "access_token"),
                StringProperty(tokens, "accessToken"));
            if (string.IsNullOrWhiteSpace(accessToken))
                return null;

            var idToken = FirstNonEmpty(
                StringProperty(tokens, "id_token"),
                StringProperty(tokens, "idToken"));
            var accountId = FirstNonEmpty(
                StringProperty(tokens, "account_id"),
                StringProperty(tokens, "accountId"));
            return new CodexCredentials(accessToken!, idToken, accountId);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveCodexHome(string instanceId, IConfig config)
    {
        var configured = FirstNonEmpty(
            config.GetScoped(instanceId, "codex_home"),
            Environment.GetEnvironmentVariable("CODEX_HOME"),
            Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.Machine));
        if (!string.IsNullOrWhiteSpace(configured))
            return Environment.ExpandEnvironmentVariables(configured!.Trim().Trim('"'));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? ".codex" : Path.Combine(profile, ".codex");
    }

    private static string ResolveUsageUrl(string instanceId, IConfig config, string codexHome)
    {
        var configured = FirstNonEmpty(
            config.GetScoped(instanceId, "codex_chatgpt_base_url"),
            ChatGptBaseUrlFromConfigToml(codexHome),
            DefaultChatGptBaseUrl);
        var normalized = ValidatedChatGptBaseUrl(configured!);
        var path = normalized.Contains("/backend-api", StringComparison.OrdinalIgnoreCase)
            ? WhamUsagePath
            : CodexUsagePath;
        return normalized + path;
    }

    internal static string ValidatedChatGptBaseUrl(string value)
    {
        var trimmed = value.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
            trimmed = DefaultChatGptBaseUrl;

        if ((trimmed.StartsWith("https://chatgpt.com", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("https://chat.openai.com", StringComparison.OrdinalIgnoreCase))
            && !trimmed.Contains("/backend-api", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/backend-api";
        }

        return ProviderEndpointPolicy.RequireCredentialBase("codex", trimmed)
            .ToString()
            .TrimEnd('/');
    }

    private static string? ChatGptBaseUrlFromConfigToml(string codexHome)
    {
        var path = Path.Combine(codexHome, "config.toml");
        if (!File.Exists(path))
            return null;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            if (line.Length == 0)
                continue;

            var parts = line.Split('=', 2);
            if (parts.Length != 2 || parts[0].Trim() != "chatgpt_base_url")
                continue;

            return parts[1].Trim().Trim('"', '\'').Trim();
        }

        return null;
    }

    private static (RateWindow? Primary, RateWindow? Secondary) NormalizeWindows(RateWindow? primary, RateWindow? secondary)
    {
        var primaryRole = WindowRoleFor(primary);
        var secondaryRole = WindowRoleFor(secondary);
        if (primaryRole == WindowRole.Weekly && secondaryRole is WindowRole.Session or WindowRole.Unknown)
            return (secondary, primary);
        if (primary is null && secondaryRole is WindowRole.Session or WindowRole.Unknown)
            return (secondary, null);
        return (primary, secondary);
    }

    private static RateWindow? WindowFromProperty(JsonElement? parent, string key, string? label = null)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty(key, out var window)
            || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var usedPercent = OptionalDouble(window, "used_percent");
        var resetAt = OptionalLong(window, "reset_at");
        var windowSeconds = OptionalLong(window, "limit_window_seconds");
        if (usedPercent is null)
            return null;

        var windowMinutes = windowSeconds is > 0 ? windowSeconds.Value / 60 : (long?)null;
        var resetsAt = resetAt is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(resetAt.Value).ToString("O", CultureInfo.InvariantCulture)
            : null;

        return new RateWindow
        {
            Label = label ?? WindowLabel(windowMinutes),
            UsedPercent = Quota.ClampPercent(usedPercent.Value),
            ResetsAt = resetsAt,
            ResetDescription = resetsAt is null ? null : "resets",
            WindowMinutes = windowMinutes,
        };
    }

    private static List<RateWindow> AdditionalWindowsFromProperty(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var limits) || limits.ValueKind != JsonValueKind.Array)
            return new List<RateWindow>();

        var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var windows = new List<RateWindow>();
        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var window in AdditionalWindowsFromLimit(limit))
            {
                var dedupeKey = $"{window.Label}:{window.WindowMinutes}";
                if (usedLabels.Add(dedupeKey))
                    windows.Add(window);
            }
        }

        return windows;
    }

    private static IEnumerable<RateWindow> AdditionalWindowsFromLimit(JsonElement limit)
    {
        var rateLimit = ObjectProperty(limit, "rate_limit");
        if (rateLimit is null)
            yield break;

        if (IsSparkLimit(limit))
        {
            var primary = WindowFromProperty(rateLimit, "primary_window");
            if (primary is not null)
            {
                primary.Label = SparkWindowLabel(primary, "Codex Spark 5-hour");
                yield return primary;
            }

            var secondary = WindowFromProperty(rateLimit, "secondary_window");
            if (secondary is not null)
            {
                secondary.Label = SparkWindowLabel(secondary, "Codex Spark Weekly");
                yield return secondary;
            }

            yield break;
        }

        var label = FirstNonEmpty(
            StringProperty(limit, "limit_name"),
            StringProperty(limit, "metered_feature"))
            ?? "Codex extra limit";
        var window = WindowFromProperty(rateLimit, "primary_window", label)
            ?? WindowFromProperty(rateLimit, "secondary_window", label);
        if (window is not null)
            yield return window;
    }

    private static bool IsSparkLimit(JsonElement limit)
    {
        return new[]
            {
                StringProperty(limit, "limit_name"),
                StringProperty(limit, "metered_feature"),
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => value!.Contains("spark", StringComparison.OrdinalIgnoreCase));
    }

    private static string SparkWindowLabel(RateWindow window, string fallback)
    {
        return window.WindowMinutes switch
        {
            > 0 and <= 6 * 60 => "Codex Spark 5-hour",
            >= 6 * 24 * 60 => "Codex Spark Weekly",
            _ => fallback,
        };
    }

    private static BalanceInfo? CreditsFromProperty(JsonElement root, string key)
    {
        var credits = ObjectProperty(root, key);
        if (credits is not { ValueKind: JsonValueKind.Object } obj)
            return null;

        var balance = OptionalDouble(obj, "balance");
        if (balance is null)
            return null;

        return new BalanceInfo
        {
            Currency = "credits",
            Total = Math.Max(0, balance.Value),
            Paid = 0.0,
            Granted = Math.Max(0, balance.Value),
        };
    }

    private static WindowRole WindowRoleFor(RateWindow? window) => window?.WindowMinutes switch
    {
        300 => WindowRole.Session,
        10080 => WindowRole.Weekly,
        null => WindowRole.None,
        _ => WindowRole.Unknown,
    };

    private static string WindowLabel(long? windowMinutes)
    {
        if (windowMinutes == 300)
            return "5h Pool";
        if (windowMinutes == 10080)
            return "Weekly Pool";
        if (windowMinutes is > 0)
            return $"{windowMinutes.Value / 60.0:0.#}h Pool";
        return "Codex Usage";
    }

    private static string? EmailFromIdToken(string? idToken) => JwtStringClaim(idToken, "email")
        ?? JwtStringClaim(idToken, "https://api.openai.com/profile", "email");

    private static string? PlanFromIdToken(string? idToken) => JwtStringClaim(idToken, "chatgpt_plan_type")
        ?? JwtStringClaim(idToken, "https://api.openai.com/auth", "chatgpt_plan_type");

    private static string? JwtStringClaim(string? idToken, string key)
    {
        var payload = JwtPayload(idToken);
        return payload is null ? null : StringProperty(payload.Value, key);
    }

    private static string? JwtStringClaim(string? idToken, string objectKey, string key)
    {
        var payload = JwtPayload(idToken);
        var obj = payload is null ? null : ObjectProperty(payload.Value, objectKey);
        return obj is null ? null : StringProperty(obj.Value, key);
    }

    private static JsonElement? JwtPayload(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            return null;

        var parts = idToken.Split('.');
        if (parts.Length < 2)
            return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            while (payload.Length % 4 != 0)
                payload += "=";
            var data = Convert.FromBase64String(payload);
            using var document = JsonDocument.Parse(data);
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
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

    private static string? StringProperty(JsonElement? parent, string key)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty(key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static double? OptionalDouble(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };
    }

    private static long? OptionalLong(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? PlanDisplay(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
            return null;

        return plan.Replace("_", " ", StringComparison.Ordinal).Trim().ToLowerInvariant() switch
        {
            "plus" => "Plus",
            "pro" => "Pro",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "free" => "Free",
            "go" => "Go",
            var value => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value),
        };
    }

    internal sealed record CodexCredentials(string AccessToken, string? IdToken, string? AccountId);

    private enum WindowRole
    {
        None,
        Session,
        Weekly,
        Unknown,
    }
}
