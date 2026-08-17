using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuotaLens.Core;
using QuotaLens.Helpers;
using static QuotaLens.Core.JsonUtil;
using static QuotaLens.Core.TextUtil;

namespace QuotaLens.Providers;

/// <summary>
/// JetBrains AI quota probe. The AI Assistant stores quota metadata in the IDE
/// configuration directory, so this provider is intentionally local-file based
/// instead of API-key or WebView based.
/// </summary>
public sealed partial class JetBrainsProvider : IProvider
{
    private const string QuotaFileName = "AIAssistantQuotaManager2.xml";

    private static readonly (string Prefix, string DisplayName)[] IdePatterns =
    {
        ("IntelliJIdea", "IntelliJ IDEA"),
        ("PyCharm", "PyCharm"),
        ("WebStorm", "WebStorm"),
        ("GoLand", "GoLand"),
        ("CLion", "CLion"),
        ("DataGrip", "DataGrip"),
        ("RubyMine", "RubyMine"),
        ("Rider", "Rider"),
        ("PhpStorm", "PhpStorm"),
        ("AppCode", "AppCode"),
        ("Fleet", "Fleet"),
        ("AndroidStudio", "Android Studio"),
        ("RustRover", "RustRover"),
        ("Aqua", "Aqua"),
        ("DataSpell", "DataSpell"),
    };

    public string Type => "jetbrains";
    public string Name => "JetBrains AI";
    public string SourceLabel => "JetBrains local";
    public Confidence Confidence => Confidence.Official;

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var (path, ideName) = ResolveQuotaFile(instanceId, config)
            ?? throw new ProviderException("Not available: JetBrains AI quota file not found. Open a JetBrains IDE with AI Assistant enabled.");

        try
        {
            var xml = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return ParseQuotaXml(xml, ideName);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Parse error: Could not read JetBrains AI quota file: {e.Message}", e);
        }
    }

    internal static ProviderSnapshot ParseQuotaXml(string xml, string? ideName = null)
    {
        var component = ComponentRegex().Match(xml);
        if (!component.Success)
            throw new ProviderException("Parse error: AIAssistantQuotaManager2 component not found");

        var quotaRaw = ExtractOptionValue(component.Value, "quotaInfo");
        if (string.IsNullOrWhiteSpace(quotaRaw))
            throw new ProviderException("Parse error: JetBrains quotaInfo not found");

        var refillRaw = ExtractOptionValue(component.Value, "nextRefill");
        using var quotaDoc = JsonDocument.Parse(WebUtility.HtmlDecode(quotaRaw));
        var quota = quotaDoc.RootElement;
        var used = FirstDouble(quota, "current", "used") ?? 0;
        var max = FirstDouble(quota, "maximum", "max", "total") ?? 0;
        var tariffQuota = quota.TryGetProperty("tariffQuota", out var tariff) && tariff.ValueKind == JsonValueKind.Object
            ? tariff
            : default(JsonElement?);
        var available = tariffQuota is { } tq
            ? FirstDouble(tq, "available", "remaining")
            : null;
        available ??= FirstDouble(quota, "available", "remaining");
        available ??= Math.Max(0, max - used);

        var quotaUntil = FirstIso(quota, "until");
        string? refillAt = null;
        string? refillDescription = null;
        if (!string.IsNullOrWhiteSpace(refillRaw))
        {
            try
            {
                using var refillDoc = JsonDocument.Parse(WebUtility.HtmlDecode(refillRaw));
                var refill = refillDoc.RootElement;
                refillAt = FirstIso(refill, "next") ?? quotaUntil;
                var amount = FirstDouble(refill, "amount") ?? NestedDouble(refill, "tariff", "amount");
                var duration = FirstString(refill, "duration") ?? NestedString(refill, "tariff", "duration");
                refillDescription = amount is not null
                    ? $"{Fmt(amount.Value)} credit refill{(string.IsNullOrWhiteSpace(duration) ? "" : $" · {duration}")}"
                    : null;
            }
            catch (JsonException)
            {
                refillAt = quotaUntil;
            }
        }

        refillAt ??= quotaUntil;
        var label = FirstString(quota, "type") is { } type && !string.IsNullOrWhiteSpace(type)
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(type.Replace("_", " ", StringComparison.Ordinal).ToLowerInvariant())
            : "Credits";

        return new ProviderSnapshot
        {
            ProviderId = "jetbrains",
            Name = string.IsNullOrWhiteSpace(ideName) ? "JetBrains AI" : $"JetBrains AI · {ideName}",
            Primary = new RateWindow
            {
                Label = label,
                UsedPercent = max > 0 ? Quota.ClampPercent(used / max * 100) : 0,
                ResetsAt = refillAt,
                DetailText = $"{Fmt(used)} / {Fmt(max)} credits ({Fmt(available.Value)} available)",
                WindowMinutes = 30 * 24 * 60,
            },
            Secondary = refillDescription is null
                ? null
                : new RateWindow
                {
                    Label = I18n.T("quota.nextRefill"),
                    UsedPercent = 0,
                    ResetsAt = refillAt,
                    DetailText = refillDescription,
                },
            Balance = new BalanceInfo
            {
                Currency = "credits",
                Total = available.Value,
                Paid = used,
                Granted = max,
            },
            SourceLabel = "JetBrains local",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    private static (string Path, string IdeName)? ResolveQuotaFile(string instanceId, IConfig config)
    {
        var customBase = Clean(config.GetScoped(instanceId, "jetbrains_base_path"));
        if (customBase is not null)
        {
            var expanded = ExpandPath(customBase);
            var path = Path.Combine(expanded, "options", QuotaFileName);
            if (File.Exists(path))
                return (path, Path.GetFileName(expanded));
        }

        return DetectInstalledIdes()
            .Where(candidate => File.Exists(candidate.Path))
            .OrderByDescending(candidate => LastWrite(candidate.Path))
            .FirstOrDefault();
    }

    private static IEnumerable<(string Path, string IdeName)> DetectInstalledIdes()
    {
        foreach (var basePath in JetBrainsBasePaths().Where(Directory.Exists))
        {
            foreach (var directory in Directory.EnumerateDirectories(basePath))
            {
                var name = Path.GetFileName(directory);
                var match = IdePatterns.FirstOrDefault(pattern =>
                    name.StartsWith(pattern.Prefix, StringComparison.OrdinalIgnoreCase));
                if (match.Prefix is null)
                    continue;

                var version = name.Length > match.Prefix.Length ? name[match.Prefix.Length..] : "";
                var display = string.IsNullOrWhiteSpace(version) ? match.DisplayName : $"{match.DisplayName} {version}";
                yield return (Path.Combine(directory, "options", QuotaFileName), display);
            }
        }
    }

    private static IEnumerable<string> JetBrainsBasePaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            yield return Path.Combine(appData, "JetBrains");
            yield return Path.Combine(appData, "Google");
        }
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "JetBrains");
            yield return Path.Combine(localAppData, "Google");
        }
    }

    private static string? ExtractOptionValue(string component, string name)
    {
        foreach (var pattern in new[]
        {
            $"""<option[^>]*name\s*=\s*["']{Regex.Escape(name)}["'][^>]*value\s*=\s*["']([^"']*)["']""",
            $"""<option[^>]*value\s*=\s*["']([^"']*)["'][^>]*name\s*=\s*["']{Regex.Escape(name)}["']""",
        })
        {
            var match = Regex.Match(component, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }

    private static DateTimeOffset LastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || expanded.StartsWith("~" + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = Path.Combine(home, expanded[2..]);
        }
        return expanded;
    }


    private static double? NestedDouble(JsonElement obj, string parent, string child) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(parent, out var nested)
        && nested.ValueKind == JsonValueKind.Object
            ? FirstDouble(nested, child)
            : null;


    private static string? NestedString(JsonElement obj, string parent, string child) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(parent, out var nested)
        && nested.ValueKind == JsonValueKind.Object
            ? FirstString(nested, child)
            : null;

    private static string? FirstIso(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var text = FirstString(obj, key);
            if (text is null)
                continue;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return parsed.ToString("O", CultureInfo.InvariantCulture);
        }
        return null;
    }



    private static string Fmt(double value) =>
        value.ToString(value >= 1000 ? "F0" : "0.##", CultureInfo.InvariantCulture);

    [GeneratedRegex("""<component[^>]*name\s*=\s*["']AIAssistantQuotaManager2["'][^>]*>[\s\S]*?</component>""", RegexOptions.IgnoreCase)]
    private static partial Regex ComponentRegex();
}
