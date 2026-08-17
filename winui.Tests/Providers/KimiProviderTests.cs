using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;
using QuotaLens.Tests.Core;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class KimiProviderTests
{
    private const string SampleUsageJson = """
        {
          "user": {
            "userId": "example-user-id-000000",
            "region": "REGION_CN",
            "membership": { "level": "LEVEL_INTERMEDIATE" },
            "businessId": ""
          },
          "usage": { "limit": "100", "used": "16", "remaining": "84", "resetTime": "2026-07-22T16:39:20.750079Z" },
          "limits": [
            {
              "window": { "duration": 300, "timeUnit": "TIME_UNIT_MINUTE" },
              "detail": { "limit": "100", "used": "66", "remaining": "34", "resetTime": "2026-07-16T17:39:20.750079Z" }
            }
          ],
          "parallel": { "limit": "20", "details": [] },
          "totalQuota": { "limit": "100", "remaining": "99" },
          "authentication": { "method": "METHOD_ACCESS_TOKEN", "scope": "FEATURE_CODING" },
          "subType": "TYPE_PURCHASE"
        }
        """;

    [TestMethod]
    public void ParseCliUsage_MapsWeeklyAndRateWindows()
    {
        var provider = Provider(FreshCredentials());

        var snapshot = provider.ParseCliUsage(SampleUsageJson);

        Assert.AreEqual("kimi", snapshot.ProviderId);
        Assert.AreEqual("Kimi · Moderato", snapshot.Name);
        Assert.AreEqual("Weekly", snapshot.Primary.Label);
        Assert.AreEqual(16.0, snapshot.Primary.UsedPercent);
        Assert.AreEqual(10080, snapshot.Primary.WindowMinutes);
        Assert.AreEqual("2026-07-22T16:39:20.750079Z", snapshot.Primary.ResetsAt);
        Assert.AreEqual("16% used", snapshot.Primary.DetailText);

        Assert.IsNotNull(snapshot.Secondary);
        Assert.AreEqual("5h Rate Limit", snapshot.Secondary!.Label);
        Assert.AreEqual(66.0, snapshot.Secondary.UsedPercent);
        Assert.AreEqual(300, snapshot.Secondary.WindowMinutes);
        Assert.AreEqual("Rate: 66% used", snapshot.Secondary.DetailText);

        Assert.IsNotNull(snapshot.Tertiary);
        Assert.AreEqual("Total quota", snapshot.Tertiary!.Label);
        Assert.AreEqual(1.0, snapshot.Tertiary.UsedPercent);
        Assert.AreEqual("1% used", snapshot.Tertiary.DetailText);

        Assert.AreEqual(1, snapshot.AdditionalWindows.Count);
        Assert.AreEqual("Concurrency", snapshot.AdditionalWindows[0].Label);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.AdditionalWindows[0].Kind);
        Assert.AreEqual("20 concurrent", snapshot.AdditionalWindows[0].ValueText);
    }

    [TestMethod]
    public void ParseCliUsage_UsesRequestCountsWhenLimitIsNotNormalized()
    {
        var provider = Provider(FreshCredentials());

        var snapshot = provider.ParseCliUsage("""
            { "usage": { "limit": "2048", "used": "512", "remaining": "1536" } }
            """);

        Assert.AreEqual(25.0, snapshot.Primary.UsedPercent);
        Assert.AreEqual("512/2048 requests", snapshot.Primary.DetailText);
        Assert.AreEqual("Kimi", snapshot.Name);
        Assert.IsNull(snapshot.Secondary);
    }

    [TestMethod]
    public void ParseWebTotalQuota_ExtractsLimitUsedRemaining()
    {
        var detail = KimiProvider.ParseWebTotalQuota(
            """{ "usages": [], "totalQuota": { "limit": "100", "used": "59", "remaining": "41" } }""");

        Assert.IsNotNull(detail);
        Assert.AreEqual("100", detail!.Limit);
        Assert.AreEqual("59", detail.Used);
        Assert.AreEqual("41", detail.Remaining);
    }

    [TestMethod]
    public void ParseWebTotalQuota_ReturnsNullWhenAbsentOrEmpty()
    {
        Assert.IsNull(KimiProvider.ParseWebTotalQuota("""{ "usages": [] }"""));
        Assert.IsNull(KimiProvider.ParseWebTotalQuota("""{ "totalQuota": {} }"""));
    }

    [TestMethod]
    public void TierName_MapsKnownLevelsAndPrettifiesUnknown()
    {
        Assert.AreEqual("Andante", KimiProvider.TierName("LEVEL_BASIC"));
        Assert.AreEqual("Moderato", KimiProvider.TierName("LEVEL_INTERMEDIATE"));
        Assert.AreEqual("Allegretto", KimiProvider.TierName("LEVEL_ADVANCED"));
        Assert.AreEqual("Super Fast", KimiProvider.TierName("LEVEL_SUPER_FAST"));
        Assert.IsNull(KimiProvider.TierName(null));
        Assert.IsNull(KimiProvider.TierName(""));
    }

    [TestMethod]
    public async Task FetchAsync_WithoutAnySource_ThrowsLoginRequired()
    {
        var provider = new KimiProvider(
            () => null,
            (_, _) => throw new AssertFailedException("no usage call expected"),
            webIsAvailable: (_, _) => false);

        var ex = await Assert.ThrowsExactlyAsync<ProviderException>(
            () => provider.FetchAsync("kimi", new EmptyConfig(), CancellationToken.None));

        StringAssert.Contains(ex.Message, "no data source is ready");
    }

    [TestMethod]
    public async Task FetchAsync_WithFreshToken_FetchesUsageDirectly()
    {
        var usageCalls = new List<string>();
        var provider = new KimiProvider(
            FreshCredentials,
            (token, _) => { usageCalls.Add(token); return Task.FromResult(UsageResponse()); });

        var snapshot = await provider.FetchAsync("kimi", new EmptyConfig(), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "fresh-access" }, usageCalls);
        Assert.AreEqual("Kimi · Moderato", snapshot.Name);
        Assert.AreEqual("Kimi Code CLI", snapshot.SourceLabel);
    }

    [TestMethod]
    public async Task FetchAsync_WithExpiredToken_NeverCallsTheApiAndThrowsLoginRequired()
    {
        // Read-only policy: an expired token is reported, never refreshed.
        var provider = new KimiProvider(
            ExpiredCredentials,
            (_, _) => throw new AssertFailedException("an expired token must not be sent to the usage API"));

        var ex = await Assert.ThrowsExactlyAsync<ProviderException>(
            () => provider.FetchAsync("kimi", new EmptyConfig(), CancellationToken.None));

        StringAssert.Contains(ex.Message, "Login required");
    }

    [TestMethod]
    public async Task FetchAsync_WithRejectedToken_ThrowsLoginRequiredInsteadOfRefreshing()
    {
        var provider = new KimiProvider(
            FreshCredentials,
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var ex = await Assert.ThrowsExactlyAsync<ProviderException>(
            () => provider.FetchAsync("kimi", new EmptyConfig(), CancellationToken.None));

        StringAssert.Contains(ex.Message, "Login required");
    }

    [TestMethod]
    public void ParseAppUsage_OrdersTotalQuotaThenWeeklyThenRate()
    {
        var snapshot = KimiProvider.ParseAppUsage(
            """
            {
              "usages": [{ "scope": "FEATURE_CODING",
                "detail": { "limit": "100", "used": "69", "remaining": "31", "resetTime": "2026-08-16T05:56:16Z" },
                "limits": [{ "window": { "duration": 300, "timeUnit": "TIME_UNIT_MINUTE" },
                  "detail": { "limit": "100", "used": "21", "remaining": "79", "resetTime": "2026-08-14T07:56:16Z" } }]
              }],
              "totalQuota": { "limit": "100", "used": "59", "remaining": "41" }
            }
            """);

        Assert.AreEqual("Total quota", snapshot.Primary.Label);
        Assert.AreEqual(59.0, snapshot.Primary.UsedPercent);
        Assert.AreEqual("Weekly", snapshot.Secondary!.Label);
        Assert.AreEqual(69.0, snapshot.Secondary.UsedPercent);
        Assert.AreEqual("5h Rate Limit", snapshot.Tertiary!.Label);
        Assert.AreEqual(21.0, snapshot.Tertiary.UsedPercent);
        Assert.AreEqual("Kimi app", snapshot.SourceLabel);
    }

    [TestMethod]
    public async Task FetchAsync_PrefersAppSourceWhenAvailable()
    {
        var appCalls = 0;
        var provider = new KimiProvider(
            () => null,
            (_, _) => throw new AssertFailedException("CLI must not be used when App is available"),
            appIsAvailable: () => true,
            fetchAppAsync: _ =>
            {
                appCalls++;
                return Task.FromResult(new ProviderSnapshot
                {
                    ProviderId = "kimi",
                    Name = "Kimi",
                    Primary = new RateWindow { Label = "Total quota", UsedPercent = 59 },
                });
            });

        var snapshot = await provider.FetchAsync("kimi", new EmptyConfig(), CancellationToken.None);

        Assert.AreEqual(1, appCalls);
        Assert.AreEqual("Total quota", snapshot.Primary.Label);
    }

    [TestMethod]
    public async Task FetchAsync_UsesWebSourceWhenAppAndCliAreUnsigned()
    {
        var webCalls = 0;
        var provider = new KimiProvider(
            () => null,
            (_, _) => throw new AssertFailedException("CLI must not be used when only Web is available"),
            appIsAvailable: () => false,
            fetchAppAsync: _ => throw new AssertFailedException("App must not be used when unsigned"),
            webIsAvailable: (_, _) => true,
            fetchWebAsync: (_, _, _) =>
            {
                webCalls++;
                return Task.FromResult(new ProviderSnapshot
                {
                    ProviderId = "kimi",
                    Name = "Kimi",
                    SourceLabel = "Kimi WebView",
                    Primary = new RateWindow { Label = "Weekly", UsedPercent = 40 },
                });
            });

        var snapshot = await provider.FetchAsync("kimi", new EmptyConfig(), CancellationToken.None);

        Assert.AreEqual(1, webCalls);
        Assert.AreEqual("Kimi WebView", snapshot.SourceLabel);
        CollectionAssert.AreEqual(new[] { "app", "cli", "web" }, provider.Sources.Select(source => source.Id).ToArray());
    }

    [TestMethod]
    public void ApplyKimiAppHeaders_CopiesJwtSessionClaims()
    {
        var token = FakeJwt(new Dictionary<string, object>
        {
            ["device_id"] = "dev-1",
            ["ssid"] = "sess-1",
            ["sub"] = "user-1",
            ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.kimi.com/");
        KimiProvider.ApplyKimiAppHeaders(request, token);

        Assert.AreEqual("Bearer " + token, request.Headers.GetValues("Authorization").Single());
        Assert.AreEqual("kimi-auth=" + token, request.Headers.GetValues("Cookie").Single());
        Assert.AreEqual("dev-1", request.Headers.GetValues("x-msh-device-id").Single());
        Assert.AreEqual("sess-1", request.Headers.GetValues("x-msh-session-id").Single());
        Assert.AreEqual("user-1", request.Headers.GetValues("x-traffic-id").Single());
    }

    [TestMethod]
    public void IsJwtExpired_UsesExpClaim()
    {
        var expired = FakeJwt(new Dictionary<string, object>
        {
            ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120,
        });
        var fresh = FakeJwt(new Dictionary<string, object>
        {
            ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600,
        });

        Assert.IsTrue(KimiProvider.IsJwtExpired(expired, DateTimeOffset.UtcNow));
        Assert.IsFalse(KimiProvider.IsJwtExpired(fresh, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void DesktopSessionIsUsable_RequiresUnexpiredToken()
    {
        // An expired desktop token must make the App source unavailable so the
        // runner falls back to CLI/Web instead of failing the card.
        var now = DateTimeOffset.UtcNow;

        Assert.IsFalse(KimiProvider.DesktopSessionIsUsable(null, now));
        Assert.IsFalse(KimiProvider.DesktopSessionIsUsable(
            FakeJwt(new Dictionary<string, object> { ["exp"] = now.ToUnixTimeSeconds() - 120 }), now));
        Assert.IsTrue(KimiProvider.DesktopSessionIsUsable(
            FakeJwt(new Dictionary<string, object> { ["exp"] = now.ToUnixTimeSeconds() + 600 }), now));
    }

    [TestMethod]
    public void AppSource_CarriesAttentionNoteForSelector()
    {
        var provider = Provider(FreshCredentials());

        Assert.AreEqual("kimi.appSourceNote", provider.Sources.Single(s => s.Id == "app").AttentionNote);
        Assert.IsNull(provider.Sources.Single(s => s.Id == "cli").AttentionNote);
    }

    [TestMethod]
    public void AppSource_CarriesDeclarativeRecoveryForCardAction()
    {
        var provider = Provider(FreshCredentials());

        Assert.AreEqual("kimi.appSourceNote", provider.Sources.Single(s => s.Id == "app").UnavailableRecovery?.DescriptionKey);
        Assert.IsNull(provider.Sources.Single(s => s.Id == "cli").UnavailableRecovery);
        Assert.IsNull(provider.Sources.Single(s => s.Id == "web").UnavailableRecovery);
    }

    [TestMethod]
    public void ReadKimiDesktopAccessToken_DecryptsSafeStorageV1()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quotelens-kimi-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var masterKey = RandomNumberGenerator.GetBytes(32);
            var protectedKey = RandomNumberGenerator.GetBytes(24);
            var localState = ElectronSafeStorageTests.WriteLocalState(directory, protectedKey);
            var encrypted = ElectronSafeStorageTests.EncryptV10(
                "{\"tokens\":{\"access_token\":\"app-token-fixture\"}}",
                masterKey);
            var tokenStore = Path.Combine(directory, "token-store.json");
            File.WriteAllText(tokenStore, System.Text.Json.JsonSerializer.Serialize(new
            {
                encryption = "safeStorage.v1",
                data = encrypted,
            }));

            var token = KimiProvider.ReadKimiDesktopAccessToken(
                tokenStore,
                localState,
                wrapped =>
                {
                    CollectionAssert.AreEqual(protectedKey, wrapped);
                    return masterKey.ToArray();
                });

            Assert.AreEqual("app-token-fixture", token);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ReadKimiDesktopAccessToken_KeepsLegacyPlaintextCompatibility()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quotelens-kimi-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tokenStore = Path.Combine(directory, "token-store.json");
            File.WriteAllText(tokenStore, "{\"tokens\":{\"access_token\":\"legacy-token-fixture\"}}");

            Assert.AreEqual(
                "legacy-token-fixture",
                KimiProvider.ReadKimiDesktopAccessToken(tokenStore, Path.Combine(directory, "missing-state")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string FakeJwt(IReadOnlyDictionary<string, object> claims)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(claims);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"aaa.{payload}.sig";
    }

    // ---- helpers -----------------------------------------------------------

    private static KimiProvider Provider(JsonObject creds) => new(
        () => creds,
        (_, _) => Task.FromResult(UsageResponse()));

    private static JsonObject FreshCredentials() => new()
    {
        ["access_token"] = "fresh-access",
        ["refresh_token"] = "old-refresh",
        ["expires_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600,
        ["scope"] = "keep-me",
        ["token_type"] = "Bearer",
    };

    private static JsonObject ExpiredCredentials() => new()
    {
        ["access_token"] = "expired-access",
        ["refresh_token"] = "old-refresh",
        ["expires_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600,
        ["scope"] = "keep-me",
        ["token_type"] = "Bearer",
    };

    private static HttpResponseMessage UsageResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(SampleUsageJson, Encoding.UTF8, "application/json"),
    };

    private sealed class EmptyConfig : IConfig
    {
        public string Get(string key, string fallback = "") => fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") => fallback;

        public bool HasScoped(string instanceId, string key) => false;

        public bool GetBool(string key, bool fallback = false) => fallback;
    }
}
