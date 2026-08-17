using System.Text.Json;
using QuotaLens.Core;
using QuotaLens.Providers;
using QuotaLens.Services;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class OpenCodeGoLocalUsageProviderTests
{
    [TestMethod]
    public void CreateStartInfo_PassesSqlAsOneArgumentAndNeverOpensAWindow()
    {
        const string sql = "SELECT createdMs, cost FROM message WHERE providerID = 'opencode-go'";

        var startInfo = OpenCodeGoLocalUsageProvider.CreateStartInfo(@"C:\Tools\opencode.exe", sql);

        Assert.AreEqual(@"C:\Tools\opencode.exe", startInfo.FileName);
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.CreateNoWindow);
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardError);
        CollectionAssert.AreEqual(
            new[] { "db", sql, "--format", "json" },
            startInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    public void ParseRows_ComputesRollingWeeklyAndAnchoredMonthlyUsage()
    {
        var now = DateTimeOffset.Parse("2026-08-12T12:00:00Z");
        var rows = new[]
        {
            Row("2026-07-05T06:30:00Z", 1),
            Row("2026-08-10T10:00:00Z", 3),
            Row("2026-08-12T08:00:00Z", 4),
            Row("2026-08-12T11:00:00Z", 2),
            Row("2026-08-12T13:00:00Z", 99),
        };

        var snapshot = OpenCodeGoLocalUsageProvider.ParseRows(JsonSerializer.Serialize(rows), now);

        Assert.AreEqual("opencodego", snapshot.ProviderId);
        Assert.AreEqual("opencode-go-recurring", snapshot.PlanId);
        Assert.AreEqual("Go", snapshot.PlanName);
        Assert.AreEqual("5h Window", snapshot.Primary.Label);
        Assert.AreEqual(50, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("$6 of $12", snapshot.Primary.DetailText);
        Assert.AreEqual("2026-08-12T13:00:00.0000000+00:00", snapshot.Primary.ResetsAt);
        Assert.AreEqual("Weekly", snapshot.Secondary!.Label);
        Assert.AreEqual(30, snapshot.Secondary.UsedPercent, 0.001);
        Assert.AreEqual("2026-08-17T00:00:00.0000000+00:00", snapshot.Secondary.ResetsAt);
        Assert.AreEqual("Monthly", snapshot.Tertiary!.Label);
        Assert.AreEqual(15, snapshot.Tertiary.UsedPercent, 0.001);
        Assert.AreEqual("2026-09-05T06:30:00.0000000+00:00", snapshot.Tertiary.ResetsAt);
        Assert.AreEqual("OpenCode local history", snapshot.SourceLabel);
        Assert.AreEqual(ProviderSourceKind.CliOrLocal, snapshot.SourceKind);
        Assert.AreEqual(ProviderAvailabilityKind.Finite, snapshot.AvailabilityKind);
        Assert.AreEqual(now, snapshot.UpdatedAt);
    }

    [TestMethod]
    public void ParseRows_RejectsHistoryWithoutValidUsage()
    {
        const string json = """
        [
          { "createdMs": 0, "cost": 1 },
          { "createdMs": 1786507200000, "cost": -1 },
          { "createdMs": "invalid", "cost": 2 }
        ]
        """;

        var exception = Assert.ThrowsExactly<ProviderException>(() =>
            OpenCodeGoLocalUsageProvider.ParseRows(json, DateTimeOffset.Parse("2026-08-12T12:00:00Z")));

        StringAssert.Contains(exception.Message, "no usage rows");
    }

    [TestMethod]
    public void MergeSources_OverlaysFreshWebQuotaButRetainsMissingLocalLane()
    {
        var now = DateTimeOffset.Now;
        var local = OpenCodeGoLocalUsageProvider.ParseRows(
            JsonSerializer.Serialize(new[] { Row(now.AddHours(-1).ToString("O"), 3) }),
            now);
        var web = WebLoginService.ParseOpenCodeGo("""
        {
          "billing": {
            "rollingUsage": { "used": 80, "limit": 100, "resetInSec": 1200 },
            "monthlyUsage": { "used": 70, "limit": 100, "resetInSec": 2592000 }
          },
          "zenBalanceUSD": 5
        }
        """);
        web.UpdatedAt = now.AddSeconds(-10);

        Assert.IsTrue(WebLoginService.ShouldOverlayOpenCodeGoCache(web, 60_000));
        var merged = WebLoginService.NormalizeOpenCodeGoLocalSnapshot(
            WebLoginService.MergeOpenCodeGoSources(local, web));

        Assert.AreEqual(80, merged.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Weekly", merged.Secondary!.Label);
        Assert.AreEqual(70, merged.Tertiary!.UsedPercent, 0.001);
        Assert.IsTrue(merged.Tertiary.CountsForAvailability);
        Assert.AreEqual(5, merged.Balance!.Total, 0.001);
        Assert.AreEqual("OpenCode Go Web quota + local history", merged.SourceLabel);
        Assert.AreEqual(ProviderSourceKind.PrivateDashboard, merged.SourceKind);
        Assert.AreEqual(web.UpdatedAt, merged.UpdatedAt);
    }

    [TestMethod]
    public void StaleWebCache_IsNotEligibleForLocalOverlay()
    {
        var stale = WebLoginService.ParseOpenCodeGo("""
        {
          "billing": {
            "rollingUsage": { "used": 80, "limit": 100, "resetInSec": 1200 }
          },
          "zenBalanceUSD": 5
        }
        """);
        stale.UpdatedAt = DateTimeOffset.Now.AddMinutes(-10);

        Assert.IsFalse(WebLoginService.ShouldOverlayOpenCodeGoCache(stale, 60_000));
    }

    [TestMethod]
    public void MergeSources_BalanceOnlyWebDataKeepsPrivateAgeAndProvenance()
    {
        var now = DateTimeOffset.Now;
        var local = OpenCodeGoLocalUsageProvider.ParseRows(
            JsonSerializer.Serialize(new[] { Row(now.AddHours(-1).ToString("O"), 1) }),
            now);
        var web = WebLoginService.ParseOpenCodeGo("""
        {
          "billing": {
            "customerID": "cus_redacted",
            "balance": 500000000
          }
        }
        """);
        web.UpdatedAt = now.AddSeconds(-15);

        var merged = WebLoginService.NormalizeOpenCodeGoLocalSnapshot(
            WebLoginService.MergeOpenCodeGoSources(local, web));

        Assert.AreEqual("5h Window", merged.Primary.Label);
        Assert.AreEqual(5, merged.Balance!.Total, 0.001);
        Assert.AreEqual("OpenCode Go local history + Web balance", merged.SourceLabel);
        Assert.AreEqual(ProviderSourceKind.PrivateDashboard, merged.SourceKind);
        Assert.AreEqual(ProviderContractStability.PrivateContract, merged.ContractStability);
        Assert.AreEqual(web.UpdatedAt, merged.UpdatedAt);
    }

    [TestMethod]
    public void NormalizeLocalSnapshot_PreservesLocalProvenance()
    {
        var now = DateTimeOffset.Now;
        var local = OpenCodeGoLocalUsageProvider.ParseRows(
            JsonSerializer.Serialize(new[] { Row(now.AddHours(-1).ToString("O"), 1) }),
            now);

        var normalized = WebLoginService.NormalizeOpenCodeGoLocalSnapshot(local);

        Assert.AreEqual("OpenCode local history", normalized.SourceLabel);
        Assert.AreEqual(ProviderSourceKind.CliOrLocal, normalized.SourceKind);
        Assert.AreEqual(ProviderContractStability.UpstreamCompatibility, normalized.ContractStability);
    }

    private static object Row(string timestamp, double cost) => new
    {
        createdMs = DateTimeOffset.Parse(timestamp).ToUnixTimeMilliseconds(),
        cost,
    };
}
