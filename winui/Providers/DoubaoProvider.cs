using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaLens.Core;

namespace QuotaLens.Providers;

public sealed class DoubaoProvider : IProvider
{
    private const int MaxOutputBytes = 256 * 1024;
    private static readonly TimeSpan ArkcliTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] UsagePlanArguments = { "usage", "plan", "--format", "json" };

    private readonly ArkcliRunner _runArkcli;

    public DoubaoProvider()
        : this(RunArkcliAsync)
    {
    }

    internal DoubaoProvider(ArkcliRunner runArkcli)
    {
        _runArkcli = runArkcli ?? throw new ArgumentNullException(nameof(runArkcli));
    }

    internal delegate Task<string> ArkcliRunner(
        string binary,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    public string Type => "doubao";
    public string Name => "Doubao";
    public string SourceLabel => "arkcli usage plan";
    public Confidence Confidence => Confidence.SemiOfficial;

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var binary = ResolveArkcliPath(instanceId, config);
        var output = await _runArkcli(binary, UsagePlanArguments, ct).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(output) > MaxOutputBytes)
            throw new ProviderException("Not available: arkcli returned more than 256 KiB of usage output.");

        return ParseArkcliUsage(output, DateTimeOffset.UtcNow);
    }

    internal static ProviderSnapshot ParseArkcliUsage(string json, DateTimeOffset now)
    {
        ArkcliUsageResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<ArkcliUsageResponse>(json);
        }
        catch (JsonException e)
        {
            throw new ProviderException($"Parse error: arkcli usage output is invalid JSON: {e.Message}", e);
        }

        if (response?.Items is null)
            throw new ProviderException("Parse error: arkcli usage output is missing items.");

        var authMethod = ProviderConfig.Clean(response.Viewer?.AuthMethod);
        if (string.Equals(authMethod, "none", StringComparison.OrdinalIgnoreCase))
            throw new ProviderException("Not available: arkcli is not authenticated. Sign in with arkcli, then refresh.");

        var windows = new List<RateWindow>();
        DateTimeOffset? updatedAt = null;
        foreach (var item in response.Items)
        {
            var productLabel = ProductLabel(item.Product);
            if (productLabel is null || item.Subscribed == false)
                continue;

            if (item.Periods is not { Count: > 0 })
            {
                var detail = CompactText(item.Error);
                throw new ProviderException(
                    $"Not available: arkcli returned incomplete {productLabel} usage"
                    + (detail is null ? "." : $": {detail}"));
            }

            if (ParseTimestamp(item.UpdatedAt) is { } itemUpdatedAt
                && (updatedAt is null || itemUpdatedAt > updatedAt))
            {
                updatedAt = itemUpdatedAt;
            }

            foreach (var period in item.Periods)
            {
                var label = ProviderConfig.Clean(period.Label)
                    ?? throw new ProviderException($"Parse error: arkcli {productLabel} period is missing a label.");
                var percent = period.Percent
                    ?? throw new ProviderException($"Parse error: arkcli {productLabel} period '{label}' is missing percent.");

                windows.Add(new RateWindow
                {
                    Label = $"{productLabel} · {PeriodLabel(label)}",
                    AvailabilityGroup = productLabel,
                    UsedPercent = Quota.ClampPercent(percent),
                    ResetsAt = ParseTimestamp(period.ResetAt)?.ToString("O", CultureInfo.InvariantCulture),
                    WindowMinutes = WindowMinutes(label),
                    CountsForAvailability = true,
                });
            }
        }

        if (windows.Count == 0)
        {
            var detail = response.Items
                .Select(item => CompactText(item.Error))
                .FirstOrDefault(error => error is not null);
            throw new ProviderException(
                "Not available: arkcli returned no subscribed Coding or Agent Plan usage"
                + (detail is null ? "." : $": {detail}"));
        }

        return new ProviderSnapshot
        {
            ProviderId = "doubao",
            Name = "Doubao",
            Primary = windows[0],
            Secondary = windows.ElementAtOrDefault(1),
            Tertiary = windows.ElementAtOrDefault(2),
            AdditionalWindows = windows.Skip(3).ToList(),
            SourceLabel = "arkcli usage plan",
            Confidence = Confidence.SemiOfficial,
            EntitlementStatus = EntitlementStatus.Active,
            UpdatedAt = updatedAt ?? now,
        };
    }

    internal static ProcessStartInfo CreateArkcliStartInfo(string binary, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static string ResolveArkcliPath(string instanceId, IConfig config) =>
        ProviderConfig.Clean(config.GetScoped(instanceId, "doubao_cli_path"))
        ?? ProviderConfig.Clean(Environment.GetEnvironmentVariable("ARKCLI_PATH"))
        ?? ProviderConfig.Clean(Environment.GetEnvironmentVariable("DOUBAO_ARKCLI_PATH"))
        ?? "arkcli";

    private static async Task<string> RunArkcliAsync(
        string binary,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        using var process = new Process { StartInfo = CreateArkcliStartInfo(binary, arguments) };
        try
        {
            if (!process.Start())
                throw new ProviderException("Not available: arkcli did not start.");
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new ProviderException(
                "Not configured: arkcli was not found. Configure the path to an existing authenticated arkcli installation.",
                e);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ArkcliTimeout);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(output[0]) + Encoding.UTF8.GetByteCount(output[1]) > MaxOutputBytes)
                throw new ArkcliOutputLimitException();

            if (process.ExitCode != 0)
            {
                var detail = CompactText(output[1]) ?? CompactText(output[0]) ?? "unknown error";
                if (IsAuthenticationError(detail))
                    throw new ProviderException("Not available: arkcli is not authenticated. Sign in with arkcli, then refresh.");

                throw new ProviderException($"Not available: arkcli usage failed ({process.ExitCode}): {detail}");
            }

            return output[0];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKillTree(process);
            await ObserveReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw new ProviderException("Not available: arkcli usage timed out after 15 seconds.");
        }
        catch (ArkcliOutputLimitException)
        {
            TryKillTree(process);
            await ObserveReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw new ProviderException("Not available: arkcli returned more than 256 KiB of usage output.");
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            await ObserveReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                return output.ToString();
            if (output.Length + read > MaxOutputBytes)
                throw new ArkcliOutputLimitException();
            output.Append(buffer, 0, read);
        }
    }

    private static async Task ObserveReadersAsync(params Task<string>[] readers)
    {
        foreach (var reader in readers)
        {
            try
            {
                await reader.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static bool IsAuthenticationError(string message) =>
        new[]
        {
            "not logged in",
            "not authenticated",
            "authentication required",
            "login required",
            "please login",
            "please log in",
        }.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string? ProductLabel(string? product) =>
        ProviderConfig.Clean(product)?.ToLowerInvariant() switch
        {
            "coding-plan" => "Coding Plan",
            "agent-plan" => "Agent Plan",
            "coding-plan-team" => "Coding Team Plan",
            "agent-plan-team" => "Agent Team Plan",
            _ => null,
        };

    private static string PeriodLabel(string label)
    {
        var normalized = NormalizeLevel(label);
        return normalized switch
        {
            "session" or "5hour" or "fivehour" or "5h" => "5-hour",
            "weekly" or "week" => "Weekly",
            "monthly" or "month" => "Monthly",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                label.Replace('_', ' ').Replace('-', ' ').Trim().ToLowerInvariant()),
        };
    }

    private static long? WindowMinutes(string label) =>
        NormalizeLevel(label) switch
        {
            "session" or "5hour" or "fivehour" or "5h" => 5 * 60,
            "weekly" or "week" => 7 * 24 * 60,
            "monthly" or "month" => 30 * 24 * 60,
            _ => null,
        };

    private static string NormalizeLevel(string label) =>
        new(label.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static DateTimeOffset? ParseTimestamp(JsonElement? element)
    {
        if (element is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = ProviderConfig.Clean(value.GetString());
            if (text is null)
                return null;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
                return FromEpoch(numeric);
            return DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? FromEpoch(number)
            : null;
    }

    private static DateTimeOffset? FromEpoch(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
            return null;

        try
        {
            var milliseconds = value >= 1e11 ? value : value * 1000;
            return DateTimeOffset.FromUnixTimeMilliseconds(checked((long)Math.Round(milliseconds)));
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static string? CompactText(string? value)
    {
        var cleaned = ProviderConfig.Clean(value);
        if (cleaned is null)
            return null;

        var compact = string.Join(" ", cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 300 ? compact : compact[..300] + "...";
    }

    private sealed class ArkcliOutputLimitException : Exception
    {
    }

    private sealed class ArkcliUsageResponse
    {
        [JsonPropertyName("viewer")]
        public ArkcliViewer? Viewer { get; set; }

        [JsonPropertyName("items")]
        public List<ArkcliUsageItem>? Items { get; set; }
    }

    private sealed class ArkcliViewer
    {
        [JsonPropertyName("auth_method")]
        public string? AuthMethod { get; set; }
    }

    private sealed class ArkcliUsageItem
    {
        [JsonPropertyName("product")]
        public string? Product { get; set; }

        [JsonPropertyName("subscribed")]
        public bool? Subscribed { get; set; }

        [JsonPropertyName("periods")]
        public List<ArkcliPeriod>? Periods { get; set; }

        [JsonPropertyName("updated_at")]
        public JsonElement? UpdatedAt { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class ArkcliPeriod
    {
        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("percent")]
        public double? Percent { get; set; }

        [JsonPropertyName("reset_at")]
        public JsonElement? ResetAt { get; set; }
    }
}
