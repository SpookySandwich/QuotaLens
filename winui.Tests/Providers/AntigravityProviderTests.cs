using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class AntigravityProviderTests
{
    [TestMethod]
    public void LanguageServerDiscovery_CoversAppAndIdeBinaryNames()
    {
        CollectionAssert.AreEquivalent(
            new[] { "language_server", "language_server_windows_x64" },
            AntigravityProvider.LanguageServerProcessNames);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-15T08:00:00Z");

    [TestMethod]
    public void ParseQuotaSummary_WithCurrentGroupedShape_PreservesFourServerBuckets()
    {
        var snapshot = AntigravityProvider.ParseQuotaSummary("antigravity-main", QuotaSummaryJson, Now);
        var windows = new[] { snapshot.Primary, snapshot.Secondary, snapshot.Tertiary }
            .Where(window => window is not null)
            .Cast<QuotaLens.Core.RateWindow>()
            .Concat(snapshot.AdditionalWindows)
            .ToArray();

        Assert.AreEqual("antigravity-main", snapshot.ProviderId);
        CollectionAssert.AreEqual(
            new[] { "Gemini weekly", "Gemini 5-hour", "Claude/GPT weekly", "Claude/GPT 5-hour" },
            windows.Select(window => window.Label).ToArray());
        CollectionAssert.AreEqual(
            new long?[] { 10080, 300, 10080, 300 },
            windows.Select(window => window.WindowMinutes).ToArray());
        CollectionAssert.AreEqual(
            new[] { 18d, 9d, 36d, 27d },
            windows.Select(window => window.UsedPercent).ToArray());
    }

    [TestMethod]
    public void ParseQuotaSummary_WithAlternativeFamilies_UsesBestFamilyBottleneckForAvailability()
    {
        const string json =
            """
            {
              "groups": [
                {
                  "displayName": "Gemini Models",
                  "buckets": [
                    { "bucketId": "gemini-5h", "displayName": "Five Hour Limit", "remaining": { "remainingFraction": 0 } },
                    { "bucketId": "gemini-weekly", "displayName": "Weekly Limit", "remaining": { "remainingFraction": 0 } }
                  ]
                },
                {
                  "displayName": "Claude and GPT models",
                  "buckets": [
                    { "bucketId": "3p-5h", "displayName": "Five Hour Limit", "remaining": { "remainingFraction": 0.8 } },
                    { "bucketId": "3p-weekly", "displayName": "Weekly Limit", "remaining": { "remainingFraction": 0.6 } }
                  ]
                }
              ]
            }
            """;

        var snapshot = AntigravityProvider.ParseQuotaSummary("antigravity", json, Now);

        Assert.AreEqual("Gemini", snapshot.Primary.AvailabilityGroup);
        Assert.AreEqual("Claude/GPT", snapshot.Tertiary!.AvailabilityGroup);
        Assert.AreEqual(60, Quota.ProviderAvailability(snapshot), 0.001);
    }

    [TestMethod]
    public void ParseQuotaSummary_WithOneOfRemainingShape_ParsesRemainingFraction()
    {
        const string json =
            """
            {
              "groups": [{
                "displayName": "Gemini Models",
                "buckets": [{
                  "bucketId": "gemini-weekly",
                  "displayName": "Weekly Limit",
                  "remaining": { "case": "remainingFraction", "value": 0.5 }
                }]
              }]
            }
            """;

        var snapshot = AntigravityProvider.ParseQuotaSummary("antigravity", json, Now);

        Assert.AreEqual("Gemini weekly", snapshot.Primary.Label);
        Assert.AreEqual(50d, snapshot.Primary.UsedPercent);
    }

    [TestMethod]
    public void ParseQuotaSummary_WithCadenceWordInsideUnrelatedLabel_DoesNotInventWindowLength()
    {
        const string json =
            """
            {
              "groups": [{
                "displayName": "Gemini Models",
                "buckets": [{
                  "bucketId": "gemini-session-history",
                  "displayName": "Session History",
                  "remaining": { "remainingFraction": 0.75 }
                }]
              }]
            }
            """;

        var snapshot = AntigravityProvider.ParseQuotaSummary("antigravity", json, Now);

        Assert.AreEqual("Gemini Session History", snapshot.Primary.Label);
        Assert.IsNull(snapshot.Primary.WindowMinutes);
    }

    [TestMethod]
    public void ParseSnapshot_WithLegacyResetOnly_DoesNotClaimWeeklyCadence()
    {
        const string json =
            """
            {
              "userStatus": {
                "email": "test@example.com",
                "userTier": { "name": "Ultra" },
                "cascadeModelConfigData": {
                  "clientModelConfigs": [{
                    "label": "Claude Sonnet",
                    "quotaInfo": {
                      "remainingFraction": 0.7,
                      "resetTime": "2026-06-20T00:00:00Z"
                    }
                  }]
                }
              }
            }
            """;

        var snapshot = AntigravityProvider.ParseSnapshot("antigravity", json, Now);

        Assert.AreEqual("Antigravity", snapshot.Name);
        Assert.HasCount(1, snapshot.AdditionalWindows);
        Assert.AreEqual("Claude / GPT quota", snapshot.AdditionalWindows[0].Label);
        Assert.IsNull(snapshot.AdditionalWindows[0].WindowMinutes);
    }

    [TestMethod]
    public async Task Live_Discover_ReturnsSuccessfully()
    {
        var provider = new AntigravityProvider();
        try
        {
            var snapshot = await provider.FetchAsync("antigravity", new EmptyConfig(), CancellationToken.None);
            Assert.IsNotNull(snapshot);
            Assert.IsNotNull(snapshot.Primary);
        }
        catch (ProviderException error) when (error.Message.Contains("must already be running", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("Live Antigravity integration is unavailable because the app is not running.");
        }
    }

    private sealed class EmptyConfig : IConfig
    {
        public string Get(string key, string fallback = "") => fallback;
        public string GetScoped(string instanceId, string key, string fallback = "") => fallback;
        public bool HasScoped(string instanceId, string key) => false;
        public bool GetBool(string key, bool fallback = false) => fallback;
    }

    private const string QuotaSummaryJson =
        """
        {
          "response": {
            "groups": [
              {
                "displayName": "Gemini Models",
                "buckets": [
                  {
                    "bucketId": "gemini-weekly",
                    "displayName": "Weekly Limit",
                    "remaining": { "remainingFraction": 0.82 },
                    "description": "refreshes in five days",
                    "resetTime": "2026-06-19T08:45:39Z"
                  },
                  {
                    "bucketId": "gemini-5h",
                    "displayName": "Five Hour Limit",
                    "remaining": { "remainingFraction": 0.91 },
                    "description": "refreshes in four hours",
                    "resetTime": "2026-06-15T11:39:34Z"
                  }
                ]
              },
              {
                "displayName": "Claude and GPT models",
                "buckets": [
                  {
                    "bucketId": "3p-weekly",
                    "displayName": "Weekly Limit",
                    "remaining": { "remainingFraction": 0.64 },
                    "resetTime": "2026-06-20T00:39:54Z"
                  },
                  {
                    "bucketId": "3p-5h",
                    "displayName": "Five Hour Limit",
                    "remaining": { "remainingFraction": 0.73 },
                    "resetTime": "2026-06-15T12:52:10Z"
                  }
                ]
              }
            ]
          }
        }
        """;
}
