using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>
/// Grok CLI quota provider. Ports CodexBar's `grok agent stdio` JSON-RPC flow:
/// initialize the ACP session, then call the x.ai/billing extension method.
/// </summary>
public sealed class GrokProvider : IProvider
{
    public string Type => "grok";
    public string Name => "Grok";
    public string SourceLabel => "grok agent stdio";
    public Confidence Confidence => Confidence.Official;

    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var binary = ResolveGrokPath(config.GetScoped(instanceId, "grok_path"));
        using var client = new GrokRpcClient(binary);

        try
        {
            await client.StartAsync(ct).ConfigureAwait(false);
            await client.RequestAsync("initialize", new
            {
                protocolVersion = "1",
                clientCapabilities = new
                {
                    fs = new { readTextFile = false, writeTextFile = false },
                    terminal = false,
                },
            }, InitializeTimeout, ct).ConfigureAwait(false);

            var billingJson = await client.RequestResultAsync("x.ai/billing", new { }, RequestTimeout, ct).ConfigureAwait(false);
            var billing = ParseBilling(billingJson);
            return Snapshot(billing);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Not available: Grok CLI failed: {e.Message}", e);
        }
        finally
        {
            client.Kill();
        }
    }

    internal static GrokBilling ParseBilling(string json)
    {
        try
        {
            var billing = JsonSerializer.Deserialize<GrokBilling>(json, JsonOptions);
            if (billing is null)
                throw new ProviderException("Parse error: Grok billing response was empty");
            return billing;
        }
        catch (JsonException e)
        {
            throw new ProviderException($"Parse error: Invalid Grok billing JSON: {e.Message}", e);
        }
    }

    internal static ProviderSnapshot Snapshot(GrokBilling billing, DateTimeOffset? updatedAt = null)
    {
        var monthlyLimitCents = billing.MonthlyLimit?.Val ?? 0;
        var includedUsedCents = billing.Usage?.IncludedUsed?.Val ?? 0;
        var totalUsedCents = billing.Usage?.TotalUsed?.Val ?? includedUsedCents;
        var onDemandUsedCents = billing.Usage?.OnDemandUsed?.Val ?? Math.Max(0, totalUsedCents - includedUsedCents);
        var onDemandCapCents = billing.OnDemandCap?.Val;
        var resetsAt = ParseIso(billing.BillingCycle?.BillingPeriodEnd);
        var startsAt = ParseIso(billing.BillingCycle?.BillingPeriodStart);
        var windowMinutes = startsAt is not null && resetsAt is not null && resetsAt > startsAt
            ? (long?)Math.Round((resetsAt.Value - startsAt.Value).TotalMinutes)
            : null;

        var usedPercent = monthlyLimitCents > 0
            ? Quota.ClampPercent((double)totalUsedCents / monthlyLimitCents * 100.0)
            : 0;

        var primaryDescription = monthlyLimitCents > 0
            ? $"{Usd(totalUsedCents)} / {Usd(monthlyLimitCents)} included"
            : $"{Usd(totalUsedCents)} used";

        var secondary = onDemandUsedCents > 0 || onDemandCapCents is > 0
            ? new RateWindow
            {
                Label = "On-demand",
                UsedPercent = onDemandCapCents is > 0
                    ? Quota.ClampPercent((double)onDemandUsedCents / onDemandCapCents.Value * 100.0)
                    : onDemandUsedCents > 0 ? 100 : 0,
                ResetsAt = resetsAt?.ToString("O", CultureInfo.InvariantCulture),
                ResetDescription = onDemandCapCents is > 0
                    ? $"{Usd(onDemandUsedCents)} / {Usd(onDemandCapCents.Value)} cap"
                    : $"{Usd(onDemandUsedCents)} used",
                WindowMinutes = windowMinutes,
            }
            : null;

        return new ProviderSnapshot
        {
            ProviderId = "grok",
            Name = "Grok",
            Primary = new RateWindow
            {
                Label = "Monthly included",
                UsedPercent = usedPercent,
                ResetsAt = resetsAt?.ToString("O", CultureInfo.InvariantCulture),
                ResetDescription = primaryDescription,
                WindowMinutes = windowMinutes,
            },
            Secondary = secondary,
            Balance = new BalanceInfo
            {
                Currency = "USD",
                Total = Math.Max(0, (monthlyLimitCents - totalUsedCents) / 100.0),
                Paid = totalUsedCents / 100.0,
                Granted = monthlyLimitCents / 100.0,
            },
            SourceLabel = "grok agent stdio",
            Confidence = Confidence.Official,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
        };
    }

    internal static string ResolveGrokPath(string? configured)
    {
        var explicitPath = ProviderConfig.Clean(configured)
            ?? ProviderConfig.Environment("GROK_CLI_PATH");
        if (explicitPath is not null)
            return Environment.ExpandEnvironmentVariables(explicitPath);

        return "grok";
    }

    private static DateTimeOffset? ParseIso(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string Usd(int cents)
    {
        var dollars = cents / 100.0;
        return dollars < 100
            ? $"${dollars.ToString("F2", CultureInfo.InvariantCulture)}"
            : $"${dollars.ToString("F0", CultureInfo.InvariantCulture)}";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal sealed class GrokBilling
    {
        [JsonPropertyName("billingCycle")] public GrokBillingCycle? BillingCycle { get; set; }
        [JsonPropertyName("monthlyLimit")] public GrokCent? MonthlyLimit { get; set; }
        [JsonPropertyName("onDemandCap")] public GrokCent? OnDemandCap { get; set; }
        [JsonPropertyName("on_demand_enabled")] public bool? OnDemandEnabledSnake { get; set; }
        [JsonPropertyName("onDemandEnabled")] public bool? OnDemandEnabled { get; set; }
        [JsonPropertyName("disabledByConfig")] public bool? DisabledByConfig { get; set; }
        [JsonPropertyName("usage")] public GrokBillingUsage? Usage { get; set; }
    }

    internal sealed class GrokBillingCycle
    {
        [JsonPropertyName("billingPeriodStart")] public string? BillingPeriodStart { get; set; }
        [JsonPropertyName("billingPeriodEnd")] public string? BillingPeriodEnd { get; set; }
    }

    internal sealed class GrokBillingUsage
    {
        [JsonPropertyName("includedUsed")] public GrokCent? IncludedUsed { get; set; }
        [JsonPropertyName("onDemandUsed")] public GrokCent? OnDemandUsed { get; set; }
        [JsonPropertyName("totalUsed")] public GrokCent? TotalUsed { get; set; }
    }

    internal sealed class GrokCent
    {
        [JsonPropertyName("val")] public int? Val { get; set; }
    }

    private sealed class GrokRpcClient : IDisposable
    {
        private readonly string _binary;
        private Process? _process;
        private int _nextId = 1;

        public GrokRpcClient(string binary)
        {
            _binary = binary;
        }

        public Task StartAsync(CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _binary,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("agent");
            psi.ArgumentList.Add("stdio");

            var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception e)
            {
                throw new ProviderException($"Not available: Grok CLI not found at {_binary}: {e.Message}", e);
            }

            _process = process;
            return Task.CompletedTask;
        }

        public async Task<JsonElement> RequestAsync(string method, object parameters, TimeSpan timeout, CancellationToken ct)
        {
            var id = _nextId++;
            var payload = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters,
            }, JsonOptions).Replace("\\/", "/", StringComparison.Ordinal);

            var process = _process ?? throw new ProviderException("Not available: Grok CLI process was not started");
            await process.StandardInput.WriteLineAsync(payload.AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            while (!timeoutCts.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Kill();
                    throw new ProviderException($"Timeout: Grok RPC timed out on {method}");
                }

                if (line is null)
                {
                    var stderr = await ReadStderrIfReadyAsync(process).ConfigureAwait(false);
                    throw new ProviderException(ProviderConfig.Clean(stderr) ?? "Malformed Grok RPC response: stdout closed");
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var responseId) || JsonId(responseId) != id)
                        continue;

                    if (root.TryGetProperty("error", out var error))
                        throw new ProviderException(GrokErrorMessage(error));

                    if (!root.TryGetProperty("result", out var result))
                        throw new ProviderException("Malformed Grok RPC response: missing result");

                    return result.Clone();
                }
            }

            Kill();
            throw new ProviderException($"Timeout: Grok RPC timed out on {method}");
        }

        public async Task<string> RequestResultAsync(string method, object parameters, TimeSpan timeout, CancellationToken ct)
        {
            var result = await RequestAsync(method, parameters, timeout, ct).ConfigureAwait(false);
            return result.GetRawText();
        }

        public void Kill()
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch { }
        }

        public void Dispose()
        {
            Kill();
            _process?.Dispose();
        }

        private static int? JsonId(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id))
                return id;
            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringId))
                return stringId;
            return null;
        }

        private static string GrokErrorMessage(JsonElement error)
        {
            var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var messageProperty)
                ? messageProperty.GetString()
                : null;

            if (message is not null
                && (message.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("grok login", StringComparison.OrdinalIgnoreCase)))
            {
                return "Not available: Grok billing requires authentication. Run grok login.";
            }

            return $"Not available: Grok request failed: {message ?? error.GetRawText()}";
        }

        private static async Task<string> ReadStderrIfReadyAsync(Process process)
        {
            try
            {
                return process.HasExited
                    ? await process.StandardError.ReadToEndAsync().ConfigureAwait(false)
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
