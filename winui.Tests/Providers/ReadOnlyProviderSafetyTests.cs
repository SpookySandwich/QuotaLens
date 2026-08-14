using QuotaLens.Core;
using QuotaLens.Providers;
using QuotaLens.Services;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class ReadOnlyProviderSafetyTests
{
    [TestMethod]
    public async Task RetiredKimiK2_HasNoRelayImplementationAndNeverReadsCredentials()
    {
        var provider = ProviderRegistry.Create("kimik2");

        var exception = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            provider.FetchAsync("legacy-kimik2", new ThrowingConfig(), CancellationToken.None));

        StringAssert.Contains(exception.Message, "Provider retired");
        StringAssert.Contains(exception.Message, "rotate any credential");
        Assert.IsFalse(Catalog.IsAddableProviderType("kimik2"));

        var source = File.ReadAllText(FindRepositoryFile("winui", "Providers", "SimpleApiProvider.cs"))
            + File.ReadAllText(FindRepositoryFile("winui", "Providers", "RetiredProvider.cs"));
        var retiredRelayHost = string.Concat("kimi-k2", ".ai");
        Assert.IsFalse(source.Contains(retiredRelayHost, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ParseArkcliUsage_WithSubscribedPlans_MapsEveryPeriodToAWindow()
    {
        // Arrange
        const string json = """
        {
          "viewer": { "auth_method": "oauth" },
          "items": [
            {
              "product": "coding-plan",
              "subscribed": true,
              "updated_at": 1893456000000,
              "periods": [
                { "label": "5h", "percent": 25, "reset_at": "2030-01-01T05:00:00Z" },
                { "label": "weekly", "percent": 50, "reset_at": 1894060800000 }
              ]
            },
            {
              "product": "agent-plan",
              "subscribed": true,
              "updated_at": 1893542400,
              "periods": [
                { "label": "five_hour", "percent": 75, "reset_at": 1893560400 },
                { "label": "monthly", "percent": 10, "reset_at": "2030-02-01T00:00:00Z" }
              ]
            },
            {
              "product": "coding-plan-team",
              "subscribed": false,
              "error": "not subscribed"
            }
          ]
        }
        """;

        // Act
        var snapshot = DoubaoProvider.ParseArkcliUsage(json, DateTimeOffset.Parse("2030-01-03T00:00:00Z"));

        // Assert
        Assert.AreEqual("Coding Plan · 5-hour", snapshot.Primary.Label);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(300L, snapshot.Primary.WindowMinutes);
        Assert.AreEqual("Coding Plan · Weekly", snapshot.Secondary!.Label);
        Assert.AreEqual("Agent Plan · 5-hour", snapshot.Tertiary!.Label);
        Assert.HasCount(1, snapshot.AdditionalWindows);
        Assert.AreEqual("Agent Plan · Monthly", snapshot.AdditionalWindows[0].Label);
        Assert.AreEqual("2030-01-02T00:00:00.0000000+00:00", snapshot.UpdatedAt.ToString("O"));
        Assert.AreEqual("arkcli usage plan", snapshot.SourceLabel);
        Assert.AreEqual(Confidence.SemiOfficial, snapshot.Confidence);
    }

    [TestMethod]
    public void ParseArkcliUsage_WithAlternativeProducts_UsesBestProductBottleneckForAvailability()
    {
        const string json =
            """
            {
              "items": [
                {
                  "product": "coding-plan",
                  "subscribed": true,
                  "periods": [
                    { "label": "5h", "percent": 100 },
                    { "label": "weekly", "percent": 100 }
                  ]
                },
                {
                  "product": "agent-plan",
                  "subscribed": true,
                  "periods": [
                    { "label": "5h", "percent": 20 },
                    { "label": "weekly", "percent": 40 }
                  ]
                }
              ]
            }
            """;

        var snapshot = DoubaoProvider.ParseArkcliUsage(json, DateTimeOffset.UtcNow);

        Assert.AreEqual("Coding Plan", snapshot.Primary.AvailabilityGroup);
        Assert.AreEqual("Agent Plan", snapshot.Tertiary!.AvailabilityGroup);
        Assert.AreEqual(60, Quota.ProviderAvailability("doubao", snapshot), 0.001);
    }

    [TestMethod]
    public void ParseArkcliUsage_WithIncompleteSubscribedPlan_ThrowsItemError()
    {
        // Arrange
        const string json = """
        {
          "viewer": { "auth_method": "oauth" },
          "items": [
            {
              "product": "coding-plan",
              "subscribed": true,
              "error": "permission denied",
              "periods": []
            }
          ]
        }
        """;

        // Act
        var exception = Assert.ThrowsExactly<ProviderException>(() =>
            DoubaoProvider.ParseArkcliUsage(json, DateTimeOffset.UtcNow));

        // Assert
        StringAssert.Contains(exception.Message, "incomplete Coding Plan usage");
        StringAssert.Contains(exception.Message, "permission denied");
    }

    [TestMethod]
    public async Task FetchAsync_ForDoubao_InvokesOnlyArkcliUsagePlanCommand()
    {
        // Arrange
        string? capturedBinary = null;
        string[]? capturedArguments = null;
        var callCount = 0;
        var provider = new DoubaoProvider((binary, arguments, _) =>
        {
            callCount++;
            capturedBinary = binary;
            capturedArguments = arguments.ToArray();
            return Task.FromResult("""
            {
              "viewer": { "auth_method": "oauth" },
              "items": [
                {
                  "product": "coding-plan",
                  "subscribed": true,
                  "periods": [
                    { "label": "5h", "percent": 20 }
                  ]
                }
              ]
            }
            """);
        });
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["doubao-main.doubao_cli_path"] = @"C:\Tools\arkcli.exe",
        });

        // Act
        var snapshot = await provider.FetchAsync("doubao-main", config, CancellationToken.None);

        // Assert
        Assert.AreEqual(1, callCount);
        Assert.AreEqual(@"C:\Tools\arkcli.exe", capturedBinary);
        CollectionAssert.AreEqual(new[] { "usage", "plan", "--format", "json" }, capturedArguments!);
        Assert.AreEqual("Coding Plan · 5-hour", snapshot.Primary.Label);
    }

    [TestMethod]
    public void CreateArkcliStartInfo_UsesHiddenRedirectedProcessWithoutShell()
    {
        // Arrange
        var arguments = new[] { "usage", "plan", "--format", "json" };

        // Act
        var startInfo = DoubaoProvider.CreateArkcliStartInfo("arkcli", arguments);

        // Assert
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.CreateNoWindow);
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardError);
        CollectionAssert.AreEqual(arguments, startInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    public async Task FetchAsync_ForAzureWithoutArmConfiguration_ExplainsRequiredReadOnlyAccess()
    {
        // Arrange
        var provider = new AzureOpenAIProvider();
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["azure-main.azureopenai_subscription_id"] = " ",
            ["azure-main.azureopenai_location"] = " ",
        });

        // Act
        var exception = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            provider.FetchAsync("azure-main", config, CancellationToken.None));

        // Assert
        StringAssert.Contains(exception.Message, "subscription ID, location");
        StringAssert.Contains(exception.Message, "Azure Resource Manager authentication");
        StringAssert.Contains(exception.Message, "never used for refresh");
    }

    [TestMethod]
    public void AzureRefreshSource_UsesArmQuotaGetAndContainsNoInferenceRequestPath()
    {
        // Arrange
        var azureSource = File.ReadAllText(FindRepositoryFile("winui", "Providers", "AzureOpenAIProvider.cs"));

        // Assert
        StringAssert.Contains(azureSource, "/usages?api-version=");
        StringAssert.Contains(azureSource, "HttpMethod.Get");
        foreach (var forbidden in new[]
        {
            "chat/completions",
            "max_tokens",
            "max_completion_tokens",
            "HttpMethod.Post",
        })
        {
            Assert.IsFalse(
                azureSource.Contains(forbidden, StringComparison.Ordinal),
                $"Azure quota refresh must not contain inference marker '{forbidden}'.");
        }
    }

    [TestMethod]
    public void BuildAzureUsagesUri_UsesCredentialSafeManagementEndpoint()
    {
        var uri = AzureOpenAIProvider.BuildUsagesUri("subscription/id", "East US 2");

        Assert.AreEqual("https", uri.Scheme);
        Assert.AreEqual("management.azure.com", uri.Host);
        Assert.AreEqual(
            "/subscriptions/subscription%2Fid/providers/Microsoft.CognitiveServices/locations/East%20US%202/usages",
            uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).Insert(0, "/"));
        Assert.AreEqual("api-version=2024-10-01", uri.Query.TrimStart('?'));
    }

    [TestMethod]
    public void ParseAzureUsages_PreservesCapacityAllocationsWithoutTreatingThemAsExhaustion()
    {
        const string json = """
        {
          "value": [
            {
              "name": { "value": "Tokens", "localizedValue": "Tokens per minute" },
              "currentValue": 50,
              "limit": 100,
              "unit": "Count"
            },
            {
              "name": { "value": "Requests" },
              "currentValue": 15,
              "limit": 20,
              "unit": "CountPerMinute"
            },
            {
              "name": { "localizedValue": "Deployments" },
              "currentValue": 1,
              "limit": 4,
              "unit": "Count"
            },
            {
              "currentValue": 1,
              "limit": 10,
              "unit": "Count"
            },
            {
              "name": { "value": "No finite quota" },
              "currentValue": 10,
              "limit": 0,
              "unit": "Count"
            }
          ]
        }
        """;
        var updatedAt = DateTimeOffset.Parse("2026-08-03T05:00:00Z");

        var snapshot = AzureOpenAIProvider.ParseUsages(json, "eastus2", updatedAt);

        Assert.AreEqual("Requests", snapshot.Primary.Label);
        Assert.AreEqual(75, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual("15 of 20 CountPerMinute allocated", snapshot.Primary.ValueText);
        Assert.AreEqual("Regional capacity allocation; not live request consumption", snapshot.Primary.ResetDescription);
        Assert.AreEqual("Tokens per minute", snapshot.Secondary!.Label);
        Assert.AreEqual("Deployments", snapshot.Tertiary!.Label);
        Assert.HasCount(1, snapshot.AdditionalWindows);
        Assert.AreEqual("Regional quota", snapshot.AdditionalWindows[0].Label);
        Assert.IsFalse(snapshot.Primary.CountsForAvailability);
        Assert.AreEqual("eastus2", snapshot.Accounts.Single().Plan);
        Assert.AreEqual(ProviderSourceKind.OfficialApi, snapshot.SourceKind);
        Assert.AreEqual(ProviderAvailabilityKind.Unknown, snapshot.AvailabilityKind);
        Assert.AreEqual(
            ProviderAvailabilityKind.Unknown,
            Quota.ProviderAvailabilityState("azureopenai", snapshot).Kind);
        Assert.AreEqual(updatedAt, snapshot.UpdatedAt);
    }

    [TestMethod]
    public void CreateAzureCliStartInfo_UsesHiddenReadOnlyTokenCommand()
    {
        var startInfo = AzureOpenAIProvider.CreateAzureCliStartInfo(@"C:\Tools\az.cmd");

        Assert.IsTrue(startInfo.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.CreateNoWindow);
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardError);
        CollectionAssert.AreEqual(
            new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand" },
            startInfo.ArgumentList.Take(4).ToArray());
        Assert.AreEqual(@"C:\Tools\az.cmd", startInfo.Environment["QUOTALENS_CLI_BINARY"]);
        Assert.AreEqual("8", startInfo.Environment["QUOTALENS_CLI_ARG_COUNT"]);
        CollectionAssert.AreEqual(
            new[] { "account", "get-access-token", "--resource", "https://management.azure.com/", "--query", "accessToken", "--output", "tsv" },
            Enumerable.Range(0, 8).Select(index => startInfo.Environment[$"QUOTALENS_CLI_ARG_{index}"]).ToArray());
    }

    [TestMethod]
    public void KimiCliRefreshSource_DoesNotMutateCliCredentialsOrRotateTokens()
    {
        var source = File.ReadAllText(FindRepositoryFile("winui", "Providers", "KimiProvider.cs"));

        foreach (var forbidden in new[]
        {
            "File.WriteAllText",
            "File.Move",
            "refresh_token",
            "/oauth/token",
        })
        {
            Assert.IsFalse(
                source.Contains(forbidden, StringComparison.Ordinal),
                $"Kimi refresh must remain read-only and cannot contain '{forbidden}'.");
        }
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(relativeSegments).ToArray());
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(relativeSegments)} from the test output directory.");
    }

    private sealed class FakeConfig(IReadOnlyDictionary<string, string> values) : IConfig
    {
        public string Get(string key, string fallback = "") =>
            values.TryGetValue(key, out var value) ? value : fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") =>
            values.TryGetValue($"{instanceId}.{key}", out var value) ? value : fallback;

        public bool HasScoped(string instanceId, string key) =>
            values.ContainsKey($"{instanceId}.{key}");

        public bool GetBool(string key, bool fallback = false) => fallback;
    }

    private sealed class ThrowingConfig : IConfig
    {
        public string Get(string key, string fallback = "") => throw new InvalidOperationException("Config must not be read.");
        public string GetScoped(string instanceId, string key, string fallback = "") => throw new InvalidOperationException("Config must not be read.");
        public bool HasScoped(string instanceId, string key) => throw new InvalidOperationException("Config must not be read.");
        public bool GetBool(string key, bool fallback = false) => throw new InvalidOperationException("Config must not be read.");
    }
}
