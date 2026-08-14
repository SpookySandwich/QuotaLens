using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;

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
        Assert.AreEqual("16% used", snapshot.Primary.ResetDescription);

        Assert.IsNotNull(snapshot.Secondary);
        Assert.AreEqual("5h Rate Limit", snapshot.Secondary!.Label);
        Assert.AreEqual(66.0, snapshot.Secondary.UsedPercent);
        Assert.AreEqual(300, snapshot.Secondary.WindowMinutes);
        Assert.AreEqual("Rate: 66% used", snapshot.Secondary.ResetDescription);

        Assert.IsNotNull(snapshot.Tertiary);
        Assert.AreEqual("Total quota", snapshot.Tertiary!.Label);
        Assert.AreEqual(1.0, snapshot.Tertiary.UsedPercent);
        Assert.AreEqual("1% used", snapshot.Tertiary.ResetDescription);

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
        Assert.AreEqual("512/2048 requests", snapshot.Primary.ResetDescription);
        Assert.AreEqual("Kimi", snapshot.Name);
        Assert.IsNull(snapshot.Secondary);
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
    public async Task FetchAsync_WithoutCredentialsFile_DelegatesToWebFallback()
    {
        var fallback = new FakeProvider(_ => Task.FromResult(new ProviderSnapshot { ProviderId = "kimi", Name = "Kimi Web" }));
        var provider = new KimiProvider(
            () => null,
            (_, _) => throw new AssertFailedException("no usage call expected"),
            fallback);

        var snapshot = await provider.FetchAsync("kimi", new EmptyConfig(), CancellationToken.None);

        Assert.AreEqual("Kimi Web", snapshot.Name);
        Assert.AreEqual(1, fallback.Calls);
    }

    [TestMethod]
    public async Task FetchAsync_WithFreshToken_FetchesUsageDirectly()
    {
        var usageCalls = new List<string>();
        var provider = new KimiProvider(
            FreshCredentials,
            (token, _) => { usageCalls.Add(token); return Task.FromResult(UsageResponse()); },
            FailingFallback());

        var snapshot = await provider.FetchAsync("kimi", new EmptyConfig(), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "fresh-access" }, usageCalls);
        Assert.AreEqual("Kimi · Moderato", snapshot.Name);
        Assert.AreEqual("Kimi Code CLI", snapshot.SourceLabel);
    }

    [TestMethod]
    public async Task FetchAsync_WithExpiredToken_NeverCallsTheApiAndFallsBack()
    {
        // Read-only policy: an expired token is reported, never refreshed. Kimi's
        // refresh rotates the refresh token, so refreshing would force a write back
        // into the CLI's credential store and could strand the CLI's own session.
        var fallback = new FakeProvider(_ => Task.FromResult(new ProviderSnapshot { ProviderId = "kimi", Name = "Kimi Web Cache" }));
        var provider = new KimiProvider(
            ExpiredCredentials,
            (_, _) => throw new AssertFailedException("an expired token must not be sent to the usage API"),
            fallback);

        var snapshot = await provider.FetchAsync("kimi", new EmptyConfig(), CancellationToken.None);

        Assert.AreEqual("Kimi Web Cache", snapshot.Name);
        Assert.AreEqual(1, fallback.Calls);
    }

    [TestMethod]
    public async Task FetchAsync_WithRejectedToken_FallsBackInsteadOfRefreshing()
    {
        var fallback = new FakeProvider(_ => Task.FromResult(new ProviderSnapshot { ProviderId = "kimi", Name = "Kimi Web Cache" }));
        var provider = new KimiProvider(
            FreshCredentials,
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)),
            fallback);

        var snapshot = await provider.FetchAsync("kimi", new EmptyConfig(), CancellationToken.None);

        Assert.AreEqual("Kimi Web Cache", snapshot.Name);
    }

    [TestMethod]
    public async Task FetchAsync_WithDeadCliSessionAndNoWebSession_ThrowsLoginRequired()
    {
        var provider = new KimiProvider(
            ExpiredCredentials,
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)),
            FailingFallback());

        var ex = await Assert.ThrowsExactlyAsync<ProviderException>(
            () => provider.FetchAsync("kimi", new EmptyConfig(), CancellationToken.None));

        StringAssert.Contains(ex.Message, "Login required");
    }

    // ---- helpers -----------------------------------------------------------

    private static KimiProvider Provider(JsonObject creds) => new(
        () => creds,
        (_, _) => Task.FromResult(UsageResponse()),
        FailingFallback());

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

    private static FakeProvider FailingFallback() =>
        new(_ => throw new ProviderException("Login required - click to open Kimi in browser"));

    private sealed class FakeProvider : IProvider
    {
        private readonly Func<string, Task<ProviderSnapshot>> _fetch;

        public FakeProvider(Func<string, Task<ProviderSnapshot>> fetch) => _fetch = fetch;

        public int Calls { get; private set; }
        public string Type => "kimi";
        public string Name => "Kimi";
        public string SourceLabel => "Kimi WebView";
        public Confidence Confidence => Confidence.Official;

        public Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
        {
            Calls++;
            return _fetch(instanceId);
        }
    }

    private sealed class EmptyConfig : IConfig
    {
        public string Get(string key, string fallback = "") => fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") => fallback;

        public bool HasScoped(string instanceId, string key) => false;

        public bool GetBool(string key, bool fallback = false) => fallback;
    }
}
