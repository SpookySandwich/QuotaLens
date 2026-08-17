using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Providers;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class GeminiProviderTests
{
    [TestMethod]
    public void ParseCodeAssistStatus_WithPaidTier_PrefersPaidTierNameAndId()
    {
        const string json =
            """
            {
              "cloudaicompanionProject": { "id": "workspace-project" },
              "currentTier": { "id": "free-tier", "name": "Free" },
              "paidTier": { "id": "standard-tier", "name": "Enterprise" }
            }
            """;

        var status = GeminiProvider.ParseCodeAssistStatus(json);

        Assert.AreEqual("workspace-project", status.ProjectId);
        Assert.AreEqual("standard-tier", status.TierId);
        Assert.AreEqual("Enterprise", status.TierName);
    }

    [TestMethod]
    public void IsRetiredConsumerTier_AfterRetirement_RejectsPersonalButKeepsWorkspacePlans()
    {
        var afterRetirement = DateTimeOffset.Parse("2026-06-19T00:00:00Z");

        Assert.IsTrue(GeminiProvider.IsRetiredConsumerTier(
            "free-tier",
            "Google AI Pro",
            null,
            afterRetirement));
        Assert.IsFalse(GeminiProvider.IsRetiredConsumerTier(
            "standard-tier",
            "Standard",
            null,
            afterRetirement));
        Assert.IsFalse(GeminiProvider.IsRetiredConsumerTier(
            "free-tier",
            "Workspace",
            "example.com",
            afterRetirement));
    }

    [TestMethod]
    public void Snapshot_WithMissingOptionalFamilies_DoesNotCreateFakeZeroPercentBars()
    {
        var usage = new GeminiProvider.GeminiUsage(
            new[]
            {
                new GeminiProvider.GeminiModelQuota("gemini-3-pro", 75, null),
            },
            "dev@example.com",
            "Standard");

        var snapshot = GeminiProvider.Snapshot(usage);

        Assert.AreEqual("Gemini · Standard", snapshot.Name);
        Assert.IsNull(snapshot.Secondary);
        Assert.IsNull(snapshot.Tertiary);
        Assert.HasCount(1, snapshot.Accounts);
        Assert.AreEqual("dev@example.com", snapshot.Accounts[0].Email);
    }

    [TestMethod]
    public void ParseQuotaResponse_WithGroupedShape_ExtractsWeeklyAndFiveHourWindows()
    {
        const string json =
            """
            {
              "response": {
                "groups": [
                  {
                    "displayName": "Gemini Models",
                    "buckets": [
                      {
                        "bucketId": "gemini-weekly",
                        "displayName": "Weekly Limit Remaining",
                        "description": "You have used some of your weekly limit, it will fully refresh in 3 days, 5 hours.",
                        "remainingFraction": 0.98,
                        "resetTime": "2026-08-19T12:28:50Z"
                      },
                      {
                        "bucketId": "gemini-5h",
                        "displayName": "Five Hour Limit Remaining",
                        "remainingFraction": 0.95,
                        "resetTime": "2026-08-16T12:01:32Z"
                      }
                    ]
                  },
                  {
                    "displayName": "Claude and GPT models",
                    "buckets": [
                      {
                        "bucketId": "3p-weekly",
                        "displayName": "Weekly Limit Remaining",
                        "remainingFraction": 1.0,
                        "resetTime": "2026-08-23T07:06:20Z"
                      },
                      {
                        "bucketId": "3p-5h",
                        "displayName": "Five Hour Limit Remaining",
                        "remainingFraction": 1.0,
                        "resetTime": "2026-08-16T12:06:20Z"
                      }
                    ]
                  }
                ]
              }
            }
            """;

        var usage = GeminiProvider.ParseQuotaResponse(json, "dev@example.com");
        Assert.IsNotNull(usage.Windows);
        Assert.AreEqual(4, usage.Windows.Count);

        var snapshot = GeminiProvider.Snapshot(usage with { AccountPlan = "Pro" });
        Assert.AreEqual("Gemini · Pro", snapshot.Name);
        Assert.AreEqual("Gemini weekly", snapshot.Primary.Label);
        Assert.AreEqual(2.0, snapshot.Primary.UsedPercent, 0.01);
        Assert.IsNull(snapshot.Primary.DetailText);
        Assert.AreEqual("Gemini 5-hour", snapshot.Secondary!.Label);
        Assert.AreEqual(5.0, snapshot.Secondary.UsedPercent, 0.01);
        Assert.AreEqual("Claude/GPT weekly", snapshot.Tertiary!.Label);
        Assert.AreEqual(0.0, snapshot.Tertiary.UsedPercent, 0.01);
        Assert.AreEqual("Claude/GPT 5-hour", snapshot.AdditionalWindows[0].Label);
    }

    [TestMethod]
    public void ParseQuotaResponse_WithCadenceBuckets_ExtractsWeeklyAndFiveHourWindows()
    {
        const string json =
            """
            {
              "buckets": [
                {
                  "bucketId": "gemini-weekly",
                  "displayName": "Weekly Limit",
                  "remainingFraction": 0.85,
                  "description": "refreshes in 5 days",
                  "resetTime": "2026-08-21T00:00:00Z"
                },
                {
                  "bucketId": "gemini-5h",
                  "displayName": "Five Hour Limit",
                  "remainingFraction": 1.0,
                  "resetTime": "2026-08-16T12:00:00Z"
                }
              ]
            }
            """;

        var usage = GeminiProvider.ParseQuotaResponse(json, "dev@example.com");
        Assert.IsNotNull(usage.Windows);
        Assert.AreEqual(2, usage.Windows.Count);

        var snapshot = GeminiProvider.Snapshot(usage);
        Assert.AreEqual("Gemini", snapshot.Name);
        Assert.AreEqual("Gemini weekly", snapshot.Primary.Label);
        Assert.AreEqual(15.0, snapshot.Primary.UsedPercent, 0.01);
        Assert.AreEqual("Gemini 5-hour", snapshot.Secondary!.Label);
        Assert.AreEqual(0.0, snapshot.Secondary.UsedPercent, 0.01);
    }
}
