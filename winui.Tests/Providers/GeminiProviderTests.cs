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
                new GeminiProvider.GeminiModelQuota("gemini-3-pro", 75, null, null),
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
}
