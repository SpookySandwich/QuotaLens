using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>Reads regional Azure OpenAI quota from Azure Resource Manager without inference.</summary>
public sealed class AzureOpenAIProvider : IProvider
{
    private const string ManagementEndpoint = "https://management.azure.com";
    private const string ApiVersion = "2024-10-01";

    internal const string QuotaMonitoringConfigurationError =
        "Not configured: Azure OpenAI quota monitoring requires a subscription ID, location, and read-only "
        + "Azure Resource Manager authentication. Sign in with Azure CLI or provide an ARM access token. "
        + "Resource API keys, resource endpoints, and deployments cannot report quota and are never used for refresh.";

    public string Type => "azureopenai";
    public string Name => "Azure OpenAI";
    public string SourceLabel => "Azure Resource Manager";
    public Confidence Confidence => Confidence.Official;

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var subscriptionId = ProviderConfig.ScopedOrEnvironment(
            instanceId,
            config,
            "azureopenai_subscription_id",
            "AZURE_SUBSCRIPTION_ID");
        var location = ProviderConfig.ScopedOrEnvironment(
            instanceId,
            config,
            "azureopenai_location",
            "AZURE_LOCATION",
            "AZURE_OPENAI_LOCATION");
        if (string.IsNullOrWhiteSpace(subscriptionId) || string.IsNullOrWhiteSpace(location))
            throw new ProviderException(QuotaMonitoringConfigurationError);

        var accessToken = ProviderConfig.ScopedOrEnvironment(
            instanceId,
            config,
            "azureopenai_arm_token",
            "AZURE_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
            accessToken = await AzureCliAccessTokenAsync(instanceId, config, ct).ConfigureAwait(false);

        var uri = BuildUsagesUri(subscriptionId!, location!);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        try
        {
            using var response = await Http.Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 401 or 403)
            {
                throw new ProviderException(
                    "Login required: Azure Resource Manager rejected the credential. Run 'az login' and verify Cognitive Services Reader access.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderException(
                    $"Network error: Azure Resource Manager HTTP {(int)response.StatusCode}: {ProviderConfig.ResponseSummary(body)}");
            }

            return ParseUsages(body, location!, DateTimeOffset.UtcNow);
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException($"Network error: Azure Resource Manager request failed: {e.Message}", e);
        }
    }

    internal static Uri BuildUsagesUri(string subscriptionId, string location)
    {
        var endpoint =
            $"{ManagementEndpoint}/subscriptions/{Uri.EscapeDataString(subscriptionId.Trim())}" +
            $"/providers/Microsoft.CognitiveServices/locations/{Uri.EscapeDataString(location.Trim())}" +
            $"/usages?api-version={ApiVersion}";
        return ProviderEndpointPolicy.RequireCredentialTarget("azureopenai", endpoint);
    }

    internal static ProviderSnapshot ParseUsages(string json, string location, DateTimeOffset updatedAt)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("value", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            throw new ProviderException("Parse error: Azure Resource Manager response did not contain quota usages.");
        }

        var windows = new List<RateWindow>();
        foreach (var item in values.EnumerateArray())
        {
            var limit = Number(item, "limit");
            var current = Number(item, "currentValue");
            if (limit is not > 0 || current is null)
                continue;

            var name = item.TryGetProperty("name", out var nameObject)
                ? String(nameObject, "localizedValue") ?? String(nameObject, "value")
                : null;
            var unit = String(item, "unit");
            windows.Add(new RateWindow
            {
                Label = string.IsNullOrWhiteSpace(name) ? "Regional quota" : name!,
                Kind = RateWindowKind.Informational,
                UsedPercent = Quota.ClampPercent(current.Value / limit.Value * 100),
                ValueText = $"{Format(current.Value)} of {Format(limit.Value)}{UnitSuffix(unit)} allocated",
                ResetDescription = "Regional capacity allocation; not live request consumption",
                CountsForAvailability = false,
            });
        }

        if (windows.Count == 0)
            throw new ProviderException("Not available: Azure Resource Manager returned no finite Azure OpenAI capacity allocations for this location.");

        var ordered = windows
            .OrderByDescending(window => window.UsedPercent)
            .ThenBy(window => window.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ProviderSnapshot
        {
            ProviderId = "azureopenai",
            Name = "Azure OpenAI",
            Primary = ordered[0],
            Secondary = ordered.ElementAtOrDefault(1),
            Tertiary = ordered.ElementAtOrDefault(2),
            AdditionalWindows = ordered.Skip(3).ToList(),
            Accounts = new List<AccountInfo> { new() { Plan = location.Trim() } },
            SourceLabel = "Azure Resource Manager",
            Confidence = Confidence.Official,
            SourceKind = ProviderSourceKind.OfficialApi,
            ContractStability = ProviderContractStability.Official,
            AvailabilityKind = ProviderAvailabilityKind.Unknown,
            UpdatedAt = updatedAt,
        };
    }

    internal static ProcessStartInfo CreateAzureCliStartInfo(string binary)
    {
        return HiddenCliProcess.CreateStartInfo(
            binary,
            new[]
            {
                "account", "get-access-token", "--resource", ManagementEndpoint + "/", "--query", "accessToken", "--output", "tsv",
            });
    }

    private static async Task<string> AzureCliAccessTokenAsync(
        string instanceId,
        IConfig config,
        CancellationToken ct)
    {
        var binary = ProviderConfig.ScopedOrEnvironment(
            instanceId,
            config,
            "azureopenai_az_path",
            "AZURE_CLI_PATH") ?? "az";
        using var process = new Process { StartInfo = CreateAzureCliStartInfo(binary) };
        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            throw new ProviderException($"Not configured: Azure CLI could not be launched: {e.Message}", e);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var stdout = await stdoutTask.WaitAsync(timeout.Token).ConfigureAwait(false);
            var stderr = await stderrTask.WaitAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var token = ProviderConfig.Clean(stdout);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(token))
            {
                throw new ProviderException(
                    $"Login required: Azure CLI could not provide a Resource Manager token: {ProviderConfig.ResponseSummary(stderr)}");
            }
            return token!;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort only.
            }
            throw new ProviderException("Timeout: Azure CLI token request did not complete.");
        }
    }

    private static double? Number(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.TryGetDouble(out var number)
            ? number
            : null;

    private static string? String(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? ProviderConfig.Clean(value.GetString())
            : null;

    private static string UnitSuffix(string? unit) =>
        string.IsNullOrWhiteSpace(unit) || unit.Equals("Count", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" {unit}";

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
