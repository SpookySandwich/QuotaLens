using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class KiroProviderTests
{
    [TestMethod]
    public void CreateStartInfo_UsesDocumentedNonInteractiveUsageCommandWithoutStdin()
    {
        var startInfo = KiroProvider.CreateStartInfo("kiro-cli.exe");

        CollectionAssert.AreEqual(
            new[] { "chat", "--no-interactive", "/usage" },
            startInfo.ArgumentList.ToArray());
        Assert.IsFalse(startInfo.RedirectStandardInput);
        Assert.IsTrue(startInfo.CreateNoWindow);
    }

    [TestMethod]
    public void CreateStartInfo_ContextProbeUsesSameHiddenNonInteractiveBoundary()
    {
        var startInfo = KiroProvider.CreateStartInfo("kiro-cli.exe", "/context");

        CollectionAssert.AreEqual(
            new[] { "chat", "--no-interactive", "/context" },
            startInfo.ArgumentList.ToArray());
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.CreateNoWindow);
    }

    [TestMethod]
    public void ParseUsage_WithBaseBonusAndOverageData_PreservesKnownUsage()
    {
        const string output =
            """
            Estimated Usage | resets on 2026-09-01 | KIRO PRO
            Monthly credits:
            ███████████████ 25% (12.5 of 1000 covered in plan)
            Bonus credits: 20/100 credits used, expires in 8 days
            Overages: enabled
            Credits used: 3
            Est. cost: $0.12 USD
            """;

        var now = DateTimeOffset.Parse("2026-08-02T00:00:00Z");
        var snapshot = KiroProvider.ParseUsage("kiro-main", output, now);

        Assert.AreEqual("kiro-main", snapshot.ProviderId);
        Assert.AreEqual("Kiro", snapshot.Name);
        Assert.AreEqual(25d, snapshot.Primary.UsedPercent);
        Assert.AreEqual("Monthly credits", snapshot.Primary.Label);
        Assert.AreEqual(QuotaCadencePolicy.MonthlyMinutes, snapshot.Primary.WindowMinutes);
        Assert.IsTrue(snapshot.Primary.CountsForAvailability);
        StringAssert.Contains(snapshot.Primary.DetailText, "Overage: 3 credits · $0.12 USD");
        Assert.IsNotNull(snapshot.Secondary);
        Assert.AreEqual("Bonus credits", snapshot.Secondary!.Label);
        Assert.AreEqual(20d, snapshot.Secondary.UsedPercent);
        Assert.HasCount(1, snapshot.Accounts);
        Assert.AreEqual(12.5d, snapshot.Accounts[0].CreditsUsed);
        Assert.AreEqual(1000d, snapshot.Accounts[0].CreditsTotal);
        Assert.AreEqual(Confidence.SemiOfficial, snapshot.Confidence);
    }

    [TestMethod]
    public void ParseUsage_WithUnknownOutput_ThrowsInsteadOfClaimingZeroUsage()
    {
        Assert.ThrowsExactly<ProviderException>(() =>
            KiroProvider.ParseUsage("kiro", "Kiro usage format changed", DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void ParseUsage_WithManagedPlanWithoutMetrics_ReportsUnavailable()
    {
        var error = Assert.ThrowsExactly<ProviderException>(() =>
            KiroProvider.ParseUsage(
                "kiro",
                "Plan: Q Developer Pro\nUsage managed by organization",
                DateTimeOffset.UtcNow));

        StringAssert.Contains(error.Message, "managed by the organization");
    }

    [TestMethod]
    public void ParseUsage_WithContextOutput_PreservesContextBreakdownWithoutGatingAvailability()
    {
        const string usage = """
            Plan: KIRO PRO
            Monthly credits:
            █████ 25% (25 of 100 covered in plan)
            """;
        const string context = """
            Context window: 1.3% used (estimated)
            █ Context files 0.5% (estimated)
            █ Tools 0.8% (estimated)
            █ Kiro responses 0.0% (estimated)
            █ Your prompts 0.0% (estimated)
            """;

        var snapshot = KiroProvider.ParseUsage(
            "kiro",
            usage,
            DateTimeOffset.Parse("2026-08-03T00:00:00Z"),
            context);

        CollectionAssert.AreEqual(
            new[] { "Context window", "Context files", "Tools", "Kiro responses", "Your prompts" },
            snapshot.AdditionalWindows.Select(window => window.Label).ToArray());
        Assert.AreEqual(1.3, snapshot.AdditionalWindows[0].UsedPercent, 0.001);
        Assert.IsTrue(snapshot.AdditionalWindows.All(window => !window.CountsForAvailability));
        Assert.AreEqual(75, Quota.ProviderAvailability(snapshot), 0.001);

        // Context occupancy never refills, so it is a metric rather than a pool: no
        // quota bar, and no entry in the card's next-reset ranking.
        Assert.IsTrue(snapshot.AdditionalWindows.All(window => window.Kind == RateWindowKind.Informational));
        Assert.AreEqual("1.3% used", snapshot.AdditionalWindows[0].ValueText);
    }
}
